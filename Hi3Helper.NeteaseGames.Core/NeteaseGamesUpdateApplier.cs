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
///             <item>write the package version (only when the base tier changed) and then the launcher's record
///                 with the new <b>patch</b> version - the commit point of the whole operation.</item>
///         </list>
///         The version files are never written when any object failed, so an interrupted or partial update can
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
    /// <param name="completed">Called once per object that finished downloading.</param>
    /// <param name="token">Cancel token.</param>
    /// <returns>The result of the operation.</returns>
    public static async Task<NeteaseGamesApplyResult> ApplyAsync(
        HttpClient httpClient,
        string gameDirectory,
        NeteaseGamesDownloadPlan plan,
        NeteaseGamesDownloader downloader,
        Action<long>? progress,
        CancellationToken token,
        Action? completed = null)
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

        // Download everything first. A failure here leaves the installation untouched. The transfers are
        // overlapped: a fresh installation is tens of gigabytes and a serial loop would take much longer.
        IReadOnlyList<NeteaseGamesDownloadItem> failed = await downloader
            .DownloadAllAsync(plan.Items, NeteaseGamesDownloader.DefaultParallelism, progress, token, completed)
            .ConfigureAwait(false);

        if (failed.Count > 0)
        {
            foreach (NeteaseGamesDownloadItem item in failed)
            {
                SharedStatic.InstanceLogger?.LogError(
                    "[NeteaseGames] Could not download '{Path}', the patch is cancelled", item.RelativePath);
            }

            return NeteaseGamesApplyResult.DownloadFailed(failed[0].RelativePath);
        }

        foreach (NeteaseGamesDownloadItem item in plan.Items)
        {
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

        // The base tier's standalone files go into the patch directory, next to the shards.
        foreach (NeteaseGamesDownloadItem item in plan.Items)
        {
            if (item.Kind != NeteaseGamesDownloadKind.BaseFile)
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
                    "[NeteaseGames] Could not write the base file '{Path}': {Msg}", item.RelativePath, ex.Message);
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

        // Side files (the shader cache list) live next to the packages but are not described by the patch list.
        // They are written after the payload and before the version record, so an interrupted run never leaves a
        // new version next to a side file of the previous one.
        foreach (NeteaseGamesDownloadItem item in plan.Items)
        {
            if (item.Kind != NeteaseGamesDownloadKind.SideFile)
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
                    "[NeteaseGames] Could not write the side file '{Path}': {Msg}", item.RelativePath, ex.Message);
                return NeteaseGamesApplyResult.ApplyFailed(item.RelativePath);
            }
        }

        // The version record is the commit point: written only when every object is in place.
        //
        // The two counters are written separately on purpose:
        //   pkgversion   the BASE PACKAGE version - only a round that rewrote the base tier may advance it
        //   patchmd5     the official launcher's record - its fourth line is the PATCH version
        // An earlier revision fed the patch version into both, which overwrote 6,521,279 with 6,593,596 and
        // corrupted the launcher's bookkeeping even though Collapse (which reads patchmd5) saw nothing wrong.
        // `pkgversion` is the BASE PACKAGE version and only a round that genuinely advances it may write it. No
        // manifest the plugin can read announces that counter, so the plan leaves it 0 and this is a no-op - which
        // is the point: overwriting a real vendor value with the patch version (what an earlier revision did:
        // 6,521,279 -> 6,593,596) breaks the official launcher's bookkeeping.
        if (plan.PackageVersion > 0)
        {
            NeteaseGamesLocalState.WritePackageVersionIfKnown(patchDirectory, plan.PackageVersion);
        }

        if (plan.PatchVersion > 0)
        {
            // The official launcher does not look at `pkgversion`: its record is `patchmd5_*.txt`, a verbatim
            // copy of the remote OB_patchmd5 whose fourth line carries the installed @private_res_version@.
            // Leaving that file at the previous version made the launcher (and our own reader) believe the update
            // had not happened. It is rewritten from the manifest that was just installed.
            NeteaseGamesLocalState.WriteLauncherRecord(patchDirectory, plan.PatchVersion, plan.LauncherRecord,
                gameDirectory);
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
