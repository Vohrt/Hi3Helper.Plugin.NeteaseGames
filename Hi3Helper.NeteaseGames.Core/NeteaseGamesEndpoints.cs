using System;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     The NetEase network endpoints and build-time constants that the plugin needs in order to work
///     <b>without the official PCLauncher being installed</b>.
///     <para>
///         All of these values were recovered by decrypting the launcher's <c>LCFG</c> container
///         (<c>PCLauncher\config\&lt;md5&gt;</c>), see <see cref="NeteaseGamesCrypto.DecryptLcfg" />.
///         The plugin ships them as constants so that a fresh install works out of the box; when the launcher
///         happens to be present its <c>config.xml</c> is parsed as well and may override them.
///     </para>
/// </summary>
public static class NeteaseGamesEndpoints
{
    /// <summary>
    ///     The NetEase project/channel id of the CN build.
    /// </summary>
    public const string Project = "h74";

    /// <summary>
    ///     The project id of the oversea build.
    /// </summary>
    public const string ProjectOversea = "h74na";

    /// <summary>
    ///     The <c>AppKey</c> of the project (third line of the launcher's <c>dump.config</c> is the channel tag).
    /// </summary>
    public const string AppKey = "5334ebccc3f9fd71ccb1f1db20f91772";

    /// <summary>
    ///     The channel tag (CPS_TAG) of the CN build, taken from <c>dump.config</c>.
    /// </summary>
    public const string ChannelTag = "_launcher_deploy_202609161132";

    /// <summary>
    ///     The base URL of the patch / mini-pack manifests (<c>patch_list_url</c> in <c>config.xml</c>).
    /// </summary>
    public const string PatchListBaseUrl = "https://h74.update.netease.com/pl/";

    /// <summary>
    ///     The failover mirror of <see cref="PatchListBaseUrl" /> (from <c>cdn_map</c> in <c>config.xml</c>).
    /// </summary>
    public const string PatchListMirrorUrl = "https://h74-06.update.netease.com/pl/";

    /// <summary>
    ///     The content host that serves the game payload (<c>gph_url</c> in <c>config.xml</c>).
    /// </summary>
    public const string ContentHostUrl = "https://h74.gph.netease.com/";

    /// <summary>
    ///     The host that serves the per-file web assets (<c>*.fp.ps.netease.com</c>).
    /// </summary>
    public const string FilePlatformUrl = "https://h74.fp.ps.netease.com/file/";

    /// <summary>
    ///     The "hot domain server" that hands out the CDN edge IPs.
    /// </summary>
    public const string HdServerUrl = "https://dns.update.netease.com/hdserver";

    /// <summary>
    ///     The server time endpoint (used to detect clock skew before signing requests).
    /// </summary>
    public const string RemoteTimeUrl = "https://h74-ms-tools-prodnew-out.netease.com/tools/v1/time";

    /// <summary>
    ///     The base URL of the notice images.
    /// </summary>
    public const string NoticeImageBaseUrl = "https://h74.update.netease.com/game_notice/image/";

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
    ///     Name of the remote launcher version manifest.
    /// </summary>
    public const string LauncherListName = ManifestPrefix + "_" + ManifestBranch + "_launcher_version.json";

    /// <summary>
    ///     Name of the remote notice manifest.
    /// </summary>
    public const string NoticeListName = ManifestPrefix + "_" + ManifestBranch + "_launcher_notice_info.xml";

    /// <summary>
    ///     Name of the remote gray-test manifest.
    /// </summary>
    public const string GrayTestListName = ManifestPrefix + "_" + ManifestBranch + "_gray_list_info.json";

    /// <summary>
    ///     Name of the remote device-limit manifest (this one carries no prefix).
    /// </summary>
    public const string DeviceLimitListName = ManifestBranch + "_limit_device_info.json";

    /// <summary>
    ///     Name of the remote patch manifest (plain text, it carries no <c>HEX_</c> envelope).
    /// </summary>
    public const string PatchMd5ListName = ManifestPrefix + "_patchmd5_windows64_normal.txt";

    /// <summary>
    ///     The <c>cdn_map</c> domains of the CN build, in the order the launcher tries them.
    /// </summary>
    public static readonly string[] CdnDomains =
    [
        "h74-06.update.netease.com",
        "h74.update.netease.com"
    ];

    /// <summary>
    ///     The RVA of the <c>LCFG</c> AES-256 key constant inside <c>PCLauncher.exe</c>.
    /// </summary>
    public const int LcfgKeyRva = 0x2204280;

    /// <summary>
    ///     The <c>LCFG</c> AES-256 key of the official launcher (<c>CfgObfuscateMgr</c> constant).
    /// </summary>
    public static ReadOnlySpan<byte> LcfgKeyFallback =>
    [
        0x7B, 0x9C, 0xA8, 0x3D, 0x7E, 0x9B, 0x71, 0x56,
        0xF1, 0xB5, 0xA1, 0x5D, 0xCA, 0x6E, 0x61, 0x7F,
        0x74, 0x8E, 0xA9, 0x13, 0x5E, 0x66, 0x00, 0x80,
        0x95, 0x3A, 0xB5, 0x97, 0xA1, 0x4B, 0xD0, 0x7E
    ];

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
