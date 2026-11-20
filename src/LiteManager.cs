using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;

class AppInfo
{
    public string Name = "";
    public string Version = "";
    public string Publisher = "";
    public string InstallLocation = "";
    public string UninstallString = "";
    public string QuietUninstall = "";
    public string InstallDate = "";
    public string DisplayIcon = "";
    public long SizeKB = 0;
    public string Hive = "HKLM";
    public string RegPath = "";
    public bool IsMSI = false;
    public bool Protected = false;
    public bool Signed = true;
    public int Risk = 0;
    public string RiskReason = "";
    public string SizeText()
    {
        if (SizeKB <= 0) return "";
        if (SizeKB < 1024) return SizeKB + " KB";
        double mb = SizeKB / 1024.0;
        if (mb < 1024) return mb.ToString("0.0") + " MB";
        return (mb / 1024.0).ToString("0.0") + " GB";
    }
}

static class AppLoader
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string UninstallKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    static string GetStr(RegistryKey k, string name)
    {
        try
        {
            object o = k.GetValue(name);
            if (o == null) return null;
            string s = o.ToString().Trim();
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
    }

    public static List<AppInfo> Load()
    {
        List<AppInfo> list = new List<AppInfo>();
        string[][] places = new string[][]
        {
            new string[] { "HKLM", UninstallKey },
            new string[] { "HKLM", UninstallKey32 },
            new string[] { "HKCU", UninstallKey },
            new string[] { "HKCU", UninstallKey32 }
        };

        foreach (string[] p in places)
        {
            RegistryKey root = p[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            RegistryKey baseKey = null;
            try { baseKey = root.OpenSubKey(p[1]); }
            catch { baseKey = null; }
            if (baseKey == null) continue;

            try
            {
                foreach (string sub in baseKey.GetSubKeyNames())
                {
                    RegistryKey sk = null;
                    try
                    {
                        sk = baseKey.OpenSubKey(sub);
                        if (sk == null) continue;

                        string name = GetStr(sk, "DisplayName");
                        if (name == null) continue;

                        object sysComp = sk.GetValue("SystemComponent");
                        if (sysComp != null && Convert.ToInt32(sysComp) == 1) continue;

                        if (!string.IsNullOrEmpty(GetStr(sk, "ParentKeyName"))) continue;

                        string rt = GetStr(sk, "ReleaseType");
                        if (!string.IsNullOrEmpty(rt))
                        {
                            string r = rt.ToLower();
                            if (r.Contains("update") || r.Contains("hotfix") || r.Contains("security") || r.Contains("servicepack")) continue;
                        }

                        AppInfo a = new AppInfo();
                        a.Name = name;
                        a.Hive = p[0];
                        a.RegPath = p[1] + "\\" + sub;
                        a.Version = GetStr(sk, "DisplayVersion");
                        if (a.Version == null) a.Version = "";
                        a.Publisher = GetStr(sk, "Publisher");
                        if (a.Publisher == null) a.Publisher = "";
                        a.InstallLocation = GetStr(sk, "InstallLocation");
                        if (a.InstallLocation == null) a.InstallLocation = "";
                        a.InstallLocation = a.InstallLocation.Trim('"').Trim();
                        a.UninstallString = GetStr(sk, "UninstallString");
                        if (a.UninstallString == null) a.UninstallString = "";
                        a.QuietUninstall = GetStr(sk, "QuietUninstallString");
                        if (a.QuietUninstall == null) a.QuietUninstall = "";
                        a.DisplayIcon = GetStr(sk, "DisplayIcon");
                        if (a.DisplayIcon == null) a.DisplayIcon = "";

                        string d = GetStr(sk, "InstallDate");
                        if (!string.IsNullOrEmpty(d) && d.Length == 8)
                            a.InstallDate = d.Substring(0, 4) + "-" + d.Substring(4, 2) + "-" + d.Substring(6, 2);
                        else a.InstallDate = "";

                        object sz = sk.GetValue("EstimatedSize");
                        if (sz != null)
                        {
                            try { a.SizeKB = Convert.ToInt64(sz); } catch { a.SizeKB = 0; }
                        }

                        object wi = sk.GetValue("WindowsInstaller");
                        if (wi != null && Convert.ToInt32(wi) == 1) a.IsMSI = true;
                        if (a.UninstallString.ToLower().Contains("msiexec")) a.IsMSI = true;

                        if (a.InstallLocation.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase) ||
                            a.InstallLocation.StartsWith(@"C:\WINDOWS", StringComparison.OrdinalIgnoreCase))
                            a.Protected = true;

                        if (a.Publisher.ToLower().Contains("microsoft") && a.Name.ToLower().Contains("microsoft"))
                            a.Protected = true;

                        list.Add(a);
                    }
                    catch { }
                    finally { if (sk != null) sk.Close(); }
                }
            }
            catch { }
            finally { if (baseKey != null) baseKey.Close(); }
        }

        // 去重
        List<AppInfo> unique = new List<AppInfo>();
        HashSet<string> seen = new HashSet<string>();
        foreach (AppInfo a in list)
        {
            string key = a.Name.ToLower() + "|" + a.Version.ToLower() + "|" + a.InstallLocation.ToLower();
            if (seen.Contains(key)) continue;
            seen.Add(key);
            unique.Add(a);
        }

        // 风险评估
        foreach (AppInfo a in unique) Assess(a);

        unique.Sort(delegate (AppInfo x, AppInfo y) { return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase); });
        return unique;
    }

    static bool IsSigned(string exe)
    {
        try
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
            X509Certificate.CreateFromSignedFile(exe);
            return true;
        }
        catch { return false; }
    }

    static string FindMainExe(AppInfo a)
    {
        try
        {
            if (!string.IsNullOrEmpty(a.DisplayIcon))
            {
                string p = a.DisplayIcon;
                if (p.StartsWith("\""))
                {
                    int q = p.IndexOf('"', 1);
                    if (q > 0) p = p.Substring(1, q - 1);
                }
                else
                {
                    int idx = p.ToLower().IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                    if (idx > 0) p = p.Substring(0, idx + 4);
                }
                if (File.Exists(p)) return p;
            }
            if (!string.IsNullOrEmpty(a.InstallLocation) && Directory.Exists(a.InstallLocation))
            {
                string[] files = Directory.GetFiles(a.InstallLocation, "*.exe", System.IO.SearchOption.TopDirectoryOnly);
                foreach (string f in files)
                {
                    string fn = Path.GetFileName(f).ToLower();
                    if (fn.StartsWith("unins") || fn.StartsWith("uninst") || fn.StartsWith("setup")) continue;
                    return f;
                }
                if (files.Length > 0) return files[0];
            }
        }
        catch { }
        return null;
    }

    static readonly string[] RiskyWords = new string[]
    {
        "助手", "卫士", "管家", "加速器", "优化大师", "清理大师", "全家桶", "大全",
        "精灵", "魔盒", "万能", "一键", "极速", "推广", "下载器", "破解", "去广告",
        "导航站", "壁纸", "天气", "小说", "抢票", "刷机", "福利", "赚钱", "返利"
    };

    static void Assess(AppInfo a)
    {
        int score = 0;
        List<string> reasons = new List<string>();
        string pub = a.Publisher == null ? "" : a.Publisher;

        if (pub.Length == 0)
        {
            score += 25;
            reasons.Add("无发布者信息");
        }
        else if (pub.ToLower().Contains("unknown") || pub.Contains("未知"))
        {
            score += 25;
            reasons.Add("发布者为未知");
        }

        bool hasUninstall = !string.IsNullOrEmpty(a.UninstallString);
        bool hasDir = !string.IsNullOrEmpty(a.InstallLocation) && Directory.Exists(a.InstallLocation);
        if (!hasUninstall && !hasDir)
        {
            score += 30;
            reasons.Add("无卸载命令也无安装目录");
        }
        else if (!hasUninstall)
        {
            score += 18;
            reasons.Add("缺少卸载入口");
        }

        if (!hasDir && !string.IsNullOrEmpty(a.InstallLocation))
        {
            score += 10;
            reasons.Add("安装目录不存在");
        }

        string loc = a.InstallLocation.ToLower();
        string temp = Path.GetTempPath().ToLower();
        if (loc.Length > 0 && (loc.Contains(temp.TrimEnd('\\')) || loc.Contains(@"\downloads\") || loc.Contains(@"\temp\") || loc.Contains(@"\tmp\")))
        {
            score += 30;
            reasons.Add("安装在临时/下载目录");
        }

        int wordHits = 0;
        foreach (string w in RiskyWords)
        {
            if (a.Name.Contains(w))
            {
                wordHits++;
                reasons.Add("名称含推广词「" + w + "」");
                break;
            }
        }
        if (wordHits > 0) score += 12;

        string exe = FindMainExe(a);
        if (exe != null)
        {
            if (!IsSigned(exe))
            {
                a.Signed = false;
                score += 22;
                reasons.Add("程序无数字签名");
            }
        }
        else
        {
            a.Signed = false;
            score += 8;
            reasons.Add("未找到主程序文件");
        }

        bool isMsPub = pub.ToLower().Contains("microsoft") || pub.ToLower().Contains("windows");
        bool isMsName = a.Name.ToLower().StartsWith("microsoft") || a.Name.ToLower().StartsWith("windows");
        if (isMsPub || isMsName)
        {
            score -= 30;
        }

        if (score < 0) score = 0;
        if (score > 100) score = 100;
        a.Risk = score;

        StringBuilder sb = new StringBuilder();
        foreach (string r in reasons)
        {
            if (sb.Length > 0) sb.Append("；");
            sb.Append(r);
        }
        a.RiskReason = sb.Length == 0 ? "未发现明显风险" : sb.ToString();
    }
}

static class Uninstaller
{
    public static bool Run(string fileName, string args, int timeoutMs, bool shell)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = args;
            psi.UseShellExecute = shell;
            psi.CreateNoWindow = !shell;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process p = Process.Start(psi);
            if (p == null) return false;
            bool exited = p.WaitForExit(timeoutMs);
            return exited;
        }
        catch { return false; }
    }

    public static string ParseExe(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return "";
        cmd = cmd.Trim();
        if (cmd.StartsWith("\""))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 0) return cmd.Substring(1, end - 1);
        }
        string lower = cmd.ToLower();
        int idx = lower.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) return cmd.Substring(0, idx + 4);
        int sp = cmd.IndexOf(' ');
        return sp > 0 ? cmd.Substring(0, sp) : cmd;
    }

    public static string ParseArgs(string cmd)
    {
        string exe = ParseExe(cmd);
        if (string.IsNullOrEmpty(exe)) return "";
        int at = cmd.ToLower().IndexOf(exe.ToLower(), StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "";
        return cmd.Substring(at + exe.Length).Trim().TrimStart('"').Trim();
    }

    static Regex GuidRe = new Regex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.IgnoreCase);

    static string GetGuid(AppInfo a)
    {
        Match m = GuidRe.Match(a.UninstallString == null ? "" : a.UninstallString);
        if (m.Success) return m.Value;
        string sub = a.RegPath.Substring(a.RegPath.LastIndexOf('\\') + 1);
        if (GuidRe.IsMatch(sub)) return sub;
        return null;
    }

    public static void KillIn(string dir)
    {
        if (string.IsNullOrEmpty(dir) || dir.Length < 8) return;
        dir = dir.TrimEnd('\\');
        List<Process> ps = new List<Process>();
        foreach (Process p in Process.GetProcesses())
        {
            try
            {
                string path = p.MainModule.FileName;
                if (path != null && path.Length > 0 && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    ps.Add(p);
            }
            catch { }
        }
        foreach (Process p in ps)
        {
            try
            {
                string n = p.ProcessName.ToLower();
                if (n == "explorer" || n == "svchost" || n == "csrss" || n == "winlogon" || n == "services" || n == "lsass") continue;
                p.Kill();
                p.WaitForExit(3000);
            }
            catch { }
        }
    }

    public static bool DeleteDir(string dir)
    {
        if (string.IsNullOrEmpty(dir) || dir.Length < 8) return false;
        string d = dir.TrimEnd('\\');
        if (!Directory.Exists(d)) return true;
        return FileForce.DeleteTree(d, null);
    }

    static void DeleteShortcuts(string name)
    {
        string clean = name.Trim();
        if (clean.Length < 3) return;
        List<string> roots = new List<string>();
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Programs)); } catch { }
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)); } catch { }
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch { }
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)); } catch { }
        try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)); } catch { }

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                string[] lnks = Directory.GetFiles(root, "*.lnk", System.IO.SearchOption.AllDirectories);
                foreach (string l in lnks)
                {
                    string fn = Path.GetFileNameWithoutExtension(l);
                    if (fn.ToLower().Contains(clean.ToLower()))
                    {
                        try { File.Delete(l); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    static string Clean(string s)
    {
        if (s == null) return "";
        char[] bad = Path.GetInvalidFileNameChars();
        StringBuilder sb = new StringBuilder();
        foreach (char c in s)
        {
            if (Array.IndexOf(bad, c) < 0) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    static void DeleteAppDataLeftovers(AppInfo a)
    {
        string n = Clean(a.Name);
        if (n.Length < 3) return;
        string ln = n.ToLower();
        if (ln.Contains("microsoft") || ln.Contains("windows") || ln.Contains("visual c++") || ln.Contains(".net")) return;

        string[] bases = new string[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        foreach (string b in bases)
        {
            if (string.IsNullOrEmpty(b)) continue;
            string p = Path.Combine(b, n);
            if (Directory.Exists(p))
            {
                try { DeleteDir(p); } catch { }
            }
        }
    }

    /// <summary>执行卸载。force=true 时静默失败后强制删除目录与注册表。</summary>
    public static void Execute(AppInfo a, bool force, Action<string> log)
    {
        string dir = a.InstallLocation;
        log("正在处理：" + a.Name);

        bool needForce = force;

        // 1) MSI 静默卸载
        string guid = GetGuid(a);
        if (a.IsMSI && guid != null)
        {
            log("· 调用 Windows Installer 静默卸载");
            Run("msiexec.exe", "/x " + guid + " /qn /norestart", 600000, false);
            Thread.Sleep(2000);
        }
        else
        {
            // 2) 静默命令
            string cmd = !string.IsNullOrEmpty(a.QuietUninstall) ? a.QuietUninstall : a.UninstallString;
            if (!string.IsNullOrEmpty(cmd))
            {
                string exe = ParseExe(cmd);
                string args = ParseArgs(cmd);
                if (File.Exists(exe))
                {
                    string fn = Path.GetFileName(exe).ToLower();
                    if (!string.IsNullOrEmpty(a.QuietUninstall))
                    {
                        log("· 使用注册表静默卸载命令");
                    }
                    else if (fn.StartsWith("unins"))
                    {
                        args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";
                        log("· 识别为 Inno Setup，静默卸载");
                    }
                    else if (fn.StartsWith("uninst") || fn.StartsWith("au_") || fn.StartsWith("uninstall"))
                    {
                        args = "/S";
                        log("· 识别为常见安装器，静默卸载");
                    }
                    else
                    {
                        if (args.Length == 0) args = "/S";
                        log("· 尝试静默卸载");
                    }
                    KillIn(dir);
                    Run(exe, args, 600000, false);
                    Thread.Sleep(3000);
                }
                else
                {
                    log("· 卸载程序不存在，跳过静默卸载");
                }
            }
            else
            {
                log("· 无卸载命令");
            }
        }

        if (!force)
        {
            log("完成：" + a.Name);
            return;
        }

        // 3) 强制清理
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            if (a.Protected)
            {
                log("· 属于系统组件目录，已跳过目录删除");
            }
            else
            {
                log("· 强制结束占用进程并删除安装目录");
                KillIn(dir);
                Thread.Sleep(500);
                bool ok = DeleteDir(dir);
                if (!ok) log("· 安装目录删除失败（可能被占用），请手动检查：" + dir);
            }
        }

        DeleteShortcuts(a.Name);
        DeleteAppDataLeftovers(a);

        try
        {
            RegistryKey root = a.Hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            root.DeleteSubKeyTree(a.RegPath, false);
            log("· 已清除注册表卸载项");
        }
        catch { }

        log("完成：" + a.Name);
    }
}

// ============ 强制删除引擎：无视占用、只读、权限、超长路径 ============
static class FileForce
{
    const int CCH_RM_MAX_APP_NAME = 255;
    const int CCH_RM_MAX_SVC_NAME = 63;

    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);
    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint pSessionHandle);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, [In] RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);
    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded,
        ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgAffectedApps, out uint lpdwRebootReasons);

    static readonly string[] ProtectedProcs = new string[]
    {
        "explorer", "svchost", "csrss", "winlogon", "services", "lsass", "smss", "wininit",
        "dwm", "fontdrvhost", "ctfmon", "sihost", "taskhostw", "system", "registry", "memory compression"
    };

    // 超长路径转 \\?\ 形式
    public static string ToLong(string p)
    {
        if (string.IsNullOrEmpty(p)) return p;
        if (p.StartsWith(@"\\?\")) return p;
        if (p.Length < 240) return p;
        if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
        return @"\\?\" + p;
    }

    // 找出正在锁住该文件的进程（Restart Manager）
    public static List<int> GetLockers(string path)
    {
        List<int> ids = new List<int>();
        uint handle = 0;
        try
        {
            string key = Guid.NewGuid().ToString("N");
            if (RmStartSession(out handle, 0, key) != 0) return ids;
            try
            {
                if (RmRegisterResources(handle, 1, new string[] { path }, 0, null, 0, null) != 0) return ids;

                uint need = 0;
                uint have = 0;
                uint reasons = 0;
                int rc = RmGetList(handle, out need, ref have, null, out reasons);
                if (need == 0) return ids;

                RM_PROCESS_INFO[] arr = new RM_PROCESS_INFO[need];
                have = need;
                rc = RmGetList(handle, out need, ref have, arr, out reasons);
                if (rc != 0) return ids;

                for (int i = 0; i < have; i++)
                {
                    int pid = arr[i].Process.dwProcessId;
                    if (pid > 4 && pid != Process.GetCurrentProcess().Id) ids.Add(pid);
                }
            }
            finally
            {
                try { RmEndSession(handle); } catch { }
            }
        }
        catch { }
        return ids;
    }

    // 结束占用该文件的进程
    public static bool KillLockers(string path, Action<string> log)
    {
        bool killed = false;
        foreach (int id in GetLockers(path))
        {
            try
            {
                Process p = Process.GetProcessById(id);
                string n = p.ProcessName.ToLower();
                bool prot = false;
                for (int i = 0; i < ProtectedProcs.Length; i++) if (ProtectedProcs[i] == n) { prot = true; break; }
                if (prot) continue;

                string disp = p.ProcessName;
                p.Kill();
                p.WaitForExit(2000);
                if (log != null) log("结束占用进程 " + disp);
                killed = true;
            }
            catch { }
        }
        return killed;
    }

    static void RunHidden(string exe, string args, int timeoutMs)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process p = Process.Start(psi);
            if (p != null)
            {
                p.WaitForExit(timeoutMs);
                try { p.Dispose(); } catch { }
            }
        }
        catch { }
    }

    static bool Exists(string p)
    {
        try
        {
            if (File.Exists(p) || Directory.Exists(p)) return true;
        }
        catch { }
        return false;
    }

    static bool TryDelete(string lp, string orig)
    {
        try
        {
            if (Directory.Exists(lp))
            {
                try { File.SetAttributes(lp, FileAttributes.Normal); } catch { }
                Directory.Delete(lp, true);
                return !Exists(orig);
            }
            if (!File.Exists(lp) && !Exists(orig)) return true;
            try { File.SetAttributes(lp, FileAttributes.Normal); } catch { }
            File.Delete(lp);
            return !Exists(orig);
        }
        catch { }
        // 权限不足时：接管所有权 + 授予管理员完全控制（含父目录）
        try
        {
            string parent = Path.GetDirectoryName(orig);
            if (parent != null && parent.Length > 3)
            {
                RunHidden("takeown.exe", "/f \"" + parent + "\" /a", 20000);
                RunHidden("icacls.exe", "\"" + parent + "\" /grant *S-1-5-32-544:F /c /q", 20000);
            }
            RunHidden("takeown.exe", "/f \"" + orig + "\" /a", 20000);
            RunHidden("icacls.exe", "\"" + orig + "\" /grant *S-1-5-32-544:F /c /q", 20000);

            if (Directory.Exists(lp))
            {
                Directory.Delete(lp, true);
                return !Exists(orig);
            }
            if (!File.Exists(lp)) return !Exists(orig);
            try { File.SetAttributes(lp, FileAttributes.Normal); } catch { }
            File.Delete(lp);
            return !Exists(orig);
        }
        catch { return false; }
    }

    // 删单个文件：直接删 → 终止占用进程 → 接管权限 → cmd 强删
    public static bool DeleteFile(string path, bool killLockers, Action<string> log)
    {
        if (string.IsNullOrEmpty(path)) return true;
        if (!Exists(path)) return true;

        string lp = ToLong(path);
        if (TryDelete(lp, path)) return true;

        if (killLockers)
        {
            if (KillLockers(path, log))
            {
                Thread.Sleep(300);
                if (TryDelete(lp, path)) return true;
            }
        }

        try { RunHidden("cmd.exe", "/c del /f /q \"" + lp + "\"", 30000); } catch { }
        if (!Exists(path)) return true;

        try { RunHidden("cmd.exe", "/c rd /s /q \"" + lp + "\"", 30000); } catch { }
        return !Exists(path);
    }

    public static bool DeleteFile(string path)
    {
        return DeleteFile(path, true, null);
    }

    // 删整个目录树：逐个文件强删，再逐层删空目录
    public static bool DeleteTree(string dir, Action<string> log)
    {
        if (string.IsNullOrEmpty(dir) || dir.Length < 4) return false;
        string d = dir.TrimEnd('\\');
        if (!Directory.Exists(d)) return true;

        for (int round = 0; round < 3; round++)
        {
            try
            {
                Directory.Delete(d, true);
                if (!Directory.Exists(d)) return true;
            }
            catch { }

            WalkDelete(d, log);
            if (!Directory.Exists(d)) return true;

            try
            {
                RunHidden("takeown.exe", "/f \"" + d + "\" /r /d y", 90000);
                RunHidden("icacls.exe", "\"" + d + "\" /grant *S-1-5-32-544:F /t /c /q", 90000);
            }
            catch { }
            Thread.Sleep(500);
        }

        try { RunHidden("cmd.exe", "/c rd /s /q \"" + ToLong(d) + "\"", 90000); } catch { }
        return !Directory.Exists(d);
    }

    static void WalkDelete(string dir, Action<string> log)
    {
        List<string> files = new List<string>();
        try { files.AddRange(Directory.GetFiles(dir, "*", System.IO.SearchOption.AllDirectories)); } catch { }
        for (int i = 0; i < files.Count; i++)
        {
            try { DeleteFile(files[i], true, log); } catch { }
        }

        List<string> subs = new List<string>();
        try { subs.AddRange(Directory.GetDirectories(dir, "*", System.IO.SearchOption.AllDirectories)); } catch { }
        subs.Sort(delegate (string x, string y) { return y.Length - x.Length; });
        for (int i = 0; i < subs.Count; i++)
        {
            try { Directory.Delete(subs[i], false); } catch { }
        }
        try { Directory.Delete(dir, false); } catch { }
    }
}

class DocItem
{
    public string Path = "";
    public string Name = "";
    public string Ext = "";
    public string Cat = "";
    public long Size = 0;
    public DateTime Modified = DateTime.MinValue;
    public string SizeText()
    {
        if (Size < 1024) return Size + " B";
        if (Size < 1024 * 1024) return (Size / 1024.0).ToString("0.0") + " KB";
        if (Size < 1024L * 1024 * 1024) return (Size / 1024.0 / 1024.0).ToString("0.0") + " MB";
        return (Size / 1024.0 / 1024.0 / 1024.0).ToString("0.00") + " GB";
    }
}

class FolderItem
{
    public string Path = "";
    public string Name = "";
}

/// <summary>软件管理页列头点击排序用的比较器。</summary>
class AppSorter : System.Collections.IComparer
{
    public int Col = 0;
    public bool Asc = true;

    static string Sub(ListViewItem it, int col)
    {
        return (col >= 0 && col < it.SubItems.Count) ? it.SubItems[col].Text : "";
    }

    // 注册表安装日期是 YYYYMMDD 文本，空值排最后
    static int CmpDate(string x, string y)
    {
        if (x.Length == 0 && y.Length == 0) return 0;
        if (x.Length == 0) return 1;
        if (y.Length == 0) return -1;
        return string.Compare(x, y, StringComparison.Ordinal);
    }

    public int Compare(object x, object y)
    {
        ListViewItem a = (ListViewItem)x;
        ListViewItem b = (ListViewItem)y;
        AppInfo ai = a.Tag as AppInfo;
        AppInfo bi = b.Tag as AppInfo;
        int c;
        if (ai == null || bi == null)
        {
            c = string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            switch (Col)
            {
                case 0: c = string.Compare(ai.Name, bi.Name, StringComparison.OrdinalIgnoreCase); break;
                case 3: c = ai.SizeKB.CompareTo(bi.SizeKB); break;
                case 4: c = CmpDate(ai.InstallDate, bi.InstallDate); break;
                default: c = string.Compare(Sub(a, Col), Sub(b, Col), StringComparison.OrdinalIgnoreCase); break;
            }
            if (c == 0) c = string.Compare(ai.Name, bi.Name, StringComparison.OrdinalIgnoreCase);
        }
        return Asc ? c : -c;
    }
}

class ScanResult
{
    public List<DocItem> Files = new List<DocItem>();
    public List<FolderItem> Folders = new List<FolderItem>();
}

static class DocScanner
{
    public static List<string> Categories = new List<string>()
    {
        "Word 文档", "Excel 表格", "PPT 演示", "PDF 文档", "文本文件", "其他文档", "图片", "视频", "压缩文件"
    };

    /// <summary>文档管理页只显示这些（图片/视频/压缩文件各自独立页面）。</summary>
    public static List<string> DocCategories = new List<string>()
    {
        "Word 文档", "Excel 表格", "PPT 演示", "PDF 文档", "文本文件", "其他文档"
    };

    public static bool IsDocCategory(string cat)
    {
        for (int i = 0; i < DocCategories.Count; i++) if (DocCategories[i] == cat) return true;
        return false;
    }

    static Dictionary<string, string> map = Build();

    static Dictionary<string, string> Build()
    {
        Dictionary<string, string> m = new Dictionary<string, string>();
        string[] word = new string[] { ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".wps", ".rtf", ".odt", ".pages" };
        string[] excel = new string[] { ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv", ".et", ".ett", ".ods", ".numbers" };
        string[] ppt = new string[] { ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".dps", ".odp", ".key" };
        string[] pdf = new string[] { ".pdf" };
        string[] txt = new string[] { ".txt", ".md", ".log", ".json", ".xml", ".ini", ".htm", ".html", ".yaml", ".yml" };
        string[] img = new string[] { ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico",
                                      ".svg", ".heic", ".heif", ".psd", ".psb", ".ai", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2" };
        string[] vid = new string[] { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".m4v", ".ts", ".m2ts", ".mts",
                                      ".rmvb", ".rm", ".3gp", ".mpg", ".mpeg", ".vob", ".webm", ".ogv", ".f4v", ".asf", ".swf", ".m3u8" };
        string[] arc = new string[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".z", ".cab",
                                      ".iso", ".lz", ".lzma", ".arj", ".ace", ".jar", ".war", ".whl", ".apk", ".xapk", ".001" };
        string[] other = new string[] { ".ofd", ".epub", ".mobi", ".tex", ".xmind", ".vsd", ".vsdx", ".pub", ".one" };
        foreach (string e in word) m[e] = "Word 文档";
        foreach (string e in excel) m[e] = "Excel 表格";
        foreach (string e in ppt) m[e] = "PPT 演示";
        foreach (string e in pdf) m[e] = "PDF 文档";
        foreach (string e in txt) m[e] = "文本文件";
        foreach (string e in img) m[e] = "图片";
        foreach (string e in vid) m[e] = "视频";
        foreach (string e in arc) m[e] = "压缩文件";
        foreach (string e in other) m[e] = "其他文档";
        return m;
    }

    static readonly string[] SkipNames = new string[]
    {
        "windows", "program files", "program files (x86)", "programdata", "$recycle.bin",
        "system volume information", "appdata", "node_modules", ".git", "windowsapps",
        "winsxs", "assembly", "installer", "temp", "tmp", "cache",
        "recovery", "$winreagent", "msocache", "config.msi", "perflogs", "windows.old",
        "driverstore", "softwaredistribution", "package cache", "packages"
    };

    static bool Skip(string dir)
    {
        string n = Path.GetFileName(dir).ToLower();
        foreach (string s in SkipNames) if (n == s) return true;
        if (n.StartsWith(".") && n.Length > 1) return true;
        try
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return true;
        }
        catch { return true; }
        return false;
    }

    /// <summary>全盘扫描所有固定磁盘：文件 + 文件夹。</summary>
    public static ScanResult Scan(Action<int, int> progress, Func<bool> cancelled)
    {
        List<string> roots = new List<string>();
        foreach (DriveInfo dr in DriveInfo.GetDrives())
        {
            try
            {
                if (dr.DriveType == DriveType.Fixed && dr.IsReady) roots.Add(dr.RootDirectory.FullName);
            }
            catch { }
        }

        ScanResult res = new ScanResult();
        HashSet<string> seenRoot = new HashSet<string>();
        foreach (string r in roots)
        {
            if (string.IsNullOrEmpty(r)) continue;
            string key = r.ToLower();
            if (seenRoot.Contains(key)) continue;
            seenRoot.Add(key);
            if (!Directory.Exists(r)) continue;

            FolderItem rootItem = new FolderItem();
            rootItem.Path = r;
            rootItem.Name = r.TrimEnd('\\');
            res.Folders.Add(rootItem);

            Walk(r, res, progress, cancelled);
            if (res.Files.Count > 300000 || res.Folders.Count > 200000) break;
            if (cancelled != null && cancelled()) break;
        }
        res.Files.Sort(delegate (DocItem x, DocItem y) { return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase); });
        res.Folders.Sort(delegate (FolderItem x, FolderItem y) { return string.Compare(x.Path, y.Path, StringComparison.OrdinalIgnoreCase); });
        return res;
    }

    static void Walk(string root, ScanResult res, Action<int, int> progress, Func<bool> cancelled)
    {
        Stack<string> stack = new Stack<string>();
        stack.Push(root);
        int tick = 0;
        while (stack.Count > 0)
        {
            if (res.Files.Count > 300000 || res.Folders.Count > 200000) return;
            if (cancelled != null && cancelled()) return;
            string dir = stack.Pop();

            string[] subs = null;
            try { subs = Directory.GetDirectories(dir); } catch { subs = null; }
            if (subs != null)
            {
                foreach (string s in subs)
                {
                    try
                    {
                        if (Skip(s)) continue;
                        FolderItem fi = new FolderItem();
                        fi.Path = s;
                        fi.Name = Path.GetFileName(s.TrimEnd('\\'));
                        if (string.IsNullOrEmpty(fi.Name)) fi.Name = s;
                        res.Folders.Add(fi);
                        stack.Push(s);
                    }
                    catch { }
                }
            }

            string[] files = null;
            try { files = Directory.GetFiles(dir); } catch { files = null; }
            if (files == null) continue;
            foreach (string f in files)
            {
                string ext = Path.GetExtension(f);
                if (string.IsNullOrEmpty(ext)) continue;
                ext = ext.ToLowerInvariant();
                if (!map.ContainsKey(ext)) continue;
                DocItem it = new DocItem();
                it.Path = f;
                it.Name = Path.GetFileName(f);
                it.Ext = ext;
                it.Cat = map[ext];
                try
                {
                    FileInfo fi = new FileInfo(f);
                    it.Size = fi.Length;
                    it.Modified = fi.LastWriteTime;
                }
                catch { }
                res.Files.Add(it);
                tick++;
                if (progress != null && (tick % 500 == 0)) progress(res.Files.Count, res.Folders.Count);
            }
        }
        if (progress != null) progress(res.Files.Count, res.Folders.Count);
    }
}


class CatView
{
    public TabPage Page;
    public ListView List;
    public TextBox Search;
    public Label State;
    public ProgressBar Progress;
    public Button Scan, All, None, Del, DelHard;
    public ComboBox CatCombo;
    public List<DocItem> View = new List<DocItem>();
    public List<bool> Chk = new List<bool>();
    public string Cat = null;
    public bool IsDocPage = false;
    public bool Dirty = true;
    // 排序：-1 表示按扫描顺序，其它为列序号；SortAsc 为升序
    public int SortCol = -1;
    public bool SortAsc = true;
    // 记录每一行最近一次自绘时的区域，用于按行精确重绘
    public Dictionary<int, Rectangle> Rects = new Dictionary<int, Rectangle>();
}

class FolderView
{
    public TabPage Page;
    public ListView List;
    public TextBox Search;
    public Label State;
    public ProgressBar Progress;
    public Button Scan, OpenAll;
    public List<FolderItem> View = new List<FolderItem>();
    public bool Dirty = true;
    public int SortCol = -1;
    public bool SortAsc = true;
}

class MainForm : Form
{
    TabControl tabs = new TabControl();
    TabPage pageApps = new TabPage("软件管理");
    TabPage pageRogue = new TabPage("疑似流氓软件");

    ListView lvApps = new ListView();
    ListView lvRogue = new ListView();

    Label lblAppState = new Label();
    Label lblRogueState = new Label();

    Button btnAppRefresh, btnAppAll, btnAppNone, btnAppUninstall;
    Button btnRogueRefresh, btnRogueUninstall;
    TextBox tbSearch = new TextBox();
    ProgressBar prgApps = new ProgressBar();
    ProgressBar prgRogue = new ProgressBar();
    CheckBox chkHigh = new CheckBox();

    CatView vDocs, vImages, vVideos, vArchives;
    FolderView vFolders;
    AppSorter appSorter = new AppSorter();

    bool scanningDocs = false;
    volatile bool cancelDocScan = false;
    bool deletingDocs = false;

    List<AppInfo> apps = new List<AppInfo>();
    List<DocItem> docs = new List<DocItem>();
    List<FolderItem> folders = new List<FolderItem>();
    bool busy = false;

    public MainForm()
    {
        Text = "轻量软件管理器";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 700);
        MinimumSize = new Size(1180, 620);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.None;
        BuildUI();
        Load += delegate
        {
            string test = Environment.GetEnvironmentVariable("LM_TEST");
            if (test == "docs") tabs.SelectedIndex = 2;
            else if (test == "rogue") tabs.SelectedIndex = 1;
            else if (test == "images") tabs.SelectedIndex = 3;
            else if (test == "videos") tabs.SelectedIndex = 4;
            else if (test == "archives") tabs.SelectedIndex = 5;
            else if (test == "folders") tabs.SelectedIndex = 6;

            RefreshApps();

            // 打开软件后自动全盘扫描
            BeginInvoke(new Action(delegate { ScanDocs(); }));
        };
    }

    static Button MkButton(string text, bool primary, int width)
    {
        Button b = new Button();
        b.Text = text;
        b.Width = width;
        b.Height = 30;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.UseVisualStyleBackColor = false;
        if (primary)
        {
            b.BackColor = Color.FromArgb(38, 94, 214);
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = Color.FromArgb(38, 94, 214);
        }
        else
        {
            b.BackColor = Color.White;
            b.ForeColor = Color.FromArgb(45, 48, 54);
            b.FlatAppearance.BorderColor = Color.FromArgb(196, 200, 208);
        }
        b.TabStop = false;
        return b;
    }

    static Label MkState()
    {
        Label l = new Label();
        l.Dock = DockStyle.Right;
        l.Width = 300;
        l.TextAlign = ContentAlignment.MiddleRight;
        l.ForeColor = Color.FromArgb(98, 104, 112);
        l.Text = "";
        return l;
    }

    static Label MkSearchLabel()
    {
        Label l = new Label();
        l.Text = "搜索：";
        l.AutoSize = false;
        l.Width = 60;
        l.Height = 22;
        l.TextAlign = ContentAlignment.MiddleLeft;
        l.ForeColor = Color.FromArgb(90, 96, 104);
        l.Location = new Point(6, 11);
        return l;
    }

    static TextBox MkSearchBox(EventHandler onChange, int width)
    {
        TextBox t = new TextBox();
        t.BorderStyle = BorderStyle.FixedSingle;
        t.BackColor = Color.White;
        t.Width = width;
        t.Location = new Point(68, 11);
        t.TextChanged += onChange;
        return t;
    }

    static ListView MkList()
    {
        ListView lv = new ListView();
        lv.View = View.Details;
        lv.FullRowSelect = true;
        lv.CheckBoxes = true;
        lv.GridLines = true;
        lv.HideSelection = false;
        lv.Dock = DockStyle.Fill;
        lv.BorderStyle = BorderStyle.None;
        lv.MultiSelect = true;
        lv.Font = new Font("Microsoft YaHei UI", 9F);

        ImageList il = new ImageList();
        il.ImageSize = new Size(1, 22);
        il.ColorDepth = ColorDepth.Depth32Bit;
        Bitmap dot = new Bitmap(1, 22);
        Graphics g = Graphics.FromImage(dot);
        g.Clear(Color.Transparent);
        g.Dispose();
        il.Images.Add(dot);
        lv.SmallImageList = il;
        EnableDoubleBuffer(lv);
        return lv;
    }

    static void InvalidateRows(CatView v, int first, int last)
    {
        if (first < 0) return;
        Rectangle a, b;
        bool got = v.Rects.TryGetValue(first, out a) & v.Rects.TryGetValue(last, out b);
        if (got && a.Height > 0 && b.Height > 0)
        {
            RepaintRows(v, Math.Min(a.Top, b.Top), Math.Max(a.Bottom, b.Bottom));
            return;
        }
        try
        {
            a = v.List.GetItemRect(first, ItemBoundsPortion.Entire);
            b = v.List.GetItemRect(last, ItemBoundsPortion.Entire);
            RepaintRows(v, Math.Min(a.Top, b.Top), Math.Max(a.Bottom, b.Bottom));
            return;
        }
        catch { }
        v.List.Invalidate();
    }

    // ================== 排序 ==================

    static readonly string[] DocHeaders = { "文件名", "类型", "格式", "大小", "修改时间", "完整路径", "操作" };

    static int CmpDoc(DocItem a, DocItem b, int col)
    {
        if (a == null) return (b == null) ? 0 : -1;
        if (b == null) return 1;
        switch (col)
        {
            case 0: return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            case 1: return string.Compare(a.Cat, b.Cat, StringComparison.OrdinalIgnoreCase);
            case 2: return string.Compare(a.Ext, b.Ext, StringComparison.OrdinalIgnoreCase);
            case 3: return a.Size.CompareTo(b.Size);
            case 4: return a.Modified.CompareTo(b.Modified);
            case 5: return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            default: return 0;
        }
    }

    void UpdateDocHeaders(CatView v)
    {
        for (int i = 0; i < v.List.Columns.Count && i < DocHeaders.Length; i++)
        {
            string t = DocHeaders[i];
            if (i == v.SortCol) t += v.SortAsc ? " ▲" : " ▼";
            v.List.Columns[i].Text = t;
        }
    }

    void SortCatView(CatView v, int col)
    {
        if (col < 0 || col > 5) return;                       // 操作列不参与排序
        if (v.SortCol == col) v.SortAsc = !v.SortAsc;
        else { v.SortCol = col; v.SortAsc = true; }
        ApplyCatSort(v);
    }

    /// <summary>按 v.SortCol / v.SortAsc 重排 View，并同步勾选状态。</summary>
    void ApplyCatSort(CatView v)
    {
        if (v.SortCol < 0 || v.View.Count == 0) { UpdateDocHeaders(v); return; }

        int col = v.SortCol;
        bool asc = v.SortAsc;
        List<int> order = new List<int>(v.View.Count);
        for (int i = 0; i < v.View.Count; i++) order.Add(i);
        order.Sort(delegate (int x, int y)
        {
            int c = CmpDoc(v.View[x], v.View[y], col);
            if (c == 0 && col != 0) c = CmpDoc(v.View[x], v.View[y], 0);
            return asc ? c : -c;
        });

        List<DocItem> nv = new List<DocItem>(order.Count);
        List<bool> nc = new List<bool>(order.Count);
        for (int k = 0; k < order.Count; k++)
        {
            nv.Add(v.View[order[k]]);
            nc.Add(k < v.Chk.Count ? v.Chk[order[k]] : false);
        }
        v.View = nv;
        v.Chk = nc;
        v.Rects.Clear();
        v.List.VirtualListSize = 0;
        v.List.VirtualListSize = v.View.Count;
        v.List.Invalidate();
        UpdateDocHeaders(v);
    }

    void SortFolderView(FolderView f, int col)
    {
        if (col < 0 || col > 1) return;
        if (f.SortCol == col) f.SortAsc = !f.SortAsc;
        else { f.SortCol = col; f.SortAsc = true; }
        ApplyFolderSort(f);
    }

    void ApplyFolderSort(FolderView f)
    {
        if (f.SortCol < 0 || f.View.Count == 0) { UpdateFolderHeaders(f); return; }

        int col = f.SortCol;
        bool asc = f.SortAsc;
        List<int> order = new List<int>(f.View.Count);
        for (int i = 0; i < f.View.Count; i++) order.Add(i);
        order.Sort(delegate (int x, int y)
        {
            int c;
            if (col == 0) c = string.Compare(f.View[x].Name, f.View[y].Name, StringComparison.OrdinalIgnoreCase);
            else c = string.Compare(f.View[x].Path, f.View[y].Path, StringComparison.OrdinalIgnoreCase);
            if (c == 0 && col != 0) c = string.Compare(f.View[x].Name, f.View[y].Name, StringComparison.OrdinalIgnoreCase);
            return asc ? c : -c;
        });

        List<FolderItem> nv = new List<FolderItem>(order.Count);
        for (int k = 0; k < order.Count; k++) nv.Add(f.View[order[k]]);
        f.View = nv;
        f.List.VirtualListSize = 0;
        f.List.VirtualListSize = f.View.Count;
        f.List.Invalidate();
        UpdateFolderHeaders(f);
    }

    void UpdateFolderHeaders(FolderView f)
    {
        string[] baseNames = { "文件夹名", "完整路径", "操作" };
        for (int i = 0; i < f.List.Columns.Count && i < baseNames.Length; i++)
        {
            string t = baseNames[i];
            if (i == f.SortCol) t += f.SortAsc ? " ▲" : " ▼";
            f.List.Columns[i].Text = t;
        }
    }

    static readonly string[] AppHeaders = { "软件名称", "版本", "发布者", "大小", "安装日期", "安装位置", "状态" };

    void UpdateAppHeaders()
    {
        for (int i = 0; i < lvApps.Columns.Count && i < AppHeaders.Length; i++)
        {
            string t = AppHeaders[i];
            if (i == appSorter.Col) t += appSorter.Asc ? " ▲" : " ▼";
            lvApps.Columns[i].Text = t;
        }
    }

    static Panel MkTop()
    {
        Panel p = new Panel();
        p.Dock = DockStyle.Top;
        p.Height = 46;
        p.BackColor = Color.FromArgb(246, 247, 249);
        p.Padding = new Padding(8, 7, 8, 0);
        return p;
    }

    static ProgressBar MkProgress(int width)
    {
        ProgressBar p = new ProgressBar();
        p.Minimum = 0;
        p.Maximum = 100;
        p.Value = 0;
        p.Width = width;
        p.Height = 14;
        p.Style = ProgressBarStyle.Continuous;
        p.Visible = false;
        return p;
    }

    static void SetProgress(ProgressBar p, int percent)
    {
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;
        if (p.Style != ProgressBarStyle.Continuous) p.Style = ProgressBarStyle.Continuous;
        p.Value = percent;
    }

    static void MarqueeOn(ProgressBar p)
    {
        p.Style = ProgressBarStyle.Marquee;
        p.MarqueeAnimationSpeed = 60;
        p.Visible = true;
    }

    static void ProgressOff(ProgressBar p)
    {
        p.Style = ProgressBarStyle.Continuous;
        p.Value = 0;
        p.Visible = false;
    }

    void BuildUI()
    {
        tabs.Dock = DockStyle.Fill;
        tabs.Font = new Font("Microsoft YaHei UI", 9F);
        tabs.Padding = new Point(14, 4);
        Controls.Add(tabs);

        tabs.TabPages.Add(pageApps);
        tabs.TabPages.Add(pageRogue);

        BuildAppsPage();
        BuildRoguePage();

        vDocs = BuildCatPage("文档管理", null, true);
        vImages = BuildCatPage("图片管理", "图片", false);
        vVideos = BuildCatPage("视频管理", "视频", false);
        vArchives = BuildCatPage("压缩文件管理", "压缩文件", false);
        vFolders = BuildFolderPage();

        tabs.SelectedIndexChanged += delegate { EnsureFillSelected(); };
        tabs.SelectedIndex = 0;
    }

    void BuildAppsPage()
    {
        Panel top1 = MkTop();
        lblAppState = MkState();

        tbSearch = MkSearchBox(delegate { FillApps(); }, 144);

        btnAppRefresh = MkButton("刷新列表", false, 92);
        btnAppUninstall = MkButton("强制全量卸载", true, 118);
        btnAppAll = MkButton("全选", false, 64);
        btnAppNone = MkButton("取消全选", false, 90);
        btnAppRefresh.Location = new Point(226, 8);
        btnAppUninstall.Location = new Point(324, 8);
        btnAppAll.Location = new Point(448, 8);
        btnAppNone.Location = new Point(518, 8);
        btnAppRefresh.Click += delegate { RefreshApps(); };
        btnAppUninstall.Click += delegate { UninstallSelectedApps(lvApps); };
        btnAppAll.Click += delegate { CheckAll(lvApps, true); };
        btnAppNone.Click += delegate { CheckAll(lvApps, false); };

        prgApps = MkProgress(120);
        prgApps.Location = new Point(618, 15);

        top1.Controls.Add(MkSearchLabel());
        top1.Controls.Add(tbSearch);
        top1.Controls.Add(btnAppRefresh);
        top1.Controls.Add(btnAppUninstall);
        top1.Controls.Add(btnAppAll);
        top1.Controls.Add(btnAppNone);
        top1.Controls.Add(prgApps);
        top1.Controls.Add(lblAppState);

        lvApps = MkList();
        lvApps.Columns.Add("软件名称", 300);
        lvApps.Columns.Add("版本", 110);
        lvApps.Columns.Add("发布者", 180);
        lvApps.Columns.Add("大小", 82, HorizontalAlignment.Right);
        lvApps.Columns.Add("安装日期", 92);
        lvApps.Columns.Add("安装位置", 340);
        lvApps.Columns.Add("状态", 96);
        lvApps.DoubleClick += delegate { OpenLocationOf(lvApps); };
        lvApps.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Delete) UninstallSelectedApps(lvApps); };
        lvApps.ColumnClick += delegate (object s, ColumnClickEventArgs e)
        {
            if (e.Column < 0 || e.Column > 6) return;
            if (appSorter.Col == e.Column) appSorter.Asc = !appSorter.Asc;
            else { appSorter.Col = e.Column; appSorter.Asc = true; }
            lvApps.ListViewItemSorter = appSorter;
            lvApps.Sort();
            UpdateAppHeaders();
        };
        pageApps.Controls.Add(lvApps);
        pageApps.Controls.Add(top1);

        ContextMenuStrip cm2 = new ContextMenuStrip();
        cm2.Items.Add("打开安装位置", null, delegate { OpenLocationOf(lvApps); });
        cm2.Items.Add("强制全量卸载", null, delegate { UninstallSelectedApps(lvApps); });
        lvApps.ContextMenuStrip = cm2;
    }

    void BuildRoguePage()
    {
        Panel top2 = MkTop();
        lblRogueState = MkState();
        btnRogueRefresh = MkButton("重新扫描", false, 92);
        btnRogueUninstall = MkButton("卸载选中", true, 100);
        chkHigh = new CheckBox();
        chkHigh.Text = "仅显示高风险(≥70)";
        chkHigh.AutoSize = true;
        chkHigh.ForeColor = Color.FromArgb(45, 48, 54);
        btnRogueRefresh.Location = new Point(8, 8);
        btnRogueUninstall.Location = new Point(108, 8);
        chkHigh.Location = new Point(222, 14);
        chkHigh.CheckedChanged += delegate { FillRogue(); };
        btnRogueRefresh.Click += delegate { RefreshApps(); };
        btnRogueUninstall.Click += delegate { UninstallSelectedApps(lvRogue); };

        prgRogue = MkProgress(120);
        prgRogue.Location = new Point(380, 15);

        top2.Controls.Add(btnRogueRefresh);
        top2.Controls.Add(btnRogueUninstall);
        top2.Controls.Add(chkHigh);
        top2.Controls.Add(prgRogue);
        top2.Controls.Add(lblRogueState);

        lvRogue = MkList();
        lvRogue.Columns.Add("软件名称", 260);
        lvRogue.Columns.Add("发布者", 150);
        lvRogue.Columns.Add("风险分", 66, HorizontalAlignment.Right);
        lvRogue.Columns.Add("判定依据", 420);
        lvRogue.Columns.Add("安装位置", 300);
        lvRogue.DoubleClick += delegate { OpenLocationOf(lvRogue); };
        pageRogue.Controls.Add(lvRogue);
        pageRogue.Controls.Add(top2);

        ContextMenuStrip cm3 = new ContextMenuStrip();
        cm3.Items.Add("打开安装位置", null, delegate { OpenLocationOf(lvRogue); });
        cm3.Items.Add("卸载选中", null, delegate { UninstallSelectedApps(lvRogue); });
        lvRogue.ContextMenuStrip = cm3;
    }

    CatView BuildCatPage(string title, string cat, bool withCombo)
    {
        CatView v = new CatView();
        v.Cat = cat;
        v.IsDocPage = withCombo;
        v.Page = new TabPage(title);
        v.Page.BackColor = Color.White;

        Panel top = MkTop();
        v.State = MkState();

        v.Search = MkSearchBox(delegate { FillCatView(v); }, 144);

        int x = 232;
        if (withCombo)
        {
            v.CatCombo = new ComboBox();
            v.CatCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            v.CatCombo.Width = 100;
            v.CatCombo.Location = new Point(x, 11);
            v.CatCombo.Items.Add("全部文档");
            foreach (string c in DocScanner.DocCategories) v.CatCombo.Items.Add(c);
            v.CatCombo.SelectedIndex = 0;
            v.CatCombo.SelectedIndexChanged += delegate { FillCatView(v); };
            top.Controls.Add(v.CatCombo);
            x = 338;
        }

        v.Scan = MkButton("全盘扫描", true, 84);
        v.Scan.Location = new Point(x, 8);
        v.Scan.Click += delegate { ScanDocs(); };
        x += 90;

        v.All = MkButton("全选", false, 56);
        v.All.Location = new Point(x, 8);
        v.All.Click += delegate { CheckPage(v, true); };
        x += 62;

        v.None = MkButton("取消全选", false, 84);
        v.None.Location = new Point(x, 8);
        v.None.Click += delegate { CheckPage(v, false); };
        x += 90;

        v.Del = MkButton("删除", false, 64);
        v.Del.Location = new Point(x, 8);
        v.Del.Click += delegate { DeleteDocsPage(v, true); };
        x += 70;

        v.DelHard = MkButton("彻底删除", false, 84);
        v.DelHard.Location = new Point(x, 8);
        v.DelHard.Click += delegate { DeleteDocsPage(v, false); };
        x += 90;

        v.Progress = MkProgress(100);
        v.Progress.Location = new Point(x, 15);

        top.Controls.Add(MkSearchLabel());
        top.Controls.Add(v.Search);
        top.Controls.Add(v.Scan);
        top.Controls.Add(v.All);
        top.Controls.Add(v.None);
        top.Controls.Add(v.Del);
        top.Controls.Add(v.DelHard);
        top.Controls.Add(v.Progress);
        top.Controls.Add(v.State);

        v.List = MkList();
        v.List.Columns.Add("文件名", 296);
        v.List.Columns.Add("类型", 92);
        v.List.Columns.Add("格式", 60);
        v.List.Columns.Add("大小", 82, HorizontalAlignment.Right);
        v.List.Columns.Add("修改时间", 156);
        v.List.Columns.Add("完整路径", 328);
        v.List.Columns.Add("操作", 76, HorizontalAlignment.Center);

        SetupDocList(v);

        v.Page.Controls.Add(v.List);
        v.Page.Controls.Add(top);
        tabs.TabPages.Add(v.Page);
        return v;
    }

    DocItem GetDoc(CatView v, int i)
    {
        return (i >= 0 && i < v.View.Count) ? v.View[i] : null;
    }

    static bool GetChk(CatView v, int i)
    {
        return i >= 0 && i < v.Chk.Count && v.Chk[i];
    }

    void ToggleChk(CatView v, int i)
    {
        if (i < 0 || i >= v.Chk.Count) return;
        v.Chk[i] = !v.Chk[i];
        InvalidateRow(v, i);
    }

    static void EnableDoubleBuffer(Control c)
    {
        try
        {
            System.Reflection.PropertyInfo pi = typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (pi != null) pi.SetValue(c, true, null);
        }
        catch { }
    }

    /// <summary>只重画这一行，避免整表闪白。</summary>
    static void InvalidateRow(CatView v, int i)
    {
        Rectangle r;
        if (v.Rects.TryGetValue(i, out r) && r.Height > 0) { RepaintRows(v, r.Top, r.Bottom); return; }
        try
        {
            r = v.List.GetItemRect(i, ItemBoundsPortion.Entire);
            if (r.Height > 0) { RepaintRows(v, r.Top, r.Bottom); return; }
        }
        catch { }
        v.List.Invalidate();
    }

    static void RepaintRows(CatView v, int top, int bottom)
    {
        int left = 0;
        int width = v.List.ClientSize.Width;
        int t = Math.Max(0, top);
        int b = Math.Min(v.List.ClientSize.Height, bottom);
        if (b - t <= 0) return;
        v.List.Invalidate(new Rectangle(left, t, width, b - t));
    }

    static void DrawCheck(Graphics g, Rectangle cell, bool on)
    {
        Rectangle box = new Rectangle(cell.Left + 4, cell.Top + (cell.Height - 13) / 2, 13, 13);
        if (on)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 120, 215))) g.FillRectangle(b, box);
            using (Pen wp = new Pen(Color.White, 1.6f))
            {
                g.DrawLine(wp, box.Left + 3, box.Top + 7, box.Left + 6, box.Top + 10);
                g.DrawLine(wp, box.Left + 6, box.Top + 10, box.Left + 11, box.Top + 4);
            }
        }
        else
        {
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, box);
            using (Pen p = new Pen(Color.FromArgb(130, 140, 150))) g.DrawRectangle(p, box.Left, box.Top, box.Width - 1, box.Height - 1);
        }
    }

    ListViewItem MakeDocRow(CatView v, int i)
    {
        DocItem d = GetDoc(v, i);
        ListViewItem it = new ListViewItem(d == null ? "" : d.Name);
        it.SubItems.Add(d == null ? "" : d.Cat);
        it.SubItems.Add(d == null ? "" : d.Ext);
        it.SubItems.Add(d == null ? "" : d.SizeText());
        it.SubItems.Add(d == null ? "" : d.Modified.ToString("yyyy-MM-dd HH:mm"));
        it.SubItems.Add(d == null ? "" : d.Path);
        it.SubItems.Add("转到");
        it.Tag = i;
        return it;
    }

    static void DrawCell(Graphics g, Rectangle cell, string text, Font f, Color c, bool right)
    {
        if (cell.Width <= 4) return;
        Rectangle r = Rectangle.FromLTRB(cell.Left + 4, cell.Top, cell.Right - 4, cell.Bottom);
        TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        if (right) flags |= TextFormatFlags.Right;
        TextRenderer.DrawText(g, text == null ? "" : text, f, r, c, flags);
    }

    void DrawDocRow(CatView v, DrawListViewItemEventArgs e)
    {
        int idx = e.ItemIndex;
        DocItem d = GetDoc(v, idx);
        bool sel = v.List.SelectedIndices.Contains(idx);
        Rectangle row = e.Bounds;
        if (row.Height > 0) v.Rects[idx] = row;
        Graphics g = e.Graphics;
        using (SolidBrush b = new SolidBrush(sel ? SystemColors.Highlight : Color.White)) g.FillRectangle(b, row);
        Color fg = sel ? SystemColors.HighlightText : Color.Black;

        int x = row.Left;
        Rectangle c0 = new Rectangle(x, row.Top, v.List.Columns[0].Width, row.Height);
        DrawCheck(g, c0, GetChk(v, idx));
        DrawCell(g, Rectangle.FromLTRB(c0.Left + 22, c0.Top, c0.Right, c0.Bottom), d == null ? "" : d.Name, v.List.Font, fg, false);
        x += v.List.Columns[0].Width;

        Rectangle c1 = new Rectangle(x, row.Top, v.List.Columns[1].Width, row.Height);
        DrawCell(g, c1, d == null ? "" : d.Cat, v.List.Font, fg, false);
        x += v.List.Columns[1].Width;

        Rectangle c2 = new Rectangle(x, row.Top, v.List.Columns[2].Width, row.Height);
        DrawCell(g, c2, d == null ? "" : d.Ext, v.List.Font, fg, false);
        x += v.List.Columns[2].Width;

        Rectangle c3 = new Rectangle(x, row.Top, v.List.Columns[3].Width, row.Height);
        DrawCell(g, c3, d == null ? "" : d.SizeText(), v.List.Font, fg, true);
        x += v.List.Columns[3].Width;

        Rectangle c4 = new Rectangle(x, row.Top, v.List.Columns[4].Width, row.Height);
        DrawCell(g, c4, d == null ? "" : d.Modified.ToString("yyyy-MM-dd HH:mm"), v.List.Font, fg, false);
        x += v.List.Columns[4].Width;

        Rectangle c5 = new Rectangle(x, row.Top, v.List.Columns[5].Width, row.Height);
        DrawCell(g, c5, d == null ? "" : d.Path, v.List.Font, fg, false);
        x += v.List.Columns[5].Width;

        Rectangle c6 = new Rectangle(x, row.Top, v.List.Columns[6].Width, row.Height);
        Rectangle btn = new Rectangle(c6.X + 8, c6.Y + 4, c6.Width - 16, c6.Height - 9);
        using (SolidBrush b2 = new SolidBrush(sel ? Color.FromArgb(214, 229, 252) : Color.White)) g.FillRectangle(b2, btn);
        using (Pen p = new Pen(Color.FromArgb(150, 168, 210))) g.DrawRectangle(p, btn);
        TextRenderer.DrawText(g, "转到", v.List.Font, btn, Color.FromArgb(38, 94, 214),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        using (Pen lp = new Pen(Color.FromArgb(232, 234, 237))) g.DrawLine(lp, row.Left, row.Bottom - 1, row.Right, row.Bottom - 1);
    }

    void SetupDocList(CatView v)
    {
        v.List.CheckBoxes = false;
        v.List.VirtualMode = true;
        v.List.RetrieveVirtualItem += delegate (object s, RetrieveVirtualItemEventArgs e)
        {
            e.Item = MakeDocRow(v, e.ItemIndex);
        };

        v.List.OwnerDraw = true;
        v.List.DrawColumnHeader += delegate (object s, DrawListViewColumnHeaderEventArgs e) { e.DrawDefault = true; };
        v.List.DrawItem += delegate (object s, DrawListViewItemEventArgs e) { DrawDocRow(v, e); };
        v.List.DrawSubItem += delegate (object s, DrawListViewSubItemEventArgs e) { };
        v.List.ColumnClick += delegate (object s, ColumnClickEventArgs e) { SortCatView(v, e.Column); };

        v.List.MouseDown += delegate (object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ListViewHitTestInfo hit = v.List.HitTest(e.X, e.Y);
            if (hit.Item == null || hit.Item.Index < 0) return;
            if (e.X <= 22) ToggleChk(v, hit.Item.Index);
        };

        v.List.MouseUp += delegate (object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.Clicks > 1) return;
            if (e.X <= 22) return;
            ListViewHitTestInfo hit = v.List.HitTest(e.X, e.Y);
            if (hit.Item != null && hit.SubItem != null && hit.SubItem.Text == "转到") JumpToDoc(v, hit.Item.Index);
        };
        v.List.DoubleClick += delegate
        {
            Point p = v.List.PointToClient(Cursor.Position);
            if (p.X <= 22) return;
            ListViewHitTestInfo hit = v.List.HitTest(p.X, p.Y);
            if (hit.SubItem != null && hit.SubItem.Text == "转到") return;
            OpenDocLocation(v);
        };
        v.List.KeyDown += delegate (object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) DeleteDocsPage(v, true);
            else if (e.KeyCode == Keys.Enter) OpenDocLocation(v);
            else if (e.KeyCode == Keys.Space)
            {
                int first = -1, last = -1;
                for (int k = 0; k < v.List.SelectedIndices.Count; k++)
                {
                    int idx = v.List.SelectedIndices[k];
                    if (idx < v.Chk.Count) v.Chk[idx] = !v.Chk[idx];
                    if (first < 0 || idx < first) first = idx;
                    if (idx > last) last = idx;
                }
                InvalidateRows(v, first, last);
                e.Handled = true;
            }
        };

        ContextMenuStrip cm = new ContextMenuStrip();
        cm.Items.Add("打开所在文件夹", null, delegate { OpenDocLocation(v); });
        cm.Items.Add("删除(回收站)", null, delegate { DeleteDocsPage(v, true); });
        cm.Items.Add("彻底删除", null, delegate { DeleteDocsPage(v, false); });
        v.List.ContextMenuStrip = cm;
    }

    FolderView BuildFolderPage()
    {
        FolderView f = new FolderView();
        f.Page = new TabPage("文件夹管理");
        f.Page.BackColor = Color.White;

        Panel top = MkTop();
        f.State = MkState();
        f.Search = MkSearchBox(delegate { FillFolderView(f); }, 144);

        f.Scan = MkButton("全盘扫描", true, 84);
        f.OpenAll = MkButton("打开选中文件夹", false, 120);
        f.Scan.Location = new Point(232, 8);
        f.OpenAll.Location = new Point(322, 8);
        f.Scan.Click += delegate { ScanDocs(); };
        f.OpenAll.Click += delegate { OpenSelectedFolders(); };

        f.Progress = MkProgress(120);
        f.Progress.Location = new Point(452, 15);

        top.Controls.Add(MkSearchLabel());
        top.Controls.Add(f.Search);
        top.Controls.Add(f.Scan);
        top.Controls.Add(f.OpenAll);
        top.Controls.Add(f.Progress);
        top.Controls.Add(f.State);

        f.List = MkList();
        f.List.Columns.Add("文件夹名", 360);
        f.List.Columns.Add("完整路径", 580);
        f.List.Columns.Add("操作", 128, HorizontalAlignment.Center);
        f.List.CheckBoxes = false;
        f.List.VirtualMode = true;
        f.List.RetrieveVirtualItem += delegate (object s, RetrieveVirtualItemEventArgs e)
        {
            FolderItem d = (e.ItemIndex >= 0 && e.ItemIndex < f.View.Count) ? f.View[e.ItemIndex] : null;
            ListViewItem it = new ListViewItem(d == null ? "" : d.Name);
            it.SubItems.Add(d == null ? "" : d.Path);
            it.SubItems.Add("打开此文件夹");
            it.Tag = e.ItemIndex;
            e.Item = it;
        };

        f.List.OwnerDraw = true;
        f.List.DrawColumnHeader += delegate (object s, DrawListViewColumnHeaderEventArgs e) { e.DrawDefault = true; };
        f.List.ColumnClick += delegate (object s, ColumnClickEventArgs e) { SortFolderView(f, e.Column); };
        f.List.DrawItem += delegate (object s, DrawListViewItemEventArgs e)
        {
            int idx = e.ItemIndex;
            FolderItem d = (idx >= 0 && idx < f.View.Count) ? f.View[idx] : null;
            bool sel = f.List.SelectedIndices.Contains(idx);
            Rectangle row = e.Bounds;
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(sel ? SystemColors.Highlight : Color.White)) g.FillRectangle(b, row);
            Color fg = sel ? SystemColors.HighlightText : Color.Black;

            int x = row.Left;
            Rectangle c0 = new Rectangle(x, row.Top, f.List.Columns[0].Width, row.Height);
            DrawCell(g, c0, d == null ? "" : d.Name, f.List.Font, fg, false);
            x += f.List.Columns[0].Width;

            Rectangle c1 = new Rectangle(x, row.Top, f.List.Columns[1].Width, row.Height);
            DrawCell(g, c1, d == null ? "" : d.Path, f.List.Font, fg, false);
            x += f.List.Columns[1].Width;

            Rectangle c2 = new Rectangle(x, row.Top, f.List.Columns[2].Width, row.Height);
            Rectangle btn = new Rectangle(c2.X + 9, c2.Y + 4, c2.Width - 18, c2.Height - 9);
            using (SolidBrush b2 = new SolidBrush(sel ? Color.FromArgb(214, 229, 252) : Color.White)) g.FillRectangle(b2, btn);
            using (Pen p = new Pen(Color.FromArgb(150, 168, 210))) g.DrawRectangle(p, btn);
            TextRenderer.DrawText(g, "打开此文件夹", f.List.Font, btn, sel ? SystemColors.HighlightText : Color.FromArgb(38, 94, 214),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            using (Pen lp = new Pen(Color.FromArgb(232, 234, 237))) g.DrawLine(lp, row.Left, row.Bottom - 1, row.Right, row.Bottom - 1);
        };
        f.List.DrawSubItem += delegate (object s, DrawListViewSubItemEventArgs e) { };

        f.List.MouseUp += delegate (object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.Clicks > 1) return;
            ListViewHitTestInfo hit = f.List.HitTest(e.X, e.Y);
            if (hit.Item != null && hit.SubItem != null && hit.SubItem.Text == "打开此文件夹") OpenFolderItem(hit.Item.Index);
        };
        f.List.DoubleClick += delegate
        {
            Point p = f.List.PointToClient(Cursor.Position);
            ListViewHitTestInfo hit = f.List.HitTest(p.X, p.Y);
            if (hit.SubItem != null && hit.SubItem.Text == "打开此文件夹") return;
            OpenSelectedFolders();
        };
        f.List.KeyDown += delegate (object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) OpenSelectedFolders();
        };

        ContextMenuStrip cm4 = new ContextMenuStrip();
        cm4.Items.Add("打开此文件夹", null, delegate { OpenSelectedFolders(); });
        f.List.ContextMenuStrip = cm4;

        f.Page.Controls.Add(f.List);
        f.Page.Controls.Add(top);
        tabs.TabPages.Add(f.Page);
        return f;
    }

    // ============ 通用 ============
    static List<T> GetSelected<T>(ListView lv)
    {
        HashSet<T> set = new HashSet<T>();
        List<T> ordered = new List<T>();
        foreach (ListViewItem it in lv.CheckedItems)
        {
            T t = (T)it.Tag;
            if (t != null && !set.Contains(t)) { set.Add(t); ordered.Add(t); }
        }
        if (ordered.Count == 0)
        {
            foreach (ListViewItem it in lv.SelectedItems)
            {
                T t = (T)it.Tag;
                if (t != null && !set.Contains(t)) { set.Add(t); ordered.Add(t); }
            }
        }
        return ordered;
    }

    static void CheckAll(ListView lv, bool on)
    {
        lv.BeginUpdate();
        foreach (ListViewItem it in lv.Items) it.Checked = on;
        lv.EndUpdate();
    }

    void EnsureFillSelected()
    {
        if (scanningDocs) return;
        TabPage p = tabs.SelectedTab;
        if (p == null) return;
        if (vDocs != null && p == vDocs.Page && vDocs.Dirty) { FillCatView(vDocs); return; }
        if (vImages != null && p == vImages.Page && vImages.Dirty) { FillCatView(vImages); return; }
        if (vVideos != null && p == vVideos.Page && vVideos.Dirty) { FillCatView(vVideos); return; }
        if (vArchives != null && p == vArchives.Page && vArchives.Dirty) { FillCatView(vArchives); return; }
        if (vFolders != null && p == vFolders.Page && vFolders.Dirty) { FillFolderView(vFolders); }
    }

    void OpenLocationOf(ListView lv)
    {
        if (lv.SelectedItems.Count == 0) return;
        object tag = lv.SelectedItems[0].Tag;
        string path = null;
        if (tag is AppInfo)
        {
            AppInfo a = (AppInfo)tag;
            if (!string.IsNullOrEmpty(a.InstallLocation) && Directory.Exists(a.InstallLocation)) path = a.InstallLocation;
            else if (File.Exists(a.InstallLocation)) path = a.InstallLocation;
        }
        if (path == null) { MessageBox.Show("没有可打开的位置。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        try
        {
            if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
            else Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    void OpenDocLocation(CatView v)
    {
        if (v.List.SelectedIndices.Count == 0) return;
        int idx = v.List.SelectedIndices[0];
        if (idx < 0 || idx >= v.View.Count) return;
        DocItem d = v.View[idx];
        if (d == null) return;
        try
        {
            if (File.Exists(d.Path)) Process.Start("explorer.exe", "/select,\"" + d.Path + "\"");
            else
            {
                string dir = Path.GetDirectoryName(d.Path);
                if (dir != null && Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
            }
        }
        catch { }
    }

    /// <summary>跳转到该文件所在文件夹并选中文件。</summary>
    void JumpToDoc(CatView v, int idx)
    {
        if (idx < 0 || idx >= v.View.Count) return;
        DocItem d = v.View[idx];
        if (d == null) return;
        try
        {
            if (File.Exists(d.Path)) Process.Start("explorer.exe", "/select,\"" + d.Path + "\"");
            else
            {
                string dir = Path.GetDirectoryName(d.Path);
                if (dir != null && Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
                else MessageBox.Show("文件已不存在。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    void OpenFolderItem(int idx)
    {
        if (idx < 0 || idx >= vFolders.View.Count) return;
        FolderItem f = vFolders.View[idx];
        if (f == null || string.IsNullOrEmpty(f.Path)) return;
        try { Process.Start("explorer.exe", "\"" + f.Path + "\""); } catch { }
    }

    void OpenSelectedFolders()
    {
        if (vFolders.List.SelectedIndices.Count == 0)
        {
            MessageBox.Show("请先选中要打开的文件夹（可多选）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        int n = 0;
        for (int k = 0; k < vFolders.List.SelectedIndices.Count; k++)
        {
            if (n >= 10) break;
            int idx = vFolders.List.SelectedIndices[k];
            if (idx < 0 || idx >= vFolders.View.Count) continue;
            FolderItem f = vFolders.View[idx];
            if (f != null && Directory.Exists(f.Path))
            {
                try { Process.Start("explorer.exe", "\"" + f.Path + "\""); } catch { }
            }
            n++;
        }
    }

    // ============ 软件 ============
    void SetAppButtons(bool on)
    {
        btnAppRefresh.Enabled = on;
        btnAppUninstall.Enabled = on;
        btnRogueRefresh.Enabled = on;
        btnRogueUninstall.Enabled = on;
    }

    void RefreshApps()
    {
        if (busy) return;
        busy = true;
        SetAppButtons(false);
        lblAppState.Text = "正在读取已安装软件…";
        lblRogueState.Text = "正在读取…";
        MarqueeOn(prgApps);
        Task.Run(delegate
        {
            List<AppInfo> res = null;
            try { res = AppLoader.Load(); }
            catch { res = new List<AppInfo>(); }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    apps = res;
                    FillApps();
                    FillRogue();
                    SetAppButtons(true);
                    busy = false;
                    ProgressOff(prgApps);
                    int sus = 0;
                    foreach (AppInfo a in apps) if (a.Risk >= 45) sus++;
                    lblAppState.Text = "已安装 " + apps.Count + " 个软件 · 疑似流氓 " + sus + " 个";
                }));
            }
            catch { }
        });
    }

    void FillApps()
    {
        string kw = tbSearch.Text.Trim().ToLower();
        int matched = 0;

        lvApps.BeginUpdate();
        lvApps.Items.Clear();
        foreach (AppInfo a in apps)
        {
            if (kw.Length > 0)
            {
                bool hit = a.Name.ToLower().Contains(kw) ||
                           a.Publisher.ToLower().Contains(kw) ||
                           a.InstallLocation.ToLower().Contains(kw) ||
                           a.Version.ToLower().Contains(kw);
                if (!hit) continue;
            }
            ListViewItem it = new ListViewItem(a.Name);
            it.SubItems.Add(a.Version);
            it.SubItems.Add(a.Publisher);
            it.SubItems.Add(a.SizeText());
            it.SubItems.Add(a.InstallDate);
            it.SubItems.Add(a.InstallLocation);
            it.SubItems.Add(a.Risk >= 45 ? ("可疑 · " + a.Risk) : "正常");
            it.Tag = a;
            if (a.Risk >= 45) it.ForeColor = Color.FromArgb(180, 60, 40);
            lvApps.Items.Add(it);
            matched++;
        }
        lvApps.EndUpdate();
        if (lvApps.ListViewItemSorter != null) { lvApps.Sort(); UpdateAppHeaders(); }

        if (kw.Length > 0) lblAppState.Text = "匹配 " + matched + " 个 / 共 " + apps.Count + " 个";
        else lblAppState.Text = "已安装 " + apps.Count + " 个软件";
    }

    void FillRogue()
    {
        lvRogue.BeginUpdate();
        lvRogue.Items.Clear();
        int min = chkHigh.Checked ? 70 : 45;
        List<AppInfo> hits = new List<AppInfo>();
        foreach (AppInfo a in apps) if (a.Risk >= min) hits.Add(a);
        hits.Sort(delegate (AppInfo x, AppInfo y) { return y.Risk.CompareTo(x.Risk); });
        foreach (AppInfo a in hits)
        {
            ListViewItem it = new ListViewItem(a.Name);
            it.SubItems.Add(a.Publisher);
            it.SubItems.Add(a.Risk.ToString());
            it.SubItems.Add(a.RiskReason);
            it.SubItems.Add(a.InstallLocation);
            it.Tag = a;
            if (a.Risk >= 70) it.ForeColor = Color.FromArgb(180, 40, 40);
            lvRogue.Items.Add(it);
        }
        lvRogue.EndUpdate();
        lblRogueState.Text = "疑似流氓 " + hits.Count + " 个（确认后才会卸载）";
    }

    void UninstallSelectedApps(ListView lv)
    {
        List<AppInfo> sel = GetSelected<AppInfo>(lv);
        if (sel.Count == 0)
        {
            MessageBox.Show("请先勾选要卸载的软件（可多选）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("即将强制全量卸载以下 " + sel.Count + " 个软件：");
        sb.AppendLine();
        int i = 0;
        foreach (AppInfo a in sel)
        {
            i++;
            if (i > 12) { sb.AppendLine("…… 及其他 " + (sel.Count - 12) + " 个"); break; }
            sb.AppendLine(i + ". " + a.Name + "  (" + (string.IsNullOrEmpty(a.Publisher) ? "未知发布者" : a.Publisher) + ")");
            if (!string.IsNullOrEmpty(a.InstallLocation)) sb.AppendLine("     目录：" + a.InstallLocation);
        }
        sb.AppendLine();
        sb.AppendLine("流程：先静默调用卸载程序，成功后继续清理安装目录、开始菜单/桌面快捷方式、注册表卸载项及残留数据。");
        sb.AppendLine("该操作不可撤销，请确认无误后继续。");

        if (MessageBox.Show(sb.ToString(), "确认卸载", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;

        RunUninstall(sel);
    }

    void RunUninstall(List<AppInfo> sel)
    {
        if (busy) return;
        busy = true;
        SetAppButtons(false);
        prgApps.Style = ProgressBarStyle.Continuous;
        prgRogue.Style = ProgressBarStyle.Continuous;
        SetProgress(prgApps, 0);
        SetProgress(prgRogue, 0);
        prgApps.Visible = true;
        prgRogue.Visible = true;
        lblAppState.Text = "正在卸载 0/" + sel.Count + " …";
        lblRogueState.Text = "正在卸载…";

        Task.Run(delegate
        {
            int done = 0;
            int total = sel.Count;
            List<string> lines = new List<string>();
            foreach (AppInfo a in sel)
            {
                try
                {
                    Uninstaller.Execute(a, true, delegate (string msg)
                    {
                        try
                        {
                            BeginInvoke(new Action(delegate
                            {
                                lblAppState.Text = "正在卸载 " + (done + 1) + "/" + total + " · " + msg;
                            }));
                        }
                        catch { }
                    });
                    lines.Add("√ " + a.Name);
                }
                catch (Exception ex)
                {
                    lines.Add("× " + a.Name + " —— " + ex.Message);
                }
                done++;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        SetProgress(prgApps, (done * 100) / total);
                        SetProgress(prgRogue, (done * 100) / total);
                        lblAppState.Text = "已处理 " + done + "/" + total;
                    }));
                }
                catch { }
            }

            List<AppInfo> res = null;
            try { res = AppLoader.Load(); } catch { res = new List<AppInfo>(); }

            try
            {
                BeginInvoke(new Action(delegate
                {
                    apps = res;
                    FillApps();
                    FillRogue();
                    SetAppButtons(true);
                    busy = false;
                    SetProgress(prgApps, 100);
                    SetProgress(prgRogue, 100);
                    prgApps.Visible = false;
                    prgRogue.Visible = false;
                    lblAppState.Text = "已安装 " + apps.Count + " 个软件";
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("卸载处理完成（" + sel.Count + " 个）：");
                    sb.AppendLine();
                    foreach (string l in lines) sb.AppendLine(l);
                    sb.AppendLine();
                    sb.AppendLine("如个别软件仍残留，可再次刷新列表勾选后重试。");
                    MessageBox.Show(sb.ToString(), "卸载结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }));
            }
            catch { }
        });
    }

    // ============ 文件列表（文档 / 图片 / 视频 / 压缩文件）============
    void FillCatView(CatView v)
    {
        string kw = v.Search.Text.Trim();
        string want = v.Cat;
        if (v.IsDocPage)
        {
            want = null;
            int ci = v.CatCombo.SelectedIndex;
            if (ci >= 1 && ci - 1 < DocScanner.DocCategories.Count) want = DocScanner.DocCategories[ci - 1];
        }

        v.View.Clear();
        v.Chk.Clear();
        int totalMatch = 0;
        long totalBytes = 0;

        for (int i = 0; i < docs.Count; i++)
        {
            DocItem d = docs[i];
            if (v.IsDocPage && !DocScanner.IsDocCategory(d.Cat)) continue;
            if (want != null && d.Cat != want) continue;
            if (kw.Length > 0 &&
                d.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0 &&
                d.Path.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
            totalMatch++;
            totalBytes += d.Size;
            v.View.Add(d);
            v.Chk.Add(false);
        }

        v.List.VirtualListSize = 0;
        v.List.VirtualListSize = v.View.Count;
        v.Rects.Clear();
        if (v.SortCol >= 0) ApplyCatSort(v); else { v.List.Invalidate(); UpdateDocHeaders(v); }

        string sizeText = (totalBytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
        v.State.Text = "共 " + totalMatch + " 个 · " + sizeText;
        v.Dirty = false;
    }

    void FillFolderView(FolderView f)
    {
        string kw = f.Search.Text.Trim();
        f.View.Clear();
        for (int i = 0; i < folders.Count; i++)
        {
            FolderItem d = folders[i];
            if (kw.Length > 0 &&
                d.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0 &&
                d.Path.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
            f.View.Add(d);
        }

        f.List.VirtualListSize = 0;
        f.List.VirtualListSize = f.View.Count;
        if (f.SortCol >= 0) ApplyFolderSort(f); else { f.List.Invalidate(); UpdateFolderHeaders(f); }

        f.State.Text = "共 " + f.View.Count + " 个文件夹";
        f.Dirty = false;
    }

    void ClearCatView(CatView v)
    {
        v.View.Clear();
        v.Chk.Clear();
        v.Rects.Clear();
        v.List.VirtualListSize = 0;
        v.List.Invalidate();
        v.Dirty = true;
    }

    void CheckPage(CatView v, bool on)
    {
        for (int i = 0; i < v.Chk.Count; i++) v.Chk[i] = on;
        v.List.Invalidate();
    }

    List<DocItem> GetCheckedDocs(CatView v)
    {
        List<DocItem> r = new List<DocItem>();
        for (int i = 0; i < v.View.Count && i < v.Chk.Count; i++)
            if (v.Chk[i]) r.Add(v.View[i]);
        if (r.Count == 0)
        {
            for (int k = 0; k < v.List.SelectedIndices.Count; k++)
            {
                int idx = v.List.SelectedIndices[k];
                if (idx >= 0 && idx < v.View.Count) r.Add(v.View[idx]);
            }
        }
        return r;
    }

    void DeleteDocsPage(CatView v, bool recycle)
    {
        if (deletingDocs) return;
        List<DocItem> sel = GetCheckedDocs(v);
        if (sel.Count == 0)
        {
            MessageBox.Show("请先勾选要删除的文件（可多选，只勾一个即单个删除）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string tip = recycle
            ? "将删除 " + sel.Count + " 个文件（放入回收站，可恢复）。\r\n若文件被占用或无权限，会自动结束占用进程、接管权限后强制删除（此时不可恢复）。"
            : "将彻底删除 " + sel.Count + " 个文件，不可恢复！\r\n被占用或无权限的文件会先结束占用进程、接管权限，再强制删除。";

        StringBuilder sb = new StringBuilder(tip);
        sb.AppendLine();
        sb.AppendLine();
        int i = 0;
        foreach (DocItem d in sel)
        {
            i++;
            if (i > 10) { sb.AppendLine("…… 及其他 " + (sel.Count - 10) + " 个"); break; }
            sb.AppendLine(d.Name + "    " + d.Path);
        }
        sb.AppendLine();
        sb.Append("确定继续？");
        if (MessageBox.Show(sb.ToString(), "确认删除", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;

        int ok = 0;
        int fail = 0;
        int forced = 0;
        int total = sel.Count;
        List<string> errors = new List<string>();
        List<DocItem> failedDocs = new List<DocItem>();

        deletingDocs = true;
        v.Del.Enabled = false;
        v.DelHard.Enabled = false;
        v.Scan.Enabled = false;
        v.All.Enabled = false;
        v.None.Enabled = false;
        v.Progress.Style = ProgressBarStyle.Continuous;
        SetProgress(v.Progress, 0);
        v.Progress.Visible = true;

        try
        {
            for (int n = 0; n < total; n++)
            {
                DocItem d = sel[n];
                try
                {
                    if (!File.Exists(d.Path) && !Directory.Exists(d.Path))
                    {
                        ok++;
                    }
                    else
                    {
                        bool doneNow = false;
                        if (recycle)
                        {
                            try
                            {
                                FileSystem.DeleteFile(d.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                                doneNow = !File.Exists(d.Path) && !Directory.Exists(d.Path);
                            }
                            catch { doneNow = false; }
                        }
                        if (!doneNow)
                        {
                            doneNow = FileForce.DeleteFile(d.Path, true, delegate (string note)
                            {
                                try { v.State.Text = note; } catch { }
                            });
                            if (doneNow) forced++;
                        }
                        if (doneNow) ok++;
                        else
                        {
                            fail++;
                            failedDocs.Add(d);
                            if (errors.Count < 5) errors.Add(d.Name + "：" + d.Path);
                        }
                    }
                }
                catch (Exception ex)
                {
                    fail++;
                    failedDocs.Add(d);
                    if (errors.Count < 5) errors.Add(d.Name + "：" + ex.Message);
                }

                SetProgress(v.Progress, ((n + 1) * 100) / total);
                v.State.Text = "正在" + (recycle ? "删除" : "彻底删除") + " " + (n + 1) + "/" + total + " …";
                Application.DoEvents();
            }
        }
        finally
        {
            deletingDocs = false;
            ProgressOff(v.Progress);
            v.Del.Enabled = true;
            v.DelHard.Enabled = true;
            v.Scan.Enabled = true;
            v.All.Enabled = true;
            v.None.Enabled = true;
        }

        HashSet<DocItem> gone = new HashSet<DocItem>();
        for (int n = 0; n < sel.Count; n++) if (!failedDocs.Contains(sel[n])) gone.Add(sel[n]);
        if (gone.Count > 0) docs.RemoveAll(delegate (DocItem d) { return gone.Contains(d); });

        if (v != vDocs) ClearCatView(vDocs);
        if (v != vImages) ClearCatView(vImages);
        if (v != vVideos) ClearCatView(vVideos);
        if (v != vArchives) ClearCatView(vArchives);

        if (gone.Count > 0) FillCatView(v);
        else for (int k = 0; k < v.Chk.Count; k++) v.Chk[k] = false;

        string msg = "删除完成：成功 " + ok + " 个，失败 " + fail + " 个。";
        if (forced > 0) msg += "\r\n其中 " + forced + " 个是强制删除（被占用或权限不足，已终止占用进程并接管权限，无法进回收站）。";
        if (errors.Count > 0) msg += "\r\n\r\n仍失败的文件（可能正在被程序打开或系统保护）：\r\n" + string.Join("\r\n", errors.ToArray());
        MessageBox.Show(msg, "删除结果", MessageBoxButtons.OK, fail > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    // ============ 全盘扫描 ============
    void SetScanButtons(string text)
    {
        vDocs.Scan.Text = text;
        vImages.Scan.Text = text;
        vVideos.Scan.Text = text;
        vArchives.Scan.Text = text;
        vFolders.Scan.Text = text;
    }

    void ScanDocs()
    {
        // 扫描中再次点击 = 停止
        if (scanningDocs)
        {
            cancelDocScan = true;
            SetScanButtons("正在停止…");
            return;
        }
        scanningDocs = true;
        cancelDocScan = false;
        SetScanButtons("停止");

        ClearCatView(vDocs);
        ClearCatView(vImages);
        ClearCatView(vVideos);
        ClearCatView(vArchives);
        vFolders.View.Clear();
        vFolders.List.VirtualListSize = 0;
        vFolders.List.Invalidate();
        vFolders.Dirty = true;

        vDocs.State.Text = "正在全盘扫描…（首次可能需要几分钟）";
        MarqueeOn(vDocs.Progress);
        MarqueeOn(vImages.Progress);
        MarqueeOn(vVideos.Progress);
        MarqueeOn(vArchives.Progress);
        MarqueeOn(vFolders.Progress);

        DateTime lastTick = DateTime.MinValue;

        Task.Run(delegate
        {
            ScanResult res = null;
            try
            {
                res = DocScanner.Scan(delegate (int files, int dirs)
                {
                    DateTime now = DateTime.Now;
                    if ((now - lastTick).TotalMilliseconds < 300) return;
                    lastTick = now;
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            string s = "正在扫描… 已发现 " + files + " 个文件";
                            vDocs.State.Text = s;
                            vImages.State.Text = s;
                            vVideos.State.Text = s;
                            vArchives.State.Text = s;
                            vFolders.State.Text = s;
                        }));
                    }
                    catch { }
                }, delegate { return cancelDocScan; });
            }
            catch { res = new ScanResult(); }
            if (res == null) res = new ScanResult();

            try
            {
                BeginInvoke(new Action(delegate
                {
                    docs = res.Files;
                    folders = res.Folders;
                    vDocs.Dirty = true;
                    vImages.Dirty = true;
                    vVideos.Dirty = true;
                    vArchives.Dirty = true;
                    vFolders.Dirty = true;
                    EnsureFillSelected();
                    finishDocScan();
                }));
            }
            catch { }
        });
    }

    void finishDocScan()
    {
        scanningDocs = false;
        cancelDocScan = false;
        SetScanButtons("全盘扫描");
        ProgressOff(vDocs.Progress);
        ProgressOff(vImages.Progress);
        ProgressOff(vVideos.Progress);
        ProgressOff(vArchives.Progress);
        ProgressOff(vFolders.Progress);
        EnsureFillSelected();
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
