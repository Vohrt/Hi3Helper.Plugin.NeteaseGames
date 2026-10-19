using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.NeteaseGames.Core.Management;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     Applies a patch to an installation of the game.
///     <para>
///         The official launcher replaces the changed patch packages and the package indexes in the patch
///         directory and then writes the new package version. The game reads the packages through the
///         indexes, so the content of a package never has to be unpacked.
///     </para>
/// </summary>
public static class NeteaseGamesUpdateApplier
{
    /// <summary>
    ///     Downloads the changed packages and indexes and installs them into the patch directory.
    /// </summary>
    /// <param name="httpClient">The client used for the content requests.</param>
    /// <param name="gameDirectory">The root of the installation.</param>
    /// <param name="local">The patch list of the local installation, or <c>null</c> for a fresh installation.</param>
    /// <param name="remote">The patch list of the remote installation.</param>
    /// <param name="baseVersion">The base version the content is stored under.</param>
    /// <param name="packageVersion">The package version the installation is updated to.</param>
    /// <param name="progress">An optional progress callback.</param>
    /// <param name="token">Cancel token.</param>
    /// <returns><c>true</c> when every package was installed.</returns>
    public static async Task<bool> ApplyAsync(
        HttpClient httpClient,
        string gameDirectory,
        NeteaseGamesPatchList? local,
        NeteaseGamesPatchList remote,
        string baseVersion,
        int packageVersion,
        Action<long>? progress,
        CancellationToken token)
    {
        string patchDirectory = Path.Combine(gameDirectory, NeteaseGamesPaths.PatchDirectoryRelativePath);
        string staging = Path.Combine(patchDirectory, "staging");
        Directory.CreateDirectory(staging);

        NeteaseGamesDownloader downloader = new(httpClient);

        List<(string Name, string Section, long Size)> changed = [];
        foreach ((string name, NeteaseGamesPackageEntry package) in remote.Packages)
        {
            if (local is not null &&
                local.Packages.TryGetValue(name, out NeteaseGamesPackageEntry? localPackage) &&
                localPackage is not null &&
                HashesEqual(localPackage.Hashes, package.Hashes))
            {
                continue;
            }

            changed.Add((name, package.Section, package.TotalSize));
        }

        long total = 0;
        foreach ((_, _, long size) in changed)
        {
            total += size;
        }

        SharedStatic.InstanceLogger?.LogInformation(
            "[NeteaseGames] Applying patch: {Count} packages ({Size} bytes), base {Base}, package version {Version}",
            changed.Count, total, baseVersion, packageVersion);

        long done = 0;
        foreach ((string name, string section, long size) in changed)
        {
            token.ThrowIfCancellationRequested();

            string relative = string.IsNullOrEmpty(section)
                ? $"{baseVersion}/{name}"
                : $"{baseVersion}/{section}/{name}";

            NeteaseGamesDownloadItem item = new()
            {
                RelativePath = relative,
                ExpectedSize = size,
                TargetPath = Path.Combine(staging, name)
            };

            long start = done;
            bool ok = await downloader.DownloadAsync(
                item,
                bytes => progress?.Invoke(start + bytes),
                token).ConfigureAwait(false);

            if (!ok)
            {
                SharedStatic.InstanceLogger?.LogWarning(
                    "[NeteaseGames] Could not download the package '{Name}'", name);
                return false;
            }

            // The package replaces the old one inside the patch directory.
            string target = Path.Combine(patchDirectory, name);
            File.Move(item.TargetPath, target, overwrite: true);
            done += size;
        }

        // The files that are not part of a package (the "mpkexclude" list) are delivered one by one. Their
        // location on the content host is not known yet, so they are reported instead of being ignored.
        int loose = CountChangedLooseFiles(local, remote);
        if (loose > 0)
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGames] {Count} loose files changed but their download location is not known yet",
                loose);
        }

        if (packageVersion > 0)
        {
            string versionFile = Path.Combine(patchDirectory, NeteaseGamesPaths.PackageVersionFileName);
            await File.WriteAllTextAsync(versionFile, packageVersion.ToString(), token).ConfigureAwait(false);
        }

        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception)
        {
            // A leftover staging directory is not fatal.
        }

        return true;
    }

    /// <summary>
    ///     Counts the files that are not part of a package and whose content changed.
    /// </summary>
    private static int CountChangedLooseFiles(NeteaseGamesPatchList? local, NeteaseGamesPatchList remote)
    {
        if (local is null)
        {
            return 0;
        }

        int count = 0;
        foreach ((string path, NeteaseGamesFileEntry entry) in remote.LooseFiles)
        {
            if (!local.LooseFiles.TryGetValue(path, out NeteaseGamesFileEntry localEntry) || localEntry.Hash != entry.Hash)
            {
                count++;
            }
        }

        return count;
    }

    private static bool HashesEqual(List<ulong> left, List<ulong> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }
}
