using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
    private string? _remotePatchManifestText;
    private long _remotePatchVersion;
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
    ///     The <b>patch</b> version announced by the remote patch manifest (line 3).
    ///     <para>
    ///         The name is historical: it is not the base package version. It is the counter an update advances
    ///         and the one the host compares against <c>CurrentGameVersion</c>.
    ///     </para>
    /// </summary>
    public long RemotePatchVersion
    {
        get
        {
            lock (_syncRoot)
            {
                return _remotePatchVersion;
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

    /// <summary>
    ///     The verbatim (decrypted) text of the remote patch manifest.
    ///     <para>
    ///         The official launcher keeps this file as its own record of the installed version, so it is stored
    ///         here and written back after a successful update.
    ///     </para>
    /// </summary>
    public string? RemotePatchManifestText
    {
        get
        {
            lock (_syncRoot)
            {
                return _remotePatchManifestText;
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

        // The engine version of the installation is the one of the engine build (`Engine\Config\Built.version`).
        // `latest_engine_ver` is only a fallback: it records the timestamp of the last applied mini pack, not the
        // build identity.
        string local = state.EngineVersion;
        if (string.IsNullOrEmpty(local))
        {
            local = state.LatestEngineVersion;
        }

        bool update = !string.IsNullOrEmpty(local) &&
                      !EngineVersionsMatch(local, manifest.CurrentVersion) &&
                      !EngineVersionsMatch(local, manifest.EngineVersion);

        lock (_syncRoot)
        {
            _hasEngineUpdate = update;
        }
    }

    /// <summary>
    ///     Compares two engine version tokens, tolerating the suffix forms the two writers use.
    ///     <para>
    ///         `Built.version` is <c>261014173721_4a476f25b_release</c> and the mini pack announces the same value,
    ///         while the game client's <c>versiondata.json</c> records
    ///         <c>261014173721_4a476f25b_release_r0000_6ad03d31</c> - the same build with a revision tail. A plain
    ///         string comparison reports "different build" for what is in fact the same engine.
    ///     </para>
    /// </summary>
    /// <param name="local">The local token.</param>
    /// <param name="remote">The remote token.</param>
    public static bool EngineVersionsMatch(string? local, string? remote)
    {
        if (string.IsNullOrEmpty(local) || string.IsNullOrEmpty(remote))
        {
            return false;
        }

        string left = StripEngineRevision(local);
        string right = StripEngineRevision(remote);
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        // Some shapes only carry the numeric build stamp (e.g. `latest_engine_ver` = 202610151759); accept a
        // prefix match in that case so a timestamp against a full token does not look like a build change.
        return left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Removes the client's <c>_r0000_(hash)</c> revision tail from an engine version token.
    /// </summary>
    /// <param name="version">The token.</param>
    public static string StripEngineRevision(string version)
    {
        int marker = version.IndexOf("_r", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? version[..marker] : version;
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

            long remotePatchVersion = await FetchRemotePatchVersionAsync(token).ConfigureAwait(false);
            if (remotePatchVersion > 0)
            {
                lock (_syncRoot)
                {
                    _remotePatchVersion = remotePatchVersion;
                    // Only mark the probe as complete once the authoritative version is known: a timeout or
                    // a transient failure must be retried the next time the host asks.
                    _remoteProbed = true;
                }

                ApiGameVersion = new GameVersion(0, 0, (int)remotePatchVersion, 0);
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

    /// <summary>
    ///     Fetches the base tier manifest (<c>bigpatchlist_windows64_normal.txt</c>) from one <c>big_</c> directory.
    /// </summary>
    /// <param name="directoryVersion">
    ///     The directory version (the part after <c>big_</c>). A fresh installation has to try <b>both</b> the base
    ///     version and the content version, because neither serves the whole pool on its own.
    /// </param>
    /// <param name="token">Cancel token.</param>
    /// <returns>The parsed manifest, or <c>null</c> when the directory does not serve one.</returns>
    public async Task<NeteaseGamesBigPatchList?> FetchBigPatchListAsync(string directoryVersion,
        CancellationToken token)
    {
        if (string.IsNullOrEmpty(directoryVersion))
        {
            return null;
        }

        string path = NeteaseGamesContentEndpoints.GetBigPatchContentPath(
            directoryVersion, section: null, NeteaseGamesBigPatchList.FileName);

        byte[]? raw = await GetContentBytesAsync(path, token).ConfigureAwait(false);
        if (raw is null || raw.Length == 0)
        {
            return null;
        }

        try
        {
            return NeteaseGamesBigPatchList.Parse(raw);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Downloads one content object, trying every content host in turn.
    /// </summary>
    /// <param name="relativePath">The path relative to the content root.</param>
    /// <param name="token">Cancel token.</param>
    /// <returns>The bytes, or <c>null</c> when no host served the object.</returns>
    private async Task<byte[]?> GetContentBytesAsync(string relativePath, CancellationToken token)
    {
        List<string> roots =
        [
            NeteaseGamesEndpoints.ContentHostUrl,
            .. NeteaseGamesContentEndpoints.ContentMirrorHosts.Select(static h => "https://" + h + "/")
        ];

        foreach (string root in roots)
        {
            string url = root.TrimEnd('/') + "/" + relativePath.TrimStart('/');
            try
            {
                using System.Net.Http.HttpResponseMessage response =
                    await _httpClient.GetAsync(url, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException)
            {
                // Try the next host.
            }
        }

        return null;
    }

    private async Task<long> FetchRemotePatchVersionAsync(CancellationToken token)
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

            // Keep the verbatim manifest: it becomes the launcher's own record once an update succeeds (the
            // official launcher stores this file as-is and compares its fourth line).
            _remotePatchManifestText = text;
        }

        if (manifest.PatchVersion > 0)
        {
            return manifest.PatchVersion;
        }

        // Parsing already reads line 3; this is only reached for a malformed manifest.
        SharedStatic.InstanceLogger?.LogWarning(
            "[NeteaseGames] The remote manifest carries no usable patch version: '{Text}'",
            text.Length > 200 ? text[..200] : text);

        return 0;
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
