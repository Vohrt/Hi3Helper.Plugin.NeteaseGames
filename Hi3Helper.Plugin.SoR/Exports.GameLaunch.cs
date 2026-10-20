using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.NeteaseGames.Core.Management;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.Plugin.SoR;

public partial class Exports
{
    /// <summary>
    ///     The game is started by the plugin itself: the client refuses to run without the launcher marker in
    ///     the patch directory and its manifest asks for administrator rights, so the start has to go through
    ///     the shell.
    /// </summary>
    protected override (bool IsSupported, Task<bool> Task) LaunchGameFromGameManagerCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, string? startArgument, bool isRunBoosted,
        ProcessPriorityClass processPriority, CancellationToken token)
        => NeteaseGamesGameLauncher.LaunchGameFromGameManager(context, startArgument, isRunBoosted, processPriority, token);

    protected override bool IsGameRunningCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool isGameRunning, out DateTime gameStartTime)
        => NeteaseGamesGameLauncher.IsGameRunning(context, out isGameRunning, out gameStartTime);

    protected override (bool IsSupported, Task<bool> Task) WaitRunningGameCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, CancellationToken token)
        => (true, NeteaseGamesGameLauncher.WaitRunningGame(context, token));

    protected override bool KillRunningGameCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool wasGameRunning, out DateTime gameStartTime)
        => NeteaseGamesGameLauncher.KillRunningGame(context, out wasGameRunning, out gameStartTime);
}
