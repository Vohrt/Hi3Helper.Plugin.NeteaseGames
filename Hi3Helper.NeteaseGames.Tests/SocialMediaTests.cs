using System;
using System.Collections.Generic;
using System.Linq;
using Hi3Helper.NeteaseGames.Core.Management.Api;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The social sidebar is built from the manifest entries that describe a channel (WeChat, Bilibili, ...).
///     Their entry documents publish the QR artwork and carry an empty title, so the label comes from the
///     element name of the manifest.
/// </summary>
public class SocialMediaTests
{
    [Theory]
    [InlineData("vxItem", true)]
    [InlineData("officialVXItem", true)]
    [InlineData("bilibiliItem", true)]
    [InlineData("tiktokItem", true)]
    [InlineData("xhsItem", true)]
    [InlineData("QQGroupItem", true)]
    [InlineData("dashenItem", true)]
    [InlineData("smartAppItem", true)]
    [InlineData("weiboItem", true)]
    [InlineData("AnnounceItem", false)]
    [InlineData("ActivityItem", false)]
    [InlineData("BannerItem", false)]
    [InlineData("", false)]
    public void IsSocialEntry_RecognisesChannelElements(string element, bool expected)
    {
        Assert.Equal(expected, NeteaseGamesLauncherApiNews.IsSocialEntry(element));
    }

    [Theory]
    [InlineData("vxItem", "官方微信")]
    [InlineData("officialVXItem", "官方微信")]
    [InlineData("bilibiliItem", "哔哩哔哩")]
    [InlineData("tiktokItem", "抖音")]
    [InlineData("douyinItem", "抖音")]
    [InlineData("xhsItem", "小红书")]
    [InlineData("QQGroupItem", "QQ 群")]
    [InlineData("dashenItem", "大神")]
    [InlineData("smartAppItem", "网易大神 App")]
    [InlineData("weiboItem", "微博")]
    public void DescribeChannel_UsesTheElementName(string channel, string expected)
    {
        // The entry documents of these channels carry an empty title, so the element name is the only source.
        Assert.Equal(expected, NeteaseGamesLauncherApiNews.DescribeChannel(channel, "one:130"));
    }

    [Fact]
    public void DescribeChannel_FallsBackToTheEntryId()
    {
        Assert.Equal("官方渠道 #130", NeteaseGamesLauncherApiNews.DescribeChannel("unknownItem", "one:130"));
        Assert.Equal("官方渠道", NeteaseGamesLauncherApiNews.DescribeChannel("unknownItem", "no-separator"));
    }

    [Fact]
    public void ParseManifest_MarksSocialEntriesAndKeepsTheirChannel()
    {
        const string xml = """
            <root>
              <vxItem title="one:130">
                <url title="zh_CN">https://h74.update.netease.com/game_notice/vx_zh_CN</url>
              </vxItem>
              <bilibiliItem title="one:128">
                <url title="zh_CN">https://h74.update.netease.com/game_notice/bili_zh_CN</url>
              </bilibiliItem>
              <AnnounceItem title="one:185">
                <url title="zh_CN">https://h74.update.netease.com/game_notice/announce_zh_CN</url>
              </AnnounceItem>
            </root>
            """;

        List<NeteaseGamesLauncherApiNews.NoticeEntry> entries = NeteaseGamesLauncherApiNews.ParseManifest(xml);

        Assert.Equal(3, entries.Count);
        Assert.Equal(2, entries.Count(e => e.IsSocial));
        Assert.Contains(entries, e => e.IsSocial && e.Channel == "vxItem");
        Assert.Contains(entries, e => e.IsSocial && e.Channel == "bilibiliItem");
        // A regular notice is not a channel.
        Assert.Contains(entries, e => !e.IsSocial && e.Channel == "AnnounceItem");
    }

    [Fact]
    public void ParseDetail_ReadsTheQrArtworkOfAChannel()
    {
        const string json = """
            {"title":{"content":"","date":"2026-07-06"},"text":"",
             "images":["https://h74.update.netease.com/game_notice/image/119006515014.png"],"link":""}
            """;

        NeteaseGamesLauncherApiNews.NoticeDetail? detail = NeteaseGamesLauncherApiNews.ParseDetail(json);

        Assert.NotNull(detail);
        Assert.Equal(string.Empty, detail!.Title);
        Assert.Single(detail.Images);
        Assert.Equal(string.Empty, detail.Link);
    }
}
