using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.AzurPromilia.Management.Api;
using Hi3Helper.Plugin.AzurPromilia.Utils;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia.Management;

[GeneratedComClass]
internal partial class AzurPromiliaGameManager : GameManagerBase
{
    internal const int PackageTypeSlice = 1;
    internal const int PackageTypeReality = 2;
    internal const int DownloadModeSlice = 1;
    internal const int DownloadModeReality = 2;
    internal const string LocalManifestFileName = "azurpromilia-manifest.json";
    internal const string LauncherVersionFileName = "version";

    private readonly string _gameExecutableNameByPreset;
    private readonly AzurPromiliaChannelConfig _channel;

    private AzurPromiliaApiClient? _apiClient;
    private GameReleaseInfo? _released;
    private GameReleaseInfo? _preload;
    private string? _localVersionCode;
    private bool _isInitialized;
    private bool? _isInstalledCache;

    internal AzurPromiliaGameManager(string gameExecutableNameByPreset,
        AzurPromiliaChannelConfig channel)
    {
        _gameExecutableNameByPreset = gameExecutableNameByPreset;
        _channel = channel;

        ApiResponseHttpClient = new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.All)
            .AllowCookies()
            .AllowRedirections()
            .AllowUntrustedCert()
            .SetTimeout(TimeSpan.FromSeconds(30))
            .Create();

        ApiResponseHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            AzurPromiliaDeviceInfo.UserAgent);
    }

    #region Published state
    internal GameReleaseInfo? Released => _released;
    internal GameReleaseInfo? Preload => _preload;
    internal int DownloadMode { get; private set; } = DownloadModeSlice;
    internal string? LaunchEntry { get; private set; }
    internal string? LaunchParams { get; private set; }
    internal string? GameFolderName { get; private set; }
    internal AzurPromiliaApiClient? ApiClient => _apiClient;
    internal AzurPromiliaChannelConfig Channel => _channel;
    internal string? LocalVersionCode => _localVersionCode;
    internal string GameExecutableNameByPreset => _gameExecutableNameByPreset;

    #endregion

    protected override HttpClient ApiResponseHttpClient { get; set; }

    #region GameManagerBase overrides

    protected override bool IsInstalled
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentGameInstallPath))
            {
                return false;
            }

            if (_isInstalledCache.HasValue)
            {
                return _isInstalledCache.Value;
            }

            string executableName = !string.IsNullOrEmpty(LaunchEntry) ? LaunchEntry! : _gameExecutableNameByPreset;
            string executablePath = Path.Combine(CurrentGameInstallPath, executableName);
            bool exists = File.Exists(executablePath);

            SharedStatic.InstanceLogger.LogDebug(
                "[AzurPromiliaGameManager] IsInstalled={Exists} (probe: {Path})", exists, executablePath);

            _isInstalledCache = exists;
            return exists;
        }
    }

    protected override bool HasUpdate
        // The launcher compares raw version codes rather than parsed versions, so do the same.
        => IsInstalled &&
           !string.IsNullOrEmpty(_released?.VersionCode) &&
           !string.Equals(_released!.VersionCode!.Trim(), _localVersionCode?.Trim(),
               StringComparison.OrdinalIgnoreCase);

    protected override bool HasPreload => IsInstalled && _preload != null;

    protected override GameVersion ApiGameVersion { get; set; } = GameVersion.Empty;

    protected override void SetGamePathInner(string gamePath)
    {
        SharedStatic.InstanceLogger.LogInformation("[AzurPromiliaGameManager] SetGamePathInner: '{Path}'", gamePath);

        CurrentGameInstallPath = gamePath;
        _isInstalledCache = null;
        _localVersionCode = null;

        if (string.IsNullOrEmpty(gamePath))
        {
            return;
        }

        // Re-derive the local version for the newly selected directory, then refresh the API state.
        ReadLocalGameVersion();

        _ = Task.Run(async () =>
        {
            try
            {
                await InitAsyncInner(true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogError(
                    "[AzurPromiliaGameManager] Re-initialisation failed: {Error}", ex);
            }
        });
    }

    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion)
        => CurrentGameVersion = gameVersion;

    protected override Task<string?> FindExistingInstallPathAsyncInner(CancellationToken token)
    {
        // Azur Promilia does not advertise an install path through a stable, documented location, so
        // discovery is left to the launcher UI (the user picks the directory).
        return Task.FromResult<string?>(null);
    }

    public override void LoadConfig()
    {
    }

    public override void SaveConfig()
    {
    }

    protected override Task<int> InitAsync(CancellationToken token) => InitAsyncInner(true, token);

    #endregion

    #region Initialisation
    internal async Task<int> InitAsyncInner(bool forceInit = false, CancellationToken token = default)
    {
        if (!forceInit && _isInitialized)
        {
            return 0;
        }

        ReadLocalGameVersion();

        try
        {
            EnsureApiClient();
            await FetchGameVersionConfigAsync(token).ConfigureAwait(false);
            await FetchLaunchEntryAsync(token).ConfigureAwait(false);

            _isInitialized = true;
            return 0;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(
                "[AzurPromiliaGameManager] API initialisation failed, falling back to local state: {Error}", ex);

            if (ApiGameVersion == GameVersion.Empty)
            {
                ApiGameVersion = CurrentGameVersion;
            }

            _isInitialized = true;
            return -1;
        }
    }

    internal async Task<AzurPromiliaApiClient?> EnsureApiClientAsync(CancellationToken token)
    {
        await InitAsyncInner(false, token).ConfigureAwait(false);
        return _apiClient;
    }

    private void EnsureApiClient()
    {
        if (_apiClient != null)
        {
            return;
        }
        HttpClient httpClient = new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.All)
            .AllowCookies()
            .AllowRedirections()
            .AllowUntrustedCert()
            .SetTimeout(TimeSpan.FromSeconds(30))
            .Create();

        string subChannelCode = _channel.SubChannelCode;
        (string detectedSubChannel, string traceId) = AzurPromiliaDeviceInfo.TryReadDeliveryInfo(null);
        if (!string.IsNullOrEmpty(detectedSubChannel))
        {
            subChannelCode = detectedSubChannel;
        }

        _apiClient = new AzurPromiliaApiClient(_channel, httpClient, subChannelCode, traceId);

        SharedStatic.InstanceLogger.LogInformation(
            "[AzurPromiliaGameManager] Channel context ready. launcherId={LauncherId}, appId={AppId}, channel={ChannelCode}",
            _channel.LauncherId, _channel.AppId, _channel.ChannelCode);
    }

    private async Task FetchGameVersionConfigAsync(CancellationToken token)
    {
        AzurPromiliaApiClient api = _apiClient!;

        Dictionary<string, string?> query = new()
        {
            ["launcherId"] = _channel.LauncherId
        };

        GameVersionConfigData? data = await api
            .GetAsync(api.BaseDomains, "/pc-launcher/client/gameversion/packageconfig", query,
                AzurPromiliaApiContext.Default.AzurPromiliaResponseGameVersionConfigData, token)
            .ConfigureAwait(false);

        if (data == null)
        {
            return;
        }

        _released = data.Released;
        _preload = data.Preload;
        DownloadMode = data.DownloadMode;

        GameFolderName = data.Released?.GameFolderName;

        if (!string.IsNullOrEmpty(data.Released?.VersionCode))
        {
            ApiGameVersion = new GameVersion(data.Released!.VersionCode!);
            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromiliaGameManager] Published version: code={VersionCode}, name={VersionName}, mode={Mode}",
                data.Released.VersionCode, data.Released.VersionName, DownloadMode);
        }
        else
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromiliaGameManager] The server published no released version.");
        }

        // getGameInfo carries the preload payload when packageconfig omits it.
        if (_preload == null)
        {
            try
            {
                GameVersionConfigData? gameInfo = await api
                    .GetAsync(api.BaseDomains, "/pc-launcher/client/gameversion/config",
                        new Dictionary<string, string?> { ["launcherId"] = _channel.LauncherId },
                        AzurPromiliaApiContext.Default.AzurPromiliaResponseGameVersionConfigData, token)
                    .ConfigureAwait(false);

                _preload = gameInfo?.Preload;

                if (_released == null && gameInfo?.Released != null)
                {
                    _released = gameInfo.Released;
                    ApiGameVersion = new GameVersion(_released.VersionCode ?? "0");
                }
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[AzurPromiliaGameManager] getGameInfo failed: {Error}", ex.Message);
            }
        }
    }

    private async Task FetchLaunchEntryAsync(CancellationToken token)
    {
        AzurPromiliaApiClient api = _apiClient!;

        if (string.IsNullOrEmpty(_released?.VersionCode))
        {
            return;
        }

        Dictionary<string, string?> query = new()
        {
            ["launcherId"] = _channel.LauncherId,
            ["gameVersionCode"] = _released!.VersionCode!
        };

        GameExeInfoData? data = await api
            .GetAsync(api.BaseDomains, "/pc-launcher/client/gameversion/pre/check", query,
                AzurPromiliaApiContext.Default.AzurPromiliaResponseGameExeInfoData, token)
            .ConfigureAwait(false);

        if (data == null)
        {
            return;
        }

        LaunchEntry = !string.IsNullOrEmpty(data.LaunchEntry) ? data.LaunchEntry : _released.LaunchEntry;
        LaunchParams = data.LaunchParams;

        SharedStatic.InstanceLogger.LogInformation(
            "[AzurPromiliaGameManager] Launch entry: {Entry}, params: {Params}",
            LaunchEntry ?? "(none)", LaunchParams ?? "(none)");
    }

    private void ReadLocalGameVersion()
    {
        if (string.IsNullOrEmpty(CurrentGameInstallPath))
        {
            return;
        }

        string indexPath = Path.Combine(CurrentGameInstallPath, "_index", LauncherVersionFileName);

        try
        {
            if (File.Exists(indexPath))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(indexPath));
                if (document.RootElement.TryGetProperty("version", out JsonElement versionElement) &&
                    versionElement.ValueKind == JsonValueKind.String)
                {
                    _localVersionCode = versionElement.GetString();

                    string? versionName = document.RootElement.TryGetProperty("versionName", out JsonElement nameElement) &&
                                          nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()
                        : _localVersionCode;

                    if (!string.IsNullOrEmpty(_localVersionCode))
                    {
                        CurrentGameVersion = new GameVersion(_localVersionCode);
                        SharedStatic.InstanceLogger.LogInformation(
                            "[AzurPromiliaGameManager] Local version {Version} (name: {Name}) read from _index/version",
                            _localVersionCode, versionName ?? _localVersionCode);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromiliaGameManager] Failed to read the local version marker: {Error}", ex.Message);
        }

        // Fall back to the plugin's own manifest.
        AzurPromiliaLocalManifest? manifest = ReadLocalManifest(CurrentGameInstallPath);
        if (manifest?.VersionCode != null)
        {
            _localVersionCode = manifest.VersionCode;
            CurrentGameVersion = new GameVersion(manifest.VersionCode);
            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromiliaGameManager] Local version {Version} read from the plugin manifest.",
                manifest.VersionCode);
        }
    }

    #endregion

    #region Index download & parsing
    internal async Task DownloadIndexAsync(GamePackageInfo package, string targetPath, CancellationToken token)
    {
        if (string.IsNullOrEmpty(package.DownloadUrl))
        {
            throw new InvalidOperationException("The package descriptor carries no download URL.");
        }

        if (File.Exists(targetPath))
        {
            FileInfo existing = new(targetPath);
            if (existing.Length == package.FileSize)
            {
                string existingMd5 = await AzurPromiliaCrypto.Md5FileAsync(targetPath, null, token)
                    .ConfigureAwait(false);

                if (string.Equals(existingMd5, package.FileMd5, StringComparison.OrdinalIgnoreCase))
                {
                    SharedStatic.InstanceLogger.LogInformation(
                        "[AzurPromiliaGameManager] Index already up to date at '{Path}'.", targetPath);
                    return;
                }
            }
        }

        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SharedStatic.InstanceLogger.LogInformation(
            "[AzurPromiliaGameManager] Downloading index from '{Url}' to '{Path}'",
            package.DownloadUrl, targetPath);

        string payload = await _apiClient!.GetStringAsync(package.DownloadUrl!, token).ConfigureAwait(false);

        await File.WriteAllTextAsync(targetPath, payload, token).ConfigureAwait(false);

        // The index is itself described by an MD5; verify it before trusting the contents.
        if (!string.IsNullOrEmpty(package.FileMd5))
        {
            string actualMd5 = await AzurPromiliaCrypto.Md5FileAsync(targetPath, null, token).ConfigureAwait(false);
            if (!string.Equals(actualMd5, package.FileMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Index MD5 mismatch for '{targetPath}'. Expected {package.FileMd5}, got {actualMd5}.");
            }
        }
    }

    internal async Task<AzurPromiliaGameIndex> LoadIndexAsync(GamePackageInfo package, string targetPath,
        bool isSlice, CancellationToken token)
    {
        await DownloadIndexAsync(package, targetPath, token).ConfigureAwait(false);

        string csv = AzurPromiliaCrypto.DecryptResourceIndexFromFile(targetPath);

        return isSlice
            ? AzurPromiliaGameIndex.ParseInstallIndex(csv)
            : AzurPromiliaGameIndex.ParseGameIndex(csv);
    }

    internal static string GetIndexPath(string gamePath, string versionCode)
        => Path.Combine(gamePath, "_index", versionCode);
    internal static string GetSliceTempPath(string gamePath)
        => Path.Combine(gamePath, "_temp");

    #endregion

    #region Local manifest
    internal AzurPromiliaLocalManifest? ReadLocalManifest(string installPath)
    {
        string path = Path.Combine(installPath, "_index", LocalManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path),
                AzurPromiliaApiContext.Default.AzurPromiliaLocalManifest);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromiliaGameManager] Failed to read the local manifest: {Error}", ex.Message);
            return null;
        }
    }

    internal void WriteLocalManifest(string installPath, AzurPromiliaGameIndex index, int downloadMode)
    {
        try
        {
            List<AzurPromiliaLocalManifestFile> files = new(index.Files.Count);
            foreach (AzurPromiliaIndexEntry entry in index.Files)
            {
                files.Add(new AzurPromiliaLocalManifestFile
                {
                    Path = entry.Name,
                    Size = entry.Size,
                    Md5 = entry.Md5
                });
            }

            AzurPromiliaLocalManifest manifest = new()
            {
                VersionCode = _released?.VersionCode,
                VersionName = _released?.VersionName,
                DownloadMode = downloadMode,
                Files = files
            };

            string directory = Path.Combine(installPath, "_index");
            Directory.CreateDirectory(directory);

            File.WriteAllText(Path.Combine(directory, LocalManifestFileName),
                JsonSerializer.Serialize(manifest, AzurPromiliaApiContext.Default.AzurPromiliaLocalManifest));

            _isInstalledCache = null;
            _localVersionCode = manifest.VersionCode;

            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromiliaGameManager] Local manifest written for version {Version}.", manifest.VersionCode);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromiliaGameManager] Failed to write the local manifest: {Error}", ex.Message);
        }
    }

    #endregion

    public override void Dispose()
    {
        _apiClient?.Dispose();
        _apiClient = null;

        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
