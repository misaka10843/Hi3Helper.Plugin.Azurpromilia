using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Hi3Helper.Plugin.AzurPromilia.Utils;

/// <summary>
/// The channel/signing context published by the official launcher in <c>res/app_config.dat</c>.
/// <para>
/// These five values drive every signed request: <c>launcherId</c> is a mandatory query parameter,
/// while <c>appId</c> / <c>channelCode</c> / <c>secretKey</c> are the tail of the signature string.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AzurPromiliaChannelConfig
{
    /// <summary>Mandatory <c>launcherId</c> query parameter for every business API call.</summary>
    public required string LauncherId { get; init; }

    /// <summary>Game id, used for registry keys and per-account store directory naming.</summary>
    public required string GameId { get; init; }

    /// <summary>Application id, part of the signature and used in log-upload object keys.</summary>
    public required string AppId { get; init; }

    /// <summary>Channel code, part of the signature.</summary>
    public required string ChannelCode { get; init; }

    /// <summary>Signature secret, the final component of the signed payload.</summary>
    public required string SecretKey { get; init; }

    /// <summary>Attribution sub-channel, read from <c>delyinfo.json</c>. Defaults to <c>SC000</c>.</summary>
    public string SubChannelCode { get; init; } = "SC000";

    /// <summary>
    /// The values recovered from the retail CN launcher (<c>0.6.2.2</c>), used when
    /// <c>res/app_config.dat</c> cannot be found on disk.
    /// </summary>
    public static AzurPromiliaChannelConfig Default { get; } = new()
    {
        LauncherId = "a35deadb9f8846c9ad69bc2f4e299b8c",
        GameId = "35",
        AppId = "354",
        ChannelCode = "C000",
        SecretKey = "20bccb31-fb57-4683-b6d8-6314d43811f2"
    };

    /// <summary>Registry key name the official launcher registers its uninstall entry under.</summary>
    public string UninstallRegistryKeyName => $"AzurPromilia_{GameId}_{ChannelCode}";

    /// <summary>Full registry path of the official launcher's uninstall entry.</summary>
    public string UninstallRegistryPath =>
        $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallRegistryKeyName}";

    /// <summary>
    /// Attempts to read and decrypt <c>&lt;launcherRoot&gt;/res/app_config.dat</c>.
    /// </summary>
    /// <param name="launcherRoot">The official launcher installation root, or <c>null</c>.</param>
    /// <param name="config">The parsed configuration on success.</param>
    /// <param name="error">A human readable reason when the read fails.</param>
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

            config = Parse(json);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to read {configPath}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Parses the decrypted <c>app_config.dat</c> JSON payload.
    /// </summary>
    public static AzurPromiliaChannelConfig Parse(string json)
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
            SecretKey = Read("secretKey", Default.SecretKey)
        };
    }

    /// <summary>
    /// Resolves the effective channel configuration: an explicit launcher root wins, then the
    /// auto-detected launcher, then the compiled-in defaults.
    /// </summary>
    /// <param name="launcherRoot">
    /// Optional explicit path to the official launcher installation (the directory that contains
    /// <c>res/app_config.dat</c>).
    /// </param>
    public static AzurPromiliaChannelConfig Resolve(string? launcherRoot = null)
    {
        // NOTE: the out variables are declared in the branches rather than inline in the condition.
        // Combining `&&` with an inline `out` declaration leaves the variable "possibly unassigned"
        // in the `else` branch as far as definite-assignment analysis is concerned (CS0165), because
        // the compiler cannot prove the second operand ran.
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
            "[AzurPromilia] Falling back to the built-in channel configuration. " +
            "If the official launcher updates its channel keys, point 'launcherRoot' in the plugin " +
            "account file at the launcher install directory so app_config.dat can be re-read.");

        return Default;
    }

    /// <summary>
    /// Tries to locate the official launcher installation through the uninstall registry entry,
    /// then through a set of conventional install locations.
    /// </summary>
    public static string? DetectLauncherRoot()
    {
        foreach (string candidate in EnumerateLauncherRootCandidates())
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

    private static IEnumerable<string> EnumerateLauncherRootCandidates()
    {
        // 1) Uninstall registry entry written by the official launcher.
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? installLocation = null;
            try
            {
                using RegistryKey? baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? subKey = baseKey.OpenSubKey(Default.UninstallRegistryPath);
                installLocation = subKey?.GetValue("InstallLocation") as string
                                  ?? subKey?.GetValue("DisplayIcon") as string;
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogDebug(
                    "[AzurPromilia] Registry probe failed ({View}): {Error}", view, ex.Message);
            }

            if (!string.IsNullOrEmpty(installLocation))
            {
                // DisplayIcon may point at the executable rather than the directory.
                if (File.Exists(installLocation))
                {
                    installLocation = Path.GetDirectoryName(installLocation);
                }

                yield return installLocation!;
            }
        }

        // 2) Conventional install locations.
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

    /// <summary>
    /// Returns the per-account store directory used by the official launcher
    /// (<c>%APPDATA%/&lt;product&gt;/Launcher_&lt;gameId&gt;_&lt;channelCode&gt;_&lt;subChannelCode&gt;</c>).
    /// </summary>
    public string GetOfficialStoreDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "AzurPromilia",
            $"Launcher_{GameId}_{ChannelCode}_{SubChannelCode}");
    }
}
