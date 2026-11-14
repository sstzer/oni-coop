using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Run two ONI instances on one PC for local multiplayer testing, without touching game files.
//
// Unity's "force single instance" player setting makes every ONI process create a named mutex
// ("...-SingleInstanceMutex-...") at startup and refuse to start if it already exists. The check only
// happens at startup, so once instance A is up we close that mutex handle inside A (same technique as
// Sysinternals `handle -c`; no admin needed because A runs as the same user) and start instance B.
//
//   oni-multilaunch mutexes <pid>             list named mutexes held by a process
//   oni-multilaunch unlock <pid>              close its single-instance mutex
//   oni-multilaunch two [exe] [logdir]        start A, unlock it, start B (windowed, separate logs)
//
// Extra game arguments can follow: "--" for both instances, "--a" for A only, "--b" for B only, e.g.
//   oni-multilaunch two -- -coop-token t --a -coop-role host --b -coop-role guest
static class Program
{
    const string DefaultExe = @"C:\Program Files (x86)\Steam\steamapps\common\OxygenNotIncluded\OxygenNotIncluded.exe";
    const string DefaultLogDir = @"C:\Users\DELL\Documents\ONI-local-test";
    const string Marker = "SingleInstanceMutex";

    static int Main(string[] args)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "mutexes":
                    foreach (var (_, name) in Handles.Mutexes(int.Parse(args[1]))) Console.WriteLine(name);
                    return 0;
                case "unlock":
                    return Unlock(int.Parse(args[1]), TimeSpan.Zero) ? 0 : 3;
                case "two":
                    return Two(args);
                default:
                    Console.Error.WriteLine("usage: oni-multilaunch mutexes <pid> | unlock <pid> | two [exe] [logdir] [-- args] [--a args] [--b args]");
                    return 1;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 2;
        }
    }

    /// <summary>Windows command-line quoting (CommandLineToArgvW rules).</summary>
    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0) return a;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in a)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    static void WaitForLog(string file, string marker, TimeSpan max)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < max)
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (new StreamReader(fs).ReadToEnd().Contains(marker))
                {
                    Console.WriteLine($"instance A ready after {sw.Elapsed.TotalSeconds:F0} s");
                    return;
                }
            }
            catch (IOException) { }
            Thread.Sleep(500);
        }
        Console.WriteLine($"'{marker}' not seen in {file} after {max.TotalSeconds:F0} s, starting B anyway");
    }

    static bool Unlock(int pid, TimeSpan wait)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            foreach (var (handle, name) in Handles.Mutexes(pid))
            {
                if (!name.Contains(Marker)) continue;
                Handles.CloseRemote(pid, handle);
                Console.WriteLine($"closed '{name}' in pid {pid}");
                return true;
            }
            Thread.Sleep(500);
        } while (sw.Elapsed < wait);
        Console.Error.WriteLine($"no *{Marker}* mutex found in pid {pid}");
        return false;
    }

    static int Two(string[] args)
    {
        var pos = new List<string>();
        var extra = new Dictionary<string, List<string>> { ["--"] = [], ["--a"] = [], ["--b"] = [] };
        List<string> cur = pos;
        foreach (string arg in args[1..])
        {
            if (extra.TryGetValue(arg, out var section)) cur = section;
            else cur.Add(arg);
        }
        string exe = pos.Count > 0 ? pos[0] : DefaultExe;
        string logDir = pos.Count > 1 ? pos[1] : DefaultLogDir;
        Directory.CreateDirectory(logDir);

        Process Start(string tag)
        {
            var gameArgs = new[] { "-logFile", Path.Combine(logDir, $"instance{tag}.log"),
                                   "-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "800" }
                           .Concat(extra["--"]).Concat(extra["--" + tag.ToLowerInvariant()]).ToList();
            // Shell execute: the game must not inherit our stdout. When this tool runs from WSL, an inherited pipe
            // keeps the calling shell waiting until both games exit.
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = true,
                Arguments = string.Join(' ', gameArgs.Select(Quote)),
            };
            var p = Process.Start(psi)!;
            Console.WriteLine($"instance {tag}: pid {p.Id}  {psi.Arguments}");
            return p;
        }

        var a = Start("A");
        if (!Unlock(a.Id, TimeSpan.FromSeconds(60))) return 3;
        // Both instances share Documents\Klei\OxygenNotIncluded: if B opens WorldGenDataSave.worldgen while A is
        // still writing it, B (or A) shows "cannot start a new game" and sits there. Start B once A is past that.
        WaitForLog(Path.Combine(logDir, "instanceA.log"), "World settings reload complete", TimeSpan.FromSeconds(120));
        var b = Start("B");
        Thread.Sleep(5000);
        Console.WriteLine(b.HasExited ? $"instance B exited early (code {b.ExitCode})" : "both instances running");
        return b.HasExited ? 4 : 0;
    }
}

static unsafe class Handles
{
    const int SystemExtendedHandleInformation = 64;
    const int ObjectNameInformation = 1, ObjectTypeInformation = 2;
    const uint StatusInfoLengthMismatch = 0xC0000004;
    const uint ProcessDupHandle = 0x40;
    const uint DuplicateCloseSource = 1, DuplicateSameAccess = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct Entry
    {
        public IntPtr Object;
        public UIntPtr Pid;
        public UIntPtr Handle;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }

    [DllImport("ntdll.dll")] static extern uint NtQuerySystemInformation(int cls, IntPtr info, int len, out int ret);
    [DllImport("ntdll.dll")] static extern uint NtQueryObject(IntPtr h, int cls, IntPtr info, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DuplicateHandle(IntPtr srcProc, IntPtr src, IntPtr dstProc, out IntPtr dst, uint access, bool inherit, uint options);

    /// <summary>(handle value in the target process, object name) for every named mutex the process holds.</summary>
    public static List<(IntPtr handle, string name)> Mutexes(int pid)
    {
        var result = new List<(IntPtr, string)>();
        IntPtr proc = OpenProcess(ProcessDupHandle, false, pid);
        if (proc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess({pid})");
        try
        {
            foreach (var e in SystemHandles().Where(e => (int)e.Pid == pid))
            {
                if (!DuplicateHandle(proc, (IntPtr)(long)e.Handle, GetCurrentProcess(), out IntPtr dup, 0, false, DuplicateSameAccess))
                    continue;
                try
                {
                    // Only ever query names of Mutant objects: name queries on some pipe handles can hang.
                    if (Query(dup, ObjectTypeInformation) != "Mutant") continue;
                    string name = Query(dup, ObjectNameInformation);
                    if (name.Length > 0) result.Add(((IntPtr)(long)e.Handle, name));
                }
                finally { CloseHandle(dup); }
            }
        }
        finally { CloseHandle(proc); }
        return result;
    }

    /// <summary>Closes a handle inside another process (DuplicateHandle with DUPLICATE_CLOSE_SOURCE).</summary>
    public static void CloseRemote(int pid, IntPtr handle)
    {
        IntPtr proc = OpenProcess(ProcessDupHandle, false, pid);
        if (proc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess({pid})");
        try
        {
            if (!DuplicateHandle(proc, handle, GetCurrentProcess(), out IntPtr dup, 0, false, DuplicateCloseSource))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateHandle(close source)");
            CloseHandle(dup);
        }
        finally { CloseHandle(proc); }
    }

    static List<Entry> SystemHandles()
    {
        int len = 1 << 20;
        while (true)
        {
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                uint st = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, len, out int ret);
                if (st == StatusInfoLengthMismatch) { len = Math.Max(len * 2, ret + (1 << 16)); continue; }
                if (st != 0) throw new Win32Exception($"NtQuerySystemInformation failed: 0x{st:X8}");
                long count = (long)Marshal.ReadIntPtr(buf);
                var list = new List<Entry>((int)count);
                Entry* p = (Entry*)(buf + 2 * IntPtr.Size);
                for (long i = 0; i < count; i++) list.Add(p[i]);
                return list;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    static string Query(IntPtr h, int cls)
    {
        int len = 1024;
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (NtQueryObject(h, cls, buf, len, out _) != 0) return "";
            var us = Marshal.PtrToStructure<UnicodeString>(buf);
            return us.Buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
