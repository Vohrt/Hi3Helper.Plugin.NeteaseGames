using System;
using Hi3Helper.NeteaseGames.Core;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The launcher's own record (<c>patchmd5_windows64_normal.txt</c>) carries two independent facts: line 3 is the
///     installed patch version and line 0 is the content version directory that round was downloaded from. The
///     content version decides whether side files of that round (the shader cache list) are already on disk.
///     Measured on 2026-10-23: line 0 = <c>20261023122834919</c>, line 3 = <c>6589318</c>.
/// </summary>
public class LauncherRecordContentVersionTests
{
    private const string LiveRecord = """
        20261023122834919
        10590933344404223586
        0
        6589318

        {"normal":{"mininum_pkgversion":0},"@metadata@":{"@private_res_version@":"6589318"}}
        """;

    [Fact]
    public void ContentVersion_ComesFromLineZero()
    {
        Assert.Equal("20261023122834919", NeteaseGamesLocalState.ReadPatchMd5ContentVersion(LiveRecord));
    }

    [Fact]
    public void PatchVersion_AndContentVersion_AreIndependent()
    {
        // The two lines must not be confused: line 0 is a 17-digit stamp, line 3 a 7-digit counter.
        Assert.Equal("20261023122834919", NeteaseGamesLocalState.ReadPatchMd5ContentVersion(LiveRecord));
        Assert.Equal(6589318, NeteaseGamesLocalState.ReadPatchMd5Version(LiveRecord));
    }

    [Fact]
    public void ContentVersion_RejectsNonNumericFirstLine()
    {
        const string broken = """
            not-a-version
            123
            0
            6589318
            {}
            """;

        Assert.Equal(string.Empty, NeteaseGamesLocalState.ReadPatchMd5ContentVersion(broken));
        // The patch version is still readable: a malformed first line must not hide it.
        Assert.Equal(6589318, NeteaseGamesLocalState.ReadPatchMd5Version(broken));
    }

    [Fact]
    public void ContentVersion_HandlesEmptyInput()
    {
        Assert.Equal(string.Empty, NeteaseGamesLocalState.ReadPatchMd5ContentVersion(string.Empty));
        Assert.Equal(string.Empty, NeteaseGamesLocalState.ReadPatchMd5ContentVersion("   "));
    }

    [Fact]
    public void ContentVersion_HandlesRecordWithoutBody()
    {
        const string noBody = """
            20261023122834919
            10590933344404223586
            0
            6589318
            """;

        Assert.Equal("20261023122834919", NeteaseGamesLocalState.ReadPatchMd5ContentVersion(noBody));
    }
}
