using System;
using System.IO;
using Hi3Helper.NeteaseGames.Core;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The version records a fresh installation leaves behind.
///     <para>
///         This is the failure that cost a 66 GB download twice (measured 2026-10-26): the records were written only
///         after the <b>whole</b> plan succeeded, so a failure on the last 7.9 MB side file left no
///         <c>patchmd5</c> at all. The game then found no local record, assumed its base tier was missing and began
///         re-downloading 60 GB that was already on disk.
///     </para>
/// </summary>
public class InstallVersionRecordTests
{
    private const string Manifest = """
        20261026174908718
        6233731084051916376
        0
        6606340

        {
            "normal": { "latest_pkgversion": 5282637 },
            "@metadata@": {
                "@bigpatchmd5@": 16703640110960187218,
                "@private_res_version@": "6606340",
                "@shadercachelistmd5@": { "shadercachelist_dx12.txt": 4429205421084825608 }
            }
        }
        """;

    private static string ScratchPatchDir()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "scratch", "rec" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    ///     Creates a game root whose <c>LocalData\Patch</c> is the place the records belong, which is where
    ///     <see cref="NeteaseGamesLocalState.Read" /> looks for them.
    /// </summary>
    private static (string GameRoot, string PatchDirectory) ScratchInstall()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "scratch", "inst" + Guid.NewGuid().ToString("N")[..8]);
        string patch = Path.Combine(root, NeteaseGamesPaths.PatchDirectoryRelativePath);
        Directory.CreateDirectory(patch);
        return (root, patch);
    }

    /// <summary>The launcher record is written with the patch version in its fourth line.</summary>
    [Fact]
    public void WritesTheLauncherRecordWithThePatchVersion()
    {
        string dir = ScratchPatchDir();

        NeteaseGamesLocalState.WriteInstallVersionRecords(dir, 6606340, Manifest, dir, packageVersion: 0);

        string recordPath = Path.Combine(dir, NeteaseGamesPaths.PatchMd5FileName);
        Assert.True(File.Exists(recordPath));

        byte[] raw = File.ReadAllBytes(recordPath);
        byte[] plain = NeteaseGamesCrypto.IsHexEnvelope(raw)
            ? NeteaseGamesCrypto.DecryptHexEnvelope(raw)
            : raw;

        Assert.Equal(6606340, NeteaseGamesLocalState.ReadPatchMd5Version(System.Text.Encoding.UTF8.GetString(plain)));
    }

    /// <summary>
    ///     <c>pkgversion</c> is <b>not</b> written when the value is unknown. The base package version is announced
    ///     nowhere the plugin can read; inventing one would corrupt the vendor's bookkeeping the same way writing
    ///     the patch version there did (6,521,279 -> 6,593,596).
    /// </summary>
    [Fact]
    public void DoesNotInventAPackageVersion()
    {
        string dir = ScratchPatchDir();

        NeteaseGamesLocalState.WriteInstallVersionRecords(dir, 6606340, Manifest, dir, packageVersion: 0);

        Assert.False(File.Exists(Path.Combine(dir, NeteaseGamesPaths.PackageVersionFileName)));
    }

    /// <summary>When a real package version is supplied it is written; a later unknown value does not erase it.</summary>
    [Fact]
    public void WritesARealPackageVersionAndNeverErasesIt()
    {
        string dir = ScratchPatchDir();

        NeteaseGamesLocalState.WriteInstallVersionRecords(dir, 6606340, Manifest, dir, packageVersion: 6521279);
        string path = Path.Combine(dir, NeteaseGamesPaths.PackageVersionFileName);
        Assert.Equal("6521279", File.ReadAllText(path));

        // A second run that does not know the value must leave the existing one alone.
        NeteaseGamesLocalState.WritePackageVersionIfKnown(dir, 0);
        Assert.Equal("6521279", File.ReadAllText(path));
    }

    /// <summary>A version of 0 means "nothing was installed" and must not produce a record.</summary>
    [Fact]
    public void ZeroPatchVersionWritesNothing()
    {
        string dir = ScratchPatchDir();

        NeteaseGamesLocalState.WriteInstallVersionRecords(dir, 0, Manifest, dir, packageVersion: 6521279);

        Assert.False(File.Exists(Path.Combine(dir, NeteaseGamesPaths.PatchMd5FileName)));
        Assert.False(File.Exists(Path.Combine(dir, NeteaseGamesPaths.PackageVersionFileName)));
    }

    /// <summary>
    ///     The record has to be readable as the local state straight away, which is what the host's synchronously
    ///     answered version query depends on.
    /// </summary>
    [Fact]
    public void TheWrittenRecordIsReadBackAsTheLocalVersion()
    {
        (string root, string patch) = ScratchInstall();

        NeteaseGamesLocalState.WriteInstallVersionRecords(patch, 6606340, Manifest, root, packageVersion: 0);
        NeteaseGamesLocalState state = NeteaseGamesLocalState.Read(root);

        Assert.Equal(6606340, state.PatchVersion);
        Assert.Equal("20261026174908718", state.ContentVersion);
        Assert.Equal("4429205421084825608", state.ShaderCacheListMd5);
    }
}
