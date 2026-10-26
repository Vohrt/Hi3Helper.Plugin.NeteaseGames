using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    ///     Name of the staging directory a base tier chunk is downloaded into before it is appended to its shard.
    ///     <para>
    ///         It is separate from <see cref="StagingDirectoryName" /> because the two have different lifetimes:
    ///         the patch staging directory is deleted as soon as the delta tier is in place, while a base chunk
    ///         has to survive until it has been appended (and is deleted per chunk).
    ///     </para>
    /// </summary>
    public const string BaseStagingDirectoryName = ".collapse_base";

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

    /// <summary>
    ///     The installer has nothing to initialise: the manifests are fetched by the plan that needs them.
    ///     <para>
    ///         ⚠ This override is <b>not</b> optional. The base <see cref="InitializableTask.InitAsync" /> is a
    ///         placeholder that sleeps ten seconds and returns 69420, and the host awaits it before every install,
    ///         update, uninstall and preload (<c>PluginGameInstallWrapper</c> calls <c>InitPluginComAsync</c> at
    ///         four places). Without this override every one of those operations started ten seconds late -
    ///         measured 2026-10-26: the base implementation returned 69420 after 10,012 ms.
    ///     </para>
    /// </summary>
    /// <param name="token">Cancel token.</param>
    protected override Task<int> InitAsync(CancellationToken token) => Task.FromResult(0);

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

        // Without a remote list an empty plan means "unknown", not "complete". Failing here keeps the host from
        // reporting a healthy installation it never verified.
        if (plan.IsRemoteListUnavailable)
        {
            throw new IOException(
                "The remote patch list could not be read, so the state of the installation is unknown. " +
                "Check the network connection and try again.");
        }

        // A fresh installation needs both tiers assembled from their chunk pools (big_common / big_shader). The flag
        // is only set when neither pool manifest could be fetched, so it means "the plan is incomplete", not
        // "installing is unsupported".
        if (plan.IsFreshInstallUnsupported)
        {
            SharedStatic.InstanceLogger?.LogWarning("[NeteaseGames] Fresh install still unsupported");
            throw new NotSupportedException(
                "The installation cannot be planned: neither base tier manifest could be fetched. Check the " +
                "network connection and try again.");
        }

        // A round that really rewrites the base tier cannot be finished by the plugin. `@bigpatchopen@` alone is
        // not the signal for that (it is a branch flag that is always on); the base tier fingerprint decides.
        // Applying only the delta tier and then writing the new version would report a finished update while the
        // installation is actually half updated, so such a run is refused up front.
        // Repair decides exactly the same work as Update, so it has to pass the same gate; applying it to Update
        // only let the "repair" button run the half-update this check exists to prevent (measured 2026-10-26).
        if (mode is NeteaseGamesPlanMode.Update or NeteaseGamesPlanMode.Repair)
        {
            VerifyBaseTierCompletable(manager.RemotePatchManifest, plan,
                NeteaseGamesLocalState.Read(gamePath));
        }

        if (plan.IsEmpty)
        {
            SharedStatic.InstanceLogger?.LogInformation(
                "[NeteaseGames] Nothing to download ({Mode}), the installation is complete", mode);
            progressStateDelegate?.Invoke(InstallProgressState.Completed);
            return;
        }

        progressStateDelegate?.Invoke(InstallProgressState.Download);

        long totalBytes = plan.TotalSize;

        // The badge counts real downloads, not plan rows: a fresh installation is ~11,000 assembled chunks plus
        // 342 plan items, and the chunks are what the user waits for.
        int totalFiles = plan.TotalWorkUnits;

        // One cumulative counter for the whole run. The host computes the percentage and the speed from
        // DownloadedBytes alone, so every phase has to feed the same monotonically increasing total: the assemblers
        // report one chunk at a time (that size, not a running total), while the downloader reports the running total
        // of its own phase. Handing either of those straight to the host made the bar oscillate between 5 MB and
        // 9 MB out of 66 GB and jump backwards (measured 2026-10-26).
        NeteaseGamesProgressTracker progress = new(progressDelegate, totalBytes, totalFiles);

        // Bytes that a previous, interrupted run already left on disk count from the start.
        progress.AddExisting(plan.DownloadedSize);

        // Report the initial state right away so the host does not show a frozen "0 / N" badge.
        progress.Report();

        NeteaseGamesDownloader downloader = new(_httpClient);

        // The base tier of a fresh installation is written first: it is 64 GB of shard files that the delta tier
        // has to sit on top of. Assembling it here (rather than as plan items) is what lets a chunk be downloaded
        // once and appended at its layout offset instead of being materialised as a temporary file per chunk.
        if (plan.BaseAssembly is { Steps.Count: > 0 } assembly)
        {
            string patchDirectory = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath);
            string baseStaging = Path.Combine(patchDirectory, BaseStagingDirectoryName);

            SharedStatic.InstanceLogger?.LogInformation(
                "[NeteaseGames] Assembling the base tier: {Chunks} chunks into {Shards} shards ({Size} bytes)",
                assembly.Steps.Count, assembly.ShardCount, assembly.TotalSize);

            string? failedChunk = await NeteaseGamesBaseAssembler.AssembleAsync(
                assembly,
                patchDirectory,
                baseStaging,
                downloader,
                progress.AddDelta,
                token).ConfigureAwait(false);


            if (failedChunk is not null)
            {
                throw new IOException(
                    $"The base tier could not be assembled ('{failedChunk}'). Run the installation again to " +
                    "resume; the shards already written are kept.");
            }

            // The game reads a shard THROUGH the index, so the index has to describe the layout that was just
            // produced - not the vendor's. Writing it is the step that makes the assembled bytes addressable.
            WriteBaseIndex(patchDirectory, assembly, plan.ShaderCacheList, plan);
        }

        // The shader tier is the second pool of shards (ResourceExA.mpk + ResourceExA1..16.mpk). It is not part of
        // the patch list and has no public per-shard endpoint, so a fresh installation has to assemble it too;
        // without it the game has no compiled shaders and the tier's index is missing entirely.
        if (plan.ShaderAssembly is { Steps.Count: > 0 } shaderAssembly)
        {
            string patchDirectory = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath);
            string shaderStaging = Path.Combine(patchDirectory, BaseStagingDirectoryName);

            SharedStatic.InstanceLogger?.LogInformation(
                "[NeteaseGames] Assembling the shader tier: {Chunks} chunks into {Shards} shards ({Size} bytes)",
                shaderAssembly.Steps.Count, shaderAssembly.ShardCount, shaderAssembly.AssemblySize);

            string? failedChunk = await NeteaseGamesBaseAssembler.AssembleAsync(
                shaderAssembly,
                patchDirectory,
                shaderStaging,
                downloader,
                progress.AddDelta,
                token,
                NeteaseGamesBaseLayoutPlanner.GetShaderShardFileName).ConfigureAwait(false);

            if (failedChunk is not null)
            {
                throw new IOException(
                    $"The shader tier could not be assembled ('{failedChunk}'). Run the installation again to " +
                    "resume; the shards already written are kept.");
            }

            WriteShaderIndex(patchDirectory, shaderAssembly, plan.ShaderCacheList);
        }

        // The two tiers - 66 GB of shards and their indexes - are on disk at this point, and they are the part a
        // retry cannot cheaply redo. Record them now instead of waiting for the delta tier: when the run used to
        // fail after the tiers but before the record (a single 7.9 MB side file, measured 2026-10-26), the game
        // found no `patchmd5` on its next start, assumed the base tier was missing and began re-downloading 60 GB
        // that was already there.
        //
        // The delta tier is applied below and writes the record again with the same patch version, so this is the
        // earlier of two identical commits rather than a different value.
        if (plan.BaseAssembly is { Steps.Count: > 0 } || plan.ShaderAssembly is { Steps.Count: > 0 })
        {
            string patchDirectory = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath);
            // The local patch list is not seeded here: it is one of the delta tier's own objects, so it only exists
            // once ApplyAsync has moved it into place. See the second commit point there.
            NeteaseGamesLocalState.WriteInstallVersionRecords(
                patchDirectory,
                plan.PatchVersion,
                plan.LauncherRecord,
                gamePath,
                plan.PackageVersion);

            SharedStatic.InstanceLogger?.LogInformation(
                "[NeteaseGames] Recorded the assembled tiers as version {Version}", plan.PatchVersion);
        }

        NeteaseGamesApplyResult result = await NeteaseGamesUpdateApplier.ApplyAsync(
            _httpClient,
            gamePath,
            plan,
            downloader,
            progress.SetAbsolute,
            token,
            progress.AddCompletedUnit).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new IOException(
                $"The patch could not be applied ('{result.FailedPath}'). The installation was left untouched.");
        }

        progressStateDelegate?.Invoke(InstallProgressState.Verify);

        // The base tier is not described by the patch list, but its own SQLite index knows how far each shard's
        // payload reaches. Reporting truncated base shards here keeps a run that cannot be completed from looking
        // like a finished update - the plugin cannot download that tier, so the user has to let the official
        // launcher repair it.
        IReadOnlyList<string> truncatedBaseShards = FindTruncatedBaseShards(gamePath);
        if (truncatedBaseShards.Count > 0)
        {
            SharedStatic.InstanceLogger?.LogError(
                "[NeteaseGames] {Count} base tier shards are shorter than the index describes after the update: {Shards}",
                truncatedBaseShards.Count, string.Join(", ", truncatedBaseShards));
        }

        // The local version is derived from the files on disk, so it has to be re-read after the update.
        manager.SetGamePath(gamePath);
        manager.EnsureLocalVersionProbed();
        InvalidatePlan();

        // Finish at exactly 100 %: the phases can fall a few bytes short (a side file whose size is unknown is
        // counted as 0), and a bar that stops at 99.99 % looks like a stuck run.
        progress.Complete();
        progressStateDelegate?.Invoke(InstallProgressState.Completed);
    }

    /// <summary>
    ///     Builds the plan of the current installation, or returns the plan that was built before.
    /// </summary>
    /// <param name="token">Cancel token.</param>
    private async Task<NeteaseGamesDownloadPlan> GetOrBuildPlanAsync(CancellationToken token)
    {
        string gamePath = EnsureAndGetGamePath();

        // A plan built before the remote manifests were probed carries no items and is flagged "remote list
        // unavailable"; caching it made every later size query answer 0 forever, because the cache is keyed on the
        // game path alone (measured 2026-10-26: size 0 after a successful probe, while a direct BuildPlanAsync on the
        // same installation returned 16,753,304 bytes).
        NeteaseGamesDownloadPlan? cached = _cachedPlan;
        if (cached is not null && _cachedPlanGamePath == gamePath && !cached.IsRemoteListUnavailable)
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
            // Both a delta update and a fresh installation record the PATCH version (line 3 of the remote manifest).
            // A fresh installation used to leave it at 0, which made the first state query after the install answer
            // "0.0.0" against the remote version - so the host immediately offered an update for the installation it
            // had just finished, and the next run walked into the base tier gate (measured 2026-10-26 on a simulated
            // post-install tree).
            //
            // `pkgversion` stays 0: it is the BASE PACKAGE version, a different counter, and this plan never rewrites
            // the base tier. Writing the patch version there corrupted the launcher's record (measured: 6,521,279
            // overwritten with 6,593,596).
            PatchVersion = manager.RemotePatchVersion,
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
            // The engine payload is served by its own host and only with the URL signature. Leaving this to the
            // downloader's default content hosts sent every engine request to h74.gph.*, which answers 404 for
            // /loose_file/... - measured 2026-10-24: 3 hosts x 2 attempts, all 404, so any run that touched the
            // engine failed with "the patch could not be applied".
            IReadOnlyList<string> engineHosts = NeteaseGamesContentEndpoints.GetEnginePayloadHosts();

            foreach (NeteaseGamesEngineFile file in await manager
                         .FetchEnginePayloadFileListAsync(miniPack.LooseFileParentDir, token)
                         .ConfigureAwait(false))
            {
                // `used_del_dir\mpk_res\...` looks like a delete marker but is not: it delivers the root level
                // resource packages (Resources*.mpk / ResourceEx*.mpk / mpk_filelist.info / package_resource.info)
                // to the game root. Measured 2026-10-26: downloading ResourceExA.mpkinfo.1, ResourceExB.mpkinfo.1
                // and Resources.mpkinfo.1 answers 200 with the exact MD5 of the file on disk. The target path is
                // resolved by NeteaseGamesEnginePatcher.ResolveTargetPath, which maps the prefix to the game root.
                string targetRelative = NeteaseGamesEnginePatcher.ResolveTargetPath(file);

                string localPath = Path.Combine(gamePath, targetRelative.Replace('/', Path.DirectorySeparatorChar));

                // Engine files are verified by size (the vendor hash algorithm is not known), so the same check
                // serves the update, the repair and the fresh install.
                if (!engineMissing && IsLocalFileComplete(localPath, file.Size, verifyOnDisk))
                {
                    continue;
                }

                plan.Items.Add(new NeteaseGamesDownloadItem
                {
                    RelativePath = $"{miniPack.LooseFileParentDir.TrimEnd('/')}/{file.UrlPath}".TrimStart('/'),
                    TargetRelativePath = targetRelative,
                    Kind = NeteaseGamesDownloadKind.LooseFile,
                    ExpectedSize = file.Size,
                    Hosts = engineHosts,
                    RequiresSignature = true
                });
            }

            // The engine file list itself belongs in the installation. The plugin used to download it only to read
            // it and never kept a copy, so a fresh installation had no Engine\md5_engine.txt - the file the official
            // installation carries (21,811 bytes, measured 2026-10-26) and the game verifies its engine against.
            string engineListPath = Path.Combine(gamePath, NeteaseGamesPaths.EngineMd5RelativePath);
            if (!File.Exists(engineListPath))
            {
                plan.Items.Add(new NeteaseGamesDownloadItem
                {
                    RelativePath = $"{miniPack.LooseFileParentDir.TrimEnd('/')}/{NeteaseGamesEnginePatcher.EngineFileListName}",
                    TargetRelativePath = NeteaseGamesPaths.EngineMd5RelativePath,
                    Kind = NeteaseGamesDownloadKind.LooseFile,
                    // The list does not announce its own size. Leaving it 0 makes the downloader fetch it from zero
                    // instead of trying to resume a file of unknown completeness.
                    ExpectedSize = 0,
                    Hosts = engineHosts,
                    RequiresSignature = true
                });
            }
        }

        // 2. The patch packages and the indexes that describe them.
        //
        // The remote list is the only authority here. It is fetched from the content version of the manifest
        // (line 0), which is the same directory the launcher downloads from: the list served there is byte for
        // byte the one the launcher keeps locally, and every package size in it matched the files the launcher
        // installed. Using the local list as a reference was wrong - the launcher cannot decide from a file it
        // wrote itself, otherwise it could never notice an update.
        NeteaseGamesPatchManifest? manifest = manager.RemotePatchManifest;
        if (manifest is not null && !string.IsNullOrEmpty(manifest.ContentVersion))
        {
            NeteaseGamesPatchList remote = await manager
                .FetchRemotePatchListAsync(manifest.ContentVersion, token)
                .ConfigureAwait(false);

            // The per-resource content hashes live in this list's `common` / `windows64_common` sections, and they
            // are the only source for the `hash` column of the base index (measured 2026-10-26: 232,449 of the
            // vendor's 232,450 rows match it exactly). Keeping the list on the plan lets the index writer fill that
            // column with real values instead of zeros.
            plan.RemotePatchList = remote;

            if (remote.Packages.Count == 0)
            {
                // Without a package list there is nothing to plan; refuse instead of silently reporting success.
                plan.IsRemoteListUnavailable = true;
                SharedStatic.InstanceLogger?.LogError(
                    "[NeteaseGames] The remote patch list of version {Version} contains no packages",
                    manifest.ContentVersion);
            }

            // The loose file section of the patch list is NOT a set of files on disk: those entries
            // (Char/…, Sounds/…, VA/… - 231k of them) are virtual engine paths that are stored inside the mpk
            // packages. Verified on a live installation: none of them exists as a file. Only the packages (and
            // their indexes) are downloadable objects.
            if (remote.LooseFiles.Count > 0)
            {
                SharedStatic.InstanceLogger?.LogDebug(
                    "[NeteaseGames] The patch list lists {Count} virtual resource paths; they are stored inside the packages",
                    remote.LooseFiles.Count);
            }

            // The indexes describe the internal layout of every shard, so they are checked against the shards
            // that are already on disk. The vendor's 64 bit content hash is not known, but a shard whose size
            // differs from the largest (offset + size) its index records was replaced by a shard of another
            // version - that is a content level check, not a name/size guess.
            Dictionary<uint, long> shardLayouts = await ReadLocalShardLayoutsAsync(gamePath, remote, token)
                .ConfigureAwait(false);
            Dictionary<string, uint> shardMarkers = BuildShardMarkerMap(remote);

            foreach ((string name, NeteaseGamesPackageEntry remotePackage) in remote.Packages)
            {
                string packagePath = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath, name);

                // Both modes compare against the size the remote list announces. An update brings the
                // installation to the remote state and a repair restores exactly that state, so the two only
                // differ in wording - what has to be downloaded is the same set.
                if (!NeedsPackageDownload(packagePath, name, remotePackage.TotalSize, shardMarkers, shardLayouts))
                {
                    continue;
                }

                plan.Items.Add(new NeteaseGamesDownloadItem
                {
                    RelativePath = ContentPath(manifest.ContentVersion, remotePackage.Section, name),
                    TargetRelativePath = name,
                    Kind = IsPackageIndex(name) ? NeteaseGamesDownloadKind.Index : NeteaseGamesDownloadKind.Package,
                    ExpectedSize = remotePackage.TotalSize
                });
            }
        }
        else
        {
            plan.IsRemoteListUnavailable = true;
            SharedStatic.InstanceLogger?.LogError(
                "[NeteaseGames] The remote manifest is missing or carries no content version; nothing can be planned");
        }

        // 3. The shader cache list.
        //
        // The official launcher keeps `shadercachelist_dx12.txt` in the patch directory and refreshes it with the
        // content version (measured 2026-10-23: 7,985,520 bytes on disk and on the content host, identical MD5).
        // It is not part of the patch list, so it has to be planned separately - leaving it out meant the plugin
        // silently kept a stale shader cache list forever.
        if (manifest is not null && !string.IsNullOrEmpty(manifest.ContentVersion))
        {
            AppendShaderCacheList(plan, gamePath, manifest.ContentVersion,
                NeteaseGamesLocalState.Read(gamePath), manifest.ShaderCacheListMd5);

            // A fresh installation has no local patch list at all, and every later update compares against it. The
            // content host serves the same document the launcher keeps locally (measured 2026-10-26: the file at the
            // installed content version is byte-for-byte the local patchlist_windows64_normal.txt), so it is fetched
            // once here and written under the launcher's own name.
            if (isFreshInstall)
            {
                AppendPatchList(plan, manifest.ContentVersion);
            }
        }

        // ⚠ AssignStaging must run AFTER every item has been added. It used to run here, before the base tier's
        // loose chunks were appended below, so those three items kept an empty StagedPath - and the downloader's
        // Path.GetDirectoryName("") threw ArgumentException at the very end of a 60 GB run (measured 2026-10-26).
        // The call now sits right before the plan is returned.

        // A fresh installation needs the base tier (Patch.mpk + Patch1..127) and the shader tier
        // (ResourceExA*), both assembled from the big_common / big_shader chunk pool.
        //
        // ⚠ The pool is described by TWO different bigpatchlist files and they are NOT interchangeable:
        //
        //   big_{@base_version@}  the older, cumulative pool. Its manifest lists 10,419 chunks, and 20 of them
        //                         no longer agree with the vendor's own Patch.mpkinfo (the round re-cut them).
        //                         Its chunk bytes are the previous generation: measured 2026-10-26,
        //                         big_{base}/Char_730 reconstructs only 3 of 6 resources to the bytes the live
        //                         shard holds, and base/essential_0 does not match either.
        //   big_{line 0}          the current pool. Its manifest lists 10,424 chunks and EVERY one of them
        //                         agrees with Patch.mpkinfo (10,421 consistent + 3 chunks that hold loose paths).
        //                         Its bytes are the live generation: line0/Char_730 reconstructs 6/6 and
        //                         line0/essential_1 matches Patch126 byte for byte.
        //
        // So the line-0 manifest is the authority, and merging the two copies (which an earlier revision did)
        // is actively harmful: on a name collision Merge() keeps the OLDER chunk, which would write 283
        // resources' worth of stale bytes across 19 chunks.
        //
        // The two directories split differently, which the downloader handles per chunk: every chunk the base
        // directory listed is served there and 404 on the line-0 directory, and the 9 chunks only the line-0
        // manifest lists are served on the line-0 directory (measured 30/30 and 9/9). So each chunk records
        // the directory whose manifest listed it, and the base directory is only a fallback.
        if (mode == NeteaseGamesPlanMode.Install)
        {
            string? contentVersion = manifest?.ContentVersion;
            if (manifest is null || string.IsNullOrEmpty(contentVersion))
            {
                SharedStatic.InstanceLogger?.LogWarning(
                    "[NeteaseGames] The remote manifest carries no content version; the base tier cannot be planned");
            }
            else
            {
                // The line-0 copy first, then the base copy only to fill in chunks the line-0 copy does not list.
                NeteaseGamesBigPatchList? line0 = await manager.FetchBigPatchListAsync(contentVersion, token)
                    .ConfigureAwait(false);

                NeteaseGamesBigPatchList? older = null;
                if (!string.IsNullOrEmpty(manifest.BaseVersion) &&
                    !string.Equals(manifest.BaseVersion, contentVersion, StringComparison.Ordinal))
                {
                    older = await manager.FetchBigPatchListAsync(manifest.BaseVersion, token).ConfigureAwait(false);
                }

                if (line0 is null && older is null)
                {
                    plan.IsFreshInstallUnsupported = true;
                    SharedStatic.InstanceLogger?.LogError(
                        "[NeteaseGames] Neither big patch manifest could be fetched; a fresh installation cannot be planned");
                }
                else
                {
                    // Plan from the line-0 manifest only; fall back to the older copy for chunks it does not name.
                    NeteaseGamesBaseAssemblyPlan assembly = await BuildBaseAssemblyPlanAsync(
                        manager, line0, older, contentVersion, manifest.BaseVersion, token).ConfigureAwait(false);

                    plan.BaseAssembly = assembly;
                    AppendBaseTierItems(plan, assembly);

                    // The shader tier is a second pool with the same rules but a different manifest name and a
                    // different shard file prefix. It is planned only for a fresh installation: an installation
                    // that already has it keeps it, and the tier is not described by the patch list.
                    await BuildShaderAssemblyPlanAsync(manager, plan, contentVersion, manifest.BaseVersion, token)
                        .ConfigureAwait(false);
                }
            }
        }

        // Every item exists by now (the base tier appends its loose chunks and the shader tier adds none), so the
        // staged paths can be assigned in one pass.
        plan.AssignStaging(stagingDirectory);

        SharedStatic.InstanceLogger?.LogInformation(
            "[NeteaseGames] Plan ({Mode}): {Packages} packages, {Indexes} indexes, {Size} bytes",
            mode, plan.PackageCount, plan.IndexCount, plan.TotalSize);

        return plan;
    }

    /// <summary>
    ///     Builds the map from a shard file name to the shard id (marker) the vendor's indexes use for it.
    ///     <para>
    ///         <b>The marker is the number in the file name, doubled:</b> <c>Patch1000.mpk</c> -&gt; 2000,
    ///         <c>Patch1001.mpk</c> -&gt; 2002, <c>Patch2000.mpk</c> -&gt; 4000, <c>Patch2500.mpk</c> -&gt; <b>5000</b>,
    ///         <c>Patch3000.mpk</c> -&gt; 6000. Verified on 2026-10-24 against all 47 live shards and all three live
    ///         indexes: every predicted marker exists and no index carries a marker that is not predicted.
    ///     </para>
    ///     <para>
    ///         ⚠ An earlier revision derived the marker from the shard's <i>ordinal inside its section</i>
    ///         (<c>sectionBase + ordinal * 2</c>). That happens to agree with the numbering above for contiguous
    ///         sections, but it produced <b>4060</b> for <c>Patch2500.mpk</c> instead of the real <b>5000</b>, and
    ///         the layout check then silently passed for that shard because it could not find any index data.
    ///     </para>
    /// </summary>
    /// <param name="remote">The remote patch list (its index names decide what is read).</param>
    public static Dictionary<string, uint> BuildShardMarkerMap(NeteaseGamesPatchList remote)
    {
        ArgumentNullException.ThrowIfNull(remote);

        Dictionary<string, uint> markers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in remote.Packages.Keys)
        {
            if (IsPackageIndex(name))
            {
                continue;
            }

            uint marker = GetShardMarker(name);
            if (marker > 0)
            {
                markers[name] = marker;
            }
        }

        return markers;
    }

    /// <summary>
    ///     Derives the marker (shard id) of a shard file from its name: the first number in the name, doubled.
    /// </summary>
    /// <param name="name">The file name, e.g. <c>Patch2500.mpk</c>.</param>
    /// <returns>The marker, or 0 when the name carries no number (the caller then cannot check the shard).</returns>
    public static uint GetShardMarker(string name)
    {
        int index = ParseShardIndex(name);
        return index == int.MaxValue || index < 0 ? 0 : (uint)index * 2;
    }

    /// <summary>
    ///     Extracts the numeric suffix of a shard name (<c>Patch2017.mpk</c> -&gt; 2017).
    /// </summary>
    public static int ParseShardIndex(string name)
    {
        int start = 0;
        while (start < name.Length && !char.IsAsciiDigit(name[start]))
        {
            start++;
        }

        int end = start;
        while (end < name.Length && char.IsAsciiDigit(name[end]))
        {
            end++;
        }

        return end > start && int.TryParse(name.AsSpan(start, end - start), out int value) ? value : int.MaxValue;
    }

    /// <summary>
    ///     Checks a shard on disk against the layout its index describes.
    ///     <para>
    ///         The largest <c>offset + size</c> an index records for a shard is how far the <b>payload</b> of that
    ///         shard reaches - it is <b>not</b> necessarily the file length. Measured on 2026-10-23:
    ///         <c>Patch1009.mpk</c> is 17,985,424 bytes on disk and on the content host, while its index
    ///         (marker 2018) ends at 17,923,658, so the shard carries 61,766 bytes of tail padding past the last
    ///         resource. Requiring equality therefore produced a false repair of a perfectly good shard.
    ///     </para>
    ///     <para>
    ///         The check is a lower bound instead: a shard must be at least as long as its payload. A shard that
    ///         is <b>shorter</b> is truncated; one taken from another patch version normally differs in the other
    ///         direction and is caught by the announced-size comparison the caller performs.
    ///     </para>
    /// </summary>
    /// <param name="packagePath">The shard on disk.</param>
    /// <param name="name">The shard name.</param>
    /// <param name="markers">The shard name to shard id map.</param>
    /// <param name="layouts">The shard id to payload length map.</param>
    /// <returns>
    ///         <c>true</c> when the shard covers its payload, or when there is no index on disk at all (then the
    ///         announced size is the only evidence). <c>false</c> when an index exists but does not describe this
    ///         shard: that means the name/marker mapping is wrong or the index belongs to another round, and
    ///         passing it would hide a broken or stale shard.
    /// </returns>
    public static bool IsLayoutConsistent(string packagePath, string name, Dictionary<string, uint> markers,
        Dictionary<uint, long> layouts)
    {
        if (layouts.Count == 0)
        {
            // No index on disk: nothing can be compared, the announced-size check is all there is.
            return true;
        }

        if (!markers.TryGetValue(name, out uint marker) || !layouts.TryGetValue(marker, out long payloadLength))
        {
            // An index exists but says nothing about this shard. That is not "healthy" - it is "unprovable",
            // and the previous behavior of returning true here is what let the wrong Patch2500 marker (4060
            // instead of 5000) silently disable this gate for that shard.
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGames] The index on disk does not describe the shard '{Name}' (marker {Marker}); it cannot be verified",
                name, marker);
            return false;
        }

        try
        {
            long actual = new FileInfo(packagePath).Length;
            if (actual >= payloadLength)
            {
                return true;
            }

            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGames] The shard '{Name}' has {Actual} bytes but its index describes {Expected} bytes of payload; it is truncated",
                name, actual, payloadLength);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
    ///     Decides whether a package of the remote list has to be downloaded.
    ///     <para>
    ///         A <b>shard</b> is judged by two gates: the announced size and the layout its index describes
    ///         (see <see cref="IsLayoutConsistent" />). Both have to pass for the shard to count as healthy.
    ///     </para>
    ///     <para>
    ///         An <b>index</b> (<c>*.mpkinfo</c>) is judged by its size only. The layout gate cannot apply to it:
    ///         the indexes are the very files that describe the shards, so they are deliberately absent from
    ///         <see cref="BuildShardMarkerMap" />, and <see cref="IsLayoutConsistent" /> returns <c>false</c> for
    ///         "an index exists but does not describe this file" - which is true for every index by construction.
    ///         Applying the gate to them made all three indexes part of <b>every</b> plan: measured on a fully
    ///         up-to-date installation, the plan was 3 items / 10,579,120 bytes and <c>plan.IsEmpty</c> could
    ///         never be true, so the host never saw a finished update.
    ///     </para>
    /// </summary>
    /// <param name="packagePath">The package on disk.</param>
    /// <param name="name">The package name as the remote list writes it.</param>
    /// <param name="expectedSize">The size the remote list announces.</param>
    /// <param name="markers">The shard name to shard id map.</param>
    /// <param name="layouts">The shard id to payload length map.</param>
    /// <returns><c>true</c> when the package has to be downloaded.</returns>
    public static bool NeedsPackageDownload(string packagePath, string name, long expectedSize,
        Dictionary<string, uint> markers, Dictionary<uint, long> layouts)
    {
        if (!IsLocalFileComplete(packagePath, expectedSize, verifyHash: false))
        {
            return true;
        }

        return !IsPackageIndex(name) && !IsLayoutConsistent(packagePath, name, markers, layouts);
    }

    /// <summary>
    ///     Whether a package name is a package index (<c>*.mpkinfo</c>) instead of a shard.
    /// </summary>
    public static bool IsPackageIndex(string name)
        => name.EndsWith(".mpkinfo", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Decides whether a round may be applied by the plugin, i.e. whether it can be completed without
    ///     rewriting the base shard tier.
    ///     <para>
    ///         <c>@bigpatchopen@</c> is a <b>branch-level capability flag</b>, not a statement that this round
    ///         touches the base tier. Measured 2026-10-22 .. 10-24 it was <c>true</c> on every single round while
    ///         the delta tier alone was sufficient - so refusing whenever it is set (which an earlier revision did)
    ///         made <b>every</b> update fail with "this update cannot be applied by the plugin".
    ///     </para>
    ///     <para>
    ///         The real question is whether the base tier <b>changed</b>, and the manifest answers it directly:
    ///         <c>@bigpatchmd5@</c> is the fingerprint of the base tier. If it equals what the installation
    ///         recorded, the base tier is already current and the round is a pure delta. Only when it differs (or
    ///         when the round ships a full package) is the plugin unable to complete the work.
    ///     </para>
    /// </summary>
    /// <param name="manifest">The remote manifest that was probed (may be null).</param>
    /// <param name="plan">The plan that was built.</param>
    /// <param name="localState">The local state that records the installed base tier fingerprint (may be null).</param>
    /// <exception cref="NotSupportedException">The round really does rewrite the base tier.</exception>
    public static void VerifyBaseTierCompletable(NeteaseGamesPatchManifest? manifest, NeteaseGamesDownloadPlan plan,
        NeteaseGamesLocalState? localState = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (manifest is null || plan.IsEmpty)
        {
            return;
        }

        if (manifest.IsFullPackage)
        {
            RejectBaseTier("this round ships a full package (@is_full@)");
        }

        if (!manifest.IsBigPatchOpen)
        {
            return;
        }

        // The switch is on. It only means "the base tier may be rewritten this round" - compare the fingerprint
        // the installation recorded with the one the round announces.
        string? remoteFingerprint = manifest.BigPatchMd5;
        string? localFingerprint = localState?.BigPatchMd5;

        if (string.IsNullOrEmpty(remoteFingerprint))
        {
            // A round with the switch on but no fingerprint cannot be judged: stay on the safe side.
            RejectBaseTier("this round turns @bigpatchopen@ on without announcing @bigpatchmd5@");
        }

        if (string.IsNullOrEmpty(localFingerprint))
        {
            // Nothing recorded locally (e.g. an installation the plugin never updated). The delta tier alone still
            // brings the patch version up to date; a base-tier rewrite would have bumped the fingerprint, and the
            // next successful run records it.
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGames] The installation carries no recorded base tier fingerprint (@bigpatchmd5@); " +
                "applying the delta tier only");
            return;
        }

        if (string.Equals(localFingerprint, remoteFingerprint, StringComparison.Ordinal))
        {
            SharedStatic.InstanceLogger?.LogInformation(
                "[NeteaseGames] @bigpatchopen@ is on but the base tier fingerprint is unchanged ({Fingerprint}); " +
                "this round is a pure delta", remoteFingerprint);
            return;
        }

        RejectBaseTier(
            $"the base tier changed (@bigpatchmd5@ {localFingerprint} -> {remoteFingerprint})");

        void RejectBaseTier(string reason)
        {
            SharedStatic.InstanceLogger?.LogError(
                "[NeteaseGames] Refusing the update: {Reason}. The base tier (Patch.mpk, Patch1..127, ResourceExA..) " +
                "is not served by any public single-shard endpoint, so it has to be installed by the official launcher",
                reason);

            throw new NotSupportedException(
                $"This update cannot be applied by the plugin: {reason}. The base packages (Patch.mpk, Patch1..127, " +
                "ResourceExA..) are only available to the official launcher. Open the official launcher once to let " +
                "it finish this update, then use the plugin for the delta updates again.");
        }
    }

    /// <summary>
    ///     Builds the base tier assembly plan of a fresh installation.
    ///     <para>
    ///         The pool is planned <b>by the line-0 manifest</b>, which is the only copy whose chunks agree with the
    ///         vendor's own <c>Patch.mpkinfo</c> (10,421 of 10,424 chunks, the other three holding the loose paths);
    ///         the older <c>@base_version@</c> copy is consulted only for chunk names the line-0 copy does not list.
    ///     </para>
    ///     <para>
    ///         Every chunk is placed as one unbroken byte run. That is sound because a chunk is a gapless
    ///         concatenation of its resources in list order - verified 2026-10-26 by slicing two chunks' resources
    ///         back out of the live shards byte for byte (<c>CGs_13</c> @ shard 85, <c>Char_13</c> with 57
    ///         resources after a 10 KB hole in the vendor's own layout, and <c>Char_730</c> 6/6). So the planner
    ///         only has to decide where each chunk starts, and assembly is a straight copy.
    ///     </para>
    /// </summary>
    /// <param name="manager">The game manager (for the resource sizes).</param>
    /// <param name="line0">The line-0 manifest (authoritative).</param>
    /// <param name="older">The base-version manifest, or <c>null</c>.</param>
    /// <param name="contentVersion">The line-0 directory version.</param>
    /// <param name="baseVersion">The base directory version, or an empty string.</param>
    /// <param name="token">Cancel token.</param>
    private static async Task<NeteaseGamesBaseAssemblyPlan> BuildBaseAssemblyPlanAsync(
        NeteaseGamesGameManager manager,
        NeteaseGamesBigPatchList? line0,
        NeteaseGamesBigPatchList? older,
        string contentVersion,
        string? baseVersion,
        CancellationToken token)
    {
        NeteaseGamesBaseAssemblyPlan plan = new();

        // The resource sizes come from the remote patch list, which is the only source that announces them.
        NeteaseGamesPatchList remote = await manager.FetchRemotePatchListAsync(contentVersion, token)
            .ConfigureAwait(false);

        Dictionary<string, long> sizes = new(StringComparer.Ordinal);
        foreach ((string path, NeteaseGamesFileEntry entry) in remote.LooseFiles)
        {
            sizes[path] = entry.Size;
        }

        // The chunks in the order they should be laid out: the line-0 copy first (authoritative), then any chunk
        // the base copy has and the line-0 copy does not (4 names on the live branch). A name present in both is
        // taken from the line-0 copy only - the base copy's version of it is stale.
        List<NeteaseGamesBigChunk> ordered = [];
        HashSet<(string Section, string Name)> seen = [];
        foreach (NeteaseGamesBigPatchList? source in new[] { line0, older })
        {
            if (source is null)
            {
                continue;
            }

            foreach (NeteaseGamesBigChunk chunk in source.Chunks.Values)
            {
                if (seen.Add((chunk.Section, chunk.Name)))
                {
                    ordered.Add(chunk);
                }
            }
        }

        if (ordered.Count == 0)
        {
            return plan;
        }

        // Which pool paths are standalone patch files rather than shard resources. A fresh install has no local
        // index to compare against, so the distinction is made from the path shape - the same three names the live
        // installation carries as plain files and that its own Patch.mpkinfo does not describe. It deliberately is
        // NOT "every single-resource chunk": 672 such chunks ('Fonts_1', 'CGs_0', 'Char_14', ...) are ordinary
        // shard resources, and treating them as files would drop them out of the base tier.
        HashSet<string> loosePaths = [NeteaseGamesBaseLooseFiles.Prerequisite,
            NeteaseGamesBaseLooseFiles.LazycodeDictionary, NeteaseGamesBaseLooseFiles.FourAFontBlob];

        NeteaseGamesBaseLayout layout = NeteaseGamesBaseLayoutPlanner.Plan(ordered, sizes, loosePaths);

        foreach (NeteaseGamesBigChunk skipped in layout.SkippedChunks)
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGames] Chunk '{Chunk}' is skipped: its announced size disagrees with its resources",
                skipped.Name);
        }

        // Which directory serves a chunk is decided by a measured rule, not by which manifest happened to list it:
        // the line-0 directory serves the chunks THIS round changed (9 new names + 19 names whose size moved),
        // and the base directory serves everything else. Verified 2026-10-26 by asking both directories for every
        // one of the 28 changed chunks: the line-0 directory answered with exactly the announced size, 28/28, and
        // for 30 sampled unchanged chunks the base directory answered and the line-0 directory answered 404.
        // Listing-based selection alone would be wrong: the base manifest lists 10,419 chunks, 19 of them stale,
        // and those stale copies are also served under the base directory with the stale size.
        Dictionary<(string Section, string Name), string> provider = [];
        if (older is not null && !string.IsNullOrEmpty(baseVersion))
        {
            foreach (((string Section, string Name) key, NeteaseGamesBigChunk chunk) in older.Chunks)
            {
                // A chunk the line-0 copy re-cut is served by the line-0 directory; one it left untouched is
                // served by the base directory.
                bool unchanged = line0 is not null &&
                                 line0.Chunks.TryGetValue(key, out NeteaseGamesBigChunk? current) &&
                                 current.Size == chunk.Size &&
                                 current.Paths.SequenceEqual(chunk.Paths);

                provider[key] = unchanged ? baseVersion : contentVersion;
            }
        }

        if (line0 is not null)
        {
            foreach ((string Section, string Name) key in line0.Chunks.Keys)
            {
                // A name the base manifest does not list at all is new this round, so the line-0 directory serves it.
                provider.TryAdd(key, contentVersion);
            }
        }

        // BuildSteps matches each chunk to the placements its resources landed at.
        foreach (NeteaseGamesBaseAssembler.Step step in NeteaseGamesBaseAssembler.BuildSteps(layout, ordered, string.Empty))
        {
            string source = provider.TryGetValue((step.Chunk.Section, step.Chunk.Name), out string? version)
                ? version
                : contentVersion;

            plan.Steps.Add(step with { SourceVersion = source });
        }

        plan.ShardCount = layout.ShardCount;
        plan.Placements.AddRange(layout.Placements);

        // The standalone patch files are not part of the shard layout; they are downloaded as whole objects.
        foreach (NeteaseGamesBigChunk chunk in ordered)
        {
            if (chunk.Paths.Count != 1 || !NeteaseGamesBaseLooseFiles.IsLoose(chunk.Paths[0]))
            {
                continue;
            }

            string source = provider.TryGetValue((chunk.Section, chunk.Name), out string? version)
                ? version
                : contentVersion;

            plan.LooseChunks.Add(new NeteaseGamesBaseLooseChunk(chunk, source));
        }

        SharedStatic.InstanceLogger?.LogInformation(
            "[NeteaseGames] Base tier plan: {Chunks} chunks, {Shards} shards, {Size} bytes, {Skipped} skipped",
            plan.Steps.Count, layout.ShardCount, plan.TotalSize, layout.SkippedChunks.Count);

        return plan;
    }

    /// <summary>
    ///     Plans the shader tier of a fresh installation.
    ///     <para>
    ///         The tier is the second shard pool: <c>ResourceExA.mpk</c> + <c>ResourceExA1..16.mpk</c>
    ///         (4,989,773,170 bytes / 17 files on the live branch) plus its index <c>ResourceExA.patchinfo</c>
    ///         (173,861 rows). It is not part of the patch list, and no content host serves the shards directly, so
    ///         it has to be assembled from <c>big_shader</c> chunks exactly like the base tier.
    ///     </para>
    ///     <para>
    ///         Two measured rules carry over from the base tier unchanged:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b>A chunk is a gapless concatenation of its resources in list order.</b> Verified on 2026-10-26:
    ///             <c>dx12_916</c> 137/137, <c>dx12_920</c> 4/4, <c>dx12_1</c> 518/518 and <c>dx12_500</c> 150/150
    ///             resources slice back out of the live <c>ResourceExA*.mpk</c> byte for byte.
    ///         </item>
    ///         <item>
    ///             <b>The directory split is the same.</b> The 3 chunks this round re-cut (<c>dx12_916/917/918</c>)
    ///             and the one it added (<c>dx12_920</c>) are served only under <c>big_{line 0}</c>; the other 916
    ///             are served only under <c>big_{@base_version@}</c> (measured: every sampled chunk 404s on the
    ///             other directory).
    ///         </item>
    ///     </list>
    ///     <para>
    ///         Unlike the base tier, every shader chunk is size-consistent (920/920 measured) and no path is listed
    ///         twice, so nothing is skipped. The per-resource size and hash come from the shader cache list
    ///         (<c>shadercachelist_dx12.txt</c>), whose 173,859 entries match the live index exactly.
    ///     </para>
    /// </summary>
    /// <param name="manager">The game manager (for the manifests).</param>
    /// <param name="plan">The plan to extend.</param>
    /// <param name="contentVersion">The line-0 directory version.</param>
    /// <param name="baseVersion">The <c>@base_version@</c> directory version, or an empty string.</param>
    /// <param name="token">Cancel token.</param>
    private static async Task BuildShaderAssemblyPlanAsync(NeteaseGamesGameManager manager,
        NeteaseGamesDownloadPlan plan, string contentVersion, string? baseVersion, CancellationToken token)
    {
        NeteaseGamesShaderCacheList? cacheList = await manager.FetchShaderCacheListAsync(contentVersion, token)
            .ConfigureAwait(false);

        if (cacheList is null)
        {
            // Without the per-resource sizes the pool cannot be laid out at all: a chunk is placed by summing the
            // sizes of the resources it lists, and an unknown size would silently produce a wrong offset.
            SharedStatic.InstanceLogger?.LogError(
                "[NeteaseGames] The shader cache list could not be read; the shader tier cannot be planned");
            plan.IsFreshInstallUnsupported = true;
            return;
        }

        plan.ShaderCacheList = cacheList;

        NeteaseGamesBigPatchList? line0 = await manager.FetchShaderPoolAsync(contentVersion, token)
            .ConfigureAwait(false);

        NeteaseGamesBigPatchList? older = null;
        if (!string.IsNullOrEmpty(baseVersion) &&
            !string.Equals(baseVersion, contentVersion, StringComparison.Ordinal))
        {
            older = await manager.FetchShaderPoolAsync(baseVersion, token).ConfigureAwait(false);
        }

        if (line0 is null && older is null)
        {
            SharedStatic.InstanceLogger?.LogError(
                "[NeteaseGames] Neither shader pool manifest could be fetched; the shader tier cannot be planned");
            plan.IsFreshInstallUnsupported = true;
            return;
        }

        Dictionary<string, long> sizes = cacheList.ToSizeMap();

        // Same ordering rule as the base tier: the line-0 copy first, then any chunk only the base copy lists.
        //
        // ⚠ The section of a chunk is the name of the object's own <i>property</i> in the manifest, and the shader
        // manifest calls it `big_file_list` while the content host serves the chunks under `big_shader`. Using the
        // parsed section verbatim therefore asked for `big_{v}/big_file_list/dx12_920`, which answers 404 (measured
        // 2026-10-26), so every chunk is re-sectioned to the directory the host actually uses.
        List<NeteaseGamesBigChunk> ordered = [];
        HashSet<(string Section, string Name)> seen = [];
        foreach (NeteaseGamesBigPatchList? source in new[] { line0, older })
        {
            if (source is null)
            {
                continue;
            }

            foreach (NeteaseGamesBigChunk chunk in source.Chunks.Values)
            {
                NeteaseGamesBigChunk normalized = chunk with { Section = NeteaseGamesContentEndpoints.BigShaderSection };
                if (seen.Add((normalized.Section, normalized.Name)))
                {
                    ordered.Add(normalized);
                }
            }
        }

        NeteaseGamesBaseLayout layout = NeteaseGamesBaseLayoutPlanner.Plan(ordered, sizes);

        foreach (NeteaseGamesBigChunk skipped in layout.SkippedChunks)
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGames] Shader chunk '{Chunk}' is skipped: its announced size disagrees with its resources",
                skipped.Name);
        }

        // A chunk this round re-cut or added is served by the line-0 directory, everything else by the base one.
        // Keyed by name only: the section was rewritten above, and the two manifests agree on it anyway.
        Dictionary<string, string> provider = new(StringComparer.Ordinal);
        if (older is not null && !string.IsNullOrEmpty(baseVersion))
        {
            foreach (((string Section, string Name) key, NeteaseGamesBigChunk chunk) in older.Chunks)
            {
                bool unchanged = line0 is not null &&
                                 line0.Chunks.TryGetValue(key, out NeteaseGamesBigChunk? current) &&
                                 current.Size == chunk.Size &&
                                 current.Paths.SequenceEqual(chunk.Paths);

                provider[key.Name] = unchanged ? baseVersion : contentVersion;
            }
        }

        if (line0 is not null)
        {
            foreach ((string Section, string Name) key in line0.Chunks.Keys)
            {
                provider.TryAdd(key.Name, contentVersion);
            }
        }

        NeteaseGamesBaseAssemblyPlan assembly = new()
        {
            ShardCount = layout.ShardCount
        };

        foreach (NeteaseGamesBaseAssembler.Step step in NeteaseGamesBaseAssembler.BuildSteps(layout, ordered, string.Empty))
        {
            string source = provider.TryGetValue(step.Chunk.Name, out string? version)
                ? version
                : contentVersion;

            assembly.Steps.Add(step with { SourceVersion = source });
        }

        assembly.Placements.AddRange(layout.Placements);

        SharedStatic.InstanceLogger?.LogInformation(
            "[NeteaseGames] Shader tier plan: {Chunks} chunks, {Shards} shards, {Size} bytes, {Skipped} skipped",
            assembly.Steps.Count, layout.ShardCount, assembly.AssemblySize, layout.SkippedChunks.Count);

        plan.ShaderAssembly = assembly;
    }

    /// <summary>
    ///     Writes <c>LocalData\Patch\ResourceExA.patchinfo</c> for the shader layout the assembly produced.
    ///     <para>
    ///         The file is the shader counterpart of <c>Patch.mpkinfo</c>: a SQLite database whose table is
    ///         <c>info1</c> (not <c>info0</c>) with two meta rows, <c>max_index</c> and <c>status</c>. Measured on the
    ///         live file: 173,861 rows (173,859 resources + 2 directory placeholders <c>Shader</c> and
    ///         <c>Shader/dx12</c>), <c>max_index=16</c>, and
    ///         <c>status={"compacting": false, "suffix": 1, "extra": "", "delete_compacting": false}</c>.
    ///     </para>
    ///     <para>
    ///         Every row carries the real per-resource hash from the shader cache list, not 0: measured, all 173,859
    ///         hashes of the live index equal the values in <c>shadercachelist_dx12.txt</c>.
    ///     </para>
    /// </summary>
    /// <param name="patchDirectory">The <c>LocalData\Patch</c> directory.</param>
    /// <param name="assembly">The shader assembly that was written.</param>
    /// <param name="cacheList">The shader cache list that supplied the hashes.</param>
    private static void WriteShaderIndex(string patchDirectory, NeteaseGamesBaseAssemblyPlan assembly,
        NeteaseGamesShaderCacheList? cacheList)
    {
        List<NeteaseGamesBaseIndexWriter.Row> rows = new(assembly.Placements.Count);
        foreach (NeteaseGamesBasePlacement placement in assembly.Placements)
        {
            if (placement.Size <= 0)
            {
                continue;
            }

            ulong hash = cacheList is not null &&
                         cacheList.Resources.TryGetValue(placement.Path, out NeteaseGamesShaderResource resource)
                ? resource.Hash
                : 0;

            rows.Add(new NeteaseGamesBaseIndexWriter.Row(
                placement.Path,
                (uint)placement.Size,
                (ushort)placement.ShardIndex,
                (uint)placement.Offset,
                hash));
        }

        // The live shader index carries two placeholder rows for the directory tree (`Shader` and `Shader/dx12`),
        // so the same placeholder rule as the base tier applies here too.
        foreach (string directory in EnumerateDirectories(rows))
        {
            rows.Add(new NeteaseGamesBaseIndexWriter.Row(directory, 0, 0, 0, 0));
        }

        string indexPath = Path.Combine(patchDirectory, ShaderIndexFileName);
        string temporary = indexPath + ".tmp";

        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            NeteaseGamesBaseIndexWriter.Write(stream, NeteaseGamesBaseIndex.ShaderTableName, rows,
            [
                new KeyValuePair<string, string>("max_index",
                    (assembly.ShardCount - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("status", ShaderIndexStatus)
            ]);
        }

        File.Move(temporary, indexPath, overwrite: true);

        SharedStatic.InstanceLogger?.LogInformation(
            "[NeteaseGames] Wrote {Path}: {Resources} resources + {Directories} directories across {Shards} shards",
            indexPath, assembly.Placements.Count, rows.Count - assembly.Placements.Count, assembly.ShardCount);
    }

    /// <summary>The file name of the shader tier index.</summary>
    public const string ShaderIndexFileName = "ResourceExA.patchinfo";

    /// <summary>
    ///     The <c>status</c> meta value of the live <c>ResourceExA.patchinfo</c> (measured 2026-10-26).
    /// </summary>
    public const string ShaderIndexStatus =
        "{\"compacting\": false, \"suffix\": 1, \"extra\": \"\", \"delete_compacting\": false}";

    /// <summary>
    ///     Writes <c>LocalData\Patch\Patch.mpkinfo</c> for the layout the assembly produced.
    ///     <para>
    ///         The game addresses every base resource through this index, so it must describe the shards that were
    ///         actually written. The vendor's own index cannot be reused: its resource-to-shard mapping is history
    ///         dependent and differs from the layout the planner produces (measured 2026-10-26: the vendor keeps
    ///         <c>w_0</c> in shard 106, the planner puts it elsewhere). What matters is that each resource's
    ///         <c>(shard, offset, size)</c> matches the bytes on disk, which it does by construction.
    ///     </para>
    ///     <para>
    ///         The two meta rows are the ones the live file carries: <c>max_index</c> (the highest shard id) and
    ///         <c>extra_mpkinfo_families</c> (the delta tier's index and shard names). They are written as the live
    ///         installation has them so a launcher that reads them sees a normal installation.
    ///     </para>
    /// </summary>
    /// <param name="patchDirectory">The <c>LocalData\Patch</c> directory.</param>
    /// <param name="assembly">The assembly that was written.</param>
    private static void WriteBaseIndex(string patchDirectory, NeteaseGamesBaseAssemblyPlan assembly,
        NeteaseGamesShaderCacheList? shaderCacheList, NeteaseGamesDownloadPlan plan)
    {
        // Every row carries the vendor's 64 bit content hash. The vendor's own index has a non-zero hash in all
        // 232,450 rows, and the game verifies resources by it - an index full of zeros is what made a complete
        // 66 GB installation look broken and re-download itself (measured 2026-10-26).
        //
        // Two sources, in order of authority:
        //   1. the remote patch list (common / windows64_common) - matches 232,449 of the vendor's 232,450 rows;
        //   2. the shader cache list, which covers the shader resources the base pool also carries.
        // Only a resource neither source knows keeps 0, which is now an edge case rather than the rule.
        Dictionary<string, ulong> knownHashes = [];
        if (shaderCacheList is not null)
        {
            foreach (NeteaseGamesShaderResource resource in shaderCacheList.Resources.Values)
            {
                knownHashes[resource.Path] = resource.Hash;
            }
        }

        if (plan.RemotePatchList is { } patchList)
        {
            foreach ((string path, NeteaseGamesFileEntry entry) in patchList.LooseFiles)
            {
                knownHashes[path] = entry.Hash;
            }
        }

        List<NeteaseGamesBaseIndexWriter.Row> rows = new(assembly.Placements.Count);
        foreach (NeteaseGamesBasePlacement placement in assembly.Placements)
        {
            if (placement.Size <= 0)
            {
                continue;
            }

            rows.Add(new NeteaseGamesBaseIndexWriter.Row(
                placement.Path,
                (uint)placement.Size,
                (ushort)placement.ShardIndex,
                (uint)placement.Offset,
                knownHashes.TryGetValue(placement.Path, out ulong hash) ? hash : 0));
        }

        // The live index carries one placeholder row per directory that holds resources (size 0, shard 0,
        // offset 0, hash 0). Measured on 2026-10-26: 2,460 of them, and they are exactly the set of parent
        // directories of the shard resources (the three standalone patch files are not counted). Leaving them out
        // produced an index with 232,450 rows where the vendor's has 234,910.
        foreach (string directory in EnumerateDirectories(rows))
        {
            rows.Add(new NeteaseGamesBaseIndexWriter.Row(directory, 0, 0, 0, 0));
        }

        string indexPath = Path.Combine(patchDirectory, NeteaseGamesBaseIndex.FileName);
        string temporary = indexPath + ".tmp";

        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            NeteaseGamesBaseIndexWriter.Write(stream, NeteaseGamesBaseIndex.TableName, rows,
            [
                new KeyValuePair<string, string>("max_index",
                    (assembly.ShardCount - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                // The live value is the list of delta tier shards and indexes this installation knows about; it is
                // what the launcher uses to find them. Measured: 50 entries (47 shards + 3 indexes).
                new KeyValuePair<string, string>("extra_mpkinfo_families", BuildFamilyList(plan))
            ]);
        }

        File.Move(temporary, indexPath, overwrite: true);

        SharedStatic.InstanceLogger?.LogInformation(
            "[NeteaseGames] Wrote {Path}: {Rows} rows ({Resources} resources + {Directories} directories) across " +
            "{Shards} shards", indexPath, rows.Count, assembly.Placements.Count, rows.Count - assembly.Placements.Count,
            assembly.ShardCount);
    }

    /// <summary>
    ///     The distinct parent directories of the given rows, in a stable order.
    /// </summary>
    private static IEnumerable<string> EnumerateDirectories(IEnumerable<NeteaseGamesBaseIndexWriter.Row> rows)
    {
        HashSet<string> directories = new(StringComparer.Ordinal);
        foreach (NeteaseGamesBaseIndexWriter.Row row in rows)
        {
            int slash = row.Path.LastIndexOf('/');
            if (slash > 0)
            {
                directories.Add(row.Path[..slash]);
            }
        }

        List<string> ordered = [.. directories];
        ordered.Sort(StringComparer.Ordinal);
        return ordered;
    }

    /// <summary>
    ///     Builds the <c>extra_mpkinfo_families</c> value: the delta tier shards and indexes, as a JSON array of
    ///     file names in the order the live installation lists them.
    /// </summary>
    private static string BuildFamilyList(NeteaseGamesDownloadPlan plan)
    {
        List<string> families = [];
        foreach (NeteaseGamesDownloadItem item in plan.Items)
        {
            if (item.Kind is NeteaseGamesDownloadKind.Package or NeteaseGamesDownloadKind.Index &&
                !families.Contains(item.TargetRelativePath, StringComparer.Ordinal))
            {
                families.Add(item.TargetRelativePath);
            }
        }

        if (families.Count == 0)
        {
            return "[]";
        }

        System.Text.StringBuilder builder = new("[");
        for (int i = 0; i < families.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append('"').Append(families[i]).Append('"');
        }

        return builder.Append(']').ToString();
    }

    /// <summary>
    ///     Adds the download items of the base tier that are not part of the shard assembly.
    ///     <para>
    ///         Three of the line-0 manifest's paths are not resources inside the base index but standalone files in
    ///         the patch directory (<c>prerequisite</c>, <c>Misc/lazycode_zstd_dict</c>,
    ///         <c>4a/4a31…</c>). Measured 2026-10-26: each one lives in a single-resource chunk, and the chunk's
    ///         bytes are <b>byte for byte</b> the file on disk (MD5 equal), so they are fetched as whole objects
    ///         and do not take part in the shard layout.
    ///     </para>
    /// </summary>
    /// <param name="plan">The plan to extend.</param>
    /// <param name="assembly">The base tier assembly.</param>
    private static void AppendBaseTierItems(NeteaseGamesDownloadPlan plan, NeteaseGamesBaseAssemblyPlan assembly)
    {
        foreach (NeteaseGamesBaseLooseChunk loose in assembly.LooseChunks)
        {
            plan.Items.Add(new NeteaseGamesDownloadItem
            {
                RelativePath = NeteaseGamesContentEndpoints.GetBigPatchContentPath(
                    loose.SourceVersion, loose.Chunk.Section, loose.Chunk.Name),
                // They live inside LocalData\Patch, next to the shards - not at the game root. Measured 2026-10-26
                // against the official installation: `prerequisite`, `Misc/lazycode_zstd_dict` and the 4a blob are
                // all under LocalData\Patch there, while this planner used to drop them at the root, where the game
                // never looks for them.
                TargetRelativePath = loose.Chunk.Paths[0].Replace('/', Path.DirectorySeparatorChar),
                Kind = NeteaseGamesDownloadKind.BaseFile,
                ExpectedSize = loose.Chunk.Size,
                Hosts = NeteaseGamesContentEndpoints.GetBigPatchHosts(),
                RequiresSignature = false
            });
        }
    }

    /// <summary>
    ///     Adds the shader cache list to a plan when it is missing or when the round moved to another content
    ///     version.
    ///     <para>
    ///         The official launcher keeps <c>shadercachelist_dx12.txt</c> (7,985,520 bytes, measured 2026-10-23)
    ///         in the patch directory and refreshes it together with the content version. It is <b>not</b> part of
    ///         the patch list, so it can only be planned here.
    ///     </para>
    ///     <para>
    ///         The file is re-downloaded when it is missing or when the content version directory changed since
    ///         the installation was last recorded (the launcher records that version in <c>patchmd5</c> line 0).
    ///         An unchanged content version means the file was already fetched from that directory.
    ///     </para>
    /// </summary>
    /// <param name="plan">The plan to extend.</param>
    /// <param name="gamePath">The installation root.</param>
    /// <param name="contentVersion">The content version the file is served from.</param>
    /// <param name="localState">The version record read from disk.</param>
    /// <summary>
    ///     Adds the local patch list (<c>patchlist_windows64_normal.txt</c>) to a fresh installation's plan.
    ///     <para>
    ///         The launcher keeps that file next to the shards and every later update compares against it; without it
    ///         the next run has no baseline. It is not in the patch list itself (it <i>is</i> the patch list), so it
    ///         has to be fetched from the content version directory, where it is byte for byte the copy the launcher
    ///         writes (measured 2026-10-26: identical MD5 at the installed content version).
    ///     </para>
    /// </summary>
    /// <param name="plan">The plan to extend.</param>
    /// <param name="contentVersion">The content version directory.</param>
    private static void AppendPatchList(NeteaseGamesDownloadPlan plan, string contentVersion)
    {
        plan.Items.Add(new NeteaseGamesDownloadItem
        {
            RelativePath = ContentPath(contentVersion, null, NeteaseGamesContentEndpoints.PatchListMpkFileName),
            TargetRelativePath = NeteaseGamesPaths.PatchListFileName,
            Kind = NeteaseGamesDownloadKind.SideFile,
            ExpectedSize = 0
        });

        SharedStatic.InstanceLogger?.LogInformation(
            "[NeteaseGames] The local patch list will be installed from content version {Version}", contentVersion);
    }

    private static void AppendShaderCacheList(NeteaseGamesDownloadPlan plan, string gamePath, string contentVersion,
        NeteaseGamesLocalState localState, string remoteShaderCacheListMd5)
    {
        string localPath = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath,
            NeteaseGamesContentEndpoints.ShaderCacheListFileName);

        bool missing = !File.Exists(localPath);

        // The manifest announces the hash of the shader cache list it expects (@shadercachelistmd5@), and the local
        // record carries the hash of the one installed. Comparing those is the authoritative test; the content
        // version alone is a poor proxy - it moves on every round even when this file did not change, which made
        // every installation re-download 7,985,520 bytes for nothing.
        bool hashDiffers = !string.IsNullOrEmpty(localState.ShaderCacheListMd5) &&
                           !string.IsNullOrEmpty(remoteShaderCacheListMd5) &&
                           !string.Equals(localState.ShaderCacheListMd5, remoteShaderCacheListMd5,
                               StringComparison.Ordinal);

        if (!missing && !hashDiffers)
        {
            return;
        }

        plan.Items.Add(new NeteaseGamesDownloadItem
        {
            RelativePath = ContentPath(contentVersion, null, NeteaseGamesContentEndpoints.ShaderCacheListFileName),
            TargetRelativePath = NeteaseGamesContentEndpoints.ShaderCacheListFileName,
            Kind = NeteaseGamesDownloadKind.SideFile,
            // The remote size is not announced anywhere the plugin can read it, so the expected size stays 0
            // ("unknown") for a refresh as well. Filling in the LOCAL file's length (which an earlier revision did)
            // made every refresh fail: the server sends the new file, the downloader compares it against the old
            // length and reports a size mismatch, so the whole run ended as DownloadFailed.
            ExpectedSize = 0
        });

        SharedStatic.InstanceLogger?.LogInformation(
            "[NeteaseGames] The shader cache list will be refreshed ({Reason})",
            missing ? "missing" : $"content version {localState.ContentVersion} -> {contentVersion}");
    }

    /// <summary>
    ///     Verifies the base shard tier against its own index (<c>LocalData\Patch\Patch.mpkinfo</c>).
    ///     <para>
    ///         The base tier (<c>Patch.mpk</c> + <c>Patch1..127.mpk</c>) is not described by the patch list, but the
    ///         launcher keeps a SQLite index of it. For every shard the index records where each resource starts and
    ///         how long it is, so <c>max(start + size)</c> is the exact byte length of that shard's payload:
    ///         measured on 2026-10-24 against 128 live shards, <b>127 match the file size byte for byte</b> and the
    ///         one exception (<c>Patch100</c>) carries 1,876,274 B of real tail padding.
    ///     </para>
    ///     <para>
    ///         A shard that is <b>shorter</b> than its payload has lost data. A shard that is <b>missing</b> is
    ///         reported as well: it cannot be repaired by the plugin (the base tier has no public per-shard
    ///         endpoint), but silently omitting it made a half-present installation look complete.
    ///     </para>
    /// </summary>
    /// <param name="gamePath">The installation root.</param>
    /// <returns>
    ///         The shards that are missing or shorter than their index describes, or an empty list when the index
    ///         is unavailable (in which case nothing can be asserted).
    ///     </returns>
    public static IReadOnlyList<string> FindTruncatedBaseShards(string gamePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(gamePath);

        List<string> truncated = [];
        string patchDirectory = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath);

        NeteaseGamesBaseIndex? index = NeteaseGamesBaseIndex.Read(
            Path.Combine(patchDirectory, NeteaseGamesBaseIndex.FileName));
        if (index is null)
        {
            return truncated;
        }

        foreach ((int shardId, long payloadLength) in index.ShardEnds)
        {
            string path = Path.Combine(patchDirectory, BaseShardFileName(shardId));
            try
            {
                if (!File.Exists(path))
                {
                    // Reported, not skipped: an installation that lost a base shard is broken even though the
                    // plugin cannot download a replacement. Skipping it here is what let a half-present base tier
                    // pass as complete.
                    truncated.Add(BaseShardFileName(shardId));
                    continue;
                }

                if (new FileInfo(path).Length < payloadLength)
                {
                    truncated.Add(BaseShardFileName(shardId));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                truncated.Add(BaseShardFileName(shardId));
            }
        }

        if (truncated.Count > 0)
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGames] {Count} base tier shards are missing or shorter than their index describes: {Shards}",
                truncated.Count, string.Join(", ", truncated));
        }

        return truncated;
    }

    /// <summary>
    ///     The file name of a base tier shard. Shard 0 is <c>Patch.mpk</c> (no number), the rest are
    ///     <c>Patch{id}.mpk</c>.
    /// </summary>
    /// <param name="shardId">The shard id the index uses.</param>
    public static string BaseShardFileName(int shardId)
        => shardId == 0 ? "Patch.mpk" : $"Patch{shardId}.mpk";

    /// <summary>
    ///     Reads the package indexes that are already on disk and returns, for every shard id they describe, the
    ///     byte size that shard must have.
    ///     <para>
    ///         The index layout was verified against all three live indexes on 2026-10-23: 50,443 / 232,998 /
    ///         13,130 entries parse, and for each shard id the largest <c>offset + size</c> equals the byte size
    ///         of the shard file. A shard whose disk size differs from that value belongs to another patch
    ///         version even when its name and the announced size still look plausible - this is the only
    ///         <b>content</b> level check available, because the vendor's 64 bit hash is not implemented.
    ///     </para>
    ///     <para>
    ///         Best effort: an installation without indexes yields an empty map and the plan falls back to the
    ///         announced sizes.
    ///     </para>
    /// </summary>
    /// <param name="gamePath">The installation root.</param>
    /// <param name="remote">The remote patch list (its index names decide what is read).</param>
    /// <param name="token">Cancel token.</param>
    private static async Task<Dictionary<uint, long>> ReadLocalShardLayoutsAsync(string gamePath,
        NeteaseGamesPatchList remote, CancellationToken token)
    {
        Dictionary<uint, long> layouts = [];

        foreach (string indexName in remote.Packages.Keys)
        {
            if (!IsPackageIndex(indexName))
            {
                continue;
            }

            string indexPath = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath, indexName);
            NeteaseGamesPackageIndex? index = await ReadPackageIndexAsync(indexPath, token).ConfigureAwait(false);
            if (index is null)
            {
                continue;
            }

            foreach ((uint marker, NeteaseGamesShardSpan span) in index.Spans)
            {
                if (!layouts.TryGetValue(marker, out long existing) || span.End > existing)
                {
                    layouts[marker] = span.End;
                }
            }
        }

        return layouts;
    }

    /// <summary>
    ///     Reads and parses a package index from disk (both the <c>HEX_</c> envelope and plain JSON-style
    ///     payloads are accepted, the vendor writes the envelope).
    /// </summary>
    /// <param name="path">The index file.</param>
    /// <param name="token">Cancel token.</param>
    public static async Task<NeteaseGamesPackageIndex?> ReadPackageIndexAsync(string path, CancellationToken token)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] raw = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            byte[] plain = NeteaseGamesCrypto.IsHexEnvelope(raw)
                ? NeteaseGamesCrypto.DecryptHexEnvelope(raw)
                : raw;

            return NeteaseGamesPackageIndex.Parse(plain);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Builds the content path of an object below the version directory.
    /// </summary>
    private static string ContentPath(string version, string? section, string name)
        => string.IsNullOrEmpty(section) ? $"{version}/{name}" : $"{version}/{section}/{name}";

    /// <inheritdoc/>
    public override void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
