using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Hi3Helper.Plugin.AzurPromilia.Utils;

[SupportedOSPlatform("windows")]
public sealed class AzurPromiliaChannelConfig
{
    public required string LauncherId { get; init; }
    public required string GameId { get; init; }
    public required string AppId { get; init; }
    public required string ChannelCode { get; init; }
    public required string SecretKey { get; init; }
    public string SubChannelCode { get; init; } = "SC000";
    public string TraceId { get; init; } = "";
    public static AzurPromiliaChannelConfig Default { get; } = new()
    {
        LauncherId = "93cdd43e096046138053253dc56d3562",
        GameId = "35",
        AppId = "354",
        ChannelCode = "C000",
        SecretKey = "20bccb31-fb57-4683-b6d8-6314d43811f2"
    };
    internal static AzurPromiliaChannelConfig Bilibili { get; } = new()
    {
        LauncherId = "741e8ecd27934b5bb8c1e77ca148d4a8",
        GameId = Default.GameId,
        AppId = Default.AppId,
        ChannelCode = "C001",
        SecretKey = Default.SecretKey
    };

    internal static AzurPromiliaChannelConfig Global { get; } = new()
    {
        LauncherId = "1a3ee4447516436a9ec4b5d1ec1635e2",
        GameId = "43",
        AppId = "434",
        ChannelCode = "C500",
        SecretKey = "13236ff1-87c6-4331-9175-7b7bcaba7942"
    };

    internal string ApiLanguage
    {
        get
        {
            if (ChannelCode != "C500")
            {
                return "zh-Hans";
            }

            string locale = SharedStatic.PluginLocaleCode;
            return locale.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
                   locale.StartsWith("zh-", StringComparison.OrdinalIgnoreCase) ||
                   locale.StartsWith("zh_", StringComparison.OrdinalIgnoreCase)
                ? "zh-Hant"
                : "en";
        }
    }

    internal IReadOnlyList<string> ApiDomains => ChannelCode == "C500"
        ? ["https://api-grp-rgn1.jimi-global.com", "https://l2-api-grp-rgn1.jimi-global.com"]
        : ["https://api-grp.manjuu.com", "https://api-grp-l2.manjuu.com"];

    internal static AzurPromiliaChannelConfig ResolveForChannel(AzurPromiliaChannelConfig fallback)
    {
        foreach (string root in EnumerateLauncherRootCandidates(fallback))
        {
            if (TryLoadFromLauncherRoot(root, out AzurPromiliaChannelConfig? detected, out _) &&
                detected!.GameId == fallback.GameId && detected.AppId == fallback.AppId &&
                detected.ChannelCode == fallback.ChannelCode)
            {
                return detected;
            }
        }

        return fallback;
    }

    public string UninstallRegistryKeyName => $"AzurPromilia_{GameId}_{ChannelCode}";
    public string UninstallRegistryPath =>
        $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallRegistryKeyName}";
    internal string GameRegistryPath => $@"Software\AzurPromilia\{GameId}_{ChannelCode}";
    public static bool TryLoadFromLauncherRoot(string? launcherRoot,
        out AzurPromiliaChannelConfig? config,
        out string? error)
    {
        config = null;
        error = null;

        if (string.IsNullOrEmpty(launcherRoot))
        {
            error = "No launcher root directory was provided.";
            return false;
        }

        string configPath = Path.Combine(launcherRoot, "res", "app_config.dat");
        if (!File.Exists(configPath))
        {
            error = $"Configuration file not found: {configPath}";
            return false;
        }

        try
        {
            string json = AzurPromiliaCrypto.DecryptAppConfig(configPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                error = $"Decryption of {configPath} produced an empty payload.";
                return false;
            }

            string appData = Path.Combine(launcherRoot, "AppData");
            if (!Directory.Exists(appData) && Directory.GetParent(launcherRoot) is { } parent)
            {
                appData = Path.Combine(parent.FullName, "AppData");
            }

            (string subChannel, string traceId) = AzurPromiliaDeviceInfo.TryReadDeliveryInfo(appData);
            config = Parse(json, subChannel, traceId);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to read {configPath}: {ex.Message}";
            return false;
        }
    }

    public static AzurPromiliaChannelConfig Parse(string json, string subChannelCode = "SC000", string traceId = "")
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string Read(string name, string fallback)
            => root.TryGetProperty(name, out JsonElement element) &&
               element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? fallback
                : fallback;

        return new AzurPromiliaChannelConfig
        {
            LauncherId = Read("launcherId", Default.LauncherId),
            GameId = Read("gameId", Default.GameId),
            AppId = Read("appId", Default.AppId),
            ChannelCode = Read("channelCode", Default.ChannelCode),
            SecretKey = Read("secretKey", Default.SecretKey),
            SubChannelCode = subChannelCode,
            TraceId = traceId
        };
    }

    public static AzurPromiliaChannelConfig Resolve(string? launcherRoot = null)
    {
        if (!string.IsNullOrEmpty(launcherRoot))
        {
            if (TryLoadFromLauncherRoot(launcherRoot, out AzurPromiliaChannelConfig? explicitConfig,
                    out string? explicitError))
            {
                SharedStatic.InstanceLogger.LogInformation(
                    "[AzurPromilia] Channel config loaded from explicit launcher root '{Root}'. launcherId={LauncherId}",
                    launcherRoot, explicitConfig!.LauncherId);
                return explicitConfig;
            }

            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromilia] Could not use the configured launcher root '{Root}': {Error}",
                launcherRoot, explicitError);
        }

        string? detected = DetectLauncherRoot();
        if (!string.IsNullOrEmpty(detected))
        {
            if (TryLoadFromLauncherRoot(detected, out AzurPromiliaChannelConfig? detectedConfig,
                    out string? detectedError))
            {
                SharedStatic.InstanceLogger.LogInformation(
                    "[AzurPromilia] Channel config auto-detected at '{Root}'. launcherId={LauncherId}",
                    detected, detectedConfig!.LauncherId);
                return detectedConfig;
            }

            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromilia] Auto-detected launcher root '{Root}' was unusable: {Error}", detected,
                detectedError);
        }

        SharedStatic.InstanceLogger.LogWarning(
            "[AzurPromilia] Falling back to the built-in channel configuration.");

        return Default;
    }

    public static string? DetectLauncherRoot()
    {
        foreach (string candidate in EnumerateLauncherRootCandidates(Default))
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            if (File.Exists(Path.Combine(candidate, "res", "app_config.dat")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateLauncherRootCandidates(AzurPromiliaChannelConfig channel)
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? installLocation = null;
            string? displayIcon = null;
            try
            {
                using RegistryKey? baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? subKey = baseKey.OpenSubKey(channel.UninstallRegistryPath);
                installLocation = subKey?.GetValue("InstallLocation") as string;
                displayIcon = subKey?.GetValue("DisplayIcon") as string;
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogDebug(
                    "[AzurPromilia] Registry probe failed ({View}): {Error}", view, ex.Message);
            }

            if (!string.IsNullOrEmpty(displayIcon) && File.Exists(displayIcon.Trim('"')))
            {
                yield return Path.GetDirectoryName(displayIcon.Trim('"'))!;
            }

            if (!string.IsNullOrEmpty(installLocation))
            {
                yield return installLocation;
            }
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (string root in new[] { programFiles, programFilesX86, localAppData })
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            foreach (string folder in new[] { "AzurPromilia", "Azur Promilia", "蓝色星原", "AzurPromiliaLauncher" })
            {
                yield return Path.Combine(root, folder);
            }
        }
    }

}
