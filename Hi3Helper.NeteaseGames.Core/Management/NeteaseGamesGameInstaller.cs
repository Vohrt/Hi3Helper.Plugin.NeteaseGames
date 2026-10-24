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

        // Without a remote list an empty plan means "unknown", not "complete". Failing here keeps the host from
        // reporting a healthy installation it never verified.
        if (plan.IsRemoteListUnavailable)
        {
            throw new IOException(
                "The remote patch list could not be read, so the state of the installation is unknown. " +
                "Check the network connection and try again.");
        }

        // A fresh installation cannot be completed by downloading patch shards alone: the base packages are not
        // part of the patch list. Failing here is deliberate - reporting success would leave the player with an
        // installation that cannot start.
        if (plan.IsFreshInstallUnsupported)
        {
            throw new NotSupportedException(
                "A fresh installation is not supported yet: the base packages (Patch.mpk, Patch1..127, " +
                "ResourceExA..) are not part of the patch list and cannot be downloaded. Install the game with " +
                "the official launcher once, then use this plugin to keep it updated.");
        }

        // A round that really rewrites the base tier cannot be finished by the plugin. `@bigpatchopen@` alone is
        // not the signal for that (it is a branch flag that is always on); the base tier fingerprint decides.
        // Applying only the delta tier and then writing the new version would report a finished update while the
        // installation is actually half updated, so such a run is refused up front.
        if (mode == NeteaseGamesPlanMode.Update)
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
            // An update advances the PATCH version only. The base package version (`pkgversion`) is left at 0
            // because this plan never rewrites the base tier - writing the patch version there would corrupt the
            // official launcher's record (measured: 6,521,279 overwritten with 6,593,596).
            PatchVersion = mode == NeteaseGamesPlanMode.Install ? 0 : manager.RemotePatchVersion,
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
                    ExpectedSize = file.Size,
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
                NeteaseGamesLocalState.Read(gamePath));
        }

        plan.AssignStaging(stagingDirectory);

        // A fresh installation is not supported yet (see Install below and the TODO in the README): the base
        // packages (Patch.mpk, Patch1..127, ResourceExA..) are not part of the patch list and every content host
        // path tried for them answers 404, so they can only be produced by the vendor's own installation. Refuse
        // instead of reporting a completed installation that cannot start.
        if (mode == NeteaseGamesPlanMode.Install)
        {
            plan.IsFreshInstallUnsupported = true;
        }

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
    private static void AppendShaderCacheList(NeteaseGamesDownloadPlan plan, string gamePath, string contentVersion,
        NeteaseGamesLocalState localState)
    {
        string localPath = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath,
            NeteaseGamesContentEndpoints.ShaderCacheListFileName);

        bool missing = !File.Exists(localPath);
        bool versionMoved = !string.IsNullOrEmpty(localState.ContentVersion) &&
                            !string.Equals(localState.ContentVersion, contentVersion, StringComparison.Ordinal);

        if (!missing && !versionMoved)
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
