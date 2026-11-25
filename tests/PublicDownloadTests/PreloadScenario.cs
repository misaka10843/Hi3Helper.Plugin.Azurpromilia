using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.AzurPromilia.Management;
using Hi3Helper.Plugin.AzurPromilia.Management.Api;
using Hi3Helper.Plugin.AzurPromilia.Utils;

internal static class PreloadScenario
{
    public static async Task RunAsync()
    {
        string path = Directory.CreateTempSubdirectory("azur-preload-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(path, "Game.exe"), "old");
            using PackageHandler handler = new();
            using HttpClient http = new(handler);
            AzurPromiliaGameManager manager = new("Game.exe", AzurPromiliaChannelConfig.Default);
            typeof(AzurPromiliaGameManager).GetProperty("ApiResponseHttpClient",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, http);
            using AzurPromiliaApiClient api = new(AzurPromiliaChannelConfig.Default, http, "", "");
            typeof(AzurPromiliaGameManager).GetField("_apiClient", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(manager, api);
            typeof(AzurPromiliaGameManager).GetProperty("CurrentGameInstallPath",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, path);
            AzurPromiliaGameInstaller installer = new(manager);
            installer.DownloadHttpClient.Dispose();
            typeof(AzurPromiliaGameInstaller).GetField("_downloadHttpClient",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(installer, http);

            string versionPath = Path.Combine(path, "_index", "version");
            Directory.CreateDirectory(Path.GetDirectoryName(versionPath)!);
            const string installed = """{"version":"1.0.0","versionName":"1.0.0"}""";
            await File.WriteAllTextAsync(versionPath, installed);

            await StartAsync(installer, "StartPreloadAsyncInner");
            string staged = Path.Combine(path, "_pre_download", "1.1.0", "Game.exe");
            if (await File.ReadAllTextAsync(staged) != PackageHandler.Payload ||
                await File.ReadAllTextAsync(Path.Combine(path, "Game.exe")) != "old" ||
                await File.ReadAllTextAsync(versionPath) != installed)
            {
                throw new InvalidOperationException("Preload modified the installed game or did not stage the new file.");
            }

            handler.Published = true;
            await StartAsync(installer, "StartInstallAsyncInner");
            if (await File.ReadAllTextAsync(Path.Combine(path, "Game.exe")) != PackageHandler.Payload ||
                !(await File.ReadAllTextAsync(versionPath)).Contains("1.1.0", StringComparison.Ordinal) ||
                File.Exists(staged) || handler.PayloadRequests != 1)
            {
                throw new InvalidOperationException("Release install did not reuse the verified preload.");
            }

            installer.Dispose();
            manager.Dispose();
            Console.WriteLine("PASS: preload isolation and verified reuse through the install entry point.");
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    private static Task StartAsync(AzurPromiliaGameInstaller installer, string entryPoint)
        => (Task)typeof(AzurPromiliaGameInstaller).GetMethod(entryPoint, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(installer, [null, null, CancellationToken.None])!;

    private sealed class PackageHandler : HttpMessageHandler
    {
        internal const string Payload = "new game contents";
        private readonly string _index = MakeIndex();

        internal bool Published { get; set; }
        internal int PayloadRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string package = $$"""
                {"versionCode":"1.1.0","launchEntry":"Game.exe","domainList":["https://cdn.example"],
                 "gamePackageList":[{"packageType":2,"downloadUrl":"/index","fileSize":{{_index.Length}},"fileMd5":"{{AzurPromiliaCrypto.Md5Hex(_index)}}"}]}
                """;
            string body = path switch
            {
                "/pc-launcher/client/gameversion/packageconfig" when Published =>
                    $$$"""{"code":0,"data":{"released":{{{package}}},"downloadMode":2}}""",
                "/pc-launcher/client/gameversion/packageconfig" =>
                    $$$"""{"code":0,"data":{"released":{"versionCode":"1.0.0","launchEntry":"Game.exe"},"preload":{{{package}}},"downloadMode":2}}""",
                "/pc-launcher/client/gameversion/config" => """{"code":0,"data":{}}""",
                "/pc-launcher/client/gameversion/pre/check" => """{"code":0,"data":{"launchEntry":"Game.exe"}}""",
                "/index" => _index,
                "/file" => Payload,
                _ => throw new InvalidOperationException($"Unexpected request to {path}")
            };
            if (path == "/file")
            {
                PayloadRequests++;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }

        private static string MakeIndex()
        {
            using Aes aes = Aes.Create();
            aes.Key = (byte[])typeof(AzurPromiliaCrypto).GetField("ResourceIndexKeyBytes",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            byte[] iv = (byte[])typeof(AzurPromiliaCrypto).GetField("ResourceIndexIvBytes",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            byte[] csv = Encoding.UTF8.GetBytes($"0,{Payload.Length},{AzurPromiliaCrypto.Md5Hex(Payload)},Game.exe,/file");
            return Convert.ToBase64String(aes.EncryptCbc(csv, iv));
        }
    }
}
