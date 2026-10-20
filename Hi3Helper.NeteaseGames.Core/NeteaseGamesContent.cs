using System;
using System.Collections.Generic;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     The content endpoints of the NetEase download CDN.
///     <para>
///         The payload of the game (the file list, the mpk shards, the mini pack) does not live on the
///         update host but on the "game patch host" <c>h74.gph.netease.com</c>.
///     </para>
///     <para>
///         The layout of the content host is:
///         <code>
///         /&lt;version&gt;/&lt;file&gt;                  - the file lists (patchlist, sort map, preload list)
///         /&lt;version&gt;/&lt;section&gt;/&lt;file&gt;       - the mpk shards (section = mpk_script / mpk_medium / mpk_indep)
///         </code>
///         Every request has to carry the signature (<c>?key1=..&amp;key2=..</c>, see
///         <see cref="NeteaseGamesUrlSigner" />), otherwise the edge answers with <c>403 Forbidden</c>.
///     </para>
/// </summary>
public static class NeteaseGamesContentEndpoints
{
    /// <summary>The primary content host.</summary>
    public const string ContentHost = "h74.gph.netease.com";

    /// <summary>The mirror hosts of the content host (in the order of the remote configuration).</summary>
    public static readonly string[] ContentMirrorHosts =
    [
        "h74-21.gph.netease.com",
        "h74-19.gph.netease.com"
    ];

    /// <summary>
    ///     The host that serves the engine payload (the mini pack).
    ///     <para>
    ///         The payload lives below <c>/loose_file/mini_pack_&lt;version&gt;/</c> and is only available on the
    ///         game download host; the regular content hosts answer with 404 for it.
    ///     </para>
    /// </summary>
    public const string EnginePayloadHost = "h74.gdl.netease.com";

    /// <summary>
    ///     The hosts that may serve the engine payload.
    /// </summary>
    public static IReadOnlyList<string> GetEnginePayloadHosts() => [EnginePayloadHost];

    /// <summary>The name of the full file list.</summary>
    public const string PatchListFileName = "patchlist_windows64_normal.txt";

    /// <summary>The name of the file list that only contains the files that are stored inside the mpk shards.</summary>
    public const string PatchListMpkFileName = "patchlist_windows64_normal_mpk.txt";

    /// <summary>The name of the sort map.</summary>
    public const string SortMapFileName = "sort_map_windows64_normal.txt";

    /// <summary>The name of the preload list.</summary>
    public const string PreloadListFileName = "preload_list_windows64_normal.txt";

    /// <summary>
    ///     Builds the signed URL of a content object.
    /// </summary>
    /// <param name="version">The content version (the base version of the patch).</param>
    /// <param name="section">The section of the object (e.g. <c>mpk_script</c>); may be empty.</param>
    /// <param name="fileName">The name of the object.</param>
    /// <param name="now">The current time (the signature depends on it).</param>
    /// <param name="host">The host to use.</param>
    public static string GetContentUrl(string version, string? section, string fileName, DateTimeOffset now,
        string host = ContentHost)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        string path = $"/{version}";
        if (!string.IsNullOrEmpty(section))
        {
            path += $"/{section}";
        }

        path += $"/{fileName}";

        return NeteaseGamesUrlSigner.Sign($"https://{host}{path}", now);
    }
}
