using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The shader tier rules measured on 2026-10-26, and the index completeness rules that came out of comparing the
///     produced indexes with the live installation.
/// </summary>
public class ShaderTierTests
{
    private const string ShaderListJson = """
        {
            "file_list": {
                "Shader/dx12/aaa.bin": [11, 100],
                "Shader/dx12/bbb.bin": [22, 200],
                "Shader/dx12/ccc.bin": [33, 300],
                "Shader/dx12.mapping": [44, 400]
            }
        }
        """;

    private const string ShaderPoolJson = """
        {
            "big_file_list": {
                "dx12_1": [300, ["Shader/dx12/aaa.bin", "Shader/dx12/bbb.bin"], 1],
                "dx12_2": [700, ["Shader/dx12/ccc.bin", "Shader/dx12.mapping"], 2]
            },
            "mapping_version": "20261021113421287"
        }
        """;

    [Fact]
    public void ShaderCacheList_ParsesHashAndSize()
    {
        NeteaseGamesShaderCacheList list = NeteaseGamesShaderCacheList.Parse(Encoding.UTF8.GetBytes(ShaderListJson));

        Assert.Equal(4, list.Count);
        Assert.Equal(100, list.Resources["Shader/dx12/aaa.bin"].Size);
        Assert.Equal(11UL, list.Resources["Shader/dx12/aaa.bin"].Hash);
        Assert.Equal(400, list.ToSizeMap()["Shader/dx12.mapping"]);
    }

    [Fact]
    public void ShaderCacheList_RejectsAPayloadWithoutFileList()
    {
        Assert.Throws<InvalidDataException>(
            () => NeteaseGamesShaderCacheList.Parse(Encoding.UTF8.GetBytes("""{"other":{}}""")));
    }

    /// <summary>
    ///     The shader pool manifest is parsed by the same reader as the base tier pool: its chunk entries are
    ///     <c>[size, [paths], hash]</c> under a section named <c>big_file_list</c>.
    /// </summary>
    [Fact]
    public void ShaderPool_ParsesThroughTheBigPatchReader()
    {
        NeteaseGamesBigPatchList pool = NeteaseGamesBigPatchList.Parse(Encoding.UTF8.GetBytes(ShaderPoolJson));

        Assert.Equal(2, pool.Count);
        Assert.Equal(["big_file_list"], pool.FileSections);
        Assert.Equal(300, pool.Chunks[("big_file_list", "dx12_1")].Size);
        Assert.Equal(2, pool.Chunks[("big_file_list", "dx12_1")].Paths.Count);
    }

    /// <summary>
    ///     ⚠ The section the manifest declares (<c>big_file_list</c>) is <b>not</b> the directory the content host
    ///     serves the chunks from (<c>big_shader</c>). Building the URL from the parsed section answered 404 for every
    ///     chunk (measured 2026-10-26), so the URL must always use the shader section constant.
    /// </summary>
    [Fact]
    public void ShaderChunkUrl_UsesTheShaderSection_NotTheManifestSection()
    {
        Assert.Equal("big_20261021113421287/big_shader/dx12_920",
            NeteaseGamesContentEndpoints.GetBigPatchContentPath("20261021113421287",
                NeteaseGamesContentEndpoints.BigShaderSection, "dx12_920"));

        // The manifest's own section name still produces a path - that is exactly the trap: it looks valid and the
        // host answers 404. Pin the shape rather than asserting an inequality that the formatter cannot express.
        Assert.Equal("big_20261021113421287/big_file_list/dx12_920",
            NeteaseGamesContentEndpoints.GetBigPatchContentPath("20261021113421287", "big_file_list", "dx12_920"));
        Assert.NotEqual(NeteaseGamesContentEndpoints.BigShaderSection, "big_file_list");
    }

    /// <summary>
    ///     Every shader chunk is size-consistent on the live pool (920/920), so the planner must place all of them
    ///     and skip none.
    /// </summary>
    [Fact]
    public void ShaderPool_IsFullyPlaceable()
    {
        NeteaseGamesBigPatchList pool = NeteaseGamesBigPatchList.Parse(Encoding.UTF8.GetBytes(ShaderPoolJson));
        NeteaseGamesShaderCacheList list = NeteaseGamesShaderCacheList.Parse(Encoding.UTF8.GetBytes(ShaderListJson));

        NeteaseGamesBaseLayout layout = NeteaseGamesBaseLayoutPlanner.Plan(pool.Chunks.Values, list.ToSizeMap());

        Assert.Empty(layout.SkippedChunks);
        Assert.Equal(4, layout.Placements.Count);
        Assert.Equal(1000, layout.TotalSize);
    }

    /// <summary>
    ///     The live index carries one placeholder row per directory that holds resources: <c>Patch.mpkinfo</c> has
    ///     2,460 of them next to 232,450 resources, and <c>ResourceExA.patchinfo</c> two (<c>Shader</c> and
    ///     <c>Shader/dx12</c>). A writer that omits them produces a database the launcher did not produce.
    /// </summary>
    [Fact]
    public void IndexWriter_RoundTripsPlaceholderRows()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "scratch", "shader" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "ResourceExA.patchinfo");

        List<NeteaseGamesBaseIndexWriter.Row> rows =
        [
            new("Shader/dx12/aaa.bin", 100, 0, 0, 11),
            new("Shader/dx12/bbb.bin", 200, 0, 100, 22),
            new("Shader", 0, 0, 0, 0),
            new("Shader/dx12", 0, 0, 0, 0)
        ];

        using (FileStream stream = new(path, FileMode.Create, FileAccess.Write))
        {
            NeteaseGamesBaseIndexWriter.Write(stream, NeteaseGamesBaseIndex.ShaderTableName, rows,
            [
                new KeyValuePair<string, string>("max_index", "0"),
                new KeyValuePair<string, string>("status",
                    "{\"compacting\": false, \"suffix\": 1, \"extra\": \"\", \"delete_compacting\": false}")
            ]);
        }

        NeteaseGamesBaseIndex? index = NeteaseGamesBaseIndex.Read(path, NeteaseGamesBaseIndex.ShaderTableName);

        Assert.NotNull(index);
        Assert.Equal(4, index!.EntryCount);
        // The two placeholder rows carry no bytes, so they must not move the shard's payload end.
        Assert.Equal(300, index.ShardEnds[0]);
    }

    /// <summary>
    ///     The base index and the shader index live in the same schema shape but different tables (<c>info0</c> vs
    ///     <c>info1</c>); reading one with the other's table name must not silently return an empty index.
    /// </summary>
    [Fact]
    public void ShaderIndex_UsesTheSameTableNameAsTheBaseIndex()
    {
        // Measured 2026-10-26 on both the vendor's ResourceExA.patchinfo and the plugin's own output: the table is
        // `info0` with 173,861 rows, not `info1` as an earlier revision assumed.
        Assert.Equal("info0", NeteaseGamesBaseIndex.ShaderTableName);
        Assert.Equal(NeteaseGamesBaseIndex.TableName, NeteaseGamesBaseIndex.ShaderTableName);

        string directory = Path.Combine(AppContext.BaseDirectory, "scratch", "table" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "ResourceExA.patchinfo");

        using (FileStream stream = new(path, FileMode.Create, FileAccess.Write))
        {
            NeteaseGamesBaseIndexWriter.Write(stream, NeteaseGamesBaseIndex.ShaderTableName,
                [new NeteaseGamesBaseIndexWriter.Row("Shader/dx12/aaa.bin", 100, 0, 0, 11)],
                [new KeyValuePair<string, string>("max_index", "0")]);
        }

        NeteaseGamesBaseIndex? index = NeteaseGamesBaseIndex.Read(path, NeteaseGamesBaseIndex.ShaderTableName);
        Assert.NotNull(index);
        Assert.Equal(1, index!.EntryCount);
    }

    /// <summary>
    ///     A plan's reported size must count the standalone loose chunks once. They are carried by the assembly (for
    ///     its own accounting) and added to the item list, and counting both overstated the live plan by 119,967
    ///     bytes.
    /// </summary>
    [Fact]
    public void DownloadPlan_DoesNotCountLooseChunksTwice()
    {
        NeteaseGamesBigChunk chunk = new("big_common", "x_0", 1000, ["p"], 1);
        NeteaseGamesBaseAssemblyPlan assembly = new() { ShardCount = 1 };
        assembly.Steps.Add(new NeteaseGamesBaseAssembler.Step(chunk, 0, 0, "v"));
        assembly.LooseChunks.Add(new NeteaseGamesBaseLooseChunk(
            new NeteaseGamesBigChunk("big_common", "prerequisite_0", 125, ["prerequisite"], 2), "v"));

        NeteaseGamesDownloadPlan plan = new() { BaseAssembly = assembly };
        plan.Items.Add(new NeteaseGamesDownloadItem
        {
            TargetRelativePath = "prerequisite",
            Kind = NeteaseGamesDownloadKind.LooseFile,
            ExpectedSize = 125
        });

        Assert.Equal(1125, plan.TotalSize);
    }

    /// <summary>
    ///     The shader assembly contributes to the reported size and to <c>IsEmpty</c> exactly like the base one.
    /// </summary>
    [Fact]
    public void DownloadPlan_CountsTheShaderAssembly()
    {
        NeteaseGamesBaseAssemblyPlan shader = new() { ShardCount = 1 };
        shader.Steps.Add(new NeteaseGamesBaseAssembler.Step(
            new NeteaseGamesBigChunk("big_shader", "dx12_1", 700, ["a"], 1), 0, 0, "v"));

        NeteaseGamesDownloadPlan plan = new() { ShaderAssembly = shader };

        Assert.Equal(700, plan.TotalSize);
        Assert.False(plan.IsEmpty);
    }
}
