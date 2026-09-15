using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hi3Helper.Plugin.AzurPromilia.Management.Api;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia.Utils;

/// <summary>
/// User-supplied account configuration.
/// <para>
/// Azur Promilia requires a logged-in session before the server will hand out download URLs, and the
/// official login flow is a web flow guarded by Geetest / TCaptcha. A launcher plugin has no way to
/// host that UI, so this plugin deliberately does <b>not</b> try to drive the captcha flow. Instead the
/// user provides the credentials through a small JSON file which the plugin reads at startup.
/// </para>
/// <para>
/// The file lives in <b>the game install folder</b> — <c>&lt;game&gt;\account.json</c> — and nowhere
/// else. That location is deliberate: it sits right next to the game, where the user already browses,
/// instead of hiding under <c>%LOCALAPPDATA%</c>.
/// </para>
/// <para>
/// A template is written there the first time the launcher reports a game path. Nothing is written
/// before that, so the plugin never leaves a stray copy behind in a location the user cannot find.
/// </para>
/// <para>
/// Four mutually exclusive strategies are supported, evaluated in this order:
/// <list type="number">
/// <item><description><c>utoken</c> — paste an existing session token (simplest, and the only one that
/// never requires the account password).</description></item>
/// <item><description><c>importFromOfficialLauncher</c> — reuse the token the official launcher already
/// saved on this machine, decrypted locally.</description></item>
/// <item><description><c>account</c> + <c>password</c> — password login. The password is encrypted with
/// the recovered <c>extApi4</c> algorithm before being sent, and is never written to disk.</description></item>
/// <item><description><c>phone</c> + <c>smsCode</c> — SMS-code login.</description></item>
/// </list>
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AzurPromiliaAccount
{
    /// <summary>File name of the credential file.</summary>
    public const string FileName = "account.json";

    private const string DefaultReadme =
        "Azur Promilia plugin credentials. Fill in ONE of the strategies below, then restart the launcher.\n" +
        " 1) utoken                  : paste a session token you already have. Nothing else is needed.\n" +
        " 2) importFromOfficialLauncher: set to true to reuse the token saved by the official launcher on this PC.\n" +
        " 3) account + password      : phone number or e-mail plus password. Only used to obtain a token;\n" +
        "                              the password itself is never written back to this file.\n" +
        " 4) phone + smsCode         : SMS-code login.\n" +
        "Optional: launcherRoot points at the official launcher install directory so res/app_config.dat\n" +
        "can be re-read after a launcher update changes the channel keys.\n" +
        "This file lives in the game install folder. Delete it and restart the launcher to regenerate it.";

    /// <summary>Existing session token. Highest priority strategy.</summary>
    [JsonPropertyName("utoken")] public string? UToken { get; set; }

    /// <summary>Account id matching <see cref="UToken"/>. Refreshed automatically on login.</summary>
    [JsonPropertyName("uid")] public string? Uid { get; set; }

    /// <summary>Account type matching <see cref="UToken"/>. Defaults to <c>2</c> (password).</summary>
    [JsonPropertyName("accountType")] public int? AccountType { get; set; }

    /// <summary>Phone number or e-mail address for password login.</summary>
    [JsonPropertyName("account")] public string? Account { get; set; }

    /// <summary>Password for password login.</summary>
    [JsonPropertyName("password")] public string? Password { get; set; }

    /// <summary>Phone number for SMS-code login.</summary>
    [JsonPropertyName("phone")] public string? Phone { get; set; }

    /// <summary>SMS verification code for SMS-code login.</summary>
    [JsonPropertyName("smsCode")] public string? SmsCode { get; set; }

    /// <summary>
    /// When <c>true</c>, the token saved by the official launcher on this machine is reused.
    /// </summary>
    [JsonPropertyName("importFromOfficialLauncher")] public bool ImportFromOfficialLauncher { get; set; } = true;

    /// <summary>
    /// Optional path to the official launcher installation (the directory holding
    /// <c>res/app_config.dat</c>).
    /// </summary>
    [JsonPropertyName("launcherRoot")] public string? LauncherRoot { get; set; }

    /// <summary>Explanatory text written into the generated template.</summary>
    [JsonPropertyName("_readme")] public string? Readme { get; set; }

    /// <summary>Path this instance was loaded from.</summary>
    [JsonIgnore] public string? SourcePath { get; private set; }

    /// <summary>Whether any usable credential strategy is configured.</summary>
    [JsonIgnore]
    public bool HasAnyCredential =>
        !string.IsNullOrWhiteSpace(UToken) ||
        (!string.IsNullOrWhiteSpace(Account) && !string.IsNullOrWhiteSpace(Password)) ||
        (!string.IsNullOrWhiteSpace(Phone) && !string.IsNullOrWhiteSpace(SmsCode)) ||
        ImportFromOfficialLauncher;

    /// <summary>
    /// Path of the credential file inside a game install directory: <c>&lt;game&gt;\account.json</c>.
    /// </summary>
    public static string GetGameDirectoryPath(string gameDirectory)
        => Path.Combine(gameDirectory, FileName);

    /// <summary>
    /// Loads the credential file from the game folder, creating a template there when none exists.
    /// </summary>
    /// <param name="gameDirectory">
    /// The game install directory. Required: the credential file lives in the game folder and nowhere
    /// else. When this is <c>null</c> or empty, nothing is read and nothing is written.
    /// </param>
    public static AzurPromiliaAccount Load(string? gameDirectory = null)
    {
        AzurPromiliaAccount account = new();
        account.TryLoadFrom(gameDirectory);
        return account;
    }

    /// <summary>
    /// (Re)loads the credential file from the game folder and repopulates <b>this</b> instance in place.
    /// <para>
    /// Mutating in place is deliberate. The preset config, the game manager and the login service all
    /// share one instance, so resolving the source once the game path becomes known must not hand out a
    /// different object — otherwise a later <see cref="SaveRefreshedIdentity"/> would write back to the
    /// wrong file.
    /// </para>
    /// </summary>
    /// <param name="gameDirectory">
    /// The game install directory. Until the launcher reports one there is nowhere to put the file, so
    /// the call is a no-op. That is what keeps the plugin from scattering a stray copy into a location
    /// the user cannot find.
    /// </param>
    /// <returns><c>true</c> when the effective source path changed.</returns>
    public bool TryLoadFrom(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromilia] No game directory reported yet. {File} is only ever read from (and " +
                "written to) the game folder, so there is nothing to do.", FileName);
            return false;
        }

        string target = GetGameDirectoryPath(gameDirectory);

        if (File.Exists(target))
        {
            // Already using this exact file, so there is nothing to re-read.
            if (string.Equals(target, SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                string json = File.ReadAllText(target);
                AzurPromiliaAccount? loaded = JsonSerializer.Deserialize(
                    json, AccountJsonContext.Default.AzurPromiliaAccount);

                if (loaded == null)
                {
                    SharedStatic.InstanceLogger.LogError(
                        "[AzurPromilia] '{Path}' deserialised to null; keeping the current credentials.",
                        target);
                    return false;
                }

                ApplyFrom(loaded, target);
                SharedStatic.InstanceLogger.LogInformation(
                    "[AzurPromilia] Loaded account configuration from '{Path}'.", target);
                return true;
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogError(
                    "[AzurPromilia] Failed to parse '{Path}': {Error}", target, ex.Message);
                return false;
            }
        }

        // No file yet: drop the template next to the game so the user has something concrete to edit.
        WriteTemplate(target);

        if (string.Equals(target, SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        SourcePath = target;
        return true;
    }

    private void WriteTemplate(string target)
    {
        try
        {
            string? directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Readme = DefaultReadme;
            File.WriteAllText(target,
                JsonSerializer.Serialize(this, AccountJsonContext.Default.AzurPromiliaAccount),
                new UTF8Encoding(false));

            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromilia] No account configuration was found. A template has been written to '{Path}'. " +
                "Fill in 'utoken' (or 'account' + 'password'), then restart the launcher.", target);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(
                "[AzurPromilia] Unable to write the account template to '{Path}': {Error}", target, ex.Message);
        }
    }

    private void ApplyFrom(AzurPromiliaAccount source, string sourcePath)
    {
        UToken = source.UToken;
        Uid = source.Uid;
        AccountType = source.AccountType;
        Account = source.Account;
        Password = source.Password;
        Phone = source.Phone;
        SmsCode = source.SmsCode;
        ImportFromOfficialLauncher = source.ImportFromOfficialLauncher;
        LauncherRoot = source.LauncherRoot;
        Readme = source.Readme;
        SourcePath = sourcePath;
    }

    /// <summary>
    /// Persists the refreshed <c>uid</c> / <c>accountType</c> so later runs can skip rediscovery.
    /// The password field is deliberately cleared before writing.
    /// </summary>
    public void SaveRefreshedIdentity(string? uid, int accountType)
    {
        if (string.IsNullOrEmpty(SourcePath))
        {
            return;
        }

        Uid = uid;
        AccountType = accountType;

        try
        {
            // Never persist the plaintext password back to disk.
            string? password = Password;
            Password = null;

            try
            {
                File.WriteAllText(SourcePath,
                    JsonSerializer.Serialize(this, AccountJsonContext.Default.AzurPromiliaAccount),
                    new UTF8Encoding(false));
            }
            finally
            {
                Password = password;
            }
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromilia] Could not update '{Path}': {Error}", SourcePath, ex.Message);
        }
    }

    /// <summary>
    /// Reads the login token the official launcher stored on this machine.
    /// <para>
    /// The token lives in <c>data.json</c> (an electron-store file) as hex-encoded AES-256-CBC,
    /// where the key is the first 32 bytes and the IV the first 16 bytes of the UTF-8 encoded
    /// <c>device_id</c>. Since <c>device_id = md5(MachineGuid)</c>, the same key can be derived here.
    /// </para>
    /// </summary>
    /// <param name="channelConfig">Channel configuration used to locate the per-account store directory.</param>
    /// <param name="uToken">The recovered token.</param>
    /// <param name="accountType">The stored account type.</param>
    public static bool TryImportOfficialToken(AzurPromiliaChannelConfig channelConfig,
        out string? uToken,
        out int accountType)
    {
        uToken = null;
        accountType = AzurPromiliaAccountType.Password;

        string storeDirectory = channelConfig.GetOfficialStoreDirectory();
        string dataPath = Path.Combine(storeDirectory, "data.json");

        if (!File.Exists(dataPath))
        {
            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromilia] No official launcher credential store at '{Path}'; skipping import.",
                dataPath);
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(dataPath));
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("token", out JsonElement tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String)
            {
                SharedStatic.InstanceLogger.LogInformation(
                    "[AzurPromilia] '{Path}' has no saved token; the official launcher was never logged in.",
                    dataPath);
                return false;
            }

            string hexToken = tokenElement.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(hexToken))
            {
                return false;
            }

            if (root.TryGetProperty("accountType", out JsonElement typeElement) &&
                typeElement.ValueKind == JsonValueKind.Number)
            {
                accountType = typeElement.GetInt32();
            }

            byte[] encrypted = Convert.FromHexString(hexToken);
            byte[] deviceIdBytes = Encoding.UTF8.GetBytes(AzurPromiliaDeviceInfo.DeviceId);

            byte[] key = new byte[32];
            byte[] iv = new byte[16];
            Buffer.BlockCopy(deviceIdBytes, 0, key, 0, Math.Min(deviceIdBytes.Length, 32));
            Buffer.BlockCopy(deviceIdBytes, 0, iv, 0, Math.Min(deviceIdBytes.Length, 16));

            byte[] plain = AzurPromiliaCrypto.AesCbcDecrypt(key, iv, encrypted);
            uToken = Encoding.UTF8.GetString(plain);

            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromilia] Imported the login token saved by the official launcher (accountType={AccountType}).",
                accountType);
            return !string.IsNullOrEmpty(uToken);
        }
        catch (Exception ex)
        {
            // A different MachineGuid (or a corrupt store) makes the token undecryptable; the user
            // simply has to log in through the official launcher again or supply a token directly.
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromilia] Could not import the official launcher token from '{Path}': {Error}. " +
                "Provide 'utoken' in the account file instead.", dataPath, ex.Message);
            return false;
        }
    }
}

/// <summary>
/// Source-generated JSON context for the credential file.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AzurPromiliaAccount))]
public partial class AccountJsonContext : JsonSerializerContext;
