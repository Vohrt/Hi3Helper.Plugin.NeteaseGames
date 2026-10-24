using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
///     Watches a NetEase installation for the moment the launcher switches into base-shard ("fenbao") mode,
///     and preserves the evidence needed to reverse the merge rule.
///
///     The merge order of <c>PatchN.mpk</c> could not be derived from any on-disk file or from the manifests:
///     it depends on runtime state (<c>fenbao_migration_map</c>, <c>_pending_batches</c>) that only exists while a
///     round that rewrites the base tier is running. This tool waits for that round and, the moment it starts,
///     copies every artefact that can still be read afterwards:
///
///       - the patch directory listing and the small state files (fenbao_mode, pkgversion, gameversion, ...)
///       - the launcher's command line and a full memory dump of its regions that mention the fenbao machinery
///       - any file that appears under LocalData\Patch\tmp_file
///
///     Usage: FenbaoWatcher &lt;gameDir&gt; [intervalSeconds] [outDir]
///     The output directory receives timestamped evidence; nothing is written before a trigger fires.
/// </summary>
internal static class FenbaoWatcher
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size,
        out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation64 info,
        int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const ulong MaxRegionSize = 512UL * 1024 * 1024;

    /// <summary>The state files that describe the round, copied verbatim on every trigger.</summary>
    private static readonly string[] StateFiles =
    [
        "fenbao_mode", "pkgversion", "gameversion", "pkgcheck", "patch_info", "cur_driver",
        "dxmode", "latest_engine_ver", "patchmd5_windows64_normal.txt", "patchlist_windows64_normal.txt"
    ];

    /// <summary>Markers whose memory regions are dumped: the fenbao machinery and the base tier naming.</summary>
    private static readonly string[] MemoryMarkers =
    [
        "fenbao_migration_map", "_current_fenbao_name", "FenbaoTmpPatchMpk",
        "bigpatchlist_windows64", "big_common/"
    ];

    private static int Main(string[] args)
    {
        string gameDirectory = args.Length > 0 ? args[0] : @"D:\ywzh\ywzh_extreme";
        int interval = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 5;
        string outputDirectory = args.Length > 2
            ? args[2]
            : Path.Combine(Path.GetTempPath(), "NeteaseFenbaoEvidence");

        string patchDirectory = Path.Combine(gameDirectory, "LocalData", "Patch");
        Directory.CreateDirectory(outputDirectory);

        Log($"watching {patchDirectory}");
        Log($"evidence directory: {outputDirectory}");
        Log($"poll interval: {interval}s   (Ctrl+C to stop)");

        PreflightCheck();

        string lastMode = ReadMode(patchDirectory);
        List<string> lastListing = Listing(patchDirectory);
        Log($"baseline: fenbao_mode='{lastMode}', {lastListing.Count} files in the patch directory");

        int triggerCount = 0;
        while (true)
        {
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(interval));

            string mode = ReadMode(patchDirectory);
            List<string> listing = Listing(patchDirectory);
            string? reason = null;

            if (!string.Equals(mode, lastMode, StringComparison.Ordinal))
            {
                reason = $"fenbao_mode {lastMode} -> {mode}";
            }
            else if (listing.Count > lastListing.Count + 2)
            {
                reason = $"patch directory grew {lastListing.Count} -> {listing.Count}";
            }
            else if (Directory.Exists(Path.Combine(patchDirectory, "tmp_file")))
            {
                reason = "tmp_file appeared (base tier staging)";
            }

            lastMode = mode;
            lastListing = listing;

            if (reason is null)
            {
                continue;
            }

            triggerCount++;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string evidence = Path.Combine(outputDirectory, $"trigger{triggerCount:D2}_{stamp}");
            Directory.CreateDirectory(evidence);

            Log($"trigger: {reason}");
            Capture(patchDirectory, evidence, reason);
            Log($"evidence saved to {evidence}");
        }
    }

    /// <summary>
    ///     Verifies at startup that the launcher can actually be inspected, so a missing elevation is reported
    ///     now instead of the moment the evidence is needed.
    /// </summary>
    private static void PreflightCheck()
    {
        Process[] launchers = Process.GetProcessesByName("PCLauncher");
        if (launchers.Length == 0)
        {
            Log("preflight: PCLauncher is not running (or not visible; it runs elevated).");
            Log("preflight: start the launcher and this watcher from an elevated prompt to capture the merge rule.");
            foreach (Process process in launchers)
            {
                process.Dispose();
            }

            return;
        }

        foreach (Process launcher in launchers)
        {
            try
            {
                IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, launcher.Id);
                if (handle == IntPtr.Zero)
                {
                    Log($"preflight: cannot open PCLauncher ({launcher.Id}): error {Marshal.GetLastWin32Error()}.");
                    Log("preflight: run this watcher elevated, otherwise no memory dump can be taken.");
                    continue;
                }

                CloseHandle(handle);
                Log($"preflight: PCLauncher ({launcher.Id}) is readable; memory capture is armed.");
            }
            finally
            {
                launcher.Dispose();
            }
        }
    }

    private static string ReadMode(string patchDirectory)
    {
        try
        {
            string path = Path.Combine(patchDirectory, "fenbao_mode");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "<missing>";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "<unreadable>";
        }
    }

    private static List<string> Listing(string patchDirectory)
    {
        try
        {
            return [.. Directory.EnumerateFiles(patchDirectory)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Capture(string patchDirectory, string evidenceDirectory, string reason)
    {
        File.WriteAllText(Path.Combine(evidenceDirectory, "trigger.txt"),
            $"reason: {reason}\nwhen: {DateTime.Now:O}\n");

        // 1. State files and a listing, so the round can be replayed afterwards.
        try
        {
            foreach (string name in StateFiles)
            {
                string source = Path.Combine(patchDirectory, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(evidenceDirectory, name), overwrite: true);
                }
            }

            File.WriteAllLines(Path.Combine(evidenceDirectory, "listing.txt"), Listing(patchDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"  state copy failed: {ex.Message}");
        }

        // 2. Anything the launcher staged for the base tier.
        try
        {
            string tmp = Path.Combine(patchDirectory, "tmp_file");
            if (Directory.Exists(tmp))
            {
                List<string> staged = [.. Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories)];
                File.WriteAllLines(Path.Combine(evidenceDirectory, "tmp_file_listing.txt"), staged);
                Log($"  {staged.Count} staged files recorded (not copied; they can be huge)");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"  staging record failed: {ex.Message}");
        }

        // 3. The launcher's memory: the only place the merge order is ever materialised.
        //
        // The official launcher runs elevated (its manifest asks for administrator rights), so a watcher that is
        // not elevated cannot even see it in Process.GetProcessesByName. That is not an error to swallow: without
        // the memory dump the evidence is useless, so say exactly what to do.
        Process[] launchers = Process.GetProcessesByName("PCLauncher");
        if (launchers.Length == 0)
        {
            Log("  PCLauncher is not visible to this process.");
            Log("  -> the launcher runs elevated; start this watcher from an elevated prompt,");
            Log("     otherwise the memory dump (the only record of the merge order) cannot be taken.");
            return;
        }

        foreach (Process launcher in launchers)
        {
            try
            {
                File.WriteAllText(Path.Combine(evidenceDirectory, $"proclist_{launcher.Id}.txt"),
                    $"pid: {launcher.Id}\nstart: {launcher.StartTime:O}\n" +
                    $"working set: {launcher.WorkingSet64}\n");

                DumpRegions(launcher.Id, evidenceDirectory);
            }
            finally
            {
                launcher.Dispose();
            }
        }
    }

    private static void DumpRegions(int processId, string evidenceDirectory)
    {
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
        {
            Log($"  OpenProcess failed ({Marshal.GetLastWin32Error()}); run elevated");
            return;
        }

        try
        {
            int saved = 0;
            ulong address = 0;
            int regionIndex = 0;

            while (true)
            {
                if (VirtualQueryEx(handle, (IntPtr)address, out MemoryBasicInformation64 info,
                        Marshal.SizeOf<MemoryBasicInformation64>()) == 0)
                {
                    break;
                }

                address = info.BaseAddress + info.RegionSize;

                if (info.State != MemCommit || (info.Protect & (PageGuard | PageNoAccess)) != 0)
                {
                    continue;
                }

                if (info.RegionSize == 0 || info.RegionSize > MaxRegionSize)
                {
                    continue;
                }

                byte[] buffer = new byte[info.RegionSize];
                if (!ReadProcessMemory(handle, (IntPtr)info.BaseAddress, buffer, buffer.Length, out int read) ||
                    read <= 0)
                {
                    continue;
                }

                if (read < buffer.Length)
                {
                    Array.Resize(ref buffer, read);
                }

                foreach (string marker in MemoryMarkers)
                {
                    if (IndexOf(buffer, marker) < 0)
                    {
                        continue;
                    }

                    string name = $"mem_{regionIndex:D4}_{info.BaseAddress:X}_{Sanitize(marker)}.bin";
                    File.WriteAllBytes(Path.Combine(evidenceDirectory, name), buffer);
                    saved++;
                    Log($"  dumped {name} ({buffer.Length:N0} bytes)");
                    break;
                }

                regionIndex++;
            }

            Log($"  {saved} memory regions saved");
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static int IndexOf(byte[] haystack, string marker)
    {
        byte[] needle = Encoding.ASCII.GetBytes(marker);
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Sanitize(string marker)
        => marker.Replace('/', '_').Replace('\\', '_').Replace(':', '_');

    private static void Log(string message)
        => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
