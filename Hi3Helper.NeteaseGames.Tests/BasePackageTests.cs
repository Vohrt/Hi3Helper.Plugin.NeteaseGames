using System;
using System.IO;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     A fresh installation is not supported yet. The base packages (Patch.mpk, Patch1..127, ResourceExA..) are
///     not part of the patch list and every content host path tried for them answers 404, so the installer has to
///     refuse rather than report a completed installation that cannot start.
/// </summary>
public class FreshInstallGuardTests
{
    [Fact]
    public void Plan_CarriesTheFreshInstallFlag()
    {
        NeteaseGamesDownloadPlan plan = new() { IsFreshInstallUnsupported = true };

        Assert.True(plan.IsFreshInstallUnsupported);
    }

    [Fact]
    public void Plan_DefaultsToSupported()
    {
        // Update and Repair never set the flag.
        Assert.False(new NeteaseGamesDownloadPlan().IsFreshInstallUnsupported);
    }

    [Fact]
    public void Plan_DistinguishesAnUnavailableRemoteListFromAnEmptyPlan()
    {
        // An empty plan means "nothing to do"; an unavailable list means "unknown". Conflating them made the
        // installer report a healthy installation it never verified.
        NeteaseGamesDownloadPlan empty = new();
        Assert.True(empty.IsEmpty);
        Assert.False(empty.IsRemoteListUnavailable);

        NeteaseGamesDownloadPlan unknown = new() { IsRemoteListUnavailable = true };
        Assert.True(unknown.IsEmpty);
        Assert.True(unknown.IsRemoteListUnavailable);
    }
}

/// <summary>
///     Only the packages of the mpk sections are downloadable objects; the indexes are classified separately so
///     they are written last.
/// </summary>
public class PackageClassificationTests
{
    [Theory]
    [InlineData("script.mpkinfo", true)]
    [InlineData("s_file.mpkinfo", true)]
    [InlineData("indep.mpkinfo", true)]
    [InlineData("Patch1000.mpk", false)]
    [InlineData("Patch2000.mpk", false)]
    public void IndexDetection(string name, bool expected)
    {
        Assert.Equal(expected, NeteaseGamesGameInstaller.IsPackageIndex(name));
    }
}
