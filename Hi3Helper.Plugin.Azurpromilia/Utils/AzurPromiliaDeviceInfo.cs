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

[SupportedOSPlatform("windows")]
public static class AzurPromiliaDeviceInfo
{
    private const string LauncherAppVersion = "1.0.2.2";
    private const string DefaultCountry = "CN";

    private static readonly Lazy<string> MachineId = new(ComputeMachineId);
    private static readonly Lazy<string> OperatingSystemName = new(ReadOperatingSystemName);
    private static readonly Lazy<string> OperatingSystemVersion = new(ReadOperatingSystemVersion);
    private static readonly Lazy<string> NetworkType = new(DetectNetworkType);
    public static string DeviceId => MachineId.Value;
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

    public static string OperatingSystemNameValue => OperatingSystemName.Value;
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

    public static string UserAgent { get; } =
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        $"AzurPromilia/{LauncherAppVersion} Chrome/120.0.0.0 Safari/537.36";
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
            ["lang"] = channelConfig.ApiLanguage,
            ["grp-client-info"] = BuildClientInfoJson(traceId),
            ["utoken"] = uToken ?? string.Empty
        };
    }

}
