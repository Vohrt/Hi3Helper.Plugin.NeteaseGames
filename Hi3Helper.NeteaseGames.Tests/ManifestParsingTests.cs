using System;
using System.Collections.Generic;
using System.IO;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

public class ManifestParsingTests
{
    [Fact]
    public void EngineFileList_IsParsed_WithPathHashAndSize()
    {
        const string text =
            "Binaries\\Win64Release\\amd_fidelityfx_dx12.dll.1 15902446965336017002 6647504\r\n" +
            "Binaries\\Win64Release\\cclibdll.dll.1 4977914232404058321 183016\n" +
            "\n";

        IReadOnlyList<NeteaseGamesEngineFile> files = NeteaseGamesEnginePatcher.ParseEngineFileList(text);

        Assert.Equal(2, files.Count);
        Assert.Equal("Binaries\\Win64Release\\amd_fidelityfx_dx12.dll.1", files[0].RelativePath);
        Assert.Equal(15902446965336017002UL, files[0].Hash);
        Assert.Equal(6647504, files[0].Size);

        // The payload stores the newest generation with a ".1" suffix; the game uses the plain name.
        Assert.Equal("Binaries\\Win64Release\\amd_fidelityfx_dx12.dll", files[0].TargetName);
        Assert.Equal("Binaries/Win64Release/amd_fidelityfx_dx12.dll.1", files[0].UrlPath);
    }

    [Fact]
    public void EngineFileList_IgnoresMalformedLines()
    {
        const string text = "no-spaces\npath 123\npath notanumber 5\npath 1 2\n";

        IReadOnlyList<NeteaseGamesEngineFile> files = NeteaseGamesEnginePatcher.ParseEngineFileList(text);

        Assert.Single(files);
        Assert.Equal("path", files[0].RelativePath);
    }

    [Fact]
    public void EngineFileList_DetectsDeleteMarkers()
    {
        NeteaseGamesEngineFile marker = new("used_del_dir/somefile", 0, 0);
        Assert.True(NeteaseGamesEnginePatcher.IsDeleteMarker(marker));
    }

    [Fact]
    public void PatchManifest_ReadsPackageVersionAndBaseVersion()
    {
        // A trimmed copy of the live document: the authoritative version is @private_res_version@, while
        // normal.latest_pkgversion carries a different (stale, other branch) value.
        const string text =
            "20261016125738025\n" +
            "7586518639554130336\n" +
            "0\n" +
            "6534552\n" +
            "{\"normal\":{\"latest_pkgversion\":5282637},\"@metadata@\":{\"@base_version@\":20261014065508363," +
            "\"@private_res_version@\":\"6534552\",\"@engine_version@\":\"261014173721_4a476f25b_release\"}}";

        NeteaseGamesPatchManifest manifest = NeteaseGamesPatchManifest.Parse(text);

        Assert.Equal(6534552, manifest.LatestPackageVersion);
        Assert.NotEqual(5282637, manifest.LatestPackageVersion);
        Assert.Equal("20261014065508363", manifest.BaseVersion);
        Assert.Equal("261014173721_4a476f25b_release", manifest.EngineVersion);
        Assert.Equal(7586518639554130336UL, manifest.PatchListHash);
    }

    [Fact]
    public void PatchManifest_FallsBackToTheVersionLine()
    {
        // No @private_res_version@ in the payload: the fourth line still carries the same number.
        const string text =
            "20261016125738025\n7586518639554130336\n0\n6534552\n{\"normal\":{\"latest_pkgversion\":5282637}}";

        NeteaseGamesPatchManifest manifest = NeteaseGamesPatchManifest.Parse(text);

        Assert.Equal(6534552, manifest.LatestPackageVersion);
    }

    [Fact]
    public void PatchManifest_ToleratesMissingJson()
    {
        NeteaseGamesPatchManifest manifest = NeteaseGamesPatchManifest.Parse("20261016125738025\n42\n");

        Assert.Equal(42UL, manifest.PatchListHash);
        Assert.Equal(0, manifest.LatestPackageVersion);
        Assert.Equal(string.Empty, manifest.BaseVersion);
    }

    [Fact]
    public void PatchManifest_ToleratesBrokenJson()
    {
        NeteaseGamesPatchManifest manifest = NeteaseGamesPatchManifest.Parse("a\n1\n0\n0\n{not json");

        Assert.Equal(1UL, manifest.PatchListHash);
        Assert.Equal(0, manifest.LatestPackageVersion);
    }

    [Fact]
    public void PatchList_SplitsLooseFilesPackagesAndSizes()
    {
        const string json = """
            {
              "mapping_version": "20261014065508363",
              "common": { "a/b.txt": [111, 22] },
              "windows64_common": { "c/d.txt": [333, 44] },
              "mpk_script": { "script.mpkinfo": [1, 2, 3, 999] },
              "mpk_medium": { "Patch2000.mpk": [7, 888] },
              "file_sections": ["common", "windows64_common"],
              "mpkexclude": ["prerequisite", "Misc/lazycode_zstd_dict"]
            }
            """;

        NeteaseGamesPatchList list = NeteaseGamesPatchList.Parse(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Equal("20261014065508363", list.MappingVersion);
        Assert.Equal(2, list.LooseFiles.Count);
        Assert.Equal(111UL, list.LooseFiles["a/b.txt"].Hash);
        Assert.Equal(22, list.LooseFiles["a/b.txt"].Size);
        Assert.Equal(2, list.Packages.Count);

        // The last number of a package array is its size, the others are the hashes of the contained files.
        NeteaseGamesPackageEntry script = list.Packages["script.mpkinfo"];
        Assert.Equal(999, script.TotalSize);
        Assert.Equal([1UL, 2UL, 3UL], script.Hashes);
        Assert.Equal("mpk_script", script.Section);

        Assert.Equal("mpk_medium", list.Packages["Patch2000.mpk"].Section);
        Assert.Equal(2, list.MpkExclude.Count);
        Assert.Equal(2, list.FileSections.Count);
    }

    [Fact]
    public void PatchList_HashComparisonIsOrderSensitive()
    {
        List<ulong> left = [1, 2, 3];
        List<ulong> right = [1, 2, 3];
        List<ulong> different = [1, 2, 4];

        Assert.True(NeteaseGamesPatchList.HashesEqual(left, right));
        Assert.False(NeteaseGamesPatchList.HashesEqual(left, different));
        Assert.False(NeteaseGamesPatchList.HashesEqual(left, [1, 2]));
    }
}
