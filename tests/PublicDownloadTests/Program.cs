using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.AzurPromilia.Management;
using Hi3Helper.Plugin.AzurPromilia.Management.Api;
using Hi3Helper.Plugin.AzurPromilia.Utils;
using Hi3Helper.Plugin.AzurPromilia;
using Hi3Helper.Plugin.AzurPromilia.Yostar.Api;
using Hi3Helper.Plugin.AzurPromilia.Yostar.Management;
using Hi3Helper.Plugin.AzurPromilia.Yostar.Utility;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.AzurPromilia.Management.PresetConfig;

[assembly: SupportedOSPlatform("windows")]

using PublicApiHandler handler = new();
using HttpClient http = new(handler);
using AzurPromiliaApiClient api = new(AzurPromiliaChannelConfig.Default, http, "", "");
AzurPromiliaGameManager manager = new("AzurPromilia.exe", AzurPromiliaChannelConfig.Default);
typeof(AzurPromiliaGameManager).GetField("_apiClient", BindingFlags.Instance | BindingFlags.NonPublic)!
    .SetValue(manager, api);

if (await manager.InitAsyncInner() != 0 || manager.Released?.VersionCode != "1.0.0" ||
    manager.Released.GetPackage(2)?.DownloadUrl != "https://cdn.example/index" ||
    manager.Preload?.VersionCode != "1.1.0" || manager.LaunchEntry != "Game.exe" || handler.Count != 3)
{
    throw new Exception("Anonymous version, preload fallback or launch discovery failed.");
}
Console.WriteLine("PASS: public version/package, preload fallback and launch discovery without credentials.");
await PreloadScenario.RunAsync();
LauncherContentScenario.Run();
var plugin = new AzurPromiliaPlugin();
foreach ((string locale, string expected) in new[]
         {
             ("zh-cn", "zh-Hant"), ("zh_TW", "zh-Hant"), ("en-us", "en"), ("ja-jp", "en")
         })
{
    plugin.SetPluginLocaleId(locale);
    string globalLanguage = AzurPromiliaDeviceInfo.BuildBaseHeaders(AzurPromiliaChannelConfig.Global, "", "", null)["lang"];
    if (globalLanguage != expected ||
        AzurPromiliaDeviceInfo.BuildBaseHeaders(AzurPromiliaChannelConfig.Default, "", "", null)["lang"] != "zh-Hans" ||
        AzurPromiliaDeviceInfo.BuildBaseHeaders(AzurPromiliaChannelConfig.Bilibili, "", "", null)["lang"] != "zh-Hans")
    {
        throw new InvalidOperationException($"Unexpected launcher API language for {locale}.");
    }
}
plugin.SetPluginLocaleId(args.SkipWhile(arg => arg != "--locale").Skip(1).FirstOrDefault() ?? "en-us");
plugin.GetPresetConfigCount(out int presetCount);
if (presetCount != 4)
{
    throw new InvalidOperationException("Expected one preset per supported region.");
}
plugin.GetPresetConfig(3, out var japanPreset);
var japanConfig = (AzurPromiliaPresetConfig)japanPreset;
var japan = (YostarGameManager)japanConfig.GameManager!;

if (args.Contains("--live-jp"))
{
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
    YostarTargetPackage target = await japan.RefreshTargetPackageAsync(timeout.Token);
    YostarManifestFile sample = target.Manifest.Files.Where(file => file.SizeValue > 0).MinBy(file => file.SizeValue)
        ?? throw new InvalidDataException("The Japanese manifest contains no downloadable files.");
    Uri sampleUri = YostarGameInstaller.BuildDownloadUri(target.Cdn.PrimaryCdn!, target.Manifest.Source, sample.Path);
    string path = Path.GetTempFileName();
    try
    {
        byte[] data = await japan.ApiClient.DownloadHttpClient.GetByteArrayAsync(sampleUri, timeout.Token);
        await File.WriteAllBytesAsync(path, data, timeout.Token);
        if (data.LongLength != sample.SizeValue ||
            await YostarCrc64.ComputeFileAsync(path, timeout.Token) != sample.Hash ||
            !string.Equals(japan.ExecutableName, "AzurPromiliaJP.exe", StringComparison.OrdinalIgnoreCase) ||
            !japan.LaunchParameters.Contains("-start=azurpromilia_launcher"))
        {
            throw new InvalidDataException("The live Japanese package or launch configuration failed verification.");
        }
    }
    finally
    {
        File.Delete(path);
    }
    Console.WriteLine($"PASS: Japan {target.Config.LatestVersion}, {target.Manifest.Files.Count} files, CDN CRC64 and launch metadata.");
    string planDirectory = Directory.CreateTempSubdirectory("azur-japan-plan-").FullName;
    try
    {
        japan.SetGamePath(planDirectory);
        var sizeQuery = typeof(YostarGameInstaller).GetMethod("GetGameSizeAsyncInner",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var size = (Task<long>)sizeQuery.Invoke(japanConfig.GameInstaller,
            [GameInstallerKind.Install, timeout.Token])!;
        if (await size != target.Manifest.Files.Sum(file => file.SizeValue))
        {
            throw new InvalidDataException("The Japanese installer plan did not cover the complete manifest.");
        }
    }
    finally
    {
        japan.SetGamePath(string.Empty);
        Directory.Delete(planDirectory, true);
    }
    var background = await japan.ApiClient.GetBaseConfigAsync(timeout.Token);
    var operations = await japan.ApiClient.GetOperationsResourceAsync(timeout.Token);
    var social = await japan.ApiClient.GetSocialMediaResourceAsync(timeout.Token);
    Console.WriteLine($"PASS: Japan launcher content, background={background.LauncherBackgroundImg != null}, banners={operations.Banners?.Count ?? 0}, social={social != null}.");
}

if (args.Contains("--live"))
{
    string? launcherRoot = args.SkipWhile(arg => arg != "--launcher-root").Skip(1).FirstOrDefault();
    AzurPromiliaChannelConfig channelConfig = args.SkipWhile(arg => arg != "--region").Skip(1).FirstOrDefault() switch
    {
        "bilibili" => AzurPromiliaChannelConfig.Bilibili,
        "global" => AzurPromiliaChannelConfig.Global,
        _ => AzurPromiliaChannelConfig.Resolve(launcherRoot)
    };
    AzurPromiliaGameManager liveManager = new("AzurPromilia.exe", channelConfig);
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
    int result = await liveManager.InitAsyncInner(token: timeout.Token);
    if (result != 0 && liveManager.ApiClient is { } liveApi)
    {
        await liveApi.GetAsync(liveApi.BaseDomains, "/pc-launcher/client/gameversion/packageconfig",
            new() { ["launcherId"] = liveApi.Channel.LauncherId },
            AzurPromiliaApiContext.Default.AzurPromiliaResponseGameVersionConfigData, timeout.Token);
    }
    if (result != 0 ||
        new[] { liveManager.Released, liveManager.Preload }.All(release =>
            release?.GamePackageList?.Any(p => !string.IsNullOrEmpty(p.DownloadUrl)) != true))
    {
        throw new Exception("The live API did not publish an anonymous download package.");
    }
    Console.WriteLine("PASS: live anonymous package discovery.");
    Console.WriteLine($"Manjuu launch entry: {liveManager.LaunchEntry ?? "(not published)"}");
    var liveApiClient = liveManager.ApiClient!;
    NewsConfigData? content = await liveApiClient.GetAsync(liveApiClient.BaseDomains,
        "/pc-launcher/client/launcher/contentconfig", new() { ["launcherId"] = liveApiClient.Channel.LauncherId },
        AzurPromiliaApiContext.Default.AzurPromiliaResponseNewsConfigData, timeout.Token);
    await LauncherContentScenario.VerifyLiveAsync(content, timeout.Token);
    using HttpClient httpForCdn = new();
    foreach (GameReleaseInfo release in new[] { liveManager.Released, liveManager.Preload }.OfType<GameReleaseInfo>())
    foreach (int packageType in new[] { 1, 2 })
    {
        if (release.GetPackage(packageType) is not { DownloadUrl.Length: > 0 } package)
        {
            continue;
        }

        string indexPath = Path.GetTempFileName();
        try
        {
            AzurPromiliaGameIndex index = await liveManager.LoadIndexAsync(package, indexPath,
                package.PackageType == 1, timeout.Token);
            Console.WriteLine($"Version {release.VersionCode}, package {packageType}: {index.Files.Count} files, {index.Slices.Count} fragments");
            if (packageType == 2)
            {
                AzurPromiliaIndexEntry sample = index.Files
                    .Where(entry => entry.Size > 0 && entry.Urls.Count > 0 && entry.Md5 != null)
                    .MinBy(entry => entry.Size) ?? throw new InvalidDataException("No downloadable file in the index.");
                byte[] payload = await httpForCdn.GetByteArrayAsync(sample.Urls[0], timeout.Token);
                if (payload.LongLength != sample.Size ||
                    !string.Equals(AzurPromiliaCrypto.Md5Hex(payload), sample.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The sample CDN file failed integrity verification.");
                }
                Console.WriteLine("PASS: official CDN sample matched the published size and MD5.");
            }
        }
        finally
        {
            File.Delete(indexPath);
        }
    }
    liveManager.Dispose();
}

sealed class PublicApiHandler : HttpMessageHandler
{
    public int Count { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Count++;
        Uri uri = request.RequestUri!;
        if (request.Method != HttpMethod.Get || uri.Query.Contains("uid", StringComparison.OrdinalIgnoreCase) ||
            (request.Headers.TryGetValues("utoken", out var tokens) && tokens.Any(t => t.Length != 0)))
        {
            throw new Exception("The public flow attempted to authenticate.");
        }

        string timestamp = request.Headers.GetValues("timestamp").Single();
        string canonicalQuery = string.Join("&", uri.Query.TrimStart('?').Split('&').Order(StringComparer.Ordinal));
        var channel = AzurPromiliaChannelConfig.Default;
        string expected = AzurPromiliaCrypto.ComputeSignature(timestamp + canonicalQuery,
            channel.AppId, channel.ChannelCode, channel.SecretKey);
        if (request.Headers.GetValues("sign").Single() != expected)
        {
            throw new Exception("Anonymous request signature does not match the transmitted query.");
        }

        string body = uri.AbsolutePath switch
        {
            "/pc-launcher/client/gameversion/packageconfig" => """
                {"code":0,"data":{"downloadMode":2,"released":{"versionCode":"1.0.0","gamePackageList":[{"packageType":2,"downloadUrl":"https://cdn.example/index"}]}}}
                """,
            "/pc-launcher/client/gameversion/config" => """
                {"code":0,"data":{"preload":{"versionCode":"1.1.0"}}}
                """,
            "/pc-launcher/client/gameversion/pre/check" => """
                {"code":0,"data":{"launchEntry":"Game.exe"}}
                """,
            _ => throw new Exception("Unexpected endpoint: " + uri.AbsolutePath)
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        });
    }
}
