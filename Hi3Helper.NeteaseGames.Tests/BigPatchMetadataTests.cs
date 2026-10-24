using System;
using Hi3Helper.NeteaseGames.Core;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The metadata block carries the switches that decide whether a round may touch the base shard tier. The
///     values below were read from the live manifest on 2026-10-23.
/// </summary>
public class BigPatchMetadataTests
{
    /// <summary>The live manifest of 2026-10-23, trimmed to the fields that matter here.</summary>
    private const string LiveManifest = """
        20261023122834919
        10590933344404223586
        0
        6589318
        {"normal":{"mininum_pkgversion":0,"review_pkgversion":[],"new_package_url":"","latest_pkgversion":5282637},
         "@metadata@":{"@bigpatchmd5@":6128997482385675719,"@private_res_version@":"6589318",
                       "@engine_version@":"261021114347_cd5d7a8d5_release","@patch_branch@":"release",
                       "@bigpatchopen@":true,"@is_full@":false,"@base_version@":20261021113421287}}
        """;

    [Fact]
    public void BigPatchOpen_IsReadFromMetadata()
    {
        // Measured `true` on the live branch during the rounds that rewrote ten base shards.
        Assert.True(NeteaseGamesPatchManifest.Parse(LiveManifest).IsBigPatchOpen);
    }

    [Fact]
    public void IsFullPackage_IsReadFromMetadata()
    {
        Assert.False(NeteaseGamesPatchManifest.Parse(LiveManifest).IsFullPackage);
    }

    [Fact]
    public void BigPatchMd5_IsKeptAsRawToken()
    {
        Assert.Equal("6128997482385675719", NeteaseGamesPatchManifest.Parse(LiveManifest).BigPatchMd5);
    }

    [Fact]
    public void LiveManifest_StillParsesTheCoreFields()
    {
        NeteaseGamesPatchManifest manifest = NeteaseGamesPatchManifest.Parse(LiveManifest);

        Assert.Equal("20261023122834919", manifest.ContentVersion);
        Assert.Equal(6589318, manifest.PatchVersion);
        Assert.Equal("261021114347_cd5d7a8d5_release", manifest.EngineVersion);
        Assert.True(manifest.IsUsable);
    }

    /// <summary>
    ///     A manifest without the switches must not look like a base-tier round: the defaults are the safe side.
    /// </summary>
    [Fact]
    public void MissingSwitches_DefaultToInactive()
    {
        const string minimal = """
            20261023122834919
            10590933344404223586
            0
            6589318
            {"@metadata@":{"@base_version@":20261021113421287}}
            """;

        NeteaseGamesPatchManifest manifest = NeteaseGamesPatchManifest.Parse(minimal);

        Assert.False(manifest.IsBigPatchOpen);
        Assert.False(manifest.IsFullPackage);
        Assert.Equal(string.Empty, manifest.BigPatchMd5);
        Assert.Equal(6589318, manifest.PatchVersion);
    }
}
