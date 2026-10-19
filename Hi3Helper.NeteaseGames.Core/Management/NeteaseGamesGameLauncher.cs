using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.NeteaseGames.Core.Management;

/// <summary>
///     Starts the game directly, without the official PCLauncher.
///     <para>
///         The NetEase client ships the complete account stack (UniSDK) itself, so the launcher is only a
///         download manager - the game can be started on its own. The only thing the game insists on is the
///         launcher marker in the patch directory, which <see cref="PrepareLaunch" /> writes.
///     </para>
/// </summary>
public static class NeteaseGamesGameLauncher
{
    /// <summary>
    ///     The command line the official launcher uses to start the game (<c>game_args</c> in <c>config.xml</c>).
    /// </summary>
    public const string DefaultGameArguments = "--start=Python --use-pso-precache=1 --python-args=innerdesktop;script_mode#RELEASE_MODE";



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
    ///         The game opens <c>LocalData\Patch\ywzh.exe</c> while it boots and shows the dialog
    ///         "请从启动器启动游戏。" when that file does not exist. The official launcher stages a copy of the
    ///         game executable there, so the plugin does the same - only the presence of the file is checked,
    ///         its content does not matter.
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

        string marker = Path.Combine(gameDirectory, NeteaseGamesPaths.LauncherMarkerRelativePath);
        try
        {
            string? markerDirectory = Path.GetDirectoryName(marker);
            if (!string.IsNullOrEmpty(markerDirectory))
            {
                Directory.CreateDirectory(markerDirectory);
            }

            if (!IsUpToDateMarker(marker, executable))
            {
                File.Copy(executable, marker, overwrite: true);
            }
        }
        catch (Exception)
        {
            // A read-only or busy installation must not stop the start; the game will complain on its own.
        }

        if (string.IsNullOrEmpty(channelTag))
        {
            return;
        }

        string cpsTag = Path.Combine(gameDirectory, NeteaseGamesPaths.CpsTagRelativePath);
        try
        {
            if (!File.Exists(cpsTag))
            {
                File.WriteAllBytes(cpsTag, NeteaseGamesCrypto.EncryptCpsTag(channelTag));
            }
        }
        catch (Exception)
        {
            // The channel tag is optional - the game only needs the launcher marker.
        }
    }

    /// <summary>
    ///     Checks whether the staged launcher marker is the same build as the running game executable.
    /// </summary>
    private static bool IsUpToDateMarker(string marker, string executable)
    {
        try
        {
            if (!File.Exists(marker))
            {
                return false;
            }

            FileInfo markerInfo = new(marker);
            FileInfo executableInfo = new(executable);
            return markerInfo.Length == executableInfo.Length &&
                   markerInfo.LastWriteTimeUtc == executableInfo.LastWriteTimeUtc;
        }
        catch (Exception)
        {
            return false;
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
    ///         The game executable hands the start over to the staged build (<c>ywzh_.exe</c>), so both names
    ///         have to be taken into account when the lifetime of the game is followed.
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
    ///         The plugin takes over the start because the game refuses to run without the launcher marker in
    ///         the patch directory and because it has to be started with elevation (its manifest asks for
    ///         administrator rights).
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

            // The game refuses to start when the launcher marker is missing.
            PrepareLaunch(gamePath);

            // The official launcher does not start the game executable itself: it copies the executable into
            // the patch directory and starts *that* copy with the installation root as the working directory.
            // The game checks that it is the copy that lives in "<working directory>\LocalData\Patch", and
            // shows the dialog "请从启动器启动游戏。" otherwise.
            string staged = Path.Combine(gamePath, NeteaseGamesPaths.LauncherMarkerRelativePath);
            if (!File.Exists(staged))
            {
                staged = executable;
            }

            // The staged copy lives outside of the directory that holds the game libraries, so the loader
            // cannot find them ("CrashHunter_PC3.dll") and the start fails with a system error dialog.
            // The launcher therefore puts the binary directory on the search path of the child process.
            string? previousPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH",
                    $"{Path.GetDirectoryName(executable)}{Path.PathSeparator}{previousPath}");
            }
            catch (Exception)
            {
                // A missing search path is not fatal, the start reports the problem itself.
            }

            ProcessStartInfo startInfo = new(staged)
            {
                // The working directory is part of the contract: the game resolves the staged copy relative
                // to it.
                WorkingDirectory = gamePath,
                // The game executable is manifested as "requireAdministrator", so it has to be started through
                // the shell - a plain CreateProcess would fail with ERROR_ELEVATION_REQUIRED (740).
                UseShellExecute = true,
                Verb = "runas",
                // The official command line ends with the quoted takeover argument, which tells the game that
                // the launcher hands the session over to it.
                Arguments = QuoteArguments(CombineArguments(DefaultGameArguments, startArgument))
                            + " \" --takeover\""
            };

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception)
            {
                RestorePath(previousPath);
                return false;
            }
            finally
            {
                RestorePath(previousPath);
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

            await WaitForGameExitAsync(gamePath, token).ConfigureAwait(false);
            return true;
        }
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
    ///     <para>
    ///         The game executable hands the start over to the staged build and exits immediately, so the
    ///         lifetime of the game cannot be followed through the launched process alone.
    ///     </para>
    /// </summary>
    private static async Task WaitForGameExitAsync(string gamePath, CancellationToken token)
    {
        // Give the hand-over a moment, otherwise the game may not be visible yet.
        try
        {
            await Task.Delay(1500, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

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
