using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     The on-disk layout of a NetEase game installation (Messiah engine, no official launcher required).
/// </summary>
public static class NeteaseGamesPaths
{
    /// <summary>
    ///     Relative path of the game executable.
    /// </summary>
    public const string GameExecutableRelativePath = @"Engine\Binaries\Win64Release\ywzh.exe";

    /// <summary>
    ///     Relative path of the engine version marker.
    /// </summary>
    public const string BuiltVersionRelativePath = @"Engine\Config\Built.version";

    /// <summary>
    ///     Relative path of the engine file hash list.
    /// </summary>
    public const string EngineMd5RelativePath = @"Engine\md5_engine.txt";

    /// <summary>
    ///     Relative path of the patch working directory.
    /// </summary>
    public const string PatchDirectoryRelativePath = @"LocalData\Patch";

    /// <summary>
    ///     The name of the staged game executable the launcher downloads next to the running one.
    ///     <para>
    ///         The game executable hands the start over to <c>ywzh_.exe</c> (the freshly downloaded build) and
    ///         exits; the staged build is the one that keeps running. Both names have to be followed when the
    ///         lifetime of the game is tracked.
    ///     </para>
    /// </summary>
    public const string StagedGameExecutableName = "ywzh_.exe";

    /// <summary>
    ///     Relative path of the user documents (version data, notices, hotfix cache).
    /// </summary>
    public const string DocumentsRelativePath = "Documents";

    /// <summary>
    ///     Name of the local patch list file.
    /// </summary>
    public const string PatchListFileName = "patchlist_windows64_normal.txt";

    /// <summary>
    ///     Name of the local patch md5 file.
    /// </summary>
    public const string PatchMd5FileName = "patchmd5_windows64_normal.txt";

    /// <summary>
    ///     Name of the local package version file.
    /// </summary>
    public const string PackageVersionFileName = "pkgversion";

    /// <summary>
    ///     Name of the local game version token file.
    /// </summary>
    public const string GameVersionFileName = "gameversion";

    /// <summary>
    ///     Name of the local latest engine version file.
    /// </summary>
    public const string LatestEngineVersionFileName = "latest_engine_ver";

    /// <summary>
    ///     Name of the local package check file.
    /// </summary>
    public const string PackageCheckFileName = "pkgcheck";

    /// <summary>
    ///     The key the official launcher owns, relative to <c>HKEY_CURRENT_USER</c>.
    ///     <para>
    ///         The launcher stores only values in it (<c>InstPath</c>, <c>AdditionalPaths</c>,
    ///         <c>APP_EXE_NAME</c>) and creates no sub keys, so it can safely act as the parent of the key the
    ///         host uses for the Collapse specific settings of this game.
    ///     </para>
    /// </summary>
    public const string LauncherRegistryKeyName = "OB_ywzh_launcher_PC";

    /// <summary>
    ///     The full path of the launcher key below <c>HKEY_CURRENT_USER</c> (used for the installation
    ///     discovery, which reads the key directly).
    /// </summary>
    public const string RegistryKeyPath = @"Software\" + LauncherRegistryKeyName;

    /// <summary>
    ///     The <c>GameVendorName</c> of the preset.
    ///     <para>
    ///         The host composes the settings key as <c>Software\{vendor}\{game}</c>. Naming the vendor after the
    ///         launcher key puts the Collapse settings <b>below the official key</b>
    ///         (<c>Software\OB_ywzh_launcher_PC\Collapse</c>) instead of next to it - the launcher keeps its own
    ///         values in the parent key untouched, and it is obvious who owns the child key.
    ///     </para>
    /// </summary>
    public const string CollapseSettingsVendorName = LauncherRegistryKeyName;

    /// <summary>
    ///     The <c>GameRegistryKeyName</c> of the preset (the leaf key that holds the Collapse settings).
    /// </summary>
    public const string CollapseSettingsKeyName = "Collapse";

    /// <summary>
    ///     The registry value that holds the game directories.
    /// </summary>
    public const string RegistryAdditionalPathsValue = "AdditionalPaths";

    /// <summary>
    ///     The registry value that holds the launcher directory.
    /// </summary>
    public const string RegistryInstallPathValue = "InstPath";

    /// <summary>
    ///     The registry value that holds the game executable name.
    /// </summary>
    public const string RegistryExecutableValue = "APP_EXE_NAME";
}

/// <summary>
///     The versions that are currently installed, read purely from local files.
/// </summary>
public sealed class NeteaseGamesLocalState
{
    private static readonly ConcurrentDictionary<string, (DateTime WriteTime, long Length, NeteaseGamesPatchList? List)>
        PatchListCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The content/package version (<c>pkgversion</c>), e.g. <c>6521279</c>.
    /// </summary>
    public long PackageVersion { get; init; }

    /// <summary>
    ///     The patch version announced by the local <c>patchmd5</c> file, e.g. <c>6534552</c>.
    /// </summary>
    public long PatchVersion { get; init; }

    /// <summary>
    ///     The engine version string, e.g. <c>261014173721_4a476f25b_release_r0000_6ad03d31</c>.
    /// </summary>
    public string EngineVersion { get; init; } = string.Empty;

    /// <summary>
    ///     The latest engine version timestamp, e.g. <c>202610151759</c>.
    /// </summary>
    public string LatestEngineVersion { get; init; } = string.Empty;

    /// <summary>
    ///     The raw game version token, e.g. <c>CN_windows_0a66d2e2a_6521279_6534552</c>.
    /// </summary>
    public string GameVersionToken { get; init; } = string.Empty;

    /// <summary>
    ///     The shader cache version.
    /// </summary>
    public string ShaderVersion { get; init; } = string.Empty;

    /// <summary>
    ///     Reads the local state from a game installation directory.
    /// </summary>
    /// <param name="gameDirectory">The root of the game installation.</param>
    public static NeteaseGamesLocalState Read(string gameDirectory)
    {
        string patchDirectory = Path.Combine(gameDirectory, NeteaseGamesPaths.PatchDirectoryRelativePath);
        string documentsDirectory = Path.Combine(gameDirectory, NeteaseGamesPaths.DocumentsRelativePath);

        long packageVersion = ReadInt(Path.Combine(patchDirectory, NeteaseGamesPaths.PackageVersionFileName));

        // `pkgversion` is the file the plugin writes as the commit point of an update, so it is the authority on
        // what is installed. The other two sources are written by the game client and go stale after a plugin
        // update; they are only used when `pkgversion` is missing.
        long patchVersion = packageVersion;
        string engineVersion = ReadText(Path.Combine(gameDirectory, NeteaseGamesPaths.BuiltVersionRelativePath));
        string latestEngineVersion = ReadText(Path.Combine(patchDirectory, NeteaseGamesPaths.LatestEngineVersionFileName));
        string gameVersionToken = ReadText(Path.Combine(patchDirectory, NeteaseGamesPaths.GameVersionFileName));
        string shaderVersion = string.Empty;

        // The local patchmd5 file announces the newest patch version the client knows about. It is not refreshed
        // by a plugin update, so it may only fill in when `pkgversion` is unreadable.
        string patchMd5File = Path.Combine(patchDirectory, NeteaseGamesPaths.PatchMd5FileName);
        if (patchVersion <= 0 && File.Exists(patchMd5File))
        {
            try
            {
                byte[] plain = NeteaseGamesCrypto.DecryptHexEnvelope(File.ReadAllBytes(patchMd5File));
                string text = System.Text.Encoding.UTF8.GetString(plain);
                int index = text.IndexOf("\"latest_pkgversion\"", StringComparison.Ordinal);
                if (index >= 0)
                {
                    int colon = text.IndexOf(':', index);
                    int end = text.IndexOfAny(['}', ',', '\n'], colon + 1);
                    if (colon > 0 && end > colon)
                    {
                        string value = text[(colon + 1)..end].Trim().Trim('"');
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                        {
                            patchVersion = parsed;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
            {
                // A broken patchmd5 file must not prevent the rest of the state from being read.
            }
        }

        // versiondata.json is written by the game client, not by the plugin, so it goes stale as soon as the
        // plugin installs an update: it kept reporting the version from before the update and the host then
        // offered the same update forever. It is therefore only a fallback for the values the plugin cannot
        // read from the patch directory itself.
        string versionDataFile = Path.Combine(documentsDirectory, "versiondata.json");
        if (File.Exists(versionDataFile))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(versionDataFile));
                JsonElement root = document.RootElement;
                if (packageVersion <= 0 &&
                    root.TryGetProperty("package_version", out JsonElement packageValue) &&
                    TryGetInt64Flexible(packageValue, out long parsedPackage))
                {
                    packageVersion = parsedPackage;
                }

                if (patchVersion <= 0 &&
                    root.TryGetProperty("patch_version", out JsonElement patchValue) &&
                    TryGetInt64Flexible(patchValue, out long parsedPatch))
                {
                    patchVersion = parsedPatch;
                }

                if (root.TryGetProperty("engine_version", out JsonElement engineValue) &&
                    engineValue.ValueKind == JsonValueKind.String)
                {
                    engineVersion = engineValue.GetString() ?? engineVersion;
                }

                if (root.TryGetProperty("shader_version", out JsonElement shaderValue) &&
                    shaderValue.ValueKind == JsonValueKind.String)
                {
                    shaderVersion = shaderValue.GetString() ?? string.Empty;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Ignore malformed version data.
            }
        }

        return new NeteaseGamesLocalState
        {
            PackageVersion = packageVersion,
            PatchVersion = patchVersion,
            EngineVersion = engineVersion,
            LatestEngineVersion = latestEngineVersion,
            GameVersionToken = gameVersionToken,
            ShaderVersion = shaderVersion
        };
    }

    /// <summary>
    ///     Reads the decrypted local patch list.
    ///     <para>
    ///         The list is 17 MB of JSON with more than 230k entries, and the host asks for it from the
    ///         synchronously answered size getters, so the parsed result is cached until the file changes.
    ///     </para>
    /// </summary>
    /// <param name="gameDirectory">The root of the game installation.</param>
    public static NeteaseGamesPatchList? ReadPatchList(string gameDirectory)
    {
        string file = Path.Combine(gameDirectory, NeteaseGamesPaths.PatchDirectoryRelativePath, NeteaseGamesPaths.PatchListFileName);
        if (!File.Exists(file))
        {
            InvalidatePatchListCache(gameDirectory);
            return null;
        }

        DateTime writeTime;
        long length;
        try
        {
            FileInfo info = new(file);
            writeTime = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (PatchListCache.TryGetValue(file, out (DateTime WriteTime, long Length, NeteaseGamesPatchList? List) cached) &&
            cached.WriteTime == writeTime &&
            cached.Length == length)
        {
            return cached.List;
        }

        NeteaseGamesPatchList? list = null;
        try
        {
            byte[] plain = NeteaseGamesCrypto.DecryptHexEnvelope(File.ReadAllBytes(file));
            list = NeteaseGamesPatchList.Parse(plain);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or JsonException)
        {
            // A broken list behaves like a missing one: everything is treated as changed.
        }

        PatchListCache[file] = (writeTime, length, list);
        return list;
    }

    /// <summary>
    ///     Drops the cached patch list of an installation (call it after the installation changed).
    /// </summary>
    /// <param name="gameDirectory">The root of the game installation.</param>
    public static void InvalidatePatchListCache(string gameDirectory)
    {
        string file = Path.Combine(gameDirectory, NeteaseGamesPaths.PatchDirectoryRelativePath, NeteaseGamesPaths.PatchListFileName);
        PatchListCache.TryRemove(file, out _);
    }

    /// <summary>
    ///     Finds an existing installation by looking at the launcher registry key.
    ///     <para>
    ///         The official launcher stores the game directory in <c>AdditionalPaths</c> and its own directory
    ///         in <c>InstPath</c> - and the latter contains zero width spaces, so it must never be used as a
    ///         path directly.
    ///     </para>
    /// </summary>
    /// <returns>The game directory, or <c>null</c> when nothing was found.</returns>
    public static string? FindExistingInstallPath()
    {
        foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using RegistryKey? key = hive.OpenSubKey(NeteaseGamesPaths.RegistryKeyPath);
                if (key is null)
                {
                    continue;
                }

                string? additional = key.GetValue(NeteaseGamesPaths.RegistryAdditionalPathsValue) as string;
                if (!string.IsNullOrWhiteSpace(additional))
                {
                    foreach (string candidate in additional.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
                    {
                        string cleaned = CleanPath(candidate);
                        if (IsGameDirectory(cleaned))
                        {
                            return cleaned;
                        }
                    }
                }

                string? installPath = key.GetValue(NeteaseGamesPaths.RegistryInstallPathValue) as string;
                if (!string.IsNullOrWhiteSpace(installPath))
                {
                    string cleaned = CleanPath(installPath);
                    // The launcher lives in a sibling directory of the game directory.
                    string? parent = Path.GetDirectoryName(cleaned.TrimEnd(Path.DirectorySeparatorChar));
                    if (parent is not null && Directory.Exists(parent))
                    {
                        foreach (string candidate in Directory.EnumerateDirectories(parent))
                        {
                            if (IsGameDirectory(candidate))
                            {
                                return candidate;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                           or IOException)
            {
                // Registry access may be denied; keep probing the other hive.
            }
        }

        return null;
    }

    /// <summary>
    ///     Removes the zero width characters the official launcher injects into its registry paths.
    /// </summary>
    public static string CleanPath(string path)
        => path.Replace("\u200B", string.Empty, StringComparison.Ordinal)
               .Replace("\u200C", string.Empty, StringComparison.Ordinal)
               .Replace("\u200D", string.Empty, StringComparison.Ordinal)
               .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
               .Trim();

    /// <summary>
    ///     Checks whether the given directory looks like a NetEase game installation.
    /// </summary>
    public static bool IsGameDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(directory, NeteaseGamesPaths.GameExecutableRelativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool TryGetInt64Flexible(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        value = 0;
        return false;
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static long ReadInt(string path)
    {
        string text = ReadText(path);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
    }
}
