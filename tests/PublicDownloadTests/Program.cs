using System;
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
using AzurPromiliaGameManager manager = new("AzurPromilia.exe", AzurPromiliaChannelConfig.Default);
typeof(AzurPromiliaGameManager).GetField("_apiClient", BindingFlags.Instance | BindingFlags.NonPublic)!
    .SetValue(manager, api);

if (await manager.InitAsyncInner() != 0 || manager.Released?.VersionCode != "1.0.0" ||
    manager.Released.GetPackage(2)?.DownloadUrl != "https://cdn.example/index" ||
    manager.Preload?.VersionCode != "1.1.0" || manager.LaunchEntry != "Game.exe" || handler.Count != 3)
{
    throw new Exception("Anonymous version, preload fallback or launch discovery failed.");
}
Console.WriteLine("PASS: public version/package, preload fallback and launch discovery without credentials.");

if (args.Contains("--live"))
{
    using AzurPromiliaGameManager liveManager = new("AzurPromilia.exe", AzurPromiliaChannelConfig.Default);
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
    if (await liveManager.InitAsyncInner(token: timeout.Token) != 0 ||
        liveManager.Released?.GamePackageList?.Any(p => !string.IsNullOrEmpty(p.DownloadUrl)) != true)
    {
        throw new Exception("The live API did not publish an anonymous download package.");
    }
    Console.WriteLine("PASS: live anonymous package discovery, version=" + liveManager.Released.VersionCode);
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
