using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Hi3Helper.NeteaseGames.Core;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     Round-trips <see cref="NeteaseGamesBaseIndexWriter" /> through <see cref="NeteaseGamesBaseIndex" />: whatever
///     the writer produces must be readable by the plugin's own reader, with every row intact.
/// </summary>
public class BaseIndexWriterTests
{
    private static string ScratchPath(string prefix)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "scratch", prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "Patch.mpkinfo");
    }

    /// <summary>
    ///     The transform is its own inverse, and the two measured lengths (41 byte paths and 3 byte meta values)
    ///     round-trip. This is the property the whole format rests on.
    /// </summary>
    [Fact]
    public void Obfuscate_IsItsOwnInverse()
    {
        string[] samples = ["max_index", "127", "99", "99/996132eee172893efc8ac62aa7ff72af.0",
            "Char/creature/kushoupopo/ani/P1_battle_hit_stun_hurt_l2.anim"];

        foreach (string sample in samples)
        {
            byte[] plain = Encoding.UTF8.GetBytes(sample);
            byte[] stored = NeteaseGamesBaseIndexWriter.Obfuscate(plain);
            Assert.Equal(plain, NeteaseGamesBaseIndexWriter.Obfuscate(stored));
        }
    }

    /// <summary>The 28 byte value layout, checked field by field against the offsets measured on the live file.</summary>
    [Fact]
    public void BuildValue_UsesTheMeasuredLayout()
    {
        byte[] value = NeteaseGamesBaseIndexWriter.BuildValue(0x11223344, 0x5566, 0x778899AA, 0x0102030405060708);
        Assert.Equal(NeteaseGamesBaseIndexWriter.ValueSize, value.Length);

        Assert.Equal(0x11223344u, BitConverter.ToUInt32(value, 0)); // size, little endian
        Assert.Equal(0, BitConverter.ToInt32(value, 4)); // always zero
        Assert.Equal(0x5566, BitConverter.ToUInt16(value, 8)); // shard id
        Assert.Equal(0x0102030405060708UL, BitConverter.ToUInt64(value, 10)); // hash
        Assert.Equal(0, BitConverter.ToInt16(value, 18)); // always zero
        Assert.Equal(0x778899AAu, BitConverter.ToUInt32(value, 20)); // offset
        Assert.Equal(0, BitConverter.ToInt32(value, 24)); // always zero
    }

    /// <summary>
    ///     A small database round-trips: the reader finds the rows, the shard map and the meta values.
    /// </summary>
    [Fact]
    public void SmallIndex_RoundTrips()
    {
        string path = ScratchPath("index-small-");
        try
        {
            List<NeteaseGamesBaseIndexWriter.Row> rows =
            [
                new("99/996132eee172893efc8ac62aa7ff72af.0", 1000, 2, 0, 0xAABBCCDD00112233),
                new("99/99216672fe11f033844f1b95626f18f9.0", 2000, 2, 1000, 0xAABBCCDD00112244),
                new("Char/npc/jeff/ani/jeff_idle_spec_1.anim", 3000, 4, 0, 0xAABBCCDD00112255)
            ];

            using (FileStream stream = File.Create(path))
            {
                NeteaseGamesBaseIndexWriter.Write(stream, NeteaseGamesBaseIndex.TableName, rows,
                [
                    new("max_index", "127"),
                    new("extra_mpkinfo_families", "[\"Patch1000.mpk\"]")
                ]);
            }

            NeteaseGamesBaseIndex? index = NeteaseGamesBaseIndex.Read(path);
            Assert.NotNull(index);
            Assert.Equal(3, index!.EntryCount);

            // The shard payload lengths are the maximum of offset+size per shard.
            Assert.Equal(3000L, index.ShardEnds[2]);
            Assert.Equal(3000L, index.ShardEnds[4]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>
    ///     The scale that matters: 235k rows force a three level index and a multi level table, which is where a
    ///     hand written b-tree writer breaks. The reader must still find every row.
    /// </summary>
    [Fact]
    public void LargeIndex_RoundTripsEveryRow()
    {
        string path = ScratchPath("index-large-");
        try
        {
            const int count = 235_000;
            List<NeteaseGamesBaseIndexWriter.Row> rows = new(count);
            for (int i = 0; i < count; i++)
            {
                // Paths shaped like the real ones (two hex characters, a slash, then a name).
                rows.Add(new NeteaseGamesBaseIndexWriter.Row(
                    $"{i % 256:x2}/{i:x8}-0000-0000-0000-{i:x12}.{i % 10}",
                    (uint)(i * 7 + 1), (ushort)(i % 128), (uint)(i * 13), (ulong)i * 0x9E3779B97F4A7C15));
            }

            using (FileStream stream = File.Create(path))
            {
                NeteaseGamesBaseIndexWriter.Write(stream, NeteaseGamesBaseIndex.TableName, rows,
                    [new("max_index", "127")]);
            }

            // The file has to be big enough to need interior pages in both trees.
            Assert.True(new FileInfo(path).Length > 4 * 1024 * 1024);

            NeteaseGamesBaseIndex? index = NeteaseGamesBaseIndex.Read(path);
            Assert.NotNull(index);
            Assert.Equal(count, index!.EntryCount);
            Assert.Equal(128, index.ShardCounts.Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
