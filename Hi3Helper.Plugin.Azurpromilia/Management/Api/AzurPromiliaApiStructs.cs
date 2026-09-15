using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Hi3Helper.Plugin.AzurPromilia.Management.Api;

/// <summary>
/// Standard envelope returned by every <c>/pc-launcher/**</c> business endpoint.
/// <para><c>code == 0</c> means success; anything else carries an i18n key or a server message in <c>msg</c>.</para>
/// </summary>
public class AzurPromiliaResponse<T>
{
    [JsonPropertyName("code")] public int Code { get; set; }

    [JsonPropertyName("msg")] public string? Msg { get; set; }

    [JsonPropertyName("data")] public T? Data { get; set; }
}

#region /pc-launcher/client/launcher/config

/// <summary>Payload of <c>getLauncherConfig</c>.</summary>
public class LauncherConfigData
{
    [JsonPropertyName("launcherInitConfig")]
    public LauncherInitConfig? LauncherInitConfig { get; set; }
}

/// <summary>Server-published launcher configuration: domains and the password-encryption key.</summary>
public class LauncherInitConfig
{
    /// <summary>
    /// Key/value bag. The password-encryption key lives at <c>encrypt.password.pwd</c> and is fed
    /// into <c>extApi4</c> when encrypting a password for <c>/user/pwdlogin</c>.
    /// </summary>
    [JsonPropertyName("common")] public Dictionary<string, string>? Common { get; set; }

    [JsonPropertyName("user")] public DomainNode? User { get; set; }

    [JsonPropertyName("compliance")] public DomainNode? Compliance { get; set; }

    /// <summary>Reads <c>common["encrypt.password.pwd"]</c>.</summary>
    public string? GetPasswordKey()
    {
        if (Common == null)
        {
            return null;
        }

        return Common.TryGetValue("encrypt.password.pwd", out string? key) ? key : null;
    }
}

/// <summary>A node carrying a list of failover domains.</summary>
public class DomainNode
{
    [JsonPropertyName("domains")] public List<string>? Domains { get; set; }
}

#endregion

#region /pc-launcher/client/gameversion/packageconfig + /gameversion/config

/// <summary>Payload of <c>getGameVersionConfig</c> / <c>getGameInfo</c>.</summary>
public class GameVersionConfigData
{
    [JsonPropertyName("released")] public GameReleaseInfo? Released { get; set; }

    [JsonPropertyName("preload")] public GameReleaseInfo? Preload { get; set; }

    /// <summary>
    /// <c>1</c> = SLICE (index + slices, merged into a zip then extracted),
    /// <c>2</c> = REALITY (per-file download, no merge or extraction).
    /// </summary>
    [JsonPropertyName("downloadMode")] public int DownloadMode { get; set; } = 1;
}

/// <summary>One published game version.</summary>
public class GameReleaseInfo
{
    [JsonPropertyName("gameName")] public string? GameName { get; set; }

    [JsonPropertyName("gameFolderName")] public string? GameFolderName { get; set; }

    /// <summary>Server-side version code; compared against the locally installed version.</summary>
    [JsonPropertyName("versionCode")] public string? VersionCode { get; set; }

    /// <summary>Human readable version name, used for display.</summary>
    [JsonPropertyName("versionName")] public string? VersionName { get; set; }

    /// <summary>Relative path of the executable that launches the game.</summary>
    [JsonPropertyName("launchEntry")] public string? LaunchEntry { get; set; }

    [JsonPropertyName("packageSize")] public long PackageSize { get; set; }

    [JsonPropertyName("gamePackageList")] public List<GamePackageInfo>? GamePackageList { get; set; }

    [JsonPropertyName("gameExcludedFileList")] public List<GameExcludedFile>? GameExcludedFileList { get; set; }

    /// <summary>
    /// Returns the package descriptor of the requested type
    /// (<c>1</c> = SLICE index, <c>2</c> = REALITY index).
    /// </summary>
    public GamePackageInfo? GetPackage(int packageType)
    {
        if (GamePackageList == null)
        {
            return null;
        }

        foreach (GamePackageInfo package in GamePackageList)
        {
            if (package.PackageType == packageType)
            {
                return package;
            }
        }

        return null;
    }
}

/// <summary>A downloadable package descriptor; the URL points at the resource <i>index</i> file.</summary>
public class GamePackageInfo
{
    /// <summary><c>1</c> = SLICE (fragmented), <c>2</c> = REALITY (full).</summary>
    [JsonPropertyName("packageType")] public int PackageType { get; set; }

    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }

    [JsonPropertyName("fileMd5")] public string? FileMd5 { get; set; }

    [JsonPropertyName("fileSize")] public long FileSize { get; set; }
}

/// <summary>A file or directory that must survive an update untouched.</summary>
public class GameExcludedFile
{
    [JsonPropertyName("filePath")] public string? FilePath { get; set; }

    /// <summary><c>1</c> = directory, anything else = file.</summary>
    [JsonPropertyName("fileType")] public int FileType { get; set; }
}

#endregion

#region /pc-launcher/client/gameversion/pre/check

/// <summary>Payload of <c>getGameExeInfo</c>: the authoritative launch entry and arguments.</summary>
public class GameExeInfoData
{
    [JsonPropertyName("launchEntry")] public string? LaunchEntry { get; set; }

    [JsonPropertyName("launchParams")] public string? LaunchParams { get; set; }
}

#endregion

#region /pc-launcher/client/launcher/contentconfig

/// <summary>Payload of <c>getNews</c>.</summary>
public class NewsConfigData
{
    /// <summary>
    /// Media keyed by content id: <c>"1"</c> = background image, <c>"3"</c> = logo, <c>"12"</c> = home video.
    /// </summary>
    [JsonPropertyName("mediaContentMap")] public Dictionary<string, List<MediaContentEntry>>? MediaContentMap { get; set; }

    /// <summary>News/announcement rows keyed by content id.</summary>
    [JsonPropertyName("txtContentMap")] public Dictionary<string, List<MediaContentEntry>>? TxtContentMap { get; set; }
}

/// <summary>
/// A single content entry. The same shape is reused across media and text maps, so the model is
/// intentionally permissive: unknown properties are ignored and missing ones stay <c>null</c>.
/// </summary>
public class MediaContentEntry
{
    [JsonPropertyName("image")] public string? Image { get; set; }

    [JsonPropertyName("video")] public string? Video { get; set; }

    [JsonPropertyName("title")] public string? Title { get; set; }

    [JsonPropertyName("content")] public string? Content { get; set; }

    [JsonPropertyName("link")] public string? Link { get; set; }

    [JsonPropertyName("url")] public string? Url { get; set; }

    [JsonPropertyName("jumpUrl")] public string? JumpUrl { get; set; }

    [JsonPropertyName("startTime")] public string? StartTime { get; set; }

    [JsonPropertyName("publishTime")] public string? PublishTime { get; set; }
}

#endregion

#region /passportv2/client/**

/// <summary>
/// Login result. The renderer's request helper unwraps <c>data</c>, but to stay resilient against
/// either shape both the nested and the flattened forms are modelled here.
/// </summary>
public class LoginResult
{
    [JsonPropertyName("code")] public int Code { get; set; } = 200;

    [JsonPropertyName("msg")] public string? Msg { get; set; }

    [JsonPropertyName("data")] public LoginUserInfo? Data { get; set; }

    // Flattened fallbacks, in case the endpoint returns the user object at the root.

    [JsonPropertyName("utoken")] public string? RootUToken { get; set; }

    [JsonPropertyName("uid")] public string? RootUid { get; set; }

    [JsonPropertyName("accountType")] public int? RootAccountType { get; set; }

    [JsonPropertyName("mail")] public string? RootMail { get; set; }

    [JsonPropertyName("phone")] public string? RootPhone { get; set; }

    /// <summary>Returns the effective user info regardless of which envelope shape was used.</summary>
    public LoginUserInfo? GetUserInfo()
    {
        if (Data != null)
        {
            return Data;
        }

        if (string.IsNullOrEmpty(RootUToken))
        {
            return null;
        }

        return new LoginUserInfo
        {
            UToken = RootUToken,
            Uid = RootUid,
            AccountType = RootAccountType ?? 0,
            Mail = RootMail,
            Phone = RootPhone
        };
    }
}

/// <summary>The logged-in user record returned by the passport endpoints.</summary>
public class LoginUserInfo
{
    [JsonPropertyName("utoken")] public string? UToken { get; set; }

    [JsonPropertyName("uid")] public string? Uid { get; set; }

    [JsonPropertyName("accountType")] public int AccountType { get; set; }

    [JsonPropertyName("mail")] public string? Mail { get; set; }

    [JsonPropertyName("phone")] public string? Phone { get; set; }

    /// <summary><c>1</c> while the account sits in the cancellation cool-down period.</summary>
    [JsonPropertyName("unsubState")] public int UnsubState { get; set; }

    /// <summary><c>2</c> marks a banned account.</summary>
    [JsonPropertyName("userState")] public int UserState { get; set; }
}

/// <summary>Body of <c>POST /passportv2/client/user/tokenlogin</c>.</summary>
public class TokenLoginRequest
{
    [JsonPropertyName("accountType")] public int AccountType { get; set; }
}

/// <summary>Body of <c>POST /passportv2/client/user/pwdlogin</c>.</summary>
public class PasswordLoginRequest
{
    /// <summary><c>2</c> for password login (see <see cref="AzurPromiliaAccountType"/>).</summary>
    [JsonPropertyName("accountType")] public int AccountType { get; set; } = AzurPromiliaAccountType.Password;

    /// <summary>Phone number or e-mail address.</summary>
    [JsonPropertyName("account")] public string? Account { get; set; }

    /// <summary>Password already encrypted with <c>extApi4</c>.</summary>
    [JsonPropertyName("pwd")] public string? Password { get; set; }
}

/// <summary>Body of <c>POST /passportv2/client/user/phonelogin</c>.</summary>
public class PhoneLoginRequest
{
    /// <summary><c>0</c> for SMS-code login.</summary>
    [JsonPropertyName("accountType")] public int AccountType { get; set; } = AzurPromiliaAccountType.Phone;

    [JsonPropertyName("phone")] public string? Phone { get; set; }

    [JsonPropertyName("code")] public string? Code { get; set; }
}

#endregion

#region Local bookkeeping

/// <summary>
/// Manifest the plugin maintains inside the game directory so incremental updates and integrity
/// checks have a local baseline to diff against.
/// </summary>
public class AzurPromiliaLocalManifest
{
    [JsonPropertyName("versionCode")] public string? VersionCode { get; set; }

    [JsonPropertyName("versionName")] public string? VersionName { get; set; }

    [JsonPropertyName("downloadMode")] public int DownloadMode { get; set; }

    [JsonPropertyName("files")] public List<AzurPromiliaLocalManifestFile>? Files { get; set; }
}

/// <summary>One file recorded in the local manifest.</summary>
public class AzurPromiliaLocalManifestFile
{
    /// <summary>Path relative to the game root.</summary>
    [JsonPropertyName("path")] public required string Path { get; set; }

    [JsonPropertyName("size")] public long Size { get; set; }

    [JsonPropertyName("md5")] public string? Md5 { get; set; }
}

#endregion

/// <summary>
/// Account type discriminator used by the passport endpoints.
/// Values recovered from the renderer bundle (<c>{Phone:0, Email:1, Password:2, Guest:3, TapTap:4}</c>).
/// </summary>
public static class AzurPromiliaAccountType
{
    public const int Phone = 0;
    public const int Email = 1;
    public const int Password = 2;
    public const int Guest = 3;
    public const int TapTap = 4;
}
