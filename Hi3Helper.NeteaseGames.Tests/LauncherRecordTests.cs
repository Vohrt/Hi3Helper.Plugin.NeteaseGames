using System;
using System.IO;
using System.Text;
using Hi3Helper.NeteaseGames.Core;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The official launcher keeps its version record in <c>patchmd5_windows64_normal.txt</c>: a verbatim copy of
///     the remote <c>OB_patchmd5_*.txt</c> whose fourth header line carries the installed
///     <c>@private_res_version@</c>. The plugin has to read that layout and rewrite it after an update, otherwise
///     the launcher keeps believing the update never happened.
/// </summary>
public class LauncherRecordTests
{
    /// <summary>A manifest shaped exactly like the live <c>OB_patchmd5_windows64_normal.txt</c>.</summary>
    private const string SampleManifest = """
        20261022122731826
        16867045629764259494
        0
        6582435
        {"normal":{"mininum_pkgversion":0,"review_pkgversion":[],"new_package_url":"","latest_pkgversion":5282637},"@metadata@":{"@base_version@":"20261021113421287","@private_res_version@":6582435,"@private_patch_version@":6582435,"@private_script_version@":6582435,"@engine_version@":"261019222124_71902dc10_release","@patch_branch@":"release"}}
        """;

    [Fact]
    public void ReadPatchMd5Version_ReadsTheFourthLine()
    {
        Assert.Equal(6582435, NeteaseGamesLocalState.ReadPatchMd5Version(SampleManifest));
    }

    [Fact]
    public void ReadPatchMd5Version_FallsBackToTheMetadataBlock()
    {
        // A file written without the four header lines still announces the version in its metadata.
        const string withoutHeader = """
            {"@metadata@":{"@private_res_version@":6534552,"@base_version@":"20261014065508363"}}
            """;

        Assert.Equal(6534552, NeteaseGamesLocalState.ReadPatchMd5Version(withoutHeader));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a manifest")]
    [InlineData("0\n0\n0\n0\n{}")]
    public void ReadPatchMd5Version_ReturnsZeroForUnusableInput(string text)
    {
        Assert.Equal(0, NeteaseGamesLocalState.ReadPatchMd5Version(text));
    }

    [Fact]
    public void BuildLauncherRecord_PutsTheVersionOnTheFourthLine()
    {
        string record = NeteaseGamesLocalState.BuildLauncherRecord(SampleManifest, 6582435);
        string[] lines = record.Split('\n');

        Assert.Equal("6582435", lines[3].Trim());
        Assert.Equal("0", lines[2].Trim());
        // The manifest's own hash line is preserved.
        Assert.Equal("16867045629764259494", lines[1].Trim());
        // A fresh sync timestamp is written (17 digits, yyyyMMddHHmmssfff).
        Assert.Equal(17, lines[0].Trim().Length);
    }

    [Fact]
    public void BuildLauncherRecord_ReplacesEveryVersionField()
    {
        // Pretend a newer version was installed than the manifest announced.
        string record = NeteaseGamesLocalState.BuildLauncherRecord(SampleManifest, 7000001);

        foreach (string property in new[]
                 {
                     "\"@private_res_version@\"", "\"@private_patch_version@\"", "\"@private_script_version@\""
                 })
        {
            int index = record.IndexOf(property, StringComparison.Ordinal);
            Assert.True(index >= 0, $"{property} is missing");

            int colon = record.IndexOf(':', index);
            int start = colon + 1;
            while (char.IsWhiteSpace(record[start]))
            {
                start++;
            }

            int end = start;
            while (end < record.Length && char.IsDigit(record[end]))
            {
                end++;
            }

            Assert.Equal("7000001", record[start..end]);
        }

        Assert.Equal(7000001, NeteaseGamesLocalState.ReadPatchMd5Version(record));
    }

    [Fact]
    public void BuildLauncherRecord_DoesNotTouchTheStaleNormalBlock()
    {
        // `normal.latest_pkgversion` is a stale value of another branch; it must be left alone so the file keeps
        // matching what the launcher wrote.
        string record = NeteaseGamesLocalState.BuildLauncherRecord(SampleManifest, 7000001);

        Assert.Contains("\"latest_pkgversion\":5282637", record, StringComparison.Ordinal);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrips()
    {
        byte[] plain = Encoding.UTF8.GetBytes(SampleManifest);
        byte[] encrypted = NeteaseGamesCrypto.EncryptHexEnvelope(plain);

        Assert.True(NeteaseGamesCrypto.IsHexEnvelope(encrypted));
        Assert.Equal(plain, NeteaseGamesCrypto.DecryptHexEnvelope(encrypted));
    }

    [Fact]
    public void EncryptDecrypt_RoundTripsUnicodeContent()
    {
        byte[] plain = Encoding.UTF8.GetBytes("{\"text\":\"遗忘之海 · Sea of Remnants\"}");
        byte[] encrypted = NeteaseGamesCrypto.EncryptHexEnvelope(plain);

        Assert.Equal(plain, NeteaseGamesCrypto.DecryptHexEnvelope(encrypted));
    }

    [Fact]
    public void WriteLauncherRecord_WritesARecordThatReadsBack()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "rec-" + Guid.NewGuid().ToString("N"));
        string patchDirectory = Path.Combine(root, NeteaseGamesPaths.PatchDirectoryRelativePath);
        Directory.CreateDirectory(patchDirectory);

        try
        {
            NeteaseGamesLocalState.WriteLauncherRecord(patchDirectory, 7000001, SampleManifest, root);

            string path = Path.Combine(patchDirectory, NeteaseGamesPaths.PatchMd5FileName);
            Assert.True(File.Exists(path));

            byte[] raw = File.ReadAllBytes(path);
            Assert.True(NeteaseGamesCrypto.IsHexEnvelope(raw));

            string text = Encoding.UTF8.GetString(NeteaseGamesCrypto.DecryptHexEnvelope(raw));
            Assert.Equal(7000001, NeteaseGamesLocalState.ReadPatchMd5Version(text));
        }
        finally
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
    }

    [Fact]
    public void WriteLauncherRecord_WithoutAManifestLeavesTheFileAlone()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "rec-" + Guid.NewGuid().ToString("N"));
        string patchDirectory = Path.Combine(root, NeteaseGamesPaths.PatchDirectoryRelativePath);
        Directory.CreateDirectory(patchDirectory);

        try
        {
            string path = Path.Combine(patchDirectory, NeteaseGamesPaths.PatchMd5FileName);
            byte[] original = NeteaseGamesCrypto.EncryptHexEnvelope(Encoding.UTF8.GetBytes(SampleManifest));
            File.WriteAllBytes(path, original);

            // No manifest available: the record must not be replaced with a fabricated one.
            NeteaseGamesLocalState.WriteLauncherRecord(patchDirectory, 7000001, null, root);

            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
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
    }
}
