using System;
using Hi3Helper.NeteaseGames.Core;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The mapping from an engine payload entry to its path in the game directory. The live branch carries ten
///     <c>used_del_dir\mpk_res\…</c> entries that read like removals but are the root level resource packages, so
///     the mapping is pinned here.
/// </summary>
public class EnginePayloadPathTests
{
    /// <summary>
    ///     An ordinary entry lands below <c>Engine\</c> with the <c>.1</c> generation suffix dropped.
    /// </summary>
    [Theory]
    [InlineData(@"Binaries\Win64Release\cclibdll.dll.1", "Engine/Binaries/Win64Release/cclibdll.dll")]
    [InlineData(@"Config\Built.version.1", "Engine/Config/Built.version")]
    [InlineData(@"Binaries\Win64Release\Web\devtools_resources.pak.1",
        "Engine/Binaries/Win64Release/Web/devtools_resources.pak")]
    public void ResolveTargetPath_PutsOrdinaryEntriesUnderEngine(string relativePath, string expected)
    {
        NeteaseGamesEngineFile file = new(relativePath, 0, 0);
        Assert.Equal(expected, NeteaseGamesEnginePatcher.ResolveTargetPath(file));
    }

    /// <summary>
    ///     A <c>used_del_dir\mpk_res\</c> entry lands in the <b>game root</b>, with both the directory prefix and the
    ///     generation suffix removed. Measured 2026-10-26: the downloaded bytes have the exact MD5 of the file the
    ///     live installation keeps at that root path.
    /// </summary>
    [Theory]
    [InlineData(@"used_del_dir\mpk_res\ResourceExA.mpkinfo.1", "ResourceExA.mpkinfo")]
    [InlineData(@"used_del_dir\mpk_res\ResourceExB.mpkinfo.1", "ResourceExB.mpkinfo")]
    [InlineData(@"used_del_dir\mpk_res\Resources.mpkinfo.1", "Resources.mpkinfo")]
    [InlineData(@"used_del_dir\mpk_res\Resources2.mpk.1", "Resources2.mpk")]
    [InlineData(@"used_del_dir\mpk_res\mpk_filelist.info.1", "mpk_filelist.info")]
    [InlineData(@"used_del_dir\mpk_res\package_resource.info.1", "package_resource.info")]
    public void ResolveTargetPath_PutsRootResourcePackagesAtTheGameRoot(string relativePath, string expected)
    {
        NeteaseGamesEngineFile file = new(relativePath, 0, 0);
        Assert.Equal(expected, NeteaseGamesEnginePatcher.ResolveTargetPath(file));
    }

    /// <summary>
    ///     No live entry is a removal request: all ten <c>used_del_dir</c> entries deliver files. Anything that acts
    ///     on <see cref="NeteaseGamesEnginePatcher.IsDeleteMarker" /> by skipping would drop them, which is what an
    ///     earlier revision did.
    /// </summary>
    [Fact]
    public void TheLiveUsedDelDirEntriesAreFilesNotRemovals()
    {
        string[] live =
        [
            @"used_del_dir\mpk_res\mpk_filelist.info.1",
            @"used_del_dir\mpk_res\package_resource.info.1",
            @"used_del_dir\mpk_res\ResourceExA.mpk.1",
            @"used_del_dir\mpk_res\ResourceExA.mpkinfo.1",
            @"used_del_dir\mpk_res\ResourceExB.mpk.1",
            @"used_del_dir\mpk_res\ResourceExB.mpkinfo.1",
            @"used_del_dir\mpk_res\Resources.mpk.1",
            @"used_del_dir\mpk_res\Resources.mpkinfo.1",
            @"used_del_dir\mpk_res\Resources1.mpk.1",
            @"used_del_dir\mpk_res\Resources2.mpk.1"
        ];

        foreach (string path in live)
        {
            NeteaseGamesEngineFile file = new(path, 0, 0);

            // Every one of them resolves to a real file name in the game root...
            string target = NeteaseGamesEnginePatcher.ResolveTargetPath(file);
            Assert.DoesNotContain("used_del_dir", target, StringComparison.Ordinal);
            Assert.DoesNotContain("/", target);
            Assert.DoesNotContain(".1", target, StringComparison.Ordinal);
        }
    }
}
