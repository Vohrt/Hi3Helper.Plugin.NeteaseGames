using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.NeteaseGames.Core.Management;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     Assembles the base shard tier of a fresh installation from the downloadable chunk pool.
///     <para>
///         The assembly is a byte copy, not a reconstruction: <see cref="NeteaseGamesBaseLayoutPlanner" /> lays every
///         chunk down as one unbroken run, and a chunk is already a flat concatenation of its resources in list
///         order. So each chunk's downloaded bytes are appended to its shard file, and the resource offsets the
///         planner recorded stay valid without any per-resource work. Verified on 2026-10-25 over the live pool:
///         9,731 multi-resource chunks, every one of them contiguous in the produced layout.
///     </para>
///     <para>
///         <b>Resumability.</b> The chunks of a shard are appended strictly in layout order, so a shard file's bytes
///         are the concatenation of a known prefix of that shard's chunk list. An interrupted run is resumed by
///         truncating the shard back to the end of the last complete chunk - no chunk has to be downloaded twice.
///     </para>
///     <para>
///         <b>Why the shards are assembled in parallel but each shard sequentially.</b> Appending is inherently
///         ordered, but different shards share no state, so up to <see cref="ShardParallelism" /> shards are built
///         at once and each one downloads its chunks in order. That keeps the byte copy honest (no seeking, no
///         sparse files, no partial-chunk state) while still saturating the link.
///     </para>
/// </summary>
public static class NeteaseGamesBaseAssembler
{
    /// <summary>How many shard files are assembled at the same time.</summary>
    public const int ShardParallelism = 4;

    /// <summary>One stage of the assembly: a chunk together with the shard and offset it was laid out at.</summary>
    /// <param name="Chunk">The chunk to download.</param>
    /// <param name="ShardIndex">The shard the chunk belongs to.</param>
    /// <param name="Offset">The offset of the chunk's first byte inside that shard.</param>
    /// <param name="SourceVersion">
    ///     The <c>big_</c> directory whose manifest listed this chunk (the first one to try), or an empty string
    ///     when any directory may serve it.
    /// </param>
    public readonly record struct Step(NeteaseGamesBigChunk Chunk, int ShardIndex, long Offset, string SourceVersion);

    /// <summary>
    ///     Turns a layout into the ordered chunks to download, grouped by shard.
    /// </summary>
    /// <param name="layout">The layout the planner produced.</param>
    /// <param name="chunks">The chunks, in the order they were planned.</param>
    /// <param name="sourceVersion">The directory that served <paramref name="chunks" />.</param>
    public static List<Step> BuildSteps(NeteaseGamesBaseLayout layout, IEnumerable<NeteaseGamesBigChunk> chunks,
        string sourceVersion)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(chunks);

        // A step is emitted only for a chunk that actually PLACED something. Two chunks can list the same path
        // (measured 2026-10-26: the stale 'resource.repository_0' and the current 'resource.repository_1' both list
        // 'resource.repository'), and the planner keeps the first placement - so matching by path alone would emit
        // a step for the loser as well, and its bytes would be copied over the winner's. The placement records which
        // chunk it came from, and that is the only chunk that gets a step.
        Dictionary<string, NeteaseGamesBasePlacement> placements = new(StringComparer.Ordinal);
        foreach (NeteaseGamesBasePlacement placement in layout.Placements)
        {
            placements[placement.Path] = placement;
        }

        List<Step> steps = [];
        foreach (NeteaseGamesBigChunk chunk in chunks)
        {
            if (chunk.Paths.Count == 0)
            {
                continue;
            }

            int shard = -1;
            long offset = 0;
            bool found = false;
            foreach (string path in chunk.Paths)
            {
                if (!placements.TryGetValue(path, out NeteaseGamesBasePlacement placement) ||
                    placement.Size <= 0)
                {
                    continue;
                }

                // The planner recorded which chunk claimed the path; a chunk that lost the claim contributes nothing.
                if (placement.ChunkName.Length > 0 &&
                    (!string.Equals(placement.ChunkName, chunk.Name, StringComparison.Ordinal) ||
                     !string.Equals(placement.ChunkSection, chunk.Section, StringComparison.Ordinal)))
                {
                    continue;
                }

                shard = placement.ShardIndex;
                offset = placement.Offset;
                found = true;
                break;
            }

            if (!found)
            {
                continue;
            }

            steps.Add(new Step(chunk, shard, offset, sourceVersion));
        }

        return steps;
    }

    /// <summary>Groups steps by shard, keeping the layout order inside each shard.</summary>
    public static IEnumerable<List<Step>> GroupByShard(IEnumerable<Step> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        Dictionary<int, List<Step>> byShard = [];
        List<int> order = [];
        foreach (Step step in steps)
        {
            if (!byShard.TryGetValue(step.ShardIndex, out List<Step>? list))
            {
                list = [];
                byShard[step.ShardIndex] = list;
                order.Add(step.ShardIndex);
            }

            list.Add(step);
        }

        foreach (int shard in order.OrderBy(static s => s))
        {
            List<Step> list = byShard[shard];
            list.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
            yield return list;
        }
    }

    /// <summary>
    ///     Assembles every shard file of the base tier.
    /// </summary>
    /// <param name="plan">The steps to run.</param>
    /// <param name="shardDirectory">The directory the shard files are written to.</param>
    /// <param name="stagingDirectory">The directory a chunk is downloaded into.</param>
    /// <param name="downloader">The downloader.</param>
    /// <param name="progress">Called with the number of bytes finished so far.</param>
    /// <param name="token">Cancel token.</param>
    /// <param name="shardFileName">
    ///     Names the shard file of a shard index. The base tier and the shader tier use different prefixes
    ///     (<c>Patch{N}.mpk</c> vs <c>ResourceExA{N}.mpk</c>), so the caller decides; <c>null</c> means the base tier.
    /// </param>
    /// <returns>The failing chunk name, or <c>null</c> when every chunk was assembled.</returns>
    public static async Task<string?> AssembleAsync(
        NeteaseGamesBaseAssemblyPlan plan,
        string shardDirectory,
        string stagingDirectory,
        NeteaseGamesDownloader downloader,
        Action<long>? progress,
        CancellationToken token,
        Func<int, string>? shardFileName = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(downloader);

        shardFileName ??= NeteaseGamesBaseLayoutPlanner.GetShardFileName;

        Directory.CreateDirectory(shardDirectory);
        Directory.CreateDirectory(stagingDirectory);

        List<List<Step>> shards = [.. GroupByShard(plan.Steps)];

        // The first failure wins; the rest of that shard is abandoned but other shards finish, so a retry only has
        // to redo what actually failed.
        using SemaphoreSlim gate = new(ShardParallelism);
        List<Task<string?>> running = [];
        try
        {
            foreach (List<Step> shard in shards)
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        return await AssembleShardAsync(shard, shardDirectory, stagingDirectory, downloader, progress,
                                token, shardFileName)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, token));
            }

            string?[] results = await Task.WhenAll(running).ConfigureAwait(false);
            return results.FirstOrDefault(static r => r is not null);
        }
        finally
        {
            // A cancelled run leaves the gate taken by tasks that never started; disposing it after they are gone
            // is what keeps the semaphore from throwing on the way out.
            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancel.
            }
        }
    }

    /// <summary>Assembles one shard file, appending its chunks in layout order.</summary>
    private static async Task<string?> AssembleShardAsync(
        List<Step> steps,
        string shardDirectory,
        string stagingDirectory,
        NeteaseGamesDownloader downloader,
        Action<long>? progress,
        CancellationToken token,
        Func<int, string> shardFileName)
    {
        if (steps.Count == 0)
        {
            return null;
        }

        int shardIndex = steps[0].ShardIndex;
        string shardPath = Path.Combine(shardDirectory, shardFileName(shardIndex));

        // Resume: keep the longest prefix of chunks that is already on disk, byte for byte.
        long resumeOffset = 0;
        int firstMissing = 0;
        if (File.Exists(shardPath))
        {
            long length = new FileInfo(shardPath).Length;
            long expected = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                long end = steps[i].Offset + steps[i].Chunk.Size;
                if (end <= length)
                {
                    expected = end;
                    firstMissing = i + 1;
                }
                else
                {
                    break;
                }
            }

            resumeOffset = expected;

            // A chunk boundary is exact: everything past it is a partial chunk from the interrupted run and is cut
            // off, otherwise the next append would land at the wrong offset.
            if (length != resumeOffset)
            {
                using FileStream trim = new(shardPath, FileMode.Open, FileAccess.Write, FileShare.None);
                trim.SetLength(resumeOffset);
            }

            if (firstMissing > 0)
            {
                SharedStatic.InstanceLogger?.LogInformation(
                    "[NeteaseGames] Base shard {Shard} resumes after {Chunks} chunks ({Bytes} bytes already on disk)",
                    shardIndex, firstMissing, resumeOffset);
            }
        }
        else
        {
            // A fresh file: the layout of a shard always starts at 0.
            using FileStream create = new(shardPath, FileMode.Create, FileAccess.Write, FileShare.None);
            create.SetLength(0);
        }

        for (int i = firstMissing; i < steps.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            Step step = steps[i];
            string chunkPath = Path.Combine(stagingDirectory,
                $"big_{step.ShardIndex:D3}_{step.Chunk.Section}_{step.Chunk.Name}");

            bool downloaded = await DownloadChunkAsync(step, chunkPath, downloader, token).ConfigureAwait(false);
            if (!downloaded)
            {
                return $"{step.Chunk.Section}/{step.Chunk.Name}";
            }

            long size = new FileInfo(chunkPath).Length;
            if (size != step.Chunk.Size)
            {
                SharedStatic.InstanceLogger?.LogError(
                    "[NeteaseGames] Chunk {Chunk} is {Actual} bytes but the manifest announces {Expected}",
                    step.Chunk.Name, size, step.Chunk.Size);
                TryDelete(chunkPath);
                return $"{step.Chunk.Section}/{step.Chunk.Name}";
            }

            // Append at the recorded offset. The file position is used rather than FileMode.Append because the
            // offset is what the index will describe, so a mismatch must be visible immediately.
            using (FileStream shard = new(shardPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                shard.Position = step.Offset;
                using FileStream chunk = new(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                await chunk.CopyToAsync(shard, 1 << 20, token).ConfigureAwait(false);
            }

            TryDelete(chunkPath);
            progress?.Invoke(size);
        }

        return null;
    }

    /// <summary>Downloads one chunk, trying the directory its manifest came from first.</summary>
    private static Task<bool> DownloadChunkAsync(Step step, string chunkPath, NeteaseGamesDownloader downloader,
        CancellationToken token)
    {
        // A chunk is served from the directory whose manifest listed it; the other directory is only a fallback
        // because a chunk that directory does not list answers 404 there. Measured 2026-10-26: every chunk the base
        // copy lists is served there and 404 on the line-0 copy (30/30), while the 9 chunks only the line-0 copy
        // lists are served there and 404 on the base copy (9/9) - so both directories have to be reachable.
        NeteaseGamesDownloadItem item = new()
        {
            RelativePath = NeteaseGamesContentEndpoints.GetBigPatchContentPath(
                step.SourceVersion, step.Chunk.Section, step.Chunk.Name),
            TargetRelativePath = Path.GetFileName(chunkPath),
            Kind = NeteaseGamesDownloadKind.LooseFile,
            ExpectedSize = step.Chunk.Size,
            StagedPath = chunkPath
        };

        return downloader.DownloadAsync(item, progress: null, token);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover staging file is harmless; the next run overwrites it.
        }
    }
}

/// <summary>
///     A chunk that carries a standalone patch file instead of a shard resource.
/// </summary>
/// <param name="Chunk">The chunk to download.</param>
/// <param name="SourceVersion">The <c>big_</c> directory that serves it.</param>
public readonly record struct NeteaseGamesBaseLooseChunk(NeteaseGamesBigChunk Chunk, string SourceVersion);

/// <summary>
///     The chunks a fresh installation of the base tier needs, in layout order.
/// </summary>
public sealed class NeteaseGamesBaseAssemblyPlan
{
    /// <summary>Every step, in layout order.</summary>
    public List<NeteaseGamesBaseAssembler.Step> Steps { get; } = [];

    /// <summary>The number of shard files the assembly produces.</summary>
    public int ShardCount { get; set; }

    /// <summary>
    ///     The chunks whose only path is a standalone patch file rather than a shard resource.
    ///     <para>
    ///         These do not take part in the shard layout: each is downloaded as one object and written straight to
    ///         its path in the patch directory (see <see cref="NeteaseGamesBaseLooseFiles" />). They are kept here so
    ///         the plan's reported size covers them too.
    ///     </para>
    /// </summary>
    public List<NeteaseGamesBaseLooseChunk> LooseChunks { get; } = [];

    /// <summary>
    ///     Where every resource was placed. The index that makes the produced shards readable is written from this,
    ///     so it has to be the same set the assembly wrote - not the vendor's layout.
    /// </summary>
    public List<NeteaseGamesBasePlacement> Placements { get; } = [];

    /// <summary>The total number of bytes that have to be downloaded.</summary>
    public long TotalSize
    {
        get
        {
            long total = 0;
            foreach (NeteaseGamesBaseAssembler.Step step in Steps)
            {
                total += step.Chunk.Size;
            }

            foreach (NeteaseGamesBaseLooseChunk loose in LooseChunks)
            {
                total += loose.Chunk.Size;
            }

            return total;
        }
    }

    /// <summary>
    ///     The bytes the assembly downloads, without the standalone loose chunks.
    ///     <para>
    ///         The loose chunks are also added to the download plan as items of their own, so counting them here as
    ///         well made the reported total 119,967 bytes larger than the work (measured 2026-10-26 on the live
    ///         branch). The plan reports <see cref="TotalSize" /> for its own accounting and this value for the
    ///         progress denominator.
    ///     </para>
    /// </summary>
    public long AssemblySize
    {
        get
        {
            long total = 0;
            foreach (NeteaseGamesBaseAssembler.Step step in Steps)
            {
                total += step.Chunk.Size;
            }

            return total;
        }
    }
}
