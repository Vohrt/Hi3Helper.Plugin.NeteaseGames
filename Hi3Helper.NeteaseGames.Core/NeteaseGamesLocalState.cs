using System;
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
    ///     Relative path of the file the game uses to detect that it was started by the official launcher.
    ///     <para>
    ///         The game opens <c>LocalData\Patch\ywzh.exe</c> while it boots. When the file is missing it shows
    ///         the dialog "请从启动器启动游戏。" (please start the game from the launcher) and refuses to continue.
    ///         The launcher stages a copy of the game executable there, so writing that copy is enough to make
    ///         the game accept a direct start. The content of the file is irrelevant - only its presence is
    ///         checked.
    ///     </para>
    /// </summary>
    public const string LauncherMarkerRelativePath = @"LocalData\Patch\ywzh.exe";

    /// <summary>
    ///     Relative path of the channel tag file the launcher writes into the patch directory.
    ///     <para>
    ///         The file holds the encrypted channel tag (Base64 + XOR + zlib, see
    ///         <see cref="NeteaseGamesCrypto.DecryptCpsTag" />).
    ///     </para>
    /// </summary>
    public const string CpsTagRelativePath = @"LocalData\Patch\CPS_TAG";

    /// <summary>
    ///     The name of the staged game executable the launcher downloads next to the running one.
    ///     <para>
    ///         The game executable hands the start over to <c>ywzh_.exe</c> (the freshly downloaded build) and
    ///         exits; the staged build is the one that keeps running.
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
    ///     The registry key the official launcher writes its paths into.
    /// </summary>
    public const string RegistryKeyName = @"Software\OB_ywzh_launcher_PC";

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
    ///     Whether the local installation is behind the announced patch version.
    /// </summary>
    public bool HasContentUpdate => PatchVersion > PackageVersion && PackageVersion > 0;

    /// <summary>
    ///     Reads the local state from a game installation directory.
    /// </summary>
    /// <param name="gameDirectory">The root of the game installation.</param>
    public static NeteaseGamesLocalState Read(string gameDirectory)
    {
        string patchDirectory = Path.Combine(gameDirectory, NeteaseGamesPaths.PatchDirectoryRelativePath);
        string documentsDirectory = Path.Combine(gameDirectory, NeteaseGamesPaths.DocumentsRelativePath);

        long packageVersion = ReadInt(Path.Combine(patchDirectory, NeteaseGamesPaths.PackageVersionFileName));
        long patchVersion = packageVersion;
        string engineVersion = ReadText(Path.Combine(gameDirectory, NeteaseGamesPaths.BuiltVersionRelativePath));
        string latestEngineVersion = ReadText(Path.Combine(patchDirectory, NeteaseGamesPaths.LatestEngineVersionFileName));
        string gameVersionToken = ReadText(Path.Combine(patchDirectory, NeteaseGamesPaths.GameVersionFileName));
        string shaderVersion = string.Empty;

        // The local patchmd5 file announces the newest known patch version.
        string patchMd5File = Path.Combine(patchDirectory, NeteaseGamesPaths.PatchMd5FileName);
        if (File.Exists(patchMd5File))
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
            catch (Exception)
            {
                // A broken patchmd5 file must not prevent the rest of the state from being read.
            }
        }

        // versiondata.json carries the same numbers in a machine readable form.
        string versionDataFile = Path.Combine(documentsDirectory, "versiondata.json");
        if (File.Exists(versionDataFile))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(versionDataFile));
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("package_version", out JsonElement packageValue) &&
                    TryGetInt64Flexible(packageValue, out long parsedPackage))
                {
                    packageVersion = parsedPackage;
                }

                if (root.TryGetProperty("patch_version", out JsonElement patchValue) &&
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
            catch (Exception)
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
    /// </summary>
    /// <param name="gameDirectory">The root of the game installation.</param>
    public static NeteaseGamesPatchList? ReadPatchList(string gameDirectory)
    {
        string file = Path.Combine(gameDirectory, NeteaseGamesPaths.PatchDirectoryRelativePath, NeteaseGamesPaths.PatchListFileName);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            byte[] plain = NeteaseGamesCrypto.DecryptHexEnvelope(File.ReadAllBytes(file));
            return NeteaseGamesPatchList.Parse(plain);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     Finds an existing installation by looking at the launcher registry key.
    ///     <para>
    ///         The official launcher stores the game directory in <c>AdditionalPaths</c> and its own directory
    ///         in <c>InstPath</c> - and the latter contains four U+200B zero width spaces, so it must never be
    ///         used as a path directly.
    ///     </para>
    /// </summary>
    /// <returns>The game directory, or <c>null</c> when nothing was found.</returns>
    public static string? FindExistingInstallPath()
    {
        foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using RegistryKey? key = hive.OpenSubKey(NeteaseGamesPaths.RegistryKeyName);
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
                    if (parent is not null)
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
            catch (Exception)
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
        catch (Exception)
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
        catch (Exception)
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
