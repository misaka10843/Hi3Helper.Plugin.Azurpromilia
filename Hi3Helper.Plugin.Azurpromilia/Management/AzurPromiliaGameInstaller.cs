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

[GeneratedComClass]
internal partial class AzurPromiliaGameInstaller : GameInstallerBase
{
    internal const int DefaultConcurrency = 4;
    internal const string TempFileSuffix = ".~tmp";
    internal const int MaxFileRetries = 4;

    private readonly HttpClient _downloadHttpClient;

    internal AzurPromiliaGameInstaller(IGameManager? gameManager)
        : base(gameManager)
    {
        // Download URLs are plain CDN GETs; the launcher does not sign them, so neither do we.
        _downloadHttpClient = new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.None)
            .AllowRedirections()
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
            ? Manager.Preload
            : Manager.Released;

        if (released == null)
        {
            return 0L;
        }

        if (gameInstallerKind == GameInstallerKind.Preload)
        {
            var (total, _) = await new Install(this).GetPreloadSizesAsync(released, token).ConfigureAwait(false);
            return total;
        }

        if (released.PackageSize > 0)
        {
            return released.PackageSize;
        }

        try
        {
            AzurPromiliaGameIndex? index = await FetchRealityIndexAsync(released, token).ConfigureAwait(false);
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
        catch (Exception ex) when (!token.IsCancellationRequested)
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

        if (gameInstallerKind == GameInstallerKind.Preload)
        {
            if (Manager.Preload is not { } preload)
            {
                return 0L;
            }

            var (_, staged) = await new Install(this).GetPreloadSizesAsync(preload, token).ConfigureAwait(false);
            return staged;
        }

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
            string localPath = GetContentPath(installPath, NormalizeIndexPath(file.Path));
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
        => new Install(this).RunAsync(GameInstallerKind.Preload, progressDelegate, progressStateDelegate, token);

    protected override Task UninstallAsyncInner(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
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

        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
        if (Path.GetPathRoot(fullPath) == fullPath)
        {
            throw new InvalidOperationException("A drive root cannot be uninstalled as a game directory.");
        }

        SharedStatic.InstanceLogger.LogInformation("Uninstalling Azur Promilia from '{Path}'.", fullPath);
        Directory.Delete(fullPath, true);
        return Task.CompletedTask;
    }

    private async Task<AzurPromiliaGameIndex?> FetchRealityIndexAsync(
        GameReleaseInfo release, CancellationToken token)
    {
        GamePackageInfo? package = release.GetPackage(AzurPromiliaGameManager.PackageTypeReality);
        if (package?.DownloadUrl == null || string.IsNullOrEmpty(release.VersionCode))
        {
            return null;
        }

        GameManager.GetGamePath(out string? installPath);
        if (string.IsNullOrEmpty(installPath))
        {
            return null;
        }

        string targetPath = AzurPromiliaGameManager.GetIndexPath(installPath!, release.VersionCode!);
        return await Manager
            .LoadIndexAsync(package, targetPath, false, token)
            .ConfigureAwait(false);
    }

    internal HttpClient DownloadHttpClient => _downloadHttpClient;

    internal static string NormalizeIndexPath(string path) => path.Replace('\\', '/').TrimStart('/');

    internal static string GetContentPath(string directory, string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        foreach (string component in normalized.Split('/'))
        {
            if (component.Length == 0 || component is "." or ".." ||
                component.EndsWith('.') || component.EndsWith(' ') ||
                component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException($"Invalid relative path '{relativePath}'.");
            }
        }

        return Path.Combine(directory, normalized.Replace('/', Path.DirectorySeparatorChar));
    }

    public override void Dispose()
    {
        _downloadHttpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
