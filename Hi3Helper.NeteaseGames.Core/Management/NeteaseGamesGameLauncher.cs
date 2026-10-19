using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Hi3Helper.NeteaseGames.Core.Management;

/// <summary>
///     Starts the game directly, without the official PCLauncher.
///     <para>
///         The NetEase client ships the complete account stack (UniSDK) itself, so the launcher is only a
///         download manager - the game can be started on its own.
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
    /// <returns>The started process.</returns>
    /// <exception cref="FileNotFoundException">The game executable does not exist.</exception>
    public static Process Launch(string gameDirectory, string? arguments = null)
    {
        string executable = Path.Combine(gameDirectory, NeteaseGamesPaths.GameExecutableRelativePath);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The game executable could not be found.", executable);
        }

        ProcessStartInfo startInfo = new(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable) ?? gameDirectory,
            UseShellExecute = false
        };

        string effectiveArguments = arguments ?? DefaultGameArguments;
        startInfo.Arguments = QuoteArguments(effectiveArguments);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("The game process could not be started.");
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
        string executable = Path.Combine(gameDirectory, NeteaseGamesPaths.GameExecutableRelativePath);
        string processName = Path.GetFileNameWithoutExtension(executable);
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

        return false;
    }

    /// <summary>
    ///     Lists the processes of the game (used by the host to follow the lifetime of the game).
    /// </summary>
    /// <param name="gameDirectory">The root of the game installation.</param>
    public static IReadOnlyList<int> GetRunningProcessIds(string gameDirectory)
    {
        string executable = Path.Combine(gameDirectory, NeteaseGamesPaths.GameExecutableRelativePath);
        string processName = Path.GetFileNameWithoutExtension(executable);
        List<int> ids = [];
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

        return ids;
    }
}
