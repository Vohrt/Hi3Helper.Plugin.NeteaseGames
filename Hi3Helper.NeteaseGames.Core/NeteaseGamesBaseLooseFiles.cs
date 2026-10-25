using System;
using System.Collections.Generic;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     The <c>big_</c> manifest paths that are standalone files in the patch directory rather than resources stored
///     inside a base shard.
///     <para>
///         <b>Why this list is fixed.</b> A base shard is assembled by concatenating a chunk's bytes, so every path a
///         chunk lists normally ends up <i>inside</i> a shard. These three are the exception: the live installation
///         keeps them as plain files next to the shards, and its own <c>Patch.mpkinfo</c> does not describe them.
///         Measured 2026-10-26:
///     </para>
///     <list type="bullet">
///         <item>
///             each one is carried by a chunk that lists exactly that single path, and the chunk's bytes are
///             <b>byte for byte</b> the file on disk (MD5 equal for all three), so they are simply written out;
///         </item>
///         <item>
///             the remaining 672 single-resource chunks (<c>Fonts_1</c>, <c>CGs_0</c>, <c>Char_14</c>, …) are
///             ordinary shard resources - treating them as files would silently drop them from the base tier.
///         </item>
///     </list>
///     <para>
///         ⚠ <c>essential</c> and <c>patch.repository</c> are <b>not</b> on this list even though they look like it:
///         both are placed inside a shard (the live index puts <c>essential</c> at shard 126, offset 73,993,554).
///     </para>
/// </summary>
public static class NeteaseGamesBaseLooseFiles
{
    /// <summary>The engine prerequisite list.</summary>
    public const string Prerequisite = "prerequisite";

    /// <summary>The zstd dictionary the launcher's Python modules are compressed with.</summary>
    public const string LazycodeDictionary = "Misc/lazycode_zstd_dict";

    /// <summary>A single content blob under the <c>4a/</c> content directory.</summary>
    public const string FourAFontBlob = "4a/4a3101465e82fbbdc7e08f834fc82419";

    /// <summary>Whether a path is one of the standalone patch files.</summary>
    /// <param name="path">The path a <c>big_</c> manifest lists.</param>
    public static bool IsLoose(string path)
        => path is Prerequisite or LazycodeDictionary or FourAFontBlob;

    /// <summary>The three paths, for use as a planner's loose-path set.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Prerequisite,
        LazycodeDictionary,
        FourAFontBlob
    };
}
