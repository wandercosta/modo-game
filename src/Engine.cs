using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ModoGame
{
    class ProcInfo
    {
        public int Pid;
        public int ParentPid;
        public string Name;
        public string Path;
        public long Bytes;
        public bool HasWindow;
        public long StartTicks;
    }

    class Group
    {
        public string Name;
        public string Path;
        public long Bytes;
        public bool HasWindow;
        public bool Restorable { get { return RestorePaths.Count > 0; } }
        public List<string> RestorePaths = new List<string>();
        public bool UserProtected;
        public List<ProcInfo> Procs = new List<ProcInfo>();
    }

    class ScanResult
    {
        public List<Group> Groups = new List<Group>();
        public int ProtectedCount;
    }

    static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
        static extern bool Process32First(IntPtr h, ref PROCESSENTRY32 e);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
        static extern bool Process32Next(IntPtr h, ref PROCESSENTRY32 e);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = 64;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);

        public static string GetPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        public static Dictionary<int, int> ParentMap()
        {
            var map = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new PROCESSENTRY32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (!Process32First(snap, ref e)) return map;
                do { map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; }
                while (Process32Next(snap, ref e));
            }
            finally { CloseHandle(snap); }
            return map;
        }

        // total e disponível em bytes
        public static void Memory(out ulong total, out ulong avail)
        {
            var m = new MEMORYSTATUSEX();
            GlobalMemoryStatusEx(m);
            total = m.ullTotalPhys;
            avail = m.ullAvailPhys;
        }
    }

    static class Engine
    {
        public static readonly string Dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModoGame");

        static string F(string name) { return System.IO.Path.Combine(Dir, name); }

        static readonly string WinDir =
            Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant() + "\\";

        // Núcleo do Windows: nunca fecha
        static readonly HashSet<string> SystemNames = new HashSet<string>(new[] {
            "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
            "svchost", "dwm", "explorer", "sihost", "fontdrvhost", "ctfmon", "taskhostw", "runtimebroker",
            "startmenuexperiencehost", "shellexperiencehost", "searchhost", "textinputhost",
            "applicationframehost", "shellhost", "lockapp", "securityhealthsystray", "audiodg", "conhost",
            "dllhost", "smartscreen", "logonui", "userinit", "taskmgr", "memory compression",
            "secure system", "modogame"
        });

        // Ficam em C:\Windows mas podem ser fechados
        static readonly HashSet<string> ClosableInWindows = new HashSet<string>(new[] {
            "powershell", "pwsh", "cmd", "notepad", "mspaint", "mstsc", "wscript", "cscript"
        });

        // Drivers, periféricos, launchers, anti-cheat e antivírus
        static readonly string[] KeepPrefixes = {
            "nvcontainer", "nvdisplay", "nvsphelper", "nvidia", "amdrs", "radeonsoftware", "rtkaud", "ravbg",
            "realtek", "razer", "lghub", "logioptions", "steelseries", "icue", "corsair",
            "easyanticheat", "beservice", "battleye", "vgc", "vgtray",
            "steam", "epicgames", "epicwebhelper", "riotclient", "battle.net", "eadesktop", "eabackground",
            "upc", "ubisoft", "galaxyclient", "hydra",
            "msmpeng", "nissrv", "securityhealth", "rustdesk"
        };

        static readonly string[] KeepPathParts = {
            "\\nvidia", "\\realtek", "\\windows defender", "\\steamapps\\", "\\epic games\\", "\\riot games\\",
            // periféricos (mouse, teclado, headset, RGB)
            "mouse", "keyboard", "teclado", "headset", "\\ajazz", "\\redragon", "\\hyperx", "\\logitech",
            "\\armoury", "\\msi\\", "\\gigabyte", "\\microsoft shared\\ink\\"
        };

        // Jogos abertos por esses launchers também ficam protegidos
        static readonly string[] Launchers = {
            "hydra", "steam", "epicgameslauncher", "riotclientservices", "battle.net", "eadesktop", "upc", "galaxyclient"
        };

        public static bool IsAdmin()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static bool IsActive() { return File.Exists(F("ativo.txt")); }

        public static HashSet<string> LoadUserProtected()
        {
            var set = new HashSet<string>();
            if (File.Exists(F("protegidos.txt")))
                foreach (var l in File.ReadAllLines(F("protegidos.txt")))
                    if (l.Trim().Length > 0) set.Add(l.Trim().ToLowerInvariant());
            return set;
        }

        public static void SetUserProtected(string name, bool isProtected)
        {
            var set = LoadUserProtected();
            if (isProtected) set.Add(name.ToLowerInvariant()); else set.Remove(name.ToLowerInvariant());
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(F("protegidos.txt"), set.OrderBy(s => s).ToArray());
        }

        static bool IsProtected(ProcInfo p, int myPid)
        {
            string n = p.Name.ToLowerInvariant();
            if (p.Pid == myPid || SystemNames.Contains(n)) return true;
            if (string.IsNullOrEmpty(p.Path)) return true;
            string path = p.Path.ToLowerInvariant();
            if (path.StartsWith(WinDir) && !ClosableInWindows.Contains(n)) return true;
            if (KeepPrefixes.Any(k => n.StartsWith(k))) return true;
            if (KeepPathParts.Any(k => path.Contains(k))) return true;
            return false;
        }

        public static ScanResult Scan()
        {
            var me = Process.GetCurrentProcess();
            int mySession = me.SessionId, myPid = me.Id;
            var parents = Native.ParentMap();
            var info = new Dictionary<int, ProcInfo>();

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.SessionId != mySession) continue;
                    var pi = new ProcInfo { Pid = p.Id, Name = p.ProcessName, Bytes = p.WorkingSet64 };
                    try { pi.HasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { }
                    try { pi.StartTicks = p.StartTime.Ticks; } catch { }
                    pi.Path = Native.GetPath(p.Id);
                    int pp;
                    if (parents.TryGetValue(p.Id, out pp)) pi.ParentPid = pp;
                    info[p.Id] = pi;
                }
                catch { }
                finally { p.Dispose(); }
            }

            var keep = new HashSet<int>(info.Values.Where(p => IsProtected(p, myPid)).Select(p => p.Pid));
            var launcherPids = new HashSet<int>(info.Values
                .Where(p => Launchers.Contains(p.Name.ToLowerInvariant())).Select(p => p.Pid));

            foreach (var p in info.Values)
            {
                int cur = p.ParentPid;
                for (int depth = 0; depth < 16 && cur != 0; depth++)
                {
                    if (launcherPids.Contains(cur)) { keep.Add(p.Pid); break; }
                    ProcInfo parent;
                    if (!info.TryGetValue(cur, out parent)) break;
                    cur = parent.ParentPid;
                }
            }

            var user = LoadUserProtected();
            var result = new ScanResult { ProtectedCount = keep.Count };
            foreach (var g in info.Values.Where(p => !keep.Contains(p.Pid)).GroupBy(p => p.Name.ToLowerInvariant()))
            {
                var grp = new Group { Name = g.First().Name, UserProtected = user.Contains(g.Key) };
                grp.Procs.AddRange(g);
                grp.Path = g.Select(p => p.Path).FirstOrDefault(s => s != null);
                grp.Bytes = g.Sum(p => p.Bytes);
                grp.HasWindow = g.Any(p => p.HasWindow);
                // App "de verdade": tem janela ou foi aberto pelo próprio Windows (inicialização, menu iniciar).
                // Por caminho, porque o mesmo nome pode ser de programas diferentes (ex.: claude app e claude CLI)
                foreach (var p in g)
                {
                    ProcInfo parent;
                    bool fromShell = info.TryGetValue(p.ParentPid, out parent) &&
                                     parent.Name.Equals("explorer", StringComparison.OrdinalIgnoreCase);
                    if ((p.HasWindow || fromShell) && !p.Path.ToLowerInvariant().StartsWith(WinDir) &&
                        !grp.RestorePaths.Contains(p.Path, StringComparer.OrdinalIgnoreCase))
                        grp.RestorePaths.Add(p.Path);
                }
                result.Groups.Add(grp);
            }
            result.Groups.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            return result;
        }

        // ---------- Ativar ----------

        public static string Activate(Action<string> log)
        {
            Directory.CreateDirectory(Dir);
            var groups = Scan().Groups.Where(g => !g.UserProtected).ToList();
            ulong total, before, after;
            Native.Memory(out total, out before);

            var restore = new List<string>();
            if (File.Exists(F("restaurar.txt"))) restore.AddRange(File.ReadAllLines(F("restaurar.txt")));
            foreach (var path in groups.SelectMany(g => g.RestorePaths))
            {
                string target = LaunchTarget(path);
                if (target != null && !restore.Contains(target, StringComparer.OrdinalIgnoreCase)) restore.Add(target);
            }
            File.WriteAllLines(F("restaurar.txt"), restore.ToArray());

            log("Ativando energia máxima…");
            if (!File.Exists(F("energia.txt")))
            {
                string cur = CurrentScheme();
                if (cur != null) File.WriteAllText(F("energia.txt"), cur);
            }
            Powercfg("/setactive SCHEME_MIN");

            var procs = groups.SelectMany(g => g.Procs).ToList();
            log("Fechando janelas…");
            foreach (var p in procs.Where(p => p.HasWindow))
                WithProcess(p, x => x.CloseMainWindow());

            for (int i = 0; i < 30 && procs.Any(IsAlive); i++) Thread.Sleep(100);

            log("Encerrando processos em segundo plano…");
            foreach (var p in procs)
                WithProcess(p, x => { if (!x.HasExited) x.Kill(); });

            Thread.Sleep(1200);
            Native.Memory(out total, out after);
            File.WriteAllText(F("ativo.txt"), DateTime.Now.ToString("o"));

            double freed = Math.Max(0, ((double)after - before) / (1L << 30));
            return string.Format(Ui.PtBr, "{0} apps fechados · {1:N1} GB liberados", groups.Count, freed);
        }

        static bool IsAlive(ProcInfo p)
        {
            bool alive = false;
            WithProcess(p, x => { alive = !x.HasExited; });
            return alive;
        }

        // Só age se o PID ainda for o mesmo processo (evita PID reutilizado)
        static void WithProcess(ProcInfo p, Action<Process> act)
        {
            try
            {
                using (var x = Process.GetProcessById(p.Pid))
                {
                    if (p.StartTicks != 0 && x.StartTime.Ticks != p.StartTicks) return;
                    act(x);
                }
            }
            catch { }
        }

        // Apps da Microsoft Store não abrem pelo caminho do exe: resolve shell:AppsFolder\PFN!AppId
        static string LaunchTarget(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            int i = exePath.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return exePath;
            try
            {
                string rest = exePath.Substring(i + 13);
                string pkg = rest.Substring(0, rest.IndexOf('\\'));
                string pkgDir = exePath.Substring(0, i + 13) + pkg;
                string[] parts = pkg.Split('_');
                string pfn = parts[0] + "_" + parts[parts.Length - 1];
                string manifest = File.ReadAllText(System.IO.Path.Combine(pkgDir, "AppxManifest.xml"));
                var m = Regex.Match(manifest, "<Application[^>]*\\sId=\"([^\"]+)\"");
                return m.Success ? "shell:AppsFolder\\" + pfn + "!" + m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        // ---------- Restaurar ----------

        public static string Restore(Action<string> log)
        {
            log("Voltando o plano de energia…");
            if (File.Exists(F("energia.txt")))
            {
                Powercfg("/setactive " + File.ReadAllText(F("energia.txt")).Trim());
                File.Delete(F("energia.txt"));
            }
            else Powercfg("/setactive SCHEME_BALANCED");

            int opened = 0;
            if (File.Exists(F("restaurar.txt")))
            {
                log("Reabrindo seus apps…");
                var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Process.GetProcesses())
                {
                    try { var s = Native.GetPath(p.Id); if (s != null) running.Add(s); }
                    catch { }
                    finally { p.Dispose(); }
                }
                foreach (var target in File.ReadAllLines(F("restaurar.txt")))
                {
                    bool store = target.StartsWith("shell:");
                    if (!store && (!File.Exists(target) || running.Contains(target))) continue;
                    try
                    {
                        // via explorer.exe: abre sem privilégio de administrador, como se você tivesse clicado
                        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = false });
                        opened++;
                        Thread.Sleep(400);
                    }
                    catch { }
                }
                File.Delete(F("restaurar.txt"));
            }
            if (File.Exists(F("ativo.txt"))) File.Delete(F("ativo.txt"));
            return opened + " apps reabertos";
        }

        // ---------- Energia ----------

        public static string CurrentScheme()
        {
            var m = Regex.Match(Powercfg("/getactivescheme"), "[0-9a-fA-F-]{36}");
            return m.Success ? m.Value : null;
        }

        public static string CurrentSchemeName()
        {
            var m = Regex.Match(Powercfg("/getactivescheme"), "\\(([^)]+)\\)");
            return m.Success ? m.Groups[1].Value : "—";
        }

        static string Powercfg(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.GetEncoding(850)
                };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return o;
                }
            }
            catch { return ""; }
        }
    }
}
