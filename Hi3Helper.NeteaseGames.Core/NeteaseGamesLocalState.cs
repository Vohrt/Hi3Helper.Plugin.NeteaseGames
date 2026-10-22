using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;
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
    ///     <para>
    ///         <b>Version sources, in the order the official launcher uses them.</b>
    ///     </para>
    ///     <para>
    ///         The official launcher keeps its record in <c>patchmd5_windows64_normal.txt</c>: the file is a
    ///         verbatim copy of the remote <c>OB_patchmd5_*.txt</c> whose fourth line is the installed
    ///         <c>@private_res_version@</c>. Everything else in the installation (<c>gameversion</c>,
    ///         <c>Documents\versiondata.json</c>) is written by the <i>game client</i>, so it only advances when
    ///         the client itself ran - never when a launcher installed an update.
    ///     </para>
    ///     <para>
    ///         This is why the version is read from the sources the launcher maintains first and the client's
    ///         files last: reading a client file after a plugin update reported the previous version, so the
    ///         host kept offering the update it had just installed.
    ///     </para>
    /// </summary>
    /// <param name="gameDirectory">The root of the game installation.</param>
    public static NeteaseGamesLocalState Read(string gameDirectory)
    {
        string patchDirectory = Path.Combine(gameDirectory, NeteaseGamesPaths.PatchDirectoryRelativePath);
        string documentsDirectory = Path.Combine(gameDirectory, NeteaseGamesPaths.DocumentsRelativePath);

        long packageVersion = ReadInt(Path.Combine(patchDirectory, NeteaseGamesPaths.PackageVersionFileName));

        // Two different versions live side by side and they are NOT interchangeable:
        //
        //   pkgversion         6521279   the base package / client version (4th field of `gameversion`)
        //   patchmd5 line 4    6582435   the patch version (5th field of `gameversion`)
        //
        // The official launcher decides whether an update is needed by comparing the patch version against
        // @private_res_version@, so `patchmd5` is the primary source here. `pkgversion` is only a fallback for
        // installations that never ran the launcher, and it must not be preferred: after a launcher run it holds
        // 6521279 while the patch version is 6582435, which made the plugin report a permanent update.
        long patchVersion = 0;
        string engineVersion = ReadText(Path.Combine(gameDirectory, NeteaseGamesPaths.BuiltVersionRelativePath));
        string latestEngineVersion = ReadText(Path.Combine(patchDirectory, NeteaseGamesPaths.LatestEngineVersionFileName));
        string gameVersionToken = ReadText(Path.Combine(patchDirectory, NeteaseGamesPaths.GameVersionFileName));
        string shaderVersion = string.Empty;

        // The launcher's own record: a verbatim copy of the remote patchmd5, whose fourth line is the installed
        // @private_res_version@ and whose @metadata@ block repeats it.
        string patchMd5File = Path.Combine(patchDirectory, NeteaseGamesPaths.PatchMd5FileName);
        if (File.Exists(patchMd5File))
        {
            try
            {
                byte[] raw = File.ReadAllBytes(patchMd5File);

                // The launcher stores the file inside the HEX_ envelope; a copy taken straight from the content
                // host is plain text. Both shapes occur on disk.
                byte[] plain = NeteaseGamesCrypto.IsHexEnvelope(raw)
                    ? NeteaseGamesCrypto.DecryptHexEnvelope(raw)
                    : raw;

                string text = System.Text.Encoding.UTF8.GetString(plain);
                long recorded = ReadPatchMd5Version(text);
                if (recorded > 0)
                {
                    patchVersion = recorded;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
            {
                // A broken patchmd5 file must not prevent the rest of the state from being read.
            }
        }

        // `pkgversion` is the base package version, not the patch version, so it is only a last resort for the
        // patch version (an installation that has neither a patchmd5 nor versiondata).
        if (patchVersion <= 0 && packageVersion > 0)
        {
            patchVersion = packageVersion;
        }

        // versiondata.json is written by the game client, not by the plugin, so it goes stale as soon as an
        // update is installed without the client running. It only fills in what has no other source (the engine
        // and shader versions) and the version counters as a last resort.
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
            byte[] raw = File.ReadAllBytes(file);

            // The list is stored in the HEX_ envelope by the game, but the content host serves it as plain text
            // and the official launcher keeps whatever it downloaded. Both shapes have to be accepted, otherwise
            // a list that came from the content host looks like a missing one and every package counts as
            // changed.
            byte[] plain = NeteaseGamesCrypto.IsHexEnvelope(raw)
                ? NeteaseGamesCrypto.DecryptHexEnvelope(raw)
                : raw;

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

    /// <summary>
    ///     Reads the installed version from the decrypted text of a <c>patchmd5</c> file.
    ///     <para>
    ///         The file is a verbatim copy of the remote <c>OB_patchmd5_*.txt</c>, whose layout is four lines
    ///         followed by the JSON body:
    ///     </para>
    ///     <code>
    ///         20261022122731826          <- sync timestamp (17 digits)
    ///         16867045629764259494       <- patch list hash
    ///         0
    ///         6582435                    <- @private_res_version@ = the installed version
    ///         { "normal": { ... }, "@metadata@": { ... } }
    ///     </code>
    ///     <para>
    ///         The fourth line is what the official launcher compares against the remote manifest, so it is read
    ///         first; the <c>@private_res_version@</c> of the embedded metadata block is used as a fallback for
    ///         files that were written without the line prefix.
    ///     </para>
    /// </summary>
    /// <param name="text">The decrypted text of a patchmd5 file.</param>
    /// <returns>The installed version, or 0 when it cannot be read.</returns>
    public static long ReadPatchMd5Version(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        // The four header lines come before the JSON body.
        int bodyStart = text.IndexOf('{');
        string header = bodyStart >= 0 ? text[..bodyStart] : text;

        string[] lines = header.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length >= 4 &&
            long.TryParse(lines[3].Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out long version) &&
            version > 0)
        {
            return version;
        }

        // Fallback: the metadata block repeats the same value.
        if (bodyStart >= 0)
        {
            int index = text.IndexOf("\"@private_res_version@\"", bodyStart, StringComparison.Ordinal);
            if (index >= 0)
            {
                int colon = text.IndexOf(':', index);
                int end = text.IndexOfAny([',', '}', '\n'], colon + 1);
                if (colon > 0 && end > colon)
                {
                    string value = text[(colon + 1)..end].Trim().Trim('"');
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) &&
                        parsed > 0)
                    {
                        return parsed;
                    }
                }
            }
        }

        return 0;
    }

    /// <summary>
    ///     Writes the launcher's own version record after a successful update.
    ///     <para>
    ///         The official launcher stores <c>patchmd5_windows64_normal.txt</c> as a verbatim copy of the remote
    ///         <c>OB_patchmd5_*.txt</c>: four header lines (sync timestamp, patch list hash, <c>0</c>, installed
    ///         version) followed by the JSON body. Its fourth line is what the launcher compares against the
    ///         remote manifest, so a launcher that finds the previous value there believes the update never
    ///         happened - which is exactly the state a plugin update used to leave behind.
    ///     </para>
    ///     <para>
    ///         The record is only written when the caller supplies the manifest that was installed; without it
    ///         the file is left untouched rather than being replaced with a fabricated one.
    ///     </para>
    /// </summary>
    /// <param name="patchDirectory">The <c>LocalData\Patch</c> directory of the installation.</param>
    /// <param name="version">The version that was installed.</param>
    /// <param name="manifest">The remote manifest the installation was brought to (may be null).</param>
    /// <param name="gameDirectory">The root of the installation (used for the client's own files).</param>
    public static void WriteLauncherRecord(string patchDirectory, long version, string? manifest, string gameDirectory)
    {
        if (version <= 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(manifest))
        {
            try
            {
                string record = BuildLauncherRecord(manifest, version);
                string path = Path.Combine(patchDirectory, NeteaseGamesPaths.PatchMd5FileName);
                byte[] encrypted = NeteaseGamesCrypto.EncryptHexEnvelope(System.Text.Encoding.UTF8.GetBytes(record));
                File.WriteAllBytes(path, encrypted);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A failed record write must not fail the update itself: `pkgversion` is already committed and
                // the version reader falls back to it.
                SharedStatic.InstanceLogger?.LogWarning(
                    "[NeteaseGames] Could not refresh the launcher record: {Msg}", ex.Message);
            }
        }

        // The client's own files are refreshed as well so that the game, the official launcher and this plugin
        // all report the same version. They are best effort: the game rewrites them on its next start.
        RefreshClientVersionFiles(patchDirectory, version, manifest, gameDirectory);
    }

    /// <summary>
    ///     Rebuilds the launcher record with the installed version in its fourth line, keeping the rest of the
    ///     manifest intact.
    /// </summary>
    /// <param name="manifest">The remote manifest (decrypted text).</param>
    /// <param name="version">The installed version.</param>
    public static string BuildLauncherRecord(string manifest, long version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest);

        int bodyStart = manifest.IndexOf('{');
        string body = bodyStart >= 0 ? manifest[bodyStart..] : manifest;
        string header = bodyStart >= 0 ? manifest[..bodyStart] : string.Empty;

        // Keep the manifest's own hash line when it is present; only the sync timestamp and the version change.
        string hash = "0";
        string[] lines = header.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length >= 2)
        {
            hash = lines[1];
        }

        // A timestamp in the same shape the launcher writes (yyyyMMddHHmmssfff).
        string stamp = DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

        // The body carries the version twice: in @metadata@ and (for completeness) in the normal block. Both are
        // replaced so a reader that consults either field agrees.
        body = ReplaceJsonNumber(body, "\"@private_res_version@\"", version);
        body = ReplaceJsonNumber(body, "\"@private_patch_version@\"", version);
        body = ReplaceJsonNumber(body, "\"@private_script_version@\"", version);

        return $"{stamp}\n{hash}\n0\n{version}\n{body}";
    }

    /// <summary>Replaces the number of a JSON property, leaving the text untouched when it is absent.</summary>
    private static string ReplaceJsonNumber(string json, string property, long value)
    {
        int index = json.IndexOf(property, StringComparison.Ordinal);
        if (index < 0)
        {
            return json;
        }

        int colon = json.IndexOf(':', index);
        if (colon < 0)
        {
            return json;
        }

        int start = colon + 1;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
        {
            start++;
        }

        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-'))
        {
            end++;
        }

        if (end == start)
        {
            return json;
        }

        return string.Concat(json.AsSpan(0, start), value.ToString(CultureInfo.InvariantCulture), json.AsSpan(end));
    }

    /// <summary>
    ///     Refreshes the version files the game client maintains, so every consumer agrees on the version.
    /// </summary>
    /// <param name="patchDirectory">The <c>LocalData\Patch</c> directory.</param>
    /// <param name="version">The installed version.</param>
    /// <param name="manifest">The installed manifest (may be null).</param>
    /// <param name="gameDirectory">The root of the installation.</param>
    private static void RefreshClientVersionFiles(string patchDirectory, long version, string? manifest,
        string gameDirectory)
    {
        try
        {
            // `gameversion` is <channel>_<platform>_<hash>_<packageVersion>_<patchVersion>; only the last field
            // is ours to update.
            string gameVersionPath = Path.Combine(patchDirectory, NeteaseGamesPaths.GameVersionFileName);
            string gameVersion = ReadText(gameVersionPath);
            if (!string.IsNullOrEmpty(gameVersion))
            {
                int lastSeparator = gameVersion.LastIndexOf('_');
                if (lastSeparator > 0)
                {
                    string updated = string.Concat(gameVersion.AsSpan(0, lastSeparator + 1),
                        version.ToString(CultureInfo.InvariantCulture));
                    File.WriteAllText(gameVersionPath, updated);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only.
        }

        try
        {
            string versionDataPath = Path.Combine(gameDirectory, NeteaseGamesPaths.DocumentsRelativePath,
                "versiondata.json");
            if (!File.Exists(versionDataPath))
            {
                return;
            }

            string json = File.ReadAllText(versionDataPath);
            json = ReplaceJsonNumber(json, "\"package_version\"", version);
            json = ReplaceJsonString(json, "\"patch_version\"", version.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(versionDataPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }

    /// <summary>Replaces the string value of a JSON property, leaving the text untouched when it is absent.</summary>
    private static string ReplaceJsonString(string json, string property, string value)
    {
        int index = json.IndexOf(property, StringComparison.Ordinal);
        if (index < 0)
        {
            return json;
        }

        int colon = json.IndexOf(':', index);
        if (colon < 0)
        {
            return json;
        }

        int open = json.IndexOf('"', colon + 1);
        if (open < 0)
        {
            return json;
        }

        int close = json.IndexOf('"', open + 1);
        if (close < 0)
        {
            return json;
        }

        return string.Concat(json.AsSpan(0, open + 1), value, json.AsSpan(close));
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
