using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
///         The remote notice manifest (<c>OB_release_launcher_notice_info.xml</c>) is the source of truth: it
///         lists the announcements and activities that are currently published, with their title, dates and
///         notice id. The notice text is fetched from the per-notice JSON on demand. The game also caches the
///         notices it pulled into <c>Documents\notice\*.json</c>; that cache is used as a fallback so the page
///         is not empty while offline.
///     </para>
/// </summary>
[GeneratedComClass]
public partial class NeteaseGamesLauncherApiNews : LauncherApiNewsBase
{
    private readonly Func<string?> _gamePathProvider;
    private readonly HttpClient _httpClient;

    private List<NewsItem> _newsItems = [];
    private List<CarouselItem> _carouselItems = [];

    /// <summary>
    ///     Creates the news API.
    /// </summary>
    /// <param name="gamePathProvider">A callback that returns the installation path, or null.</param>
    public NeteaseGamesLauncherApiNews(Func<string?> gamePathProvider)
    {
        _gamePathProvider = gamePathProvider;
        _httpClient = NeteaseGamesHttp.CreateClient(TimeSpan.FromSeconds(20));
    }

    internal sealed record NewsItem(string Title, string Description, string Url, string Date, LauncherNewsEntryType Type);

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
        List<NewsItem> remote = [];
        try
        {
            Uri uri = NeteaseGamesEndpoints.GetManifestUri(
                NeteaseGamesEndpoints.PatchListBaseUrl,
                NeteaseGamesEndpoints.NoticeListName);
            string xml = await _httpClient.GetStringAsync(uri, token).ConfigureAwait(false);
            remote = ParseNoticeManifest(xml);
            SharedStatic.InstanceLogger?.LogInformation(
                "[NeteaseGamesNews] Loaded {Count} remote notice entries", remote.Count);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Xml.XmlException)
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGamesNews] Failed to fetch the remote notice manifest: {Msg}", ex.Message);
        }

        List<NewsItem> cached = LoadCachedNews();

        _newsItems = remote.Count > 0 ? remote : cached;
        _carouselItems = LoadCarousel(cached);
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
        // The social media icons of the official launcher are not shipped with the game; the remote notice
        // manifest only announces the links, not the icons.
        handle = 0;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }

    /// <summary>
    ///     Parses the remote notice manifest.
    ///     <para>
    ///         The document has <c>ActivityInfo</c> and <c>AnnounceInfo</c> sections; every item carries a
    ///         localized title token (<c>one:&lt;notice id&gt;</c>), a per-language URL and a validity window.
    ///     </para>
    /// </summary>
    /// <param name="xml">The document.</param>
    internal static List<NewsItem> ParseNoticeManifest(string xml)
    {
        List<NewsItem> items = [];
        if (string.IsNullOrWhiteSpace(xml))
        {
            return items;
        }

        XDocument document = XDocument.Parse(xml);
        XElement? root = document.Root;
        if (root is null)
        {
            return items;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ReadSection(root.Element("AnnounceInfo"), LauncherNewsEntryType.Notice, now, items);
        ReadSection(root.Element("ActivityInfo"), LauncherNewsEntryType.Event, now, items);
        return items;
    }

    private static void ReadSection(XElement? section, LauncherNewsEntryType type, long now, List<NewsItem> items)
    {
        if (section is null)
        {
            return;
        }

        foreach (XElement item in section.Elements())
        {
            long end = ReadTimestamp(item.Element("end_timestamp")?.Value);
            if (end > 0 && end < now)
            {
                // The notice is over.
                continue;
            }

            long begin = ReadTimestamp(item.Element("begin_timestamp")?.Value);
            if (begin > 0 && begin > now)
            {
                // The notice is not published yet.
                continue;
            }

            string? url = item.Elements("url")
                .FirstOrDefault(u => string.Equals(u.Attribute("title")?.Value, "zh_CN", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?? item.Element("url")?.Value;

            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            string title = item.Attribute("title")?.Value ?? string.Empty;
            items.Add(new NewsItem(FormatTitle(title), string.Empty, url.Trim(), string.Empty, type));
        }
    }

    /// <summary>
    ///     Turns a notice id token (<c>one:186</c>) into a readable title.
    /// </summary>
    private static string FormatTitle(string token)
    {
        int separator = token.IndexOf(':');
        return separator >= 0 && separator + 1 < token.Length ? $"公告 #{token[(separator + 1)..]}" : token;
    }

    private static long ReadTimestamp(string? value)
        => long.TryParse(value, out long parsed) ? parsed : 0;

    /// <summary>
    ///     Reads the notice cache the game writes into <c>Documents\notice\*.json</c>.
    /// </summary>
    private List<NewsItem> LoadCachedNews()
    {
        List<NewsItem> items = [];
        string noticeDirectory = GetNoticeDirectory();
        if (noticeDirectory.Length == 0)
        {
            return items;
        }

        foreach (string file in Directory.EnumerateFiles(noticeDirectory, "*.json"))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("detail", out JsonElement detail))
                {
                    continue;
                }

                string title = GetString(detail, "title");
                string text = GetString(detail, "text");
                string link = GetString(detail, "link");
                string date = GetString(detail, "date");

                if (title.Length == 0 && text.Length == 0)
                {
                    continue;
                }

                items.Add(new NewsItem(
                    title.Length > 0 ? title : date,
                    text,
                    link,
                    date,
                    LauncherNewsEntryType.Notice));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A broken notice file must not break the whole feed.
            }
        }

        items.Sort((left, right) => string.CompareOrdinal(right.Date, left.Date));
        return items;
    }

    /// <summary>
    ///     Enumerates the image URLs the notice manifest publishes (candidate launcher backgrounds).
    /// </summary>
    /// <param name="xml">The notice manifest.</param>
    public static IEnumerable<string> EnumerateNoticeImageUrls(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            yield break;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            yield break;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement item in document.Descendants())
        {
            string? url = item.Element("img")?.Value
                       ?? item.Attribute("img")?.Value
                       ?? item.Element("image")?.Value;

            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            url = url.Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !seen.Add(url))
            {
                continue;
            }

            yield return url;
        }
    }
    private List<CarouselItem> LoadCarousel(List<NewsItem> fallback)
    {
        List<CarouselItem> items = [];
        string noticeDirectory = GetNoticeDirectory();
        if (noticeDirectory.Length > 0)
        {
            foreach ((DateTime _, string path) in EnumerateImages(noticeDirectory))
            {
                if (items.Count >= 5)
                {
                    break;
                }

                items.Add(new CarouselItem(string.Empty, path, NeteaseGamesEndpoints.HomePageUrl));
            }
        }

        if (items.Count > 0)
        {
            return items;
        }

        // No local artwork yet: fall back to the published notices so the strip is not empty.
        foreach (NewsItem news in fallback)
        {
            if (items.Count >= 5)
            {
                break;
            }

            if (news.Url.Length > 0)
            {
                items.Add(new CarouselItem(news.Title, string.Empty, news.Url));
            }
        }

        return items;
    }

    private string GetNoticeDirectory()
    {
        string? gamePath = _gamePathProvider();
        if (string.IsNullOrEmpty(gamePath))
        {
            return string.Empty;
        }

        string noticeDirectory = Path.Combine(gamePath, NeteaseGamesPaths.DocumentsRelativePath, "notice");
        return Directory.Exists(noticeDirectory) ? noticeDirectory : string.Empty;
    }

    internal static List<(DateTime Modified, string Path)> EnumerateImages(string noticeDirectory)
    {
        List<(DateTime Modified, string Path)> images = [];
        foreach (string file in Directory.EnumerateFiles(noticeDirectory))
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".jpg" or ".jpeg" or ".png"))
            {
                continue;
            }

            try
            {
                images.Add((new FileInfo(file).LastWriteTimeUtc, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore unreadable files.
            }
        }

        images.Sort((left, right) => right.Modified.CompareTo(left.Modified));
        return images;
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <inheritdoc/>
    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
