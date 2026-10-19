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
///         The news entries are read from the notice cache the game writes into Documents\notice\*.json
///         (the official launcher is not required for this), the banner carousel is built from the images of
///         the same folder and the remote notice manifest (release_launcher_notice_info.xml) tells which
///         banners are currently active.
///     </para>
/// </summary>
[GeneratedComClass]
public partial class NeteaseGamesLauncherApiNews : LauncherApiNewsBase
{
    private readonly Func<string?> _gamePathProvider;

    private List<NewsItem> _newsItems = [];
    private List<CarouselItem> _carouselItems = [];

    public NeteaseGamesLauncherApiNews(Func<string?> gamePathProvider)
    {
        _gamePathProvider = gamePathProvider;
    }

    private sealed record NewsItem(string Title, string Description, string Url, string Date, LauncherNewsEntryType Type);

    private sealed record CarouselItem(string Description, string ImagePath, string ClickUrl);

    [field: AllowNull]
    [field: MaybeNull]
    protected override HttpClient ApiResponseHttpClient
    {
        get
        {
            if (field is not null)
            {
                return field;
            }

            PluginHttpClientBuilder builder = new PluginHttpClientBuilder()
                .SetAllowedDecompression(System.Net.DecompressionMethods.All)
                .AllowRedirections()
                .AllowUntrustedCert()
                .AllowCookies()
                .SetUserAgent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CollapsePlugin/1.0");

            return field = builder.Create();
        }
        set;
    }

    /// <inheritdoc/>
    protected override async Task<int> InitAsync(CancellationToken token)
    {
        _newsItems = LoadNews();
        _carouselItems = LoadCarousel();

        try
        {
            Uri uri = NeteaseGamesEndpoints.GetManifestUri(
                NeteaseGamesEndpoints.PatchListBaseUrl,
                NeteaseGamesEndpoints.NoticeListName);
            string xml = await ApiResponseHttpClient.GetStringAsync(uri, token).ConfigureAwait(false);
            XDocument document = XDocument.Parse(xml);
            int banners = document.Descendants("BannerItem").Count();
            SharedStatic.InstanceLogger?.LogInformation(
                "[NeteaseGamesNews] Loaded {News} news entries, {Carousel} carousel entries, {Banners} remote banners",
                _newsItems.Count, _carouselItems.Count, banners);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger?.LogWarning("[NeteaseGamesNews] Failed to fetch the remote notice manifest: {Msg}", ex.Message);
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
        // The social media icons of the official launcher are not shipped with the game; the remote notice
        // manifest only announces the links, not the icons.
        handle = 0;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }

    private List<NewsItem> LoadNews()
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
            catch (Exception)
            {
                // A broken notice file must not break the whole feed.
            }
        }

        items.Sort((left, right) => string.CompareOrdinal(right.Date, left.Date));
        return items;
    }

    private List<CarouselItem> LoadCarousel()
    {
        List<CarouselItem> items = [];
        string noticeDirectory = GetNoticeDirectory();
        if (noticeDirectory.Length == 0)
        {
            return items;
        }

        foreach ((DateTime _, string path) in EnumerateImages(noticeDirectory))
        {
            if (items.Count >= 5)
            {
                break;
            }

            items.Add(new CarouselItem(string.Empty, path, "https://sea.163.com/news/"));
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
            catch (Exception)
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
}
