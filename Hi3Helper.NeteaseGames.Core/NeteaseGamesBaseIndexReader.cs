using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     A read-only walker for the base shard tier index (<c>LocalData\Patch\Patch.mpkinfo</c>).
///     <para>
///         The file is a standard <b>SQLite 3</b> database (<c>SQLite format 3</c>, page size 4,096 on the live
///         installation) holding a single table, <c>info0(filepath, value)</c>. Measured on 2026-10-23:
///         <b>234,910 rows</b> covering shard ids <b>0..127</b>.
///     </para>
///     <para>
///         The plugin reads it with a tiny B-tree walker instead of taking a SQLite dependency: a native SQLite
///         bundle would add an unrelated native library to a NativeAOT plugin, and the <c>SQLitePCLRaw</c> release
///         that NuGet currently resolves carries a high severity advisory. Only the table pages of one
///         two-column table are needed, which is a few dozen lines of format parsing.
///     </para>
///     <para>
///         The <c>value</c> blob is 28 bytes; three fields are stored in the clear and are all this reader needs:
///     </para>
///     <code>
///         [0:4]    u32  resource size   (how many bytes this resource occupies)
///         [8:10]   u16  shard id        (the shard the resource is stored in)
///         [20:24]  u32  start offset    (where the resource begins inside that shard)
///     </code>
///     <para>
///         ⚠ <b>Corrected on 2026-10-24.</b> An earlier revision read <c>[20:24]</c> as the resource's
///         cumulative <i>end</i> offset. It is the <i>start</i>; the end is <c>[20:24] + [0:4]</c>.
///         Grouping by shard id and taking <c>max(start + size)</c> reproduces the exact file length:
///         measured against all 128 shards on disk, <b>127 match the file size byte for byte</b>
///         (the only exception is <c>Patch100</c>, which carries 1,876,274 B of real tail padding).
///     </para>
///     <para>
///         The old reading turned the <i>last resource's own size</i> into "padding" (up to 447 MB), which made
///         every truncation check pass: a <c>Patch54.mpk</c> cut to 300,000,000 B (real length 672,533,543)
///         was still reported as healthy. Do not reintroduce a "padding" concept here.
///     </para>
/// </summary>
public sealed class NeteaseGamesBaseIndex
{
    /// <summary>The file name the launcher keeps the base tier index under.</summary>
    public const string FileName = "Patch.mpkinfo";

    /// <summary>
    ///     The name of the table that holds the shard index. Measured on 2026-10-23: <c>info0(filepath, value)</c>
    ///     with <c>rootpage = 4</c>; the sibling tables are <c>meta</c> and two automatic indexes.
    /// </summary>
    public const string TableName = "info0";

    /// <summary>
    ///     The table name of the shader tier index (<c>ResourceExA.patchinfo</c>). Measured on 2026-10-26: the live
    ///     file declares <c>info1(filepath, value)</c>, i.e. the shader tier uses the next table in the same schema
    ///     rather than reusing <c>info0</c>.
    /// </summary>
    public const string ShaderTableName = "info1";

    private const int SqliteMagicLength = 16;
    private const int PageHeaderSize = 8;
    private const int TableLeaf = 0x0D;
    private const int TableInterior = 0x05;
    private const int InteriorIndex = 0x02;

    private readonly Dictionary<int, long> _shardEnds = [];
    private readonly Dictionary<int, int> _shardCounts = [];

    /// <summary>How many resources the index describes.</summary>
    public int EntryCount { get; private set; }

    /// <summary>
    ///     The shard id to the byte length of that shard's payload
    ///     (<c>max(start offset + resource size)</c> over every resource the index places in it).
    /// </summary>
    public IReadOnlyDictionary<int, long> ShardEnds => _shardEnds;

    /// <summary>The shard id to the number of resources the index places in that shard.</summary>
    public IReadOnlyDictionary<int, int> ShardCounts => _shardCounts;

    /// <summary>Reads the index from a <c>Patch.mpkinfo</c> file.</summary>
    /// <param name="path">The database file.</param>
    /// <returns>The index, or <c>null</c> when the file is missing, unreadable or malformed.</returns>
    public static NeteaseGamesBaseIndex? Read(string path, string tableName = TableName)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Read(stream, tableName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Reads the index from a stream positioned at the start of the database.</summary>
    /// <param name="stream">The database content.</param>
    /// <param name="tableName">
    ///     The table that holds the rows: <c>info0</c> for the base tier (<c>Patch.mpkinfo</c>) and <c>info1</c> for the
    ///     shader tier (<c>ResourceExA.patchinfo</c>).
    /// </param>
    public static NeteaseGamesBaseIndex? Read(Stream stream, string tableName = TableName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrEmpty(tableName);

        byte[] header = new byte[100];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
        {
            return null;
        }

        if (!header.AsSpan(0, SqliteMagicLength).SequenceEqual("SQLite format 3\0"u8))
        {
            return null;
        }

        int pageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16));
        if (pageSize == 1)
        {
            pageSize = 65536;
        }

        if (pageSize < 512 || pageSize > 65536)
        {
            return null;
        }

        NeteaseGamesBaseIndex index = new();
        byte[] page = new byte[pageSize];
        HashSet<uint> visited = [];
        Stack<uint> pending = new();

        // Page 1 is sqlite_master: walk it to find the root page of the table that holds the shard index,
        // then walk that table. Reading the schema instead of assuming a page number keeps this working for
        // databases where the table was created in another order.
        uint? tableRoot = FindTableRootPage(stream, page, pageSize, tableName);
        if (tableRoot is null)
        {
            return null;
        }

        pending.Push(tableRoot.Value);

        while (pending.Count > 0)
        {
            uint pageNumber = pending.Pop();
            if (!visited.Add(pageNumber))
            {
                continue;
            }

            long offset = (long)(pageNumber - 1) * pageSize;
            if (offset < 0 || offset >= stream.Length)
            {
                continue;
            }

            stream.Position = offset;
            if (stream.ReadAtLeast(page, pageSize, throwOnEndOfStream: false) < PageHeaderSize)
            {
                continue;
            }

            // Page 1 carries the 100 byte database header before its own page header.
            int headerOffset = pageNumber == 1 ? 100 : 0;
            int type = page[headerOffset];
            int cellCount = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(headerOffset + 3));

            // An interior cell page has a 12 byte header (the right-most child follows the cell count), a leaf
            // cell page has an 8 byte one. The cell pointer array starts right after the header.
            bool interior = type is TableInterior or InteriorIndex;
            int cellPointerStart = headerOffset + (interior ? 12 : 8);

            if (type is TableInterior or InteriorIndex)
            {
                pending.Push(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(headerOffset + 8)));
                for (int cell = 0; cell < cellCount; cell++)
                {
                    int pointer = cellPointerStart + cell * 2;
                    if (pointer + 2 > pageSize)
                    {
                        break;
                    }

                    int cellOffset = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(pointer));
                    if (cellOffset + 4 <= pageSize)
                    {
                        pending.Push(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(cellOffset)));
                    }
                }

                continue;
            }

            if (type != TableLeaf)
            {
                continue;
            }

            for (int cell = 0; cell < cellCount; cell++)
            {
                int pointer = cellPointerStart + cell * 2;
                if (pointer + 2 > pageSize)
                {
                    break;
                }

                int cellOffset = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(pointer));
                if (!TryReadValueBlob(page, cellOffset, pageSize, out ReadOnlySpan<byte> value) || value.Length < 28)
                {
                    continue;
                }

                // [0:4] is the size of this resource, [20:24] is where it starts inside its shard. The end of the
                // resource - and therefore how far the shard's payload reaches - is the sum of the two. Reading
                // [20:24] as if it were already the end made every truncation check pass (see the type remarks).
                int shardId = BinaryPrimitives.ReadUInt16LittleEndian(value[8..]);
                uint resourceSize = BinaryPrimitives.ReadUInt32LittleEndian(value);
                long start = BinaryPrimitives.ReadUInt32LittleEndian(value[20..]);
                long end = start + resourceSize;

                index.EntryCount++;
                index._shardCounts.TryGetValue(shardId, out int count);
                index._shardCounts[shardId] = count + 1;

                if (!index._shardEnds.TryGetValue(shardId, out long existing) || end > existing)
                {
                    index._shardEnds[shardId] = end;
                }
            }
        }

        return index.EntryCount > 0 ? index : null;
    }

    /// <summary>
    ///     Finds the root page of a table by walking <c>sqlite_master</c> (page 1 onwards).
    ///     <para>
    ///         The schema table has five columns - <c>type, name, tbl_name, rootpage, sql</c>. Every row whose
    ///         <c>type</c> is <c>"table"</c> and whose <c>name</c> matches is the definition that is needed.
    ///     </para>
    /// </summary>
    /// <param name="stream">The database content.</param>
    /// <param name="page">A scratch buffer of one page.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="tableName">The table to look for.</param>
    private static uint? FindTableRootPage(Stream stream, byte[] page, int pageSize, string tableName)
    {
        HashSet<uint> visited = [];
        Stack<uint> pending = new();
        pending.Push(1);

        while (pending.Count > 0)
        {
            uint pageNumber = pending.Pop();
            if (!visited.Add(pageNumber))
            {
                continue;
            }

            long offset = (long)(pageNumber - 1) * pageSize;
            if (offset < 0 || offset >= stream.Length)
            {
                continue;
            }

            stream.Position = offset;
            if (stream.ReadAtLeast(page, pageSize, throwOnEndOfStream: false) < PageHeaderSize)
            {
                continue;
            }

            int headerOffset = pageNumber == 1 ? 100 : 0;
            int type = page[headerOffset];
            int cellCount = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(headerOffset + 3));
            bool interior = type is TableInterior or InteriorIndex;
            int cellPointerStart = headerOffset + (interior ? 12 : 8);

            if (interior)
            {
                pending.Push(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(headerOffset + 8)));
                for (int cell = 0; cell < cellCount; cell++)
                {
                    int pointer = cellPointerStart + cell * 2;
                    if (pointer + 2 > pageSize)
                    {
                        break;
                    }

                    int cellOffset = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(pointer));
                    if (cellOffset + 4 <= pageSize)
                    {
                        pending.Push(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(cellOffset)));
                    }
                }

                continue;
            }

            if (type != TableLeaf)
            {
                continue;
            }

            for (int cell = 0; cell < cellCount; cell++)
            {
                int pointer = cellPointerStart + cell * 2;
                if (pointer + 2 > pageSize)
                {
                    break;
                }

                int cellOffset = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(pointer));
                if (!TryReadRecord(page, cellOffset, pageSize, out ReadOnlySpan<byte> record))
                {
                    continue;
                }

                if (TryReadSchemaEntry(record, tableName, out uint rootPage))
                {
                    return rootPage;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Reads the five schema columns of one <c>sqlite_master</c> row and returns the root page when the row
    ///     describes the requested table.
    /// </summary>
    private static bool TryReadSchemaEntry(ReadOnlySpan<byte> record, string tableName, out uint rootPage)
    {
        rootPage = 0;

        int cursor = 0;
        if (!TryReadVarint(record, ref cursor, out long headerSize) || headerSize > record.Length)
        {
            return false;
        }

        int headerEnd = (int)headerSize;
        long[] serialTypes = new long[5];
        for (int i = 0; i < serialTypes.Length; i++)
        {
            if (!TryReadVarint(record, ref cursor, out serialTypes[i]))
            {
                return false;
            }
        }

        int dataOffset = headerEnd;
        for (int i = 0; i < serialTypes.Length; i++)
        {
            int size = SerialTypeSize(serialTypes[i]);
            if (dataOffset + size > record.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> column = record.Slice(dataOffset, size);

            // type (index 0) must be "table", name (index 1) must match, rootpage (index 3) is the answer.
            if (i == 0 && !IsText(serialTypes[i], column, "table"))
            {
                return false;
            }

            if (i == 1 && !IsText(serialTypes[i], column, tableName))
            {
                return false;
            }

            if (i == 3 && serialTypes[i] > 0 && serialTypes[i] <= 6)
            {
                long value = 0;
                for (int b = 0; b < column.Length; b++)
                {
                    value = (value << 8) | column[b];
                }

                rootPage = (uint)value;
            }

            dataOffset += size;
        }

        return rootPage > 0;
    }

    private static bool IsText(long serialType, ReadOnlySpan<byte> column, string expected)
    {
        // Text serial types are odd and >= 13.
        if (serialType < 13 || serialType % 2 == 0)
        {
            return false;
        }

        return column.SequenceEqual(System.Text.Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>Reads the record body of a table leaf cell (skipping the payload length and the rowid).</summary>
    private static bool TryReadRecord(ReadOnlySpan<byte> page, int cellOffset, int pageSize,
        out ReadOnlySpan<byte> record)
    {
        record = default;
        if (cellOffset <= 0 || cellOffset >= pageSize)
        {
            return false;
        }

        int cursor = cellOffset;
        if (!TryReadVarint(page, ref cursor, out long payloadSize) || !TryReadVarint(page, ref cursor, out _))
        {
            return false;
        }

        if (payloadSize <= 0 || payloadSize > pageSize - cursor)
        {
            // The record does not fit inline; the live index stores every row inline, so skip this cell.
            return false;
        }

        record = page.Slice(cursor, (int)payloadSize);
        return true;
    }

    /// <summary>Extracts the <c>value</c> column from a table leaf cell.</summary>
    private static bool TryReadValueBlob(ReadOnlySpan<byte> page, int cellOffset, int pageSize,
        out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!TryReadRecord(page, cellOffset, pageSize, out ReadOnlySpan<byte> record))
        {
            return false;
        }

        return TryReadSecondColumn(record, out value);
    }

    /// <summary>Walks a record header and returns the second column when it is a blob.</summary>
    private static bool TryReadSecondColumn(ReadOnlySpan<byte> record, out ReadOnlySpan<byte> value)
    {
        value = default;
        int cursor = 0;
        if (!TryReadVarint(record, ref cursor, out long headerSize) || headerSize > record.Length)
        {
            return false;
        }

        int headerEnd = (int)headerSize;
        if (!TryReadVarint(record, ref cursor, out long firstType) ||
            !TryReadVarint(record, ref cursor, out long secondType))
        {
            return false;
        }

        // A blob has a serial type of 12 + 2n.
        if (secondType < 12 || (secondType - 12) % 2 != 0)
        {
            return false;
        }

        int start = headerEnd + SerialTypeSize(firstType);
        int size = (int)((secondType - 12) / 2);
        if (size <= 0 || start + size > record.Length)
        {
            return false;
        }

        value = record.Slice(start, size);
        return true;
    }

    private static int SerialTypeSize(long serialType) => serialType switch
    {
        0 => 0,
        1 => 1,
        2 => 2,
        3 => 3,
        4 => 4,
        5 => 6,
        6 => 8,
        7 => 8,
        8 => 0,
        9 => 0,
        10 or 11 => 0,
        _ => serialType >= 12 ? (int)((serialType - 12) / 2) : 0
    };

    private static bool TryReadVarint(ReadOnlySpan<byte> buffer, ref int offset, out long value)
    {
        value = 0;
        for (int i = 0; i < 9; i++)
        {
            if (offset >= buffer.Length)
            {
                return false;
            }

            byte current = buffer[offset++];
            if (i == 8)
            {
                value = (value << 8) | current;
                return true;
            }

            value = (value << 7) | (long)(current & 0x7F);
            if ((current & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
