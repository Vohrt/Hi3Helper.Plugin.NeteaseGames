using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     Plans the base shard tier of a fresh installation: which chunks go into which shard file, at which offsets.
///     <para>
///         <b>Why a fresh installation needs this at all.</b> The plugin's update path downloads whole shard files
///         that the vendor already assembled. A machine with no installation has nothing to download those from -
///         <c>Patch.mpk</c> and <c>Patch1..127.mpk</c> are not served by any content host (measured 2026-10-25:
///         10 URL shapes x 2 hosts x signed/unsigned, all 404). The only downloadable form of the base tier is the
///         <c>big_common</c> / <c>big_shader</c> chunk pool (<see cref="NeteaseGamesBigPatchList" />), so the
///         shard files have to be <b>assembled</b> from chunks.
///     </para>
///     <para>
///         <b>What makes assembly possible.</b> A chunk is a flat concatenation of the resources its manifest entry
///         lists, in that order, with no table of contents. Two properties were measured on the live installation
///         and both are load bearing:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>A chunk's resources are contiguous inside the vendor's own shard.</b> Sampled across the pool:
///             every chunk whose resources sit in one shard occupies one unbroken run of the shard, and the run's
///             length equals the chunk's announced size.
///         </item>
///         <item>
///             <b>A chunk's announced size equals the sum of its resources' sizes.</b> Holds for 10,380 of the
///             10,401 chunks; the 21 exceptions are chunks the vendor truncated or re-cut in a later round (see
///             <see cref="NeteaseGamesBigChunk.IsSizeConsistent" />), and they are skipped rather than trusted.
///         </item>
///     </list>
///     <para>
///         Together those properties mean a chunk can be stored as one unbroken byte run and every resource inside
///         it located by a running sum - so the shard layout only has to decide <i>where each chunk starts</i>.
///         The vendor's own layout is <b>not</b> reproducible (it is history dependent: shard 10 has 10 holes and
///         its resource order is not sorted), and it does not need to be. What has to be consistent is the index
///         the plugin writes, and it describes the layout this planner produced.
///     </para>
///     <para>
///         <b>Shard size.</b> The vendor uses ~524 MB shards. This planner fills a shard up to
///         <see cref="ShardCapacity" /> and starts a new one; the last shard is short. The live line-0 pool
///         produces <b>123</b> shards, not the vendor's 128 - the shard <i>count</i> is not reproduced, and it does
///         not have to be: the index the plugin writes names the shard of every resource, so the game reads the
///         layout that was produced rather than the one the vendor happens to have.
///     </para>
/// </summary>
public static class NeteaseGamesBaseLayoutPlanner
{
    /// <summary>
    ///     The payload size one shard is filled to before the next one is started.
    ///     <para>
    ///         Chosen from the live installation: its 128 base shards are 522-530 MB each, and no chunk in the pool
    ///         is larger than 447 MB, so a chunk always fits into a fresh shard. Measured 2026-10-26: the largest
    ///         line-0 chunk is 197,844,255 B, so <see cref="NeteaseGamesBaseLayout.HasSplitChunk" /> stays false and
    ///         no chunk is ever broken up.
    ///     </para>
    /// </summary>
    public const long ShardCapacity = 524_288_000;

    /// <summary>
    ///     Plans the base tier.
    /// </summary>
    /// <param name="chunks">The chunks to place, in the order they should be laid out.</param>
    /// <param name="resourceSizes">Every resource path to its size (from the remote patch list).</param>
    /// <param name="splitOversizedChunks">
    ///     When a chunk is larger than <see cref="ShardCapacity" />, split it across shards at resource boundaries
    ///     instead of failing. A chunk can never be split in the vendor's layout (its resources are contiguous),
    ///     but a chunk larger than a whole shard cannot be placed otherwise.
    /// </param>
    /// <exception cref="InvalidOperationException">A resource has no known size.</exception>
    public static NeteaseGamesBaseLayout Plan(
        IEnumerable<NeteaseGamesBigChunk> chunks,
        IReadOnlyDictionary<string, long> resourceSizes,
        bool splitOversizedChunks = true)
        => Plan(chunks, resourceSizes, loosePaths: null, splitOversizedChunks);

    /// <summary>
    ///     Plans the base tier, leaving the named paths out of the shard layout.
    /// </summary>
    /// <param name="chunks">The chunks to place, in the order they should be laid out.</param>
    /// <param name="resourceSizes">Every resource path to its size (from the remote patch list).</param>
    /// <param name="loosePaths">
    ///     Paths that are written to the patch directory as standalone files instead of being stored inside a
    ///     shard. A chunk that holds one is laid out without it.
    /// </param>
    /// <param name="splitOversizedChunks">Whether to split a chunk larger than a whole shard.</param>
    public static NeteaseGamesBaseLayout Plan(
        IEnumerable<NeteaseGamesBigChunk> chunks,
        IReadOnlyDictionary<string, long> resourceSizes,
        IReadOnlySet<string>? loosePaths,
        bool splitOversizedChunks = true)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(resourceSizes);

        NeteaseGamesBaseLayout layout = new();
        long offset = 0;
        HashSet<string> placed = new(StringComparer.Ordinal);

        foreach (NeteaseGamesBigChunk chunk in chunks)
        {
            if (!chunk.IsSizeConsistent(resourceSizes))
            {
                layout.SkippedChunks.Add(chunk);
                continue;
            }

            // A chunk whose only path is a loose file must not be laid out at all: the file is written on its own
            // and the chunk's bytes never reach a shard.
            if (loosePaths is not null && chunk.Paths.Count > 0 && chunk.Paths.All(loosePaths.Contains))
            {
                continue;
            }

            // Start a new shard only once the current one has already reached the capacity; the chunk that crossed
            // the line stays where it is and the next one opens the following shard.
            //
            // ⚠ This is "place, then check", not "check, then place". The vendor's own shards support it: 117 of
            // their 125 shards carry more than ShardCapacity bytes and **none** carries exactly that many, i.e. a
            // shard is full only after the last chunk has been appended, and a chunk is never broken in two.
            // A "check, then place" planner (which this used to be) keeps every shard under the cap and produces a
            // different shard count and different boundaries - measured 2026-10-26: 123 shards here vs 125 there,
            // with **0 of 232,450 resources** landing at the same (shard, offset) as the vendor's.
            if (layout.ShardCount > 0 && offset >= ShardCapacity)
            {
                layout.ShardCount++;
                offset = 0;
            }

            if (layout.ShardCount == 0)
            {
                layout.ShardCount = 1;
            }

            if (chunk.Size > ShardCapacity && splitOversizedChunks)
            {
                // Larger than a whole shard: lay its resources out one by one, which is the only way it can be
                // stored at all. Each resource still gets a correct index entry, so the result is readable.
                foreach (string path in chunk.Paths)
                {
                    if (!resourceSizes.TryGetValue(path, out long size) || size <= 0)
                    {
                        throw new InvalidOperationException(
                            $"The patch list carries no size for '{path}', so the chunk '{chunk.Name}' cannot be laid out.");
                    }

                    if (offset > 0 && offset + size > ShardCapacity)
                    {
                        layout.ShardCount++;
                        offset = 0;
                    }

                    if (!placed.Add(path))
                    {
                        continue;
                    }

                    layout.Placements.Add(new NeteaseGamesBasePlacement(path, layout.ShardCount - 1, offset, size, chunk.Section, chunk.Name));
                    offset += size;
                }

                // The layout is no longer a chunk pool; record that, because the caller must not claim the chunk
                // was stored unbroken.
                layout.HasSplitChunk = true;
                continue;
            }

            // The normal case: the chunk's resources are laid down consecutively, exactly as the chunk bytes are
            // concatenated, so a single byte copy places all of them.
            long cursor = offset;
            foreach (string path in chunk.Paths)
            {
                if (!resourceSizes.TryGetValue(path, out long size) || size <= 0)
                {
                    throw new InvalidOperationException(
                        $"The patch list carries no size for '{path}', so the chunk '{chunk.Name}' cannot be laid out.");
                }

                // A loose path is written as its own file, never into a shard.
                if (loosePaths is not null && loosePaths.Contains(path))
                {
                    continue;
                }

                if (!placed.Add(path))
                {
                    // A later manifest copy wins over an earlier one, and the caller passes the merged pool in
                    // priority order, so a duplicate means the earlier chunk that held it is stale. Keeping the
                    // first placement would leave the second chunk's bytes unwritten, so the first is kept and
                    // the duplicate dropped - the two copies are of the same resource.
                    cursor += size;
                    continue;
                }

                layout.Placements.Add(new NeteaseGamesBasePlacement(path, layout.ShardCount - 1, cursor, size, chunk.Section, chunk.Name));
                cursor += size;
            }

            offset = cursor;
        }

        return layout;
    }

    /// <summary>Names the shard file of a base shard.</summary>
    /// <param name="shardIndex">The zero based shard index (the same value the index stores).</param>
    /// <remarks>
    ///     Measured on the live installation (2026-10-25): the file name is the shard index itself, so shard 0 is
    ///     <c>Patch.mpk</c>, shard 1 is <c>Patch1.mpk</c> and shard 127 is <c>Patch127.mpk</c>. All 128 shards were
    ///     matched by payload length, and the mapping is the identity.
    /// </remarks>
    public static string GetShardFileName(int shardIndex)
        => shardIndex == 0 ? "Patch.mpk" : $"Patch{shardIndex}.mpk";

    /// <summary>Names the shard file of a shader shard (<c>ResourceExA.mpk</c> for index 0).</summary>
    public static string GetShaderShardFileName(int shardIndex)
        => shardIndex == 0 ? "ResourceExA.mpk" : $"ResourceExA{shardIndex}.mpk";
}

/// <summary>Where one resource was placed by <see cref="NeteaseGamesBaseLayoutPlanner" />.</summary>
/// <param name="Path">The virtual resource path.</param>
/// <param name="ShardIndex">The zero based shard the resource lives in.</param>
/// <param name="Offset">The offset of the resource inside that shard.</param>
/// <param name="Size">The size of the resource.</param>
/// <param name="ChunkSection">The section of the chunk that placed it.</param>
/// <param name="ChunkName">The name of the chunk that placed it.</param>
public readonly record struct NeteaseGamesBasePlacement(
    string Path,
    int ShardIndex,
    long Offset,
    long Size,
    string ChunkSection = "",
    string ChunkName = "");

/// <summary>
///     The result of <see cref="NeteaseGamesBaseLayoutPlanner.Plan" />: where every resource goes, and how many
///     shards the tier needs.
/// </summary>
public sealed class NeteaseGamesBaseLayout
{
    /// <summary>Every placed resource, in layout order.</summary>
    public List<NeteaseGamesBasePlacement> Placements { get; } = [];

    /// <summary>The number of shards the layout needs.</summary>
    public int ShardCount { get; set; }

    /// <summary>Chunks that were skipped because their announced size disagreed with their resources.</summary>
    public List<NeteaseGamesBigChunk> SkippedChunks { get; } = [];

    /// <summary>Whether a chunk larger than a whole shard had to be split at resource boundaries.</summary>
    public bool HasSplitChunk { get; set; }

    /// <summary>The payload length of one shard (the largest end offset in it).</summary>
    public long GetShardLength(int shardIndex)
    {
        long length = 0;
        foreach (NeteaseGamesBasePlacement placement in Placements)
        {
            if (placement.ShardIndex == shardIndex)
            {
                long end = placement.Offset + placement.Size;
                if (end > length)
                {
                    length = end;
                }
            }
        }

        return length;
    }

    /// <summary>The total number of bytes the layout describes.</summary>
    public long TotalSize
    {
        get
        {
            long total = 0;
            foreach (NeteaseGamesBasePlacement placement in Placements)
            {
                total += placement.Size;
            }

            return total;
        }
    }
}
