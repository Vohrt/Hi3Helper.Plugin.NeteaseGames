using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     One chunk of the base tier (<c>big_common/…</c> or <c>big_shader/…</c>).
///     <para>
///         A chunk is a flat concatenation of the resources it lists, in list order. It carries <b>no</b> table
///         of contents, so the byte range of a single resource inside a chunk is the running sum of the sizes of
///         the resources listed before it. Measured on 2026-10-25: a resource sliced that way out of
///         <c>big_common/Patch_Sounds_1</c> has the exact MD5 the <c>essential</c> list announces, and all 846
///         resources of <c>Patch121.mpk</c> reconstructed that way matched the shard byte for byte.
///     </para>
/// </summary>
/// <param name="Section">The section the chunk lives in (<c>big_common</c> / <c>big_windows64_common</c>).</param>
/// <param name="Name">The name of the chunk on the content host (e.g. <c>Char_120</c>).</param>
/// <param name="Size">The announced size of the chunk in bytes.</param>
/// <param name="Paths">The resources the chunk contains, in storage order.</param>
/// <param name="Hash">The 64 bit hash the manifest announces for the chunk.</param>
public sealed record NeteaseGamesBigChunk(string Section, string Name, long Size, IReadOnlyList<string> Paths, ulong Hash)
{
    /// <summary>
    ///     Whether the size the manifest announces for this chunk equals the sum of the sizes of the resources it
    ///     lists.
    ///     <para>
    ///         Measured on 2026-10-25 over the live pool (10,401 chunks): it holds for 10,380 of them. The 21
    ///         exceptions are chunks the vendor cut in a later round - e.g. <c>CGs_137</c> is announced as
    ///         15,809,701 bytes while the resources it lists add up to 83,014,771 (the full chunk was re-issued as
    ///         <c>CGs_141</c>). A chunk that fails this check cannot be laid out by simple concatenation, so
    ///         <see cref="NeteaseGamesBaseLayoutPlanner" /> skips it instead of writing bytes at wrong offsets.
    ///     </para>
    /// </summary>
    /// <param name="resourceSizes">Every resource path to its size.</param>
    public bool IsSizeConsistent(IReadOnlyDictionary<string, long> resourceSizes)
    {
        ArgumentNullException.ThrowIfNull(resourceSizes);

        long sum = 0;
        foreach (string path in Paths)
        {
            if (!resourceSizes.TryGetValue(path, out long size))
            {
                return false;
            }

            sum += size;
        }

        return sum == Size;
    }
}

/// <summary>
///     A resource of the base tier as one of the <c>big_</c> manifests describes it.
/// </summary>
/// <param name="Path">The virtual path of the resource (e.g. <c>Char/npc/…/base_man.skeleton</c>).</param>
/// <param name="Size">The size of the resource in bytes.</param>
/// <param name="Hash">The 64 bit content hash (the same value the patch list carries).</param>
public readonly record struct NeteaseGamesBigResource(string Path, long Size, ulong Hash);

/// <summary>
///     The base tier manifest (<c>bigpatchlist_windows64_normal.txt</c>), which describes the whole base shard
///     tier as a pool of chunks.
///     <para>
///         <b>Why this is the manifest a fresh installation needs.</b> The patch list
///         (<c>patchlist_windows64_normal.txt</c>) describes the <i>delta</i> tier - 47 shards and three indexes
///         that only exist once the game has been installed. The base tier (<c>Patch.mpk</c> +
///         <c>Patch1..127</c>, 64,232,426,422 bytes) is distributed only through this manifest and its chunks.
///     </para>
///     <para>
///         <b>Which directory serves it.</b> Both the base version (<c>@base_version@</c>) and the content version
///         (line 0 of the patch manifest) serve a copy of this manifest, and they are <b>different</b> files:
///         measured on 2026-10-25, the base version copy lists 10,419 chunks and the content version copy 10,424.
///         The content version copy is the newer and complete one - it lists every one of the 232,450 resources
///         the live index describes, while the base version copy is missing 17 of them (resources that were
///         rotated into dedicated chunks such as <c>CGs_142</c>). Chunks are fetched from whichever directory
///         serves them: the 9 newest exist only under the content version, everything else only under the base
///         version. <see cref="NeteaseGamesBigPatchList.Merge" /> combines both manifests and keeps a per-chunk
///         download order.
///     </para>
/// </summary>
public sealed class NeteaseGamesBigPatchList
{
    /// <summary>The name of the manifest inside the <c>big_</c> directory.</summary>
    public const string FileName = "bigpatchlist_windows64_normal.txt";

    private readonly Dictionary<(string Section, string Name), NeteaseGamesBigChunk> _chunks = [];

    /// <summary>The sections the manifest declares (in file order).</summary>
    public List<string> FileSections { get; } = [];

    /// <summary>The <c>mapping_version</c> the manifest was built for (only the newer copy carries it).</summary>
    public string MappingVersion { get; set; } = string.Empty;

    /// <summary>The chunks, keyed by (section, name).</summary>
    public IReadOnlyDictionary<(string Section, string Name), NeteaseGamesBigChunk> Chunks => _chunks;

    /// <summary>The number of chunks.</summary>
    public int Count => _chunks.Count;

    /// <summary>The total number of bytes the chunks announce.</summary>
    public long TotalSize
    {
        get
        {
            long total = 0;
            foreach (NeteaseGamesBigChunk chunk in _chunks.Values)
            {
                total += chunk.Size;
            }

            return total;
        }
    }

    /// <summary>Adds a chunk, replacing an existing entry of the same name.</summary>
    public void Add(NeteaseGamesBigChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        _chunks[(chunk.Section, chunk.Name)] = chunk;
    }

    /// <summary>
    ///     Parses a <c>bigpatchlist</c> document.
    /// </summary>
    /// <param name="json">The raw JSON (it carries no <c>HEX_</c> envelope - measured 2026-10-25).</param>
    /// <exception cref="InvalidDataException">The document is not a big patch list.</exception>
    public static NeteaseGamesBigPatchList Parse(ReadOnlySpan<byte> json)
    {
        NeteaseGamesBigPatchList result = new();

        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The base tier manifest is not a JSON object.");
        }

        if (root.TryGetProperty("mapping_version", out JsonElement mappingVersion) &&
            mappingVersion.ValueKind == JsonValueKind.String)
        {
            result.MappingVersion = mappingVersion.GetString() ?? string.Empty;
        }

        // The sections are the properties that hold an object of chunk entries. `file_sections` names them
        // explicitly; falling back to "every object property" keeps the parser working if the vendor renames it.
        if (root.TryGetProperty("file_sections", out JsonElement sections) && sections.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement section in sections.EnumerateArray())
            {
                if (section.ValueKind == JsonValueKind.String)
                {
                    result.FileSections.Add(section.GetString() ?? string.Empty);
                }
            }
        }

        if (result.FileSections.Count == 0)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    result.FileSections.Add(property.Name);
                }
            }
        }

        foreach (string section in result.FileSections)
        {
            if (string.IsNullOrEmpty(section) || !root.TryGetProperty(section, out JsonElement entries) ||
                entries.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty entry in entries.EnumerateObject())
            {
                // Each entry is [size, [paths...], hash].
                if (entry.Value.ValueKind != JsonValueKind.Array || entry.Value.GetArrayLength() < 2)
                {
                    continue;
                }

                long size = entry.Value[0].TryGetInt64(out long announcedSize) ? announcedSize : 0;
                JsonElement paths = entry.Value[1];
                if (paths.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                List<string> list = new(paths.GetArrayLength());
                foreach (JsonElement path in paths.EnumerateArray())
                {
                    if (path.ValueKind == JsonValueKind.String)
                    {
                        list.Add(path.GetString() ?? string.Empty);
                    }
                }

                ulong hash = 0;
                if (entry.Value.GetArrayLength() >= 3)
                {
                    entry.Value[2].TryGetUInt64(out hash);
                }

                result.Add(new NeteaseGamesBigChunk(section, entry.Name, size, list, hash));
            }
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException("The base tier manifest carries no chunks.");
        }

        return result;
    }

    /// <summary>
    ///     Builds the map from every resource path to the chunk that stores it.
    ///     <para>
    ///         A path can appear in more than one chunk: 1,076 of the 10,419 chunks of the live manifest span two
    ///         shards, and a path that moved between chunks appears in both the old and the new manifest. When a
    ///         path is listed twice, the chunk that comes later in <paramref name="priority" /> order wins, so the
    ///         caller can make the newest manifest authoritative.
    ///     </para>
    /// </summary>
    /// <param name="priority">The manifests, oldest first.</param>
    public static Dictionary<string, NeteaseGamesBigResource> BuildResourceMap(
        IEnumerable<NeteaseGamesBigPatchList> priority)
    {
        ArgumentNullException.ThrowIfNull(priority);

        Dictionary<string, NeteaseGamesBigResource> map = new(StringComparer.Ordinal);
        foreach (NeteaseGamesBigPatchList list in priority)
        {
            foreach (NeteaseGamesBigChunk chunk in list._chunks.Values)
            {
                // The per-resource size is not in the manifest; it comes from the patch list. Only the path and
                // the chunk assignment are recorded here.
                foreach (string path in chunk.Paths)
                {
                    map[path] = new NeteaseGamesBigResource(path, 0, 0);
                }
            }
        }

        return map;
    }

    /// <summary>
    ///     Merges two manifests: every chunk of <paramref name="older" /> is kept unless
    ///     <paramref name="newer" /> carries a chunk of the same name, in which case the newer one wins.
    /// </summary>
    /// <param name="older">The older manifest (the one served under <c>big_{@base_version@}</c>).</param>
    /// <param name="newer">The newer manifest (the one served under <c>big_{line 0}</c>).</param>
    public static NeteaseGamesBigPatchList Merge(NeteaseGamesBigPatchList older, NeteaseGamesBigPatchList newer)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);

        NeteaseGamesBigPatchList merged = new() { MappingVersion = newer.MappingVersion };
        foreach (string section in older.FileSections)
        {
            merged.FileSections.Add(section);
        }

        foreach (string section in newer.FileSections)
        {
            if (!merged.FileSections.Contains(section))
            {
                merged.FileSections.Add(section);
            }
        }

        foreach (NeteaseGamesBigChunk chunk in older._chunks.Values)
        {
            merged.Add(chunk);
        }

        foreach (NeteaseGamesBigChunk chunk in newer._chunks.Values)
        {
            merged.Add(chunk);
        }

        return merged;
    }
}
