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
///         <see cref="InitAsync" />, which is the only hook the host awaits during startup.
///     </para>
/// </summary>
[GeneratedComClass]
public partial class NeteaseGamesGameManager : GameManagerBase
{
    private readonly object _syncRoot = new();
    private NeteaseGamesMiniPackManifest? _remoteManifest;
    private long _remotePackageVersion;
    private bool _remoteProbed;

    [field: AllowNull]
    [field: MaybeNull]
    protected override HttpClient ApiResponseHttpClient
    {
        get
        {
            if (field is not null)
            {
                return field;
            }

            PluginHttpClientBuilder builder = new PluginHttpClientBuilder()
                .SetAllowedDecompression(System.Net.DecompressionMethods.All)
                .AllowRedirections()
                .AllowUntrustedCert()
                .AllowCookies()
                .SetUserAgent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CollapsePlugin/1.0");

            return field = builder.Create();
        }
        set;
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
        await ProbeRemoteVersionAsync(token).ConfigureAwait(false);
        return 0;
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
        CurrentGameVersion = state.PackageVersion > 0
            ? new GameVersion(0, 0, (int)state.PackageVersion, 0)
            : GameVersion.Empty;
    }

    /// <summary>
    ///     Fetches the remote version information (mini-pack manifest + remote patch manifest).
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

            _remoteProbed = true;
        }

        try
        {
            Uri manifestUri = NeteaseGamesEndpoints.GetMiniPackListUri(NeteaseGamesEndpoints.PatchListBaseUrl);
            byte[] payload = await ApiResponseHttpClient.GetByteArrayAsync(manifestUri, token).ConfigureAwait(false);
            NeteaseGamesMiniPackManifest manifest = NeteaseGamesMiniPackManifest.Parse(payload);

            lock (_syncRoot)
            {
                _remoteManifest = manifest;
            }

            SharedStatic.InstanceLogger?.LogInformation(
                "[NeteaseGames] Remote mini pack: version={Version}, engine={Engine}, blocks={Blocks}, size={Size}",
                manifest.CurrentVersion, manifest.EngineVersion, manifest.DownloadInfo.Count, manifest.TotalDownloadSize);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger?.LogWarning("[NeteaseGames] Failed to fetch remote mini pack manifest: {Msg}", ex.Message);
        }

        // The remote patch manifest carries the authoritative package version.
        try
        {
            long remotePackageVersion = await FetchRemotePackageVersionAsync(token).ConfigureAwait(false);
            if (remotePackageVersion > 0)
            {
                lock (_syncRoot)
                {
                    _remotePackageVersion = remotePackageVersion;
                }

                ApiGameVersion = new GameVersion(0, 0, (int)remotePackageVersion, 0);
                EnsureLocalVersionProbed();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger?.LogWarning("[NeteaseGames] Failed to fetch remote patch manifest: {Msg}", ex.Message);
        }
    }

    private async Task<long> FetchRemotePackageVersionAsync(CancellationToken token)
    {
        Uri uri = NeteaseGamesEndpoints.GetManifestUri(
            NeteaseGamesEndpoints.PatchListBaseUrl,
            NeteaseGamesEndpoints.PatchMd5ListName);

        byte[] raw = await ApiResponseHttpClient.GetByteArrayAsync(uri, token).ConfigureAwait(false);
        byte[] plain = NeteaseGamesCrypto.IsHexEnvelope(raw)
            ? NeteaseGamesCrypto.DecryptHexEnvelope(raw)
            : raw;

        string text = System.Text.Encoding.UTF8.GetString(plain);
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

        if (best > 0)
        {
            return best;
        }

        return ParsePackageVersionFromJson(text);
    }

    /// <summary>
    ///     Reads the package version from the JSON part of the patch manifest
    ///     (<c>normal.latest_pkgversion</c>).
    /// </summary>
    private static long ParsePackageVersionFromJson(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0)
        {
            return 0;
        }

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(text[start..]);
            if (document.RootElement.TryGetProperty("normal", out System.Text.Json.JsonElement normal) &&
                normal.TryGetProperty("latest_pkgversion", out System.Text.Json.JsonElement latest) &&
                latest.ValueKind == System.Text.Json.JsonValueKind.Number &&
                latest.TryGetInt64(out long version))
            {
                return version;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return 0;
        }

        return 0;
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
                catch (Exception)
                {
                    // Skip files that vanished while scanning.
                }
            }
        }
        catch (Exception)
        {
            // Return whatever we have counted so far.
        }

        return total;
    }
}

