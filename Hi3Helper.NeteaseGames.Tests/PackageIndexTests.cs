using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The package index (<c>*.mpkinfo</c>) is the only place where the vendor publishes the internal layout of a
///     shard. <b>The marker of an entry is the shard id</b>, and for every id the largest <c>offset + size</c> is
///     the byte size the shard must have. Every number below was measured on 2026-10-23 against the live
///     installation: <c>script.mpkinfo</c> has 50,443 entries over 15 shard ids, and marker 2000 -&gt; 16,095,638
///     bytes is exactly the size of <c>Patch1000.mpk</c> on disk.
/// </summary>
public class PackageIndexTests
{
    /// <summary>Builds an index payload out of (name, offset, size, marker) tuples.</summary>
    private static byte[] BuildIndex(uint version, params (string Name, uint Offset, uint Size, uint Marker)[] entries)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(version);
        writer.Write((uint)entries.Length);
        foreach ((string name, uint offset, uint size, uint marker) in entries)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            writer.Write((ushort)nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write(offset);
            writer.Write(size);
            writer.Write(marker);
        }

        writer.Flush();
        return stream.ToArray();
    }

    [Fact]
    public void Parse_ReadsEntriesAndBuildsShardSpans()
    {
        byte[] payload = BuildIndex(1,
            ("7a/7af3a81cc8d825adf151e97c32921576", 0, 1000, 2000),
            ("7b/other", 1000, 500, 2000),
            ("UIScript/npc.csb", 0, 4096, 6000));

        NeteaseGamesPackageIndex? index = NeteaseGamesPackageIndex.Parse(payload);

        Assert.NotNull(index);
        Assert.Equal(3, index!.Entries.Count);
        // The largest `offset + size` of a marker is the size its shard must have.
        Assert.Equal(1500, index.Spans[2000].End);
        Assert.Equal(2, index.Spans[2000].EntryCount);
        Assert.Equal(4096, index.Spans[6000].End);
    }

    [Fact]
    public void Parse_RejectsUnsupportedVersion()
    {
        byte[] payload = BuildIndex(2, ("a/b", 0, 10, 2000));
        Assert.Null(NeteaseGamesPackageIndex.Parse(payload));
    }

    [Fact]
    public void Parse_RejectsTruncatedPayload()
    {
        byte[] payload = BuildIndex(1, ("a/b", 0, 10, 2000));
        byte[] truncated = payload[..^4];
        Assert.Null(NeteaseGamesPackageIndex.Parse(truncated));
    }

    /// <summary>
    ///     The marker to shard id mapping is derived from the position of the shard inside its section:
    ///     <c>mpk_script</c> starts at 2000 with a step of 2, <c>mpk_medium</c> at 4000, <c>mpk_indep</c> at 6000.
    ///     Verified against the live indexes: <c>Patch1000.mpk</c> -&gt; 2000, <c>Patch2000.mpk</c> -&gt; 4000,
    ///     <c>Patch3000.mpk</c> -&gt; 6000.
    /// </summary>
    [Fact]
    public void BuildShardMarkerMap_AssignsTheVendorIds()
    {
        NeteaseGamesPatchList list = new();
        list.Packages["Patch1000.mpk"] = new NeteaseGamesPackageEntry { Section = "mpk_script", TotalSize = 16_095_638 };
        list.Packages["Patch1001.mpk"] = new NeteaseGamesPackageEntry { Section = "mpk_script", TotalSize = 14_327_433 };
        list.Packages["script.mpkinfo"] = new NeteaseGamesPackageEntry { Section = "mpk_script", TotalSize = 1_870_356 };
        list.Packages["Patch2000.mpk"] = new NeteaseGamesPackageEntry { Section = "mpk_medium", TotalSize = 15_656_291 };
        list.Packages["Patch3000.mpk"] = new NeteaseGamesPackageEntry { Section = "mpk_indep", TotalSize = 45_261_483 };

        Dictionary<string, uint> markers = NeteaseGamesGameInstaller.BuildShardMarkerMap(list);

        Assert.Equal(2000u, markers["Patch1000.mpk"]);
        Assert.Equal(2002u, markers["Patch1001.mpk"]);
        Assert.Equal(4000u, markers["Patch2000.mpk"]);
        Assert.Equal(6000u, markers["Patch3000.mpk"]);
        // Indexes are not shards: they never take part in the marker numbering.
        Assert.False(markers.ContainsKey("script.mpkinfo"));
    }

    [Fact]
    public void ParseShardIndex_ExtractsTheNumericSuffix()
    {
        Assert.Equal(1000, NeteaseGamesGameInstaller.ParseShardIndex("Patch1000.mpk"));
        // The whole run of digits is the suffix, so the sort order matches the vendor's numbering.
        Assert.Equal(2029, NeteaseGamesGameInstaller.ParseShardIndex("Patch2029.mpk"));
        Assert.Equal(int.MaxValue, NeteaseGamesGameInstaller.ParseShardIndex("script.mpkinfo"));
    }

    /// <summary>
    ///     Creates a scratch directory next to the test binary. The system temp directory is not used because the
    ///     test host may run under a restricted token that can read it but not create entries inside it.
    /// </summary>
    private static string CreateScratchDirectory(string prefix)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "scratch", prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    ///     A shard must cover its payload but <b>may be longer</b>: measured on 2026-10-23, <c>Patch1009.mpk</c> is
    ///     17,985,424 bytes while its index ends at 17,923,658 (61,766 bytes of tail padding). Requiring equality
    ///     would flag a healthy shard as broken and make the plugin re-download tens of megabytes for nothing.
    /// </summary>
    [Fact]
    public void IsLayoutConsistent_AcceptsPaddingButRejectsTruncation()
    {
        string directory = CreateScratchDirectory("layout-");
        try
        {
            string shard = Path.Combine(directory, "Patch1000.mpk");
            File.WriteAllBytes(shard, new byte[1500]);

            Dictionary<string, uint> markers = new() { ["Patch1000.mpk"] = 2000 };
            Dictionary<uint, long> layouts = new() { [2000] = 1000 };

            // Longer than the payload: healthy (this is the Patch1009.mpk shape).
            Assert.True(NeteaseGamesGameInstaller.IsLayoutConsistent(shard, "Patch1000.mpk", markers, layouts));

            // Exactly the payload: also healthy.
            layouts[2000] = 1500;
            Assert.True(NeteaseGamesGameInstaller.IsLayoutConsistent(shard, "Patch1000.mpk", markers, layouts));

            // Shorter than the payload: truncated.
            layouts[2000] = 16095638;
            Assert.False(NeteaseGamesGameInstaller.IsLayoutConsistent(shard, "Patch1000.mpk", markers, layouts));

            // Without an index on disk there is nothing to compare against.
            Assert.True(NeteaseGamesGameInstaller.IsLayoutConsistent(shard, "Patch1000.mpk", markers, []));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    ///     <c>mpk_filelist.info</c> is the only vendor file that publishes real MD5 hashes; all eight entries matched
    ///     the files on disk when measured on 2026-10-23.
    /// </summary>
    [Fact]
    public void ShardCluster_ParsesFingerprintAndFiles()
    {
        const string text = """
            5e7eadfc182b5d97ea95b019d60d3b55784d81a8482e4e8128398d49c139e1a4
            ResourceExA.mpk	62389447	cb424697843095e6d45d7f20aa641b36
            ResourceExA.mpkinfo	64	d05a0633056838a267a14e57734f48e3
            """;

        NeteaseGamesShardCluster cluster = NeteaseGamesShardCluster.Parse(text);

        Assert.Equal(2, cluster.Files.Count);
        Assert.StartsWith("5e7eadfc", cluster.Fingerprint);
        Assert.Equal("ResourceExA.mpk", cluster.Files[0].Name);
        Assert.Equal(62_389_447, cluster.Files[0].Size);
        Assert.Equal("cb424697843095e6d45d7f20aa641b36", cluster.Files[0].Md5);
    }

    /// <summary>
    ///     The live index of 2026-10-23 is a <c>HEX_</c> envelope over the binary layout; the reader has to accept
    ///     both shapes (a file that came straight from the content host is unwrapped).
    /// </summary>
    [Fact]
    public async Task ReadPackageIndex_AcceptsBothEnvelopeAndPlainPayload()
    {
        byte[] payload = BuildIndex(1, ("a/b", 0, 10, 2000));
        string directory = CreateScratchDirectory("read-");
        try
        {
            string plain = Path.Combine(directory, "plain.mpkinfo");
            await File.WriteAllBytesAsync(plain, payload);
            NeteaseGamesPackageIndex? fromPlain =
                await NeteaseGamesGameInstaller.ReadPackageIndexAsync(plain, CancellationToken.None);
            Assert.NotNull(fromPlain);
            Assert.Single(fromPlain!.Entries);

            string wrapped = Path.Combine(directory, "wrapped.mpkinfo");
            await File.WriteAllBytesAsync(wrapped, NeteaseGamesCrypto.EncryptHexEnvelope(payload));
            NeteaseGamesPackageIndex? fromWrapped =
                await NeteaseGamesGameInstaller.ReadPackageIndexAsync(wrapped, CancellationToken.None);
            Assert.NotNull(fromWrapped);
            Assert.Single(fromWrapped!.Entries);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
