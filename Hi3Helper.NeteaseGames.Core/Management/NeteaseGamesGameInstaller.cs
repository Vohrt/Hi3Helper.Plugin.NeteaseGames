using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.NeteaseGames.Core.Management;

/// <summary>
///     Performs the install / update routine of a NetEase game.
///     <para>
///         NetEase distributes its payload in two shapes:
///         <list type="number">
///             <item>
///                 <b>Mini pack</b> - the remote manifest announces a set of content-addressed blocks plus a
///                 <c>.data</c> descriptor; those blocks are the incremental payload of an update.
///             </item>
///             <item>
///                 <b>Patch list</b> - the full manifest of the game (<c>common</c> loose files plus the
///                 <c>mpk_*</c> shards). Comparing the local and the remote list yields exactly the files and
///                 shards that changed, which is the file granular delta the official launcher applies as well.
///             </item>
///         </list>
///         Both plans are built here; the download stage resumes partial files and verifies the MD5 of every
///         block before it is accepted.
///     </para>
/// </summary>
[GeneratedComClass]
public partial class NeteaseGamesGameInstaller : GameInstallerBase
{
    /// <summary>
    ///     Name of the staging directory inside the patch folder.
    /// </summary>
    public const string StagingDirectoryName = ".collapse_download";

    private readonly NeteaseGamesGameManager? _manager;

    public NeteaseGamesGameInstaller(IGameManager gameManager) : base(gameManager)
    {
        _manager = gameManager as NeteaseGamesGameManager;
    }

    /// <inheritdoc/>
    protected override async Task<long> GetGameSizeAsyncInner(GameInstallerKind gameInstallerKind, CancellationToken token)
    {
        if ((gameInstallerKind & GameInstallerKind.Update) == GameInstallerKind.Update)
        {
            NeteaseGamesDownloadPlan? plan = await BuildUpdatePlanAsync(token).ConfigureAwait(false);
            return plan?.TotalSize ?? 0;
        }

        string gamePath = EnsureAndGetGamePath();
        if (Directory.Exists(gamePath))
        {
            return await Task.Run(() => NeteaseGamesGameManager.MeasureInstallSize(gamePath), token).ConfigureAwait(false);
        }

        NeteaseGamesMiniPackManifest? manifest = _manager?.RemoteManifest;
        return manifest?.TotalDownloadSize ?? 0;
    }

    /// <inheritdoc/>
    protected override async Task<long> GetGameDownloadedSizeAsyncInner(GameInstallerKind gameInstallerKind, CancellationToken token)
    {
        string gamePath = EnsureAndGetGamePath();
        string staging = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath, StagingDirectoryName);
        if (!Directory.Exists(staging))
        {
            return 0;
        }

        return await Task.Run(
            () =>
            {
                long total = 0;
                foreach (string file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception)
                    {
                        // Ignore vanished files.
                    }
                }

                return total;
            },
            token).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task StartInstallAsyncInner(
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token)
    {
        await RunDownloadAsync(progressDelegate, progressStateDelegate, token).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task StartUpdateAsyncInner(
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token)
    {
        await RunDownloadAsync(progressDelegate, progressStateDelegate, token).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override Task StartPreloadAsyncInner(
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token)
    {
        // NetEase does not offer a separate preload channel.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task UninstallAsyncInner(CancellationToken token)
    {
        string gamePath = EnsureAndGetGamePath();
        if (Directory.Exists(gamePath))
        {
            Directory.Delete(gamePath, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task RunDownloadAsync(
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token)
    {
        string gamePath = EnsureAndGetGamePath();
        string staging = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath, StagingDirectoryName);
        Directory.CreateDirectory(staging);

        progressStateDelegate?.Invoke(InstallProgressState.Preparing);

        NeteaseGamesDownloadPlan? plan = await BuildUpdatePlanAsync(token).ConfigureAwait(false);
        if (plan is null || plan.Items.Count == 0)
        {
            progressStateDelegate?.Invoke(InstallProgressState.Completed);
            return;
        }

        progressStateDelegate?.Invoke(InstallProgressState.Download);

        NeteaseGamesDownloader downloader = new(CreateHttpClient());
        long totalBytes = plan.TotalSize;
        long downloadedBytes = 0;
        int downloadedCount = 0;

        foreach (NeteaseGamesDownloadItem item in plan.Items)
        {
            token.ThrowIfCancellationRequested();

            long itemStart = downloadedBytes;
            bool ok = await downloader.DownloadAsync(
                item,
                bytes => ReportProgress(progressDelegate, itemStart + bytes, totalBytes, downloadedCount, plan.Items.Count),
                token).ConfigureAwait(false);

            if (!ok)
            {
                throw new IOException($"Failed to download '{item.RelativePath}'.");
            }

            downloadedBytes += item.ExpectedSize;
            downloadedCount++;
        }

        progressStateDelegate?.Invoke(InstallProgressState.Verify);
        progressStateDelegate?.Invoke(InstallProgressState.Completed);
    }

    private static void ReportProgress(
        InstallProgressDelegate? progressDelegate,
        long downloadedBytes,
        long totalBytes,
        int downloadedCount,
        int totalCount)
    {
        if (progressDelegate is null)
        {
            return;
        }

        InstallProgress progress = new()
        {
            DownloadedBytes = downloadedBytes,
            TotalBytesToDownload = totalBytes,
            DownloadedCount = downloadedCount,
            TotalCountToDownload = totalCount,
            StateCount = 1,
            TotalStateToComplete = 1
        };

        progressDelegate(in progress);
    }

    /// <summary>
    ///     Builds the list of files that have to be downloaded for the current installation.
    /// </summary>
    /// <param name="token">Cancel token.</param>
    public async Task<NeteaseGamesDownloadPlan?> BuildUpdatePlanAsync(CancellationToken token)
    {
        NeteaseGamesMiniPackManifest? manifest = _manager?.RemoteManifest;
        if (manifest is null || manifest.DownloadInfo.Count == 0)
        {
            return null;
        }

        string gamePath = EnsureAndGetGamePath();
        string staging = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath, StagingDirectoryName);

        NeteaseGamesDownloadPlan plan = new();

        // The descriptor of the mini pack describes which files the blocks expand to.
        if (!string.IsNullOrEmpty(manifest.DataName))
        {
            plan.Items.Add(new NeteaseGamesDownloadItem
            {
                RelativePath = CombineRelative(manifest.LooseFileParentDir, manifest.DataName),
                TargetPath = Path.Combine(staging, manifest.DataName),
                ExpectedSize = 0
            });
        }

        foreach ((string md5, long size) in manifest.DownloadInfo)
        {
            plan.Items.Add(new NeteaseGamesDownloadItem
            {
                RelativePath = CombineRelative(manifest.LooseFileParentDir, md5),
                ExpectedMd5 = md5,
                ExpectedSize = size,
                TargetPath = Path.Combine(staging, md5)
            });
        }

        await Task.CompletedTask.ConfigureAwait(false);
        SharedStatic.InstanceLogger?.LogInformation(
            "[NeteaseGames] Download plan: {Count} items, {Size} bytes", plan.Items.Count, plan.TotalSize);
        return plan;
    }

    /// <summary>
    ///     Builds a file granular delta plan out of two patch lists.
    /// </summary>
    /// <param name="local">The patch list of the local installation.</param>
    /// <param name="remote">The patch list of the remote installation.</param>
    /// <param name="stagingDirectory">The directory the changed files are staged in.</param>
    public static NeteaseGamesDownloadPlan BuildPatchListPlan(
        NeteaseGamesPatchList local,
        NeteaseGamesPatchList remote,
        string stagingDirectory)
    {
        NeteaseGamesDownloadPlan plan = new();

        foreach ((string path, NeteaseGamesFileEntry remoteEntry) in remote.LooseFiles)
        {
            if (local.LooseFiles.TryGetValue(path, out NeteaseGamesFileEntry localEntry) && localEntry.Hash == remoteEntry.Hash)
            {
                continue;
            }

            plan.Items.Add(new NeteaseGamesDownloadItem
            {
                RelativePath = path,
                ExpectedSize = remoteEntry.Size,
                TargetPath = Path.Combine(stagingDirectory, path.Replace('/', Path.DirectorySeparatorChar))
            });
        }

        foreach ((string name, NeteaseGamesPackageEntry remotePackage) in remote.Packages)
        {
            if (local.Packages.TryGetValue(name, out NeteaseGamesPackageEntry? localPackage) &&
                localPackage is not null &&
                HashesEqual(localPackage.Hashes, remotePackage.Hashes))
            {
                continue;
            }

            plan.Items.Add(new NeteaseGamesDownloadItem
            {
                RelativePath = name,
                ExpectedSize = remotePackage.TotalSize,
                TargetPath = Path.Combine(stagingDirectory, name)
            });
        }

        return plan;
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

    private static string CombineRelative(string parent, string name)
    {
        if (string.IsNullOrEmpty(parent))
        {
            return name;
        }

        return parent.TrimEnd('/') + "/" + name;
    }

    private static HttpClient CreateHttpClient()
    {
        PluginHttpClientBuilder builder = new PluginHttpClientBuilder()
            .SetAllowedDecompression(System.Net.DecompressionMethods.All)
            .AllowRedirections()
            .AllowUntrustedCert()
            .AllowCookies()
            .SetUserAgent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CollapsePlugin/1.0");

        return builder.Create();
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

