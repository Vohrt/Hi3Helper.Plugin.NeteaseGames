using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.NeteaseGames.Core.Management;

string game = args.Length > 0 ? args[0] : @"D:\ywzh\ywzh_extreme";
string patchDir = Path.Combine(game, NeteaseGamesPaths.PatchDirectoryRelativePath);

var index = NeteaseGamesBaseIndex.Read(Path.Combine(patchDir, NeteaseGamesBaseIndex.FileName));
if (index is null) { Console.WriteLine("base index: NOT READABLE"); return; }

Console.WriteLine($"base index rows : {index.EntryCount:N0}");
Console.WriteLine($"base shards     : {index.ShardEnds.Count}");
Console.WriteLine($"truncated shards: {NeteaseGamesGameInstaller.FindTruncatedBaseShards(game).Count}");

var installer = new NeteaseGamesGameInstaller(new NeteaseGamesGameManager());
Console.WriteLine($"plan builder    : {(installer is not null ? "ok" : "null")}");
