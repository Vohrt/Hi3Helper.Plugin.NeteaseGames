using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     The content endpoints of the NetEase download CDN.
///     <para>
///         The payload of the game (the file list, the mpk shards, the mini pack) does not live on the
///         update host but on the "game patch host" <c>h74.gph.netease.com</c>. The official launcher learns
///         the mirror hosts from a remote configuration document (see <see cref="NeteaseGamesHostOverride" />).
///     </para>
///     <para>
///         The layout of the content host is:
///         <code>
///         /&lt;version&gt;/&lt;file&gt;                  - the file lists (patchlist, sort map, preload list)
///         /&lt;version&gt;/&lt;section&gt;/&lt;file&gt;       - the mpk shards (section = mpk_script / mpk_medium / mpk_indep)
///         </code>
///         Every request has to carry the signature (<c>?key1=..&amp;key2=..</c>, see
///         <see cref="NeteaseGamesUrlSigner" />), otherwise the edge answers with <c>403 Forbidden</c>.
///     </para>
/// </summary>
public static class NeteaseGamesContentEndpoints
{
    /// <summary>The primary content host.</summary>
    public const string ContentHost = "h74.gph.netease.com";

    /// <summary>The mirror hosts of the content host (in the order of the remote configuration).</summary>
    public static readonly string[] ContentMirrorHosts =
    [
        "h74-21.gph.netease.com",
        "h74-19.gph.netease.com"
    ];

    /// <summary>
    ///     The host that serves the engine payload (the mini pack).
    ///     <para>
    ///         The payload lives below <c>/loose_file/mini_pack_&lt;version&gt;/</c> and is only available on the
    ///         game download host; the regular content hosts answer with 404 for it.
    ///     </para>
    /// </summary>
    public const string EnginePayloadHost = "h74.gdl.netease.com";

    /// <summary>
    ///     The hosts that may serve the engine payload.
    /// </summary>
    public static IReadOnlyList<string> GetEnginePayloadHosts() => [EnginePayloadHost];

    /// <summary>The name of the full file list.</summary>
    public const string PatchListFileName = "patchlist_windows64_normal.txt";

    /// <summary>The name of the file list that only contains the files that are stored inside the mpk shards.</summary>
    public const string PatchListMpkFileName = "patchlist_windows64_normal_mpk.txt";

    /// <summary>The name of the sort map.</summary>
    public const string SortMapFileName = "sort_map_windows64_normal.txt";

    /// <summary>The name of the preload list.</summary>
    public const string PreloadListFileName = "preload_list_windows64_normal.txt";

    /// <summary>
    ///     Builds the signed URL of a content object.
    /// </summary>
    /// <param name="version">The content version (the base version of the patch).</param>
    /// <param name="section">The section of the object (e.g. <c>mpk_script</c>); may be empty.</param>
    /// <param name="fileName">The name of the object.</param>
    /// <param name="now">The current time (the signature depends on it).</param>
    /// <param name="host">The host to use.</param>
    public static string GetContentUrl(string version, string? section, string fileName, DateTimeOffset now,
        string host = ContentHost)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        string path = $"/{version}";
        if (!string.IsNullOrEmpty(section))
        {
            path += $"/{section}";
        }

        path += $"/{fileName}";

        return NeteaseGamesUrlSigner.Sign($"https://{host}{path}", now);
    }

    /// <summary>
    ///     Returns all hosts that can serve the content (the primary host first).
    /// </summary>
    /// <param name="override">The remote host configuration (may be null).</param>
    public static IReadOnlyList<string> GetContentHosts(NeteaseGamesHostOverride? @override)
    {
        List<string> hosts = [ContentHost];
        if (@override is not null && @override.HostEntries.TryGetValue(ContentHost, out List<NeteaseGamesHostEntry>? entries))
        {
            foreach (NeteaseGamesHostEntry entry in entries)
            {
                if (!hosts.Contains(entry.Host, StringComparer.OrdinalIgnoreCase))
                {
                    hosts.Add(entry.Host);
                }            }
        }

        return hosts;
    }
}

/// <summary>One mirror entry of the remote host configuration.</summary>
/// <param name="Host">The host name.</param>
/// <param name="Weight">The weight of the host (the percentage of the requests it takes).</param>
public readonly record struct NeteaseGamesHostEntry(string Host, int Weight);

/// <summary>
///     The remote configuration of the download client (<c>HostEntry</c> / <c>HostResolve</c> / ...).
///     <para>
///         The official launcher downloads this document from the CDN and uses it to decide which mirror
///         host is used for which origin and which IP addresses the origin resolves to.
///     </para>
/// </summary>
public sealed class NeteaseGamesHostOverride
{
    /// <summary>The mirror hosts per origin host.</summary>
    public Dictionary<string, List<NeteaseGamesHostEntry>> HostEntries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The resolved addresses per host.</summary>
    public Dictionary<string, List<string>> HostResolves { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Parses the configuration document.
    /// </summary>
    /// <param name="json">The raw JSON payload.</param>
    public static NeteaseGamesHostOverride Parse(byte[] json)
    {
        NeteaseGamesHostOverride result = new();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("HostEntry", out JsonElement entries) && entries.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in entries.EnumerateObject())
                {
                    List<NeteaseGamesHostEntry> mirrors = [];
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in property.Value.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2)
                            {
                                continue;
                            }

                            string host = item[0].GetString() ?? string.Empty;
                            int weight = item[1].ValueKind == JsonValueKind.Number
                                ? item[1].GetInt32()
                                : 0;
                            if (host.Length > 0)
                            {
                                mirrors.Add(new NeteaseGamesHostEntry(host, weight));
                            }
                        }
                    }

                    result.HostEntries[property.Name] = mirrors;
                }
            }

            if (root.TryGetProperty("HostResolve", out JsonElement resolves) && resolves.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in resolves.EnumerateObject())
                {
                    List<string> addresses = [];
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in property.Value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } address)
                            {
                                addresses.Add(address);
                            }
                        }
                    }

                    result.HostResolves[property.Name] = addresses;
                }
            }
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }
}

/// <summary>
///     The difference between the local and the remote file list.
/// </summary>
public sealed class NeteaseGamesDelta
{
    /// <summary>Files that only exist in the remote list.</summary>
    public int AddedFiles { get; init; }

    /// <summary>Files whose content hash changed.</summary>
    public int ChangedFiles { get; init; }

    /// <summary>Files that only exist in the local list.</summary>
    public int RemovedFiles { get; init; }

    /// <summary>The total size of the loose files that have to be downloaded.</summary>
    public long LooseDownloadSize { get; init; }

    /// <summary>The mpk shards that have to be downloaded (the shard hash changed).</summary>
    public List<string> RequiredPackages { get; init; } = [];

    /// <summary>The total size of the mpk shards that have to be downloaded.</summary>
    public long PackageDownloadSize { get; init; }

    /// <summary>Whether there is anything to do at all.</summary>
    public bool HasChanges => AddedFiles > 0 || ChangedFiles > 0 || RemovedFiles > 0 || RequiredPackages.Count > 0;

    /// <summary>
    ///     Computes the difference between two file lists.
    /// </summary>
    /// <param name="local">The local file list.</param>
    /// <param name="remote">The remote file list.</param>
    public static NeteaseGamesDelta Compute(NeteaseGamesPatchList local, NeteaseGamesPatchList remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        int added = 0;
        int changed = 0;
        int removed = 0;
        long looseSize = 0;

        foreach ((string path, NeteaseGamesFileEntry entry) in remote.LooseFiles)
        {
            if (!local.LooseFiles.TryGetValue(path, out NeteaseGamesFileEntry localEntry))
            {
                added++;
                looseSize += entry.Size;
            }
            else if (localEntry.Hash != entry.Hash)
            {
                changed++;
                looseSize += entry.Size;
            }
        }

        foreach (string path in local.LooseFiles.Keys)
        {
            if (!remote.LooseFiles.ContainsKey(path))
            {
                removed++;
            }
        }

        List<string> packages = [];
        long packageSize = 0;
        foreach ((string name, NeteaseGamesPackageEntry entry) in remote.Packages)
        {
            // The hash list of a package contains the hashes of the files that are stored inside it,
            // so the whole list has to be compared - not only its first element.
            if (!local.Packages.TryGetValue(name, out NeteaseGamesPackageEntry? localEntry) ||
                !PackageEquals(localEntry, entry))
            {
                packages.Add(name);
                packageSize += entry.TotalSize;
            }
        }

        return new NeteaseGamesDelta
        {
            AddedFiles = added,
            ChangedFiles = changed,
            RemovedFiles = removed,
            LooseDownloadSize = looseSize,
            RequiredPackages = packages,
            PackageDownloadSize = packageSize
        };
    }
    /// <summary>
    ///     Compares the hash lists of two packages.
    /// </summary>
    private static bool PackageEquals(NeteaseGamesPackageEntry left, NeteaseGamesPackageEntry right)
    {
        if (left.Hashes.Count != right.Hashes.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Hashes.Count; i++)
        {
            if (left.Hashes[i] != right.Hashes[i])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
///     The JSON part of the remote patch manifest (<c>patchmd5_windows64_normal.txt</c>).
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
    ///     Parses the manifest. The document starts with four numeric lines, the JSON part follows.
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
            if (root.TryGetProperty("normal", out JsonElement normal) &&
                normal.TryGetProperty("latest_pkgversion", out JsonElement latestElement) &&
                latestElement.ValueKind == JsonValueKind.Number)
            {
                latest = latestElement.GetInt64();
            }

            string baseVersion = string.Empty;
            string engineVersion = string.Empty;
            if (root.TryGetProperty("@metadata@", out JsonElement metadata))
            {
                if (metadata.TryGetProperty("@base_version@", out JsonElement baseElement) &&
                    baseElement.ValueKind == JsonValueKind.Number)
                {
                    baseVersion = baseElement.GetRawText();
                }
                else if (metadata.TryGetProperty("@base_version@", out JsonElement baseString) &&
                         baseString.ValueKind == JsonValueKind.String)
                {
                    baseVersion = baseString.GetString() ?? string.Empty;
                }

                if (metadata.TryGetProperty("@engine_version@", out JsonElement engineElement) &&
                    engineElement.ValueKind == JsonValueKind.String)
                {
                    engineVersion = engineElement.GetString() ?? string.Empty;
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
