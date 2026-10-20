using System;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     The NetEase network endpoints and build-time constants that the plugin needs in order to work
///     <b>without the official PCLauncher being installed</b>.
///     <para>
///         All of these values were recovered by decrypting the launcher's <c>LCFG</c> container
///         (<c>PCLauncher\config\&lt;md5&gt;</c>) and by observing a running launcher. The plugin ships them as
///         constants so that a fresh install works out of the box.
///     </para>
///     <para>
///         ⚠ These are <b>server side</b> names: when NetEase changes a host or a branch prefix, the plugin has
///         to be updated. The failure mode is explicit (the manifest fetch fails and the plugin reports that no
///         remote version is known), never a silent wrong answer.
///     </para>
/// </summary>
public static class NeteaseGamesEndpoints
{
    /// <summary>
    ///     The NetEase project/channel id of the CN build.
    /// </summary>
    public const string Project = "h74";

    /// <summary>
    ///     The base URL of the patch / mini-pack manifests (<c>patch_list_url</c> in <c>config.xml</c>).
    /// </summary>
    public const string PatchListBaseUrl = "https://h74.update.netease.com/pl/";

    /// <summary>
    ///     The content host that serves the game payload (<c>gph_url</c> in <c>config.xml</c>).
    /// </summary>
    public const string ContentHostUrl = "https://h74.gph.netease.com/";

    /// <summary>
    ///     The base URL of the notice images.
    /// </summary>
    public const string NoticeImageBaseUrl = "https://h74.update.netease.com/game_notice/image/";

    /// <summary>
    ///     The official home page of the game; also used as the click target of the carousel.
    /// </summary>
    public const string HomePageUrl = "https://sea.163.com/";

    /// <summary>
    ///     A large key visual that is used when the installation has no cached artwork yet.
    /// </summary>
    public const string BackgroundFallbackUrl = "https://h74.update.netease.com/game_notice/image/270811864061.png";

    /// <summary>
    ///     The prefix that the official launcher puts in front of the branch based manifests
    ///     (<c>launcher_prefix</c> / <c>minipack_prefix</c> / <c>notice_prefix</c> in <c>config.xml</c>).
    ///     <para>
    ///         Without it the server answers with the stale "release" branch, with it the current one is
    ///         returned (e.g. <c>OB_release_mini_pack_version.json</c>).
    ///     </para>
    /// </summary>
    public const string ManifestPrefix = "OB";

    /// <summary>
    ///     The branch of the manifests (<c>minipack_branch</c> in <c>config.xml</c>).
    /// </summary>
    public const string ManifestBranch = "release";

    /// <summary>
    ///     Name of the remote mini-pack manifest.
    /// </summary>
    public const string MiniPackListName = ManifestPrefix + "_" + ManifestBranch + "_mini_pack_version.json";

    /// <summary>
    ///     Name of the remote notice manifest.
    /// </summary>
    public const string NoticeListName = ManifestPrefix + "_" + ManifestBranch + "_launcher_notice_info.xml";

    /// <summary>
    ///     Name of the remote patch manifest (plain text, it carries no <c>HEX_</c> envelope).
    /// </summary>
    public const string PatchMd5ListName = ManifestPrefix + "_patchmd5_windows64_normal.txt";

    /// <summary>
    ///     Builds the URL of the remote mini-pack manifest.
    /// </summary>
    /// <param name="baseUrl">The manifest base URL.</param>
    public static Uri GetMiniPackListUri(string baseUrl) => new(baseUrl + MiniPackListName);

    /// <summary>
    ///     Builds the URL of a remote manifest by its file name.
    /// </summary>
    /// <param name="baseUrl">The manifest base URL.</param>
    /// <param name="name">The manifest file name.</param>
    public static Uri GetManifestUri(string baseUrl, string name) => new(baseUrl + name);
}
