using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     Decodes the obfuscation envelope NetEase uses on its launcher manifests.
///     <para>
///         <b>HEX_ manifest envelope</b>: <c>"HEX_"</c> + <c>Base64( XOR( zlib(plaintext) ) )</c>, where the XOR
///         key is the ASCII string <c>netease_joker_h74_encrypt_key</c> applied cyclically.
///     </para>
///     <para>
///         The same envelope without the magic is also used for the CPS channel tag of the launcher. That
///         variant is not needed by the plugin (the game writes its own state) and is deliberately not
///         implemented here - the reverse engineering record lives in <c>docs/re/netease-re.md</c>.
///     </para>
/// </summary>
public static class NeteaseGamesCrypto
{
    /// <summary>The cyclic XOR key of the HEX_ manifest envelope.</summary>
    private const string HexEnvelopeKey = "netease_joker_h74_encrypt_key";

    /// <summary>The magic bytes of the HEX_ manifest envelope (exactly four bytes).</summary>
    private static ReadOnlySpan<byte> HexEnvelopeMagic => "HEX_"u8;

    /// <summary>
    ///     Checks whether the given content is a HEX_ manifest envelope.
    /// </summary>
    /// <param name="file">The raw content.</param>
    public static bool IsHexEnvelope(ReadOnlySpan<byte> file)
        => file.Length > HexEnvelopeMagic.Length && file[..HexEnvelopeMagic.Length].SequenceEqual(HexEnvelopeMagic);

    /// <summary>
    ///     Decrypts a HEX_ manifest envelope into its plaintext.
    /// </summary>
    /// <param name="file">The raw content of the envelope.</param>
    /// <exception cref="InvalidDataException">The content is not a HEX_ envelope.</exception>
    public static byte[] DecryptHexEnvelope(ReadOnlySpan<byte> file)
    {
        if (!IsHexEnvelope(file))
        {
            throw new InvalidDataException("Not a HEX_ manifest envelope.");
        }

        return Inflate(XorKey(DecodeBase64Body(file[HexEnvelopeMagic.Length..])));
    }

    /// <summary>Decodes a base64 body, tolerating line breaks and a missing padding.</summary>
    private static byte[] DecodeBase64Body(ReadOnlySpan<byte> body)
    {
        StringBuilder builder = new(body.Length);
        foreach (char character in Encoding.ASCII.GetString(body))
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

        return Convert.FromBase64String(builder.ToString());
    }

    /// <summary>Applies the cyclic XOR of the envelope key.</summary>
    private static byte[] XorKey(byte[] data)
    {
        ReadOnlySpan<byte> key = Encoding.ASCII.GetBytes(HexEnvelopeKey);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= key[i % key.Length];
        }

        return data;
    }

    /// <summary>Inflates the zlib stream of the envelope.</summary>
    private static byte[] Inflate(byte[] compressed)
    {
        using MemoryStream input = new(compressed, writable: false);
        using ZLibStream zlib = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}
