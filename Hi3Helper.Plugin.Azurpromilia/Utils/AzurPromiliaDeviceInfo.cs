using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Hi3Helper.Plugin.AzurPromilia.Utils;

/// <summary>
/// Reproduces the device fingerprint and request headers the official launcher sends.
/// <para>
/// The single most important value here is <c>device_id</c> = <c>md5(MachineGuid)</c>: it is used both as
/// the <c>device-id</c> header and as the key/IV material that protects the launcher's saved login token.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AzurPromiliaDeviceInfo
{
    private const string LauncherAppVersion = "0.6.2.2";
    private const string DefaultCountry = "CN";

    private static readonly Lazy<string> MachineId = new(ComputeMachineId);
    private static readonly Lazy<string> OperatingSystemName = new(ReadOperatingSystemName);
    private static readonly Lazy<string> OperatingSystemVersion = new(ReadOperatingSystemVersion);
    private static readonly Lazy<string> NetworkType = new(DetectNetworkType);

    /// <summary>
    /// <c>md5(MachineGuid)</c>, a 32 character lowercase hex string.
    /// </summary>
    public static string DeviceId => MachineId.Value;

    /// <summary>
    /// Reads <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c> and hashes it.
    /// Falls back to a deterministic hash of the machine name when the value is unavailable.
    /// </summary>
    private static string ComputeMachineId()
    {
        string? machineGuid = null;

        try
        {
            using RegistryKey? baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            machineGuid = key?.GetValue("MachineGuid") as string;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromilia] Unable to read MachineGuid from the registry: {Error}", ex.Message);
        }

        if (string.IsNullOrEmpty(machineGuid))
        {
            machineGuid = Environment.MachineName;
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromilia] MachineGuid unavailable, falling back to the machine name for the device id.");
        }

        return AzurPromiliaCrypto.Md5Hex(machineGuid);
    }

    /// <summary>
    /// The launcher-visible operating system name, e.g. <c>Windows 11 Pro Version 23H2</c>.
    /// </summary>
    public static string OperatingSystemNameValue => OperatingSystemName.Value;

    /// <summary>
    /// The launcher-visible operating system build, e.g. <c>10.0.22631.4460</c>.
    /// </summary>
    public static string OperatingSystemVersionValue => OperatingSystemVersion.Value;

    private static string ReadOperatingSystemName()
    {
        try
        {
            using RegistryKey? baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            string? productName = key?.GetValue("ProductName") as string;
            string? displayVersion = key?.GetValue("DisplayVersion") as string
                                     ?? key?.GetValue("ReleaseId") as string;

            if (!string.IsNullOrEmpty(productName))
            {
                return string.IsNullOrEmpty(displayVersion)
                    ? productName
                    : $"{productName} Version {displayVersion}";
            }
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogDebug(
                "[AzurPromilia] Unable to read the OS product name: {Error}", ex.Message);
        }

        return $"Windows {Environment.OSVersion.Version}";
    }

    private static string ReadOperatingSystemVersion()
    {
        Version version = Environment.OSVersion.Version;
        string build = version.Build.ToString(CultureInfo.InvariantCulture);

        try
        {
            using RegistryKey? baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            if (key?.GetValue("UBR") is int ubr)
            {
                build = $"{version.Build}.{ubr}";
            }
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogDebug(
                "[AzurPromilia] Unable to read the OS revision (UBR): {Error}", ex.Message);
        }

        return $"{version.Major}.{version.Minor}.{build}";
    }

    /// <summary>
    /// The device language with any script subtag stripped, e.g. <c>zh-Hans</c> becomes <c>zh</c>.
    /// </summary>
    public static string DeviceLanguage
    {
        get
        {
            string language = CultureInfo.CurrentUICulture.Name;
            if (string.IsNullOrEmpty(language))
            {
                return "zh";
            }

            int separator = language.IndexOf('-');
            return separator > 0 ? language[..separator] : language;
        }
    }

    /// <summary>
    /// The device region, e.g. <c>CN</c>.
    /// </summary>
    public static string DeviceCountry
    {
        get
        {
            try
            {
                string region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
                if (!string.IsNullOrEmpty(region))
                {
                    return region;
                }
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogDebug(
                    "[AzurPromilia] Unable to resolve the region: {Error}", ex.Message);
            }

            return DefaultCountry;
        }
    }

    /// <summary>
    /// The current UTC offset rendered as <c>UTC+08:00</c>, matching the launcher's formatting.
    /// </summary>
    public static string DeviceTimezone
    {
        get
        {
            TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
            char sign = offset < TimeSpan.Zero ? '-' : '+';
            TimeSpan absolute = offset.Duration();

            return $"UTC{sign}{absolute.Hours:00}:{absolute.Minutes:00}";
        }
    }

    /// <summary>
    /// <c>WiFi</c> when a wireless adapter is up, otherwise <c>Ethernet</c>.
    /// </summary>
    public static string NetworkTypeValue => NetworkType.Value;

    private static string DetectNetworkType()
    {
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    return "WiFi";
                }
            }
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogDebug(
                "[AzurPromilia] Network type detection failed: {Error}", ex.Message);
        }

        return "Ethernet";
    }

    /// <summary>
    /// The user agent advertised by the launcher's requests.
    /// </summary>
    public static string UserAgent { get; } =
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        $"AzurPromilia/{LauncherAppVersion} Chrome/120.0.0.0 Safari/537.36";

    /// <summary>
    /// Serialises the <c>grp-client-info</c> header value.
    /// </summary>
    /// <param name="traceId">Current trace id, taken from <c>delyinfo.json</c> when present.</param>
    public static string BuildClientInfoJson(string traceId = "")
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("UA", UserAgent);
            writer.WriteString("device_model", "Launcher");
            writer.WriteString("device_os", OperatingSystemNameValue);
            writer.WriteString("device_os_version", OperatingSystemVersionValue);
            writer.WriteString("device_network", NetworkTypeValue);
            writer.WriteString("device_language", DeviceLanguage);
            writer.WriteString("device_timezone", DeviceTimezone);
            writer.WriteString("device_country", DeviceCountry);
            writer.WriteString("app_version", LauncherAppVersion);
            writer.WriteString("grp_trace_id", traceId);
            writer.WriteStartObject("spec_device_info");
            writer.WriteEndObject();
            writer.WriteString("device_uuid", string.Empty);
            writer.WriteString("sub_channel_trace_id", string.Empty);
            writer.WriteString("init_trace_id", traceId);
            writer.WriteString("previous_trace_id", string.Empty);
            writer.WriteString("current_trace_id", traceId);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Reads the launcher's <c>delyinfo.json</c> attribution file if present, returning its
    /// <c>sub_channel_code</c> and <c>current_trace_id</c>.
    /// </summary>
    /// <param name="launcherAppDataPath">
    /// The launcher's <c>AppData</c> directory (the one containing <c>delyinfo.json</c>).
    /// </param>
    public static (string SubChannelCode, string TraceId) TryReadDeliveryInfo(string? launcherAppDataPath)
    {
        const string defaultSubChannel = "SC000";

        if (string.IsNullOrEmpty(launcherAppDataPath))
        {
            return (defaultSubChannel, string.Empty);
        }

        string path = Path.Combine(launcherAppDataPath, "delyinfo.json");
        if (!File.Exists(path))
        {
            return (defaultSubChannel, string.Empty);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;

            string subChannel = root.TryGetProperty("sub_channel_code", out JsonElement subElement) &&
                                subElement.ValueKind == JsonValueKind.String
                ? subElement.GetString() ?? defaultSubChannel
                : defaultSubChannel;

            string traceId = root.TryGetProperty("current_trace_id", out JsonElement traceElement) &&
                             traceElement.ValueKind == JsonValueKind.String
                ? traceElement.GetString() ?? string.Empty
                : string.Empty;

            return (subChannel, traceId);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromilia] Failed to read delyinfo.json at '{Path}': {Error}", path, ex.Message);
            return (defaultSubChannel, string.Empty);
        }
    }

    /// <summary>
    /// Builds the static (non-signed) portion of the request headers. The per-request
    /// <c>timestamp</c> / <c>sign</c> headers are added by <see cref="AzurPromiliaApiHelper"/>.
    /// </summary>
    /// <param name="channelConfig">Channel configuration supplying the app/channel codes.</param>
    /// <param name="subChannelCode">Attribution sub-channel code.</param>
    /// <param name="traceId">Current trace id.</param>
    /// <param name="uToken">Login token, or <c>null</c> while signed out.</param>
    public static Dictionary<string, string> BuildBaseHeaders(AzurPromiliaChannelConfig channelConfig,
        string subChannelCode, string traceId, string? uToken)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["device-id"] = DeviceId,
            ["grp-trace-id"] = traceId,
            ["grp-app-id"] = channelConfig.AppId,
            ["grp-platform"] = "Launcher",
            ["grp-channel-code"] = channelConfig.ChannelCode,
            ["grp-sub-channel-code"] = subChannelCode,
            ["sdk-version"] = LauncherAppVersion,
            ["channel-sdk-version"] = LauncherAppVersion,
            ["grp-launcher-version-code"] = LauncherAppVersion,
            ["lang"] = "zh-Hans",
            ["grp-client-info"] = BuildClientInfoJson(traceId),
            ["utoken"] = uToken ?? string.Empty
        };
    }

    /// <summary>
    /// The launcher version string reported through <c>sdk-version</c> and friends.
    /// </summary>
    public static string AppVersion => LauncherAppVersion;
}
