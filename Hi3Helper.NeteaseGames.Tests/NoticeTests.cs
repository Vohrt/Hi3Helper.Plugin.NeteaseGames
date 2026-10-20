using System;
using System.Collections.Generic;
using System.Linq;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management.Api;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

public class RegistryKeyTests
{
    [Fact]
    public void LauncherKey_IsTheOfficialKeyAndUsedForDetection()
    {
        // The official launcher keeps InstPath / AdditionalPaths / APP_EXE_NAME here and creates no sub keys.
        Assert.Equal("OB_ywzh_launcher_PC", NeteaseGamesPaths.LauncherRegistryKeyName);
        Assert.Equal(@"Software\OB_ywzh_launcher_PC", NeteaseGamesPaths.RegistryKeyPath);
    }

    [Fact]
    public void SettingsKey_SitsBelowTheOfficialLauncherKey()
    {
        // The host composes Software\{vendor}\{game}; naming the vendor after the launcher key puts the Collapse
        // settings below the official key instead of next to it, so the launcher's own values stay untouched.
        Assert.Equal("OB_ywzh_launcher_PC", NeteaseGamesPaths.CollapseSettingsVendorName);
        Assert.Equal("Collapse", NeteaseGamesPaths.CollapseSettingsKeyName);

        string composed = $@"Software\{NeteaseGamesPaths.CollapseSettingsVendorName}\{NeteaseGamesPaths.CollapseSettingsKeyName}";
        Assert.Equal(@"Software\OB_ywzh_launcher_PC\Collapse", composed);

        // Neither segment may contain a separator of its own (the host joins them).
        Assert.DoesNotContain('\\', NeteaseGamesPaths.CollapseSettingsVendorName);
        Assert.DoesNotContain('\\', NeteaseGamesPaths.CollapseSettingsKeyName);
    }

    [Fact]
    public void SettingsKey_IsNotTheLauncherKeyItself()
    {
        // Writing the Collapse settings into the launcher key would mix them with the launcher's own values.
        Assert.NotEqual(NeteaseGamesPaths.LauncherRegistryKeyName, NeteaseGamesPaths.CollapseSettingsKeyName);
    }
}

public class NoticeTests
{
    private const string Manifest = """
        <root>
          <AnnounceInfo>
            <AnnounceItem title="one:185">
              <url title="zh_CN">https://h74.update.netease.com/game_notice/OYFCTTW9HmmB_zh_CN</url>
            </AnnounceItem>
          </AnnounceInfo>
          <ActivityInfo>
            <ActivityItem title="one:186">
              <url title="zh_CN">https://h74.update.netease.com/game_notice/t7IHsIVCqn7z_zh_CN</url>
            </ActivityItem>
          </ActivityInfo>
          <BannerItem title="one:206">
            <url title="zh_CN">https://h74.update.netease.com/game_notice/nBTh55He5orR_zh_CN</url>
          </BannerItem>
        </root>
        """;

    [Fact]
    public void ParseManifest_CollectsSectionsAndFlagsBanners()
    {
        List<NeteaseGamesLauncherApiNews.NoticeEntry> entries = NeteaseGamesLauncherApiNews.ParseManifest(Manifest);

        Assert.Equal(3, entries.Count);
        Assert.Single(entries, e => e.IsBanner);
        Assert.All(entries, e => Assert.StartsWith("https://", e.Url, StringComparison.Ordinal));
    }

    [Fact]
    public void ParseManifest_DropsExpiredEntries()
    {
        const string xml = """
            <root>
              <AnnounceInfo>
                <AnnounceItem title="one:1">
                  <url title="zh_CN">https://h74.update.netease.com/game_notice/a_zh_CN</url>
                  <end_timestamp>1</end_timestamp>
                </AnnounceItem>
                <AnnounceItem title="one:2">
                  <url title="zh_CN">https://h74.update.netease.com/game_notice/b_zh_CN</url>
                  <end_timestamp>4102444800</end_timestamp>
                </AnnounceItem>
              </AnnounceInfo>
            </root>
            """;

        List<NeteaseGamesLauncherApiNews.NoticeEntry> entries = NeteaseGamesLauncherApiNews.ParseManifest(xml);

        Assert.Single(entries);
        Assert.Equal("one:2", entries[0].Id);
    }

    [Fact]
    public void ParseManifest_ToleratesBrokenXml()
    {
        Assert.Empty(NeteaseGamesLauncherApiNews.ParseManifest("<not-closed"));
        Assert.Empty(NeteaseGamesLauncherApiNews.ParseManifest(string.Empty));
    }

    [Fact]
    public void ParseDetail_ReadsTitleDateImagesAndLink()
    {
        const string json = """
            {"title":{"content":"10月15日不停服更新公告","date":"2026-10-19"},
             "text":"","images":["https://h74.update.netease.com/game_notice/image/1.jpg"],
             "link":"https://mp.weixin.qq.com/s/x"}
            """;

        NeteaseGamesLauncherApiNews.NoticeDetail? detail = NeteaseGamesLauncherApiNews.ParseDetail(json);

        Assert.NotNull(detail);
        Assert.Equal("10月15日不停服更新公告", detail!.Title);
        Assert.Equal("2026-10-19", detail.Date);
        Assert.Equal("https://mp.weixin.qq.com/s/x", detail.Link);
        Assert.Single(detail.Images);
    }

    [Fact]
    public void ParseDetail_StripsInlineMarkupFromTheTitle()
    {
        const string json = """{"title":{"content":"<b>重要</b>公告","date":"2026-10-19"},"images":[],"link":"https://a.b"}""";

        NeteaseGamesLauncherApiNews.NoticeDetail? detail = NeteaseGamesLauncherApiNews.ParseDetail(json);

        Assert.Equal("重要公告", detail!.Title);
    }

    [Fact]
    public void ParseDetail_RejectsUnusableDocuments()
    {
        Assert.Null(NeteaseGamesLauncherApiNews.ParseDetail(string.Empty));
        Assert.Null(NeteaseGamesLauncherApiNews.ParseDetail("{not json"));
        Assert.Null(NeteaseGamesLauncherApiNews.ParseDetail("""{"title":{"content":""},"images":[],"link":""}"""));
    }

    [Fact]
    public void ParseDetail_IgnoresNonHttpImages()
    {
        const string json = """{"title":{"content":"t","date":"2026-10-19"},"images":["relative.jpg","https://ok.jpg"],"link":""}""";

        NeteaseGamesLauncherApiNews.NoticeDetail? detail = NeteaseGamesLauncherApiNews.ParseDetail(json);

        Assert.Single(detail!.Images);
        Assert.Equal("https://ok.jpg", detail.Images[0]);
    }
}