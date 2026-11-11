using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Hi3Helper.Plugin.AzurPromilia.Management.Api;

public class AzurPromiliaResponse<T>
{
    [JsonPropertyName("code")] public int Code { get; set; }

    [JsonPropertyName("msg")] public string? Msg { get; set; }

    [JsonPropertyName("data")] public T? Data { get; set; }
}


#region /pc-launcher/client/gameversion/packageconfig + /gameversion/config
public class GameVersionConfigData
{
    [JsonPropertyName("released")] public GameReleaseInfo? Released { get; set; }

    [JsonPropertyName("preload")] public GameReleaseInfo? Preload { get; set; }
    [JsonPropertyName("downloadMode")] public int DownloadMode { get; set; } = 1;
}
public class GameReleaseInfo
{
    [JsonPropertyName("gameName")] public string? GameName { get; set; }

    [JsonPropertyName("gameFolderName")] public string? GameFolderName { get; set; }
    [JsonPropertyName("versionCode")] public string? VersionCode { get; set; }
    [JsonPropertyName("versionName")] public string? VersionName { get; set; }
    [JsonPropertyName("launchEntry")] public string? LaunchEntry { get; set; }

    [JsonPropertyName("packageSize")] public long PackageSize { get; set; }

    [JsonPropertyName("gamePackageList")] public List<GamePackageInfo>? GamePackageList { get; set; }

    [JsonPropertyName("gameExcludedFileList")] public List<GameExcludedFile>? GameExcludedFileList { get; set; }
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
public class GamePackageInfo
{
    [JsonPropertyName("packageType")] public int PackageType { get; set; }

    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }

    [JsonPropertyName("fileMd5")] public string? FileMd5 { get; set; }

    [JsonPropertyName("fileSize")] public long FileSize { get; set; }
}
public class GameExcludedFile
{
    [JsonPropertyName("filePath")] public string? FilePath { get; set; }
    [JsonPropertyName("fileType")] public int FileType { get; set; }
}

#endregion

#region /pc-launcher/client/gameversion/pre/check
public class GameExeInfoData
{
    [JsonPropertyName("launchEntry")] public string? LaunchEntry { get; set; }

    [JsonPropertyName("launchParams")] public string? LaunchParams { get; set; }
}

#endregion

#region /pc-launcher/client/launcher/contentconfig
public class NewsConfigData
{
    [JsonPropertyName("mediaContentMap")] public Dictionary<string, List<MediaContentEntry>>? MediaContentMap { get; set; }
    [JsonPropertyName("txtContentMap")] public Dictionary<string, List<MediaContentEntry>>? TxtContentMap { get; set; }
}
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



#region Local bookkeeping
public class AzurPromiliaLocalManifest
{
    [JsonPropertyName("versionCode")] public string? VersionCode { get; set; }

    [JsonPropertyName("versionName")] public string? VersionName { get; set; }

    [JsonPropertyName("downloadMode")] public int DownloadMode { get; set; }

    [JsonPropertyName("files")] public List<AzurPromiliaLocalManifestFile>? Files { get; set; }
}
public class AzurPromiliaLocalManifestFile
{
    [JsonPropertyName("path")] public required string Path { get; set; }

    [JsonPropertyName("size")] public long Size { get; set; }

    [JsonPropertyName("md5")] public string? Md5 { get; set; }
}

#endregion
