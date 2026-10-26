using Hi3Helper.NeteaseGames.Core;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The shader cache list refresh decision. It used to compare the content version, which moves on every round,
///     so a fresh installation (and every update) planned a 7,985,520 byte re-download of a file that had not
///     changed - and on 2026-10-26 that download was the object the whole run then failed on.
///     <para>
///         The manifest's <c>@metadata@.@shadercachelistmd5@["shadercachelist_dx12.txt"]</c> is the authoritative
///         hash; the local <c>patchmd5</c> record carries the same field for the installed round. Measured: both were
///         <c>4429205421084825608</c> when the file on disk was already current.
///     </para>
/// </summary>
public class ShaderCacheListHashTests
{
    private const string Record = """
        20261025174155378
        6233731084051916376
        0
        6600116

        {
            "normal": { "latest_pkgversion": 5282637 },
            "@metadata@": {
                "@bigpatchmd5@": 16703640110960187218,
                "@bigshadercachelistmd5@": {
                    "bigshadercachelist_dx11.txt": 16980104819606790995,
                    "bigshadercachelist_dx12.txt": 15990974540103381754,
                    "bigshadercachelist_dx9.txt": ""
                },
                "@shadercachelistmd5@": {
                    "shadercachelist_dx9.txt": 15262859430986571946,
                    "shadercachelist_dx11.txt": 2419789743448247974,
                    "shadercachelist_dx12.txt": 4429205421084825608
                }
            }
        }
        """;

    /// <summary>
    ///     The dx12 entry is read, and it is <b>not</b> the <c>@bigshadercachelistmd5@</c> entry of the same name -
    ///     both blocks carry a <c>dx12.txt</c> key and only the second is the one the game reads.
    /// </summary>
    [Fact]
    public void ReadsTheDx12EntryFromTheRightBlock()
    {
        Assert.Equal("4429205421084825608", NeteaseGamesLocalState.ReadPatchMd5ShaderCacheListHash(Record));
    }

    /// <summary>A record without the block yields an empty hash, which the planner reads as "unknown".</summary>
    [Fact]
    public void RecordWithoutTheBlockYieldsEmpty()
    {
        Assert.Equal(string.Empty,
            NeteaseGamesLocalState.ReadPatchMd5ShaderCacheListHash("""{"@metadata@":{"@bigpatchmd5@":1}}"""));
        Assert.Equal(string.Empty, NeteaseGamesLocalState.ReadPatchMd5ShaderCacheListHash(string.Empty));
    }

    /// <summary>The manifest parser exposes the same value the local reader finds in a record.</summary>
    [Fact]
    public void ManifestAndRecordAgree()
    {
        NeteaseGamesPatchManifest manifest = NeteaseGamesPatchManifest.Parse(Record);

        Assert.Equal("4429205421084825608", manifest.ShaderCacheListMd5);
        Assert.Equal(manifest.ShaderCacheListMd5, NeteaseGamesLocalState.ReadPatchMd5ShaderCacheListHash(Record));
    }
}
