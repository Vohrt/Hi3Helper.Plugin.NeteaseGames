using System;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.NeteaseGames.Core.Management;
using Xunit;

namespace Hi3Helper.NeteaseGames.Tests;

/// <summary>
///     The exit awaiter of the plugin API means "wait until the game <b>exits</b>". Returning as soon as the
///     process was seen made the host treat a running game as finished and loop through
///     GAME STARTED / GAME STOPPED every few milliseconds.
/// </summary>
public class GameLaunchContractTests
{
    [Theory]
    [InlineData("ywzh", true)]
    [InlineData("ywzh_", true)]
    [InlineData("YWZH", true)]
    [InlineData("ywzh_launcher", false)]
    [InlineData("PCLauncher", false)]
    [InlineData("CollapseLauncher", false)]
    [InlineData("NTE", false)]
    public void IsGameProcessName_OnlyMatchesTheGameItself(string name, bool expected)
    {
        // The launcher (ywzh_launcher / PCLauncher) must never be mistaken for the game, otherwise the host
        // sees a "running game" that is not the game and the start/stop detection starts to flap.
        Assert.Equal(expected, NeteaseGamesGameLauncher.IsGameProcessName(name));
    }

    [Fact]
    public void Arguments_KeepTheSemicolonArgumentTogether()
    {
        string quoted = NeteaseGamesGameLauncher.QuoteArguments(NeteaseGamesGameLauncher.DefaultGameArguments);

        // --python-args=innerdesktop;script_mode#RELEASE_MODE contains a semicolon; splitting it would make the
        // engine parse two arguments and start with a broken script mode.
        Assert.Contains("\"--python-args=innerdesktop;script_mode#RELEASE_MODE\"", quoted, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultArguments_MatchTheOfficialLaunchContract()
    {
        Assert.Contains("--start=Python", NeteaseGamesGameLauncher.DefaultGameArguments, StringComparison.Ordinal);
        Assert.Contains("--use-pso-precache=1", NeteaseGamesGameLauncher.DefaultGameArguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--takeover", NeteaseGamesGameLauncher.DefaultGameArguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--fs-", NeteaseGamesGameLauncher.DefaultGameArguments, StringComparison.Ordinal);
    }

    [Fact]
    public void GameAppearTimeout_IsBounded()
    {
        // The awaiter may not block forever when the game never starts.
        Assert.True(NeteaseGamesGameLauncher.GameAppearTimeout > TimeSpan.Zero);
        Assert.True(NeteaseGamesGameLauncher.GameAppearTimeout <= TimeSpan.FromMinutes(2));
    }
}
