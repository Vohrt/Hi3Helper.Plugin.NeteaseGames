using System;
using System.Collections.Generic;
using System.IO;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     A plan is built for one of three purposes: an update (only what changed), a fresh installation (every
///     object) and a repair (everything that is missing or truncated on disk).
/// </summary>
public class PlanModeTests
{
    [Fact]
    public void Modes_AreDistinct()
    {
        Assert.NotEqual(NeteaseGamesPlanMode.Update, NeteaseGamesPlanMode.Install);
        Assert.NotEqual(NeteaseGamesPlanMode.Install, NeteaseGamesPlanMode.Repair);
        Assert.NotEqual(NeteaseGamesPlanMode.Update, NeteaseGamesPlanMode.Repair);
    }

    [Fact]
    public void IsLocalFileComplete_TreatsMissingFileAsIncomplete()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "missing-" + Guid.NewGuid().ToString("N") + ".bin");

        Assert.False(NeteaseGamesGameInstaller.IsLocalFileComplete(path, 100, verifyHash: false));
    }

    [Fact]
    public void IsLocalFileComplete_ComparesTheSize()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "size-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, new byte[100]);

            Assert.True(NeteaseGamesGameInstaller.IsLocalFileComplete(path, 100, verifyHash: false));
            // A truncated file (an interrupted download) must be re-fetched.
            Assert.False(NeteaseGamesGameInstaller.IsLocalFileComplete(path, 200, verifyHash: false));
            // A file from an older version has a different size as well.
            Assert.False(NeteaseGamesGameInstaller.IsLocalFileComplete(path, 50, verifyHash: false));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void IsLocalFileComplete_WithoutAnExpectedSizeAcceptsAnExistingFile()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "nosize-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, new byte[10]);

            // The manifest did not announce a size, so there is nothing to compare against.
            Assert.True(NeteaseGamesGameInstaller.IsLocalFileComplete(path, 0, verifyHash: false));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void DownloaderExposesAParallelismDefault()
    {
        // A fresh installation downloads hundreds of objects; the default has to be above one.
        Assert.True(NeteaseGamesDownloader.DefaultParallelism > 1);
        Assert.True(NeteaseGamesDownloader.DefaultParallelism <= 8);
    }

    [Fact]
    public void PlanPackageIndexDetection_StillWorksWithRepair()
    {
        // The repair path relies on the same classification as the update path.
        Assert.True(NeteaseGamesGameInstaller.IsPackageIndex("Patch.mpkinfo"));
        Assert.True(NeteaseGamesGameInstaller.IsPackageIndex("script.mpkinfo"));
        Assert.False(NeteaseGamesGameInstaller.IsPackageIndex("Patch2000.mpk"));
    }
}

/// <summary>
///     The loose file section of the patch list is a set of <b>virtual engine paths</b> (Char/…, Sounds/…),
///     not files: the content lives inside the mpk packages. A plan must therefore never schedule them.
/// </summary>
public class VirtualResourcePathTests
{
    [Fact]
    public void PatchList_LooseFilesAreReportedSeparatelyFromPackages()
    {
        const string json = """
            {
              "common": { "Char/Prop/base_sp.skeleton": [111, 222] },
              "mpk_medium": { "Patch2000.mpk": [7, 888] },
              "file_sections": ["common", "mpk_medium"]
            }
            """;

        NeteaseGamesPatchList list = NeteaseGamesPatchList.Parse(System.Text.Encoding.UTF8.GetBytes(json));

        // The loose entries are parsed (they describe the engine content) ...
        Assert.Single(list.LooseFiles);
        Assert.Equal(222, list.LooseFiles["Char/Prop/base_sp.skeleton"].Size);
        // ... but only the package is a downloadable object.
        Assert.Single(list.Packages);
        Assert.Equal("mpk_medium", list.Packages["Patch2000.mpk"].Section);
    }
}
