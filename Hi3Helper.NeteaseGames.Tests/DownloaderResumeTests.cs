using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     Resume behaviour of <see cref="NeteaseGamesDownloader" />. A fresh installation failed on its last object -
///     <c>shadercachelist_dx12.txt</c> - because that object's size is not announced in any manifest
///     (<see cref="NeteaseGamesDownloadItem.ExpectedSize" /> = 0), so the "already complete" guard never fired and
///     the request carried <c>Range: bytes=7985520-</c> against a 7,985,520 byte file. Every host answered 416 and
///     the run was cancelled after assembling 66 GB (measured 2026-10-26).
/// </summary>
public class DownloaderResumeTests
{
    /// <summary>Answers like the content edge: 416 when the requested range starts at or past the end.</summary>
    private sealed class EdgeHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly long _size;

        public int Requests { get; private set; }
        public long? LastRangeStart { get; private set; }

        public EdgeHandler(long size)
        {
            _size = size;
            _body = new byte[size];
            for (int i = 0; i < size; i++)
            {
                _body[i] = (byte)(i * 31 % 251);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            long start = 0;
            System.Net.Http.Headers.RangeItemHeaderValue? first =
                System.Linq.Enumerable.FirstOrDefault(request.Headers.Range?.Ranges ?? []);
            if (first is not null)
            {
                start = first.From ?? 0;
                LastRangeStart = start;
            }

            if (start >= _size)
            {
                // What the edge really answers for "bytes=<length>-".
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            }

            if (start > 0)
            {
                byte[] tail = _body[(int)start..];
                HttpResponseMessage partial = new(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(tail)
                };
                partial.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                    start, _size - 1, _size);
                return Task.FromResult(partial);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_body)
            });
        }
    }

    private static NeteaseGamesDownloadItem Item(string stagedPath, long expectedSize)
        => new()
        {
            RelativePath = "20261026174908718/shadercachelist_dx12.txt",
            TargetRelativePath = "shadercachelist_dx12.txt",
            Kind = NeteaseGamesDownloadKind.SideFile,
            ExpectedSize = expectedSize,
            StagedPath = stagedPath
        };

    private static string Scratch()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "scratch", "dl" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    ///     A staged file of the same length as the remote object, with no announced size, must be discarded and
    ///     re-fetched from zero rather than resumed past the end.
    /// </summary>
    [Fact]
    public async Task UnknownSize_DoesNotResumePastTheEnd()
    {
        const long size = 4096;
        string dir = Scratch();
        string staged = Path.Combine(dir, "000337_shadercachelist_dx12.txt");
        await File.WriteAllBytesAsync(staged, new byte[size]); // a complete copy of the object

        EdgeHandler handler = new(size);
        using HttpClient http = new(handler);
        NeteaseGamesDownloader downloader = new(http);

        bool ok = await downloader.DownloadAsync(Item(staged, expectedSize: 0), null, CancellationToken.None);

        Assert.True(ok);
        // The first request must not carry a range at all: the staged copy was dropped up front.
        Assert.Null(handler.LastRangeStart);
        Assert.Equal(size, new FileInfo(staged).Length);
    }

    /// <summary>
    ///     A 416 is treated as "this prefix is unusable": the staged file is dropped and the retry succeeds from
    ///     zero. Before the fix the same range was asked for on every host and the run was cancelled.
    /// </summary>
    [Fact]
    public async Task RangeNotSatisfiable_DropsTheFileAndRetriesFromZero()
    {
        const long size = 2048;
        string dir = Scratch();
        string staged = Path.Combine(dir, "obj.bin");

        // A staged file longer than the object forces the first request to be a resume that the edge refuses.
        await File.WriteAllBytesAsync(staged, new byte[size * 2]);

        EdgeHandler handler = new(size);
        using HttpClient http = new(handler);
        NeteaseGamesDownloader downloader = new(http);

        bool ok = await downloader.DownloadAsync(Item(staged, expectedSize: size), null, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(size, new FileInfo(staged).Length);
    }

    /// <summary>A staged partial file with an announced size still resumes (the normal, working path).</summary>
    [Fact]
    public async Task KnownSize_StillResumes()
    {
        const long size = 4096;
        string dir = Scratch();
        string staged = Path.Combine(dir, "obj.bin");
        await File.WriteAllBytesAsync(staged, new byte[1024]);

        EdgeHandler handler = new(size);
        using HttpClient http = new(handler);
        NeteaseGamesDownloader downloader = new(http);

        bool ok = await downloader.DownloadAsync(Item(staged, expectedSize: size), null, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(1024, handler.LastRangeStart);
        Assert.Equal(size, new FileInfo(staged).Length);
    }
}
