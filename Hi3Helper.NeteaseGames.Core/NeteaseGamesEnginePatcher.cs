using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     One file of the engine payload (the mini pack).
/// </summary>
/// <param name="RelativePath">The path relative to the engine directory (with backslashes).</param>
/// <param name="Hash">The 64 bit content hash of the file (the same hash the patch list uses).</param>
/// <param name="Size">The size of the file in bytes.</param>
public sealed record NeteaseGamesEngineFile(string RelativePath, ulong Hash, long Size)
{
    /// <summary>
    ///     The path of the file relative to the engine directory, with forward slashes.
    /// </summary>
    public string UrlPath => RelativePath.Replace('\\', '/');

    /// <summary>
    ///     The name the file has on disk.
    ///     <para>
    ///         The payload stores the newest generation of every file with a <c>.1</c> suffix
    ///         (e.g. <c>Binaries\Win64Release\cclibdll.dll.1</c>), the game uses the name without it.
    ///     </para>
    /// </summary>
    public string TargetName =>
        RelativePath.EndsWith(".1", StringComparison.Ordinal)
            ? RelativePath[..^2]
            : RelativePath;
}

/// <summary>
///     Applies the engine payload (the mini pack) to a local installation.
///     <para>
///         The payload is not a set of blocks but a list of loose files that are stored below
///         <c>/loose_file/mini_pack_&lt;version&gt;/</c> on the content host. The list itself is the plain text
///         file <c>md5_engine.txt</c> of that directory; every line is
///         <c>&lt;path&gt; &lt;hash&gt; &lt;size&gt;</c>.
///     </para>
///     <para>
///         The files are turned into items of a <see cref="Management.NeteaseGamesDownloadPlan" /> and moved into
///         place by <see cref="NeteaseGamesUpdateApplier" />, so the engine payload gets the same resume,
///         verification and write ordering as the rest of the patch. Nothing is buffered in memory here: the
///         payload contains files of more than 100 MB.
///     </para>
/// </summary>
public static class NeteaseGamesEnginePatcher
{
    /// <summary>The name of the file list inside the payload directory.</summary>
    public const string EngineFileListName = "md5_engine.txt";

    /// <summary>The prefix of the entries that describe files which have to be removed.</summary>
    public const string DeleteDirectoryPrefix = "used_del_dir";

    /// <summary>The directory of the engine inside the game directory.</summary>
    public const string EngineDirectoryName = "Engine";

    /// <summary>
    ///     Whether an entry of the file list is a marker that asks for a removal instead of a file.
    ///     <para>
    ///         ⚠ <b>No live entry is one (measured 2026-10-26).</b> All 10 <c>used_del_dir\mpk_res\…</c> entries are
    ///         real files that belong in the game root - see <see cref="ResolveTargetPath" />. The predicate is kept
    ///         because the prefix reads like a removal request, but nothing should act on it before a real removal
    ///         entry has been observed.
    ///     </para>
    /// </summary>
    /// <param name="file">The entry.</param>
    public static bool IsDeleteMarker(NeteaseGamesEngineFile file)
        => file.RelativePath.StartsWith(DeleteDirectoryPrefix, StringComparison.Ordinal);

    /// <summary>
    ///     The directory the <c>used_del_dir</c> entries are written to, relative to the game root.
    /// </summary>
    public const string RootResourceDirectoryName = "mpk_res";

    /// <summary>
    ///     Resolves the path an engine payload entry is written to, relative to the <b>game root</b>.
    ///     <para>
    ///         Ordinary entries live below <c>Engine\</c> and drop the <c>.1</c> generation suffix. The
    ///         <c>used_del_dir\mpk_res\</c> entries are the exception: despite the name they are not removals, they
    ///         are the root level resource packages. Measured 2026-10-26 - the three smallest were downloaded and
    ///         their MD5 matched the files in the game root byte for byte:
    ///     </para>
    ///     <code>
    ///         used_del_dir\mpk_res\ResourceExA.mpkinfo.1   ->  ResourceExA.mpkinfo
    ///         used_del_dir\mpk_res\Resources.mpkinfo.1      ->  Resources.mpkinfo
    ///         used_del_dir\mpk_res\package_resource.info.1  ->  package_resource.info
    ///     </code>
    /// </summary>
    /// <param name="file">The entry.</param>
    /// <returns>The path relative to the game root, with forward slashes.</returns>
    public static string ResolveTargetPath(NeteaseGamesEngineFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.RelativePath.StartsWith(DeleteDirectoryPrefix, StringComparison.Ordinal))
        {
            string rest = file.RelativePath[(DeleteDirectoryPrefix.Length + 1)..];
            int slash = rest.IndexOf('\\');
            if (slash >= 0 && string.Equals(rest[..slash], RootResourceDirectoryName, StringComparison.Ordinal))
            {
                rest = rest[(slash + 1)..];
            }

            return StripGenerationSuffix(rest).Replace('\\', '/');
        }

        return $"{EngineDirectoryName}/{StripGenerationSuffix(file.RelativePath).Replace('\\', '/')}";
    }

    private static string StripGenerationSuffix(string path)
        => path.EndsWith(".1", StringComparison.Ordinal) ? path[..^2] : path;

    /// <summary>
    ///     Parses the <c>md5_engine.txt</c> file list.
    /// </summary>
    /// <param name="text">The raw text.</param>
    public static IReadOnlyList<NeteaseGamesEngineFile> ParseEngineFileList(string text)
    {
        List<NeteaseGamesEngineFile> files = [];
        if (string.IsNullOrEmpty(text))
        {
            return files;
        }

        foreach (string line in text.Split('\n'))
        {
            string entry = line.TrimEnd('\r');
            if (entry.Length == 0)
            {
                continue;
            }

            int hashStart = entry.LastIndexOf(' ');
            if (hashStart <= 0)
            {
                continue;
            }

            int sizeStart = entry.LastIndexOf(' ', hashStart - 1);
            if (sizeStart <= 0)
            {
                continue;
            }

            string path = entry[..sizeStart];
            if (!ulong.TryParse(entry[(sizeStart + 1)..hashStart], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out ulong hash))
            {
                continue;
            }

            if (!long.TryParse(entry[(hashStart + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out long size))
            {
                continue;
            }

            files.Add(new NeteaseGamesEngineFile(path, hash, size));
        }

        return files;
    }

    /// <summary>
    ///     Downloads the file list of an engine payload.
    /// </summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="looseFileParentDir">The payload directory (e.g. <c>loose_file/mini_pack_202610151759</c>).</param>
    /// <param name="hosts">The hosts that may serve the payload.</param>
    /// <param name="token">The cancellation token.</param>
    /// <returns>The entries of the file list; an empty list when it could not be downloaded.</returns>
    /// <summary>
    ///     Downloads the raw <c>md5_engine.txt</c> so it can be written into the installation.
    ///     <para>
    ///         The game itself needs this file: the official installation carries
    ///         <c>Engine\md5_engine.txt</c> (21,811 bytes, measured 2026-10-26) and the plugin used to download the
    ///         list only to read it, never to keep it - so a fresh installation was missing the file the game
    ///         verifies its engine against.
    ///     </para>
    /// </summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="looseFileParentDir">The payload directory.</param>
    /// <param name="hosts">The hosts that may serve it.</param>
    /// <param name="token">Cancel token.</param>
    /// <returns>The file content, or <c>null</c> when no host served it.</returns>
    public static async Task<byte[]?> FetchFileListBytesAsync(
        HttpClient client,
        string looseFileParentDir,
        IReadOnlyList<string> hosts,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(looseFileParentDir);

        string listPath = $"/{looseFileParentDir.TrimStart('/')}/{EngineFileListName}";
        foreach (string host in hosts)
        {
            string url = NeteaseGamesUrlSigner.Sign($"https://{host}{listPath}", DateTimeOffset.UtcNow);
            try
            {
                using HttpResponseMessage response = await client.GetAsync(url, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                SharedStatic.InstanceLogger?.LogWarning(
                    "[NeteaseGames] The engine file list of {Host} could not be read: {Msg}", host, ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    ///     Parses the engine file list and returns its entries.
    /// </summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="looseFileParentDir">The payload directory.</param>
    /// <param name="hosts">The hosts that may serve it.</param>
    /// <param name="token">Cancel token.</param>
    public static async Task<IReadOnlyList<NeteaseGamesEngineFile>> FetchFileListAsync(
        HttpClient client,
        string looseFileParentDir,
        IReadOnlyList<string> hosts,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(looseFileParentDir);

        string listPath = $"/{looseFileParentDir.TrimStart('/')}/{EngineFileListName}";
        foreach (string host in hosts)
        {
            string url = NeteaseGamesUrlSigner.Sign($"https://{host}{listPath}", DateTimeOffset.UtcNow);
            try
            {
                using HttpResponseMessage response = await client.GetAsync(url, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                string list = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                IReadOnlyList<NeteaseGamesEngineFile> files = ParseEngineFileList(list);
                SharedStatic.InstanceLogger?.LogInformation(
                    "[NeteaseGames] The engine payload contains {Count} files", files.Count);
                return files;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                SharedStatic.InstanceLogger?.LogWarning(
                    "[NeteaseGames] The engine file list of {Host} could not be read: {Msg}", host, ex.Message);
            }
        }

        SharedStatic.InstanceLogger?.LogError("[NeteaseGames] The engine file list could not be downloaded");
        return [];
    }
}
