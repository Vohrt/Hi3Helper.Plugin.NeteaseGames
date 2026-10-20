using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.NeteaseGames.Core.Management;

/// <summary>
///     Starts the game directly, without the official PCLauncher.
///     <para>
///         The NetEase client ships the complete account stack (UniSDK) itself, so the launcher is only a
///         download manager - the game can be started on its own. The start contract was taken from a running
///         official launcher: it starts the game executable of the installation with the installation root as
///         the working directory and with four arguments, without staging a copy of the executable and without
///         the file system arguments of the launcher.
///     </para>
/// </summary>
public static class NeteaseGamesGameLauncher
{
    /// <summary>
    ///     The command line the official launcher uses to start the game.
    ///     <para>
    ///         It was read from a running official launcher:
    ///         <c>"...\Engine\Binaries\Win64Release\ywzh.exe" --start=Python --use-pso-precache=1
    ///         --python-args=innerdesktop;script_mode#RELEASE_MODE --dx12</c>.
    ///     </para>
    /// </summary>
    public const string DefaultGameArguments =
        "--start=Python --use-pso-precache=1 --python-args=innerdesktop;script_mode#RELEASE_MODE --dx12";

    /// <summary>
    ///     Starts the game executable of an installation.
    /// </summary>
    /// <param name="gameDirectory">The root of the game installation.</param>
    /// <param name="arguments">The command line arguments, or <c>null</c> for <see cref="DefaultGameArguments" />.</param>
    /// <param name="channelTag">The channel tag of the installation, or <c>null</c> when it is unknown.</param>
    /// <returns>The started process.</returns>
    /// <exception cref="FileNotFoundException">The game executable does not exist.</exception>
    public static Process Launch(string gameDirectory, string? arguments = null, string? channelTag = null)
    {
        string executable = Path.Combine(gameDirectory, NeteaseGamesPaths.GameExecutableRelativePath);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The game executable could not be found.", executable);
        }

        PrepareLaunch(gameDirectory, channelTag);

        ProcessStartInfo startInfo = new(executable)
        {
            // The game resolves its resources relative to the working directory, so it has to be the
            // directory of the game executable (verified: with the installation root the game crashes).
            WorkingDirectory = Path.GetDirectoryName(executable) ?? gameDirectory,
            // The game executable is manifested as "requireAdministrator", so it has to be started through the
            // shell - a plain CreateProcess would fail with ERROR_ELEVATION_REQUIRED (740).
            UseShellExecute = true,
            Verb = "runas"
        };

        string effectiveArguments = arguments ?? DefaultGameArguments;
        startInfo.Arguments = QuoteArguments(effectiveArguments);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("The game process could not be started.");
    }

    /// <summary>
    ///     Prepares an installation for a direct start.
    ///     <para>
    ///         Nothing has to be prepared: the official launcher does not stage a copy of the game executable
    ///         either, it starts the executable of the installation itself. The state of the patch directory
    ///         belongs to the game itself - a directly started game rewrites its version and check files.
    ///     </para>
    /// </summary>
    /// <param name="gameDirectory">The root of the game installation.</param>
    /// <param name="channelTag">The channel tag of the installation, or <c>null</c> when it is unknown.</param>
    public static void PrepareLaunch(string gameDirectory, string? channelTag = null)
    {
        string executable = Path.Combine(gameDirectory, NeteaseGamesPaths.GameExecutableRelativePath);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The game executable could not be found.", executable);
        }
    }

    /// <summary>
    ///     Quotes the arguments that contain a semicolon, otherwise the Windows command line parser would cut
    ///     them in half (the <c>--python-args=innerdesktop;script_mode#RELEASE_MODE</c> argument).
    /// </summary>
    public static string QuoteArguments(string arguments)
    {
        StringBuilder builder = new();
        int start = 0;
        for (int i = 0; i <= arguments.Length; i++)
        {
            bool end = i == arguments.Length;
            char current = end ? ' ' : arguments[i];
            if (!end && current != ' ')
            {
                continue;
            }

            string token = arguments[start..i];
            if (token.Length > 0)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                if (token.Contains(';', StringComparison.Ordinal) && !token.Contains('"', StringComparison.Ordinal))
                {
                    builder.Append('"').Append(token).Append('"');
                }
                else
                {
                    builder.Append(token);
                }
            }

            start = i + 1;
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Checks whether the game is currently running.
    /// </summary>
    /// <param name="gameDirectory">The root of the game installation.</param>
    public static bool IsRunning(string gameDirectory)
    {
        foreach (string processName in GetProcessNames(gameDirectory))
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    return true;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Lists the processes of the game (used by the host to follow the lifetime of the game).
    /// </summary>
    /// <param name="gameDirectory">The root of the game installation.</param>
    public static IReadOnlyList<int> GetRunningProcessIds(string gameDirectory)
    {
        List<int> ids = [];
        foreach (string processName in GetProcessNames(gameDirectory))
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    ids.Add(process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return ids;
    }

    /// <summary>
    ///     The names of the processes that belong to the game.
    ///     <para>
    ///         The game starts itself a second time under the name <c>ywzh_.exe</c>, so both names have to be
    ///         taken into account when the lifetime of the game is followed.
    ///     </para>
    /// </summary>
    private static IEnumerable<string> GetProcessNames(string gameDirectory)
    {
        string executable = Path.Combine(gameDirectory, NeteaseGamesPaths.GameExecutableRelativePath);
        string processName = Path.GetFileNameWithoutExtension(executable);
        yield return processName;

        string staged = Path.GetFileNameWithoutExtension(NeteaseGamesPaths.StagedGameExecutableName);
        if (!string.Equals(staged, processName, StringComparison.OrdinalIgnoreCase))
        {
            yield return staged;
        }
    }

    #region Host facing game launch API

    /// <summary>
    ///     Starts the game on behalf of the host.
    ///     <para>
    ///         The plugin takes over the start because the game has to be started with elevation (its manifest
    ///         asks for administrator rights) and because the host does not know the start contract of the game.
    ///     </para>
    /// </summary>
    public static (bool IsSupported, Task<bool> Task) LaunchGameFromGameManager(
        GameManagerExtension.RunGameFromGameManagerContext context, string? startArgument, bool isRunBoosted,
        ProcessPriorityClass processPriority, CancellationToken token)
    {
        return (true, Impl());

        async Task<bool> Impl()
        {
            if (!TryGetGamePath(context, out string? gamePath))
            {
                return false;
            }

            string executable = Path.Combine(gamePath, NeteaseGamesPaths.GameExecutableRelativePath);
            if (!File.Exists(executable))
            {
                return false;
            }

            // The official launcher writes the channel tag of the installation when it is missing.
            PrepareLaunch(gamePath);

            // The game ships the account stack (UniSDK) itself and shows its own login dialog, so no login
            // state has to be provided. A stored state is still applied when there is one.
            if (NeteaseGamesSession.ApplyStoredSession())
            {
                SharedStatic.InstanceLogger?.LogInformation("[NeteaseGames] The stored login state was applied");
            }

            ProcessStartInfo startInfo = new(executable)
            {
                // The game resolves its resources relative to the working directory, so it has to be the
                // directory of the game executable (verified: with the installation root the game crashes).
                WorkingDirectory = Path.GetDirectoryName(executable) ?? gamePath,
                // The game executable is manifested as "requireAdministrator", so it has to be started through
                // the shell - a plain CreateProcess would fail with ERROR_ELEVATION_REQUIRED (740).
                UseShellExecute = true,
                Verb = "runas",
                Arguments = QuoteArguments(CombineArguments(DefaultGameArguments, startArgument))
            };

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception)
            {
                return false;
            }

            if (process is null)
            {
                return false;
            }

            using (process)
            {
                try
                {
                    process.PriorityBoostEnabled = isRunBoosted;
                    process.PriorityClass = processPriority;
                }
                catch (Exception)
                {
                    // The priority of an elevated process cannot be changed from a non elevated host.
                }
            }

            // The game opens its window a little later; until then it only holds a few hundred megabytes.
            if (!await WaitForGameWindowAsync(gamePath, TimeSpan.FromMinutes(3), token).ConfigureAwait(false))
            {
                SharedStatic.InstanceLogger?.LogError(
                    "[NeteaseGames] The game did not open a window within three minutes");
                return false;
            }

            await WaitForGameExitAsync(gamePath, token).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    ///     Waits until the game shows its window.
    /// </summary>
    private static async Task<bool> WaitForGameWindowAsync(
        string gameDirectory, TimeSpan timeout, CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            foreach (string processName in GetProcessNames(gameDirectory))
            {
                foreach (Process candidate in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        candidate.Refresh();
                        if (candidate.MainWindowHandle != IntPtr.Zero)
                        {
                            return true;
                        }
                    }
                    catch (Exception)
                    {
                        // The process may have exited in the meantime.
                    }
                    finally
                    {
                        candidate.Dispose();
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Restores the search path of the host process.
    /// </summary>
    private static void RestorePath(string? path)
    {
        try
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }
        catch (Exception)
        {
            // Ignore, the path is restored on the next start anyway.
        }
    }

    /// <summary>
    ///     Reports whether the game is running.
    /// </summary>
    public static bool IsGameRunning(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool isGameRunning, out DateTime gameStartTime)
    {
        isGameRunning = false;
        gameStartTime = default;

        if (!TryGetGamePath(context, out string? gamePath))
        {
            return true;
        }

        foreach (string processName in GetProcessNames(gamePath))
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    isGameRunning = true;
                    try
                    {
                        gameStartTime = process.StartTime;
                    }
                    catch (Exception)
                    {
                        // The start time of an elevated process is not readable from a non elevated host.
                        gameStartTime = DateTime.Now;
                    }

                    return true;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return true;
    }

    /// <summary>
    ///     Waits until the game is running.
    /// </summary>
    public static async Task<bool> WaitRunningGame(GameManagerExtension.RunGameFromGameManagerContext context,
        CancellationToken token)
    {
        if (!TryGetGamePath(context, out string? gamePath))
        {
            return false;
        }

        while (!token.IsCancellationRequested)
        {
            if (IsRunning(gamePath))
            {
                return true;
            }

            try
            {
                await Task.Delay(500, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    ///     Stops the game.
    /// </summary>
    public static bool KillRunningGame(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool wasGameRunning, out DateTime gameStartTime)
    {
        wasGameRunning = false;
        gameStartTime = default;

        if (!TryGetGamePath(context, out string? gamePath))
        {
            return true;
        }

        foreach (string processName in GetProcessNames(gamePath))
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    wasGameRunning = true;
                    try
                    {
                        gameStartTime = process.StartTime;
                    }
                    catch (Exception)
                    {
                        // The start time of an elevated process is not readable from a non elevated host.
                        gameStartTime = DateTime.Now;
                    }

                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                        // The process may have exited in the meantime.
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return true;
    }

    /// <summary>
    ///     Waits until no game process is left.
    /// </summary>
    private static async Task WaitForGameExitAsync(string gamePath, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!IsRunning(gamePath))
            {
                return;
            }

            try
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    ///     Reads the game path from the game manager of the host.
    /// </summary>
    private static bool TryGetGamePath(GameManagerExtension.RunGameFromGameManagerContext context,
        out string gamePath)
    {
        gamePath = string.Empty;
        try
        {
            context.GameManager.GetGamePath(out string? path);
            gamePath = path ?? string.Empty;
        }
        catch (Exception)
        {
            // The host may not have a game path yet.
        }

        return gamePath.Length > 0;
    }

    /// <summary>
    ///     Merges the official arguments with the additional arguments of the host.
    /// </summary>
    private static string CombineArguments(string official, string? additional)
    {
        if (string.IsNullOrWhiteSpace(additional))
        {
            return official;
        }

        return official + " " + additional.Trim();
    }

    #endregion
}
