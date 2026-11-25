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

if (args.Contains("--live"))
{
    string? launcherRoot = args.SkipWhile(arg => arg != "--launcher-root").Skip(1).FirstOrDefault();
    AzurPromiliaChannelConfig channelConfig = AzurPromiliaChannelConfig.Resolve(launcherRoot);
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
