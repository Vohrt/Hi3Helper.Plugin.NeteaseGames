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
///         The official launcher replaces the changed patch packages, their package indexes and the loose
///         files that are excluded from the packages, and only then writes the new package version. The game
///         reads packages <b>through</b> the indexes, so an outdated index next to a replaced package makes
///         the game read misaligned data - the write order below matters:
///         <list type="number">
///             <item>download every changed object into the staging directory,</item>
///             <item>move the packages into place,</item>
///             <item>move the package indexes and the loose files into place,</item>
///             <item>write the package version (the commit point of the whole operation).</item>
///         </list>
///         The version file is never written when any object failed, so an interrupted or partial update can
///         never be mistaken for a finished one.
///     </para>
/// </summary>
public static class NeteaseGamesUpdateApplier
{
    /// <summary>
    ///     Applies a patch to an installation.
    /// </summary>
    /// <param name="httpClient">The client used for the content requests.</param>
    /// <param name="gameDirectory">The root of the installation.</param>
    /// <param name="plan">The plan that lists every object that has to be downloaded.</param>
    /// <param name="downloader">The downloader that performs the transfers.</param>
    /// <param name="progress">Reports the number of bytes that are already on disk.</param>
    /// <param name="token">Cancel token.</param>
    /// <returns>The result of the operation.</returns>
    public static async Task<NeteaseGamesApplyResult> ApplyAsync(
        HttpClient httpClient,
        string gameDirectory,
        NeteaseGamesDownloadPlan plan,
        NeteaseGamesDownloader downloader,
        Action<long>? progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentException.ThrowIfNullOrEmpty(gameDirectory);

        string patchDirectory = Path.Combine(gameDirectory, NeteaseGamesPaths.PatchDirectoryRelativePath);
        Directory.CreateDirectory(patchDirectory);

        SharedStatic.InstanceLogger?.LogInformation(
            "[NeteaseGames] Applying patch: {Packages} packages, {Indexes} indexes, {Loose} loose files, {Size} bytes",
            plan.PackageCount, plan.IndexCount, plan.LooseCount, plan.TotalSize);

        // Download everything first. A failure here leaves the installation untouched.
        foreach (NeteaseGamesDownloadItem item in plan.Items)
        {
            token.ThrowIfCancellationRequested();

            long start = plan.DownloadedSize;
            bool ok = await downloader
                .DownloadAsync(item, bytes => progress?.Invoke(start + bytes), token)
                .ConfigureAwait(false);

            if (!ok)
            {
                SharedStatic.InstanceLogger?.LogError(
                    "[NeteaseGames] Could not download '{Path}', the patch is cancelled", item.RelativePath);
                return NeteaseGamesApplyResult.DownloadFailed(item.RelativePath);
            }

            plan.ReportDownloaded(item.ExpectedSize);
        }

        // Packages first, then the indexes that describe them, then the loose files.
        foreach (NeteaseGamesDownloadItem item in plan.Items)
        {
            if (item.Kind != NeteaseGamesDownloadKind.Package)
            {
                continue;
            }

            try
            {
                MoveIntoPlace(item, patchDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SharedStatic.InstanceLogger?.LogError(
                    "[NeteaseGames] Could not replace the package '{Path}': {Msg}", item.RelativePath, ex.Message);
                return NeteaseGamesApplyResult.ApplyFailed(item.RelativePath);
            }
        }

        foreach (NeteaseGamesDownloadItem item in plan.Items)
        {
            if (item.Kind != NeteaseGamesDownloadKind.Index)
            {
                continue;
            }

            try
            {
                MoveIntoPlace(item, patchDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SharedStatic.InstanceLogger?.LogError(
                    "[NeteaseGames] Could not replace the index '{Path}': {Msg}", item.RelativePath, ex.Message);
                return NeteaseGamesApplyResult.ApplyFailed(item.RelativePath);
            }
        }

        foreach (NeteaseGamesDownloadItem item in plan.Items)
        {
            if (item.Kind != NeteaseGamesDownloadKind.LooseFile)
            {
                continue;
            }

            try
            {
                MoveIntoPlace(item, gameDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SharedStatic.InstanceLogger?.LogError(
                    "[NeteaseGames] Could not write the file '{Path}': {Msg}", item.RelativePath, ex.Message);
                return NeteaseGamesApplyResult.ApplyFailed(item.RelativePath);
            }
        }

        // The version file is the commit point: written only when every object is in place.
        if (plan.PackageVersion > 0)
        {
            try
            {
                WriteAtomic(Path.Combine(patchDirectory, NeteaseGamesPaths.PackageVersionFileName),
                    plan.PackageVersion.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SharedStatic.InstanceLogger?.LogError(
                    "[NeteaseGames] Could not write the package version: {Msg}", ex.Message);
                return NeteaseGamesApplyResult.ApplyFailed(NeteaseGamesPaths.PackageVersionFileName);
            }
        }

        plan.CleanStaging();
        SharedStatic.InstanceLogger?.LogInformation("[NeteaseGames] The patch was applied");
        return NeteaseGamesApplyResult.Success;
    }

    /// <summary>
    ///     Moves a staged object over its target, creating the parent directory first.
    /// </summary>
    /// <param name="item">The plan item.</param>
    /// <param name="root">The root the target path is resolved against (the patch directory or the game root).</param>
    private static void MoveIntoPlace(NeteaseGamesDownloadItem item, string root)
    {
        string target = item.ResolveTargetPath(root);
        string? directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(item.StagedPath))
        {
            throw new FileNotFoundException($"The staged file '{item.StagedPath}' is missing.", item.StagedPath);
        }

        File.Move(item.StagedPath, target, overwrite: true);
    }

    /// <summary>
    ///     Writes a small text file through a temporary file so that a crash cannot leave it half written.
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>The result of an <see cref="NeteaseGamesUpdateApplier.ApplyAsync" /> call.</summary>
public readonly record struct NeteaseGamesApplyResult(bool IsSuccess, string? FailedPath)
{
    /// <summary>Whether the patch was applied completely.</summary>
    public bool IsSuccess { get; } = IsSuccess;

    /// <summary>The object that failed, or <c>null</c> on success.</summary>
    public string? FailedPath { get; } = FailedPath;

    /// <summary>A successful result.</summary>
    public static NeteaseGamesApplyResult Success => new(true, null);

    /// <summary>A result that reports a failed download.</summary>
    public static NeteaseGamesApplyResult DownloadFailed(string path) => new(false, path);

    /// <summary>A result that reports a failed file replacement.</summary>
    public static NeteaseGamesApplyResult ApplyFailed(string path) => new(false, path);
}
