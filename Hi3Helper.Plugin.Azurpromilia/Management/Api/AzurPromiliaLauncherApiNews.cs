using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
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
    private static readonly HashSet<string> CarouselContentIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "2", "4", "5", "6", "7", "8", "9", "10", "11"
    };

    private readonly AzurPromiliaCnPresetConfig _presetConfig;

    private readonly List<MediaContentEntry> _news = new();
    private readonly List<MediaContentEntry> _carousel = new();

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

            _news.Clear();
            _carousel.Clear();

            if (data?.TxtContentMap != null)
            {
                foreach (KeyValuePair<string, List<MediaContentEntry>> pair in data.TxtContentMap)
                {
                    if (pair.Value == null)
                    {
                        continue;
                    }

                    foreach (MediaContentEntry entry in pair.Value)
                    {
                        if (CarouselContentIds.Contains(pair.Key) && !string.IsNullOrEmpty(entry.Image))
                        {
                            _carousel.Add(entry);
                        }
                        else
                        {
                            _news.Add(entry);
                        }
                    }
                }
            }

            // Fall back to the promotional banner slots when no dedicated carousel content exists.
            if (_carousel.Count == 0 && data?.MediaContentMap != null)
            {
                foreach (KeyValuePair<string, List<MediaContentEntry>> pair in data.MediaContentMap)
                {
                    if (pair.Key is "1" or "3" or "12" || pair.Value == null)
                    {
                        continue;
                    }

                    foreach (MediaContentEntry entry in pair.Value)
                    {
                        if (!string.IsNullOrEmpty(entry.Image))
                        {
                            _carousel.Add(entry);
                        }
                    }
                }
            }

            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromiliaNews] Loaded {News} news item(s) and {Carousel} carousel item(s).",
                _news.Count, _carousel.Count);

            return 0;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError("[AzurPromiliaNews] Initialisation failed: {Error}", ex);
            return -1;
        }
    }

    public override void GetNewsEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        if (_news.Count == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = _news.Count;

        PluginDisposableMemory<LauncherNewsEntry> memory = PluginDisposableMemory<LauncherNewsEntry>.Alloc(count);
        handle = memory.AsSafePointer();
        isDisposable = true;
        isAllocated = true;

        for (int i = 0; i < count; i++)
        {
            MediaContentEntry item = _news[i];

            string title = item.Title ?? item.Content ?? string.Empty;
            string link = item.Link ?? item.JumpUrl ?? item.Url ?? string.Empty;
            string date = FormatDate(item.PublishTime ?? item.StartTime) ?? string.Empty;

            memory[i].Write(title, null, link, date, LauncherNewsEntryType.Info);
        }
    }

    public override void GetCarouselEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        if (_carousel.Count == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = _carousel.Count;

        PluginDisposableMemory<LauncherCarouselEntry> memory =
            PluginDisposableMemory<LauncherCarouselEntry>.Alloc(count);
        handle = memory.AsSafePointer();
        isDisposable = true;
        isAllocated = true;

        for (int i = 0; i < count; i++)
        {
            MediaContentEntry item = _carousel[i];
            memory[i].Write(item.Title ?? item.Content, item.Image, item.Link ?? item.JumpUrl ?? item.Url);
        }
    }

    public override void GetSocialMediaEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
        => InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
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
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromiliaNews] Asset download failed for '{Url}': {Error}", fileUrl, ex.Message);
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
