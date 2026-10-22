using System;
using System.Collections.Generic;
using System.IO;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     A fresh installation cannot be completed by downloading patch shards alone.
///     <para>
///         Verified against the live content host: it serves the patch shards (<c>Patch1000.mpk</c> under
///         <c>mpk_script</c>) but answers 404 for every base package (<c>Patch.mpk</c>, <c>Patch1..123</c>,
///         <c>ResourceExA..</c>) in every section. Those packages are 59 GB of a 64 GB installation and are not
///         described by any manifest the plugin can read, so the installer has to refuse instead of reporting a
///         completed installation that cannot start.
///     </para>
/// </summary>
public class BasePackageTests
{
    private static string CreateInstallation(bool withBasePackages)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "base-" + Guid.NewGuid().ToString("N"));
        string patchDirectory = Path.Combine(root, NeteaseGamesPaths.PatchDirectoryRelativePath);
        Directory.CreateDirectory(patchDirectory);

        if (withBasePackages)
        {
            File.WriteAllBytes(Path.Combine(patchDirectory, NeteaseGamesGameInstaller.BasePackageName), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(patchDirectory, "ResourceExA.mpk"), [4, 5, 6]);
        }

        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Harmless leftover.
        }
    }

    [Fact]
    public void EmptyInstallation_ReportsTheMissingBasePackages()
    {
        string root = CreateInstallation(withBasePackages: false);
        try
        {
            IReadOnlyList<string> missing = NeteaseGamesGameInstaller.FindMissingBasePackages(root);

            Assert.Contains(NeteaseGamesGameInstaller.BasePackageName, missing);
            Assert.Contains("ResourceExA.mpk", missing);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void CompleteInstallation_ReportsNothingMissing()
    {
        string root = CreateInstallation(withBasePackages: true);
        try
        {
            Assert.Empty(NeteaseGamesGameInstaller.FindMissingBasePackages(root));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void BasePackageName_IsTheFirstPackageOfAnInstallation()
    {
        // `Patch.mpk` is the package every installation has; it never appears in a patch list.
        Assert.Equal("Patch.mpk", NeteaseGamesGameInstaller.BasePackageName);
    }

    [Fact]
    public void PatchShards_AreNotBasePackages()
    {
        // The numbered shards (Patch1000.mpk, Patch2000.mpk, ...) are downloadable and must not be confused with
        // the base packages, which are not.
        Assert.NotEqual("Patch1000.mpk", NeteaseGamesGameInstaller.BasePackageName);
        Assert.True(NeteaseGamesGameInstaller.IsPackageIndex("script.mpkinfo"));
        Assert.False(NeteaseGamesGameInstaller.IsPackageIndex(NeteaseGamesGameInstaller.BasePackageName));
    }

    [Fact]
    public void Plan_CarriesTheMissingBasePackages()
    {
        // The plan exposes them so the installer can refuse before touching the disk.
        NeteaseGamesDownloadPlan plan = new() { MissingBasePackages = ["Patch.mpk"] };

        Assert.Single(plan.MissingBasePackages);
        Assert.Equal("Patch.mpk", plan.MissingBasePackages[0]);
    }

    [Fact]
    public void Plan_DefaultsToNoMissingPackages()
    {
        // Update and Repair never look at the base packages, so the default must stay empty.
        NeteaseGamesDownloadPlan plan = new();

        Assert.Empty(plan.MissingBasePackages);
    }
}
