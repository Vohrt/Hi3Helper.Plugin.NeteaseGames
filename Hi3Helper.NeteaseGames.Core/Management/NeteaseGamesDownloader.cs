using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.NeteaseGames.Core.Management;

/// <summary>What an object of a download plan is used for; it decides where it is moved to.</summary>
public enum NeteaseGamesDownloadKind
{
    /// <summary>An <c>mpk</c> shard that lives in the patch directory.</summary>
    Package,

    /// <summary>A package index (<c>*.mpkinfo</c>) that lives next to the packages.</summary>
    Index,

    /// <summary>A regular game file that is stored below the installation root.</summary>
    LooseFile
}

/// <summary>A single object of a download plan.</summary>
public sealed class NeteaseGamesDownloadItem
{
    /// <summary>The path of the object relative to the content root (used for the URL).</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>The path the object is written to, relative to its root (see <see cref="Kind" />).</summary>
    public string TargetRelativePath { get; init; } = string.Empty;

    /// <summary>What the object is.</summary>
    public NeteaseGamesDownloadKind Kind { get; init; }

    /// <summary>The expected MD5 of the object, when the server announces one.</summary>
    public string? ExpectedMd5 { get; init; }

    /// <summary>The expected size of the object, or 0 when it is unknown.</summary>
    public long ExpectedSize { get; init; }

    /// <summary>The absolute path the object is staged at.</summary>
    public string StagedPath { get; set; } = string.Empty;

    /// <summary>
    ///     Resolves the final path of the object.
    ///     <para>
    ///         The path comes from a remote manifest, so it is validated here: a path that escapes its root
    ///         (<c>..\</c> or an absolute path) is rejected instead of being written outside the installation.
    ///     </para>
    /// </summary>
    /// <param name="root">The root directory of the object kind.</param>
    public string ResolveTargetPath(string root)
    {
        if (string.IsNullOrWhiteSpace(TargetRelativePath))
        {
            throw new InvalidDataException("The plan item has no target path.");
        }

        string normalizedRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, TargetRelativePath));

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        string prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, comparison) &&
            !string.Equals(candidate, normalizedRoot, comparison))
        {
            throw new InvalidDataException(
                $"The path '{TargetRelativePath}' escapes the installation directory.");
        }

        return candidate;
    }
}

/// <summary>
///     The list of objects that have to be downloaded for an install or an update.
///     <para>
///         The plan is the <b>single</b> source of truth for the reported sizes: the host asks
///         <c>GetGameSizeAsync</c> / <c>GetGameDownloadedSizeAsync</c> before it starts the routine and then
///         compares them with the progress that is reported during it, so all three numbers must come from
///         the same place or the host computes a wrong remaining size (and a wrong disk space requirement).
///     </para>
/// </summary>
public sealed class NeteaseGamesDownloadPlan
{
    /// <summary>The objects of the plan.</summary>
    public List<NeteaseGamesDownloadItem> Items { get; } = [];

    /// <summary>The package version the installation is updated to, or 0 when the version must not change.</summary>
    public long PackageVersion { get; init; }

    /// <summary>
    ///     The decrypted remote patch manifest (<c>OB_patchmd5_*.txt</c>) the plan was built from.
    ///     <para>
    ///         It is written back as the launcher's own record once the update succeeded, with its version fields
    ///         replaced by <see cref="PackageVersion"/> - the official launcher reads that file, not
    ///         <c>pkgversion</c>.
    ///     </para>
    /// </summary>
    public string? LauncherRecord { get; init; }

    /// <summary>
    ///     Base packages a fresh installation would need but cannot download.
    ///     <para>
    ///         The content host serves only the patch shards; the base packages (<c>Patch.mpk</c>,
    ///         <c>ResourceExA..</c>) are not part of any manifest the plugin can read and are not downloadable.
    ///         When this is not empty the installation has to be created by the official launcher first, and the
    ///         installer must refuse rather than report a completed installation that cannot start.
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> MissingBasePackages { get; set; } = [];

    /// <summary>The staging directory every object is downloaded into.</summary>
    public string StagingDirectory { get; init; } = string.Empty;

    private long _downloaded;

    /// <summary>The number of packages in the plan.</summary>
    public int PackageCount => CountOf(NeteaseGamesDownloadKind.Package);

    /// <summary>The number of indexes in the plan.</summary>
    public int IndexCount => CountOf(NeteaseGamesDownloadKind.Index);

    /// <summary>The number of loose files in the plan.</summary>
    public int LooseCount => CountOf(NeteaseGamesDownloadKind.LooseFile);

    /// <summary>Whether the plan is empty (nothing to do).</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>The number of bytes that are already on disk.</summary>
    public long DownloadedSize => Interlocked.Read(ref _downloaded);

    /// <summary>The total number of bytes to download.</summary>
    public long TotalSize
    {
        get
        {
            long total = 0;
            foreach (NeteaseGamesDownloadItem item in Items)
            {
                total += item.ExpectedSize;
            }

            return total;
        }
    }

    /// <summary>Whether any object carries a verifiable MD5.</summary>
    public bool HasVerifiableObjects
    {
        get
        {
            foreach (NeteaseGamesDownloadItem item in Items)
            {
                if (!string.IsNullOrEmpty(item.ExpectedMd5))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Records that an object was downloaded.</summary>
    /// <param name="bytes">The size of the object.</param>
    public void ReportDownloaded(long bytes) => Interlocked.Add(ref _downloaded, bytes);

    /// <summary>Deletes the staging directory; a leftover directory is not fatal.</summary>
    public void CleanStaging()
    {
        if (string.IsNullOrEmpty(StagingDirectory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(StagingDirectory))
            {
                Directory.Delete(StagingDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGames] The staging directory '{Path}' could not be removed: {Msg}", StagingDirectory, ex.Message);
        }
    }

    private int CountOf(NeteaseGamesDownloadKind kind)
    {
        int count = 0;
        foreach (NeteaseGamesDownloadItem item in Items)
        {
            if (item.Kind == kind)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     Assigns the staged paths of every object below the given staging directory.
    /// </summary>
    /// <param name="stagingDirectory">The staging directory.</param>
    public void AssignStaging(string stagingDirectory)
    {
        int index = 0;
        foreach (NeteaseGamesDownloadItem item in Items)
        {
            // The staged name is an ordinal, so two objects with the same file name (a package and its index
            // often share the stem) can never overwrite each other while they are being downloaded.
            item.StagedPath = Path.Combine(stagingDirectory, $"{index:D6}_{Path.GetFileName(item.TargetRelativePath)}");
            index++;
        }
    }
}

/// <summary>
///     Downloads the NetEase content payload.
///     <para>
///         Every content request has to be signed (see <see cref="NeteaseGamesUrlSigner" />), otherwise the
///         edge answers with 403. Downloads resume from a partial file and are verified by MD5 when the
///         server announces one.
///     </para>
/// </summary>
public sealed class NeteaseGamesDownloader
{
    /// <summary>How many transfers may overlap by default.</summary>
    public const int DefaultParallelism = 4;

    /// <summary>The size of the copy buffer.</summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>How often a single object is attempted per content host before it is given up.</summary>
    private const int AttemptsPerHost = 2;

    private readonly HttpClient _httpClient;
    private readonly List<string> _contentRoots;

    /// <summary>
    ///     Creates a downloader.
    /// </summary>
    /// <param name="httpClient">The client used for the transfers.</param>
    /// <param name="contentRoots">The content roots to try, in order; the defaults are used when null.</param>
    public NeteaseGamesDownloader(HttpClient httpClient, IEnumerable<string>? contentRoots = null)
    {
        _httpClient = httpClient;
        _contentRoots = contentRoots is null
            ? [NeteaseGamesEndpoints.ContentHostUrl, .. NeteaseGamesContentEndpoints.ContentMirrorHosts.Select(h => "https://" + h + "/")]
            : [.. contentRoots];
    }

    /// <summary>Builds the candidate URLs for a content path.</summary>
    /// <param name="relativePath">The path relative to the content root.</param>
    public IReadOnlyList<Uri> BuildUris(string relativePath)
    {
        List<Uri> uris = [];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (string root in _contentRoots)
        {
            string url = root.TrimEnd('/') + "/" + relativePath.TrimStart('/');
            uris.Add(new Uri(NeteaseGamesUrlSigner.Sign(url, now)));
        }

        return uris;
    }

    /// <summary>
    ///     Downloads an object into its staged path, resuming a partial download when possible and verifying
    ///     the MD5 afterwards.
    /// </summary>
    /// <param name="item">The object to download.</param>
    /// <param name="progress">Reports the number of bytes that are on disk.</param>
    /// <param name="token">Cancel token.</param>
    /// <returns>Whether the object is complete and verified.</returns>
    public async Task<bool> DownloadAsync(NeteaseGamesDownloadItem item, Action<long>? progress, CancellationToken token)
    {
        string? directory = Path.GetDirectoryName(item.StagedPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        long alreadyDownloaded = 0;
        if (File.Exists(item.StagedPath))
        {
            alreadyDownloaded = new FileInfo(item.StagedPath).Length;

            // A file that is already complete must not be requested again: the resume would ask for a range
            // that starts behind the end of the file, and the edge answers with "416 Not Satisfiable".
            if (item.ExpectedSize > 0 && alreadyDownloaded == item.ExpectedSize)
            {
                if (await IsVerifiedAsync(item, token).ConfigureAwait(false))
                {
                    SharedStatic.InstanceLogger?.LogInformation(
                        "[NeteaseGames] '{Path}' is already complete ({Size} bytes)", item.StagedPath, alreadyDownloaded);
                    progress?.Invoke(alreadyDownloaded);
                    return true;
                }

                DeleteStaged(item);
                alreadyDownloaded = 0;
            }
        }

        Exception? lastError = null;
        foreach (Uri uri in BuildUris(item.RelativePath))
        {
            for (int attempt = 1; attempt <= AttemptsPerHost; attempt++)
            {
                token.ThrowIfCancellationRequested();

                // A retry always starts from whatever is on disk at that moment; a corrupt or partly written
                // file is dropped first, otherwise the next attempt would append to garbage.
                if (File.Exists(item.StagedPath))
                {
                    long current = new FileInfo(item.StagedPath).Length;
                    if (item.ExpectedSize > 0 && current > item.ExpectedSize)
                    {
                        DeleteStaged(item);
                        current = 0;
                    }

                    alreadyDownloaded = current;
                }

                try
                {
                    (bool ok, bool corrupt) = await DownloadFromAsync(uri, item, alreadyDownloaded, progress, token)
                        .ConfigureAwait(false);
                    if (ok)
                    {
                        return true;
                    }

                    if (corrupt)
                    {
                        DeleteStaged(item);
                        alreadyDownloaded = 0;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    lastError = ex;
                    SharedStatic.InstanceLogger?.LogWarning(
                        "[NeteaseGames] Download failed for {Uri} (attempt {Attempt}/{Attempts}): {Msg}",
                        uri, attempt, AttemptsPerHost, ex.Message);
                }

                if (attempt < AttemptsPerHost)
                {
                    // Exponential backoff: a busy edge recovers within a second or two.
                    TimeSpan delay = TimeSpan.FromMilliseconds(400 * (1 << (attempt - 1)));
                    try
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }
            }
        }

        if (lastError is not null)
        {
            SharedStatic.InstanceLogger?.LogError(
                "[NeteaseGames] '{Path}' could not be downloaded: {Msg}", item.RelativePath, lastError.Message);
            return false;
        }

        SharedStatic.InstanceLogger?.LogError(
            "[NeteaseGames] '{Path}' was refused by every content host", item.RelativePath);
        return false;
    }

    /// <summary>Deletes the staged file of an object, ignoring the failure.</summary>
    private static void DeleteStaged(NeteaseGamesDownloadItem item)
    {
        try
        {
            if (File.Exists(item.StagedPath))
            {
                File.Delete(item.StagedPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next attempt overwrites it anyway.
        }
    }

    /// <summary>Checks whether a staged file matches the announced size and MD5.</summary>
    private static async Task<bool> IsVerifiedAsync(NeteaseGamesDownloadItem item, CancellationToken token)
    {
        if (item.ExpectedSize > 0 && new FileInfo(item.StagedPath).Length != item.ExpectedSize)
        {
            return false;
        }

        if (string.IsNullOrEmpty(item.ExpectedMd5))
        {
            // Nothing to verify against: trust the size the server announced.
            return item.ExpectedSize > 0;
        }

        string actual = await ComputeMd5Async(item.StagedPath, token).ConfigureAwait(false);
        return string.Equals(actual, item.ExpectedMd5, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(bool Ok, bool Corrupt)> DownloadFromAsync(
        Uri uri,
        NeteaseGamesDownloadItem item,
        long alreadyDownloaded,
        Action<long>? progress,
        CancellationToken token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        if (alreadyDownloaded > 0)
        {
            request.Headers.Range = new RangeHeaderValue(alreadyDownloaded, null);
        }

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGames] Content host refused {Uri} with {Status}", uri, (int)response.StatusCode);
            return (false, false);
        }

        // The server may ignore the range (200) - then the file has to be written from the beginning again.
        bool append = alreadyDownloaded > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        long written = append ? alreadyDownloaded : 0;

        await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using FileStream target = new(
            item.StagedPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: BufferSize,
            useAsync: true);

        // The buffer is rented: a content package is up to 800 MB, and allocating a fresh megabyte per file
        // (or per retry) puts avoidable pressure on the large object heap.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), token).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                written += read;
                progress?.Invoke(written);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await target.FlushAsync(token).ConfigureAwait(false);

        if (item.ExpectedSize > 0 && written != item.ExpectedSize)
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGames] '{Path}' has a wrong size ({Actual} instead of {Expected})",
                item.RelativePath, written, item.ExpectedSize);
            return (false, true);
        }

        if (string.IsNullOrEmpty(item.ExpectedMd5))
        {
            return (true, false);
        }

        string actual = await ComputeMd5Async(item.StagedPath, token).ConfigureAwait(false);
        if (!string.Equals(actual, item.ExpectedMd5, StringComparison.OrdinalIgnoreCase))
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGames] MD5 mismatch for '{Path}': expected {Expected}, got {Actual}",
                item.RelativePath, item.ExpectedMd5, actual);
            return (false, true);
        }

        return (true, false);
    }

    /// <summary>
    ///     Downloads every object of a plan concurrently, reporting the total number of bytes on disk.
    ///     <para>
    ///         A game payload is tens of gigabytes across hundreds of objects, so the transfers are overlapped.
    ///         The reported progress is the sum over all objects (it never goes backwards because each object
    ///         only grows), which is exactly what the host expects from a single byte counter.
    ///     </para>
    /// </summary>
    /// <param name="items">The objects to download.</param>
    /// <param name="parallelism">How many transfers may run at the same time.</param>
    /// <param name="progress">Reports the total number of bytes that are on disk.</param>
    /// <param name="token">Cancel token.</param>
    /// <returns>The objects that could not be downloaded (empty on success).</returns>
    public async Task<IReadOnlyList<NeteaseGamesDownloadItem>> DownloadAllAsync(
        IReadOnlyList<NeteaseGamesDownloadItem> items,
        int parallelism,
        Action<long>? progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(items);

        List<NeteaseGamesDownloadItem> failed = [];
        if (items.Count == 0)
        {
            return failed;
        }

        // Per object byte counters: the total is their sum, so an object that already had bytes on disk from a
        // previous run is accounted for from the start.
        long[] perItem = new long[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            perItem[i] = GetExistingLength(items[i].StagedPath);
        }

        long total = 0;
        foreach (long value in perItem)
        {
            total += value;
        }

        progress?.Invoke(total);

        object gate = new();
        long reported = total;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, items.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, parallelism),
                CancellationToken = token
            },
            async (index, innerToken) =>
            {
                NeteaseGamesDownloadItem item = items[index];

                // Each object reports its own running total; the shared counter is advanced by the delta.
                long itemStart = perItem[index];
                bool ok = await DownloadAsync(
                        item,
                        bytes =>
                        {
                            long delta = bytes - itemStart;
                            if (delta <= 0)
                            {
                                return;
                            }

                            itemStart = bytes;
                            long current;
                            lock (gate)
                            {
                                reported += delta;
                                current = reported;
                            }

                            progress?.Invoke(current);
                        },
                        innerToken)
                    .ConfigureAwait(false);

                if (!ok)
                {
                    lock (gate)
                    {
                        failed.Add(item);
                    }
                }
            }).ConfigureAwait(false);

        return failed;
    }

    /// <summary>Returns the length of a file, or 0 when it does not exist.</summary>
    private static long GetExistingLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Computes the MD5 of a file.</summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="token">Cancel token.</param>
    public static async Task<string> ComputeMd5Async(string path, CancellationToken token)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        byte[] hash = await MD5.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
