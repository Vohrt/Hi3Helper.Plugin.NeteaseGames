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
    ///     The name of the shader cache list the launcher keeps in the patch directory.
    ///     <para>
    ///         Measured on 2026-10-23: the file is 7,985,520 bytes on disk and on the content host with an identical
    ///         MD5. It is <b>not</b> part of the patch list, so it has to be planned separately; leaving it out meant
    ///         a stale shader cache list was kept forever.
    ///     </para>
    /// </summary>
    public const string ShaderCacheListFileName = "shadercachelist_dx12.txt";

    /// <summary>
    ///     The directory prefix of the base shard tier ("big patch") content.
    ///     <para>
    ///         Measured on 2026-10-23 by reading the live launcher's own request URLs out of its memory:
    ///         <c>https://h74.gph.netease.com/big_20261023122834919/bigpatchlist_windows64_normal.txt</c>,
    ///         <c>.../big_20261023122834919/big_common/Char_736</c>, <c>.../big_shader/dx12_916</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ The directory is <c>big_</c> prefixed to <c>@metadata@.@base_version@</c> - <b>not</b> to line 0 of
    ///         the manifest. Measured 2026-10-24: under <c>big_{line 0}</c> only the 9 chunks the round added are
    ///         present (60 random samples: 1 hit); under <c>big_{@base_version@}</c> the whole cumulative pool is
    ///         (60/60, 15/15, 15/15 hits). Line 0 is the directory for the <i>patch</i> packages, which is the
    ///         opposite of what this tier needs - do not mix the two.
    ///     </para>
    /// </summary>
    public const string BigPatchDirectoryPrefix = "big_";

    /// <summary>The name of the base shard tier manifest inside the <c>big_</c> directory.</summary>
    public const string BigPatchListFileName = "bigpatchlist_windows64_normal.txt";

    /// <summary>The section that holds the base payload chunks inside the <c>big_</c> directory.</summary>
    public const string BigCommonSection = "big_common";

    /// <summary>The section that holds the shader chunks inside the <c>big_</c> directory.</summary>
    public const string BigShaderSection = "big_shader";

    /// <summary>
    ///     Builds the content path of an object in the base shard tier.
    /// </summary>
    /// <param name="contentVersion">The <c>@base_version@</c> of the manifest (NOT line 0 - see the remarks on
    ///     <see cref="BigPatchDirectoryPrefix" />).</param>
    /// <param name="section">The section (see <see cref="BigCommonSection" />), or null for the manifest itself.</param>
    /// <param name="name">The object name.</param>
    public static string GetBigPatchContentPath(string contentVersion, string? section, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentVersion);
        ArgumentException.ThrowIfNullOrEmpty(name);

        string path = $"{BigPatchDirectoryPrefix}{contentVersion}";
        if (!string.IsNullOrEmpty(section))
        {
            path += $"/{section}";
        }

        return $"{path}/{name}";
    }

    /// <summary>Builds the signed URL of an object in the base shard tier.</summary>
    /// <param name="contentVersion">The <c>@base_version@</c> of the manifest (NOT line 0).</param>
    /// <param name="section">The section, or null for the manifest itself.</param>
    /// <param name="name">The object name.</param>
    /// <param name="now">The current time (the signature depends on it).</param>
    public static string GetBigPatchUrl(string contentVersion, string? section, string name, DateTimeOffset now)
        => NeteaseGamesUrlSigner.Sign(
            $"https://{ContentHost}/{GetBigPatchContentPath(contentVersion, section, name)}", now);

    /// <summary>
    ///     Builds the signed URL of a content object.
    /// </summary>
    /// <param name="version">The content version (line 0 of the manifest) - correct for the patch tier.</param>
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
