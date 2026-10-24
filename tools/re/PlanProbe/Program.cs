using System.Diagnostics;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;

// End-to-end probe: reads the live remote manifests and the real installation, then builds the same plan the
// plugin builds, printing every number so it can be compared against the official launcher's state.

string gamePath = args.Length > 0 ? args[0] : @"D:\ywzh\ywzh_extreme";

Console.WriteLine($"== installation: {gamePath} ==");
NeteaseGamesLocalState state = NeteaseGamesLocalState.Read(gamePath);
Console.WriteLine($"local patch version : {state.PatchVersion}");
Console.WriteLine($"local pkg version   : {state.PackageVersion}");
Console.WriteLine($"gameversion token   : {state.GameVersionToken}");
Console.WriteLine($"engine version      : {state.EngineVersion}");
Console.WriteLine($"shader version      : {state.ShaderVersion}");

Console.WriteLine();
Console.WriteLine("== remote manifests ==");
using HttpClient http = NeteaseGamesHttp.CreateClient(TimeSpan.FromSeconds(60));

string manifestUrl = NeteaseGamesEndpoints.PatchListBaseUrl + NeteaseGamesEndpoints.PatchMd5ListName;
string remoteText = await http.GetStringAsync(manifestUrl);
NeteaseGamesPatchManifest remote = NeteaseGamesPatchManifest.Parse(remoteText);
Console.WriteLine($"content version (line 0): {remote.ContentVersion}");
Console.WriteLine($"patch version   (line 3): {remote.PatchVersion}");
Console.WriteLine($"base version (@metadata): {remote.BaseVersion}");
Console.WriteLine($"engine version          : {remote.EngineVersion}");
Console.WriteLine($"needs update            : {remote.PatchVersion != state.PatchVersion}");

Console.WriteLine();
Console.WriteLine("== remote patch list (content version directory) ==");
NeteaseGamesGameManager manager = new();
manager.SetGamePath(gamePath);
manager.ProbeRemoteVersionAsync(CancellationToken.None).GetAwaiter().GetResult();
NeteaseGamesPatchList list = manager.FetchRemotePatchListAsync(remote.ContentVersion, CancellationToken.None)
    .GetAwaiter().GetResult();
Console.WriteLine($"packages: {list.Packages.Count}  loose: {list.LooseFiles.Count}  mapping: {list.MappingVersion}");

Dictionary<uint, long> layouts = [];
foreach (string indexName in list.Packages.Keys)
{
    if (!indexName.EndsWith(".mpkinfo", StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    string indexPath = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath, indexName);
    NeteaseGamesPackageIndex? index = await NeteaseGamesGameInstaller.ReadPackageIndexAsync(indexPath, CancellationToken.None);
    if (index is null)
    {
        Console.WriteLine($"  index {indexName}: UNREADABLE");
        continue;
    }

    Console.WriteLine($"  index {indexName}: entries={index.Entries.Count} shards={index.Spans.Count}");
    foreach ((uint marker, NeteaseGamesShardSpan span) in index.Spans)
    {
        if (!layouts.TryGetValue(marker, out long existing) || span.End > existing)
        {
            layouts[marker] = span.End;
        }
    }
}

Dictionary<string, uint> markers = NeteaseGamesGameInstaller.BuildShardMarkerMap(list);

Console.WriteLine();
Console.WriteLine("== shard verification (disk size vs index layout vs remote size) ==");
int ok = 0, sizeMismatch = 0, layoutMismatch = 0, missing = 0;
foreach ((string name, NeteaseGamesPackageEntry entry) in list.Packages)
{
    string path = Path.Combine(gamePath, NeteaseGamesPaths.PatchDirectoryRelativePath, name);
    long disk = File.Exists(path) ? new FileInfo(path).Length : -1;

    string layout = markers.TryGetValue(name, out uint marker) && layouts.TryGetValue(marker, out long expected)
        ? expected.ToString()
        : "-";

    string verdict;
    if (disk < 0) { missing++; verdict = "MISSING"; }
    else if (disk != entry.TotalSize) { sizeMismatch++; verdict = "SIZE"; }
    // The index records how far the payload reaches, not the file length: a shard may carry tail padding
    // (measured: Patch1009.mpk is 17,985,424 bytes while its payload ends at 17,923,658), so only "shorter
    // than its payload" is a defect.
    else if (layout != "-" && disk < long.Parse(layout)) { layoutMismatch++; verdict = "TRUNCATED"; }
    else { ok++; verdict = "ok"; }

    if (verdict != "ok")
    {
        Console.WriteLine($"  {name,-24} disk={disk,12} remote={entry.TotalSize,12} index={layout,12} -> {verdict}");
    }
}

Console.WriteLine($"  ok={ok} missing={missing} sizeMismatch={sizeMismatch} layoutMismatch={layoutMismatch}");

Console.WriteLine();
Console.WriteLine("== package tier cluster (mpk_filelist.info) ==");
string clusterPath = Path.Combine(gamePath, NeteaseGamesShardCluster.FileName);
if (File.Exists(clusterPath))
{
    byte[] raw = File.ReadAllBytes(clusterPath);
    byte[] plain = NeteaseGamesCrypto.IsHexEnvelope(raw) ? NeteaseGamesCrypto.DecryptHexEnvelope(raw) : raw;
    NeteaseGamesShardCluster cluster = NeteaseGamesShardCluster.Parse(System.Text.Encoding.UTF8.GetString(plain));
    Console.WriteLine($"  fingerprint: {cluster.Fingerprint}");
    int good = 0, bad = 0;
    foreach (NeteaseGamesShardCluster.FileEntry file in cluster.Files)
    {
        string path = Path.Combine(gamePath, file.Name);
        if (!File.Exists(path)) { bad++; Console.WriteLine($"  {file.Name}: MISSING"); continue; }
        long size = new FileInfo(path).Length;
        string md5 = await NeteaseGamesDownloader.ComputeMd5Async(path, CancellationToken.None);
        bool matches = size == file.Size && md5.Equals(file.Md5, StringComparison.OrdinalIgnoreCase);
        if (matches) { good++; } else { bad++; Console.WriteLine($"  {file.Name}: size {size}/{file.Size} md5 {md5}/{file.Md5}"); }
    }

    Console.WriteLine($"  verified={good} failed={bad}");
}
else
{
    Console.WriteLine("  (no cluster manifest)");
}

Console.WriteLine();
Console.WriteLine("== engine payload ==");
NeteaseGamesMiniPackManifest? mini = manager.RemoteManifest;
if (mini is not null)
{
    Console.WriteLine($"  current={mini.CurrentVersion} engine={mini.EngineVersion} blocks={mini.DownloadInfo.Count}");
    IReadOnlyList<NeteaseGamesEngineFile> files = manager
        .FetchEnginePayloadFileListAsync(mini.LooseFileParentDir, CancellationToken.None).GetAwaiter().GetResult();
    int present = 0, absent = 0, wrong = 0, del = 0;
    foreach (NeteaseGamesEngineFile file in files)
    {
        if (NeteaseGamesEnginePatcher.IsDeleteMarker(file)) { del++; continue; }
        string path = Path.Combine(gamePath, NeteaseGamesEnginePatcher.EngineDirectoryName,
            file.TargetName.Replace('\\', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) { absent++; continue; }
        if (new FileInfo(path).Length == file.Size) { present++; } else { wrong++; }
    }

    Console.WriteLine($"  engine files: listed={files.Count} present={present} missing={absent} wrongSize={wrong} deleteMarkers={del}");
}

Console.WriteLine();
Console.WriteLine("== plan (plugin view) ==");
Stopwatch watch = Stopwatch.StartNew();
NeteaseGamesGameInstaller installer = new(manager);
NeteaseGamesDownloadPlan plan = installer
    .BuildPlanAsync(gamePath, NeteaseGamesPlanMode.Update, CancellationToken.None)
    .GetAwaiter().GetResult();
watch.Stop();

Console.WriteLine($"  mode=Update items={plan.Items.Count} packages={plan.PackageCount} indexes={plan.IndexCount} " +
                  $"loose={plan.LooseCount} bytes={plan.TotalSize} remoteListUnavailable={plan.IsRemoteListUnavailable}");
foreach (NeteaseGamesDownloadItem item in plan.Items.Take(20))
{
    Console.WriteLine($"    {item.Kind,-9} {item.TargetRelativePath,-28} {item.ExpectedSize}");
}

Console.WriteLine($"  built in {watch.ElapsedMilliseconds} ms");
