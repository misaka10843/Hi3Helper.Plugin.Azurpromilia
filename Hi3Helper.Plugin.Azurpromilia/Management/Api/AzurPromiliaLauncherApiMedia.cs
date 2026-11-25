using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.AzurPromilia.Management.PresetConfig;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia.Management.Api;

[GeneratedComClass]
public partial class AzurPromiliaLauncherApiMedia : LauncherApiMediaBase
{
    private const string MediaKeyBackground = "1";
    private const string MediaKeyVideo = "12";

    private readonly AzurPromiliaPresetConfig _presetConfig;

    private string[] _backgroundPaths = [];

    public AzurPromiliaLauncherApiMedia(AzurPromiliaPresetConfig presetConfig)
    {
        _presetConfig = presetConfig;
    }

    [field: AllowNull] [field: MaybeNull] protected override HttpClient ApiResponseHttpClient { get; set; } = new();

    [field: AllowNull]
    [field: MaybeNull]
    protected HttpClient ApiDownloadHttpClient
    {
        get => field ??= new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.None)
            .AllowCookies()
            .AllowRedirections()
            .Create();
        set;
    }

    protected override string ApiResponseBaseUrl => string.Empty;

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        try
        {
            if (_presetConfig.GameManager is not AzurPromiliaGameManager manager)
            {
                return -1;
            }

            AzurPromiliaApiClient? api = await manager.EnsureApiClientAsync(token).ConfigureAwait(false);
            if (api == null)
            {
                return -1;
            }

            NewsConfigData? data = await api
                .GetAsync(api.BaseDomains, "/pc-launcher/client/launcher/contentconfig",
                    new Dictionary<string, string?> { ["launcherId"] = api.Channel.LauncherId },
                    AzurPromiliaApiContext.Default.AzurPromiliaResponseNewsConfigData, token)
                .ConfigureAwait(false);

            Dictionary<string, List<MediaContentEntry>>? mediaMap = data?.MediaContentMap;
            if (mediaMap == null)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[AzurPromiliaMedia] contentconfig returned no mediaContentMap.");
                return -1;
            }

            await LoadMediaAsync(data, token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            SharedStatic.InstanceLogger.LogError("[AzurPromiliaMedia] Initialisation failed: {Error}", ex);
            return -1;
        }
    }

    internal async Task LoadMediaAsync(NewsConfigData? data, CancellationToken token)
    {
        var map = data?.MediaContentMap;
        Task<string?> image = CacheMediaAsync(FindMedia(MediaKeyBackground, entry => entry.Image), ".png", token);
        Task<string?> video = CacheMediaAsync(FindMedia(MediaKeyVideo, entry => entry.Video), ".mp4", token);
        await Task.WhenAll(image, video).ConfigureAwait(false);

        // Collapse selects the first background when no preference has been saved.
        _backgroundPaths = new[] { await video.ConfigureAwait(false), await image.ConfigureAwait(false) }
            .OfType<string>().ToArray();
        return;

        string? FindMedia(string key, Func<MediaContentEntry, string?> selector)
            => map?.GetValueOrDefault(key)?.OrderBy(entry => entry.Sort).Select(selector)
                .Select(MediaContentEntry.WebUrl).FirstOrDefault(url => url != null);
    }

    private async Task<string?> CacheMediaAsync(string? url, string defaultExtension, CancellationToken token)
    {
        if (url == null)
        {
            return null;
        }

        string extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".avif" or ".bmp" or ".gif" or ".mp4" or ".webm"))
        {
            extension = defaultExtension;
        }

        // Collapse's URI handling unescapes the CDN filename when opening its cached copy.
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hi3Helper", "AzurPromilia", "Media");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        string path = Path.Combine(directory, hash + extension);
        string partial = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > 0 })
            {
                return path;
            }

            Directory.CreateDirectory(directory);
            using HttpResponseMessage response = await ApiDownloadHttpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (FileStream output = File.Create(partial))
            {
                await response.Content.CopyToAsync(output, token).ConfigureAwait(false);
                if (output.Length == 0 ||
                    (response.Content.Headers.ContentLength is long size && output.Length != size))
                {
                    throw new InvalidDataException("The published media download was empty or incomplete.");
                }
            }

            File.Move(partial, path, true);
            return path;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            SharedStatic.InstanceLogger.LogWarning(ex, "Could not cache Azur Promilia media.");
            return null;
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    public override void GetBackgroundEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        string[] paths = _backgroundPaths;
        if (paths.Length == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        PluginDisposableMemory<LauncherPathEntry> memory = PluginDisposableMemory<LauncherPathEntry>.Alloc(paths.Length);
        for (int i = 0; i < paths.Length; i++)
        {
            memory[i].Write(paths[i], Span<byte>.Empty);
        }

        handle = memory.AsSafePointer();
        count = paths.Length;
        isDisposable = true;
        isAllocated = true;
    }

    public override void GetBackgroundFlag(out LauncherBackgroundFlag result)
    {
        result = _backgroundPaths.Aggregate(LauncherBackgroundFlag.None, (flags, path) =>
            flags | LauncherBackgroundFlag.IsSourceFile |
            (Path.GetExtension(path) is ".mp4" or ".webm"
                ? LauncherBackgroundFlag.TypeIsVideo
                : LauncherBackgroundFlag.TypeIsImage));
    }

    public override void GetLogoFlag(out LauncherBackgroundFlag result) => result = LauncherBackgroundFlag.None;

    // Collapse treats overlays as full-frame artwork. The official logo is a standalone badge.
    public override void GetLogoOverlayEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
        => InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);

    public override void GetBackgroundSpriteFps(out float result) => result = 60f;

    protected override Task DownloadAssetAsyncInner(HttpClient? client, string fileUrl, Stream outputStream,
        PluginDisposableMemory<byte> fileChecksum, PluginFiles.FileReadProgressDelegate? downloadProgress,
        CancellationToken token)
        => base.DownloadAssetAsyncInner(ApiDownloadHttpClient, fileUrl, outputStream, fileChecksum,
            downloadProgress, token);

    private static void InitializeEmpty(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        handle = nint.Zero;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }

    public override void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        ApiResponseHttpClient?.Dispose();
        ApiDownloadHttpClient?.Dispose();
        base.Dispose();
    }
}
