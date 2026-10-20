using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     A single loose file entry of the patch list: a 64-bit content hash plus the file size.
/// </summary>
public readonly record struct NeteaseGamesFileEntry(ulong Hash, long Size);

/// <summary>
///     A package (mpk shard) entry of the patch list. Every hash inside the list belongs to a file that is
///     stored inside that shard; the first hash is the hash of the shard itself.
/// </summary>
public sealed class NeteaseGamesPackageEntry
{
    public List<ulong> Hashes { get; } = [];

    public long TotalSize { get; set; }

    /// <summary>
    ///     The section the package belongs to (e.g. <c>mpk_script</c>). The content host stores the
    ///     packages in a directory with that name.
    /// </summary>
    public string Section { get; set; } = string.Empty;
}

/// <summary>
///     The remote mini-pack manifest (<c>release_release_mini_pack_version.json</c>).
///     <para>
///         This is the entry point of the update protocol: it announces the current remote version and the
///         list of payload blocks that have to be downloaded, plus the directories the payload is unpacked to.
///     </para>
/// </summary>
public sealed class NeteaseGamesMiniPackManifest
{
    /// <summary>
    ///     The payload blocks: (md5 of the block, size of the block).
    /// </summary>
    public List<(string Md5, long Size)> DownloadInfo { get; } = [];

    /// <summary>
    ///     Directory (relative to the content root) the loose files are unpacked into.
    /// </summary>
    public string LooseFileParentDir { get; set; } = string.Empty;

    /// <summary>
    ///     File name of the mini-pack manifest that describes the payload blocks.
    /// </summary>
    public string DataName { get; set; } = string.Empty;

    /// <summary>
    ///     The engine version the mini pack was built against.
    /// </summary>
    public string EngineVersion { get; set; } = string.Empty;

    /// <summary>
    ///     The remote content version (e.g. <c>202603182149</c>).
    /// </summary>
    public string CurrentVersion { get; set; } = string.Empty;

    /// <summary>
    ///     Directory (relative to the content root) the mini pack payload is stored in.
    /// </summary>
    public string ParentDir { get; set; } = string.Empty;

    /// <summary>
    ///     The total size of all payload blocks.
    /// </summary>
    public long TotalDownloadSize
    {
        get
        {
            long total = 0;
            foreach ((_, long size) in DownloadInfo)
            {
                total += size;
            }

            return total;
        }
    }

    /// <summary>
    ///     Parses the manifest.
    /// </summary>
    /// <param name="json">The raw JSON payload.</param>
    public static NeteaseGamesMiniPackManifest Parse(byte[] json)
    {
        NeteaseGamesMiniPackManifest manifest = new();

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("download_info", out JsonElement downloadInfo) &&
            downloadInfo.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in downloadInfo.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2)
                {
                    continue;
                }

                string md5 = item[0].GetString() ?? string.Empty;
                long size = item[1].TryGetInt64(out long value) ? value : 0;
                manifest.DownloadInfo.Add((md5, size));
            }
        }

        manifest.LooseFileParentDir = GetString(root, "loose_file_parent_dir");
        manifest.DataName = GetString(root, "data_name");
        manifest.EngineVersion = GetString(root, "engine_version");
        manifest.CurrentVersion = GetString(root, "current_version");
        manifest.ParentDir = GetString(root, "parent_dir");
        return manifest;
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>
///     The full patch list (<c>patchlist_windows64_normal.txt</c>, 17 MB / 231k entries).
///     <para>
///         The document is parsed with a streaming <see cref="Utf8JsonReader" /> so that the DOM is never
///         materialised - a full DOM of this file would need hundreds of megabytes.
///     </para>
/// </summary>
public sealed class NeteaseGamesPatchList
{
    /// <summary>
    ///     Loose files (the <c>common</c> and <c>windows64_common</c> sections).
    /// </summary>
    public Dictionary<string, NeteaseGamesFileEntry> LooseFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Package shards (the <c>mpk_script</c>, <c>mpk_medium</c> and <c>mpk_indep</c> sections).
    /// </summary>
    public Dictionary<string, NeteaseGamesPackageEntry> Packages { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     The version of the path mapping that belongs to this patch list.
    /// </summary>
    public string MappingVersion { get; set; } = string.Empty;

    /// <summary>
    ///     Paths that are excluded from the mpk packages.
    /// </summary>
    public List<string> MpkExclude { get; } = [];

    /// <summary>
    ///     The sections that were present in the document.
    /// </summary>
    public List<string> FileSections { get; } = [];

    /// <summary>
    ///     Compares the hash lists of two packages.
    /// </summary>
    /// <param name="left">The first hash list.</param>
    /// <param name="right">The second hash list.</param>
    public static bool HashesEqual(List<ulong> left, List<ulong> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Parses the patch list.
    /// </summary>
    /// <param name="json">The raw (already decrypted) JSON payload.</param>
    public static NeteaseGamesPatchList Parse(byte[] json)
    {
        NeteaseGamesPatchList list = new();
        Utf8JsonReader reader = new(json, isFinalBlock: true, state: default);

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            string property = reader.GetString() ?? string.Empty;

            // Move the reader onto the value of the property.
            if (!reader.Read())
            {
                break;
            }

            switch (property)
            {
                case "common":
                case "windows64_common":
                    ReadLooseFiles(ref reader, list);
                    break;
                case "mpk_script":
                case "mpk_medium":
                case "mpk_indep":
                    ReadPackages(ref reader, list, property);
                    break;
                case "mapping_version":
                    list.MappingVersion = reader.GetString() ?? string.Empty;
                    break;
                case "file_sections":
                    ReadStringArray(ref reader, list.FileSections);
                    break;
                case "mpkexclude":
                    ReadStringArray(ref reader, list.MpkExclude);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return list;
    }

    private static void ReadLooseFiles(ref Utf8JsonReader reader, NeteaseGamesPatchList list)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            string path = reader.GetString() ?? string.Empty;
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip();
                continue;
            }

            ulong hash = 0;
            long size = 0;
            int index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.Number)
                {
                    continue;
                }

                if (index == 0)
                {
                    hash = reader.GetUInt64();
                }
                else if (index == 1)
                {
                    size = reader.GetInt64();
                }

                index++;
            }

            list.LooseFiles[path] = new NeteaseGamesFileEntry(hash, size);
        }
    }

    private static void ReadPackages(ref Utf8JsonReader reader, NeteaseGamesPatchList list, string section)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            string name = reader.GetString() ?? string.Empty;
            if (!list.Packages.TryGetValue(name, out NeteaseGamesPackageEntry? entry))
            {
                entry = new NeteaseGamesPackageEntry { Section = section };
                list.Packages[name] = entry;
            }
            else if (entry.Section.Length == 0)
            {
                entry.Section = section;
            }
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip();
                continue;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.Number)
                {
                    entry.Hashes.Add(reader.GetUInt64());
                }
            }

            // The last number of the list is the size of the package, the others are the hashes of the
            // files that are stored inside it.
            if (entry.Hashes.Count > 0)
            {
                entry.TotalSize = (long)entry.Hashes[^1];
                entry.Hashes.RemoveAt(entry.Hashes.Count - 1);
            }
        }
    }

    private static void ReadStringArray(ref Utf8JsonReader reader, List<string> target)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                target.Add(reader.GetString() ?? string.Empty);
            }
        }
    }
}

/// <summary>
///     The JSON part of the remote patch manifest (<c>patchmd5_windows64_normal.txt</c>).
///     <para>
///         The document starts with four numeric lines and is followed by a JSON object. The second line is the
///         hash of the remote file list, the <c>normal.latest_pkgversion</c> property of the JSON part carries
///         the authoritative package version.
///     </para>
/// </summary>
public sealed record NeteaseGamesPatchManifest
{
    /// <summary>The base version of the patch (the version directory of the content host).</summary>
    public string BaseVersion { get; init; } = string.Empty;

    /// <summary>The newest package version.</summary>
    public long LatestPackageVersion { get; init; }

    /// <summary>The engine version the patch belongs to.</summary>
    public string EngineVersion { get; init; } = string.Empty;

    /// <summary>The version of the patch list (the hash of the remote file list).</summary>
    public ulong PatchListHash { get; init; }

    /// <summary>
    ///     Parses the manifest.
    /// </summary>
    /// <param name="text">The raw text of the manifest.</param>
    public static NeteaseGamesPatchManifest Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        NeteaseGamesPatchManifest result = new();
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length >= 2 && ulong.TryParse(lines[1], out ulong patchListHash))
        {
            result = result with { PatchListHash = patchListHash };
        }

        int start = text.IndexOf('{');
        if (start < 0)
        {
            return result;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text[start..]);
            JsonElement root = document.RootElement;

            long latest = 0;
            string baseVersion = string.Empty;
            string engineVersion = string.Empty;
            if (root.TryGetProperty("@metadata@", out JsonElement metadata))
            {
                // The authoritative package version of the branch. `normal.latest_pkgversion` must NOT be
                // used: it carried a stale value from another branch (5282637) while the real version was
                // 6534552, which made the plugin report a permanent update.
                if (metadata.TryGetProperty("@private_res_version@", out JsonElement privateVersion))
                {
                    if (privateVersion.ValueKind == JsonValueKind.Number && privateVersion.TryGetInt64(out long parsedNumber))
                    {
                        latest = parsedNumber;
                    }
                    else if (privateVersion.ValueKind == JsonValueKind.String &&
                             long.TryParse(privateVersion.GetString(), out long parsedText))
                    {
                        latest = parsedText;
                    }
                }

                if (metadata.TryGetProperty("@base_version@", out JsonElement baseElement))
                {
                    baseVersion = baseElement.ValueKind switch
                    {
                        JsonValueKind.Number => baseElement.GetRawText(),
                        JsonValueKind.String => baseElement.GetString() ?? string.Empty,
                        _ => string.Empty
                    };
                }

                if (metadata.TryGetProperty("@engine_version@", out JsonElement engineElement) &&
                    engineElement.ValueKind == JsonValueKind.String)
                {
                    engineVersion = engineElement.GetString() ?? string.Empty;
                }
            }

            if (latest <= 0)
            {
                // Fall back to the fourth numeric line, which repeats the same version.
                string[] versionLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (int i = 2; i < versionLines.Length && i < 4; i++)
                {
                    if (long.TryParse(versionLines[i], out long candidate) && candidate > latest)
                    {
                        latest = candidate;
                    }
                }
            }

            result = result with
            {
                LatestPackageVersion = latest,
                BaseVersion = baseVersion,
                EngineVersion = engineVersion
            };
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }
}

