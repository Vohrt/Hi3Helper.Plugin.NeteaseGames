using System;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.Plugin.Core.Update;

namespace Hi3Helper.Plugin.SoR;

/// <summary>
///     Self update of the plugin itself.
///     <para>
///         The updater fetches <c>&lt;base&gt;/manifest.json</c> plus every asset the manifest lists (here
///         <c>SoR.dll</c>). Those files are published to the <c>release</c> branch of this repository by
///         <c>.github/workflows/release.yml</c> (one directory per game) and served over
///         <c>raw.githubusercontent.com</c>.
///     </para>
///     <para>
///         ⚠ The base URL has to point at the branch the workflow actually pushes to. An earlier revision
///         pointed at a custom CDN host that nothing ever uploaded to, so every update check failed with 404
///         and the plugin silently never updated.
///     </para>
/// </summary>
[GeneratedComClass]
public partial class SelfUpdate : PluginSelfUpdateBase
{
    /// <summary>The directory of this game on the release branch.</summary>
    private const string CdnFileSuffix = "SoR/";

    /// <summary>The primary source (raw.githubusercontent.com).</summary>
    private const string CdnPrimaryUrl =
        "https://raw.githubusercontent.com/Vohrt/Hi3Helper.Plugin.NeteaseGames/release/" + CdnFileSuffix;

    /// <summary>The fallback, which resolves to the same content through github.com's redirect.</summary>
    private const string CdnFallbackUrl =
        "https://github.com/Vohrt/Hi3Helper.Plugin.NeteaseGames/raw/release/" + CdnFileSuffix;

    private static readonly string[] BaseCdnUrls = [CdnPrimaryUrl, CdnFallbackUrl];

    private HttpClient? _updateHttpClient;

    /// <inheritdoc/>
    protected override ReadOnlySpan<string> BaseCdnUrlSpan => BaseCdnUrls;

    /// <summary>
    ///     The client of the update download. Certificate validation stays enabled: the plugin downloads an
    ///     executable, so it must never accept an unverified server.
    /// </summary>
    protected override HttpClient UpdateHttpClient
        => _updateHttpClient ??= NeteaseGamesHttp.CreateClient(TimeSpan.FromSeconds(100));
}
