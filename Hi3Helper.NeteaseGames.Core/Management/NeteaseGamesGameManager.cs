using System;
using System.Diagnostics.CodeAnalysis;
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
///     Detects the local installation of a NetEase game, exposes the local and the remote version and
///     reports whether an update is available.
///     <para>
///         Everything that the launcher UI asks for synchronously (installed? version?) is answered from the
///         local files only, so the plugin never blocks on the network. The remote version is fetched during
///         <see cref="InitAsync" />, which is the only hook the host awaits during startup - so the probe is
///         bounded and never keeps the launcher waiting for a slow network.
///     </para>
/// </summary>
[GeneratedComClass]
public partial class NeteaseGamesGameManager : GameManagerBase
{
    /// <summary>
    ///     The maximum time the startup probe may take. The host awaits <see cref="InitAsync" /> while the
    ///     region loads, so a hanging endpoint must not be able to delay it.
    /// </summary>
    public static readonly TimeSpan StartupProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly object _syncRoot = new();
    private readonly HttpClient _httpClient;
    private NeteaseGamesMiniPackManifest? _remoteManifest;
    private NeteaseGamesPatchManifest? _remotePatchManifest;
    private long _remotePackageVersion;
    private bool _remoteProbed;
    private bool _hasEngineUpdate;

    /// <summary>
    ///     Creates a game manager.
    /// </summary>
    public NeteaseGamesGameManager()
    {
        _httpClient = NeteaseGamesHttp.CreateClient();
    }

    /// <inheritdoc/>
    protected override HttpClient ApiResponseHttpClient
    {
        get => _httpClient;
        set { }
    }

    /// <summary>
    ///     The remote mini-pack manifest, when it could be fetched.
    /// </summary>
    public NeteaseGamesMiniPackManifest? RemoteManifest
    {
        get
        {
            lock (_syncRoot)
            {
                return _remoteManifest;
            }
        }
    }

    /// <summary>
    ///     Whether the engine of the installation is older than the engine the remote manifest announces.
    /// </summary>
    public bool HasEngineUpdate
    {
        get
        {
            lock (_syncRoot)
            {
                return _hasEngineUpdate;
            }
        }
    }

    /// <summary>
    ///     The package version announced by the remote patch manifest.
    /// </summary>
    public long RemotePackageVersion
    {
        get
        {
            lock (_syncRoot)
            {
                return _remotePackageVersion;
            }
        }
    }

    /// <summary>
    ///     The remote patch manifest, when it could be fetched.
    /// </summary>
    public NeteaseGamesPatchManifest? RemotePatchManifest
    {
        get
        {
            lock (_syncRoot)
            {
                return _remotePatchManifest;
            }
        }
    }

    /// <inheritdoc/>
    protected override bool IsInstalled
    {
        get
        {
            string? path = CurrentGameInstallPath;
            return !string.IsNullOrEmpty(path) && NeteaseGamesLocalState.IsGameDirectory(path);
        }
    }

    /// <inheritdoc/>
    protected override bool HasUpdate
    {
        get
        {
            if (!IsInstalled)
            {
                return false;
            }

            if (ApiGameVersion == GameVersion.Empty)
            {
                // The remote version has not been probed yet - never claim an update that we cannot prove.
                return false;
            }

            return CurrentGameVersion != ApiGameVersion;
        }
    }

    /// <inheritdoc/>
    protected override bool HasPreload => false;

    /// <inheritdoc/>
    protected override async Task<int> InitAsync(CancellationToken token)
    {
        await InitAsyncInternal(token).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    ///     Runs the bounded startup probe on behalf of the preset (whose own <c>InitAsync</c> is the hook the
    ///     host awaits).
    /// </summary>
    /// <param name="token">Cancel token.</param>
    public async Task InitAsyncInternal(CancellationToken token)
    {
        // Bounded: the host awaits this while the launcher UI loads.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(StartupProbeTimeout);
        await ProbeRemoteVersionAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <inheritdoc cref="GameManagerBase.FindExistingInstallPathAsyncInner"/>
    protected override Task<string?> FindExistingInstallPathAsyncInner(CancellationToken token)
    {
        string? existing = NeteaseGamesLocalState.FindExistingInstallPath();
        if (!string.IsNullOrEmpty(existing))
        {
            CurrentGameInstallPath = existing;
            EnsureLocalVersionProbed();
        }

        return Task.FromResult(existing);
    }

    /// <inheritdoc cref="GameManagerBase.SetGamePathInner"/>
    protected override void SetGamePathInner(string gamePath)
    {
        CurrentGameInstallPath = gamePath;
        NeteaseGamesLocalState.InvalidatePatchListCache(gamePath);
        EnsureLocalVersionProbed();
    }

    /// <inheritdoc cref="GameManagerBase.SetCurrentGameVersionInner"/>
    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion)
    {
        // The local version is always derived from the files on disk, so there is nothing to persist here.
    }

    /// <inheritdoc/>
    public override void LoadConfig()
    {
        // The plugin is stateless: every value is derived from the installation directory.
    }

    /// <inheritdoc/>
    public override void SaveConfig()
    {
        // The plugin is stateless: every value is derived from the installation directory.
    }

    /// <summary>
    ///     Reads the local version from disk (lazily, but synchronously - the host asks for it on the UI thread).
    /// </summary>
    public void EnsureLocalVersionProbed()
    {
        string? path = CurrentGameInstallPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        NeteaseGamesLocalState state = NeteaseGamesLocalState.Read(path);

        // The version the host compares against the remote one must be the same counter the remote manifest
        // reports: the resource/patch version. `pkgversion` is a different counter (the package version), and
        // comparing it against the remote resource version made the plugin claim a permanent update
        // (local 6521279 vs remote 6534552, while the installation was actually up to date).
        long localVersion = state.PatchVersion > 0 ? state.PatchVersion : state.PackageVersion;
        CurrentGameVersion = localVersion > 0
            ? new GameVersion(0, 0, (int)localVersion, 0)
            : GameVersion.Empty;

        UpdateEngineState(state);
    }

    /// <summary>
    ///     Compares the engine version of the installation with the remote one.
    /// </summary>
    /// <param name="state">The local state.</param>
    private void UpdateEngineState(NeteaseGamesLocalState state)
    {
        NeteaseGamesMiniPackManifest? manifest;
        lock (_syncRoot)
        {
            manifest = _remoteManifest;
        }

        if (manifest is null || string.IsNullOrEmpty(manifest.CurrentVersion))
        {
            return;
        }

        // The engine version of the installation is the one of the engine build.
        string local = state.EngineVersion;
        if (string.IsNullOrEmpty(local))
        {
            local = state.LatestEngineVersion;
        }

        bool update = !string.IsNullOrEmpty(local) &&
                      !string.Equals(local, manifest.CurrentVersion, StringComparison.Ordinal) &&
                      !string.Equals(local, manifest.EngineVersion, StringComparison.Ordinal);

        lock (_syncRoot)
        {
            _hasEngineUpdate = update;
        }
    }

    /// <summary>
    ///     Fetches the remote version information (mini-pack manifest + remote patch manifest).
    ///     Safe to call more than once: a successful probe is not repeated.
    /// </summary>
    /// <param name="token">Cancel token.</param>
    public async Task ProbeRemoteVersionAsync(CancellationToken token)
    {
        lock (_syncRoot)
        {
            if (_remoteProbed)
            {
                return;
            }
        }

        try
        {
            Uri manifestUri = NeteaseGamesEndpoints.GetMiniPackListUri(NeteaseGamesEndpoints.PatchListBaseUrl);
            byte[] payload = await _httpClient.GetByteArrayAsync(manifestUri, token).ConfigureAwait(false);
            NeteaseGamesMiniPackManifest manifest = NeteaseGamesMiniPackManifest.Parse(payload);

            lock (_syncRoot)
            {
                _remoteManifest = manifest;
            }

            SharedStatic.InstanceLogger?.LogInformation(
                "[NeteaseGames] Remote mini pack: version={Version}, engine={Engine}, blocks={Blocks}, size={Size}",
                manifest.CurrentVersion, manifest.EngineVersion, manifest.DownloadInfo.Count, manifest.TotalDownloadSize);

            if (!string.IsNullOrEmpty(CurrentGameInstallPath))
            {
                UpdateEngineState(NeteaseGamesLocalState.Read(CurrentGameInstallPath));
            }

            long remotePackageVersion = await FetchRemotePackageVersionAsync(token).ConfigureAwait(false);
            if (remotePackageVersion > 0)
            {
                lock (_syncRoot)
                {
                    _remotePackageVersion = remotePackageVersion;
                    // Only mark the probe as complete once the authoritative version is known: a timeout or
                    // a transient failure must be retried the next time the host asks.
                    _remoteProbed = true;
                }

                ApiGameVersion = new GameVersion(0, 0, (int)remotePackageVersion, 0);
                EnsureLocalVersionProbed();
            }
        }
        catch (OperationCanceledException)
        {
            SharedStatic.InstanceLogger?.LogInformation("[NeteaseGames] The remote version probe timed out");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException)
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[NeteaseGames] Failed to fetch the remote manifests: {Msg}", ex.Message);
        }
    }

    /// <summary>
    ///     Fetches the engine payload file list of the announced mini pack.
    /// </summary>
    /// <param name="looseFileParentDir">The payload directory.</param>
    /// <param name="token">Cancel token.</param>
    public Task<System.Collections.Generic.IReadOnlyList<NeteaseGamesEngineFile>> FetchEnginePayloadFileListAsync(
        string looseFileParentDir, CancellationToken token)
        => NeteaseGamesEnginePatcher.FetchFileListAsync(
            _httpClient,
            looseFileParentDir,
            NeteaseGamesContentEndpoints.GetEnginePayloadHosts(),
            token);

    private async Task<long> FetchRemotePackageVersionAsync(CancellationToken token)
    {
        Uri uri = NeteaseGamesEndpoints.GetManifestUri(
            NeteaseGamesEndpoints.PatchListBaseUrl,
            NeteaseGamesEndpoints.PatchMd5ListName);

        byte[] raw = await _httpClient.GetByteArrayAsync(uri, token).ConfigureAwait(false);
        byte[] plain = NeteaseGamesCrypto.IsHexEnvelope(raw)
            ? NeteaseGamesCrypto.DecryptHexEnvelope(raw)
            : raw;

        string text = System.Text.Encoding.UTF8.GetString(plain);
        NeteaseGamesPatchManifest manifest = NeteaseGamesPatchManifest.Parse(text);

        lock (_syncRoot)
        {
            _remotePatchManifest = manifest;
        }

        if (manifest.LatestPackageVersion > 0)
        {
            return manifest.LatestPackageVersion;
        }

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Line layout: <engine version>, <big patch md5>, <package version>, <private script version>.
        // Depending on the branch the package version is on the third or on the fourth line, so the
        // largest of the two numeric candidates is used.
        long best = 0;
        for (int i = 2; i < lines.Length && i < 4; i++)
        {
            if (long.TryParse(lines[i], out long candidate) && candidate > best)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    ///     Downloads the remote file list (the variant that contains the files of the mpk shards).
    /// </summary>
    /// <param name="version">The base version of the patch.</param>
    /// <param name="token">Cancel token.</param>
    public async Task<NeteaseGamesPatchList> FetchRemotePatchListAsync(string version, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);

        string url = NeteaseGamesContentEndpoints.GetContentUrl(
            version, null, NeteaseGamesContentEndpoints.PatchListMpkFileName, DateTimeOffset.UtcNow);

        byte[] raw = await _httpClient.GetByteArrayAsync(new Uri(url), token).ConfigureAwait(false);
        byte[] plain = NeteaseGamesCrypto.IsHexEnvelope(raw)
            ? NeteaseGamesCrypto.DecryptHexEnvelope(raw)
            : raw;

        return NeteaseGamesPatchList.Parse(plain);
    }

    /// <summary>
    ///     Computes the total size of the installation.
    /// </summary>
    /// <param name="gameDirectory">The installation directory.</param>
    public static long MeasureInstallSize(string gameDirectory)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(gameDirectory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip files that vanished while scanning.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Return whatever we have counted so far.
        }

        return total;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
