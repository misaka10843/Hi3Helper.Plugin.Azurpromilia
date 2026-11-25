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

namespace Hi3Helper.Plugin.AzurPromilia.Management.PresetConfig;

[GeneratedComClass]
public partial class AzurPromiliaCnPresetConfig : PluginPresetConfigBase
{
    private const string FallbackExecutableName = "AzurPromilia.exe";

    private const string ZoneHomePage = "https://www.azurpromilia.com/";

    private AzurPromiliaChannelConfig? _channelConfig;
    internal AzurPromiliaChannelConfig ChannelConfig =>
        _channelConfig ??= AzurPromiliaChannelConfig.Resolve();

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

    [field: AllowNull] [field: MaybeNull] public override string ProfileName => field ??= "AzurPromiliaCn";

    [field: AllowNull]
    [field: MaybeNull]
    public override string ZoneDescription =>
        field ??= "《蓝色星原：旅谣》是一款由蛮啾网络开发的开放世界动作角色扮演游戏。";

    [field: AllowNull] [field: MaybeNull] public override string ZoneName => field ??= "Mainland China";

    [field: AllowNull] [field: MaybeNull] public override string ZoneFullName => field ??= "蓝色星原：旅谣 (中国大陆)";

    [field: AllowNull] [field: MaybeNull] public override string ZoneLogoUrl => field ??= string.Empty;

    [field: AllowNull] [field: MaybeNull] public override string ZonePosterUrl => field ??= string.Empty;

    [field: AllowNull] [field: MaybeNull] public override string ZoneHomePageUrl => field ??= ZoneHomePage;

    public override GameReleaseChannel ReleaseChannel => GameReleaseChannel.Public;

    [field: AllowNull] [field: MaybeNull] public override string GameMainLanguage => field ??= "zh-CN";

    [field: AllowNull]
    [field: MaybeNull]
    public override string LauncherGameDirectoryName => field ??= "Azur Promilia";

    [field: AllowNull] [field: MaybeNull] public override List<string> SupportedLanguages => field ??= ["Chinese"];

    public override ILauncherApiMedia? LauncherApiMedia
    {
        get => field ??= new AzurPromiliaLauncherApiMedia(this);
        set;
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => field ??= new AzurPromiliaLauncherApiNews(this);
        set;
    }

    public override IGameManager? GameManager
    {
        get => field ??= new AzurPromiliaGameManager(FallbackExecutableName, ChannelConfig);
        set;
    }

    public override IGameInstaller? GameInstaller
    {
        get => field ??= new AzurPromiliaGameInstaller(GameManager!);
        set;
    }

    protected override Task<int> InitAsync(CancellationToken token) => Task.FromResult(0);
}
