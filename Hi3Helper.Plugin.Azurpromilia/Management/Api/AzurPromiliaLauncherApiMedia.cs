using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.AzurPromilia.Management.PresetConfig;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia.Management.Api;

/// <summary>
/// Launcher background / logo provider, backed by <c>getNews</c>.
/// <para>
/// The server publishes a <c>mediaContentMap</c> keyed by content id:
/// <c>"1"</c> = background image, <c>"3"</c> = logo, <c>"12"</c> = home background video.
/// </para>
/// </summary>
[GeneratedComClass]
public partial class AzurPromiliaLauncherApiMedia : LauncherApiMediaBase
{
    private const string MediaKeyBackground = "1";
    private const string MediaKeyLogo = "3";
    private const string MediaKeyVideo = "12";

    private readonly AzurPromiliaCnPresetConfig _presetConfig;

    private string? _backgroundImageUrl;
    private string? _backgroundVideoUrl;
    private string? _logoUrl;

    public AzurPromiliaLauncherApiMedia(AzurPromiliaCnPresetConfig presetConfig)
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
            .AllowUntrustedCert()
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

            _backgroundImageUrl = FirstImage(mediaMap, MediaKeyBackground);
            _logoUrl = FirstImage(mediaMap, MediaKeyLogo);
            _backgroundVideoUrl = FirstVideo(mediaMap, MediaKeyVideo);

            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromiliaMedia] background={Background}, video={Video}, logo={Logo}",
                _backgroundImageUrl ?? "(none)", _backgroundVideoUrl ?? "(none)", _logoUrl ?? "(none)");

            return 0;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError("[AzurPromiliaMedia] Initialisation failed: {Error}", ex);
            return -1;
        }
    }

    private static string? FirstImage(Dictionary<string, List<MediaContentEntry>> map, string key)
    {
        if (!map.TryGetValue(key, out List<MediaContentEntry>? entries) || entries.Count == 0)
        {
            return null;
        }

        return entries[0].Image;
    }

    private static string? FirstVideo(Dictionary<string, List<MediaContentEntry>> map, string key)
    {
        if (!map.TryGetValue(key, out List<MediaContentEntry>? entries) || entries.Count == 0)
        {
            return null;
        }

        return entries[0].Video;
    }

    public override void GetBackgroundEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        // Prefer the static image; the video (key "12") is used only when no image is published.
        string? url = !string.IsNullOrEmpty(_backgroundImageUrl) ? _backgroundImageUrl : _backgroundVideoUrl;

        if (string.IsNullOrEmpty(url))
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        PluginDisposableMemory<LauncherPathEntry> memory = PluginDisposableMemory<LauncherPathEntry>.Alloc();
        memory[0].Write(url, Span<byte>.Empty);

        handle = memory.AsSafePointer();
        count = 1;
        isDisposable = true;
        isAllocated = true;
    }

    public override void GetBackgroundFlag(out LauncherBackgroundFlag result)
    {
        result = !string.IsNullOrEmpty(_backgroundImageUrl)
            ? LauncherBackgroundFlag.TypeIsImage
            : !string.IsNullOrEmpty(_backgroundVideoUrl)
                ? LauncherBackgroundFlag.TypeIsVideo
                : LauncherBackgroundFlag.None;
    }

    public override void GetLogoFlag(out LauncherBackgroundFlag result)
    {
        result = string.IsNullOrEmpty(_logoUrl)
            ? LauncherBackgroundFlag.None
            : LauncherBackgroundFlag.TypeIsImage;
    }

    public override void GetLogoOverlayEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        if (string.IsNullOrEmpty(_logoUrl))
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        PluginDisposableMemory<LauncherPathEntry> memory = PluginDisposableMemory<LauncherPathEntry>.Alloc();
        memory[0].Write(_logoUrl, Span<byte>.Empty);

        handle = memory.AsSafePointer();
        count = 1;
        isDisposable = true;
        isAllocated = true;
    }

    public override void GetBackgroundSpriteFps(out float result) => result = 60f;

    protected override async Task DownloadAssetAsyncInner(HttpClient? client, string fileUrl, Stream outputStream,
        PluginDisposableMemory<byte> fileChecksum, PluginFiles.FileReadProgressDelegate? downloadProgress,
        CancellationToken token)
    {
        try
        {
            await base.DownloadAssetAsyncInner(ApiDownloadHttpClient, fileUrl, outputStream, fileChecksum,
                downloadProgress, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Background art is decorative: a failure must not break the launcher.
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromiliaMedia] Background download failed for '{Url}': {Error}", fileUrl, ex.Message);
        }
    }

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
