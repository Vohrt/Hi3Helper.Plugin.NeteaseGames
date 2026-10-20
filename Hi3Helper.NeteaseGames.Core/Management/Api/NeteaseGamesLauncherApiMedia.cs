using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.NeteaseGames.Core.Management.Api;

/// <summary>
///     Provides the background image of a NetEase game.
///     <para>
///         <b>The host materialises the image itself</b>: the value returned by
///         <see cref="GetBackgroundEntries" /> must be a URL or a base64 payload - <b>never</b> a local file
///         path. A local path is not understood by the host, which leaves Collapse on its "loading background"
///         spinner forever (this was the bug behind the endless spinner).
///     </para>
///     <para>
///         The background is the official key visual of the game (the 1920x1080 splash artwork the client ships
///         in <c>LocalData\Splash.bmp</c>), embedded as a resource and handed over as a base64 payload. NetEase
///         publishes nothing usable over HTTP for this: every notice image is a 1026x246 banner strip and the
///         notice manifest carries no image URL at all.
///     </para>
/// </summary>
[GeneratedComClass]
public partial class NeteaseGamesLauncherApiMedia : LauncherApiMediaBase
{
    /// <summary>The logical name of the embedded key visual.</summary>
    public const string BackgroundResourceName = "background.jpg";

    private string? _backgroundData;

    /// <summary>
    ///     Creates the media API.
    /// </summary>
    /// <param name="gamePathProvider">A callback that returns the installation path, or null (unused today).</param>
    public NeteaseGamesLauncherApiMedia(Func<string?> gamePathProvider)
    {
        // The background is embedded, so neither the network nor the installation is touched.
    }

    /// <inheritdoc/>
    protected override HttpClient ApiResponseHttpClient
    {
        get => throw new NotSupportedException("The background is embedded, no HTTP client is used.");
        set { }
    }

    /// <inheritdoc/>
    protected override Task<int> InitAsync(CancellationToken token)
    {
        _backgroundData = ReadEmbeddedResourceAsBase64(BackgroundResourceName);
        if (string.IsNullOrEmpty(_backgroundData))
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGamesMedia] The embedded background '{Name}' could not be read", BackgroundResourceName);
        }

        return Task.FromResult(0);
    }

    /// <inheritdoc/>
    public override void GetBackgroundFlag(out LauncherBackgroundFlag result)
    {
        // A base64 payload the host writes itself, so no source flag is set.
        result = string.IsNullOrEmpty(_backgroundData)
            ? LauncherBackgroundFlag.None
            : LauncherBackgroundFlag.TypeIsImage;
    }

    /// <inheritdoc/>
    public override void GetLogoFlag(out LauncherBackgroundFlag result) => result = LauncherBackgroundFlag.None;

    /// <inheritdoc/>
    public override void GetBackgroundEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (string.IsNullOrEmpty(_backgroundData))
        {
            handle = 0;
            count = 0;
            isDisposable = false;
            isAllocated = false;
            return;
        }

        count = 1;
        PluginDisposableMemory<LauncherPathEntry> memory = PluginDisposableMemory<LauncherPathEntry>.Alloc(count);
        memory[0].Write(_backgroundData, Span<byte>.Empty);

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

    /// <summary>
    ///     Reads an embedded resource as base64 (the inline encoding the host understands).
    /// </summary>
    /// <param name="name">The logical resource name.</param>
    public static string? ReadEmbeddedResourceAsBase64(string name)
    {
        try
        {
            Assembly assembly = typeof(NeteaseGamesLauncherApiMedia).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return null;
            }

            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            return Convert.ToBase64String(buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _backgroundData = null;
        IsDisposed = true;
        GC.SuppressFinalize(this);
    }
}
