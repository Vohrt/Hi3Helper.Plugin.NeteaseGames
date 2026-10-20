using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
/// </summary>
public static class NeteaseGamesEnginePatcher
{
    /// <summary>
    ///     The name of the file list inside the payload directory.
    /// </summary>
    public const string EngineFileListName = "md5_engine.txt";

    /// <summary>
    ///     The prefix of the entries that describe files which have to be removed.
    /// </summary>
    public const string DeleteDirectoryPrefix = "used_del_dir";

    /// <summary>
    ///     The directory of the engine inside the game directory.
    /// </summary>
    public const string EngineDirectoryName = "Engine";

    /// <summary>
    ///     Parses the <c>md5_engine.txt</c> file list.
    /// </summary>
    /// <param name="text">The raw text.</param>
    public static IReadOnlyList<NeteaseGamesEngineFile> ParseEngineFileList(string text)
    {
        List<NeteaseGamesEngineFile> files = [];

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
    ///     Downloads the engine payload and writes it into the game directory.
    /// </summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="gameDirectory">The game directory.</param>
    /// <param name="looseFileParentDir">
    ///     The payload directory (e.g. <c>loose_file/mini_pack_202610151759</c>).
    /// </param>
    /// <param name="hosts">The hosts that may serve the payload.</param>
    /// <param name="progress">Reports the number of downloaded bytes.</param>
    /// <param name="token">The cancellation token.</param>
    /// <param name="maxFiles">
    ///     The maximum number of files that are applied (null = all of them); a repair run only needs a few.
    /// </param>
    /// <returns>Whether the payload was applied.</returns>
    public static async Task<bool> ApplyAsync(
        HttpClient client,
        string gameDirectory,
        string looseFileParentDir,
        IReadOnlyList<string> hosts,
        Action<long>? progress,
        CancellationToken token,
        int? maxFiles = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameDirectory);
        ArgumentException.ThrowIfNullOrEmpty(looseFileParentDir);

        string listPath = $"/{looseFileParentDir.TrimStart('/')}/{EngineFileListName}";
        string? list = null;
        foreach (string host in hosts)
        {
            string url = NeteaseGamesUrlSigner.Sign($"https://{host}{listPath}", DateTimeOffset.UtcNow);
            try
            {
                using HttpResponseMessage response = await client.GetAsync(url, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    list = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    break;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // Try the next host.
            }
        }

        if (list is null)
        {
            SharedStatic.InstanceLogger?.LogError("[NeteaseGames] The engine file list could not be downloaded");
            return false;
        }

        IReadOnlyList<NeteaseGamesEngineFile> files = ParseEngineFileList(list);
        SharedStatic.InstanceLogger?.LogInformation(
            "[NeteaseGames] The engine payload contains {Count} files", files.Count);

        string engineDirectory = Path.Combine(gameDirectory, EngineDirectoryName);
        long downloaded = 0;
        int applied = 0;

        foreach (NeteaseGamesEngineFile file in files)
        {
            token.ThrowIfCancellationRequested();

            if (maxFiles.HasValue && applied >= maxFiles.Value)
            {
                break;
            }

            if (file.RelativePath.StartsWith(DeleteDirectoryPrefix, StringComparison.Ordinal))
            {
                TryDelete(engineDirectory, file);
                continue;
            }

            string target = Path.Combine(engineDirectory,
                file.TargetName.Replace('\\', Path.DirectorySeparatorChar));
            if (File.Exists(target) && new FileInfo(target).Length == file.Size)
            {
                downloaded += file.Size;
                progress?.Invoke(downloaded);
                applied++;
                continue;
            }

            byte[]? payload = await DownloadAsync(client, looseFileParentDir, file, hosts, token)
                .ConfigureAwait(false);
            if (payload is null)
            {
                SharedStatic.InstanceLogger?.LogError("[NeteaseGames] The engine file {Path} could not be downloaded",
                    file.RelativePath);
                return false;
            }

            if (file.Size > 0 && payload.LongLength != file.Size)
            {
                SharedStatic.InstanceLogger?.LogError(
                    "[NeteaseGames] The engine file {Path} has the wrong size ({Actual} instead of {Expected})",
                    file.RelativePath, payload.LongLength, file.Size);
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, payload, token).ConfigureAwait(false);

            downloaded += payload.LongLength;
            progress?.Invoke(downloaded);
            applied++;
        }

        SharedStatic.InstanceLogger?.LogInformation("[NeteaseGames] The engine payload was applied ({Count} files)",
            applied);
        return true;
    }

    private static async Task<byte[]?> DownloadAsync(HttpClient client, string looseFileParentDir,
        NeteaseGamesEngineFile file, IReadOnlyList<string> hosts, CancellationToken token)
    {
        string path = $"/{looseFileParentDir.TrimStart('/')}/{file.UrlPath}";
        foreach (string host in hosts)
        {
            string url = NeteaseGamesUrlSigner.Sign($"https://{host}{path}", DateTimeOffset.UtcNow);
            try
            {
                using HttpResponseMessage response = await client.GetAsync(url, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // Try the next host.
            }
        }

        return null;
    }

    private static void TryDelete(string engineDirectory, NeteaseGamesEngineFile file)
    {
        try
        {
            string target = Path.Combine(engineDirectory,
                file.TargetName.Replace('\\', Path.DirectorySeparatorChar));
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch (Exception)
        {
            // Removing an obsolete file is best effort.
        }
    }
}
