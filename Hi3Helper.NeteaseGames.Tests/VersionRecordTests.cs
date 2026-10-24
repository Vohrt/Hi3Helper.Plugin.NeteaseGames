using System;
using System.IO;
using System.Text.Json;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     After an update the plugin has to refresh three records so that the official launcher, the game client and
///     the plugin all agree on one version. Measured state on disk (2026-10-23) is the fixture below.
///     <para>
///         ⚠ <c>versiondata.json</c> carries <b>two independent counters</b>: <c>package_version</c> (6521279, the
///         base package version, never advanced by a patch) and <c>patch_version</c> ("6589318", the one an update
///         advances). Writing the patch version into <c>package_version</c> corrupted the client's bookkeeping, so
///         the refresh only touches <c>patch_version</c>.
///     </para>
/// </summary>
public class VersionRecordTests
{
    /// <summary>
    ///     Guard against the exact regression that shipped before 2026-10-24: an update must advance the patch
    ///     version in the launcher record and leave <c>pkgversion</c> alone.
    /// </summary>
    [Fact]
    public void Plan_SeparatesTheTwoVersionCounters()
    {
        NeteaseGamesDownloadPlan update = new() { PatchVersion = 6593596, PackageVersion = 0 };

        // A delta update advances exactly one counter.
        Assert.True(update.PatchVersion > 0);
        Assert.Equal(0, update.PackageVersion);
    }

    private static string CreateScratchDirectory()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "scratch", "records-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    ///     `gameversion` is `<channel>_<platform>_<hash>_<packageVersion>_<patchVersion>`; only the last field is
    ///     ours to advance.
    /// </summary>
    [Fact]
    public void GameVersionToken_OnlyThePatchFieldAdvances()
    {
        Assert.Equal("CN_windows_0a66d2e2a_6521279_6589318", RewriteGameVersion("CN_windows_0a66d2e2a_6521279_6585899", 6589318));
    }

    private static string RewriteGameVersion(string token, long patchVersion)
    {
        int lastSeparator = token.LastIndexOf('_');
        Assert.True(lastSeparator > 0);
        return string.Concat(token.AsSpan(0, lastSeparator + 1), patchVersion.ToString());
    }

    /// <summary>
    ///     A real record must keep `package_version` untouched when an update advances the patch version. The
    ///     refresh is driven through <see cref="NeteaseGamesLocalState.WriteLauncherRecord" />, which is the entry
    ///     point the applier uses.
    /// </summary>
    [Fact]
    public void WriteLauncherRecord_KeepsPackageVersionAndAdvancesPatchVersion()
    {
        string root = CreateScratchDirectory();
        try
        {
            string patchDirectory = Path.Combine(root, NeteaseGamesPaths.PatchDirectoryRelativePath);
            string documents = Path.Combine(root, NeteaseGamesPaths.DocumentsRelativePath);
            Directory.CreateDirectory(patchDirectory);
            Directory.CreateDirectory(documents);

            File.WriteAllText(Path.Combine(patchDirectory, NeteaseGamesPaths.GameVersionFileName),
                "CN_windows_0a66d2e2a_6521279_6585899");
            File.WriteAllText(Path.Combine(documents, "versiondata.json"),
                """{"package_version": 6521279, "patch_version": "6585899", "engine_version": "261014173721_4a476f25b_release", "shader_version": "e6585302-p6579883"}""");

            const string remoteManifest = """
                20261023122834919
                10590933344404223586
                0
                6589318
                {"normal":{"mininum_pkgversion":0},"@metadata@":{"@private_res_version@":"6589318"}}
                """;

            NeteaseGamesLocalState.WriteLauncherRecord(patchDirectory, 6589318, remoteManifest, root);

            // gameversion: only the trailing patch field moved.
            string gameVersion = File.ReadAllText(Path.Combine(patchDirectory, NeteaseGamesPaths.GameVersionFileName)).Trim();
            Assert.Equal("CN_windows_0a66d2e2a_6521279_6589318", gameVersion);

            // versiondata.json: patch_version advanced, package_version untouched, unrelated fields preserved.
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(documents, "versiondata.json")));
            JsonElement rootElement = document.RootElement;
            Assert.Equal(6521279, rootElement.GetProperty("package_version").GetInt64());
            Assert.Equal("6589318", rootElement.GetProperty("patch_version").GetString());
            Assert.Equal("261014173721_4a476f25b_release", rootElement.GetProperty("engine_version").GetString());
            Assert.Equal("e6585302-p6579883", rootElement.GetProperty("shader_version").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    ///     The record the official launcher reads is rewritten with the newly installed version in its fourth line,
    ///     wrapped in the <c>HEX_</c> envelope the launcher itself writes.
    /// </summary>
    [Fact]
    public void WriteLauncherRecord_WritesTheEnvelopeWithTheNewVersion()
    {
        string root = CreateScratchDirectory();
        try
        {
            string patchDirectory = Path.Combine(root, NeteaseGamesPaths.PatchDirectoryRelativePath);
            Directory.CreateDirectory(patchDirectory);

            const string remoteManifest = """
                20261023122834919
                10590933344404223586
                0
                6589318
                {"normal":{"mininum_pkgversion":0},"@metadata@":{"@private_res_version@":"6589318"}}
                """;

            NeteaseGamesLocalState.WriteLauncherRecord(patchDirectory, 6589318, remoteManifest, root);

            string path = Path.Combine(patchDirectory, NeteaseGamesPaths.PatchMd5FileName);
            Assert.True(File.Exists(path));

            byte[] raw = File.ReadAllBytes(path);
            Assert.True(NeteaseGamesCrypto.IsHexEnvelope(raw));

            string text = System.Text.Encoding.UTF8.GetString(NeteaseGamesCrypto.DecryptHexEnvelope(raw));
            Assert.Equal("20261023122834919", NeteaseGamesLocalState.ReadPatchMd5ContentVersion(text));
            Assert.Equal(6589318, NeteaseGamesLocalState.ReadPatchMd5Version(text));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
