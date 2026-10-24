using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
class Dump2 {
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, int p);
  [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr b, byte[] buf, int s, out int r);
  [DllImport("kernel32.dll")] static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, int s);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] struct MBI { public ulong B; public ulong Ab; public uint Ap; public uint a1; public ulong Rs; public uint St; public uint Pr; public uint T; public uint a2; }
  static void Main(string[] a) {
    IntPtr h = OpenProcess(0x0410, false, int.Parse(a[0]));
    if (h == IntPtr.Zero) { Console.WriteLine("open fail " + Marshal.GetLastWin32Error()); return; }
    byte[] mk = Encoding.ASCII.GetBytes(a[1]);
    string dir = a[2]; Directory.CreateDirectory(dir);
    ulong addr = 0; int i = 0;
    while (true) {
      MBI m; if (VirtualQueryEx(h, (IntPtr)addr, out m, Marshal.SizeOf<MBI>()) == 0) break;
      addr = m.B + m.Rs;
      if (m.St != 0x1000 || (m.Pr & 0x101) != 0) continue;
      if (m.Rs > 1024UL*1024*1024) continue;
      byte[] buf = new byte[m.Rs]; int rd;
      if (!ReadProcessMemory(h, (IntPtr)m.B, buf, (int)m.Rs, out rd) || rd <= 0) continue;
      Array.Resize(ref buf, rd);
      int idx = Find(buf, mk);
      if (idx >= 0) File.WriteAllBytes(Path.Combine(dir, $"r{i}_{m.B:X}.bin"), buf);
      i++;
    }
    CloseHandle(h);
    Console.WriteLine("done");
  }
  static int Find(byte[] h, byte[] n) { for (int i = 0; i <= h.Length - n.Length; i++) { bool ok = true; for (int j = 0; j < n.Length; j++) if (h[i+j] != n[j]) { ok = false; break; } if (ok) return i; } return -1; }
}
