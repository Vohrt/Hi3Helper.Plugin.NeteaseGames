using System;
using System.Collections.Generic;
using System.IO;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The remote manifest is four header lines followed by JSON. <b>Line 0 is the content version</b> - the
///     directory every object is downloaded from - and <b>line 3 is the patch version</b> that decides whether an
///     installation is current. <c>@metadata@.@base_version@</c> is neither: requesting objects under it returns
///     older packages (measured 2026-10-22: 13,283,727 bytes instead of the installed 15,767,230).
/// </summary>
public class ManifestParsingTests
{
    /// <summary>The live manifest of 2026-10-22, trimmed to the relevant fields.</summary>
    private const string LiveManifest = """
        20261022122731826
        16867045629764259494
        0
        6582435
        {"normal":{"mininum_pkgversion":0,"review_pkgversion":[],"new_package_url":"","latest_pkgversion":5282637},"@metadata@":{"@base_version@":"20261021113421287","@private_res_version@":6582435,"@private_patch_version@":6582435,"@private_script_version@":6582435,"@engine_version@":"261019222124_71902dc10_release","@patch_branch@":"release"}}
        """;

    [Fact]
    public void ContentVersion_ComesFromLineZero()
    {
        NeteaseGamesPatchManifest manifest = NeteaseGamesPatchManifest.Parse(LiveManifest);

        Assert.Equal("20261022122731826", manifest.ContentVersion);
        // The metadata value is a different number and must not be used as the download directory.
        Assert.Equal("20261021113421287", manifest.BaseVersion);
        Assert.NotEqual(manifest.ContentVersion, manifest.BaseVersion);
    }

    [Fact]
    public void PatchVersion_ComesFromLineThree()
    {
        NeteaseGamesPatchManifest manifest = NeteaseGamesPatchManifest.Parse(LiveManifest);

        Assert.Equal(6582435, manifest.PatchVersion);
        // `normal.latest_pkgversion` carries a stale value of another branch and is never read.
        Assert.NotEqual(5282637, manifest.PatchVersion);
    }

    [Fact]
    public void PatchListHash_ComesFromLineOne()
    {
        Assert.Equal(16867045629764259494UL, NeteaseGamesPatchManifest.Parse(LiveManifest).PatchListHash);
    }

    [Fact]
    public void EngineVersion_IsReadFromMetadata()
    {
        Assert.Equal("261019222124_71902dc10_release",
            NeteaseGamesPatchManifest.Parse(LiveManifest).EngineVersion);
    }

    [Fact]
    public void IsUsable_RequiresAContentVersionAndAPatchVersion()
    {
        Assert.True(NeteaseGamesPatchManifest.Parse(LiveManifest).IsUsable);

        // No header at all: nothing can be planned.
        Assert.False(NeteaseGamesPatchManifest.Parse("""{"@metadata@":{}}""").IsUsable);

        // Header without a usable version on line 3 and without the metadata copy.
        Assert.False(NeteaseGamesPatchManifest.Parse("a\nb\nc\nd\n{}").IsUsable);
    }

    [Fact]
    public void PatchVersion_FallsBackToMetadataWhenLineThreeIsMissing()
    {
        // A manifest without the header still announces the version inside the metadata block.
        const string withoutHeader = """
            {"@metadata@":{"@private_res_version@":6534552,"@base_version@":"20261014065508363"}}
            """;

        Assert.Equal(6534552, NeteaseGamesPatchManifest.Parse(withoutHeader).PatchVersion);
    }

    [Fact]
    public void MalformedManifest_DoesNotThrow()
    {
        NeteaseGamesPatchManifest manifest = NeteaseGamesPatchManifest.Parse("not a manifest at all");

        Assert.False(manifest.IsUsable);
        Assert.Equal(0, manifest.PatchVersion);
        Assert.Equal(string.Empty, manifest.ContentVersion);
    }

    [Fact]
    public void EngineFileList_IsParsed_WithPathHashAndSize()
    {
        // The live format: each download block is a two element array [md5, size].
        const string text =
            """
            {"download_info":[["abc",100],["def",200]],
             "loose_file_parent_dir":"loose_file/x","engine_version":"v1",
             "data_name":"d","current_version":"1","parent_dir":"p"}
            """;

        NeteaseGamesMiniPackManifest manifest = NeteaseGamesMiniPackManifest.Parse(
            System.Text.Encoding.UTF8.GetBytes(text));

        Assert.Equal("loose_file/x", manifest.LooseFileParentDir);
        Assert.Equal(2, manifest.DownloadInfo.Count);
        Assert.Equal("abc", manifest.DownloadInfo[0].Md5);
        Assert.Equal(100, manifest.DownloadInfo[0].Size);
    }
}

/// <summary>
///     Package entries in the patch list end with the package size; every preceding number is a chunk hash.
/// </summary>
public class PatchListPackageEntryTests
{
    [Fact]
    public void LastNumberIsThePackageSize()
    {
        // Shape taken from the live list: Patch2000.mpk with chunk hashes and a trailing size.
        const string json = """
            {"mpk_medium":{"Patch2000.mpk":[3996881272972637595,7003971163209048021,15629001]},
             "file_sections":["mpk_medium"]}
            """;

        NeteaseGamesPatchList list = NeteaseGamesPatchList.Parse(System.Text.Encoding.UTF8.GetBytes(json));

        NeteaseGamesPackageEntry entry = list.Packages["Patch2000.mpk"];
        Assert.Equal(15629001, entry.TotalSize);
        // The size is not kept among the hashes.
        Assert.Equal(2, entry.Hashes.Count);
        Assert.DoesNotContain(15629001UL, entry.Hashes);
        Assert.Equal("mpk_medium", entry.Section);
    }

    [Fact]
    public void LooseSectionsAreNotPackages()
    {
        // `common` / `windows64_common` describe virtual engine paths stored inside the packages; they are not
        // downloadable objects and must never be planned.
        const string json = """
            {"common":{"Char/Prop/base_sp.skeleton":[111,222]},
             "windows64_common":{"00/abc":[333,444]},
             "mpk_script":{"Patch1000.mpk":[555,666]},
             "file_sections":["common","windows64_common","mpk_script"]}
            """;

        NeteaseGamesPatchList list = NeteaseGamesPatchList.Parse(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Single(list.Packages);
        Assert.True(list.Packages.ContainsKey("Patch1000.mpk"));
        Assert.Equal(2, list.LooseFiles.Count);
    }
}
