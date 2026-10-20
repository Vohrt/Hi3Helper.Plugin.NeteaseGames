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
///                 <b>Engine payload (mini pack)</b> - a list of loose files below
///                 <c>/loose_file/mini_pack_&lt;version&gt;/</c> that carries the engine binaries.
///             </item>
///             <item>
///                 <b>Patch list</b> - the full manifest of the game: the loose files of the
///                 <c>common</c>/<c>windows64_common</c> sections plus the <c>mpk_*</c> shards and their indexes.
///                 Comparing the local and the remote list yields exactly the objects that changed, which is
///                 the file granular delta the official launcher applies as well.
///             </item>
///         </list>
///         Both parts are turned into a single <see cref="NeteaseGamesDownloadPlan" />, so the size that the
///         host asks for and the bytes that are reported during the routine always describe the same work.
///     </para>
/// </summary>
[GeneratedComClass]
public partial class NeteaseGamesGameInstaller : GameInstallerBase
{
    /// <summary>
    ///     Name of the staging directory inside the patch folder. Every object of a plan is downloaded here
    ///     first, so a failed update never touches the installation.
    /// </summary>
    public const string StagingDirectoryName = ".collapse_download";

    private readonly NeteaseGamesGameManager? _manager;
    private readonly HttpClient _httpClient;
    private NeteaseGamesDownloadPlan? _cachedPlan;

    /// <summary>
    ///     Creates an installer.
    /// </summary>
    /// <param name="gameManager">The game manager of the same preset.</param>
    public NeteaseGamesGameInstaller(IGameManager gameManager) : base(gameManager)
    {
        _manager = gameManager as NeteaseGamesGameManager;
        _httpClient = NeteaseGamesHttp.CreateClient();
    }

    /// <inheritdoc/>
    protected override async Task<long> GetGameSizeAsyncInner(GameInstallerKind gameInstallerKind, CancellationToken token)
    {
        // The host compares this value with GetGameDownloadedSizeAsync to compute the remaining bytes and the
        // disk space requirement, so both have to describe the same plan.
        if (gameInstallerKind is GameInstallerKind.Install or GameInstallerKind.Update)
        {
            NeteaseGamesDownloadPlan plan = await GetOrBuildPlanAsync(token).ConfigureAwait(false);
            return plan.TotalSize;
        }

        // Preload is not used by this game (there is no separate preload channel).
        return 0;
    }

    /// <inheritdoc/>
    protected override async Task<long> GetGameDownloadedSizeAsyncInner(GameInstallerKind gameInstallerKind, CancellationToken token)
    {
        if (gameInstallerKind is GameInstallerKind.Install or GameInstallerKind.Update)
        {
            NeteaseGamesDownloadPlan plan = await GetOrBuildPlanAsync(token).ConfigureAwait(false);
            return plan.DownloadedSize;
        }

        return 0;
    }

    /// <inheritdoc/>
    protected override async Task StartInstallAsyncInner(
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token)
    {
        await RunAsync(progressDelegate, progressStateDelegate, token).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task StartUpdateAsyncInner(
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token)
    {
        await RunAsync(progressDelegate, progressStateDelegate, token).ConfigureAwait(false);
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

        // The installation root has to be the directory the game lives in: deleting a parent directory
        // (which may contain the official launcher) is never acceptable.
        if (!NeteaseGamesLocalState.IsGameDirectory(gamePath))
        {
            throw new InvalidOperationException(
                $"'{gamePath}' is not a NetEase game installation, refusing to delete it.");
        }

        Directory.Delete(gamePath, recursive: true);
        return Task.CompletedTask;
    }

    private async Task RunAsync(
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token)
    {
        string gamePath = EnsureAndGetGamePath();
        progressStateDelegate?.Invoke(InstallProgressState.Preparing);

        NeteaseGamesGameManager? manager = _manager;
        if (manager is null)
        {
            throw new InvalidOperationException("The installer needs the NetEase game manager of its preset.");
        }

        // The remote manifests may not have been fetched yet when the host calls straight into the installer.
        await manager.ProbeRemoteVersionAsync(token).ConfigureAwait(false);

        NeteaseGamesDownloadPlan plan = await GetOrBuildPlanAsync(token).ConfigureAwait(false);
        if (plan.IsEmpty)
        {
            SharedStatic.InstanceLogger?.LogInformation("[NeteaseGames] Nothing to download, the game is up to date");
            progressStateDelegate?.Invoke(InstallProgressState.Completed);
            return;
        }

        progressStateDelegate?.Invoke(InstallProgressState.Download);

        long totalBytes = plan.TotalSize;
        int totalFiles = plan.Items.Count;
        int completedFiles = 0;

        // Report the initial state right away so the host does not show a frozen "0 / N" badge.
        ReportProgress(progressDelegate, plan.DownloadedSize, totalBytes, completedFiles, totalFiles);

        NeteaseGamesDownloader downloader = new(_httpClient);
        NeteaseGamesApplyResult result = await NeteaseGamesUpdateApplier.ApplyAsync(
            _httpClient,
            gamePath,
            plan,
            downloader,
            bytes => ReportProgress(progressDelegate, bytes, totalBytes, Volatile.Read(ref completedFiles), totalFiles),
            token).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new IOException(
                $"The patch could not be applied ('{result.FailedPath}'). The installation was left untouched.");
        }

        progressStateDelegate?.Invoke(InstallProgressState.Verify);

        // The local version is derived from the files on disk, so it has to be re-read after the update.
        manager.SetGamePath(gamePath);
        manager.EnsureLocalVersionProbed();

        completedFiles = totalFiles;
        ReportProgress(progressDelegate, totalBytes, totalBytes, completedFiles, totalFiles);
        progressStateDelegate?.Invoke(InstallProgressState.Completed);
    }

    /// <summary>
    ///     Builds the plan of the current installation, or returns the plan that was built before.
    /// </summary>
    /// <param name="token">Cancel token.</param>
    private async Task<NeteaseGamesDownloadPlan> GetOrBuildPlanAsync(CancellationToken token)
    {
        string gamePath = EnsureAndGetGamePath();

        NeteaseGamesDownloadPlan? cached = _cachedPlan;
        if (cached is not null && _cachedPlanGamePath == gamePath)
        {
            return cached;
        }

        NeteaseGamesDownloadPlan plan = await BuildPlanAsync(gamePath, token).ConfigureAwait(false);
        _cachedPlan = plan;
        _cachedPlanGamePath = gamePath;
        return plan;
    }

    private string? _cachedPlanGamePath;

    /// <summary>
    ///     Invalidates the cached plan (called when the installation changed).
    /// </summary>
    public void InvalidatePlan() => _cachedPlan = null;

    /// <summary>
    ///     Builds the plan of an installation.
    /// </summary>
    /// <param name="gamePath">The installation root.</param>
    /// <param name="token">Cancel token.</param>
    public async Task<NeteaseGamesDownloadPlan> BuildPlanAsync(string gamePath, CancellationToken token)
    {
        NeteaseGamesGameManager? manager = _manager;
        NeteaseGamesDownloadPlan empty = new()
        {
            StagingDirectory = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath, StagingDirectoryName)
        };

        if (manager is null)
        {
            return empty;
        }

        string stagingDirectory = empty.StagingDirectory;
        NeteaseGamesDownloadPlan plan = new()
        {
            StagingDirectory = stagingDirectory,
            PackageVersion = manager.RemotePackageVersion
        };

        // 1. The engine payload (mini pack): loose files below /loose_file/mini_pack_<version>/.
        NeteaseGamesMiniPackManifest? miniPack = manager.RemoteManifest;
        bool engineMissing = !File.Exists(Path.Combine(gamePath, NeteaseGamesPaths.GameExecutableRelativePath));
        if (miniPack is not null && !string.IsNullOrEmpty(miniPack.LooseFileParentDir) &&
            (engineMissing || manager.HasEngineUpdate))
        {
            foreach (NeteaseGamesEngineFile file in await manager
                         .FetchEnginePayloadFileListAsync(miniPack.LooseFileParentDir, token)
                         .ConfigureAwait(false))
            {
                if (NeteaseGamesEnginePatcher.IsDeleteMarker(file))
                {
                    continue;
                }

                string target = file.TargetName.Replace('\\', '/');
                string localPath = Path.Combine(gamePath, NeteaseGamesEnginePatcher.EngineDirectoryName,
                    file.TargetName.Replace('\\', Path.DirectorySeparatorChar));

                if (File.Exists(localPath) && new FileInfo(localPath).Length == file.Size)
                {
                    continue;
                }

                plan.Items.Add(new NeteaseGamesDownloadItem
                {
                    RelativePath = $"{miniPack.LooseFileParentDir.TrimEnd('/')}/{file.UrlPath}".TrimStart('/'),
                    TargetRelativePath = $"{NeteaseGamesEnginePatcher.EngineDirectoryName}/{target}",
                    Kind = NeteaseGamesDownloadKind.LooseFile,
                    ExpectedSize = file.Size
                });
            }
        }

        // 2. The patch list delta: changed loose files, changed packages and the indexes that describe them.
        NeteaseGamesPatchManifest? manifest = manager.RemotePatchManifest;
        if (manifest is not null && !string.IsNullOrEmpty(manifest.BaseVersion))
        {
            NeteaseGamesPatchList local = NeteaseGamesLocalState.ReadPatchList(gamePath) ?? new NeteaseGamesPatchList();
            NeteaseGamesPatchList remote = await manager
                .FetchRemotePatchListAsync(manifest.BaseVersion, token)
                .ConfigureAwait(false);

            foreach ((string path, NeteaseGamesFileEntry remoteEntry) in remote.LooseFiles)
            {
                if (local.LooseFiles.TryGetValue(path, out NeteaseGamesFileEntry localEntry) &&
                    localEntry.Hash == remoteEntry.Hash)
                {
                    continue;
                }

                plan.Items.Add(new NeteaseGamesDownloadItem
                {
                    RelativePath = ContentPath(manifest.BaseVersion, null, path),
                    TargetRelativePath = path.Replace('/', Path.DirectorySeparatorChar),
                    Kind = NeteaseGamesDownloadKind.LooseFile,
                    ExpectedSize = remoteEntry.Size
                });
            }

            foreach ((string name, NeteaseGamesPackageEntry remotePackage) in remote.Packages)
            {
                bool changed = !local.Packages.TryGetValue(name, out NeteaseGamesPackageEntry? localPackage) ||
                               localPackage is null ||
                               !NeteaseGamesPatchList.HashesEqual(localPackage.Hashes, remotePackage.Hashes);

                if (changed)
                {
                    plan.Items.Add(new NeteaseGamesDownloadItem
                    {
                        RelativePath = ContentPath(manifest.BaseVersion, remotePackage.Section, name),
                        TargetRelativePath = name,
                        Kind = IsPackageIndex(name) ? NeteaseGamesDownloadKind.Index : NeteaseGamesDownloadKind.Package,
                        ExpectedSize = remotePackage.TotalSize
                    });
                }
            }
        }

        plan.AssignStaging(stagingDirectory);

        SharedStatic.InstanceLogger?.LogInformation(
            "[NeteaseGames] Plan: {Packages} packages, {Indexes} indexes, {Loose} loose files, {Size} bytes",
            plan.PackageCount, plan.IndexCount, plan.LooseCount, plan.TotalSize);

        return plan;
    }

    /// <summary>
    ///     Whether a package name is a package index (<c>*.mpkinfo</c>) instead of a shard.
    /// </summary>
    public static bool IsPackageIndex(string name)
        => name.EndsWith(".mpkinfo", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Builds the content path of an object below the version directory.
    /// </summary>
    private static string ContentPath(string version, string? section, string name)
        => string.IsNullOrEmpty(section) ? $"{version}/{name}" : $"{version}/{section}/{name}";

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
            StateCount = downloadedCount,
            TotalStateToComplete = totalCount
        };

        progressDelegate(in progress);
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
