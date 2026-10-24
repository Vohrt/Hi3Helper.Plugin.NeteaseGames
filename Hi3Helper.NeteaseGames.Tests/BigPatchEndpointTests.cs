using System;
using Hi3Helper.NeteaseGames.Core;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The base shard tier ("big patch") lives under a <c>big_</c> prefixed directory, NOT the bare content version.
///     Measured 2026-10-23 by reading the live launcher's own requests out of its memory:
///     <c>https://h74.gph.netease.com/big_20261023122834919/bigpatchlist_windows64_normal.txt</c> answers 200 with
///     11,124,133 bytes, while every <c>/{version}/bigpatchlist...</c> shape answers 404.
///     <para>
///         ⚠ 2026-10-24: the version that goes into that directory is <c>@base_version@</c>, not line 0.
///         60 random chunks under <c>big_{line 0}</c>: 1 hit; under <c>big_{@base_version@}</c>: 60 hits.
///     </para>
/// </summary>
public class BigPatchEndpointTests
{
    private const string ContentVersion = "20261023122834919";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1792742041);

    [Fact]
    public void ContentPath_PrefixesTheDirectoryWithBig()
    {
        Assert.Equal("big_20261023122834919/bigpatchlist_windows64_normal.txt",
            NeteaseGamesContentEndpoints.GetBigPatchContentPath(ContentVersion, null,
                NeteaseGamesContentEndpoints.BigPatchListFileName));
    }

    [Fact]
    public void ContentPath_UsesTheSectionForChunks()
    {
        Assert.Equal("big_20261023122834919/big_common/Char_736",
            NeteaseGamesContentEndpoints.GetBigPatchContentPath(ContentVersion,
                NeteaseGamesContentEndpoints.BigCommonSection, "Char_736"));

        Assert.Equal("big_20261023122834919/big_shader/dx12_916",
            NeteaseGamesContentEndpoints.GetBigPatchContentPath(ContentVersion,
                NeteaseGamesContentEndpoints.BigShaderSection, "dx12_916"));
    }

    /// <summary>
    ///     The URL has to carry the same signature as every other content request, computed over the path that
    ///     includes the <c>big_</c> prefix (signing the bare version path yields 403).
    /// </summary>
    [Fact]
    public void Url_IsSignedOverTheBigPrefixedPath()
    {
        string url = NeteaseGamesContentEndpoints.GetBigPatchUrl(ContentVersion, null,
            NeteaseGamesContentEndpoints.BigPatchListFileName, Now);

        Assert.StartsWith("https://h74.gph.netease.com/big_20261023122834919/bigpatchlist_windows64_normal.txt?",
            url, StringComparison.Ordinal);

        string path = $"/big_{ContentVersion}/bigpatchlist_windows64_normal.txt";
        string key2 = NeteaseGamesUrlSigner.GetKey2(Now);
        string key1 = NeteaseGamesUrlSigner.GetKey1(path, key2);

        Assert.Contains($"key1={key1}", url, StringComparison.Ordinal);
        Assert.Contains($"key2={key2}", url, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Guards the exact bug that made the tier look unreachable: the path must never be the bare version.
    /// </summary>
    [Fact]
    public void ContentPath_NeverUsesTheBareVersion()
    {
        string path = NeteaseGamesContentEndpoints.GetBigPatchContentPath(ContentVersion,
            NeteaseGamesContentEndpoints.BigCommonSection, "r_8921");

        Assert.False(path.StartsWith(ContentVersion, StringComparison.Ordinal));
        Assert.StartsWith("big_", path, StringComparison.Ordinal);
    }
}
