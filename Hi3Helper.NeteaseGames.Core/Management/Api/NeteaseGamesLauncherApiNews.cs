using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.NeteaseGames.Core.Management.Api;

/// <summary>
///     Provides the news feed and the banner carousel of a NetEase game.
///     <para>
///         The remote notice manifest (<c>OB_release_launcher_notice_info.xml</c>) only carries the publication
///         window plus a per-language URL of every entry. <b>The readable title, the artwork and the click
///         target live in the per-entry document that URL points to</b>:
///     </para>
///     <code>
///     {"title":{"content":"…","date":"2026-10-19"},"text":"…","images":["https://…/image/….jpg"],"link":"https://…"}
///     </code>
///     <para>
///         Reading only the manifest is what produced the placeholder titles ("公告 #164") and the empty
///         carousel. Sections of the manifest:
///     </para>
///     <list type="bullet">
///         <item><c>AnnounceInfo</c> - announcements (公告)</item>
///         <item><c>ActivityInfo</c> - activities (活动)</item>
///         <item><c>BannerItem</c> - the key visuals of the carousel (1920x1080 artworks)</item>
///     </list>
///     <para>
///         Both feeds hand the host <b>URLs</b>: it downloads and caches the artwork itself.
///     </para>
/// </summary>
[GeneratedComClass]
public partial class NeteaseGamesLauncherApiNews : LauncherApiNewsBase
{
    /// <summary>The maximum number of carousel entries.</summary>
    public const int MaximumCarouselEntries = 6;

    /// <summary>The maximum number of news entries.</summary>
    public const int MaximumNewsEntries = 40;

    /// <summary>How long a cached entry document is reused.</summary>
    private static readonly TimeSpan DetailCacheLifetime = TimeSpan.FromHours(6);

    private readonly HttpClient _httpClient;
    private readonly string? _cacheDirectory;

    private List<NewsItem> _newsItems = [];
    private List<CarouselItem> _carouselItems = [];

    /// <summary>
    ///     Creates the news API.
    /// </summary>
    /// <param name="gamePathProvider">A callback that returns the installation path, or null (unused today).</param>
    public NeteaseGamesLauncherApiNews(Func<string?> gamePathProvider)
    {
        _httpClient = NeteaseGamesHttp.CreateClient(TimeSpan.FromSeconds(20));
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "CollapseNeteaseNotices");
    }

    /// <summary>An entry of the remote manifest, before its detail document is fetched.</summary>
    public sealed record NoticeEntry(string Id, string Url, bool IsBanner, long Begin, long End);

    /// <summary>The detail document of one entry.</summary>
    public sealed record NoticeDetail(string Title, string Date, string Link, List<string> Images);

    private sealed record NewsItem(string Title, string Description, string Url, string Date, LauncherNewsEntryType Type);

    private sealed record CarouselItem(string Description, string ImagePath, string ClickUrl);

    /// <inheritdoc/>
    protected override HttpClient ApiResponseHttpClient
    {
        get => _httpClient;
        set { }
    }

    /// <inheritdoc/>
    protected override async Task<int> InitAsync(CancellationToken token)
    {
        try
        {
            Uri manifestUri = NeteaseGamesEndpoints.GetManifestUri(
                NeteaseGamesEndpoints.PatchListBaseUrl,
                NeteaseGamesEndpoints.NoticeListName);

            string xml = await _httpClient.GetStringAsync(manifestUri, token).ConfigureAwait(false);
            List<NoticeEntry> entries = ParseManifest(xml);

            Dictionary<string, NoticeDetail> details = await ResolveDetailsAsync(entries, token).ConfigureAwait(false);

            _newsItems = BuildNews(entries, details);
            _carouselItems = BuildCarousel(entries, details);

            SharedStatic.InstanceLogger?.LogInformation(
                "[NeteaseGamesNews] {News} news entries and {Carousel} carousel entries from {Manifest} manifest entries",
                _newsItems.Count, _carouselItems.Count, entries.Count);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Xml.XmlException)
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGamesNews] The notice manifest could not be read: {Msg}", ex.Message);
        }

        return 0;
    }

    /// <inheritdoc/>
    public override void GetNewsEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (_newsItems.Count == 0)
        {
            handle = 0;
            count = 0;
            isDisposable = false;
            isAllocated = false;
            return;
        }

        count = _newsItems.Count;
        PluginDisposableMemory<LauncherNewsEntry> memory = PluginDisposableMemory<LauncherNewsEntry>.Alloc(count);
        for (int i = 0; i < count; i++)
        {
            NewsItem item = _newsItems[i];
            memory[i].Write(item.Title, item.Description, item.Url, item.Date, item.Type);
        }

        handle = memory.AsSafePointer();
        isDisposable = true;
        isAllocated = true;
    }

    /// <inheritdoc/>
    public override void GetCarouselEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (_carouselItems.Count == 0)
        {
            handle = 0;
            count = 0;
            isDisposable = false;
            isAllocated = false;
            return;
        }

        count = _carouselItems.Count;
        PluginDisposableMemory<LauncherCarouselEntry> memory = PluginDisposableMemory<LauncherCarouselEntry>.Alloc(count);
        for (int i = 0; i < count; i++)
        {
            CarouselItem item = _carouselItems[i];
            memory[i].Write(item.Description, item.ImagePath, item.ClickUrl);
        }

        handle = memory.AsSafePointer();
        isDisposable = true;
        isAllocated = true;
    }

    /// <inheritdoc/>
    public override void GetSocialMediaEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        // The social media entries of the manifest are links without an icon the host could render.
        handle = 0;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }

    /// <summary>
    ///     Parses the notice manifest into the entries that are currently published.
    /// </summary>
    /// <param name="xml">The manifest.</param>
    public static List<NoticeEntry> ParseManifest(string xml)
    {
        List<NoticeEntry> entries = [];
        if (string.IsNullOrWhiteSpace(xml))
        {
            return entries;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return entries;
        }

        XElement? root = document.Root;
        if (root is null)
        {
            return entries;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (XElement item in root.Descendants())
        {
            bool isBanner = item.Name.LocalName is "BannerItem" or "bannerItem";

            // Only the item elements carry a title attribute and a url child.
            if (item.Attribute("title") is null)
            {
                continue;
            }

            ReadItem(item, isBanner, now, seen, entries);
        }

        return entries;
    }

    private static void ReadItem(XElement item, bool isBanner, long now, HashSet<string> seen,
        List<NoticeEntry> entries)
    {
        long end = ReadTimestamp(item.Element("end_timestamp")?.Value);
        if (end > 0 && end < now)
        {
            return;
        }

        long begin = ReadTimestamp(item.Element("begin_timestamp")?.Value);
        if (begin > 0 && begin > now)
        {
            return;
        }

        string? url = item.Elements("url")
                          .FirstOrDefault(u => string.Equals(u.Attribute("title")?.Value, "zh_CN",
                              StringComparison.OrdinalIgnoreCase))
                          ?.Value
                      ?? item.Element("url")?.Value;

        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string id = item.Attribute("title")?.Value ?? url.Trim();
        if (!seen.Add(id))
        {
            return;
        }

        entries.Add(new NoticeEntry(id, url.Trim(), isBanner, begin, end));
    }

    private static long ReadTimestamp(string? value)
        => long.TryParse(value, out long parsed) ? parsed : 0;

    /// <summary>
    ///     Enumerates the artwork URLs the notice entries publish, banner entries first (they are the key
    ///     visuals of the game).
    /// </summary>
    /// <param name="xml">The notice manifest.</param>
    /// <returns>An empty sequence when the manifest cannot be read or publishes no artwork.</returns>
    public static IEnumerable<string> EnumerateNoticeImageUrls(string xml)
    {
        // The artwork is not part of the manifest, so this enumeration is only a hint; the caller resolves the
        // real artwork through the entry documents.
        foreach (NoticeEntry entry in ParseManifest(xml).Where(e => e.IsBanner))
        {
            yield return entry.Url;
        }
    }

    /// <summary>
    ///     Fetches the detail document of every entry, using a short lived cache so that a restart does not
    ///     re-request the same documents.
    /// </summary>
    private async Task<Dictionary<string, NoticeDetail>> ResolveDetailsAsync(List<NoticeEntry> entries,
        CancellationToken token)
    {
        Dictionary<string, NoticeDetail> details = new(StringComparer.Ordinal);
        if (entries.Count == 0)
        {
            return details;
        }

        using SemaphoreSlim gate = new(4);
        List<Task> tasks = [];
        foreach (NoticeEntry entry in entries)
        {
            tasks.Add(ResolveOneAsync(entry, gate, details, token));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return details;
    }

    private async Task ResolveOneAsync(NoticeEntry entry, SemaphoreSlim gate,
        Dictionary<string, NoticeDetail> details, CancellationToken token)
    {
        NoticeDetail? cached = TryReadCachedDetail(entry.Url);
        if (cached is not null)
        {
            lock (details)
            {
                details[entry.Url] = cached;
            }

            return;
        }

        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string json = await _httpClient.GetStringAsync(entry.Url, token).ConfigureAwait(false);
            NoticeDetail? detail = ParseDetail(json);
            if (detail is null)
            {
                return;
            }

            lock (details)
            {
                details[entry.Url] = detail;
            }

            WriteCachedDetail(entry.Url, json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGamesNews] The document of '{Id}' could not be read: {Msg}", entry.Id, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    ///     Parses one entry document.
    /// </summary>
    /// <param name="json">The document.</param>
    public static NoticeDetail? ParseDetail(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string title = string.Empty;
            string date = string.Empty;
            if (root.TryGetProperty("title", out JsonElement titleElement))
            {
                if (titleElement.ValueKind == JsonValueKind.Object)
                {
                    title = StripMarkup(GetString(titleElement, "content"));
                    date = GetString(titleElement, "date");
                }
                else if (titleElement.ValueKind == JsonValueKind.String)
                {
                    title = StripMarkup(titleElement.GetString() ?? string.Empty);
                }
            }

            List<string> images = [];
            if (root.TryGetProperty("images", out JsonElement imageElement) &&
                imageElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement image in imageElement.EnumerateArray())
                {
                    if (image.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? value = image.GetString();
                    if (!string.IsNullOrWhiteSpace(value) &&
                        value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        images.Add(value.Trim());
                    }
                }
            }

            string link = GetString(root, "link");

            if (title.Length == 0 && images.Count == 0 && link.Length == 0)
            {
                return null;
            }

            return new NoticeDetail(title, date, link, images);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    ///     Removes the inline markup some titles carry.
    /// </summary>
    private static string StripMarkup(string value)
    {
        if (value.Length == 0 || !value.Contains('<', StringComparison.Ordinal))
        {
            return value.Trim();
        }

        System.Text.StringBuilder builder = new(value.Length);
        bool inTag = false;
        foreach (char character in value)
        {
            if (character == '<')
            {
                inTag = true;
                continue;
            }

            if (character == '>')
            {
                inTag = false;
                continue;
            }

            if (!inTag)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim();
    }

    private static List<NewsItem> BuildNews(List<NoticeEntry> entries, Dictionary<string, NoticeDetail> details)
    {
        List<NewsItem> items = [];
        foreach (NoticeEntry entry in entries)
        {
            if (entry.IsBanner || !details.TryGetValue(entry.Url, out NoticeDetail? detail))
            {
                continue;
            }

            string title = detail.Title.Length > 0 ? detail.Title : detail.Date;
            if (title.Length == 0)
            {
                continue;
            }

            string url = detail.Link.Length > 0 ? detail.Link : entry.Url;
            items.Add(new NewsItem(title, string.Empty, url, detail.Date, LauncherNewsEntryType.Notice));

            if (items.Count >= MaximumNewsEntries)
            {
                break;
            }
        }

        // Newest first: the dates are ISO formatted, so an ordinal comparison orders them correctly.
        items.Sort((left, right) => string.CompareOrdinal(right.Date, left.Date));
        return items;
    }

    private static List<CarouselItem> BuildCarousel(List<NoticeEntry> entries,
        Dictionary<string, NoticeDetail> details)
    {
        List<CarouselItem> items = [];

        // Banner entries are the key visuals of the game.
        AddArtwork(entries.Where(e => e.IsBanner), details, items);

        // Then any other artwork the published notices carry.
        AddArtwork(entries.Where(e => !e.IsBanner), details, items);

        return items;
    }

    private static void AddArtwork(IEnumerable<NoticeEntry> entries, Dictionary<string, NoticeDetail> details,
        List<CarouselItem> items)
    {
        foreach (NoticeEntry entry in entries)
        {
            if (items.Count >= MaximumCarouselEntries)
            {
                return;
            }

            if (!details.TryGetValue(entry.Url, out NoticeDetail? detail))
            {
                continue;
            }

            foreach (string image in detail.Images)
            {
                if (items.Count >= MaximumCarouselEntries)
                {
                    return;
                }

                items.Add(new CarouselItem(detail.Title, image,
                    detail.Link.Length > 0 ? detail.Link : NeteaseGamesEndpoints.HomePageUrl));
            }
        }
    }

    #region Detail cache

    private string? GetCachePath(string url)
    {
        if (string.IsNullOrEmpty(_cacheDirectory))
        {
            return null;
        }

        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        return Path.Combine(_cacheDirectory, Convert.ToHexStringLower(hash)[..32] + ".json");
    }

    private NoticeDetail? TryReadCachedDetail(string url)
    {
        try
        {
            string? path = GetCachePath(url);
            if (path is null || !File.Exists(path))
            {
                return null;
            }

            if (DateTime.UtcNow - new FileInfo(path).LastWriteTimeUtc > DetailCacheLifetime)
            {
                return null;
            }

            return ParseDetail(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteCachedDetail(string url, string json)
    {
        try
        {
            string? path = GetCachePath(url);
            if (path is null)
            {
                return;
            }

            Directory.CreateDirectory(_cacheDirectory!);
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written only costs a re-request later.
        }
    }

    #endregion

    /// <inheritdoc/>
    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
