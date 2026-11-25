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
using Microsoft.Win32;

namespace Hi3Helper.Plugin.AzurPromilia.Management;

[GeneratedComClass]
internal partial class AzurPromiliaGameManager : GameManagerBase
{
    internal const int PackageTypeSlice = 1;
    internal const int PackageTypeReality = 2;
    internal const int DownloadModeSlice = 1;
    internal const string LocalManifestFileName = "azurpromilia-manifest.json";
    internal const string LauncherVersionFileName = "version";

    private readonly string _gameExecutableNameByPreset;
    private readonly AzurPromiliaChannelConfig _channel;

    private AzurPromiliaApiClient? _apiClient;
    private GameReleaseInfo? _released;
    private GameReleaseInfo? _preload;
    private string? _localVersionCode;
    private bool _isInitialized;

    internal AzurPromiliaGameManager(string gameExecutableNameByPreset,
        AzurPromiliaChannelConfig channel)
    {
        _gameExecutableNameByPreset = gameExecutableNameByPreset;
        _channel = channel;

        ApiResponseHttpClient = new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.All)
            .AllowCookies()
            .AllowRedirections()
            .SetTimeout(TimeSpan.FromSeconds(30))
            .Create();

        ApiResponseHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            AzurPromiliaDeviceInfo.UserAgent);
    }

    internal GameReleaseInfo? Released => _released;
    internal GameReleaseInfo? Preload => _preload;
    internal int DownloadMode { get; private set; } = DownloadModeSlice;
    internal string? LaunchEntry { get; private set; }
    internal string? LaunchParams { get; private set; }
    internal AzurPromiliaApiClient? ApiClient => _apiClient;


    protected override HttpClient ApiResponseHttpClient { get; set; }


    protected override bool IsInstalled
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentGameInstallPath))
            {
                return false;
            }

            string executableName = !string.IsNullOrEmpty(LaunchEntry) ? LaunchEntry! : _gameExecutableNameByPreset;
            return File.Exists(AzurPromiliaGameInstaller.GetContentPath(CurrentGameInstallPath, executableName));
        }
    }

    protected override bool HasUpdate
        // The launcher compares raw version codes rather than parsed versions, so do the same.
        => IsInstalled &&
           !string.IsNullOrEmpty(_released?.VersionCode) &&
           !string.Equals(_released!.VersionCode!.Trim(), _localVersionCode?.Trim(),
               StringComparison.OrdinalIgnoreCase);

    protected override bool HasPreload =>
        !string.IsNullOrEmpty(_preload?.VersionCode) &&
        !string.IsNullOrEmpty(_preload.GetPackage(PackageTypeReality)?.DownloadUrl) &&
        !string.Equals(_released?.VersionCode, _preload.VersionCode, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(_localVersionCode, _preload.VersionCode, StringComparison.OrdinalIgnoreCase) &&
        (!IsInstalled || !HasUpdate);

    protected override GameVersion ApiGameVersion { get; set; } = GameVersion.Empty;

    protected override void SetGamePathInner(string gamePath)
    {
        SharedStatic.InstanceLogger.LogInformation("[AzurPromiliaGameManager] SetGamePathInner: '{Path}'", gamePath);

        _localVersionCode = null;
        CurrentGameVersion = GameVersion.Empty;
        ReadLocalGameVersion();
    }

    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion)
        => CurrentGameVersion = gameVersion;

    protected override Task<string?> FindExistingInstallPathAsyncInner(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_channel.GameRegistryPath);
            if (key?.GetValue("Path") is string path && Path.IsPathFullyQualified(path) &&
                key.GetValue("Exe") is string executable &&
                File.Exists(AzurPromiliaGameInstaller.GetContentPath(path, executable)))
            {
                return Task.FromResult<string?>(path);
            }
        }
        catch (IOException ex)
        {
            SharedStatic.InstanceLogger.LogWarning(ex, "Could not read the official game installation path.");
        }

        return Task.FromResult<string?>(null);
    }

    public override void LoadConfig()
    {
    }

    public override void SaveConfig()
    {
    }

    protected override Task<int> InitAsync(CancellationToken token) => InitAsyncInner(true, token);


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
            LaunchEntry = _released?.LaunchEntry;
            LaunchParams = null;
            try
            {
                await FetchLaunchEntryAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                SharedStatic.InstanceLogger.LogWarning(ex, "Could not load Azur Promilia launch parameters.");
            }

            _isInitialized = true;
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(
                "[AzurPromiliaGameManager] API initialisation failed, falling back to local state: {Error}", ex);

            if (ApiGameVersion == GameVersion.Empty)
            {
                ApiGameVersion = CurrentGameVersion;
            }

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
            .SetTimeout(TimeSpan.FromSeconds(30))
            .Create();

        _apiClient = new AzurPromiliaApiClient(_channel, httpClient, _channel.SubChannelCode, _channel.TraceId);

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
            catch (Exception ex) when (!token.IsCancellationRequested)
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

        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Exception? lastError = null;
        foreach (string url in package.ResolveUrls(package.DownloadUrl))
        {
            try
            {
                byte[] payload = await ApiResponseHttpClient.GetByteArrayAsync(url, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(temporaryPath, payload, token).ConfigureAwait(false);
                string actualMd5 = await AzurPromiliaCrypto.Md5FileAsync(temporaryPath, null, token)
                    .ConfigureAwait(false);
                if ((package.FileSize > 0 && payload.LongLength != package.FileSize) ||
                    !string.Equals(actualMd5, package.FileMd5, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Index verification failed for '{targetPath}'.");
                }

                File.Move(temporaryPath, targetPath, true);
                return;
            }
            catch (Exception ex) when (!token.IsCancellationRequested &&
                                       ex is HttpRequestException or InvalidDataException or TaskCanceledException)
            {
                lastError = ex;
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        throw new IOException($"Could not download a verified index to '{targetPath}'.", lastError);
    }

    internal async Task<AzurPromiliaGameIndex> LoadIndexAsync(GamePackageInfo package, string targetPath,
        bool isSlice, CancellationToken token)
    {
        await DownloadIndexAsync(package, targetPath, token).ConfigureAwait(false);

        string csv = AzurPromiliaCrypto.DecryptResourceIndexFromFile(targetPath);

        return isSlice
            ? AzurPromiliaGameIndex.ParseInstallIndex(csv, package)
            : AzurPromiliaGameIndex.ParseGameIndex(csv, package);
    }

    internal static string GetIndexPath(string gamePath, string versionCode)
        => AzurPromiliaGameInstaller.GetContentPath(Path.Combine(gamePath, "_index"), versionCode);

    internal static string GetPreloadPath(string gamePath, string versionCode)
        => AzurPromiliaGameInstaller.GetContentPath(Path.Combine(gamePath, "_pre_download"), versionCode);
    internal static string GetSliceTempPath(string gamePath)
        => Path.Combine(gamePath, "_temp");


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

    internal void WriteLocalManifest(string installPath, GameReleaseInfo release, AzurPromiliaGameIndex index,
        CancellationToken token)
    {
        List<AzurPromiliaLocalManifestFile> files = new(index.Files.Count);
        foreach (AzurPromiliaIndexEntry entry in index.Files)
        {
            files.Add(new AzurPromiliaLocalManifestFile
            {
                Path = AzurPromiliaGameInstaller.NormalizeIndexPath(entry.Name),
                Size = entry.Size,
                Md5 = entry.Md5
            });
        }

        AzurPromiliaLocalManifest manifest = new()
        {
            VersionCode = release.VersionCode,
            VersionName = release.VersionName,
            DownloadMode = DownloadMode,
            Files = files
        };

        string directory = Path.Combine(installPath, "_index");
        Directory.CreateDirectory(directory);
        token.ThrowIfCancellationRequested();
        WriteAtomically(Path.Combine(directory, LocalManifestFileName), stream =>
            JsonSerializer.Serialize(stream, manifest, AzurPromiliaApiContext.Default.AzurPromiliaLocalManifest));
        WriteAtomically(Path.Combine(directory, LauncherVersionFileName), stream =>
        {
            using Utf8JsonWriter writer = new(stream);
            writer.WriteStartObject();
            writer.WriteString("version", release.VersionCode);
            writer.WriteString("versionName", release.VersionName ?? release.VersionCode);
            writer.WriteEndObject();
        });

        _localVersionCode = manifest.VersionCode;
        CurrentGameVersion = new GameVersion(manifest.VersionCode!);
    }

    private static void WriteAtomically(string path, Action<Stream> write)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = File.Create(temporaryPath))
            {
                write(stream);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }


    public override void Dispose()
    {
        _apiClient?.Dispose();
        _apiClient = null;

        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
