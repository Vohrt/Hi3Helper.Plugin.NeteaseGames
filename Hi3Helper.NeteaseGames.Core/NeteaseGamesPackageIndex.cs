using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     The index of a patch package (<c>*.mpkinfo</c>).
///     <para>
///         The remote index files are transported in the <c>HEX_</c> envelope; after decrypting them the
///         payload is a small custom binary format:
///         <code>
///         u32 version (= 1)
///         u32 entry count
///         repeated:
///             u16 length of the path
///             the path (UTF-8)
///             u32 offset inside the package
///             u32 size of the file
///             u32 flags (always 2000)
///         16 bytes of trailer
///         </code>
///         The package itself is a plain archive: the file content is stored at the given offset with the
///         given size, so no unpacking is needed.
///     </para>
/// </summary>
public sealed class NeteaseGamesPackageIndex
{
    /// <summary>The entries of the index.</summary>
    public List<NeteaseGamesPackageIndexEntry> Entries { get; } = [];

    /// <summary>
    ///     Parses the (already decrypted) index payload.
    /// </summary>
    /// <param name="payload">The decrypted payload.</param>
    public static NeteaseGamesPackageIndex Parse(ReadOnlySpan<byte> payload)
    {
        NeteaseGamesPackageIndex index = new();
        if (payload.Length < 8)
        {
            return index;
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (version != 1)
        {
            return index;
        }

        int offset = 8;
        for (uint i = 0; i < count; i++)
        {
            if (offset + 2 > payload.Length)
            {
                break;
            }

            int pathLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
            offset += 2;
            if (pathLength <= 0 || offset + pathLength + 12 > payload.Length)
            {
                break;
            }

            string path = Encoding.UTF8.GetString(payload.Slice(offset, pathLength));
            offset += pathLength;

            uint fileOffset = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
            uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 4)..]);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 8)..]);
            offset += 12;

            index.Entries.Add(new NeteaseGamesPackageIndexEntry(path, fileOffset, fileSize, flags));
        }

        return index;
    }
}

/// <summary>One file inside a patch package.</summary>
/// <param name="Path">The path of the file (relative to the game directory).</param>
/// <param name="Offset">The offset of the content inside the package.</param>
/// <param name="Size">The size of the content.</param>
/// <param name="Flags">The flags of the entry.</param>
public readonly record struct NeteaseGamesPackageIndexEntry(string Path, uint Offset, uint Size, uint Flags);
