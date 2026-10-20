using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management.Api;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The media API has to hand the host something it can materialise itself (a URL or a base64 payload) -
///     never a local file path, which leaves Collapse on its "loading background" spinner forever.
/// </summary>
public class BackgroundTests
{
    [Fact]
    public void EmbeddedBackground_IsPresentAndIsAJpeg()
    {
        string? data = NeteaseGamesLauncherApiMedia.ReadEmbeddedResourceAsBase64(
            NeteaseGamesLauncherApiMedia.BackgroundResourceName);

        Assert.False(string.IsNullOrEmpty(data));

        byte[] bytes = Convert.FromBase64String(data!);
        // JPEG magic: FF D8 FF
        Assert.True(bytes.Length > 100_000, $"background is only {bytes.Length} bytes");
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        Assert.Equal(0xFF, bytes[2]);
    }

    [Fact]
    public void EmbeddedBackground_IsLargeEnoughForALauncherBackground()
    {
        string? data = NeteaseGamesLauncherApiMedia.ReadEmbeddedResourceAsBase64(
            NeteaseGamesLauncherApiMedia.BackgroundResourceName);
        Assert.NotNull(data);

        byte[] bytes = Convert.FromBase64String(data!);
        (int width, int height) = ReadJpegSize(bytes);

        // The official splash the client ships is 1920x814 after the letterbox is cropped.
        Assert.True(width >= 1600, $"width {width} < 1600");
        Assert.True(height >= 720, $"height {height} < 720");
        Assert.True(width >= height, "the background must be landscape");
    }

    [Fact]
    public void MissingResource_YieldsNull()
    {
        Assert.Null(NeteaseGamesLauncherApiMedia.ReadEmbeddedResourceAsBase64("does-not-exist.jpg"));
    }

    [Fact]
    public void EnumerateNoticeImageUrls_ReturnsEmpty_WhenTheManifestHasNoImages()
    {
        // The live notice manifest of this game carries no image URL at all - that is why the background is
        // embedded instead of being probed from the CDN.
        const string xml = """
            <root>
              <version>101</version>
              <AnnounceInfo>
                <AnnounceItem title="one:185">
                  <url title="zh_CN">https://h74.update.netease.com/game_notice/OYFCTTW9HmmB_zh_CN</url>
                </AnnounceItem>
              </AnnounceInfo>
            </root>
            """;

        Assert.Empty(NeteaseGamesLauncherApiNews.EnumerateNoticeImageUrls(xml));
    }

    [Fact]
    public void EnumerateNoticeImageUrls_ReturnsBannerEntriesOnly()
    {
        // The artwork is not part of the manifest: it lives in the entry documents, so this enumeration only
        // reports the banner entries (whose documents carry the 1920x1080 key visuals).
        const string xml = """
            <root>
              <AnnounceInfo>
                <AnnounceItem title="one:185">
                  <url title="zh_CN">https://h74.update.netease.com/game_notice/announce_zh_CN</url>
                </AnnounceItem>
              </AnnounceInfo>
              <ActivityInfo>
                <ActivityItem title="one:186"><img>https://h74.update.netease.com/game_notice/image/111.png</img></ActivityItem>
              </ActivityInfo>
              <BannerItem title="one:206">
                <url title="zh_CN">https://h74.update.netease.com/game_notice/banner_zh_CN</url>
              </BannerItem>
            </root>
            """;

        List<string> urls = NeteaseGamesLauncherApiNews.EnumerateNoticeImageUrls(xml).ToList();

        Assert.Single(urls);
        Assert.Equal("https://h74.update.netease.com/game_notice/banner_zh_CN", urls[0]);
    }

    [Fact]
    public void EnumerateNoticeImageUrls_ToleratesBrokenXml()
    {
        Assert.Empty(NeteaseGamesLauncherApiNews.EnumerateNoticeImageUrls("<not-closed"));
        Assert.Empty(NeteaseGamesLauncherApiNews.EnumerateNoticeImageUrls(string.Empty));
    }

    /// <summary>Reads the pixel size from a JPEG (walks the segment chain to the Start-Of-Frame marker).</summary>
    private static (int Width, int Height) ReadJpegSize(byte[] bytes)
    {
        Assert.True(bytes[0] == 0xFF && bytes[1] == 0xD8, "not a JPEG");

        int offset = 2;
        while (offset + 9 < bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            byte marker = bytes[offset + 1];
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3)
            {
                int height = (bytes[offset + 5] << 8) | bytes[offset + 6];
                int width = (bytes[offset + 7] << 8) | bytes[offset + 8];
                return (width, height);
            }

            int length = (bytes[offset + 2] << 8) | bytes[offset + 3];
            if (length < 2)
            {
                break;
            }

            offset += 2 + length;
        }

        throw new InvalidDataException("no Start-Of-Frame segment found");
    }
}
