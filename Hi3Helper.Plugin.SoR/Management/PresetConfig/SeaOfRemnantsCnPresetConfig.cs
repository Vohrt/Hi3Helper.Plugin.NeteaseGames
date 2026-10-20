using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Hi3Helper.NeteaseGames.Core.Management.Api;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.SoR.Management.PresetConfig;

/// <summary>
///     The CN (国服) preset of 《遗忘之海》 / Sea of Remnants.
///     <para>
///         The preset is completely launcher independent: the installation is detected through the registry
///         key the official launcher writes, the remote manifests are fetched from NetEase directly and the
///         game is started without the official PCLauncher.
///     </para>
/// </summary>
[GeneratedComClass]
public partial class SeaOfRemnantsCnPresetConfig : PluginPresetConfigBase
{
    private readonly NeteaseGamesGameManager _gameManager = new();
    private readonly NeteaseGamesGameInstaller _gameInstaller;
    private readonly NeteaseGamesLauncherApiMedia _launcherApiMedia;
    private readonly NeteaseGamesLauncherApiNews _launcherApiNews;

    public SeaOfRemnantsCnPresetConfig()
    {
        _gameInstaller = new NeteaseGamesGameInstaller(_gameManager);
        _launcherApiMedia = new NeteaseGamesLauncherApiMedia(GetGamePath);
        _launcherApiNews = new NeteaseGamesLauncherApiNews(GetGamePath);
    }

    private string? GetGamePath()
    {
        _gameManager.GetGamePath(out string? path);
        return path;
    }

    #region Metadata

    public override string? GameName => "遗忘之海";

    public override string GameExecutableName => NeteaseGamesPaths.GameExecutableRelativePath;

    public override string GameAppDataPath
    {
        get
        {
            string? gamePath = GetGamePath();
            if (string.IsNullOrEmpty(gamePath))
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NetEaseGames",
                    "SeaOfRemnants");
            }

            return Path.Combine(gamePath, NeteaseGamesPaths.DocumentsRelativePath);
        }
    }

    public override string GameLogFileName => string.Empty;

    /// <summary>
    ///     The host composes the settings key as <c>Software\{vendor}\{game}</c>. Naming the vendor after the
    ///     official launcher key places the Collapse settings <b>below</b> it
    ///     (<c>Software\OB_ywzh_launcher_PC\Collapse</c>): the launcher keeps its own values in the parent key
    ///     untouched and it is obvious that the child key belongs to Collapse.
    /// </summary>
    public override string GameVendorName => NeteaseGamesPaths.CollapseSettingsVendorName;

    public override string GameRegistryKeyName => NeteaseGamesPaths.CollapseSettingsKeyName;

    public override string ProfileName => "SeaOfRemnants";

    public override string ZoneDescription => "NetEase · 中国大陆";

    public override string ZoneName => "中国大陆";

    public override string ZoneFullName => "遗忘之海 (中国大陆)";

    public override string ZoneLogoUrl => NeteaseGamesEndpoints.NoticeImageBaseUrl + "095541248827.png";

    public override string ZonePosterUrl => NeteaseGamesEndpoints.BackgroundFallbackUrl;

    public override string ZoneHomePageUrl => NeteaseGamesEndpoints.HomePageUrl;

    public override GameReleaseChannel ReleaseChannel => GameReleaseChannel.Public;

    public override string GameMainLanguage => "zh_CN";

    public override string LauncherGameDirectoryName => "ywzh_extreme";

    /// <summary>
    ///     The languages the client ships text for (the notice cache of the game carries all of them).
    /// </summary>
    public override List<string> SupportedLanguages =>
    [
        "zh_CN",
        "zh_TW",
        "en_US",
        "ja_JP",
        "ko_KR",
        "es_ES",
        "de_DE",
        "fr_FR"
    ];

    #endregion

    #region API instances

    // The base class nulls these slots when it is freed. The instances below are owned by this preset, so the
    // setter has to accept the null that the base hands over - silently ignoring it would leave freed objects
    // reachable through the property.
    public override ILauncherApiMedia? LauncherApiMedia
    {
        get => _launcherApiMedia;
        set { }
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => _launcherApiNews;
        set { }
    }

    public override IGameManager? GameManager
    {
        get => _gameManager;
        set { }
    }

    public override IGameInstaller? GameInstaller
    {
        get => _gameInstaller;
        set { }
    }

    #endregion

    /// <inheritdoc/>
    protected override async Task<int> InitAsync(CancellationToken token)
    {
        // Locate the installation (if the host did not set the path yet) so the version can be answered
        // synchronously right afterwards.
        string? path = GetGamePath();
        if (string.IsNullOrEmpty(path))
        {
            string? existing = NeteaseGamesLocalState.FindExistingInstallPath();
            if (!string.IsNullOrEmpty(existing))
            {
                _gameManager.SetGamePath(existing);
            }
        }

        // The manager bounds this call itself (the host awaits this hook while the launcher UI loads).
        if (!string.IsNullOrEmpty(GetGamePath()))
        {
            await _gameManager.InitAsyncInternal(token).ConfigureAwait(false);
        }

        return 0;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        // The base class frees the media / news / manager instances it can reach through its own slots; the
        // installer is not part of that list and is released here.
        _gameInstaller.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
