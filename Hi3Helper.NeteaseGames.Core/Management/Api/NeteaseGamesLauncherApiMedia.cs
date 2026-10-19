using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.NeteaseGames.Core.Management.Api;

/// <summary>
///     Provides the background image of a NetEase game.
///     <para>
///         The game caches the artwork of the notices inside Documents\notice; the most recent images of
///         that folder are used as the launcher background, so the official launcher is not needed to get a
///         proper looking page.
///     </para>
/// </summary>
[GeneratedComClass]
public partial class NeteaseGamesLauncherApiMedia : LauncherApiMediaBase
{
    private readonly Func<string?> _gamePathProvider;

    private List<string> _backgroundPaths = [];

    public NeteaseGamesLauncherApiMedia(Func<string?> gamePathProvider)
    {
        _gamePathProvider = gamePathProvider;
    }

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
    protected override Task<int> InitAsync(CancellationToken token)
    {
        _backgroundPaths = LoadBackgrounds();
        return Task.FromResult(0);
    }

    /// <inheritdoc/>
    public override void GetBackgroundFlag(out LauncherBackgroundFlag result)
    {
        result = _backgroundPaths.Count > 0
            ? LauncherBackgroundFlag.TypeIsImage | LauncherBackgroundFlag.IsSourceFile
            : LauncherBackgroundFlag.None;
    }

    /// <inheritdoc/>
    public override void GetLogoFlag(out LauncherBackgroundFlag result) => result = LauncherBackgroundFlag.None;

    /// <inheritdoc/>
    public override void GetBackgroundEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (_backgroundPaths.Count == 0)
        {
            handle = 0;
            count = 0;
            isDisposable = false;
            isAllocated = false;
            return;
        }

        count = _backgroundPaths.Count;
        PluginDisposableMemory<LauncherPathEntry> memory = PluginDisposableMemory<LauncherPathEntry>.Alloc(count);
        for (int i = 0; i < count; i++)
        {
            memory[i].Write(_backgroundPaths[i], Span<byte>.Empty);
        }

        handle = memory.AsSafePointer();
        isDisposable = true;
        isAllocated = true;
    }

    /// <inheritdoc/>
    public override void GetLogoOverlayEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        handle = 0;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }

    private List<string> LoadBackgrounds()
    {
        List<string> paths = [];
        string? gamePath = _gamePathProvider();
        if (string.IsNullOrEmpty(gamePath))
        {
            return paths;
        }

        string noticeDirectory = Path.Combine(gamePath, NeteaseGamesPaths.DocumentsRelativePath, "notice");
        if (!Directory.Exists(noticeDirectory))
        {
            return paths;
        }

        foreach ((DateTime _, string path) in NeteaseGamesLauncherApiNews.EnumerateImages(noticeDirectory))
        {
            try
            {
                if (new FileInfo(path).Length < 32 * 1024)
                {
                    // Skip the small icons; the background should be a real artwork.
                    continue;
                }
            }
            catch (Exception)
            {
                continue;
            }

            if (paths.Count >= 3)
            {
                break;
            }

            paths.Add(path);
        }

        return paths;
    }
}
