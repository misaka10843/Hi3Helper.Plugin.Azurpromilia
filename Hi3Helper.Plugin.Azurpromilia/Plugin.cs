using System;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.AzurPromilia.Management.PresetConfig;
using Hi3Helper.Plugin.AzurPromilia.Utils;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia;

[GeneratedComClass]
public partial class AzurPromiliaPlugin : PluginBase
{
    private static readonly IPluginPresetConfig[] PresetConfigInstances =
    [
        new AzurPromiliaPresetConfig(),
        new AzurPromiliaPresetConfig(AzurPromiliaChannelConfig.Bilibili, "AzurPromiliaBilibili", "Bilibili",
            "蓝色星原：旅谣 (哔哩哔哩)", "zh-CN", "Chinese", "Azur Promilia Bilibili"),
        new AzurPromiliaPresetConfig(AzurPromiliaChannelConfig.Global, "AzurPromiliaGlobal", "Global",
            "Azur Promilia (Global)", "en-US", "English", "Azur Promilia Global"),
        new AzurPromiliaPresetConfig(AzurPromiliaChannelConfig.Default, "AzurPromiliaJapan", "Japan",
            "アズールプロミリア (日本)", "ja-JP", "Japanese", "Azur Promilia Japan",
            new Yostar.YostarLauncherOptions("AZUPRO_JP", "https://api-launcher-jp.yo-star.com",
                "AZUPRO_JP", "AzurPromiliaJP.exe"))
    ];

    private static DateTime PluginCreationDate = new(2026, 09, 15, 00, 00, 0, DateTimeKind.Utc);

    public override void GetPluginName(out string result)
        => result = "Azur Promilia Plugin";

    public override void GetPluginDescription(out string result)
        => result = "Adds support for Azur Promilia (蓝色星原：旅谣) to Collapse Launcher.";

    public override void GetPluginAuthor(out string result)
        => result = "Hi3Helper.Plugin.Azurpromilia contributors";

    public override unsafe void GetPluginCreationDate(out DateTime* result)
    {
        fixed (DateTime* ptr = &PluginCreationDate)
        {
            result = ptr;
        }
    }

    public override void GetPresetConfigCount(out int count)
        => count = PresetConfigInstances.Length;

    public override void GetPresetConfig(int index, out IPluginPresetConfig presetConfig)
    {
        if (index < 0 || index >= PresetConfigInstances.Length)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromilia] Invalid preset config index requested: {Index}", index);
            presetConfig = null!;
            return;
        }

        SharedStatic.InstanceLogger.LogInformation(
            "[AzurPromilia] Loading preset config at index {Index}", index);
        presetConfig = PresetConfigInstances[index];
    }

    public override void GetPluginSelfUpdater(out IPluginSelfUpdate selfUpdate)
        => selfUpdate = new SelfUpdate();

    public override void GetPluginAppIconUrl(out string result)
        => result = Convert.ToBase64String(AzurPromiliaImageData.AzurPromiliaAppIconData);

    public override void GetNotificationPosterUrl(out string result)
        => result = Convert.ToBase64String(AzurPromiliaImageData.AzurPromiliaPosterData);
}
