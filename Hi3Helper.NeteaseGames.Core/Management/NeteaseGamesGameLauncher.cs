using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.NeteaseGames.Core.Management;

/// <summary>
///     Starts the game directly, without the official PCLauncher.
///     <para>
///         The NetEase client ships the complete account stack (UniSDK) itself, so the launcher is only a
///         download manager - the game can be started on its own. The start contract was taken from a running
///         official launcher: it starts the game executable of the installation with the directory of the
///         executable as the working directory and with four arguments.
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
    ///     How long the game may take to show its window before the start is treated as failed.
    /// </summary>
    public static readonly TimeSpan WindowTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    ///     How long the exit awaiter waits for a game that has not appeared yet before giving up.
    /// </summary>
    public static readonly TimeSpan GameAppearTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     Quotes the arguments that contain a semicolon, otherwise the Windows command line parser would cut
    ///     them in half (the <c>--python-args=innerdesktop;script_mode#RELEASE_MODE</c> argument).
    /// </summary>
    /// <param name="arguments">The raw command line.</param>
    public static string QuoteArguments(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

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
    ///     Lists the processes of the game of a specific installation.
    ///     <para>
    ///         The process is matched by its <b>path</b>, not only by its name: two installations of the game
    ///         (or another NetEase title that happens to use the same executable name) must not be confused,
    ///         and a kill must never hit a process of another installation.
    ///     </para>
    /// </summary>
    /// <param name="gameDirectory">The root of the installation.</param>
    public static IReadOnlyList<Process> GetGameProcesses(string gameDirectory)
    {
        string executable = Path.GetFullPath(Path.Combine(gameDirectory, NeteaseGamesPaths.GameExecutableRelativePath));
        string binariesDirectory = Path.GetDirectoryName(executable) ?? gameDirectory;
        string gameRoot = Path.GetFullPath(gameDirectory);

        List<Process> processes = [];
        foreach (string name in GetProcessNames(executable))
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                if (BelongsToInstallation(process, gameRoot, binariesDirectory))
                {
                    processes.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
        }

        return processes;
    }

    /// <summary>
    ///     Checks whether a process belongs to an installation.
    ///     <para>
    ///         Matching is done by executable path. When the path cannot be read (which is the normal case for
    ///         an elevated game started from a non elevated host) the process only counts as ours when its
    ///         executable <b>name</b> matches the game and no other installation claims it - the game process
    ///         names are specific enough (<c>ywzh</c> / <c>ywzh_</c>) and a false negative would be worse than
    ///         the alternative, because the host would report the game as stopped while it is running.
    ///     </para>
    /// </summary>
    private static bool BelongsToInstallation(Process process, string gameRoot, string binariesDirectory)
    {
        try
        {
            string? path = process.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                string fullPath = Path.GetFullPath(path);
                return IsUnder(fullPath, gameRoot) || IsUnder(fullPath, binariesDirectory);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or NotSupportedException)
        {
            // The executable of an elevated process cannot be inspected from a non elevated host.
        }

        // Fall back to the process name only (see the remarks above).
        return IsGameProcessName(process.ProcessName);
    }

    /// <summary>
    ///     Whether a process name belongs to the game (the bootstrapper and the real client).
    /// </summary>
    /// <param name="processName">The process name without extension.</param>
    public static bool IsGameProcessName(string processName)
        => string.Equals(processName, "ywzh", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(processName, Path.GetFileNameWithoutExtension(NeteaseGamesPaths.StagedGameExecutableName),
               StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string root)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        string normalizedRoot = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return path.StartsWith(normalizedRoot, comparison);
    }

    /// <summary>
    ///     The names of the processes that belong to the game.
    ///     <para>
    ///         The game starts itself a second time under the name <c>ywzh_.exe</c>, so both names have to be
    ///         taken into account when the lifetime of the game is followed.
    ///     </para>
    /// </summary>
    private static IEnumerable<string> GetProcessNames(string executable)
    {
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
    ///         The returned task completes when the game exits.
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

            // The stored login state is applied only around the start: the tokens are a credential and must
            // not stay in the long living host process.
            using IDisposable? session = NeteaseGamesSession.ApplyStoredSession();
            if (session is not null)
            {
                SharedStatic.InstanceLogger?.LogInformation("[NeteaseGames] The stored login state was applied");
            }

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // The most common cause is the user declining the elevation prompt (1223 = cancelled).
                SharedStatic.InstanceLogger?.LogError(
                    "[NeteaseGames] The game could not be started: {Msg} (code {Code})", ex.Message, ex.NativeErrorCode);
                return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                SharedStatic.InstanceLogger?.LogError("[NeteaseGames] The game could not be started: {Msg}", ex.Message);
                return false;
            }

            if (process is null)
            {
                SharedStatic.InstanceLogger?.LogError("[NeteaseGames] The game process could not be created");
                return false;
            }

            // The process object of an elevated child is usable, but its handle is not owned by us.
            try
            {
                process.Dispose();
            }
            catch (Exception ex) when (ex is InvalidOperationException)
            {
                // Ignore: the game keeps running regardless of this handle.
            }

            // The game opens its window a little later; until then it only holds a few hundred megabytes.
            if (!await WaitForGameWindowAsync(gamePath, WindowTimeout, token).ConfigureAwait(false))
            {
                SharedStatic.InstanceLogger?.LogError(
                    "[NeteaseGames] The game did not open a window within {Timeout}", WindowTimeout);
                return false;
            }

            await WaitForGameExitAsync(gamePath, token).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    ///     Waits until the game shows its window.
    /// </summary>
    private static async Task<bool> WaitForGameWindowAsync(string gameDirectory, TimeSpan timeout, CancellationToken token)
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

            foreach (Process candidate in GetGameProcesses(gameDirectory))
            {
                try
                {
                    candidate.Refresh();
                    if (candidate.MainWindowHandle != IntPtr.Zero)
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The process may have exited in the meantime.
                }
                finally
                {
                    candidate.Dispose();
                }
            }
        }

        return false;
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

        foreach (Process process in GetGameProcesses(gamePath))
        {
            try
            {
                isGameRunning = true;
                try
                {
                    gameStartTime = process.StartTime;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
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

        return true;
    }

    /// <summary>
    ///     Waits until the running game exits.
    ///     <para>
    ///         ⚠ This awaiter means "wait until the game <b>exits</b>", not "wait until it starts". The host
    ///         awaits it as the process awaiter of a session that has already started:
    ///         <c>await ProcessAwaiter(token)</c> followed by "GAME STOPPED". Returning as soon as the process
    ///         is seen made the host believe the game had ended immediately, so it re-detected the still running
    ///         game and looped through GAME STARTED / GAME STOPPED every few milliseconds.
    ///     </para>
    /// </summary>
    public static async Task<bool> WaitRunningGame(GameManagerExtension.RunGameFromGameManagerContext context,
        CancellationToken token)
    {
        if (!TryGetGamePath(context, out string? gamePath))
        {
            return false;
        }

        // Give a freshly started game a moment to appear, then wait for it to disappear. The grace period
        // covers the hand over from the bootstrapper (ywzh.exe) to the real client (ywzh_.exe).
        DateTime deadline = DateTime.UtcNow + GameAppearTimeout;
        while (!token.IsCancellationRequested && !IsRunning(gamePath))
        {
            if (DateTime.UtcNow > deadline)
            {
                // Nothing started: there is nothing to wait for.
                return false;
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

        // Now wait until every game process is gone.
        while (!token.IsCancellationRequested)
        {
            if (!IsRunning(gamePath))
            {
                return true;
            }

            try
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    ///     Checks whether the game of an installation is running.
    /// </summary>
    /// <param name="gameDirectory">The root of the installation.</param>
    public static bool IsRunning(string gameDirectory)
    {
        foreach (Process process in GetGameProcesses(gameDirectory))
        {
            process.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Stops the game.
    /// </summary>
    /// <returns>
    ///     <c>true</c> when the game was stopped (or was not running); <c>false</c> when a process could not be
    ///     stopped, e.g. because the host is not elevated while the game is.
    /// </returns>
    public static bool KillRunningGame(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool wasGameRunning, out DateTime gameStartTime)
    {
        wasGameRunning = false;
        gameStartTime = default;

        if (!TryGetGamePath(context, out string? gamePath))
        {
            return true;
        }

        bool allKilled = true;
        foreach (Process process in GetGameProcesses(gamePath))
        {
            try
            {
                wasGameRunning = true;
                try
                {
                    gameStartTime = process.StartTime;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The start time of an elevated process is not readable from a non elevated host.
                    gameStartTime = DateTime.Now;
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // An elevated game cannot be killed from a non elevated host: report it instead of
                    // pretending that the button worked.
                    allKilled = false;
                    SharedStatic.InstanceLogger?.LogWarning(
                        "[NeteaseGames] The game process could not be stopped: {Msg} (code {Code})",
                        ex.Message, (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode ?? 0);
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        return allKilled;
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
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
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
