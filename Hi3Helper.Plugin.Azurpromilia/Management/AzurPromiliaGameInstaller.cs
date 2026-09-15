using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
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
/// Installer for Azur Promilia.
/// <para>
/// Fresh installs follow the server's <c>downloadMode</c>: SLICE downloads numbered fragments into
/// <c>_temp</c>, concatenates them into <c>game.zip</c> and extracts it; REALITY writes the final
/// files straight into the game directory. Updates are always file-level, driven by the REALITY index,
/// which is why a SLICE install also caches that index for later diffs.
/// </para>
/// </summary>
[GeneratedComClass]
internal partial class AzurPromiliaGameInstaller : GameInstallerBase
{
    /// <summary>Default number of concurrent file downloads.</summary>
    internal const int DefaultConcurrency = 4;

    /// <summary>Concurrency used once a network error has been observed.</summary>
    internal const int DegradedConcurrency = 2;

    /// <summary>Concurrency used after repeated network errors.</summary>
    internal const int MinimumConcurrency = 1;

    /// <summary>Suffix of partially downloaded files.</summary>
    internal const string TempFileSuffix = ".~tmp";

    /// <summary>Maximum number of per-file retries before the whole install is failed.</summary>
    internal const int MaxFileRetries = 4;

    private readonly HttpClient _downloadHttpClient;

    internal AzurPromiliaGameInstaller(IGameManager? gameManager)
        : base(gameManager)
    {
        // Download URLs are plain CDN GETs; the launcher does not sign them, so neither do we.
        _downloadHttpClient = new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.None)
            .AllowRedirections()
            .AllowUntrustedCert()
            .SetTimeout(Timeout.InfiniteTimeSpan)
            .SetMaxConnection(32)
            .Create();

        _downloadHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            AzurPromiliaDeviceInfo.UserAgent);
        _downloadHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
    }

    private AzurPromiliaGameManager Manager => (AzurPromiliaGameManager)GameManager;

    protected override async Task<int> InitAsync(CancellationToken token)
        => await Manager.InitAsyncInner(true, token).ConfigureAwait(false);

    protected override async Task<long> GetGameSizeAsyncInner(GameInstallerKind gameInstallerKind,
        CancellationToken token)
    {
        await InitAsync(token).ConfigureAwait(false);

        GameReleaseInfo? released = gameInstallerKind == GameInstallerKind.Preload
            ? Manager.Preload ?? Manager.Released
            : Manager.Released;

        if (released == null)
        {
            return 0L;
        }

        if (released.PackageSize > 0)
        {
            return released.PackageSize;
        }

        // Fall back to summing the index once it has been fetched.
        try
        {
            (AzurPromiliaGameIndex? index, _) = await FetchRealityIndexAsync(released, token).ConfigureAwait(false);
            if (index == null)
            {
                return 0L;
            }

            long total = 0;
            foreach (AzurPromiliaIndexEntry entry in index.Files)
            {
                total += entry.Size;
            }

            return total;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromiliaInstaller] Unable to determine the game size from the index: {Error}", ex.Message);
            return 0L;
        }
    }

    protected override async Task<long> GetGameDownloadedSizeAsyncInner(GameInstallerKind gameInstallerKind,
        CancellationToken token)
    {
        await InitAsync(token).ConfigureAwait(false);

        GameManager.GetGamePath(out string? installPath);
        if (string.IsNullOrEmpty(installPath))
        {
            return 0L;
        }

        AzurPromiliaLocalManifest? manifest = Manager.ReadLocalManifest(installPath!);
        if (manifest?.Files == null)
        {
            return 0L;
        }

        long downloaded = 0;
        foreach (AzurPromiliaLocalManifestFile file in manifest.Files)
        {
            string localPath = Path.Combine(installPath!, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(localPath))
            {
                downloaded += new FileInfo(localPath).Length;
            }
        }

        return downloaded;
    }

    protected override Task StartInstallAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
        => new Install(this).RunAsync(GameInstallerKind.Install, progressDelegate, progressStateDelegate, token);

    protected override Task StartUpdateAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
        => new Install(this).RunAsync(GameInstallerKind.Update, progressDelegate, progressStateDelegate, token);

    protected override Task StartPreloadAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        // The server does not currently expose a pre-download endpoint for this title, so preloading
        // behaves as a normal install of the preload version into the staging directory.
        return new Install(this).RunAsync(GameInstallerKind.Preload, progressDelegate, progressStateDelegate,
            token);
    }

    protected override Task UninstallAsyncInner(CancellationToken token)
    {
        GameManager.IsGameInstalled(out bool isInstalled);
        if (!isInstalled)
        {
            return Task.CompletedTask;
        }

        GameManager.GetGamePath(out string? installPath);
        if (string.IsNullOrEmpty(installPath))
        {
            return Task.CompletedTask;
        }

        try
        {
            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromiliaInstaller] Uninstalling from '{Path}'", installPath);
            Directory.Delete(installPath!, true);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(
                "[AzurPromiliaInstaller] Uninstall failed: {Error}", ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Fetches and parses the REALITY (per-file) index for a release, returning the cached path too.
    /// </summary>
    internal async Task<(AzurPromiliaGameIndex? Index, string? Path)> FetchRealityIndexAsync(
        GameReleaseInfo release, CancellationToken token)
    {
        GamePackageInfo? package = release.GetPackage(AzurPromiliaGameManager.PackageTypeReality);
        if (package?.DownloadUrl == null || string.IsNullOrEmpty(release.VersionCode))
        {
            return (null, null);
        }

        GameManager.GetGamePath(out string? installPath);
        if (string.IsNullOrEmpty(installPath))
        {
            return (null, null);
        }

        string targetPath = AzurPromiliaGameManager.GetIndexPath(installPath!, release.VersionCode!);
        AzurPromiliaGameIndex index = await Manager
            .LoadIndexAsync(package, targetPath, false, token)
            .ConfigureAwait(false);

        return (index, targetPath);
    }

    internal HttpClient DownloadHttpClient => _downloadHttpClient;

    /// <summary>
    /// The effective channel configuration. Read through the game manager rather than kept as a field,
    /// because the manager re-resolves it once the game directory (and with it <c>account.json</c>,
    /// which may carry <c>launcherRoot</c>) becomes known.
    /// </summary>
    internal AzurPromiliaChannelConfig Channel => Manager.Channel;

    public override void Dispose()
    {
        _downloadHttpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
