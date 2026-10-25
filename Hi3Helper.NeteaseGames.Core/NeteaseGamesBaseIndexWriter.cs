using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     Writes a base tier index (<c>Patch.mpkinfo</c> / <c>ResourceExA.patchinfo</c>) in the vendor's format.
///     <para>
///         The file is a plain <b>SQLite 3</b> database with the schema the launcher itself creates
///         (<c>CREATE TABLE IF NOT EXISTS meta (key TINYTEXT PRIMARY KEY, value TEXT)</c> and
///         <c>CREATE TABLE IF NOT EXISTS {name} (filepath TEXT PRIMARY KEY, value BLOB)</c>, recovered from the
///         <c>MChecker.py</c> bytecode inside <c>PCLauncher.exe</c>). The plugin writes it instead of taking a
///         SQLite dependency, for the same reason <see cref="NeteaseGamesBaseIndex" /> reads it by hand: a native
///         SQLite bundle would add an unrelated native library to a NativeAOT plugin, and the
///         <c>SQLitePCLRaw</c> release NuGet resolves carries a high severity advisory.
///     </para>
///     <para>
///         <b>Both string columns are obfuscated</b> with a single-byte XOR that depends on the byte position
///         inside the column, applied <i>per column</i> (not across the record):
///     </para>
///     <code>
///         plain[i] = stored[i] ^ (i &lt; (length % 17) ? 0x2B : 0x35)
///     </code>
///     <para>
///         Verified on 2026-10-25: every one of the 234,910 rows of the live <c>Patch.mpkinfo</c> and all
///         173,861 rows of <c>ResourceExA.patchinfo</c> round-trip exactly through this transform. The <c>value</c>
///         blob is <b>not</b> obfuscated; it is 28 bytes laid out as
///     </para>
///     <code>
///         [0:4]    u32  resource size
///         [8:10]   u16  shard id
///         [10:18]  u64  content hash (the same value the patch list carries)
///         [20:24]  u32  start offset inside the shard
///     </code>
///     <para>
///         Everything else is zero, and the live file confirms it: bytes [4:8], [18:20] and [24:28] are zero in
///         all 234,910 rows. Rows whose size is 0 are <b>placeholders</b> - resources that a later round removed.
///         They keep their hash but carry shard 0 and offset 0.
///     </para>
///     <para>
///         <b>What the live file requires beyond a naive writer.</b> Measured on 2026-10-25, each of these is load
///         bearing:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Both columns are stored as BLOB</b> (<c>typeof()</c> answers <c>blob</c>), because the launcher
///             binds encoded bytes rather than text. A <c>TEXT PRIMARY KEY</c> column holding blobs is compared
///             with <c>memcmp</c>, so the index sorts by the <b>obfuscated</b> bytes - measured: the stored order
///             is ascending in raw bytes and is <b>not</b> ascending in decoded bytes.
///         </item>
///         <item>
///             <b>The <c>sqlite_autoindex_*</c> rows must exist and must point at real index B-trees.</b> Deleting
///             them from a copy of the live file makes <c>SELECT count(*)</c> answer <c>database disk image is
///             malformed</c>: the schema parser requires every <c>PRIMARY KEY</c> constraint to be backed by an
///             index. The live <c>info0</c> needs a three level index (3,288 leaves, 56 interior pages, one root).
///         </item>
///         <item>
///             <b>Rowids are arbitrary.</b> The live table's rowids run 1..236,588 with 1,678 gaps and are in
///             insertion order, unrelated to the key order. This writer assigns them sequentially and keeps the
///             index entries consistent with them.
///         </item>
///         <item>
///             <b>No overflow pages are needed.</b> The live file contains no overflow page type; the largest record
///             is a 41 byte path plus a 28 byte blob. A cell that would need one is reported instead of truncated.
///         </item>
///     </list>
/// </summary>
public static class NeteaseGamesBaseIndexWriter
{
    private const int PageSize = 4096;

    /// <summary>The number of bytes of one <c>value</c> blob.</summary>
    public const int ValueSize = 28;

    /// <summary>One row of the index.</summary>
    /// <param name="Path">The (plaintext) resource path.</param>
    /// <param name="Size">The size of the resource, or 0 for a placeholder.</param>
    /// <param name="ShardId">The shard the resource lives in (0 for a placeholder).</param>
    /// <param name="Offset">The offset inside the shard (0 for a placeholder).</param>
    /// <param name="Hash">The 64 bit content hash.</param>
    public readonly record struct Row(string Path, uint Size, ushort ShardId, uint Offset, ulong Hash);

    /// <summary>
    ///     Obfuscates (or de-obfuscates) one string column value.
    /// </summary>
    /// <param name="value">The plaintext or stored bytes.</param>
    public static byte[] Obfuscate(ReadOnlySpan<byte> value)
    {
        int head = value.Length % 17;
        byte[] result = new byte[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            result[i] = (byte)(value[i] ^ (i < head ? 0x2B : 0x35));
        }

        return result;
    }

    /// <summary>Builds the 28 byte <c>value</c> blob of a row.</summary>
    public static byte[] BuildValue(uint size, ushort shardId, uint offset, ulong hash)
    {
        byte[] value = new byte[ValueSize];
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0), size);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(8), shardId);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(10), hash);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(20), offset);
        return value;
    }

    /// <summary>
    ///     Writes an index database.
    /// </summary>
    /// <param name="stream">The stream to write to (it is left open).</param>
    /// <param name="tableName">The table name (<c>info0</c> for the base tier, <c>info1</c> for the shader tier).</param>
    /// <param name="rows">The rows; they are written in the order given (that order becomes the rowid order).</param>
    /// <param name="meta">The <c>meta</c> rows (plaintext key and value).</param>
    public static void Write(Stream stream, string tableName, IEnumerable<Row> rows,
        IEnumerable<KeyValuePair<string, string>> meta)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrEmpty(tableName);

        List<Entry> metaEntries = [];
        foreach (KeyValuePair<string, string> pair in meta)
        {
            metaEntries.Add(new Entry(Obfuscate(Encoding.UTF8.GetBytes(pair.Key)),
                Obfuscate(Encoding.UTF8.GetBytes(pair.Value))));
        }

        List<Entry> dataEntries = [];
        foreach (Row row in rows)
        {
            dataEntries.Add(new Entry(Obfuscate(Encoding.UTF8.GetBytes(row.Path)),
                BuildValue(row.Size, row.ShardId, row.Offset, row.Hash)));
        }

        SqliteFileBuilder builder = new(stream);

        // The launcher's own allocation order, taken from the live file: meta (2), sqlite_autoindex_meta_1 (3),
        // info0 (4), sqlite_autoindex_info0_1 (5). SQLite does not require it, but a file shaped like the
        // vendor's is one less difference to explain.
        int metaRoot = builder.AllocatePage();
        int metaIndexRoot = builder.AllocatePage();
        int dataRoot = builder.AllocatePage();
        int dataIndexRoot = builder.AllocatePage();

        List<SqliteFileBuilder.SchemaRow> schema =
        [
            new("table", "meta", "meta", metaRoot, "CREATE TABLE meta (key TINYTEXT PRIMARY KEY, value TEXT)"),
            new("index", "sqlite_autoindex_meta_1", "meta", metaIndexRoot, null),
            new("table", tableName, tableName, dataRoot, $"CREATE TABLE {tableName} (filepath TEXT PRIMARY KEY, value BLOB)"),
            new("index", $"sqlite_autoindex_{tableName}_1", tableName, dataIndexRoot, null)
        ];

        builder.WriteSchemaPage(schema);
        builder.WriteTable(metaRoot, metaIndexRoot, metaEntries);
        builder.WriteTable(dataRoot, dataIndexRoot, dataEntries);
        builder.Finish();
    }

    /// <summary>One index entry: the obfuscated key and the column value (the 28 byte blob, or the meta text).</summary>
    private readonly record struct Entry(byte[] Key, byte[] Value);

    /// <summary>
    ///     Assembles a minimal SQLite 3 database file page by page.
    ///     <para>
    ///         Implemented: a 4,096 byte page size, table and index B-trees of any depth, and no overflow pages. A
    ///         cell that would need an overflow page is reported rather than silently truncated.
    ///     </para>
    /// </summary>
    private sealed class SqliteFileBuilder
    {
        /// <summary>The b-tree page header, present at the start of every page but the first 100 bytes of page 1.</summary>
        private const int PageHeaderSize = 8;

        /// <summary>
        ///     Where the cell pointer array starts on an <b>interior</b> page: the 8 byte header plus the 4 byte
        ///     right-most child pointer, which comes <i>before</i> the array. Writing the array at offset 8
        ///     overwrites that pointer and SQLite then reports the page's cells as out of range (measured
        ///     2026-10-25).
        /// </summary>
        private const int InteriorCellPointerStart = PageHeaderSize + 4;

        /// <summary>The database header that occupies the start of page 1.</summary>
        private const int DatabaseHeaderSize = 100;

        private readonly Stream _stream;
        private readonly List<byte[]> _pages = [];

        public SqliteFileBuilder(Stream stream)
        {
            _stream = stream;
            _pages.Add(new byte[PageSize]); // page 1: the schema
        }

        public readonly record struct SchemaRow(string Type, string Name, string TableName, int RootPage, string? Sql);

        /// <summary>Reserves the next page number.</summary>
        public int AllocatePage()
        {
            int page = _pages.Count + 1;
            _pages.Add(new byte[PageSize]);
            return page;
        }

        /// <summary>Writes page 1: the <c>sqlite_master</c> table.</summary>
        public void WriteSchemaPage(IReadOnlyList<SchemaRow> rows)
        {
            List<TableCell> cells = [];
            for (int i = 0; i < rows.Count; i++)
            {
                SchemaRow row = rows[i];
                // sqlite_master's columns are declared TEXT, so its string columns are stored as TEXT (serial type
                // 13+2n), unlike the index tables whose columns hold blobs.
                byte[] record = BuildRecord(
                [
                    Column.Text(Encoding.UTF8.GetBytes(row.Type)),
                    Column.Text(Encoding.UTF8.GetBytes(row.Name)),
                    Column.Text(Encoding.UTF8.GetBytes(row.TableName)),
                    Column.Integer(row.RootPage),
                    row.Sql is null ? Column.Null() : Column.Text(Encoding.UTF8.GetBytes(row.Sql))
                ]);

                cells.Add(new TableCell(record, i + 1, record));
            }

            WriteTableLeafPage(1, cells);
        }

        /// <summary>
        ///     Writes a table and its <c>sqlite_autoindex</c>: the table is a rowid B-tree in the order the rows are
        ///     given, the index is a key B-tree sorted by the (obfuscated) key bytes.
        /// </summary>
        public void WriteTable(int tableRoot, int indexRoot, List<Entry> entries)
        {
            // Table cells keep the insertion order (that is what makes the rowids ascending) and get sequential
            // rowids; the b-tree itself is still built in key order.
            List<TableCell> tableCells = new(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                long rowid = i + 1;
                tableCells.Add(new TableCell(entries[i].Key, rowid,
                    BuildRecord([Column.Blob(entries[i].Key), Column.Blob(entries[i].Value)])));
            }

            BuildTableTree(tableRoot, tableCells);

            List<IndexCell> indexCells = new(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                long rowid = i + 1;
                indexCells.Add(new IndexCell(entries[i].Key, rowid,
                    BuildRecord([Column.Blob(entries[i].Key), Column.Integer(rowid)])));
            }

            // A PRIMARY KEY on a column that holds blobs is compared with memcmp, so the index is sorted by the
            // obfuscated bytes. Measured on the live file: stored order is ascending in raw bytes and is not
            // ascending in decoded bytes.
            indexCells.Sort(static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
            BuildIndexTree(indexRoot, indexCells);
        }

        private readonly record struct TableCell(byte[] Key, long Rowid, byte[] Record);

        /// <summary>
        ///     One entry of the index B-tree. The same entry can be stored as a leaf cell or, when it is promoted,
        ///     as an interior cell; <see cref="Record" /> is what the cell carries either way.
        /// </summary>
        private readonly record struct IndexCell(byte[] Key, long Rowid, byte[] Record);

        // ---- table b-tree ----

        private void BuildTableTree(int root, List<TableCell> cells)
        {
            // A rowid table is keyed by the rowid, so its B-tree is ordered by rowid - not by the first column.
            // Sorting by the path instead makes SQLite report "Rowid N out of order" and refuses the file.
            cells.Sort(static (a, b) => a.Rowid.CompareTo(b.Rowid));

            List<List<TableCell>> leaves = Pack(cells,
                static c => Varint(c.Record.Length).Length + Varint(c.Rowid).Length + c.Record.Length);

            if (leaves.Count == 1)
            {
                WriteTableLeafPage(root, leaves[0]);
                return;
            }

            // An interior cell's key is the rowid of the largest entry in the LEFT child's subtree. A rowid table
            // is a B+ tree: the key is a copy of a row that stays in the leaf, not an entry that moves up (unlike
            // the index B-trees, where interior keys are distinct entries).
            List<(long LastRowid, int Page)> level = [];
            foreach (List<TableCell> leaf in leaves)
            {
                int page = AllocatePage();
                WriteTableLeafPage(page, leaf);
                level.Add((leaf[^1].Rowid, page));
            }

            while (true)
            {
                List<List<(long LastRowid, int Page)>> groups = Pack(level,
                    static c => 4 + Varint(c.LastRowid).Length);

                if (groups.Count == 1)
                {
                    WriteTableInteriorPage(root, groups[0]);
                    return;
                }

                List<(long LastRowid, int Page)> next = [];
                foreach (List<(long LastRowid, int Page)> group in groups)
                {
                    int page = AllocatePage();
                    WriteTableInteriorPage(page, group);
                    next.Add((group[^1].LastRowid, page));
                }

                level = next;
            }
        }

        private void WriteTableLeafPage(int pageNumber, List<TableCell> cells)
        {
            byte[] page = GetPage(pageNumber);
            int headerOffset = pageNumber == 1 ? DatabaseHeaderSize : 0;
            int contentStart = PageSize;

            for (int i = 0; i < cells.Count; i++)
            {
                byte[] payloadLength = Varint(cells[i].Record.Length);
                byte[] rowid = Varint(cells[i].Rowid);
                byte[] cell = new byte[payloadLength.Length + rowid.Length + cells[i].Record.Length];
                payloadLength.CopyTo(cell, 0);
                rowid.CopyTo(cell, payloadLength.Length);
                cells[i].Record.CopyTo(cell, payloadLength.Length + rowid.Length);

                contentStart -= cell.Length;
                cell.CopyTo(page, contentStart);
                BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(headerOffset + PageHeaderSize + i * 2),
                    (ushort)contentStart);
            }

            page[headerOffset] = 0x0D; // table leaf
            BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(headerOffset + 1), 0); // no freeblocks
            BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(headerOffset + 3), (ushort)cells.Count);
            BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(headerOffset + 5), (ushort)contentStart);
            page[headerOffset + 7] = 0; // fragmented free bytes
        }

        private void WriteTableInteriorPage(int pageNumber, List<(long LastRowid, int Page)> children)
        {
            byte[] page = GetPage(pageNumber);
            int contentStart = PageSize;

            // The key of a table interior cell is the rowid of the last entry in the left child's subtree; the
            // right-most child is a page pointer in the header instead of a cell, so the loop stops one short.
            for (int i = 0; i < children.Count - 1; i++)
            {
                byte[] rowid = Varint(children[i].LastRowid);
                byte[] cell = new byte[4 + rowid.Length];
                BinaryPrimitives.WriteUInt32BigEndian(cell, (uint)children[i].Page);
                rowid.CopyTo(cell, 4);
                contentStart -= cell.Length;
                cell.CopyTo(page, contentStart);
                BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(InteriorCellPointerStart + i * 2),
                    (ushort)contentStart);
            }

            page[0] = 0x05; // table interior
            BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(1), 0);
            BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(3), (ushort)(children.Count - 1));
            BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(5), (ushort)contentStart);
            page[7] = 0;
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(PageHeaderSize), (uint)children[^1].Page);
        }

        // ---- index b-tree ----

        /// <summary>
        ///     Builds an index B-tree.
        ///     <para>
        ///         An index interior cell holds a <b>distinct</b> key, not a copy of one of its children's keys:
        ///         measured on the live <c>sqlite_autoindex_info0_1</c>, the leaf keys (231,624) plus the interior
        ///         keys (3,286) add up to exactly the table's row count (234,910). The entry promoted into the
        ///         parent is therefore <i>removed</i> from the child page. Getting this wrong duplicates rows.
        ///     </para>
        /// </summary>
        private void BuildIndexTree(int root, List<IndexCell> cells)
        {
            // Level 0: the leaves. Between two consecutive leaf pages one entry is promoted out of the earlier
            // page and becomes the separator cell in the parent; it is not duplicated.
            List<List<IndexCell>> groups = Pack(cells,
                static c => Varint(c.Record.Length).Length + c.Record.Length);

            if (groups.Count == 1)
            {
                WriteIndexLeafPage(root, groups[0]);
                return;
            }

            List<IndexCell> separators = new(groups.Count - 1);
            List<int> children = new(groups.Count);
            for (int i = 0; i < groups.Count; i++)
            {
                List<IndexCell> group = groups[i];
                if (i < groups.Count - 1)
                {
                    separators.Add(group[^1]);
                    group.RemoveAt(group.Count - 1);
                }

                int page = AllocatePage();
                WriteIndexLeafPage(page, group);
                children.Add(page);
            }

            // Level 1 and up. A node holding k separator cells has k+1 children, so the invariant
            // (separators == children - 1) carries up unchanged.
            while (true)
            {
                int capacity = InteriorCapacity(separators);
                if (separators.Count <= capacity)
                {
                    WriteIndexInteriorPage(root, separators, children);
                    return;
                }

                // Split into nodes of at most `capacity` separators. The separator that ends a node is NOT part of
                // that node: it moves up to the parent and separates this node from the next, so every entry is
                // stored exactly once. Reusing it as the node's own last cell duplicates it and leaves the next
                // node's first child unreachable, which SQLite reports as "2nd reference to page N".
                List<IndexCell> parentSeparators = [];
                List<int> parentChildren = [];
                int start = 0;
                while (start < separators.Count)
                {
                    int take = Math.Min(capacity, separators.Count - start);
                    bool hasParent = start + take < separators.Count;
                    if (hasParent)
                    {
                        take--;
                        parentSeparators.Add(separators[start + take]);
                    }

                    int page = AllocatePage();
                    WriteIndexInteriorPage(page,
                        separators.GetRange(start, take),
                        children.GetRange(start, take + 1));

                    parentChildren.Add(page);
                    start += take + (hasParent ? 1 : 0);
                }

                separators = parentSeparators;
                children = parentChildren;
            }
        }

        /// <summary>How many separator cells of this size fit into one interior page.</summary>
        private static int InteriorCapacity(List<IndexCell> separators)
        {
            if (separators.Count == 0)
            {
                return 1;
            }

            // Use the largest cell of the level so the answer holds for every cell in it.
            int largest = 0;
            foreach (IndexCell cell in separators)
            {
                int size = 4 + Varint(cell.Record.Length).Length + cell.Record.Length;
                if (size > largest)
                {
                    largest = size;
                }
            }

            int capacity = (PageSize - PageHeaderSize - 4) / (largest + 2);
            return Math.Max(1, capacity);
        }

        private void WriteIndexLeafPage(int pageNumber, List<IndexCell> cells)
        {
            byte[] page = GetPage(pageNumber);
            int contentStart = PageSize;

            for (int i = 0; i < cells.Count; i++)
            {
                byte[] payloadLength = Varint(cells[i].Record.Length);
                byte[] cell = new byte[payloadLength.Length + cells[i].Record.Length];
                payloadLength.CopyTo(cell, 0);
                cells[i].Record.CopyTo(cell, payloadLength.Length);

                contentStart -= cell.Length;
                cell.CopyTo(page, contentStart);
                BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(PageHeaderSize + i * 2), (ushort)contentStart);
            }

            page[0] = 0x0A; // index leaf
            BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(1), 0);
            BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(3), (ushort)cells.Count);
            BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(5), (ushort)contentStart);
            page[7] = 0;
        }

        /// <param name="separators">One separator entry per cell (one fewer than there are children).</param>
        /// <param name="children">The child pages, one more than there are separators.</param>
        private void WriteIndexInteriorPage(int pageNumber, List<IndexCell> separators, List<int> children)
        {
            if (separators.Count != children.Count - 1)
            {
                throw new InvalidOperationException(
                    $"An index interior page needs one more child than separators ({separators.Count} vs {children.Count}).");
            }

            byte[] page = GetPage(pageNumber);
            int contentStart = PageSize;

            // Each cell is a 4 byte left child pointer followed by the separator's record; the right-most child
            // is a page pointer in the header instead of a cell. The trailing (children.Count)th child gets no
            // cell of its own, so the loop runs one short of the child count.
            for (int i = 0; i < separators.Count; i++)
            {
                byte[] record = separators[i].Record;
                byte[] payloadLength = Varint(record.Length);
                byte[] cell = new byte[4 + payloadLength.Length + record.Length];
                BinaryPrimitives.WriteUInt32BigEndian(cell, (uint)children[i]);
                payloadLength.CopyTo(cell, 4);
                record.CopyTo(cell, 4 + payloadLength.Length);
                contentStart -= cell.Length;
                cell.CopyTo(page, contentStart);
                BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(InteriorCellPointerStart + i * 2),
                    (ushort)contentStart);
            }

            page[0] = 0x02; // index interior
            BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(1), 0);
            BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(3), (ushort)separators.Count);
            BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(5), (ushort)contentStart);
            page[7] = 0;
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(PageHeaderSize), (uint)children[^1]);
        }

        // ---- shared ----

        /// <summary>Groups entries into pages, never leaving less room than the page header and cell pointers need.</summary>
        private static List<List<T>> Pack<T>(List<T> items, Func<T, int> cellSize)
        {
            List<List<T>> groups = [];
            List<T> current = [];
            int used = 0;
            foreach (T item in items)
            {
                int size = cellSize(item);
                if (size + 2 > PageSize - PageHeaderSize)
                {
                    throw new InvalidDataException(
                        $"A cell of the index does not fit into a {PageSize} byte page ({size} bytes).");
                }

                if (current.Count > 0 && used + size + 2 > PageSize - PageHeaderSize)
                {
                    groups.Add(current);
                    current = [];
                    used = 0;
                }

                current.Add(item);
                used += size + 2;
            }

            if (current.Count > 0)
            {
                groups.Add(current);
            }

            if (groups.Count == 0)
            {
                groups.Add([]);
            }

            return groups;
        }

        private byte[] GetPage(int pageNumber)
        {
            while (_pages.Count < pageNumber)
            {
                _pages.Add(new byte[PageSize]);
            }

            return _pages[pageNumber - 1];
        }

        /// <summary>Writes the file: the 100 byte database header followed by every page.</summary>
        public void Finish()
        {
            byte[] header = _pages[0];
            "SQLite format 3\0"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(16), PageSize);
            header[18] = 2; // write version: WAL
            header[19] = 2; // read version: WAL
            header[20] = 0; // reserved bytes per page
            header[21] = 64; // maximum embedded payload fraction
            header[22] = 32; // minimum embedded payload fraction
            header[23] = 32; // leaf payload fraction
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), 3); // change counter
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), (uint)_pages.Count); // database size in pages
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(40), 2); // schema cookie
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(44), 4); // schema format
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56), 1); // UTF-8
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(92), 3); // version-valid-for
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(96), 3_045_001); // sqlite version

            _stream.SetLength(0);
            foreach (byte[] page in _pages)
            {
                _stream.Write(page);
            }

            _stream.Flush();
        }

        /// <summary>
        ///     Builds a record: the header (a header-length varint followed by one serial type per column), then the
        ///     column bodies in order. <c>null</c> is SQL NULL.
        ///     <para>
        ///         Integers and blobs are <b>not</b> interchangeable here: <c>sqlite_master.rootpage</c> must be an
        ///         integer, and a writer that emits every column as a blob produces a schema SQLite rejects with
        ///         <c>malformed database schema - invalid rootpage</c> (measured 2026-10-25).
        ///     </para>
        /// </summary>
        private static byte[] BuildRecord(IReadOnlyList<Column> columns)
        {
            List<byte> types = [];
            int bodySize = 0;
            foreach (Column column in columns)
            {
                types.AddRange(Varint(column.SerialType));
                bodySize += column.Body.Length;
            }

            // The header size counts its own varint, which can change the total, so settle it by iterating.
            int headerSize = types.Count + 1;
            while (true)
            {
                int encoded = Varint(headerSize).Length;
                if (encoded + types.Count == headerSize)
                {
                    break;
                }

                headerSize = encoded + types.Count;
            }

            byte[] record = new byte[headerSize + bodySize];
            Varint(headerSize).CopyTo(record, 0);
            types.CopyTo(record, Varint(headerSize).Length);
            int at = headerSize;
            foreach (Column column in columns)
            {
                column.Body.CopyTo(record, at);
                at += column.Body.Length;
            }

            return record;
        }

        /// <summary>One column of a record: its serial type and the bytes that type describes.</summary>
        private readonly record struct Column(long SerialType, byte[] Body)
        {
            /// <summary>A blob column (both index columns and every meta value are stored as blobs).</summary>
            public static Column Blob(byte[] value) => new(12 + 2L * value.Length, value);

            /// <summary>A text column (used by <c>sqlite_master</c>, whose string columns are declared TEXT).</summary>
            public static Column Text(byte[] value) => new(13 + 2L * value.Length, value);

            /// <summary>An integer column, using the narrowest representation SQLite defines for it.</summary>
            public static Column Integer(long value)
            {
                if (value is >= -128 and <= 127)
                {
                    return new Column(1, [(byte)value]);
                }

                if (value is >= -32768 and <= 32767)
                {
                    byte[] two = new byte[2];
                    BinaryPrimitives.WriteInt16BigEndian(two, (short)value);
                    return new Column(2, two);
                }

                if (value is >= -8_388_608 and <= 8_388_607)
                {
                    return new Column(3, [(byte)(value >> 16), (byte)(value >> 8), (byte)value]);
                }

                if (value is >= int.MinValue and <= int.MaxValue)
                {
                    byte[] four = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(four, (int)value);
                    return new Column(4, four);
                }

                if (value is >= -140_737_488_355_328 and <= 140_737_488_355_327)
                {
                    byte[] six = new byte[6];
                    for (int i = 0; i < 6; i++)
                    {
                        six[i] = (byte)(value >> (8 * (5 - i)));
                    }

                    return new Column(5, six);
                }

                byte[] eight = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(eight, value);
                return new Column(6, eight);
            }

            /// <summary>SQL NULL.</summary>
            public static Column Null() => new(0, []);
        }

        /// <summary>
        ///     SQLite's variable length integer: one byte for 0..127, otherwise up to nine bytes of big-endian
        ///     base-128 with the high bit set on every byte but the last. The nine byte form ends with a full byte
        ///     rather than seven bits.
        ///     <para>
        ///         A shorter form that emits a bare <c>0x80</c> for 128 is not merely unusual - it makes SQLite read
        ///         the following header byte as part of the same varint, which surfaces as
        ///         <c>database disk image is malformed</c> (measured 2026-10-25).
        ///     </para>
        /// </summary>
        private static byte[] Varint(long value)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (value <= 0x7F)
            {
                return [(byte)value];
            }

            // How many seven bit groups the value needs.
            long remaining = value;
            int groups = 0;
            while (remaining > 0)
            {
                remaining >>= 7;
                groups++;
            }

            if (groups <= 8)
            {
                byte[] shortForm = new byte[groups];
                long copy = value;
                for (int i = groups - 1; i >= 0; i--)
                {
                    shortForm[i] = (byte)(copy & 0x7F);
                    copy >>= 7;
                }

                for (int i = 0; i < groups - 1; i++)
                {
                    shortForm[i] |= 0x80;
                }

                return shortForm;
            }

            byte[] longForm = new byte[9];
            long rest = value;
            longForm[8] = (byte)(rest & 0xFF);
            rest >>= 8;
            for (int i = 7; i >= 0; i--)
            {
                longForm[i] = (byte)((rest & 0x7F) | 0x80);
                rest >>= 7;
            }

            return longForm;
        }
    }
}
