using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Hi3Helper.Plugin.AzurPromilia.Management.Api;
using Hi3Helper.Plugin.AzurPromilia.Utils;

namespace AccountResolutionTests;

/// <summary>
/// Smoke tests for the two things the plugin cannot get wrong:
/// <list type="number">
/// <item><see cref="AzurPromiliaAccount"/> credential-file resolution.</item>
/// <item>The request signature formula, which is asymmetric between GET and POST.</item>
/// </list>
/// </summary>
internal static class Program
{
    private static int _failures;

    private static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail == null ? string.Empty : "   [" + detail + "]")}");
        if (!ok)
        {
            _failures++;
        }
    }

    // --- Signature known-answer vectors ------------------------------------------------------
    // Cross-checked against the live API; see tools/probe/probe_sign.py, which replays these
    // requests and shows that the empty-query payload is answered with "code=2, MD5校验错误"
    // while the body payload is accepted.
    private const long KnownTimestamp = 1700000000000L;
    private const string KnownTimestampText = "1700000000000";
    private const string KnownBody = "{\"accountType\":2,\"account\":\"a@b.c\",\"pwd\":\"X\"}";
    private const string KnownCanonicalQuery = "launcherId=a35deadb9f8846c9ad69bc2f4e299b8c";
    private const string KnownGetSign = "8d74e495981718726e63c60d51295189";
    private const string KnownPostSign = "b1ae763c0aaf7e2f9930bfde8f5ea9ac";
    private const string KnownEmptyQuerySign = "641d785255e0f4ba7657d85c5d709907";
    private const string KnownNullUtokenSign = "9f3ce4d58032fb035065e4913be9fc1c";

    /// <summary>
    /// Pins the signing rules. A wrong signature is reported by the server as
    /// <c>code=2, "MD5校验错误"</c>, which is indistinguishable from a bad password, so the formula is
    /// asserted directly rather than left to a live login attempt to discover.
    /// </summary>
    private static void RunSigningChecks()
    {
        AzurPromiliaChannelConfig channel = AzurPromiliaChannelConfig.Default;

        string Sign(string payload) => AzurPromiliaCrypto.ComputeSignature(payload, channel.AppId,
            channel.ChannelCode, channel.SecretKey);

        // --- GET: timestamp + canonical query + utoken ---------------------------------------
        Dictionary<string, string?> query = new() { ["launcherId"] = channel.LauncherId };

        string getPayload = AzurPromiliaApiClient.BuildGetSigningPayload(KnownTimestamp, query, string.Empty);
        Check("GET payload = timestamp + canonical query + utoken",
            getPayload == KnownTimestampText + KnownCanonicalQuery, getPayload);
        Check("GET payload digest matches the live-verified vector", Sign(getPayload) == KnownGetSign,
            Sign(getPayload));

        Dictionary<string, string?> unsorted = new() { ["uid"] = "42", ["launcherId"] = channel.LauncherId };
        string canonical = AzurPromiliaApiClient.BuildCanonicalQuery(unsorted);
        Check("GET canonical query sorts keys ordinally, not by insertion order",
            canonical == "launcherId=" + channel.LauncherId + "&uid=42", canonical);

        // --- POST: timestamp + the exact JSON body + utoken -----------------------------------
        string postPayload = AzurPromiliaApiClient.BuildPostSigningPayload(KnownTimestamp, KnownBody, string.Empty);
        Check("POST payload = timestamp + JSON body + utoken", postPayload == KnownTimestampText + KnownBody,
            postPayload);
        Check("POST payload digest matches the live-verified vector", Sign(postPayload) == KnownPostSign,
            Sign(postPayload));

        // --- Regression: a POST must not be signed over its (empty) query string ---------------
        string emptyQueryPayload =
            AzurPromiliaApiClient.BuildGetSigningPayload(KnownTimestamp, new Dictionary<string, string?>(),
                string.Empty);
        Check("POST payload differs from the empty-query payload", postPayload != emptyQueryPayload,
            emptyQueryPayload);
        Check("the empty-query payload reproduces the old failing digest",
            Sign(emptyQueryPayload) == KnownEmptyQuerySign, Sign(emptyQueryPayload));

        // --- The signed utoken must equal the utoken header, verbatim --------------------------
        string nullUtokenPayload =
            AzurPromiliaApiClient.BuildPostSigningPayload(KnownTimestamp, KnownBody, "null");
        Check("a signed 'null' utoken yields a different digest than an empty one",
            Sign(nullUtokenPayload) == KnownNullUtokenSign && Sign(nullUtokenPayload) != KnownPostSign,
            Sign(nullUtokenPayload));

        Dictionary<string, string> signedOutHeaders =
            AzurPromiliaDeviceInfo.BuildBaseHeaders(channel, "SC000", string.Empty, null);
        Check("signed-out utoken header is empty, matching the value that was signed",
            signedOutHeaders["utoken"] == string.Empty, "\"" + signedOutHeaders["utoken"] + "\"");
        Check("utoken header is present even when signed out", signedOutHeaders.ContainsKey("utoken"));
    }

    /// <summary>
    /// Opt-in end-to-end check against the live API, enabled with <c>--live</c>.
    /// <para>
    /// Unlike <see cref="RunSigningChecks"/> this drives the real <see cref="AzurPromiliaApiClient"/>, so
    /// it proves the shipped signing path is <i>accepted by the server</i> rather than merely that the
    /// digest formula matches a local vector. A deliberately non-existent account is used: the point is
    /// that the server gets far enough to reject the account. If the signature were still wrong the
    /// answer would be <c>code=2, MD5校验错误</c> instead.
    /// </para>
    /// </summary>
    private static void RunLiveChecks()
    {
        AzurPromiliaChannelConfig channel = AzurPromiliaChannelConfig.Default;
        using HttpClient httpClient = new();
        using AzurPromiliaApiClient api = new(channel, httpClient, "SC000", string.Empty);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        CancellationToken token = cts.Token;

        Dictionary<string, string?> query = new() { ["launcherId"] = channel.LauncherId };
        LauncherConfigData? config = api
            .GetAsync(api.BaseDomains, "/pc-launcher/client/launcher/config", query,
                AzurPromiliaApiContext.Default.AzurPromiliaResponseLauncherConfigData, token)
            .GetAwaiter().GetResult();

        Check("live: GET getLauncherConfig was accepted", config != null);
        if (config?.LauncherInitConfig == null)
        {
            return;
        }

        api.PasswordKey = config.LauncherInitConfig.GetPasswordKey();
        if (config.LauncherInitConfig.User?.Domains is { Count: > 0 } userDomains)
        {
            api.UserDomains = userDomains;
        }

        Check("live: server published the password-encryption key", !string.IsNullOrEmpty(api.PasswordKey),
            api.PasswordKey ?? "(null)");

        // A syntactically valid extApi4 output (always 108 characters) for an account that does not exist.
        string body = JsonSerializer.Serialize(new PasswordLoginRequest
        {
            AccountType = AzurPromiliaAccountType.Password,
            Account = "azurprobe0000@example.com",
            Password = new string('A', 108)
        }, AzurPromiliaApiContext.Default.PasswordLoginRequest);

        LoginResult result = api.PostLoginAsync("/user/pwdlogin", body, token).GetAwaiter().GetResult();

        Check("live: POST pwdlogin signature was accepted (not code=2)", result.Code != 2,
            $"code={result.Code}, msg={result.Msg}");
        Console.WriteLine($"      server answered: code={result.Code}, msg={result.Msg}");
    }

    private static int Main(string[] args)
    {
        Console.WriteLine("== signing rules ==");
        try
        {
            RunSigningChecks();
        }
        catch (Exception ex)
        {
            Check("unhandled exception in the signing checks", false, ex.ToString());
        }

        if (Array.IndexOf(args, "--live") >= 0)
        {
            Console.WriteLine();
            Console.WriteLine("== live API (opt-in) ==");
            try
            {
                RunLiveChecks();
            }
            catch (Exception ex)
            {
                Check("unhandled exception in the live checks", false, ex.ToString());
            }
        }

        Console.WriteLine();
        return RunAccountChecks();
    }

    private static int RunAccountChecks()
    {
        string root = Path.Combine(Path.GetTempPath(), "azurpromilia-account-test-" + Guid.NewGuid().ToString("N"));
        string gameDir = Path.Combine(root, "game");
        string gameAccount = Path.Combine(gameDir, AzurPromiliaAccount.FileName);

        // The legacy location this plugin deliberately no longer uses.
        string legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzurPromiliaPlugin");
        string legacyAccount = Path.Combine(legacyDir, AzurPromiliaAccount.FileName);

        // A decoy in the plugin folder, to prove that is ignored too.
        string pluginAccount = Path.Combine(AppContext.BaseDirectory, AzurPromiliaAccount.FileName);

        Directory.CreateDirectory(gameDir);

        bool hadLegacyDir = Directory.Exists(legacyDir);
        bool hadLegacy = File.Exists(legacyAccount);
        bool hadPlugin = File.Exists(pluginAccount);
        string? savedLegacy = hadLegacy ? File.ReadAllText(legacyAccount) : null;
        string? savedPlugin = hadPlugin ? File.ReadAllText(pluginAccount) : null;

        Console.WriteLine($"game folder   : {gameDir}");
        Console.WriteLine($"legacy folder : {legacyDir}   (must stay untouched)");
        Console.WriteLine();

        try
        {
            if (hadLegacy) File.Delete(legacyAccount);
            if (hadPlugin) File.Delete(pluginAccount);

            // --- 1. Nothing happens before the game directory is known ------------------------
            AzurPromiliaAccount early = AzurPromiliaAccount.Load();
            Check("load without a game directory creates no file", !File.Exists(gameAccount));
            Check("load without a game directory has no source path", early.SourcePath == null,
                early.SourcePath ?? "(null)");
            Check("load without a game directory leaves the legacy folder absent",
                !File.Exists(legacyAccount));

            // --- 2. The template lands in the game folder -------------------------------------
            AzurPromiliaAccount resolved = AzurPromiliaAccount.Load(gameDir);
            Check("template created in the game folder", File.Exists(gameAccount));
            Check("source path is the game-folder file",
                string.Equals(resolved.SourcePath, gameAccount, StringComparison.OrdinalIgnoreCase),
                resolved.SourcePath ?? "(null)");
            Check("legacy folder was not created", !File.Exists(legacyAccount));
            Check("nothing written next to the plugin assembly", !File.Exists(pluginAccount));

            // --- 3. Values are actually read back ---------------------------------------------
            File.WriteAllText(gameAccount, "{\"utoken\":\"TOKEN-FROM-GAME\",\"uid\":\"42\",\"accountType\":2}");
            AzurPromiliaAccount withToken = AzurPromiliaAccount.Load(gameDir);
            Check("utoken read from the game folder", withToken.UToken == "TOKEN-FROM-GAME", withToken.UToken);
            Check("uid read from the game folder", withToken.Uid == "42", withToken.Uid);
            Check("accountType read from the game folder", withToken.AccountType == 2,
                withToken.AccountType?.ToString());

            // --- 4. The legacy location is ignored even when populated -------------------------
            Directory.CreateDirectory(legacyDir);
            File.WriteAllText(legacyAccount, "{\"utoken\":\"TOKEN-FROM-LEGACY\"}");
            File.WriteAllText(pluginAccount, "{\"utoken\":\"TOKEN-FROM-PLUGIN-FOLDER\"}");

            AzurPromiliaAccount ignoring = AzurPromiliaAccount.Load(gameDir);
            Check("legacy %LOCALAPPDATA% file is ignored", ignoring.UToken == "TOKEN-FROM-GAME", ignoring.UToken);
            Check("plugin-folder file is ignored", ignoring.UToken == "TOKEN-FROM-GAME", ignoring.UToken);
            Check("legacy file left untouched", File.ReadAllText(legacyAccount).Contains("TOKEN-FROM-LEGACY"));

            // --- 5. In-place upgrade of the shared instance ------------------------------------
            AzurPromiliaAccount shared = AzurPromiliaAccount.Load();
            Check("shared instance starts unresolved", shared.SourcePath == null, shared.SourcePath ?? "(null)");
            Check("shared instance has no token yet", shared.UToken == null, shared.UToken ?? "(null)");

            bool upgraded = shared.TryLoadFrom(gameDir);
            Check("in-place reload reported the switch", upgraded);
            Check("shared instance now reads the game folder", shared.UToken == "TOKEN-FROM-GAME", shared.UToken);

            // --- 6. Re-resolving the same directory is a no-op ---------------------------------
            Check("repeat resolve is a no-op", !shared.TryLoadFrom(gameDir));
            Check("resolve without a game directory is a no-op", !shared.TryLoadFrom(null));

            // --- 7. Saves go to the game-folder file -------------------------------------------
            shared.SaveRefreshedIdentity("99", 2);
            Check("SaveRefreshedIdentity wrote to the game-folder file",
                File.ReadAllText(gameAccount).Contains("\"99\""));
            Check("SaveRefreshedIdentity did not touch the legacy file",
                File.ReadAllText(legacyAccount).Contains("TOKEN-FROM-LEGACY"));

            // --- 8. A deleted file is regenerated as a fresh template ---------------------------
            File.Delete(gameAccount);
            AzurPromiliaAccount regenerated = AzurPromiliaAccount.Load(gameDir);
            Check("deleted file is regenerated", File.Exists(gameAccount));
            Check("regenerated file is a template, not the old credentials", regenerated.UToken == null,
                regenerated.UToken ?? "(null)");
        }
        catch (Exception ex)
        {
            Check("unhandled exception", false, ex.ToString());
        }
        finally
        {
            try { if (File.Exists(gameAccount)) File.Delete(gameAccount); } catch { }
            try { if (File.Exists(pluginAccount)) File.Delete(pluginAccount); } catch { }
            if (savedPlugin != null) File.WriteAllText(pluginAccount, savedPlugin);

            try { if (File.Exists(legacyAccount)) File.Delete(legacyAccount); } catch { }
            if (savedLegacy != null) File.WriteAllText(legacyAccount, savedLegacy);
            // Only remove the legacy folder when this test created it in the first place.
            if (!hadLegacyDir)
            {
                try { if (Directory.Exists(legacyDir)) Directory.Delete(legacyDir); } catch { }
            }

            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "==> ALL CHECKS PASSED" : $"==> {_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
