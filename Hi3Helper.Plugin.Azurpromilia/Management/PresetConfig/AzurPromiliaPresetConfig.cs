using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.AzurPromilia.Management.Api;
using Hi3Helper.Plugin.AzurPromilia.Utils;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.AzurPromilia.Yostar;
using Hi3Helper.Plugin.AzurPromilia.Yostar.Api;
using Hi3Helper.Plugin.AzurPromilia.Yostar.Management;

namespace Hi3Helper.Plugin.AzurPromilia.Management.PresetConfig;

[GeneratedComClass]
public partial class AzurPromiliaPresetConfig : PluginPresetConfigBase
{
    private const string FallbackExecutableName = "AzurPromilia.exe";

    private const string ZoneHomePage = "https://www.azurpromilia.com/";

    private readonly AzurPromiliaChannelConfig _fallback;
    private readonly YostarLauncherOptions? _yostarOptions;

    public AzurPromiliaPresetConfig()
        : this(AzurPromiliaChannelConfig.Default, "AzurPromiliaCn", "Mainland China",
            "蓝色星原：旅谣 (中国大陆)", "zh-CN", "Chinese", "Azur Promilia")
    {
    }

    internal AzurPromiliaPresetConfig(AzurPromiliaChannelConfig channel, string profile, string zone,
        string fullName, string language, string supportedLanguage, string directory, YostarLauncherOptions? yostar = null)
    {
        _fallback = channel;
        _yostarOptions = yostar;
        ProfileName = profile;
        ZoneName = zone;
        ZoneFullName = fullName;
        GameMainLanguage = language;
        SupportedLanguages = [supportedLanguage];
        LauncherGameDirectoryName = directory;
    }

    private AzurPromiliaChannelConfig? _channelConfig;
    internal AzurPromiliaChannelConfig ChannelConfig =>
        _channelConfig ??= AzurPromiliaChannelConfig.ResolveForChannel(_fallback);

    [field: AllowNull] [field: MaybeNull] public override string GameName => field ??= "Azur Promilia";
    public override string GameExecutableName
    {
        get
        {
            if (GameManager is AzurPromiliaGameManager manager &&
                !string.IsNullOrEmpty(manager.LaunchEntry))
            {
                return manager.LaunchEntry!;
            }

            if (GameManager is YostarGameManager yostar)
            {
                return yostar.ExecutableName;
            }

            return FallbackExecutableName;
        }
    }

    public override string GameAppDataPath
    {
        get
        {
            string? gamePath = null;
            GameManager?.GetGamePath(out gamePath);
            return string.IsNullOrEmpty(gamePath) ? string.Empty : gamePath!;
        }
    }
    [field: AllowNull] [field: MaybeNull] public override string GameLogFileName => field ??= "Player.log";

    [field: AllowNull] [field: MaybeNull] public override string GameVendorName => field ??= "Manjuu";

    [field: AllowNull] [field: MaybeNull] public override string GameRegistryKeyName => field ??= "AzurPromilia";

    public override string ProfileName { get; }

    [field: AllowNull]
    [field: MaybeNull]
    public override string ZoneDescription =>
        field ??= "《蓝色星原：旅谣》是一款由蛮啾网络开发的开放世界动作角色扮演游戏。";

    public override string ZoneName { get; }

    public override string ZoneFullName { get; }

    [field: AllowNull] [field: MaybeNull] public override string ZoneLogoUrl => field ??= string.Empty;

    [field: AllowNull] [field: MaybeNull] public override string ZonePosterUrl => field ??= string.Empty;

    [field: AllowNull] [field: MaybeNull] public override string ZoneHomePageUrl => field ??= ZoneHomePage;

    public override GameReleaseChannel ReleaseChannel => GameReleaseChannel.Public;

    public override string GameMainLanguage { get; }

    public override string LauncherGameDirectoryName { get; }

    public override List<string> SupportedLanguages { get; }

    public override ILauncherApiMedia? LauncherApiMedia
    {
        get => field ??= _yostarOptions is { } options
            ? new YostarLauncherApiMedia(options)
            : new AzurPromiliaLauncherApiMedia(this);
        set;
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => field ??= _yostarOptions is { } options
            ? new YostarLauncherApiNews(options)
            : new AzurPromiliaLauncherApiNews(this);
        set;
    }

    public override IGameManager? GameManager
    {
        get => field ??= _yostarOptions is { } options
            ? new YostarGameManager(options)
            : new AzurPromiliaGameManager(FallbackExecutableName, ChannelConfig);
        set;
    }

    public override IGameInstaller? GameInstaller
    {
        get => field ??= _yostarOptions != null
            ? new YostarGameInstaller(GameManager)
            : new AzurPromiliaGameInstaller(GameManager!);
        set;
    }

    protected override Task<int> InitAsync(CancellationToken token) => Task.FromResult(0);
}
