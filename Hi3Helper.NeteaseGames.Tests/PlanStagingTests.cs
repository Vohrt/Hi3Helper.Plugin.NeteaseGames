using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     Staged paths. A 60 GB fresh installation failed at the very end with
///     <c>ArgumentException: Argument_EmptyString Arg_ParamName_Name, path</c> (measured 2026-10-26) because
///     <c>BuildPlanAsync</c> assigned the staged paths <b>before</b> the base tier appended its three standalone
///     loose chunks, so those items carried an empty <c>StagedPath</c> until the downloader opened them.
/// </summary>
public class PlanStagingTests
{
    private static NeteaseGamesDownloadItem Item(string target, long size = 10)
        => new()
        {
            RelativePath = "remote/" + target.Replace('\\', '/'),
            TargetRelativePath = target,
            Kind = NeteaseGamesDownloadKind.Package,
            ExpectedSize = size
        };

    /// <summary>Every item gets a non-empty, distinct staged path.</summary>
    [Fact]
    public void AssignStaging_GivesEveryItemAPath()
    {
        NeteaseGamesDownloadPlan plan = new() { StagingDirectory = "stage" };
        plan.Items.Add(Item("Patch1000.mpk"));
        plan.Items.Add(Item("script.mpkinfo"));
        plan.Items.Add(Item("shader.bin"));

        plan.AssignStaging("stage");

        Assert.All(plan.Items, item => Assert.False(string.IsNullOrEmpty(item.StagedPath)));
        Assert.Equal(plan.Items.Count, plan.Items.Select(i => i.StagedPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    ///     The three loose chunks of the base tier are appended <i>after</i> the ordinary items; they must still be
    ///     staged. This is the exact shape that crashed: an item added after the staging pass.
    /// </summary>
    [Fact]
    public void AssignStaging_CoversItemsAppendedAfterTheOrdinaryOnes()
    {
        NeteaseGamesDownloadPlan plan = new() { StagingDirectory = "stage" };
        plan.Items.Add(Item("Patch1000.mpk"));
        // appended last, exactly like AppendBaseTierItems does
        plan.Items.Add(new NeteaseGamesDownloadItem
        {
            RelativePath = "big_v/big_common/prerequisite_0",
            TargetRelativePath = "prerequisite",
            Kind = NeteaseGamesDownloadKind.LooseFile,
            ExpectedSize = 125
        });

        plan.AssignStaging("stage");

        NeteaseGamesDownloadItem loose = plan.Items[^1];
        Assert.False(string.IsNullOrEmpty(loose.StagedPath));
        Assert.Equal(Path.Combine("stage", "000001_prerequisite"), loose.StagedPath);
    }

    /// <summary>
    ///     An item whose target name is empty (a side file written by name only through its remote path) still gets a
    ///     real staged name instead of <c>stage/000003_</c>.
    /// </summary>
    [Fact]
    public void AssignStaging_FallsBackToTheRemoteName()
    {
        NeteaseGamesDownloadPlan plan = new() { StagingDirectory = "stage" };
        plan.Items.Add(new NeteaseGamesDownloadItem
        {
            RelativePath = "20261025174155378/shadercachelist_dx12.txt",
            TargetRelativePath = string.Empty,
            Kind = NeteaseGamesDownloadKind.SideFile
        });

        plan.AssignStaging("stage");

        Assert.Equal(Path.Combine("stage", "000000_shadercachelist_dx12.txt"), plan.Items[0].StagedPath);
    }

    /// <summary>
    ///     The work-unit total is what the host shows as "N / M": it has to include the assembled chunks, otherwise
    ///     the badge stays at "0 / 342" while 11,000 chunks are downloaded.
    /// </summary>
    [Fact]
    public void TotalWorkUnits_CountsPlanItemsAndAssembledChunks()
    {
        NeteaseGamesBaseAssemblyPlan baseAssembly = new() { ShardCount = 1 };
        baseAssembly.Steps.Add(new NeteaseGamesBaseAssembler.Step(
            new NeteaseGamesBigChunk("big_common", "a_0", 10, ["a"], 1), 0, 0, "v"));
        baseAssembly.Steps.Add(new NeteaseGamesBaseAssembler.Step(
            new NeteaseGamesBigChunk("big_common", "a_1", 10, ["b"], 2), 0, 10, "v"));

        NeteaseGamesBaseAssemblyPlan shaderAssembly = new() { ShardCount = 1 };
        shaderAssembly.Steps.Add(new NeteaseGamesBaseAssembler.Step(
            new NeteaseGamesBigChunk("big_shader", "dx12_1", 10, ["c"], 3), 0, 0, "v"));

        NeteaseGamesDownloadPlan plan = new()
        {
            BaseAssembly = baseAssembly,
            ShaderAssembly = shaderAssembly
        };
        plan.Items.Add(Item("Patch1000.mpk"));
        plan.Items.Add(Item("script.mpkinfo"));

        Assert.Equal(5, plan.TotalWorkUnits);
    }

    /// <summary>A plan with no assemblies reports only its items.</summary>
    [Fact]
    public void TotalWorkUnits_WithoutAssembliesIsTheItemCount()
    {
        NeteaseGamesDownloadPlan plan = new();
        plan.Items.Add(Item("Patch1000.mpk"));

        Assert.Equal(1, plan.TotalWorkUnits);
    }
}
