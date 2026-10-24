using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     One entry of a package index (<c>*.mpkinfo</c>): a resource inside a shard.
/// </summary>
/// <param name="Name">The resource path (e.g. <c>00/37612f7af...</c>).</param>
/// <param name="Offset">The byte offset of the resource inside its shard.</param>
/// <param name="Size">The byte length of the resource.</param>
/// <param name="Marker">The shard id the resource is stored in.</param>
public readonly record struct NeteaseGamesIndexEntry(string Name, uint Offset, uint Size, uint Marker)
{
    /// <summary>The offset just past this resource.</summary>
    public long End => (long)Offset + Size;
}

/// <summary>
///     The payload span of a shard: how far the resources the index places inside it reach, and how many there
///     are.
/// </summary>
/// <param name="Marker">The shard id.</param>
/// <param name="End">The end offset of the last resource (<c>max(offset + size)</c>).</param>
/// <param name="EntryCount">How many resources the shard holds.</param>
public readonly record struct NeteaseGamesShardSpan(uint Marker, long End, int EntryCount);

/// <summary>
///     A package index (<c>script.mpkinfo</c> / <c>s_file.mpkinfo</c> / <c>indep.mpkinfo</c>).
///     <para>
///         The layout was verified on 2026-10-23 against all three live indexes: every entry of
///         <c>script.mpkinfo</c> (50,443 of them) parses and the parse consumes the payload to within a few
///         padding bytes.
///     </para>
///     <code>
///         u32 version = 1
///         u32 count
///         count times:
///             u16 nameLength
///             char[nameLength] name
///             u32 offset
///             u32 size
///         u32 marker      // the shard id, not a flag
///     </code>
///     <para>
///         <b>The marker is the shard id.</b> For every marker, <c>max(offset + size)</c> is how far the
///         <b>payload</b> of that shard reaches (verified on 2026-10-23: marker 2000 -&gt; 16,095,638 =
///         <c>Patch1000.mpk</c>, 2002 -&gt; 14,327,433 = <c>Patch1001.mpk</c>, 4000 -&gt; 15,656,291 =
///         <c>Patch2000.mpk</c>, 6000 -&gt; 45,261,483 = <c>Patch3000.mpk</c>).
///     </para>
///     <para>
///         ⚠ <b>The payload length is not always the file length.</b> Measured: <c>Patch1009.mpk</c> is
///         17,985,424 bytes on disk and on the content host while its index (marker 2018) ends at 17,923,658 -
///         the shard carries 61,766 bytes of tail padding past its last resource. A check that requires equality
///         reports a healthy shard as broken, so the value is a <b>lower bound</b>: shorter means truncated.
///     </para>
/// </summary>
public sealed class NeteaseGamesPackageIndex
{
    /// <summary>The only index version seen in the wild.</summary>
    public const uint SupportedVersion = 1;

    private readonly List<NeteaseGamesIndexEntry> _entries = [];
    private readonly Dictionary<uint, NeteaseGamesShardSpan> _spans = [];
    private readonly Dictionary<string, NeteaseGamesIndexEntry> _byName = new(StringComparer.Ordinal);

    /// <summary>Every resource the index describes.</summary>
    public IReadOnlyList<NeteaseGamesIndexEntry> Entries => _entries;

    /// <summary>The shard spans the index describes, keyed by marker (shard id).</summary>
    public IReadOnlyDictionary<uint, NeteaseGamesShardSpan> Spans => _spans;

    /// <summary>Looks a resource up by its path.</summary>
    /// <param name="name">The resource path.</param>
    public bool TryGetEntry(string name, out NeteaseGamesIndexEntry entry) => _byName.TryGetValue(name, out entry);

    /// <summary>
    ///     Parses a decrypted package index.
    /// </summary>
    /// <param name="data">The decrypted payload.</param>
    /// <returns>The index, or <c>null</c> when the payload is not a supported index.</returns>
    public static NeteaseGamesPackageIndex? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            return null;
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (version != SupportedVersion)
        {
            return null;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (count == 0 || count > 16_000_000)
        {
            return null;
        }

        NeteaseGamesPackageIndex index = new();
        int offset = 8;
        Dictionary<uint, long> ends = [];

        for (uint i = 0; i < count; i++)
        {
            if (offset + 2 > data.Length)
            {
                return null;
            }

            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            offset += 2;

            if (nameLength == 0 || offset + nameLength + 12 > data.Length)
            {
                return null;
            }

            string name = System.Text.Encoding.UTF8.GetString(data.Slice(offset, nameLength));
            offset += nameLength;

            uint entryOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            uint marker = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]);
            offset += 12;

            NeteaseGamesIndexEntry entry = new(name, entryOffset, size, marker);
            index._entries.Add(entry);
            index._byName[name] = entry;

            long end = entry.End;
            if (!ends.TryGetValue(marker, out long current) || end > current)
            {
                ends[marker] = end;
            }

            index._spanCounts.TryGetValue(marker, out int spanCount);
            index._spanCounts[marker] = spanCount + 1;
        }

        foreach ((uint marker, long end) in ends)
        {
            index._spans[marker] = new NeteaseGamesShardSpan(marker, end, index._spanCounts[marker]);
        }

        return index;
    }

    private readonly Dictionary<uint, int> _spanCounts = [];

    /// <summary>
    ///     Checks a shard on disk against the payload layout the index describes.
    ///     <para>
    ///         The shard must be at least as long as its payload (the largest <c>offset + size</c> recorded for its
    ///         marker). It may be longer: measured on 2026-10-23, <c>Patch1009.mpk</c> carries 61,766 bytes of tail
    ///         padding past its last resource. A shard that is shorter has lost data.
    ///     </para>
    /// </summary>
    /// <param name="marker">The shard id.</param>
    /// <param name="shardPath">The shard on disk.</param>
    public bool IsShardLongEnough(uint marker, string shardPath)
    {
        if (!_spans.TryGetValue(marker, out NeteaseGamesShardSpan span))
        {
            return false;
        }

        try
        {
            return File.Exists(shardPath) && new FileInfo(shardPath).Length >= span.End;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
///     The shard cluster manifest (<c>mpk_filelist.info</c>) that the vendor writes for the package tier of an
///     installation.
///     <para>
///         Verified on 2026-10-23: eight entries, and every size and MD5 matches the file on disk exactly. This
///         is the one place in the whole protocol where the vendor publishes a real content hash, so it is used
///         to verify the cluster.
///     </para>
///     <code>
///         &lt;cluster fingerprint&gt;
///         &lt;name&gt;\t&lt;size&gt;\t&lt;md5&gt;
///         ...
///     </code>
/// </summary>
public sealed class NeteaseGamesShardCluster
{
    /// <summary>One file of the cluster.</summary>
    /// <param name="Name">The file name, relative to the installation root.</param>
    /// <param name="Size">The announced size.</param>
    /// <param name="Md5">The announced MD5 (lowercase hex).</param>
    public readonly record struct FileEntry(string Name, long Size, string Md5);

    /// <summary>The files of the cluster.</summary>
    public List<FileEntry> Files { get; } = [];

    /// <summary>The fingerprint line of the manifest.</summary>
    public string Fingerprint { get; private set; } = string.Empty;

    /// <summary>
    ///     Parses a decrypted <c>mpk_filelist.info</c>.
    /// </summary>
    /// <param name="text">The decrypted text.</param>
    public static NeteaseGamesShardCluster Parse(string text)
    {
        NeteaseGamesShardCluster cluster = new();
        if (string.IsNullOrWhiteSpace(text))
        {
            return cluster;
        }

        bool first = true;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (first)
            {
                cluster.Fingerprint = line.Trim();
                first = false;
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 3 ||
                !long.TryParse(parts[1], out long size))
            {
                continue;
            }

            cluster.Files.Add(new FileEntry(parts[0], size, parts[2].Trim().ToLowerInvariant()));
        }

        return cluster;
    }

    /// <summary>
    ///     The file that describes the cluster inside an installation.
    /// </summary>
    public const string FileName = "mpk_filelist.info";
}
