using System;
using System.IO;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     <see cref="NeteaseGamesGameInstaller.FindTruncatedBaseShards" /> checks the base tier against its own index.
///     A missing <i>index</i> means "unknown" (nothing can be judged); a missing or short <i>shard</i> is a real
///     defect and has to be reported, otherwise a half-present installation passes as complete.
/// </summary>
public class BaseShardTruncationTests
{
    private static string CreateScratchInstall()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "scratch", "base-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, NeteaseGamesPaths.PatchDirectoryRelativePath));
        return root;
    }

    private static string PatchDir(string root)
        => Path.Combine(root, NeteaseGamesPaths.PatchDirectoryRelativePath);

    [Fact]
    public void WithoutAnIndex_NothingIsReported()
    {
        string root = CreateScratchInstall();
        try
        {
            File.WriteAllBytes(Path.Combine(PatchDir(root), "Patch1.mpk"), new byte[64]);
            Assert.Empty(NeteaseGamesGameInstaller.FindTruncatedBaseShards(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingShards_AreNotReportedWhenThereIsNoIndex()
    {
        // Without an index nothing can be judged: the empty result means "unknown", not "healthy".
        string root = CreateScratchInstall();
        try
        {
            Assert.Empty(NeteaseGamesGameInstaller.FindTruncatedBaseShards(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EmptyPath_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => NeteaseGamesGameInstaller.FindTruncatedBaseShards(string.Empty));
    }

    /// <summary>
    ///     The index file name and table name are part of the contract with the installation; changing them
    ///     silently would disable the check.
    /// </summary>
    [Fact]
    public void IndexContract_MatchesTheInstallation()
    {
        Assert.Equal("Patch.mpkinfo", NeteaseGamesBaseIndex.FileName);
        Assert.Equal("info0", NeteaseGamesBaseIndex.TableName);
    }

    /// <summary>
    ///     Shard 0 is <c>Patch.mpk</c>; every other id is <c>Patch{id}.mpk</c>. Getting this wrong made the
    ///     check look at <c>Patch0.mpk</c>, which never exists, and the shard was skipped as "missing".
    /// </summary>
    [Theory]
    [InlineData(0, "Patch.mpk")]
    [InlineData(1, "Patch1.mpk")]
    [InlineData(54, "Patch54.mpk")]
    [InlineData(127, "Patch127.mpk")]
    public void BaseShardFileName_MatchesTheInstallationLayout(int shardId, string expected)
    {
        Assert.Equal(expected, NeteaseGamesGameInstaller.BaseShardFileName(shardId));
    }
}
