using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.AzurPromilia.Management.PresetConfig;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia.Management.Api;

[GeneratedComClass]
public partial class AzurPromiliaLauncherApiNews : LauncherApiNewsBase
{
    private readonly AzurPromiliaCnPresetConfig _presetConfig;

    private (MediaContentEntry Entry, LauncherNewsEntryType Type)[] _news = [];
    private MediaContentEntry[] _carousel = [];
    private MediaContentEntry[] _social = [];

    public AzurPromiliaLauncherApiNews(AzurPromiliaCnPresetConfig presetConfig)
    {
        _presetConfig = presetConfig;
    }

    [field: AllowNull] [field: MaybeNull] protected override HttpClient ApiResponseHttpClient { get; set; } = new();

    [field: AllowNull]
    [field: MaybeNull]
    protected HttpClient ApiDownloadHttpClient
    {
        get => field ??= new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.All)
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

            SetContent(data);
            return 0;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            SharedStatic.InstanceLogger.LogError("[AzurPromiliaNews] Initialisation failed: {Error}", ex);
            return -1;
        }
    }

    internal void SetContent(NewsConfigData? data)
    {
        _news = Entries(data?.TxtContentMap, "1").Select(entry => (entry, LauncherNewsEntryType.Info))
            .Concat(Entries(data?.TxtContentMap, "2").Select(entry => (entry, LauncherNewsEntryType.Notice)))
            .Concat(Entries(data?.TxtContentMap, "3").Select(entry => (entry, LauncherNewsEntryType.Event)))
            .Where(item => !string.IsNullOrWhiteSpace(item.entry.Title ?? item.entry.Content))
            .ToArray();
        _carousel = Entries(data?.MediaContentMap, "2")
            .Where(entry => MediaContentEntry.WebUrl(entry.Image) != null).ToArray();
        _social = Entries(data?.MediaContentMap, "11")
            .Where(entry => MediaContentEntry.WebUrl(entry.Icon) != null &&
                            (MediaContentEntry.WebUrl(entry.Image) != null || entry.ClickUrl != null)).ToArray();
    }

    private static IEnumerable<MediaContentEntry> Entries(Dictionary<string, List<MediaContentEntry>>? map, string key)
        => map?.GetValueOrDefault(key)?.OrderBy(entry => entry.Sort) ?? Enumerable.Empty<MediaContentEntry>();

    public override void GetNewsEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        var news = _news;
        if (news.Length == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = news.Length;

        PluginDisposableMemory<LauncherNewsEntry> memory = PluginDisposableMemory<LauncherNewsEntry>.Alloc(count);
        handle = memory.AsSafePointer();
        isDisposable = true;
        isAllocated = true;

        for (int i = 0; i < count; i++)
        {
            (MediaContentEntry item, LauncherNewsEntryType type) = news[i];

            string title = item.Title ?? item.Content ?? string.Empty;
            string link = item.ClickUrl ?? string.Empty;
            string date = item.PublishDate ?? FormatDate(item.PublishTime ?? item.StartTime) ?? string.Empty;

            memory[i].Write(title, null, link, date, type);
        }
    }

    public override void GetCarouselEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        MediaContentEntry[] carousel = _carousel;
        if (carousel.Length == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = carousel.Length;

        PluginDisposableMemory<LauncherCarouselEntry> memory =
            PluginDisposableMemory<LauncherCarouselEntry>.Alloc(count);
        handle = memory.AsSafePointer();
        isDisposable = true;
        isAllocated = true;

        for (int i = 0; i < count; i++)
        {
            MediaContentEntry item = carousel[i];
            memory[i].Write(item.Title ?? item.Content, MediaContentEntry.WebUrl(item.Image), item.ClickUrl);
        }
    }

    public override void GetSocialMediaEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        MediaContentEntry[] social = _social;
        if (social.Length == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = social.Length;
        PluginDisposableMemory<LauncherSocialMediaEntry> memory =
            PluginDisposableMemory<LauncherSocialMediaEntry>.Alloc(count);
        handle = memory.AsSafePointer();
        isDisposable = true;
        isAllocated = true;

        for (int i = 0; i < count; i++)
        {
            MediaContentEntry item = social[i];
            string? qr = MediaContentEntry.WebUrl(item.Image);
            memory[i].WriteIcon(MediaContentEntry.WebUrl(item.Icon));
            memory[i].WriteDescription(item.Title);
            memory[i].WriteClickUrl(item.ClickUrl);
            if (qr != null)
            {
                memory[i].WriteQrImage(qr);
                memory[i].WriteQrImageDescription(item.Title);
            }

            // Core's writers also set unrelated bits. Publish only the flags for supplied data.
            memory[i].Flags = LauncherSocialMediaEntryFlag.IconIsPath |
                              (qr == null ? LauncherSocialMediaEntryFlag.None : LauncherSocialMediaEntryFlag.QrImageIsPath);
        }
    }
    private static string? FormatDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string value = raw.Trim();

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch))
        {
            // Anything below this threshold is a second-precision timestamp.
            DateTimeOffset moment = epoch < 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : DateTimeOffset.FromUnixTimeMilliseconds(epoch);

            return moment.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value;
    }

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
