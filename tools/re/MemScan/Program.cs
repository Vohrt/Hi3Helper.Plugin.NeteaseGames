using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

// Scans a process's committed memory for a marker and dumps surrounding regions.
class MemScan
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, IntPtr baseAddr, byte[] buf, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION64 mbi, int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }

    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint PROCESS_VM_READ = 0x0010;
    const uint MEM_COMMIT = 0x1000;
    const uint PAGE_GUARD = 0x100;
    const uint PAGE_NOACCESS = 0x01;

    static void Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: MemScan <pid> <marker> <outdir> [contextBytes]");
            return;
        }
        int pid = int.Parse(args[0]);
        byte[] marker = Encoding.ASCII.GetBytes(args[1]);
        string outDir = args[2];
        int ctx = args.Length > 3 ? int.Parse(args[3]) : 4096;
        Directory.CreateDirectory(outDir);

        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero)
        {
            Console.WriteLine($"OpenProcess failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        ulong addr = 0;
        int regionIndex = 0;
        int hits = 0;
        while (true)
        {
            MEMORY_BASIC_INFORMATION64 mbi;
            if (VirtualQueryEx(h, (IntPtr)addr, out mbi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION64>()) == 0)
                break;
            addr = mbi.BaseAddress + mbi.RegionSize;

            if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0)
                continue;
            if (mbi.RegionSize > 512UL * 1024 * 1024)
                continue;

            byte[] buf = new byte[mbi.RegionSize];
            int read;
            if (!ReadProcessMemory(h, (IntPtr)mbi.BaseAddress, buf, (int)mbi.RegionSize, out read) || read <= 0)
                continue;
            Array.Resize(ref buf, read);

            int idx = 0;
            while ((idx = IndexOf(buf, marker, idx)) >= 0)
            {
                hits++;
                int start = Math.Max(0, idx - ctx);
                int len = Math.Min(buf.Length - start, ctx * 2 + marker.Length);
                string name = Path.Combine(outDir, $"hit_{regionIndex}_{idx:X8}.bin");
                File.WriteAllBytes(name, buf.Skip(start).Take(len).ToArray());
                Console.WriteLine($"hit @ 0x{mbi.BaseAddress + (ulong)idx:X} -> {name}");
                idx += marker.Length;
                if (hits >= 200) { Console.WriteLine("hit limit"); CloseHandle(h); return; }
            }
            regionIndex++;
        }
        CloseHandle(h);
        Console.WriteLine($"done, {hits} hits");
    }

    static int IndexOf(byte[] hay, byte[] needle, int start)
    {
        for (int i = start; i <= hay.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
