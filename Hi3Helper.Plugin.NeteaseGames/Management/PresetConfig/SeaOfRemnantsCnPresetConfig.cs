using System;
using System.Collections.Generic;
using System.IO;
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

namespace Hi3Helper.Plugin.NeteaseGames.Management.PresetConfig;

/// <summary>
///     The CN (国服) preset of 《遗忘之海》 / Sea of Remnants.
///     <para>
///         The preset is completely launcher independent: the installation is detected through the registry
///         key the official launcher writes, the remote manifests are fetched from NetEase directly and the
///         game is started without the official PCLauncher.
///     </para>
/// </summary>
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

    public override string GameExecutableName => "ywzh.exe";

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

    public override string GameLogFileName => "ywzh.log";

    public override string GameVendorName => "NetEase";

    public override string GameRegistryKeyName => NeteaseGamesPaths.RegistryKeyName;

    public override string ProfileName => "SeaOfRemnants";

    public override string ZoneDescription => "NetEase · CN";

    public override string ZoneName => "遗忘之海";

    public override string ZoneFullName => "遗忘之海 (Sea of Remnants)";

    public override string ZoneLogoUrl => "https://h74.update.netease.com/game_notice/image/095541248827.png";

    public override string ZonePosterUrl => "https://h74.update.netease.com/game_notice/image/270811864061.png";

    public override string ZoneHomePageUrl => "https://sea.163.com/";

    public override GameReleaseChannel ReleaseChannel => GameReleaseChannel.Public;

    public override string GameMainLanguage => "zh_CN";

    public override string LauncherGameDirectoryName => "ywzh_extreme";

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
        // Locate the installation (if the host did not set the path yet) and fetch the remote version.
        string? path = GetGamePath();
        if (string.IsNullOrEmpty(path))
        {
            string? existing = NeteaseGamesLocalState.FindExistingInstallPath();
            if (!string.IsNullOrEmpty(existing))
            {
                _gameManager.SetGamePath(existing);
            }
        }

        try
        {
            await _gameManager.ProbeRemoteVersionAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger?.LogWarning("[SeaOfRemnants] Failed to probe the remote version: {Msg}", ex.Message);
        }

        return 0;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _gameInstaller.Dispose();
        base.Dispose();
    }
}
