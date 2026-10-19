using System;
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
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.NeteaseGames.Core.Management;

/// <summary>A single item of a download plan.</summary>
public sealed class NeteaseGamesDownloadItem
{
    /// <summary>The name of the file inside the content root.</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>The expected MD5 of the file, when the server announces one.</summary>
    public string? ExpectedMd5 { get; init; }

    /// <summary>The expected size of the file.</summary>
    public long ExpectedSize { get; init; }

    /// <summary>The absolute path the file is written to.</summary>
    public string TargetPath { get; init; } = string.Empty;
}

/// <summary>A download plan: the list of files that have to be fetched for an install or an update.</summary>
public sealed class NeteaseGamesDownloadPlan
{
    public List<NeteaseGamesDownloadItem> Items { get; } = [];

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
}

/// <summary>
///     Downloads the NetEase content payload.
///     <para>
///         The official launcher downloads through the GPH content host; the objects below
///         loose_file/&lt;version&gt;/ are protected by the CDN edge, so every candidate URL is tried and the
///         HTTP status is reported verbatim when the edge refuses the request.
///     </para>
/// </summary>
public sealed class NeteaseGamesDownloader
{
    private readonly HttpClient _httpClient;
    private readonly List<string> _contentRoots;

    public NeteaseGamesDownloader(HttpClient httpClient, IEnumerable<string>? contentRoots = null)
    {
        _httpClient = httpClient;
        _contentRoots = contentRoots is null
            ? [NeteaseGamesEndpoints.ContentHostUrl, .. NeteaseGamesContentEndpoints.ContentMirrorHosts.Select(h => "https://" + h + "/")]
            : [.. contentRoots];
    }

    /// <summary>Builds the candidate URLs for a content path.</summary>
    public IReadOnlyList<Uri> BuildUris(string relativePath)
    {
        List<Uri> uris = [];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (string root in _contentRoots)
        {
            string url = root.TrimEnd('/') + "/" + relativePath.TrimStart('/');
            // Every content request has to be signed, otherwise the edge answers with 403.
            uris.Add(new Uri(NeteaseGamesUrlSigner.Sign(url, now)));
        }

        return uris;
    }

    /// <summary>
    ///     Downloads a file into the target path, resuming a partial download when possible and verifying the
    ///     MD5 afterwards.
    /// </summary>
    public async Task<bool> DownloadAsync(NeteaseGamesDownloadItem item, Action<long>? progress, CancellationToken token)
    {
        string? directory = Path.GetDirectoryName(item.TargetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        long alreadyDownloaded = 0;
        if (File.Exists(item.TargetPath))
        {
            alreadyDownloaded = new FileInfo(item.TargetPath).Length;

            // A file that is already complete must not be requested again: the resume would ask for a range
            // that starts behind the end of the file, and the edge answers with "416 Range Not Satisfiable".
            if (item.ExpectedSize > 0 && alreadyDownloaded == item.ExpectedSize)
            {
                if (string.IsNullOrEmpty(item.ExpectedMd5) ||
                    string.Equals(await ComputeMd5Async(item.TargetPath, token).ConfigureAwait(false),
                        item.ExpectedMd5, StringComparison.OrdinalIgnoreCase))
                {
                    SharedStatic.InstanceLogger?.LogInformation(
                        "[NeteaseGames] '{Path}' is already complete ({Size} bytes)", item.TargetPath, alreadyDownloaded);
                    progress?.Invoke(alreadyDownloaded);
                    return true;
                }

                try
                {
                    File.Delete(item.TargetPath);
                }
                catch (Exception)
                {
                    // The file is replaced by the download anyway.
                }

                alreadyDownloaded = 0;
            }
        }

        Exception? lastError = null;
        foreach (Uri uri in BuildUris(item.RelativePath))
        {
            try
            {
                bool ok = await DownloadFromAsync(uri, item, alreadyDownloaded, progress, token).ConfigureAwait(false);
                if (ok)
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                SharedStatic.InstanceLogger?.LogWarning("[NeteaseGames] Download failed for {Uri}: {Msg}", uri, ex.Message);
            }
        }

        if (lastError is not null)
        {
            throw new IOException($"Unable to download '{item.RelativePath}'.", lastError);
        }

        throw new IOException($"Unable to download '{item.RelativePath}': no content host accepted the request.");
    }

    private async Task<bool> DownloadFromAsync(
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
            return false;
        }

        bool append = alreadyDownloaded > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        long written = append ? alreadyDownloaded : 0;

        await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using FileStream target = new(
            item.TargetPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            useAsync: true);

        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            written += read;
            progress?.Invoke(written);
        }

        await target.FlushAsync(token).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(item.ExpectedMd5) && item.ExpectedSize > 0)
        {
            string actual = await ComputeMd5Async(item.TargetPath, token).ConfigureAwait(false);
            if (!string.Equals(actual, item.ExpectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                SharedStatic.InstanceLogger?.LogWarning(
                    "[NeteaseGames] MD5 mismatch for {Path}: expected {Expected}, got {Actual}",
                    item.TargetPath, item.ExpectedMd5, actual);
                return false;
            }
        }

        return true;
    }

    /// <summary>Computes the MD5 of a file.</summary>
    public static async Task<string> ComputeMd5Async(string path, CancellationToken token)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        byte[] hash = await MD5.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
