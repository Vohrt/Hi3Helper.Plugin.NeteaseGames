using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     The shader cache list (<c>bigshadercachelist_dx12.txt</c>), which is the authority for the shader tier.
///     <para>
///         The file is a <c>HEX_</c> envelope around a JSON document whose <c>file_list</c> object maps every shader
///         resource path to <c>[hash, size]</c>. It plays two roles at once:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>It names the shader pool.</b> The pool manifest (<c>big_{version}/bigshadercachelist_dx12.txt</c>
///             is the list itself; the chunks are <c>big_{version}/big_shader/dx12_N</c>) lists exactly the same
///             173,859 paths - measured 2026-10-26: the pool's resource set and this file's key set are equal.
///         </item>
///         <item>
///             <b>It carries the per-resource size and hash</b> the shader index stores. Measured: all 173,859
///             sizes and all 173,859 hashes of the live <c>ResourceExA.patchinfo</c> equal the values here.
///         </item>
///     </list>
///     <para>
///         ⚠ The hash is <b>per resource</b>, not per chunk: comparing the shader index against the chunk hash of
///         <c>big_file_list</c> matches 0 of 173,859 times, while comparing it against this file matches every time.
///     </para>
/// </summary>
public sealed class NeteaseGamesShaderCacheList
{
    /// <summary>The name of the file inside a <c>big_</c> directory.</summary>
    public const string FileName = "bigshadercachelist_dx12.txt";

    private readonly Dictionary<string, NeteaseGamesShaderResource> _resources = new(StringComparer.Ordinal);

    /// <summary>Every shader resource the list describes.</summary>
    public IReadOnlyDictionary<string, NeteaseGamesShaderResource> Resources => _resources;

    /// <summary>The number of resources.</summary>
    public int Count => _resources.Count;

    /// <summary>Parses a decrypted shader cache list.</summary>
    /// <param name="json">The decrypted JSON payload.</param>
    /// <exception cref="InvalidDataException">The payload is not a shader cache list.</exception>
    public static NeteaseGamesShaderCacheList Parse(ReadOnlySpan<byte> json)
    {
        NeteaseGamesShaderCacheList result = new();

        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("file_list", out JsonElement list) ||
            list.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The shader cache list carries no file_list object.");
        }

        foreach (JsonProperty entry in list.EnumerateObject())
        {
            // Each entry is [hash, size]; a malformed one is skipped rather than aborting the whole list.
            if (entry.Value.ValueKind != JsonValueKind.Array || entry.Value.GetArrayLength() < 2)
            {
                continue;
            }

            ulong hash = entry.Value[0].TryGetUInt64(out ulong parsedHash) ? parsedHash : 0;
            long size = entry.Value[1].TryGetInt64(out long parsedSize) ? parsedSize : 0;
            if (size <= 0)
            {
                continue;
            }

            result._resources[entry.Name] = new NeteaseGamesShaderResource(entry.Name, hash, size);
        }

        if (result._resources.Count == 0)
        {
            throw new InvalidDataException("The shader cache list describes no resources.");
        }

        return result;
    }

    /// <summary>Builds the path to size map the layout planner needs.</summary>
    public Dictionary<string, long> ToSizeMap()
    {
        Dictionary<string, long> sizes = new(_resources.Count, StringComparer.Ordinal);
        foreach (NeteaseGamesShaderResource resource in _resources.Values)
        {
            sizes[resource.Path] = resource.Size;
        }

        return sizes;
    }
}

/// <summary>One resource of the shader tier.</summary>
/// <param name="Path">The resource path (e.g. <c>Shader/dx12/734d6dcf….bin</c>).</param>
/// <param name="Hash">The 64 bit content hash the shader index stores for it.</param>
/// <param name="Size">The size in bytes.</param>
public readonly record struct NeteaseGamesShaderResource(string Path, ulong Hash, long Size);
