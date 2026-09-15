using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.AzurPromilia.Management;
using Hi3Helper.Plugin.AzurPromilia.Management.PresetConfig;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia;

/// <summary>
/// Game launch implementation.
/// <para>
/// <b>Critical:</b> the Azur Promilia client reads its login state exclusively from the
/// <c>manjuu_user_info</c> and <c>manjuu_channel_info</c> environment variables — the command line
/// carries no account information. Both variables must be set on the child process or the game starts
/// signed out.
/// </para>
/// </summary>
public partial class Exports
{
    private const string EnvUserInfo = "manjuu_user_info";
    private const string EnvChannelInfo = "manjuu_channel_info";

    protected override (bool IsSupported, Task<bool> Task) LaunchGameFromGameManagerCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context,
        string? startArgument,
        bool isRunBoosted,
        ProcessPriorityClass processPriority,
        CancellationToken token)
    {
        InstanceLogger.LogInformation("[AzurPromilia::LaunchGame] Preparing game launch...");
        return (true, Impl());

        async Task<bool> Impl()
        {
            if (!TryGetStartingProcessFromContext(context, startArgument, out Process? process))
            {
                InstanceLogger.LogError(
                    "[AzurPromilia::LaunchGame] Could not resolve the game executable. " +
                    "Make sure the game is installed and the launch entry is known.");
                return false;
            }

            using (process)
            {
                try
                {
                    process.Start();
                    InstanceLogger.LogInformation(
                        "[AzurPromilia::LaunchGame] Game started with PID {Pid}.", process.Id);
                }
                catch (Exception ex)
                {
                    InstanceLogger.LogError(ex, "[AzurPromilia::LaunchGame] Failed to start the game process.");
                    return false;
                }

                try
                {
                    process.PriorityBoostEnabled = isRunBoosted;
                    process.PriorityClass = processPriority;
                }
                catch (Exception ex)
                {
                    InstanceLogger.LogWarning(
                        "[AzurPromilia::LaunchGame] Could not adjust process priority: {Error}", ex.Message);
                }

                _ = ReadGameLog(context, token);

                try
                {
                    await process.WaitForExitAsync(token).ConfigureAwait(false);
                    InstanceLogger.LogInformation("[AzurPromilia::LaunchGame] Game process exited.");
                    return true;
                }
                catch (OperationCanceledException)
                {
                    // Cancelling the await must not kill the game; the launcher keeps it running.
                    InstanceLogger.LogInformation(
                        "[AzurPromilia::LaunchGame] Await cancelled; the game keeps running.");
                    return true;
                }
            }
        }
    }

    protected override bool IsGameRunningCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool isGameRunning,
        out DateTime gameStartTime)
    {
        isGameRunning = false;
        gameStartTime = default;

        if (!TryGetGameExecutablePath(context, out string? executablePath))
        {
            return true;
        }

        using Process? process = FindExecutableProcess(executablePath);
        if (process != null)
        {
            isGameRunning = true;

            try
            {
                gameStartTime = process.StartTime;
            }
            catch (Exception ex)
            {
                InstanceLogger.LogWarning(
                    "[AzurPromilia::IsGameRunning] Could not read the process start time: {Error}", ex.Message);
            }
        }

        return true;
    }

    protected override (bool IsSupported, Task<bool> Task) WaitRunningGameCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context,
        CancellationToken token)
    {
        return (true, Impl());

        async Task<bool> Impl()
        {
            if (!TryGetGameExecutablePath(context, out string? executablePath))
            {
                return true;
            }

            using Process? process = FindExecutableProcess(executablePath);
            if (process == null)
            {
                return true;
            }

            InstanceLogger.LogInformation("[AzurPromilia::WaitGame] Waiting for the game to exit...");

            try
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ignored: the launcher only stops waiting.
            }

            return true;
        }
    }

    protected override bool KillRunningGameCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool wasGameRunning,
        out DateTime gameStartTime)
    {
        wasGameRunning = false;
        gameStartTime = default;

        if (!TryGetGameExecutablePath(context, out string? executablePath))
        {
            return true;
        }

        using Process? process = FindExecutableProcess(executablePath);
        if (process == null)
        {
            return true;
        }

        wasGameRunning = true;

        try
        {
            gameStartTime = process.StartTime;
        }
        catch
        {
            // Non-fatal.
        }

        process.Kill();
        InstanceLogger.LogInformation(
            "[AzurPromilia::KillGame] Process {Pid} was terminated.", process.Id);

        return true;
    }

    #region Process helpers

    private static Process? FindExecutableProcess(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return null;
        }

        ReadOnlySpan<char> directoryPath = Path.GetDirectoryName(executablePath.AsSpan());
        string executableName = Path.GetFileNameWithoutExtension(executablePath);

        Process[] processes = Process.GetProcessesByName(executableName);
        Process? match = null;

        foreach (Process process in processes)
        {
            try
            {
                string? modulePath = process.MainModule?.FileName;
                if (modulePath != null &&
                    modulePath.AsSpan().StartsWith(directoryPath, StringComparison.OrdinalIgnoreCase))
                {
                    match = process;
                    break;
                }
            }
            catch (Exception)
            {
                // A process owned by another user cannot be inspected; skip it.
            }
        }

        foreach (Process process in processes.Where(p => p != match))
        {
            process.Dispose();
        }

        return match;
    }

    private static bool TryGetGameExecutablePath(GameManagerExtension.RunGameFromGameManagerContext context,
        [NotNullWhen(true)] out string? executablePath)
    {
        executablePath = null;

        if (context is not
            {
                GameManager: AzurPromiliaGameManager manager,
                PresetConfig: PluginPresetConfigBase presetConfig
            })
        {
            return false;
        }

        manager.GetGamePath(out string? gamePath);
        presetConfig.comGet_GameExecutableName(out string executableName);

        if (string.IsNullOrEmpty(gamePath) || string.IsNullOrEmpty(executableName))
        {
            return false;
        }

        string candidate = Path.Combine(gamePath!, executableName);

        if (!File.Exists(candidate))
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromilia::LaunchGame] Executable not found at '{Path}'.", candidate);
            return false;
        }

        executablePath = candidate;
        return true;
    }

    private static bool TryGetStartingProcessFromContext(
        GameManagerExtension.RunGameFromGameManagerContext context,
        string? startArgument,
        [NotNullWhen(true)] out Process? process)
    {
        process = null;

        if (!TryGetGameExecutablePath(context, out string? executablePath))
        {
            return false;
        }

        List<string> arguments = new();

        // The server's launch parameters come first, then anything the launcher appended.
        if (context.GameManager is AzurPromiliaGameManager manager &&
            !string.IsNullOrWhiteSpace(manager.LaunchParams))
        {
            arguments.AddRange(manager.LaunchParams!
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (!string.IsNullOrWhiteSpace(startArgument))
        {
            arguments.AddRange(startArgument!
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        ProcessStartInfo startInfo = new(executablePath!)
        {
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The client obtains its session solely from these two variables.
        InjectLoginEnvironment(context, startInfo);

        process = new Process { StartInfo = startInfo };
        return true;
    }

    /// <summary>
    /// Populates <c>manjuu_user_info</c> and <c>manjuu_channel_info</c> on the child process.
    /// </summary>
    private static void InjectLoginEnvironment(GameManagerExtension.RunGameFromGameManagerContext context,
        ProcessStartInfo startInfo)
    {
        string userInfo = "{}";
        string channelInfo = "{}";

        try
        {
            if (context.GameManager is AzurPromiliaGameManager manager)
            {
                if (manager.Session != null)
                {
                    userInfo = BuildUserInfoJson(manager.Session.Uid, manager.Session.UToken,
                        manager.Session.AccountType);
                }
                else
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[AzurPromilia::LaunchGame] No session is available; the game will start signed out. " +
                        "Provide credentials through the plugin's account.json file to enable login.");
                }

                if (context.PresetConfig is AzurPromiliaCnPresetConfig presetConfig)
                {
                    channelInfo = BuildChannelInfoJson(presetConfig.ChannelConfig);
                }
            }
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(
                "[AzurPromilia::LaunchGame] Failed to build the login environment: {Error}", ex);
        }

        startInfo.Environment[EnvUserInfo] = userInfo;
        startInfo.Environment[EnvChannelInfo] = channelInfo;

        InstanceLogger.LogInformation(
            "[AzurPromilia::LaunchGame] Injected {UserVar} and {ChannelVar}.",
            EnvUserInfo, EnvChannelInfo);
    }

    private static string BuildUserInfoJson(string? uid, string? uToken, int accountType)
    {
        using MemoryStream stream = new();
        using (System.Text.Json.Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("uid", uid ?? string.Empty);
            writer.WriteString("utoken", uToken ?? string.Empty);
            writer.WriteNumber("accountType", accountType);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildChannelInfoJson(Utils.AzurPromiliaChannelConfig channelConfig)
    {
        (string subChannelCode, string traceId) = Utils.AzurPromiliaDeviceInfo.TryReadDeliveryInfo(null);

        using MemoryStream stream = new();
        using (System.Text.Json.Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("channelCode", channelConfig.ChannelCode);
            writer.WriteString("subChannelCode", subChannelCode);
            writer.WriteString("initTraceId", traceId);
            writer.WriteString("currentTraceId", traceId);
            writer.WriteString("previousTraceId", string.Empty);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    #endregion

    /// <summary>
    /// Tails the game's log file and forwards each line to the launcher's log console.
    /// </summary>
    private static async Task ReadGameLog(GameManagerExtension.RunGameFromGameManagerContext context,
        CancellationToken token)
    {
        if (context is not { PresetConfig: PluginPresetConfigBase presetConfig })
        {
            return;
        }

        presetConfig.comGet_GameAppDataPath(out string gameAppDataPath);
        presetConfig.comGet_GameLogFileName(out string logFileName);

        if (string.IsNullOrEmpty(gameAppDataPath) || string.IsNullOrEmpty(logFileName))
        {
            return;
        }

        string logPath = Path.Combine(gameAppDataPath, logFileName);

        int retries = 5;
        while (!File.Exists(logPath) && retries > 0)
        {
            try
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            --retries;
        }

        if (!File.Exists(logPath))
        {
            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromilia::ReadGameLog] No game log at '{Path}'; skipping log streaming.", logPath);
            return;
        }

        InstanceLogger.LogInformation("[AzurPromilia::ReadGameLog] Streaming '{Path}'.", logPath);

        try
        {
            await using FileStream fileStream = File.Open(logPath, FileMode.OpenOrCreate, FileAccess.Read,
                FileShare.ReadWrite);
            using StreamReader reader = new(fileStream);

            fileStream.Position = 0;

            while (!token.IsCancellationRequested)
            {
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    PassStringLineToCallback(context.PrintGameLogCallback, line);
                }

                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the game exits or the launcher stops reading.
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromilia::ReadGameLog] Stopped reading the log: {Error}", ex.Message);
        }
    }

    private static unsafe void PassStringLineToCallback(GameManagerExtension.PrintGameLog? callback, string line)
    {
        if (callback == null)
        {
            return;
        }

        char* linePointer = line.GetPinnableStringPointer();
        callback(linePointer, line.Length, 0);
    }
}
