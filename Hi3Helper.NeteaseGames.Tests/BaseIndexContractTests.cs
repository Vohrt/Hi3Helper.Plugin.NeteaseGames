using System;
using System.IO;
using Hi3Helper.NeteaseGames.Core;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The base shard tier is indexed by a real SQLite database, <c>LocalData\Patch\Patch.mpkinfo</c>, holding one
///     table <c>info0(filepath, value)</c>.
///     <para>
///         The reader is a hand-written B-tree walker on purpose: a native SQLite bundle would add an unrelated
///         native library to a NativeAOT plugin, and the <c>SQLitePCLRaw</c> release NuGet resolves carries a high
///         severity advisory. Measured on 2026-10-23 against the live installation: <b>234,910 rows</b>, shard ids
///         <b>0..127</b>, and no row ever claims an offset beyond the size of the matching <c>PatchN.mpk</c>
///         (checked against all 127 shard files).
///     </para>
///     <para>
///         These tests cover the parts that can be asserted without a real installation: the format guards and the
///         record layout. The end-to-end check against a live database lives in the probe tool
///         (<c>tools/re/BaseProbe</c>), because a full database cannot be synthesised cheaply in a unit test.
///     </para>
/// </summary>
public class BaseIndexTests
{
    [Fact]
    public void Reader_RejectsEmptyStream()
    {
        using MemoryStream stream = new();
        Assert.Null(NeteaseGamesBaseIndex.Read(stream));
    }

    [Fact]
    public void Reader_RejectsFileWithoutSqliteMagic()
    {
        using MemoryStream stream = new(new byte[4096]);
        Assert.Null(NeteaseGamesBaseIndex.Read(stream));
    }

    [Fact]
    public void Reader_RejectsTruncatedHeader()
    {
        using MemoryStream stream = new("SQLite format 3\0"u8.ToArray());
        Assert.Null(NeteaseGamesBaseIndex.Read(stream));
    }

    [Fact]
    public void Reader_ReturnsNullForMissingFile()
    {
        string missing = Path.Combine(AppContext.BaseDirectory, "no-such-index-" + Guid.NewGuid().ToString("N"));
        Assert.Null(NeteaseGamesBaseIndex.Read(missing));
    }

    [Fact]
    public void Reader_ReturnsNullWhenSchemaLacksTheTable()
    {
        // A valid magic and a page size, but page 1 declares no table, so nothing can be walked.
        byte[] header = new byte[4096];
        "SQLite format 3\0"u8.ToArray().CopyTo(header, 0);
        header[16] = 0x10;   // page size 4096 (big endian)
        header[17] = 0x00;
        header[18] = 1;
        header[21] = 64;
        header[22] = 32;
        header[23] = 32;
        header[100] = 0x0D;  // empty table leaf page, zero cells
        header[103] = 0;
        header[104] = 0;

        using MemoryStream stream = new(header);
        Assert.Null(NeteaseGamesBaseIndex.Read(stream));
    }

    /// <summary>
    ///     The table the reader looks for and the file it expects are part of the contract with the installation.
    /// </summary>
    [Fact]
    public void Contract_NamesMatchTheInstallation()
    {
        Assert.Equal("Patch.mpkinfo", NeteaseGamesBaseIndex.FileName);
        Assert.Equal("info0", NeteaseGamesBaseIndex.TableName);
    }
}
