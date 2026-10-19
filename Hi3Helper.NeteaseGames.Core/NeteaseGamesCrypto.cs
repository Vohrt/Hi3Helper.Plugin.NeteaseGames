using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     Decrypts the two obfuscation layers NetEase uses on its launcher assets.
///     <list type="bullet">
///         <item><b>HEX_ manifest envelope</b>: "HEX_" + Base64( XOR( zlib(plaintext), KEY ) ), key = netease_joker_h74_encrypt_key.</item>
///         <item><b>LCFG launcher config</b>: "LCFG" | 0x01 | IV(16) | AES-256-CBC(no padding), key = constant in PCLauncher.exe.</item>
///     </list>
/// </summary>
public static class NeteaseGamesCrypto
{
    /// <summary>The cyclic XOR key of the HEX_ manifest envelope.</summary>
    public const string HexEnvelopeKey = "netease_joker_h74_encrypt_key";

    /// <summary>The magic bytes of the HEX_ manifest envelope (exactly four bytes).</summary>
    public static ReadOnlySpan<byte> HexEnvelopeMagic => "HEX_"u8;

    /// <summary>The magic bytes of the LCFG launcher config container.</summary>
    public static ReadOnlySpan<byte> LcfgMagic => "LCFG"u8;

    /// <summary>The expected LCFG container version.</summary>
    public const byte LcfgVersion = 0x01;

    /// <summary>Length of the LCFG header (magic + version).</summary>
    public const int LcfgHeaderLength = 5;

    /// <summary>Length of the in-band LCFG IV.</summary>
    public const int LcfgIvLength = 16;

    /// <summary>Decrypts a HEX_ manifest envelope.</summary>
    public static byte[] DecryptHexEnvelope(ReadOnlySpan<byte> file)
    {
        if (file.Length < HexEnvelopeMagic.Length || !file[..HexEnvelopeMagic.Length].SequenceEqual(HexEnvelopeMagic))
        {
            throw new InvalidDataException("Not a HEX_ manifest envelope.");
        }

        StringBuilder builder = new(file.Length - HexEnvelopeMagic.Length);
        foreach (char character in Encoding.ASCII.GetString(file[HexEnvelopeMagic.Length..]))
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        int remainder = builder.Length % 4;
        if (remainder != 0)
        {
            builder.Append('=', 4 - remainder);
        }

        byte[] compressed = Convert.FromBase64String(builder.ToString());

        ReadOnlySpan<byte> key = Encoding.ASCII.GetBytes(HexEnvelopeKey);
        for (int i = 0; i < compressed.Length; i++)
        {
            compressed[i] ^= key[i % key.Length];
        }

        using MemoryStream input = new(compressed, writable: false);
        using ZLibStream zlib = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Checks whether the given content is a HEX_ manifest envelope.</summary>
    public static bool IsHexEnvelope(ReadOnlySpan<byte> file)
        => file.Length > HexEnvelopeMagic.Length && file[..HexEnvelopeMagic.Length].SequenceEqual(HexEnvelopeMagic);

    /// <summary>Decrypts an LCFG launcher config container.</summary>
    public static byte[] DecryptLcfg(ReadOnlySpan<byte> file, ReadOnlySpan<byte> key)
    {
        if (file.Length < LcfgHeaderLength + LcfgIvLength || !file[..4].SequenceEqual(LcfgMagic))
        {
            throw new InvalidDataException("Not a LCFG container.");
        }

        if (file[4] != LcfgVersion)
        {
            throw new InvalidDataException($"LCFG version mismatch: expected 0x{LcfgVersion:X2}, got 0x{file[4]:X2}.");
        }

        if (key.Length != 32)
        {
            throw new ArgumentException("LCFG requires a 256-bit key.", nameof(key));
        }

        ReadOnlySpan<byte> iv = file.Slice(LcfgHeaderLength, LcfgIvLength);
        ReadOnlySpan<byte> cipher = file[(LcfgHeaderLength + LcfgIvLength)..];

        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        aes.IV = iv.ToArray();

        using ICryptoTransform transform = aes.CreateDecryptor();
        byte[] cipherCopy = cipher.ToArray();
        byte[] plain = new byte[cipherCopy.Length];
        int written = transform.TransformBlock(cipherCopy, 0, cipherCopy.Length, plain, 0);
        return written == plain.Length ? plain : plain[..written];
    }

    /// <summary>Derives the LCFG AES key from a launcher image by reading the constant of CfgObfuscateMgr.</summary>
    public static byte[] ReadLcfgKeyFromLauncher(ReadOnlySpan<byte> launcherImage, int keyRva)
        => ReadLcfgKeyFromLauncherAtOffset(launcherImage, RvaToFileOffset(launcherImage, keyRva));

    /// <summary>Reads the 32-byte LCFG AES key from a raw launcher image at a known file offset.</summary>
    public static byte[] ReadLcfgKeyFromLauncherAtOffset(ReadOnlySpan<byte> launcherImage, int keyOffset)
    {
        if (launcherImage.Length < keyOffset + 32)
        {
            throw new InvalidDataException("Launcher image is too small to contain the LCFG key.");
        }

        return launcherImage.Slice(keyOffset, 32).ToArray();
    }

    /// <summary>Translates an image RVA into a file offset using the PE section table.</summary>
    public static int RvaToFileOffset(ReadOnlySpan<byte> image, int rva)
    {
        if (image.Length < 0x40)
        {
            throw new InvalidDataException("Image is too small to be a PE file.");
        }

        int peOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(image[0x3C..0x40]);
        if (peOffset <= 0 || peOffset + 24 > image.Length)
        {
            throw new InvalidDataException("Invalid PE header offset.");
        }

        if (image[peOffset] != 'P' || image[peOffset + 1] != 'E' || image[peOffset + 2] != 0 || image[peOffset + 3] != 0)
        {
            throw new InvalidDataException("Invalid PE signature.");
        }

        int coffHeader = peOffset + 4;
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image[(coffHeader + 2)..(coffHeader + 4)]);
        int optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(image[(coffHeader + 16)..(coffHeader + 18)]);
        int sectionTable = peOffset + 24 + optionalHeaderSize;

        for (int i = 0; i < sectionCount; i++)
        {
            int section = sectionTable + (i * 40);
            if (section + 40 > image.Length)
            {
                break;
            }

            int virtualSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(image[(section + 8)..(section + 12)]);
            int virtualAddress = (int)BinaryPrimitives.ReadUInt32LittleEndian(image[(section + 12)..(section + 16)]);
            int sizeOfRawData = (int)BinaryPrimitives.ReadUInt32LittleEndian(image[(section + 16)..(section + 20)]);
            int pointerToRawData = (int)BinaryPrimitives.ReadUInt32LittleEndian(image[(section + 20)..(section + 24)]);

            int span = Math.Max(virtualSize, sizeOfRawData);
            if (rva >= virtualAddress && rva < virtualAddress + span)
            {
                return pointerToRawData + (rva - virtualAddress);
            }
        }

        throw new InvalidDataException($"RVA 0x{rva:X} is not mapped by any section.");
    }
}
