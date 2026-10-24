using System;
using System.Collections.Generic;
using System.IO;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

public class PathSafetyTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netease-path-tests"));

    [Theory]
    [InlineData("a/b.txt")]
    [InlineData("Client/WindowsNoEditor/HT/Binaries/Win64/HTGame.pak")]
    [InlineData("nested/deeper/file.bin")]
    public void ResolveTargetPath_AcceptsPathsInsideTheRoot(string relative)
    {
        NeteaseGamesDownloadItem item = new() { TargetRelativePath = relative.Replace('/', Path.DirectorySeparatorChar) };

        string resolved = item.ResolveTargetPath(Root);

        Assert.StartsWith(Root + Path.DirectorySeparatorChar, resolved);
        Assert.EndsWith(Path.GetFileName(relative), resolved);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("sub/../../../escape.txt")]
    public void ResolveTargetPath_RejectsTraversalOutsideTheRoot(string relative)
    {
        NeteaseGamesDownloadItem item = new() { TargetRelativePath = relative };

        Assert.Throws<InvalidDataException>(() => item.ResolveTargetPath(Root));
    }

    [Fact]
    public void ResolveTargetPath_RejectsEmptyPath()
    {
        NeteaseGamesDownloadItem item = new() { TargetRelativePath = "  " };

        Assert.Throws<InvalidDataException>(() => item.ResolveTargetPath(Root));
    }

    [Fact]
    public void CleanPath_RemovesZeroWidthCharacters()
    {
        string dirty = "D:\\Sea\u200B\u200B\u200B\u200B of Remnants\\ywzh_extreme";

        Assert.Equal(@"D:\Sea of Remnants\ywzh_extreme", NeteaseGamesLocalState.CleanPath(dirty));
    }

    [Fact]
    public void IsGameDirectory_RejectsMissingAndEmptyPaths()
    {
        Assert.False(NeteaseGamesLocalState.IsGameDirectory(null));
        Assert.False(NeteaseGamesLocalState.IsGameDirectory(string.Empty));
        Assert.False(NeteaseGamesLocalState.IsGameDirectory(Path.Combine(Root, "does-not-exist")));
        Assert.False(NeteaseGamesLocalState.IsGameDirectory("::invalid::path::"));
    }

    [Fact]
    public void IsGameDirectory_AcceptsADirectoryWithTheExecutable()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "gamedir-" + Guid.NewGuid().ToString("N"));
        string executable = Path.Combine(directory, NeteaseGamesPaths.GameExecutableRelativePath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "stub");

            Assert.True(NeteaseGamesLocalState.IsGameDirectory(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

public class DownloadPlanTests
{
    [Fact]
    public void Plan_ReportsSizesAndCountsConsistently()
    {
        NeteaseGamesDownloadPlan plan = new() { PatchVersion = 123, PackageVersion = 456, StagingDirectory = Path.GetTempPath() };
        plan.Items.Add(new NeteaseGamesDownloadItem
        {
            RelativePath = "v/mpk_script/script.mpkinfo",
            TargetRelativePath = "script.mpkinfo",
            Kind = NeteaseGamesDownloadKind.Index,
            ExpectedSize = 1000
        });
        plan.Items.Add(new NeteaseGamesDownloadItem
        {
            RelativePath = "v/mpk_medium/Patch2000.mpk",
            TargetRelativePath = "Patch2000.mpk",
            Kind = NeteaseGamesDownloadKind.Package,
            ExpectedSize = 5000
        });
        plan.Items.Add(new NeteaseGamesDownloadItem
        {
            RelativePath = "v/common/a.txt",
            TargetRelativePath = "a.txt",
            Kind = NeteaseGamesDownloadKind.LooseFile,
            ExpectedSize = 30
        });

        Assert.Equal(6030, plan.TotalSize);
        Assert.Equal(1, plan.PackageCount);
        Assert.Equal(1, plan.IndexCount);
        Assert.Equal(1, plan.LooseCount);
        Assert.False(plan.IsEmpty);
        Assert.Equal(0, plan.DownloadedSize);

        plan.ReportDownloaded(1000);
        plan.ReportDownloaded(5000);
        Assert.Equal(6000, plan.DownloadedSize);
        Assert.Equal(6030, plan.TotalSize);
    }

    [Fact]
    public void Plan_EmptyMeansNothingToDo()
    {
        NeteaseGamesDownloadPlan plan = new();

        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.TotalSize);
        Assert.False(plan.HasVerifiableObjects);
    }

    [Fact]
    public void Plan_StagedPathsAreUnique_EvenForEqualFileNames()
    {
        NeteaseGamesDownloadPlan plan = new() { StagingDirectory = Path.Combine(Path.GetTempPath(), "staging-test") };
        plan.Items.Add(new NeteaseGamesDownloadItem
        {
            RelativePath = "v/mpk_script/script.mpkinfo",
            TargetRelativePath = "script.mpkinfo",
            Kind = NeteaseGamesDownloadKind.Index
        });
        plan.Items.Add(new NeteaseGamesDownloadItem
        {
            RelativePath = "v/mpk_medium/script.mpkinfo",
            TargetRelativePath = "script.mpkinfo",
            Kind = NeteaseGamesDownloadKind.Package
        });

        plan.AssignStaging(plan.StagingDirectory);

        Assert.NotEqual(plan.Items[0].StagedPath, plan.Items[1].StagedPath);
        Assert.EndsWith("script.mpkinfo", plan.Items[0].StagedPath);
    }

    [Fact]
    public void PackageIndexNames_AreDetected()
    {
        Assert.True(NeteaseGamesGameInstaller.IsPackageIndex("script.mpkinfo"));
        Assert.True(NeteaseGamesGameInstaller.IsPackageIndex("Patch.mpkinfo"));
        Assert.False(NeteaseGamesGameInstaller.IsPackageIndex("Patch2000.mpk"));
        Assert.False(NeteaseGamesGameInstaller.IsPackageIndex("prerequisite"));
    }
}

public class CryptoEnvelopeTests
{
    private const string Key = "netease_joker_h74_encrypt_key";

    /// <summary>
    ///     Builds an envelope exactly the way the launcher does, so the decoder can be verified without a
    ///     real manifest.
    /// </summary>
    private static byte[] BuildEnvelope(string plaintext)
    {
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(plaintext);
        using MemoryStream compressed = new();
        using (System.IO.Compression.ZLibStream zlib = new(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        byte[] payload = compressed.ToArray();
        byte[] key = System.Text.Encoding.ASCII.GetBytes(Key);
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] ^= key[i % key.Length];
        }

        return [.. "HEX_"u8, .. System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(payload))];
    }

    [Fact]
    public void HexEnvelope_RoundTrips()
    {
        const string plaintext = "{\"normal\":{\"latest_pkgversion\":6534552}}";
        byte[] envelope = BuildEnvelope(plaintext);

        Assert.True(NeteaseGamesCrypto.IsHexEnvelope(envelope));
        string decoded = System.Text.Encoding.UTF8.GetString(NeteaseGamesCrypto.DecryptHexEnvelope(envelope));
        Assert.Equal(plaintext, decoded);
    }

    [Fact]
    public void HexEnvelope_ToleratesLineBreaks()
    {
        byte[] envelope = BuildEnvelope("{\"a\":1}");
        string wrapped = System.Text.Encoding.ASCII.GetString(envelope);
        string broken = wrapped[..20] + "\r\n" + wrapped[20..];
        byte[] withBreaks = System.Text.Encoding.ASCII.GetBytes(broken);

        string decoded = System.Text.Encoding.UTF8.GetString(NeteaseGamesCrypto.DecryptHexEnvelope(withBreaks));

        Assert.Equal("{\"a\":1}", decoded);
    }

    [Fact]
    public void HexEnvelope_RejectsForeignContent()
    {
        Assert.False(NeteaseGamesCrypto.IsHexEnvelope("plain text"u8));
        Assert.Throws<InvalidDataException>(() => NeteaseGamesCrypto.DecryptHexEnvelope("plain text"u8));
    }
}

public class UrlSignerTests
{
    [Fact]
    public void Key2_IsTheEpochShiftedByTwelveHours()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(0x6AD6DF98 - 43200 + 0);

        // 0x6AD6DF98 = 1792399768 -> key2 has to be that value when "now" is twelve hours earlier.
        Assert.Equal("6ad6df98", NeteaseGamesUrlSigner.GetKey2(now));
    }

    [Fact]
    public void Key2_IsEightHexDigitsLowercase()
    {
        string key2 = NeteaseGamesUrlSigner.GetKey2(DateTimeOffset.UtcNow);

        Assert.Equal(8, key2.Length);
        Assert.Matches("^[0-9a-f]{8}$", key2);
    }

    [Fact]
    public void GetPath_ExtractsPathWithoutHostAndQuery()
    {
        Assert.Equal("/pl/x.txt", NeteaseGamesUrlSigner.GetPath("https://h74.update.netease.com/pl/x.txt"));
        Assert.Equal("/a/b", NeteaseGamesUrlSigner.GetPath("https://h:443/a/b?key1=1&key2=2"));
        Assert.Equal("/", NeteaseGamesUrlSigner.GetPath("https://host"));
    }

    [Fact]
    public void Sign_AppendsKeyParameters()
    {
        string signed = NeteaseGamesUrlSigner.Sign("https://h74.gph.netease.com/v/file", DateTimeOffset.UnixEpoch);

        Assert.Contains("?key1=", signed, StringComparison.Ordinal);
        // UnixEpoch + the 12 hour offset is 43200 seconds = 0000a8c0.
        Assert.Contains("&key2=0000a8c0", signed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/20261014065508363/mpk_medium/Patch2000.mpk", "6ad7c991", "4727210511e67d04e0617282450707b6")]
    [InlineData("/20261014065508363/mpk_script/script.mpkinfo", "6ad7c991", "531ad5c57942cda1eec739f1b495d305")]
    [InlineData("/20261014065508363/patchlist_windows64_normal_mpk.txt", "6ad7c990", "fb2971228fb750c266d754ea39831fa0")]
    [InlineData("/loose_file/x/abc", "6ad7c990", "4dc2d63d2741be49e05b8935d43b53a6")]
    public void Key1_MatchesEveryCapturedSample(string path, string key2, string expected)
    {
        // Every vector is a URL the official launcher actually sent, together with the key2 it used.
        Assert.Equal(expected, NeteaseGamesUrlSigner.GetKey1(path, key2));
    }
}
