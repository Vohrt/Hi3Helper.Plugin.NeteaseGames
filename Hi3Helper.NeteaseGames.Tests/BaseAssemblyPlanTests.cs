using System;
using System.Collections.Generic;
using System.Linq;
using Hi3Helper.NeteaseGames.Core;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The base tier assembly rules that were measured against the live branch on 2026-10-26. Each test pins one of
///     them so a later refactor cannot quietly reintroduce a bug the live data exposed.
/// </summary>
public class BaseAssemblyPlanTests
{
    private static NeteaseGamesBigChunk Chunk(string name, params (string Path, long Size)[] resources)
        => new("big_common", name, resources.Sum(r => r.Size), [.. resources.Select(r => r.Path)], 0);

    private static Dictionary<string, long> Sizes(params (string Path, long Size)[] resources)
        => resources.ToDictionary(r => r.Path, r => r.Size, StringComparer.Ordinal);

    /// <summary>
    ///     Two chunks can list the same path - the live branch has a stale <c>resource.repository_0</c> and the
    ///     current <c>resource.repository_1</c> both claiming <c>resource.repository</c>. Only the chunk the planner
    ///     actually placed may get a step; emitting one for the loser would copy its bytes over the winner's.
    /// </summary>
    [Fact]
    public void BuildSteps_EmitsOnlyTheChunkThatPlacedTheResource()
    {
        (string, long)[] shared = [("resource.repository", 1000)];
        NeteaseGamesBigChunk stale = Chunk("resource.repository_0", shared);
        NeteaseGamesBigChunk current = Chunk("resource.repository_1", shared);

        Dictionary<string, long> sizes = Sizes(shared);
        NeteaseGamesBaseLayout layout = NeteaseGamesBaseLayoutPlanner.Plan([current, stale], sizes);

        List<NeteaseGamesBaseAssembler.Step> steps =
            NeteaseGamesBaseAssembler.BuildSteps(layout, [current, stale], "v");

        Assert.Single(steps);
        Assert.Equal("resource.repository_1", steps[0].Chunk.Name);
    }

    /// <summary>
    ///     A chunk whose resources were all claimed by an earlier chunk contributes nothing and must not be written.
    /// </summary>
    [Fact]
    public void BuildSteps_SkipsAChunkWhoseResourcesWereAlreadyPlaced()
    {
        NeteaseGamesBigChunk first = Chunk("a_0", ("x", 10), ("y", 20));
        NeteaseGamesBigChunk duplicate = Chunk("a_1", ("x", 10), ("y", 20));

        Dictionary<string, long> sizes = Sizes(("x", 10), ("y", 20));
        NeteaseGamesBaseLayout layout = NeteaseGamesBaseLayoutPlanner.Plan([first, duplicate], sizes);

        Assert.Equal(2, layout.Placements.Count);

        List<NeteaseGamesBaseAssembler.Step> steps =
            NeteaseGamesBaseAssembler.BuildSteps(layout, [first, duplicate], "v");

        Assert.Single(steps);
        Assert.Equal("a_0", steps[0].Chunk.Name);
    }

    /// <summary>
    ///     The three standalone patch files never enter a shard: their chunks are not laid out at all, and the
    ///     remaining resources of the pool still form a contiguous layout.
    /// </summary>
    [Fact]
    public void Plan_LeavesLoosePatchFilesOutOfTheShardLayout()
    {
        NeteaseGamesBigChunk normal = Chunk("n_0", ("Char/a.anim", 100), ("Char/b.anim", 200));
        NeteaseGamesBigChunk loose = Chunk("prerequisite_0", (NeteaseGamesBaseLooseFiles.Prerequisite, 125));

        Dictionary<string, long> sizes = Sizes(
            ("Char/a.anim", 100), ("Char/b.anim", 200), (NeteaseGamesBaseLooseFiles.Prerequisite, 125));

        NeteaseGamesBaseLayout layout = NeteaseGamesBaseLayoutPlanner.Plan(
            [normal, loose], sizes, NeteaseGamesBaseLooseFiles.All);

        Assert.DoesNotContain(layout.Placements, p => p.Path == NeteaseGamesBaseLooseFiles.Prerequisite);
        Assert.Equal(2, layout.Placements.Count);
        Assert.Equal(300, layout.TotalSize);
        Assert.Equal(1, layout.ShardCount);
    }

    /// <summary>
    ///     The loose-path rule must not swallow ordinary single-resource chunks: 672 of them ('Fonts_1', 'CGs_0',
    ///     'Char_14', …) are real shard resources, and dropping them would silently lose content.
    /// </summary>
    [Fact]
    public void Plan_KeepsSingleResourceChunksThatAreNotLooseFiles()
    {
        NeteaseGamesBigChunk font = Chunk("Fonts_1", ("Fonts/fzlt_big_m.ttf", 17_770_684));

        Dictionary<string, long> sizes = Sizes(("Fonts/fzlt_big_m.ttf", 17_770_684));
        NeteaseGamesBaseLayout layout = NeteaseGamesBaseLayoutPlanner.Plan(
            [font], sizes, NeteaseGamesBaseLooseFiles.All);

        Assert.Single(layout.Placements);
        Assert.Equal("Fonts/fzlt_big_m.ttf", layout.Placements[0].Path);
    }

    /// <summary>
    ///     <c>essential</c> and <c>patch.repository</c> look like the standalone files but are shard resources (the
    ///     live index places <c>essential</c> at shard 126, offset 73,993,554), so they must stay in the layout.
    /// </summary>
    [Theory]
    [InlineData("essential")]
    [InlineData("patch.repository")]
    public void IsLoose_RejectsShardResourcesThatLookStandalone(string path)
        => Assert.False(NeteaseGamesBaseLooseFiles.IsLoose(path));

    /// <summary>The three measured standalone files are recognised.</summary>
    [Theory]
    [InlineData("prerequisite")]
    [InlineData("Misc/lazycode_zstd_dict")]
    [InlineData("4a/4a3101465e82fbbdc7e08f834fc82419")]
    public void IsLoose_AcceptsTheMeasuredStandaloneFiles(string path)
        => Assert.True(NeteaseGamesBaseLooseFiles.IsLoose(path));

    /// <summary>
    ///     A chunk whose announced size disagrees with its resources is skipped rather than written at wrong
    ///     offsets - the stale base manifest has 20 such chunks.
    /// </summary>
    [Fact]
    public void Plan_SkipsAChunkWhoseSizeDisagreesWithItsResources()
    {
        NeteaseGamesBigChunk broken = new("big_common", "broken_0", 999, ["Char/a.anim"], 0);

        Dictionary<string, long> sizes = Sizes(("Char/a.anim", 100));
        NeteaseGamesBaseLayout layout = NeteaseGamesBaseLayoutPlanner.Plan([broken], sizes);

        Assert.Empty(layout.Placements);
        Assert.Single(layout.SkippedChunks);
        Assert.Equal("broken_0", layout.SkippedChunks[0].Name);
    }

    /// <summary>
    ///     A chunk larger than a whole shard is split at resource boundaries instead of failing, and the layout
    ///     records that it is no longer a pure chunk pool.
    /// </summary>
    [Fact]
    public void Plan_SplitsAChunkLargerThanAShard()
    {
        long half = NeteaseGamesBaseLayoutPlanner.ShardCapacity / 2 + 1;
        NeteaseGamesBigChunk huge = Chunk("huge_0", ("a", half), ("b", half));

        Dictionary<string, long> sizes = Sizes(("a", half), ("b", half));
        NeteaseGamesBaseLayout layout = NeteaseGamesBaseLayoutPlanner.Plan([huge], sizes);

        Assert.True(layout.HasSplitChunk);
        Assert.Equal(2, layout.ShardCount);
        Assert.Equal(2, layout.Placements.Count);
    }

    /// <summary>
    ///     Shard files are named after their index: shard 0 is <c>Patch.mpk</c>, the rest are <c>Patch{id}.mpk</c>.
    /// </summary>
    [Fact]
    public void GetShardFileName_IsTheIdentity()
    {
        Assert.Equal("Patch.mpk", NeteaseGamesBaseLayoutPlanner.GetShardFileName(0));
        Assert.Equal("Patch1.mpk", NeteaseGamesBaseLayoutPlanner.GetShardFileName(1));
        Assert.Equal("Patch127.mpk", NeteaseGamesBaseLayoutPlanner.GetShardFileName(127));
    }
}
