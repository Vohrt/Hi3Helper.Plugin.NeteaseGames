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
                    ReadPackages(ref reader, list);
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

    private static void ReadPackages(ref Utf8JsonReader reader, NeteaseGamesPatchList list)
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
                entry = new NeteaseGamesPackageEntry();
                list.Packages[name] = entry;
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

