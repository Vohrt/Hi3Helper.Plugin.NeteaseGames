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

/// <summary>What a download plan is built for.</summary>
public enum NeteaseGamesPlanMode
{
    /// <summary>Only the objects that changed since the local patch list was written.</summary>
    Update,

    /// <summary>Every object of the game, for a directory that does not contain a game yet.</summary>
    Install,

    /// <summary>Every object that is missing or truncated on disk, ignoring the local patch list.</summary>
    Repair
}

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
            NeteaseGamesPlanMode mode = gameInstallerKind == GameInstallerKind.Install
                ? ResolveInstallMode()
                : NeteaseGamesPlanMode.Update;

            NeteaseGamesDownloadPlan plan = mode == NeteaseGamesPlanMode.Update
                ? await GetOrBuildPlanAsync(token).ConfigureAwait(false)
                : await BuildPlanAsync(EnsureAndGetGamePath(), mode, token).ConfigureAwait(false);

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
            NeteaseGamesPlanMode mode = gameInstallerKind == GameInstallerKind.Install
                ? ResolveInstallMode()
                : NeteaseGamesPlanMode.Update;

            NeteaseGamesDownloadPlan plan = mode == NeteaseGamesPlanMode.Update
                ? await GetOrBuildPlanAsync(token).ConfigureAwait(false)
                : await BuildPlanAsync(EnsureAndGetGamePath(), mode, token).ConfigureAwait(false);

            return plan.DownloadedSize;
        }

        return 0;
    }

    /// <summary>
    ///     Decides what an "install" request means for the current installation: a directory without a game is
    ///     a fresh installation, an installed game that is asked to install again is a repair.
    /// </summary>
    private NeteaseGamesPlanMode ResolveInstallMode()
    {
        string gamePath = EnsureAndGetGamePath();
        return NeteaseGamesLocalState.IsGameDirectory(gamePath)
            ? NeteaseGamesPlanMode.Repair
            : NeteaseGamesPlanMode.Install;
    }

    /// <inheritdoc/>
    protected override async Task StartInstallAsyncInner(
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token)
    {
        await RunAsync(progressDelegate, progressStateDelegate, ResolveInstallMode(), token).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task StartUpdateAsyncInner(
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token)
    {
        await RunAsync(progressDelegate, progressStateDelegate, NeteaseGamesPlanMode.Update, token).ConfigureAwait(false);
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
        NeteaseGamesPlanMode mode,
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

        // A repair verifies the files on disk against the manifest, so it must not reuse a delta plan.
        NeteaseGamesDownloadPlan plan = mode == NeteaseGamesPlanMode.Update
            ? await GetOrBuildPlanAsync(token).ConfigureAwait(false)
            : await BuildPlanAsync(gamePath, mode, token).ConfigureAwait(false);

        if (plan.IsEmpty)
        {
            SharedStatic.InstanceLogger?.LogInformation(
                "[NeteaseGames] Nothing to download ({Mode}), the installation is complete", mode);
            progressStateDelegate?.Invoke(InstallProgressState.Completed);
            return;
        }

        progressStateDelegate?.Invoke(InstallProgressState.Download);

        long totalBytes = plan.TotalSize;
        int totalFiles = plan.Items.Count;

        // Report the initial state right away so the host does not show a frozen "0 / N" badge.
        ReportProgress(progressDelegate, plan.DownloadedSize, totalBytes, 0, totalFiles);

        NeteaseGamesDownloader downloader = new(_httpClient);
        NeteaseGamesApplyResult result = await NeteaseGamesUpdateApplier.ApplyAsync(
            _httpClient,
            gamePath,
            plan,
            downloader,
            bytes => ReportProgress(progressDelegate, bytes, totalBytes, 0, totalFiles),
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
        InvalidatePlan();

        ReportProgress(progressDelegate, totalBytes, totalBytes, totalFiles, totalFiles);
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

        NeteaseGamesDownloadPlan plan = await BuildPlanAsync(gamePath, NeteaseGamesPlanMode.Update, token).ConfigureAwait(false);
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
    /// <param name="mode">What the plan is built for.</param>
    /// <param name="token">Cancel token.</param>
    public async Task<NeteaseGamesDownloadPlan> BuildPlanAsync(string gamePath, NeteaseGamesPlanMode mode,
        CancellationToken token)
    {
        NeteaseGamesGameManager? manager = _manager;
        string stagingDirectory = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath, StagingDirectoryName);

        if (manager is null)
        {
            return new NeteaseGamesDownloadPlan { StagingDirectory = stagingDirectory };
        }

        NeteaseGamesDownloadPlan plan = new()
        {
            StagingDirectory = stagingDirectory,
            PackageVersion = manager.RemotePackageVersion,
            // The launcher's own record is rebuilt from this manifest once the update succeeded.
            LauncherRecord = manager.RemotePatchManifestText
        };

        // A fresh install has nothing to compare against; an update and a repair both use the local patch list
        // as the record of what the installation should contain.
        bool isFreshInstall = mode == NeteaseGamesPlanMode.Install;
        bool verifyOnDisk = mode == NeteaseGamesPlanMode.Repair;

        // 1. The engine payload (mini pack): loose files below /loose_file/mini_pack_<version>/.
        NeteaseGamesMiniPackManifest? miniPack = manager.RemoteManifest;
        bool engineMissing = !File.Exists(Path.Combine(gamePath, NeteaseGamesPaths.GameExecutableRelativePath));

        // A repair checks the engine against what is on disk; it does not chase a newer engine version, which
        // is the update's job (the engine payload of a newer version would otherwise replace a healthy one).
        bool checkEnginePayload = isFreshInstall || engineMissing ||
                                  (mode == NeteaseGamesPlanMode.Update && manager.HasEngineUpdate);

        if (miniPack is not null && !string.IsNullOrEmpty(miniPack.LooseFileParentDir) && checkEnginePayload)
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

                // Engine files are verified by size (the vendor hash algorithm is not known), so the same check
                // serves the update, the repair and the fresh install.
                if (!engineMissing && IsLocalFileComplete(localPath, file.Size, verifyOnDisk))
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

        // 2. The patch packages and the indexes that describe them.
        NeteaseGamesPatchManifest? manifest = manager.RemotePatchManifest;
        if (manifest is not null && !string.IsNullOrEmpty(manifest.BaseVersion))
        {
            // The local patch list is the only record of what the installation is supposed to contain, so a
            // repair uses it (together with the files on disk) instead of throwing it away.
            NeteaseGamesPatchList local = NeteaseGamesLocalState.ReadPatchList(gamePath) ?? new NeteaseGamesPatchList();
            bool hasLocalList = local.Packages.Count > 0;

            NeteaseGamesPatchList remote = await manager
                .FetchRemotePatchListAsync(manifest.BaseVersion, token)
                .ConfigureAwait(false);

            // The loose file section of the patch list is NOT a set of files on disk: those entries
            // (Char/…, Sounds/…, VA/… - 231k of them) are virtual engine paths that are stored inside the mpk
            // packages. Verified on a live installation: none of them exists as a file, and 186 .mpk shards
            // hold the content. Downloading them individually would fetch the whole game as loose files, so
            // only the packages (and their indexes) are handled here.
            if (remote.LooseFiles.Count > 0)
            {
                SharedStatic.InstanceLogger?.LogDebug(
                    "[NeteaseGames] The patch list lists {Count} virtual resource paths; they are stored inside the packages",
                    remote.LooseFiles.Count);
            }

            foreach ((string name, NeteaseGamesPackageEntry remotePackage) in remote.Packages)
            {
                bool upToDate = hasLocalList &&
                                local.Packages.TryGetValue(name, out NeteaseGamesPackageEntry? localPackage) &&
                                localPackage is not null &&
                                NeteaseGamesPatchList.HashesEqual(localPackage.Hashes, remotePackage.Hashes);

                if (mode == NeteaseGamesPlanMode.Repair)
                {
                    // A repair restores the recorded state; it does not upgrade. A package the local list calls
                    // current is only checked for presence and size, and one it does not know is the update's
                    // business.
                    if (hasLocalList && !local.Packages.ContainsKey(name))
                    {
                        continue;
                    }

                    string knownPath = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath, name);
                    if (IsLocalFileComplete(knownPath, remotePackage.TotalSize, verifyHash: false))
                    {
                        continue;
                    }
                }
                else if (mode == NeteaseGamesPlanMode.Update && upToDate)
                {
                    continue;
                }

                plan.Items.Add(new NeteaseGamesDownloadItem
                {
                    RelativePath = ContentPath(manifest.BaseVersion, remotePackage.Section, name),
                    TargetRelativePath = name,
                    Kind = IsPackageIndex(name) ? NeteaseGamesDownloadKind.Index : NeteaseGamesDownloadKind.Package,
                    ExpectedSize = remotePackage.TotalSize
                });
            }
        }

        plan.AssignStaging(stagingDirectory);

        SharedStatic.InstanceLogger?.LogInformation(
            "[NeteaseGames] Plan ({Mode}): {Packages} packages, {Indexes} indexes, {Loose} loose files, {Size} bytes",
            mode, plan.PackageCount, plan.IndexCount, plan.LooseCount, plan.TotalSize);

        return plan;
    }

    /// <summary>
    ///     Whether a local file is already the expected one. Only the size can be checked (the vendor hash
    ///     algorithm of the content is not known), which is enough to tell a complete file from a truncated or
    ///     missing one.
    /// </summary>
    /// <param name="path">The local file.</param>
    /// <param name="expectedSize">The size the manifest announces.</param>
    /// <param name="verifyHash">Reserved: the vendor hash is not implemented.</param>
    public static bool IsLocalFileComplete(string path, long expectedSize, bool verifyHash)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (expectedSize <= 0)
            {
                // Nothing to compare against: treat an existing file as complete.
                return true;
            }

            return new FileInfo(path).Length == expectedSize;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
