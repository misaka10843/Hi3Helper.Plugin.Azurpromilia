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

/// <summary>
/// Preset configuration for the mainland-China release of Azur Promilia (蓝色星原：旅谣).
/// <para>
/// Only the CN channel exists for this title: the launcher constants pin the region to <c>CN</c> and
/// every endpoint lives under <c>manjuu.com</c>.
/// </para>
/// </summary>
[GeneratedComClass]
public partial class AzurPromiliaCnPresetConfig : PluginPresetConfigBase
{
    /// <summary>
    /// Fallback executable name used before the API has reported the authoritative launch entry.
    /// <see cref="GameExecutableName"/> prefers the server-provided value once it is known.
    /// </summary>
    private const string FallbackExecutableName = "AzurPromilia.exe";

    private const string ZoneHomePage = "https://www.azurpromilia.com/";

    private AzurPromiliaAccount? _account;
    private AzurPromiliaChannelConfig? _channelConfig;

    /// <summary>
    /// User-supplied credentials, loaded once from <c>account.json</c>.
    /// <para>
    /// Loaded here without a game directory, so no file is created at this point. The game manager
    /// re-runs the lookup in place once <c>SetGamePath</c> has told it where the game lives, which is
    /// what puts the file in the game folder.
    /// </para>
    /// </summary>
    internal AzurPromiliaAccount Account => _account ??= AzurPromiliaAccount.Load();

    /// <summary>
    /// Channel configuration, resolved from the official launcher's <c>app_config.dat</c> when
    /// available and otherwise from the built-in values.
    /// <para>
    /// This is a first pass: the game manager re-resolves it if <c>account.json</c> later turns out to
    /// carry a different <c>launcherRoot</c>.
    /// </para>
    /// </summary>
    internal AzurPromiliaChannelConfig ChannelConfig =>
        _channelConfig ??= AzurPromiliaChannelConfig.Resolve(Account.LauncherRoot);

    [field: AllowNull] [field: MaybeNull] public override string GameName => field ??= "Azur Promilia";

    /// <summary>
    /// The game's launch entry. The API is authoritative, so once <c>getGameExeInfo</c> has run this
    /// reports the server-provided value and only falls back to a placeholder before that.
    /// </summary>
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

    /// <summary>
    /// Directory holding the game's user data. Azur Promilia keeps its save data under
    /// <c>_index</c>-adjacent folders inside the install root, so the install root is reported here.
    /// </summary>
    public override string GameAppDataPath
    {
        get
        {
            string? gamePath = null;
            GameManager?.GetGamePath(out gamePath);
            return string.IsNullOrEmpty(gamePath) ? string.Empty : gamePath!;
        }
    }

    /// <summary>
    /// Log file written by the game client. The CN client emits <c>Player.log</c> next to the
    /// executable; the plugin streams it to the launcher's console when the file exists.
    /// </summary>
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
        get => field ??= new AzurPromiliaGameManager(FallbackExecutableName, ChannelConfig, Account);
        set;
    }

    public override IGameInstaller? GameInstaller
    {
        get => field ??= new AzurPromiliaGameInstaller(GameManager!);
        set;
    }

    protected override Task<int> InitAsync(CancellationToken token) => Task.FromResult(0);
}
