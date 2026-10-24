using Hi3Helper.NeteaseGames.Core;

// Round-trip: take the live remote manifest, build the record the plugin would write, and verify every field the
// readers depend on survives.
using HttpClient http = NeteaseGamesHttp.CreateClient(TimeSpan.FromSeconds(60));
string remote = await http.GetStringAsync(NeteaseGamesEndpoints.PatchListBaseUrl + NeteaseGamesEndpoints.PatchMd5ListName);
NeteaseGamesPatchManifest parsed = NeteaseGamesPatchManifest.Parse(remote);
Console.WriteLine($"remote line0={parsed.ContentVersion} line3={parsed.PatchVersion}");

string record = NeteaseGamesLocalState.BuildLauncherRecord(remote, parsed.PatchVersion);
Console.WriteLine("--- rebuilt record header ---");
foreach (string line in record[..record.IndexOf('{')].Split('\n'))
{
    if (line.Trim().Length > 0) Console.WriteLine($"  [{line.Trim()}]");
}

string roundTripContent = NeteaseGamesLocalState.ReadPatchMd5ContentVersion(record);
long roundTripPatch = NeteaseGamesLocalState.ReadPatchMd5Version(record);
Console.WriteLine($"round-trip content={roundTripContent} patch={roundTripPatch}");
Console.WriteLine($"content preserved = {roundTripContent == parsed.ContentVersion}");
Console.WriteLine($"patch preserved   = {roundTripPatch == parsed.PatchVersion}");

// The envelope the launcher writes must decrypt back to the same text.
byte[] wrapped = NeteaseGamesCrypto.EncryptHexEnvelope(System.Text.Encoding.UTF8.GetBytes(record));
string unwrapped = System.Text.Encoding.UTF8.GetString(NeteaseGamesCrypto.DecryptHexEnvelope(wrapped));
Console.WriteLine($"envelope round-trip = {(unwrapped == record)}");
