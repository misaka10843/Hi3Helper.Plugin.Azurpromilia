using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.AzurPromilia.Management.Api;
using Hi3Helper.Plugin.AzurPromilia.Management.PresetConfig;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;

internal static class LauncherContentScenario
{
    internal static void Run()
    {
        var news = new AzurPromiliaLauncherApiNews(new AzurPromiliaCnPresetConfig());
        news.SetContent(new NewsConfigData
        {
            TxtContentMap = new()
            {
                ["1"] = [new() { Title = "News", LinkUrl = "https://example.com/news", PublishDate = "11.24" }],
                ["2"] = [new() { Title = "Notice", LinkUrl = "https://example.com/notice" }],
                ["3"] = [new() { Title = "Event", LinkUrl = "https://example.com/event" }]
            },
            MediaContentMap = new()
            {
                ["2"] =
                [
                    new() { Image = "https://example.com/b.png", Sort = 2 },
                    new() { Image = "https://example.com/a.png", LinkUrl = "https://example.com/event", Sort = 1 }
                ],
                ["11"] =
                [
                    new() { Icon = "https://example.com/social.png", Image = "https://example.com/qr.png" },
                    new() { Icon = "https://example.com/store.png", LinkUrl = "https://example.com/store" }
                ]
            }
        });

        using var entries = PluginDisposableMemoryExtension.ToManagedSpan<LauncherNewsEntry>(news.GetNewsEntries);
        using var carousel = PluginDisposableMemoryExtension.ToManagedSpan<LauncherCarouselEntry>(news.GetCarouselEntries);
        using var social = PluginDisposableMemoryExtension.ToManagedSpan<LauncherSocialMediaEntry>(news.GetSocialMediaEntries);
        Require(entries.Length == 3 && entries[0].Type == LauncherNewsEntryType.Info &&
                entries[1].Type == LauncherNewsEntryType.Notice && entries[2].Type == LauncherNewsEntryType.Event &&
                entries[0].PostDate == "11.24" && entries[0].Url == "https://example.com/news",
            "News dates, categories or links were lost.");
        Require(carousel.Length == 2 && carousel[0].ImageUrl == "https://example.com/a.png" &&
                carousel[0].ClickUrl == "https://example.com/event", "Carousel image order or links were lost.");
        Require(social.Length == 2 && social[0].QrPath == "https://example.com/qr.png" &&
                social[0].Flags == (LauncherSocialMediaEntryFlag.IconIsPath | LauncherSocialMediaEntryFlag.QrImageIsPath) &&
                social[1].ClickUrl == "https://example.com/store" &&
                social[1].Flags == LauncherSocialMediaEntryFlag.IconIsPath, "Social links or QR flags were lost.");
        Console.WriteLine("PASS: launcher content exports news categories, sorted carousel and social links/QR codes.");
        news.Dispose();
    }

    internal static async Task VerifyLiveAsync(NewsConfigData? content, CancellationToken token)
    {
        var preset = new AzurPromiliaCnPresetConfig();
        var news = new AzurPromiliaLauncherApiNews(preset);
        var media = new AzurPromiliaLauncherApiMedia(preset);
        news.SetContent(content);
        await media.LoadMediaAsync(content, token);

        using var entries = PluginDisposableMemoryExtension.ToManagedSpan<LauncherNewsEntry>(news.GetNewsEntries);
        using var carousel = PluginDisposableMemoryExtension.ToManagedSpan<LauncherCarouselEntry>(news.GetCarouselEntries);
        using var social = PluginDisposableMemoryExtension.ToManagedSpan<LauncherSocialMediaEntry>(news.GetSocialMediaEntries);
        using var backgrounds = PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(media.GetBackgroundEntries);
        using var logos = PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(media.GetLogoOverlayEntries);
        Require(entries.Length > 0 && carousel.Length > 0 && social.Length > 0 && backgrounds.Length > 0,
            "Official content did not produce valid launcher entries.");
        if (content?.MediaContentMap?.TryGetValue("12", out var videos) == true &&
            videos.Exists(entry => MediaContentEntry.WebUrl(entry.Video) != null))
        {
            Require(Path.GetExtension(backgrounds[0].Path) is ".mp4" or ".webm",
                "The official video must be the first selectable background.");
        }
        Console.WriteLine($"PASS: live content exported {entries.Length} news, {carousel.Length} banners, {social.Length} social entries.");
        for (int i = 0; i < backgrounds.Length; i++)
        {
            string? path = backgrounds[i].Path;
            Require(path != null && File.Exists(new Uri(path).LocalPath), "Background did not survive the host URI round trip.");
            Console.WriteLine($"Verified background: {path}");
        }
        Require(logos.Length == 0, "The standalone logo must not be exported as a full-frame overlay.");
        news.Dispose();
        media.Dispose();
    }

    private static void Require(bool valid, string message)
    {
        if (!valid)
        {
            throw new InvalidOperationException(message);
        }
    }
}
