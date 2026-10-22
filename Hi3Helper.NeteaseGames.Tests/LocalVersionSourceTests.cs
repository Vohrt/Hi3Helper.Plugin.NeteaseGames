using System;
using System.IO;
using System.Text.Json;
using Hi3Helper.NeteaseGames.Core;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The local version has to come from <c>pkgversion</c> - the file the plugin writes as the commit point of
///     an update. The other two sources are written by the game client and go stale after a plugin update, which
///     made the host offer the same update forever.
/// </summary>
public class LocalVersionSourceTests
{
    /// <summary>Builds a minimal installation directory with the given version files.</summary>
    private static string CreateInstallation(long pkgVersion, long? versionDataPatchVersion,
        long? patchMd5LatestVersion = null)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "ver-" + Guid.NewGuid().ToString("N"));
        string patchDirectory = Path.Combine(root, NeteaseGamesPaths.PatchDirectoryRelativePath);
        string documentsDirectory = Path.Combine(root, NeteaseGamesPaths.DocumentsRelativePath);

        Directory.CreateDirectory(patchDirectory);
        Directory.CreateDirectory(documentsDirectory);

        if (pkgVersion > 0)
        {
            File.WriteAllText(Path.Combine(patchDirectory, NeteaseGamesPaths.PackageVersionFileName),
                pkgVersion.ToString());
        }

        if (versionDataPatchVersion is not null)
        {
            File.WriteAllText(Path.Combine(documentsDirectory, "versiondata.json"),
                JsonSerializer.Serialize(new
                {
                    package_version = versionDataPatchVersion.Value,
                    patch_version = versionDataPatchVersion.Value.ToString(),
                    engine_version = "test-engine",
                    shader_version = "test-shader"
                }));
        }

        if (patchMd5LatestVersion is not null)
        {
            // The patchmd5 file is a HEX_ envelope; the reader only falls back to it when pkgversion is missing,
            // so a placeholder is enough here.
            File.WriteAllText(Path.Combine(patchDirectory, NeteaseGamesPaths.PatchMd5FileName), "HEX_AAAA");
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
            // A leftover temporary directory is harmless.
        }
    }

    [Fact]
    public void PkgVersion_WinsOverAStaleVersionData()
    {
        // Exactly the situation after a plugin update: pkgversion is the new version while the client's
        // versiondata.json still reports the old one.
        string root = CreateInstallation(pkgVersion: 6582435, versionDataPatchVersion: 6534552);
        try
        {
            NeteaseGamesLocalState state = NeteaseGamesLocalState.Read(root);

            Assert.Equal(6582435, state.PackageVersion);
            Assert.Equal(6582435, state.PatchVersion);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void VersionData_IsUsedWhenPkgVersionIsMissing()
    {
        string root = CreateInstallation(pkgVersion: 0, versionDataPatchVersion: 6534552);
        try
        {
            NeteaseGamesLocalState state = NeteaseGamesLocalState.Read(root);

            // The fallback keeps a fresh installation readable even before the first update.
            Assert.Equal(6534552, state.PatchVersion);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void VersionData_StillSuppliesTheEngineAndShaderVersion()
    {
        // Only the version counters are superseded by pkgversion; the other fields have no other source.
        string root = CreateInstallation(pkgVersion: 6582435, versionDataPatchVersion: 6534552);
        try
        {
            NeteaseGamesLocalState state = NeteaseGamesLocalState.Read(root);

            Assert.Equal("test-engine", state.EngineVersion);
            Assert.Equal("test-shader", state.ShaderVersion);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void MissingEverything_YieldsZero()
    {
        string root = CreateInstallation(pkgVersion: 0, versionDataPatchVersion: null);
        try
        {
            NeteaseGamesLocalState state = NeteaseGamesLocalState.Read(root);

            Assert.Equal(0, state.PackageVersion);
            Assert.Equal(0, state.PatchVersion);
        }
        finally
        {
            Cleanup(root);
        }
    }
}

/// <summary>
///     Two version numbers live side by side in an installation and they are NOT interchangeable:
///     <c>pkgversion</c> holds the base package / client version (the fourth field of <c>gameversion</c>) while
///     the fourth line of <c>patchmd5</c> holds the patch version (the fifth field). The official launcher
///     decides whether an update is needed from the patch version, so reading <c>pkgversion</c> made the plugin
///     report a permanent update after every launcher run.
/// </summary>
public class PatchVersionDimensionTests
{
    /// <summary>Builds an installation whose pkgversion and patchmd5 disagree, like a launcher-managed one.</summary>
    private static string CreateInstallation(long pkgVersion, long patchVersion, bool enveloped = true)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "dim-" + Guid.NewGuid().ToString("N"));
        string patchDirectory = Path.Combine(root, NeteaseGamesPaths.PatchDirectoryRelativePath);
        Directory.CreateDirectory(patchDirectory);

        File.WriteAllText(Path.Combine(patchDirectory, NeteaseGamesPaths.PackageVersionFileName),
            pkgVersion.ToString());

        string record = $"20261022122731826\n16867045629764259494\n0\n{patchVersion}\n" +
                        $"{{\"@metadata@\":{{\"@private_res_version@\":{patchVersion}}}}}";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(record);
        File.WriteAllBytes(Path.Combine(patchDirectory, NeteaseGamesPaths.PatchMd5FileName),
            enveloped ? NeteaseGamesCrypto.EncryptHexEnvelope(bytes) : bytes);

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
    public void PatchVersion_ComesFromPatchMd5_NotPkgVersion()
    {
        // The exact shape the official launcher leaves behind: pkgversion 6521279 (base package) while the patch
        // version is 6582435.
        string root = CreateInstallation(pkgVersion: 6521279, patchVersion: 6582435);
        try
        {
            NeteaseGamesLocalState state = NeteaseGamesLocalState.Read(root);

            Assert.Equal(6582435, state.PatchVersion);
            // The base package version is still reported, it is just not the patch version.
            Assert.Equal(6521279, state.PackageVersion);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PatchVersion_ReadsAPlainPatchMd5Too()
    {
        // A copy taken straight from the content host is plain text, not enveloped.
        string root = CreateInstallation(pkgVersion: 6521279, patchVersion: 6582435, enveloped: false);
        try
        {
            Assert.Equal(6582435, NeteaseGamesLocalState.Read(root).PatchVersion);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PatchVersion_FallsBackToPkgVersionWithoutPatchMd5()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "dim-" + Guid.NewGuid().ToString("N"));
        string patchDirectory = Path.Combine(root, NeteaseGamesPaths.PatchDirectoryRelativePath);
        Directory.CreateDirectory(patchDirectory);
        try
        {
            File.WriteAllText(Path.Combine(patchDirectory, NeteaseGamesPaths.PackageVersionFileName), "6521279");

            // No patchmd5 at all: the only number available is the base package version.
            Assert.Equal(6521279, NeteaseGamesLocalState.Read(root).PatchVersion);
        }
        finally
        {
            Cleanup(root);
        }
    }
}