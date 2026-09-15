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

/// <summary>
/// Game manager for Azur Promilia.
/// <para>
/// Responsibilities: resolve the channel configuration, establish a session, discover the published
/// version, download and decrypt resource indexes, and keep a local file manifest for incremental
/// updates.
/// </para>
/// <para>
/// Installation modes (<c>downloadMode</c>): <c>1</c> = SLICE (fragment merge + extraction),
/// <c>2</c> = REALITY (per-file download).
/// </para>
/// </summary>
[GeneratedComClass]
internal partial class AzurPromiliaGameManager : GameManagerBase
{
    /// <summary>Package type of the SLICE (fragmented) index.</summary>
    internal const int PackageTypeSlice = 1;

    /// <summary>Package type of the REALITY (full) index.</summary>
    internal const int PackageTypeReality = 2;

    /// <summary>Download mode reported by the server: fragmented install.</summary>
    internal const int DownloadModeSlice = 1;

    /// <summary>Download mode reported by the server: per-file install.</summary>
    internal const int DownloadModeReality = 2;

    /// <summary>Name of the plugin's own manifest inside <c>_index</c>.</summary>
    internal const string LocalManifestFileName = "azurpromilia-manifest.json";

    /// <summary>Name of the launcher's own version marker inside <c>_index</c>.</summary>
    internal const string LauncherVersionFileName = "version";

    private readonly AzurPromiliaAccount _account;
    private readonly string _gameExecutableNameByPreset;

    /// <summary>
    /// The channel configuration. Not <c>readonly</c>: it is re-resolved once the game directory is
    /// known, because <c>account.json</c> (which may carry <c>launcherRoot</c>) lives in the game
    /// folder and that folder is only reported after this manager has already been constructed.
    /// </summary>
    private AzurPromiliaChannelConfig _channel;

    /// <summary>The <c>launcherRoot</c> value <see cref="_channel"/> was resolved with.</summary>
    private string? _launcherRootHint;

    private AzurPromiliaApiClient? _apiClient;
    private GameReleaseInfo? _released;
    private GameReleaseInfo? _preload;
    private string? _localVersionCode;
    private bool _isInitialized;
    private bool? _isInstalledCache;

    internal AzurPromiliaGameManager(string gameExecutableNameByPreset,
        AzurPromiliaChannelConfig channel,
        AzurPromiliaAccount account)
    {
        _gameExecutableNameByPreset = gameExecutableNameByPreset;
        _channel = channel;
        _account = account;
        _launcherRootHint = account.LauncherRoot;

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

    /// <summary>The published (released) version, or <c>null</c> before initialisation.</summary>
    internal GameReleaseInfo? Released => _released;

    /// <summary>The pre-download (upcoming) version, when the server publishes one.</summary>
    internal GameReleaseInfo? Preload => _preload;

    /// <summary>Server-reported installation mode.</summary>
    internal int DownloadMode { get; private set; } = DownloadModeSlice;

    /// <summary>Relative path of the executable that starts the game.</summary>
    internal string? LaunchEntry { get; private set; }

    /// <summary>Command line arguments the server wants passed to the game.</summary>
    internal string? LaunchParams { get; private set; }

    /// <summary>Default folder name for the game installation.</summary>
    internal string? GameFolderName { get; private set; }

    /// <summary>The authenticated session, once established.</summary>
    internal LoginUserInfo? Session { get; private set; }

    /// <summary>The API client, available after initialisation.</summary>
    internal AzurPromiliaApiClient? ApiClient => _apiClient;

    /// <summary>The effective channel configuration.</summary>
    internal AzurPromiliaChannelConfig Channel => _channel;

    /// <summary>Local version code recorded by the plugin or the official launcher.</summary>
    internal string? LocalVersionCode => _localVersionCode;

    /// <summary>
    /// The executable name that <see cref="IsInstalled"/> probes when the server has not yet told us
    /// the real launch entry.
    /// </summary>
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

    /// <summary>
    /// Performs the full discovery sequence: channel config -> launcher config -> login ->
    /// version config -> launch entry.
    /// </summary>
    internal async Task<int> InitAsyncInner(bool forceInit = false, CancellationToken token = default)
    {
        if (!forceInit && _isInitialized)
        {
            return 0;
        }

        ResolveCredentialContext();
        ReadLocalGameVersion();

        try
        {
            EnsureApiClient();
            await FetchLauncherConfigAsync(token).ConfigureAwait(false);
            await EstablishSessionAsync(token).ConfigureAwait(false);
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

    /// <summary>
    /// Ensures initialisation has run and returns the API client, or <c>null</c> when the client
    /// could not be created.
    /// </summary>
    internal async Task<AzurPromiliaApiClient?> EnsureApiClientAsync(CancellationToken token)
    {
        await InitAsyncInner(false, token).ConfigureAwait(false);
        return _apiClient;
    }

    /// <summary>
    /// Re-reads <c>account.json</c> and re-resolves the channel configuration now that the game
    /// directory is known.
    /// <para>
    /// The credential file lives in the game folder by default, but that folder is only handed to the
    /// plugin by <see cref="SetGamePathInner"/> — which runs <i>after</i> the preset config has already
    /// constructed this manager (and therefore after the channel config was first resolved). Credential
    /// resolution consequently has to be re-run here rather than in the constructor.
    /// </para>
    /// </summary>
    private void ResolveCredentialContext()
    {
        bool sourceChanged = _account.TryLoadFrom(CurrentGameInstallPath);
        if (sourceChanged && !string.IsNullOrEmpty(_account.SourcePath))
        {
            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromiliaGameManager] Credentials are read from '{Path}'.", _account.SourcePath);
        }

        // 'launcherRoot' can name a different official launcher than the one auto-detection picked while
        // the channel config was first resolved, so rebuild the channel (and the client that captured
        // it) whenever the value actually changed.
        string? hint = _account.LauncherRoot;
        if (string.Equals(hint ?? string.Empty, _launcherRootHint ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _launcherRootHint = hint;
        _channel = AzurPromiliaChannelConfig.Resolve(hint);

        _apiClient?.Dispose();
        _apiClient = null;

        SharedStatic.InstanceLogger.LogInformation(
            "[AzurPromiliaGameManager] Channel configuration re-resolved (launcherRoot='{Root}').",
            hint ?? "(auto-detect)");
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

    /// <summary>
    /// <c>getLauncherConfig</c> — yields the failover domains and the password-encryption key.
    /// </summary>
    private async Task FetchLauncherConfigAsync(CancellationToken token)
    {
        AzurPromiliaApiClient api = _apiClient!;

        Dictionary<string, string?> query = new() { ["launcherId"] = _channel.LauncherId };

        LauncherConfigData? data = await api
            .GetAsync(api.BaseDomains, "/pc-launcher/client/launcher/config", query,
                AzurPromiliaApiContext.Default.AzurPromiliaResponseLauncherConfigData, token)
            .ConfigureAwait(false);

        LauncherInitConfig? initConfig = data?.LauncherInitConfig;
        if (initConfig == null)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromiliaGameManager] getLauncherConfig returned no launcherInitConfig; keeping the built-in domains.");
            return;
        }

        if (initConfig.User?.Domains is { Count: > 0 } userDomains)
        {
            api.UserDomains = userDomains;
        }

        if (initConfig.Compliance?.Domains is { Count: > 0 } complianceDomains)
        {
            api.ComplianceDomains = complianceDomains;
        }

        api.PasswordKey = initConfig.GetPasswordKey();

        SharedStatic.InstanceLogger.LogInformation(
            "[AzurPromiliaGameManager] Domains resolved. user={UserDomain}, compliance={ComplianceDomain}, pwdKey={HasPwdKey}",
            api.UserDomains.Count > 0 ? api.UserDomains[0] : "(none)",
            api.ComplianceDomains.Count > 0 ? api.ComplianceDomains[0] : "(none)",
            !string.IsNullOrEmpty(api.PasswordKey));
    }

    private async Task EstablishSessionAsync(CancellationToken token)
    {
        Session = await AzurPromiliaLoginService.ResolveAsync(_apiClient!, _account, token).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>getGameVersionConfig</c> — the released/preload versions and the installation mode.
    /// Requires a valid session because the server keys the response on <c>uid</c>.
    /// </summary>
    private async Task FetchGameVersionConfigAsync(CancellationToken token)
    {
        AzurPromiliaApiClient api = _apiClient!;

        if (string.IsNullOrEmpty(api.UToken))
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromiliaGameManager] Skipping getGameVersionConfig because no session is available. " +
                "Download and update will be unavailable until credentials are supplied.");
            return;
        }

        Dictionary<string, string?> query = new()
        {
            ["launcherId"] = _channel.LauncherId,
            ["uid"] = api.Uid ?? string.Empty
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
                "[AzurPromiliaGameManager] The server published no released version for this account.");
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

    /// <summary>
    /// <c>getGameExeInfo</c> — the authoritative launch entry and arguments for the published version.
    /// </summary>
    private async Task FetchLaunchEntryAsync(CancellationToken token)
    {
        AzurPromiliaApiClient api = _apiClient!;

        if (string.IsNullOrEmpty(api.UToken) || string.IsNullOrEmpty(_released?.VersionCode))
        {
            return;
        }

        Dictionary<string, string?> query = new()
        {
            ["launcherId"] = _channel.LauncherId,
            ["gameVersionCode"] = _released!.VersionCode!,
            ["uid"] = api.Uid ?? string.Empty
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

    /// <summary>
    /// Resolves the local version from the launcher's own <c>_index/version</c> marker, falling back
    /// to the plugin manifest.
    /// </summary>
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

    /// <summary>
    /// Downloads a resource index to <paramref name="targetPath"/>, skipping the download when a file
    /// with the expected size and MD5 is already present.
    /// </summary>
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

    /// <summary>
    /// Downloads and decrypts a resource index.
    /// </summary>
    /// <param name="package">Package descriptor carrying the index URL and hash.</param>
    /// <param name="targetPath">Where the encrypted index is cached on disk.</param>
    /// <param name="isSlice">Whether to parse the payload as a SLICE index.</param>
    /// <param name="token">Cancellation token.</param>
    internal async Task<AzurPromiliaGameIndex> LoadIndexAsync(GamePackageInfo package, string targetPath,
        bool isSlice, CancellationToken token)
    {
        await DownloadIndexAsync(package, targetPath, token).ConfigureAwait(false);

        string csv = AzurPromiliaCrypto.DecryptResourceIndexFromFile(targetPath);

        return isSlice
            ? AzurPromiliaGameIndex.ParseInstallIndex(csv)
            : AzurPromiliaGameIndex.ParseGameIndex(csv);
    }

    /// <summary>
    /// Returns the index file path used for a given version: <c>&lt;game&gt;/_index/&lt;version&gt;</c>.
    /// </summary>
    internal static string GetIndexPath(string gamePath, string versionCode)
        => Path.Combine(gamePath, "_index", versionCode);

    /// <summary>
    /// Returns the staging directory used by SLICE installs: <c>&lt;game&gt;/_temp</c>.
    /// </summary>
    internal static string GetSliceTempPath(string gamePath)
        => Path.Combine(gamePath, "_temp");

    #endregion

    #region Local manifest

    /// <summary>Reads the plugin's local file manifest from the game directory.</summary>
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

    /// <summary>Writes the plugin's local file manifest after a successful install or update.</summary>
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
