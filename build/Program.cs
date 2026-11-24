using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using Microsoft.Web.WebView2.Core;
using WV2 = Microsoft.Web.WebView2.Wpf.WebView2;

public class Session {
    public string Id, FilePath, Cwd, Title, First, Project, Worktree;
    // Rel: 최상위 폴더에서 본 작업 폴더(최상위는 ""), Wt: 작업본(.claude\worktrees\이름) 이름. 읽을 때마다 Cwd에서 다시 계산한다.
    public string Rel, Wt;
    public DateTime Time; public long Len; public long Stamp;
}
public class Proj {
    public string Name, Dir, Display; public int Count; public DateTime Last;
    // 폴더 트리: Count는 이 폴더에서 연 세션, Total은 하위 폴더까지 합친 수. Depth는 들여쓰기 단계.
    public int Total, Depth; public bool HasKids, Open;
    public string Rel { get { return Name == "." ? "" : Name; } }
}

static class Scanner {
    // 프로젝트를 모아 둔 최상위 폴더. 정하는 순서: launcher-config.json 의 "root" → 환경 변수 CLAUDE_LAUNCHER_ROOT → 바탕화면\Claude
    public static readonly string Root = FindRoot();
    static readonly string ProjDir = Path.Combine(ClaudeHome, "projects");
    public static string ClaudeHome { get {
        string c = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return !string.IsNullOrEmpty(c) ? c : Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE"), ".claude");
    } }
    static string FindRoot() {
        try {
            string cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher-config.json");
            if (File.Exists(cfg)) {
                var o = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(cfg, Encoding.UTF8)) as Dictionary<string, object>;
                object r;
                if (o != null && o.TryGetValue("root", out r) && r is string && ((string)r).Trim().Length > 0)
                    return Path.GetFullPath(Environment.ExpandEnvironmentVariables(((string)r).Trim())).TrimEnd('\\');
            }
        } catch { }
        string env = Environment.GetEnvironmentVariable("CLAUDE_LAUNCHER_ROOT");
        if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env).TrimEnd('\\');
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Claude");
    }
    static readonly string Prefix = Regex.Replace(Root, "[^A-Za-z0-9]", "-");
    static readonly Regex RxCwd = new Regex(@"""cwd"":""((?:[^""\\]|\\.)*)""");
    static readonly Regex RxCustom = new Regex(@"""customTitle"":""((?:[^""\\]|\\.)*)""");
    static readonly Regex RxAi = new Regex(@"""aiTitle"":""((?:[^""\\]|\\.)*)""");
    static readonly Regex RxTags = new Regex(@"<([A-Za-z][\w-]*)[^>]*>[\s\S]*?</\1>");
    static readonly Dictionary<string, Session> cache = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
    static readonly object gate = new object();

    static string Unesc(string s) { try { return Regex.Unescape(s); } catch { return s; } }

    static string FirstPrompt(string line) {
        if (line.Length > 2000000) return null;
        try {
            var js = new JavaScriptSerializer(); js.MaxJsonLength = int.MaxValue;
            var d = js.DeserializeObject(line) as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            if (d.TryGetValue("isMeta", out v) && v is bool && (bool)v) return null;
            if (d.TryGetValue("isSidechain", out v) && v is bool && (bool)v) return null;
            if (!d.TryGetValue("message", out v)) return null;
            var msg = v as Dictionary<string, object>; if (msg == null) return null;
            object c; if (!msg.TryGetValue("content", out c)) return null;
            var texts = new List<string>();
            if (c is string) texts.Add((string)c);
            else if (c is IEnumerable) {
                foreach (var p in (IEnumerable)c) {
                    var pd = p as Dictionary<string, object>;
                    if (pd != null && pd.ContainsKey("type") && "text".Equals(pd["type"]) && pd.ContainsKey("text")) texts.Add(pd["text"] as string ?? "");
                }
            }
            foreach (var t0 in texts) {
                string t = RxTags.Replace(t0, "").Trim();
                if (t.Length > 0 && !t.StartsWith("<")) return Regex.Replace(t, @"\s+", " ");
            }
        } catch { }
        return null;
    }

    static Session Read(FileInfo f) {
        var s = new Session();
        s.Id = Path.GetFileNameWithoutExtension(f.Name); s.FilePath = f.FullName; s.Time = f.LastWriteTime; s.Len = f.Length; s.Stamp = f.LastWriteTimeUtc.Ticks;
        string custom = null, ai = null;
        using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            var sr = new StreamReader(fs, new UTF8Encoding(false), false, 65536);
            for (int n = 0; n < 400; n++) {
                string line = sr.ReadLine(); if (line == null) break;
                Match m;
                if (s.Cwd == null) { m = RxCwd.Match(line); if (m.Success) s.Cwd = Unesc(m.Groups[1].Value); }
                if (s.First == null && line.Contains("\"type\":\"user\"")) s.First = FirstPrompt(line);
                if (line.Contains("\"customTitle\"")) { m = RxCustom.Match(line); if (m.Success) custom = Unesc(m.Groups[1].Value); }
                if (line.Contains("\"aiTitle\"")) { m = RxAi.Match(line); if (m.Success) ai = Unesc(m.Groups[1].Value); }
                if (s.Cwd != null && s.First != null) break;
            }
            long len = fs.Length; int take = (int)Math.Min(len, 3L * 1024 * 1024);
            fs.Seek(len - take, SeekOrigin.Begin);
            byte[] buf = new byte[take]; int read = 0;
            while (read < take) { int r = fs.Read(buf, read, take - read); if (r <= 0) break; read += r; }
            string tail = Encoding.UTF8.GetString(buf, 0, read);
            MatchCollection mc = RxCustom.Matches(tail); if (mc.Count > 0) custom = Unesc(mc[mc.Count - 1].Groups[1].Value);
            mc = RxAi.Matches(tail); if (mc.Count > 0) ai = Unesc(mc[mc.Count - 1].Groups[1].Value);
        }
        s.Title = custom ?? ai ?? s.First;
        if (s.Title != null) s.Title = Regex.Replace(s.Title, @"\s+", " ").Trim();
        if (s.Cwd != null) {
            string cwd = s.Cwd.TrimEnd('\\');
            if (cwd.Equals(Root, StringComparison.OrdinalIgnoreCase)) s.Project = ".";
            else if (cwd.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase)) {
                string rel = cwd.Substring(Root.Length + 1);
                s.Project = rel.Split('\\')[0];
                string rest = rel.Substring(s.Project.Length).TrimStart('\\');
                var wm = Regex.Match(rest, @"^\.claude\\worktrees\\([^\\]+)");
                if (wm.Success) s.Worktree = wm.Groups[1].Value; else if (rest.Length > 0) s.Worktree = rest;
            }
        }
        return s;
    }

    // 읽은 결과를 파일로 남긴다. 다음에 켤 때 바뀐 세션 파일만 다시 읽는다.
    static readonly string CacheFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"cache\sessions.json");
    static bool loaded, dirty;
    static void LoadCache() {
        loaded = true;
        try {
            if (!File.Exists(CacheFile)) return;
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            foreach (var s in js.Deserialize<List<Session>>(File.ReadAllText(CacheFile, Encoding.UTF8)))
                if (s != null && s.FilePath != null) cache[s.FilePath] = s;
        } catch { }
    }
    static void SaveCache() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile));
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            File.WriteAllText(CacheFile + ".tmp", js.Serialize(cache.Values.ToList()), Encoding.UTF8);
            if (File.Exists(CacheFile)) File.Delete(CacheFile);
            File.Move(CacheFile + ".tmp", CacheFile);
        } catch { }
    }
    public static List<Session> ScanAll() {
        lock (gate) {
            if (!loaded) LoadCache();
            dirty = false;
            var list = new List<Session>();
            var pd = new DirectoryInfo(ProjDir); if (!pd.Exists) return list;
            foreach (var d in pd.GetDirectories()) {
                if (!(d.Name.Equals(Prefix, StringComparison.OrdinalIgnoreCase) || d.Name.StartsWith(Prefix + "-", StringComparison.OrdinalIgnoreCase))) continue;
                foreach (var f in d.GetFiles("*.jsonl")) {
                    Session s, c;
                    if (cache.TryGetValue(f.FullName, out c) && c.Len == f.Length && c.Stamp == f.LastWriteTimeUtc.Ticks) { s = c; s.Time = f.LastWriteTime; }
                    else { try { s = Read(f); } catch { continue; } cache[f.FullName] = s; dirty = true; }
                    Annotate(s);
                    if (s.Rel != null && !string.IsNullOrEmpty(s.Title)) list.Add(s);
                }
            }
            if (dirty) SaveCache();
            return list;
        }
    }

    static void Annotate(Session s) {
        s.Rel = null; s.Wt = null;
        if (s.Cwd == null) return;
        string cwd = s.Cwd.TrimEnd('\\'), rel;
        if (cwd.Equals(Root, StringComparison.OrdinalIgnoreCase)) rel = "";
        else if (cwd.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase)) rel = cwd.Substring(Root.Length + 1);
        else return;
        // 작업본(.claude\worktrees\이름) 안에서 연 세션은 그 작업본을 가진 폴더의 세션으로 본다.
        string r2 = "\\" + rel;
        var m = Regex.Match(r2, @"\\\.claude\\worktrees\\([^\\]+)", RegexOptions.IgnoreCase);
        if (m.Success) { s.Wt = m.Groups[1].Value; rel = r2.Substring(0, m.Index).TrimStart('\\'); }
        s.Rel = rel;
    }

    // 폴더 트리에 넣지 않는 폴더: 숨김, 점으로 시작, 내려받은 꾸러미 폴더.
    static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", "__pycache__", "venv", "$RECYCLE.BIN", "System Volume Information" };
    static List<string> DiskKids(string rel) {
        var res = new List<string>();
        try {
            foreach (var d in new DirectoryInfo(rel.Length == 0 ? Root : Path.Combine(Root, rel)).GetDirectories()) {
                if (d.Name.StartsWith(".") || SkipDirs.Contains(d.Name) || (d.Attributes & FileAttributes.Hidden) != 0) continue;
                res.Add(rel.Length == 0 ? d.Name : rel + "\\" + d.Name);
            }
        } catch { }
        return res;
    }
    static string ParentOf(string rel) { int i = rel.LastIndexOf('\\'); return i < 0 ? "" : rel.Substring(0, i); }
    public static bool Under(string rel, string folder) {
        if (rel == null) return false;
        if (folder.Length == 0) return true;
        return rel.Equals(folder, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // 폴더 트리를 펼친 순서대로 한 줄씩 늘어놓는다. 맨 위는 최상위 폴더, 그 아래 최상위의 하위 폴더들(들여쓰기 0단계).
    // 들어가는 폴더: 최상위 바로 아래 폴더, 세션을 연 폴더와 그 위 폴더들, 펼친 폴더의 하위 폴더.
    public static List<Proj> Tree(List<Session> all, ICollection<string> open) {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        foreach (var k in DiskKids("")) set.Add(k);
        foreach (var s in all) { string r = s.Rel; while (r != null && r.Length > 0 && set.Add(r)) r = ParentOf(r); }
        foreach (var o in open.Where(x => x.Length > 0).OrderBy(x => x.Count(c => c == '\\')))
            if (set.Contains(o)) foreach (var k in DiskKids(o)) set.Add(k);
        var kids = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in set) { if (n.Length == 0) continue; string p = ParentOf(n); List<string> l; if (!kids.TryGetValue(p, out l)) kids[p] = l = new List<string>(); l.Add(n); }
        var res = new List<Proj>();
        Func<string, int, Proj> make = (n, depth) => {
            var own = all.Where(x => x.Rel.Equals(n, StringComparison.OrdinalIgnoreCase)).ToList();
            var sub = all.Where(x => Under(x.Rel, n)).ToList();
            var p = new Proj { Name = n.Length == 0 ? "." : n, Dir = n.Length == 0 ? Root : Path.Combine(Root, n), Depth = depth };
            p.Display = n.Length == 0 ? "Claude 폴더 (최상위)" : Path.GetFileName(n);
            p.Count = own.Count; p.Total = sub.Count;
            p.Last = sub.Count > 0 ? sub.Max(x => x.Time) : DateTime.MinValue;
            p.Open = n.Length > 0 && open.Contains(n);
            return p;
        };
        res.Add(make("", 0));
        Action<string, int> walk = null;
        walk = (parent, depth) => {
            List<string> l; if (!kids.TryGetValue(parent, out l)) return;
            foreach (var p in l.Select(n => make(n, depth)).OrderByDescending(p => p.Last).ThenBy(p => p.Display, StringComparer.CurrentCultureIgnoreCase)) {
                List<string> kl;
                p.HasKids = (kids.TryGetValue(p.Rel, out kl) && kl.Count > 0) || (!p.Open && DiskKids(p.Rel).Count > 0);
                if (!p.HasKids) p.Open = false;
                res.Add(p);
                if (p.Open) walk(p.Rel, depth + 1);
            }
        };
        walk("", 0);
        return res;
    }
}



// ---------- 의사 콘솔(ConPTY) ----------
[StructLayout(LayoutKind.Sequential)] public struct COORD { public short X, Y; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct STARTUPINFO {
    public int cb; public string lpReserved, lpDesktop, lpTitle;
    public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
}
[StructLayout(LayoutKind.Sequential)] public struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
[StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

static class K32 {
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out IntPtr phPC);
    [DllImport("kernel32.dll")] public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
    [DllImport("kernel32.dll")] public static extern void ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CreatePipe(out SafeFileHandle r, out SafeFileHandle w, IntPtr sa, int size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll")] public static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr h, uint code);
}

class Pty {
    IntPtr hPC, hProc, attr; FileStream inS, outS; bool closed;
    public Action<string> Output; public Action Exited;

    public void Start(string cmd, string cwd, int cols, int rows) {
        SafeFileHandle inR, inW, outR, outW;
        if (!K32.CreatePipe(out inR, out inW, IntPtr.Zero, 0) || !K32.CreatePipe(out outR, out outW, IntPtr.Zero, 0)) throw new Exception("파이프를 만들지 못했습니다.");
        int hr = K32.CreatePseudoConsole(new COORD { X = (short)cols, Y = (short)rows }, inR, outW, 0, out hPC);
        if (hr != 0) throw new Exception("의사 콘솔을 만들지 못했습니다. (" + hr + ")");
        inR.Dispose(); outW.Dispose();
        inS = new FileStream(inW, FileAccess.Write, 1, false);
        outS = new FileStream(outR, FileAccess.Read, 1, false);

        IntPtr size = IntPtr.Zero;
        K32.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        attr = Marshal.AllocHGlobal(size);
        if (!K32.InitializeProcThreadAttributeList(attr, 1, 0, ref size)) throw new Exception("속성 목록 실패");
        if (!K32.UpdateProcThreadAttribute(attr, 0, (IntPtr)0x00020016, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Exception("속성 설정 실패");
        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
        si.StartupInfo.dwFlags = 0x100; // STARTF_USESTDHANDLES: 부모의 표준 입출력을 물려받지 않게
        si.lpAttributeList = attr;
        PROCESS_INFORMATION pi;
        if (!K32.CreateProcessW(null, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, cwd, ref si, out pi))
            throw new Exception("프로그램을 시작하지 못했습니다. (오류 " + Marshal.GetLastWin32Error() + ")");
        K32.CloseHandle(pi.hThread);
        hProc = pi.hProcess;

        var rt = new Thread(ReadLoop) { IsBackground = true }; rt.Start();
        var wt = new Thread(delegate () {
            K32.WaitForSingleObject(hProc, 0xFFFFFFFF);
            Thread.Sleep(250);
            if (Exited != null) Exited();
            Close();
        }) { IsBackground = true };
        wt.Start();
    }

    void ReadLoop() {
        var dec = new UTF8Encoding(false).GetDecoder();
        byte[] buf = new byte[65536]; char[] cb = new char[65536 + 8];
        while (true) {
            int n;
            try { n = outS.Read(buf, 0, buf.Length); } catch { break; }
            if (n <= 0) break;
            int c = dec.GetChars(buf, 0, n, cb, 0);
            if (c > 0 && Output != null) Output(new string(cb, 0, c));
        }
    }

    public void Write(string s) {
        if (closed || inS == null) return;
        try { var b = Encoding.UTF8.GetBytes(s); inS.Write(b, 0, b.Length); inS.Flush(); } catch { }
    }
    public void Resize(int cols, int rows) {
        if (closed || hPC == IntPtr.Zero) return;
        K32.ResizePseudoConsole(hPC, new COORD { X = (short)Math.Max(2, cols), Y = (short)Math.Max(2, rows) });
    }
    public void Kill() {
        if (hProc != IntPtr.Zero && !closed) { try { K32.TerminateProcess(hProc, 1); } catch { } }
        ThreadPool.QueueUserWorkItem(delegate { Close(); });
    }
    readonly object gate = new object();
    void Close() {
        lock (gate) {
            if (closed) return; closed = true;
            try { if (hPC != IntPtr.Zero) K32.ClosePseudoConsole(hPC); } catch { }
            try { if (inS != null) inS.Dispose(); } catch { }
            if (attr != IntPtr.Zero) { K32.DeleteProcThreadAttributeList(attr); Marshal.FreeHGlobal(attr); attr = IntPtr.Zero; }
            if (hProc != IntPtr.Zero) { K32.CloseHandle(hProc); }
        }
    }
}

class TermTab {
    public int Id; public string Title, Dir, Cmd, SessionId;
    public bool Started, Exited; public Pty Pty; public string Key;
    public Dictionary<string, object> St, Pane; public DateTime StM, PaneM;
    public string PendModel, PendEffort; public DateTime PendAt;
    public bool HandoffWaiting; public string HandoffTranscript; public long HandoffOffset; public DateTime HandoffAt;
    public bool Ultra, UltraPending; public DateTime UltraSetAt; public long UltraScan = -1; public bool UltraFlag;
    public readonly StringBuilder Buf = new StringBuilder(); public bool FlushPending;
}

public class ProjVM { public Proj P { get; set; } public string Display { get; set; } public string Sub { get; set; } public string Glyph { get; set; }
    public Thickness Indent { get; set; } public string Chev { get; set; } public Visibility ChevVis { get; set; } }
public class SessVM { public Session S { get; set; } public string Title { get; set; } public string When { get; set; } public string Sub { get; set; } public string Worktree { get; set; } public Visibility WtVis { get; set; }
    public string Group { get; set; } public string Wt { get; set; } public Visibility WtNameVis { get; set; } public Visibility RowVis { get; set; } }

class Launcher {
    Window w; ListBox lbProj, lbSess; TextBox search, newName; TextBlock hint, txtProj, txtProjSub, status, emptyText, newErr; FrameworkElement empty, newBox; Grid content, termHost;
    Button btnOpen, btnNew, btnFolder, btnRefresh, btnMax, btnNewProj;
    FrameworkElement listPane, termCard, tabScroll; StackPanel tabStrip;
    List<Session> all = new List<Session>(); List<Proj> projects = new List<Proj>(); List<SessVM> shown = new List<SessVM>();
    DateTime lastScan = DateTime.MinValue; bool scanning; string shot; bool demo;
    string pendingProj; bool pendingStart;
    // 폴더 트리에서 펼친 폴더, 하위 폴더 세션까지 보일지. cache\view.txt 에 남긴다.
    HashSet<string> openDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool withSub; Button btnSub; TextBlock txtSub;
    static string ViewFile { get { return System.IO.Path.Combine(BaseDir, @"cache\view.txt"); } }
    void LoadView() {
        try { foreach (var l in File.ReadAllLines(ViewFile, Encoding.UTF8)) { if (l == "#sub=1") withSub = true; else if (l.Length > 0 && !l.StartsWith("#")) openDirs.Add(l); } } catch { }
    }
    void SaveView() {
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ViewFile)); File.WriteAllLines(ViewFile, new[] { withSub ? "#sub=1" : "#sub=0" }.Concat(openDirs), new UTF8Encoding(false)); } catch { }
    }
    WV2 web; bool webReady; readonly List<string> queue = new List<string>();
    readonly List<TermTab> tabs = new List<TermTab>(); TermTab activeTab; int nextId = 1;
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    ColumnDefinition colProj, colGap1, colGap2, colPanel; FrameworkElement panel, actions, projCard; StackPanel panelBody;
    TextBlock txtModel, txtEffort, chipNote; Button btnModel, btnEffort, btnPanel; bool panelOn = true;
    Button btnCompact; TextBlock txtCompact; System.Windows.Controls.Primitives.Popup compactPop; DateTime compactClosedAt;
    int compactWindow = 450000; List<string> compactItems = new List<string> { "plan", "done", "now" }; string compactCustom = "";
    int footDense; StackPanel footUsage, panelTabs; string panelCat = "plan"; Border ultraGlow, ultraEdge, termCardB; TextBlock ultraLabel; bool ultraShown; DateTime planM; string planText; string planFor; TextBlock handoffNote, txtHandoff; Button btnHandoff;
    ContextMenu picker; bool pickerIsModel; DateTime pickerClosedAt; bool launchUltra;
    string launchModel, launchEffort; DispatcherTimer tick; FileSystemWatcher watcher; int watchPending; string lastSig;
    public static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    static readonly string[][] MODELS = { new[] { "opus", "Opus 5.5" }, new[] { "sonnet", "Sonnet 5.5" }, new[] { "haiku", "Haiku 5.5" }, new[] { "fable", "Fable 5.2" } };
    static readonly string[][] EFFORTS = { new[] { "low", "낮음" }, new[] { "medium", "보통" }, new[] { "high", "높음" }, new[] { "xhigh", "매우 높음" }, new[] { "max", "최대" } };

    T F<T>(string n) where T : class { return w.FindName(n) as T; }
    static Brush B(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
    static object R(string key) { return Application.Current.Resources[key]; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    public static bool IsAdmin() { try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { return false; } }

    public Launcher(Window win, string shotPath, bool demoMode) {
        w = win; shot = shotPath; demo = demoMode;
        lbProj = F<ListBox>("LbProj"); lbSess = F<ListBox>("LbSess"); search = F<TextBox>("Search"); hint = F<TextBlock>("SearchHint");
        txtProj = F<TextBlock>("TxtProj"); txtProjSub = F<TextBlock>("TxtProjSub"); status = F<TextBlock>("Status");
        empty = F<FrameworkElement>("Empty"); emptyText = F<TextBlock>("EmptyText"); content = F<Grid>("Content");
        btnOpen = F<Button>("BtnOpen"); btnNew = F<Button>("BtnNew"); btnFolder = F<Button>("BtnFolder"); btnRefresh = F<Button>("BtnRefresh"); btnMax = F<Button>("BtnMax");
        listPane = F<FrameworkElement>("ListPane"); termCard = F<FrameworkElement>("TermCard"); termHost = F<Grid>("TermHost");
        tabScroll = F<FrameworkElement>("TabScroll"); tabStrip = F<StackPanel>("TabStrip");
        colProj = F<ColumnDefinition>("ColProj"); colGap1 = F<ColumnDefinition>("ColGap1"); colGap2 = F<ColumnDefinition>("ColGap2"); colPanel = F<ColumnDefinition>("ColPanel");
        panel = F<FrameworkElement>("Panel"); actions = F<FrameworkElement>("Actions"); projCard = F<FrameworkElement>("ProjCard"); panelBody = F<StackPanel>("PanelBody");
        txtModel = F<TextBlock>("TxtModel"); txtEffort = F<TextBlock>("TxtEffort"); chipNote = F<TextBlock>("ChipNote");
        btnModel = F<Button>("BtnModel"); btnEffort = F<Button>("BtnEffort"); btnPanel = F<Button>("BtnPanel");
        footUsage = F<StackPanel>("FootUsage"); handoffNote = F<TextBlock>("HandoffNote"); txtHandoff = F<TextBlock>("TxtHandoff"); btnHandoff = F<Button>("BtnHandoff");
        btnHandoff.Click += delegate { Handoff(); };
        btnCompact = F<Button>("BtnCompact"); txtCompact = F<TextBlock>("TxtCompact");
        btnCompact.Click += delegate { ShowCompact(); };
        LoadCompact(); UpdateCompactChip();
        panelTabs = F<StackPanel>("PanelTabs"); ultraGlow = F<Border>("UltraGlow"); ultraEdge = F<Border>("UltraEdge"); ultraLabel = F<TextBlock>("UltraLabel"); termCardB = F<Border>("TermCard");
        ultraLabel.Foreground = Rainbow(false);
        w.SizeChanged += delegate { int d = w.ActualWidth < 1160 ? 2 : w.ActualWidth < 1330 ? 1 : 0; if (d != footDense) { footDense = d; RenderPanel(true); } };
        btnModel.Click += delegate { ShowPicker(btnModel, true); };
        btnEffort.Click += delegate { ShowPicker(btnEffort, false); };
        btnPanel.Click += delegate { panelOn = !panelOn; Layout(); };
        tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += delegate { Tick(); };
        tick.Start();
        // 상태 파일이 바뀌면 1초를 기다리지 않고 바로 반영한다.
        try {
            Directory.CreateDirectory(StatusPath(""));
            watcher = new FileSystemWatcher(StatusPath(""), "*.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            FileSystemEventHandler h = (s, e) => {
                if (Interlocked.Exchange(ref watchPending, 1) == 1) return;
                w.Dispatcher.BeginInvoke((Action)delegate { watchPending = 0; Tick(); }, DispatcherPriority.Background);
            };
            watcher.Changed += h; watcher.Created += h;
            watcher.Renamed += (s, e) => h(s, null);
            watcher.EnableRaisingEvents = true;
        } catch { }
        btnNewProj = F<Button>("BtnNewProj"); newBox = F<FrameworkElement>("NewProjBox"); newName = F<TextBox>("NewProjName"); newErr = F<TextBlock>("NewProjErr");

        try { using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("claude.ico")) w.Icon = BitmapFrame.Create(st, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); } catch { }

        w.SourceInitialized += delegate {
            var h = new WindowInteropHelper(w).Handle;
            var src = HwndSource.FromHwnd(h); if (src != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            int v = 1; DwmSetWindowAttribute(h, 20, ref v, 4);
            v = 3; DwmSetWindowAttribute(h, 38, ref v, 4);
            v = 2; DwmSetWindowAttribute(h, 33, ref v, 4);
            SetWindowLong(h, -16, GetWindowLong(h, -16) & ~0x00080000);
        };
        w.StateChanged += delegate {
            bool max = w.WindowState == WindowState.Maximized;
            content.Margin = max ? new Thickness(7) : new Thickness(0);
            btnMax.Content = max ? "\xE923" : "\xE922";
        };
        F<Button>("BtnMin").Click += delegate { w.WindowState = WindowState.Minimized; };
        btnMax.Click += delegate { w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; };
        F<Button>("BtnClose").Click += delegate { w.Close(); };
        btnRefresh.Click += delegate { Rescan(); };
        btnOpen.Click += delegate { OpenSelected(); };
        btnNew.Click += delegate { NewSession(); };
        btnFolder.Click += delegate { var p = CurProj(); if (p != null && Directory.Exists(p.Dir)) Process.Start("explorer.exe", "\"" + p.Dir + "\""); };

        // 새 프로젝트
        btnNewProj.Click += delegate { ShowNewProj(true); };
        F<Button>("BtnNewProjCancel").Click += delegate { ShowNewProj(false); };
        F<Button>("BtnNewProjOk").Click += delegate { CreateProject(); };
        newName.PreviewKeyDown += (s, e) => {
            if (e.Key == Key.Enter) { CreateProject(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ShowNewProj(false); e.Handled = true; }
        };
        newName.TextChanged += delegate { newErr.Visibility = Visibility.Collapsed; };

        search.TextChanged += delegate { hint.Visibility = search.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible; ShowSessions(null); };
        search.PreviewKeyDown += (s, e) => {
            if (e.Key == Key.Down || e.Key == Key.Enter) { FocusSess(); e.Handled = true; }
            else if (e.Key == Key.Escape) { search.Text = ""; e.Handled = true; }
        };
        lbProj.SelectionChanged += delegate { if (search.Text.Length > 0) search.Text = ""; else ShowSessions(null); };
        lbProj.PreviewMouseLeftButtonUp += delegate { if (activeTab != null) Activate(null); };
        lbProj.PreviewKeyDown += (s, e) => {
            var vm = lbProj.SelectedItem as ProjVM; var p = vm == null ? null : vm.P;
            if (e.Key == Key.Right && p != null && p.HasKids && !p.Open) { ToggleDir(p, true); e.Handled = true; }
            else if (e.Key == Key.Left && p != null && p.Open) { ToggleDir(p, false); e.Handled = true; }
            else if (e.Key == Key.Left && p != null && p.Rel.Length > 0) { SelectDir(p.Rel.Contains("\\") ? p.Rel.Substring(0, p.Rel.LastIndexOf('\\')) : "."); e.Handled = true; }
            else if (e.Key == Key.Enter || e.Key == Key.Right) { Activate(null); FocusSess(); e.Handled = true; }
        };
        // 폴더 앞 꺾쇠를 누르면 펼치고 접는다. 폴더 줄을 두 번 눌러도 된다.
        lbProj.PreviewMouseLeftButtonDown += (s, e) => {
            var fe = e.OriginalSource as FrameworkElement;
            var c = ItemsControl.ContainerFromElement(lbProj, e.OriginalSource as DependencyObject) as ListBoxItem;
            var vm = c == null ? null : c.DataContext as ProjVM;
            if (vm == null || !vm.P.HasKids) return;
            if ((fe != null && "chev".Equals(fe.Tag)) || e.ClickCount == 2) { ToggleDir(vm.P, !vm.P.Open); e.Handled = true; }
        };
        btnSub = F<Button>("BtnSub"); txtSub = F<TextBlock>("TxtSub");
        btnSub.Click += delegate { withSub = !withSub; SaveView(); ShowSessions(null); };
        LoadView();
        lbSess.SelectionChanged += delegate { UpdateButtons(); };
        lbSess.PreviewKeyDown += (s, e) => {
            if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
            else if (e.Key == Key.Left || e.Key == Key.Escape) { FocusList(lbProj); e.Handled = true; }
        };
        lbSess.MouseDoubleClick += (s, e) => {
            var c = ItemsControl.ContainerFromElement(lbSess, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (c != null) OpenSelected();
        };
        var cm = lbSess.ContextMenu;
        ((MenuItem)cm.Items[0]).Click += delegate { OpenSelected(); };
        ((MenuItem)cm.Items[1]).Click += delegate { var x = CurSess(); if (x != null && Directory.Exists(x.Cwd)) Process.Start("explorer.exe", "\"" + x.Cwd + "\""); };
        ((MenuItem)cm.Items[2]).Click += delegate { var x = CurSess(); if (x != null) { Clipboard.SetText(x.Id); status.Text = "세션 ID를 복사했습니다."; } };
        ((MenuItem)cm.Items[3]).Click += delegate { var x = CurSess(); if (x != null) LaunchExternal(x.Cwd, "--resume " + x.Id, x.Title); };

        w.PreviewKeyDown += (s, e) => {
            if (web != null && web.IsKeyboardFocusWithin) return;   // 터미널 입력은 그대로 터미널로
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (e.Key == Key.F5) { Rescan(); e.Handled = true; }
            else if (ctrl && e.Key == Key.F) { Activate(null); search.Focus(); search.SelectAll(); e.Handled = true; }
            else if (ctrl && e.Key == Key.N) { NewSession(); e.Handled = true; }
        };
        w.Activated += delegate { if (activeTab == null && (DateTime.Now - lastScan).TotalSeconds > 8) Rescan(); };
        w.Closing += (s, e) => {
            int running = tabs.Count(t => t.Started && !t.Exited);
            if (running > 0 && shot == null) {
                var r = MessageBox.Show(w, "실행 중인 터미널이 " + running + "개 있습니다.\n창을 닫으면 모두 끝납니다. 닫을까요?", "Claude 런처", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
            }
            foreach (var t in tabs) { if (t.Pty != null) t.Pty.Kill(); DeleteStatus(t); }
        };
        if (shot != null) w.Topmost = true;
        UpdateButtons();
        RenderTabs();
    }

    // ---------- 새 프로젝트 ----------
    void ShowNewProj(bool on) {
        newBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        btnNewProj.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        newErr.Visibility = Visibility.Collapsed;
        if (on) { newName.Text = ""; newName.Focus(); }
    }
    void CreateProject() {
        string name = (newName.Text ?? "").Trim();
        string err = null;
        if (name.Length == 0) err = "이름을 적어 주세요.";
        else if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) err = "폴더 이름에 쓸 수 없는 글자가 있습니다.  \\ / : * ? \" < > |";
        else if (name == "." || name == ".." || name.StartsWith(".")) err = "점(.)으로 시작하는 이름은 쓸 수 없습니다.";
        else if (name.EndsWith(".") || name.EndsWith(" ")) err = "이름 끝에 점이나 빈칸을 둘 수 없습니다.";
        else if (Directory.Exists(System.IO.Path.Combine(Scanner.Root, name)) || File.Exists(System.IO.Path.Combine(Scanner.Root, name))) err = "같은 이름의 폴더가 이미 있습니다.";
        if (err != null) { newErr.Text = err; newErr.Visibility = Visibility.Visible; return; }
        try { Directory.CreateDirectory(System.IO.Path.Combine(Scanner.Root, name)); }
        catch (Exception ex) { newErr.Text = "만들지 못했습니다: " + ex.Message; newErr.Visibility = Visibility.Visible; return; }
        ShowNewProj(false);
        pendingProj = name; pendingStart = true;
        status.Text = "새 프로젝트 폴더를 만들었습니다  ·  " + name;
        lastScan = DateTime.MinValue; Rescan(true);
    }

    static string When(DateTime t) {
        if (t == DateTime.MinValue) return "";
        var today = DateTime.Today;
        if (t.Date == today) return "오늘 " + t.ToString("HH:mm");
        if (t.Date == today.AddDays(-1)) return "어제 " + t.ToString("HH:mm");
        if (t.Year == today.Year) return t.ToString("M월 d일 HH:mm");
        return t.ToString("yyyy.M.d HH:mm");
    }

    Proj CurProj() { var v = lbProj.SelectedItem as ProjVM; return v == null ? null : v.P; }
    Session CurSess() { var v = lbSess.SelectedItem as SessVM; return v == null ? null : v.S; }

    public void PreSelect(string name) { pendingProj = name; }
    void ToggleDir(Proj p, bool open) {
        if (p == null || p.Rel.Length == 0) return;
        if (open) openDirs.Add(p.Rel); else openDirs.Remove(p.Rel);
        SaveView();
        BindTree(p.Name, null);
    }
    void SelectDir(string name) {
        int k = projects.FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (k >= 0) { lbProj.SelectedIndex = k; lbProj.ScrollIntoView(lbProj.SelectedItem); FocusList(lbProj); }
    }
    // 트리를 다시 그린다. 고른 폴더가 접힌 폴더 안에 있으면 위 폴더를 펼친다.
    void BindTree(string keepProj, string keepSess) {
        if (keepProj != null && keepProj != ".") {
            string r = keepProj; int i;
            while ((i = r.LastIndexOf('\\')) > 0) { r = r.Substring(0, i); openDirs.Add(r); }
        }
        projects = Scanner.Tree(all, openDirs);
        var vms = projects.Select(p => new ProjVM { P = p, Display = p.Display,
            Glyph = p.Name == "." ? "\xE80F" : (p.Open ? "\xE838" : "\xE8B7"),
            Indent = new Thickness(p.Depth * 16, 0, 0, 0),
            Chev = p.Open ? "\xE70D" : "\xE76C",
            ChevVis = p.HasKids ? Visibility.Visible : Visibility.Hidden,
            Sub = DirSub(p) }).ToList();
        lbProj.ItemsSource = vms;
        int k = keepProj == null ? -1 : projects.FindIndex(p => p.Name.Equals(keepProj, StringComparison.OrdinalIgnoreCase));
        if (k < 0) k = projects.FindIndex(p => p.Total > 0);
        lbProj.SelectedIndex = k >= 0 ? k : (projects.Count > 0 ? 0 : -1);
        if (lbProj.SelectedItem != null) lbProj.ScrollIntoView(lbProj.SelectedItem);
        ShowSessions(keepSess);
    }
    static string DirSub(Proj p) {
        if (p.Total == 0) return "세션 없음";
        string w = " · " + When(p.Last);
        if (p.Count == p.Total) return "세션 " + p.Count + "개" + w;
        if (p.Count == 0) return "하위에 " + p.Total + "개" + w;
        return "세션 " + p.Count + "개 (+" + (p.Total - p.Count) + ")" + w;
    }
    static readonly System.Globalization.CultureInfo Ko = new System.Globalization.CultureInfo("ko-KR");
    static string DayGroup(DateTime t) {
        var today = DateTime.Today;
        string d = t.Year == today.Year ? t.ToString("M월 d일 (ddd)", Ko) : t.ToString("yyyy년 M월 d일 (ddd)", Ko);
        if (t.Date == today) return "오늘  ·  " + d;
        if (t.Date == today.AddDays(-1)) return "어제  ·  " + d;
        return d;
    }
    // 목록 둘째 줄: 이어 받은 세션이 앞에 붙이는 긴 안내 문구는 걷어 내고 내용만 보인다.
    static string CleanPreview(string s) {
        if (s == null) return "";
        s = Regex.Replace(s, @"^\s*Background \(from the session that started this one\):\s*", "");
        var m = Regex.Match(s, @"^이전 세션에서 넘겨받은 인계 기록입니다:\s*(\S+?\.md)");
        if (m.Success) return "↪ 인계 받아 시작  ·  " + System.IO.Path.GetFileName(m.Groups[1].Value);
        return s;
    }
    static string ShortWt(string wt) { return wt == null ? null : Regex.Replace(wt, @"-[0-9a-f]{6,}$", ""); }

    void FocusList(ListBox lb) {
        if (lb.Items.Count == 0) { lb.Focus(); return; }
        if (lb.SelectedIndex < 0) lb.SelectedIndex = 0;
        lb.ScrollIntoView(lb.SelectedItem);
        lb.UpdateLayout();
        var c = lb.ItemContainerGenerator.ContainerFromIndex(lb.SelectedIndex) as ListBoxItem;
        if (c != null) c.Focus(); else lb.Focus();
    }
    void FocusSess() { FocusList(lbSess); }

    void UpdateButtons() {
        btnOpen.IsEnabled = CurSess() != null;
        btnNew.IsEnabled = CurProj() != null;
        btnFolder.IsEnabled = CurProj() != null;
    }

    void ShowSessions(string keepId) {
        var p = CurProj();
        if (keepId == null) { var c = CurSess(); if (c != null) keepId = c.Id; }
        string q = search.Text.Trim();
        string sel = p == null ? null : p.Rel;
        Func<Session, bool> inDir = s => sel != null && (withSub ? Scanner.Under(s.Rel, sel) : s.Rel.Equals(sel, StringComparison.OrdinalIgnoreCase));
        var list = p == null ? new List<Session>() : all.Where(inDir)
            .Where(s => q.Length == 0 || (s.Title ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                       || (s.First ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                       || (s.Rel ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                       || (s.Wt ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(s => s.Time).ToList();
        shown = list.Select(s => {
            string subDir = sel == null || s.Rel.Length <= sel.Length ? "" : s.Rel.Substring(sel.Length).TrimStart('\\');
            string wt = ShortWt(s.Wt);
            string prev = s.First == null || s.First == s.Title ? "" : OneLine(CleanPreview(s.First), 300);
            return new SessVM { S = s, Title = s.Title, When = s.Time.ToString("HH:mm"), Group = DayGroup(s.Time), Sub = prev,
                Worktree = subDir, WtVis = subDir.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
                Wt = wt == null ? null : "작업본 " + wt, WtNameVis = wt == null ? Visibility.Collapsed : Visibility.Visible,
                RowVis = subDir.Length == 0 && wt == null && prev.Length == 0 ? Visibility.Collapsed : Visibility.Visible };
        }).ToList();
        var view = new System.Windows.Data.ListCollectionView(shown);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("Group"));
        lbSess.ItemsSource = view;
        int k = keepId == null ? -1 : shown.FindIndex(x => x.S.Id == keepId);
        lbSess.SelectedIndex = k >= 0 ? k : (shown.Count > 0 ? 0 : -1);
        if (p != null) {
            txtProj.Text = p.Display;
            int total = all.Count(inDir);
            int own = all.Count(s => s.Rel.Equals(sel, StringComparison.OrdinalIgnoreCase)), deep = all.Count(s => Scanner.Under(s.Rel, sel));
            txtProjSub.Text = p.Dir + "   ·   " + (q.Length > 0 ? ("검색 결과 " + shown.Count + "개 / 전체 " + total + "개") : ("세션 " + total + "개"));
            btnSub.Visibility = deep > own ? Visibility.Visible : Visibility.Collapsed;
            txtSub.Inlines.Clear();
            txtSub.Inlines.Add(new Run(withSub ? "\xE73A" : "\xE739") { FontFamily = (FontFamily)R("Icons"), FontSize = 12, BaselineAlignment = BaselineAlignment.Center });
            txtSub.Inlines.Add(new Run("  하위 폴더 세션도 보기 (" + (deep - own) + ")"));
        } else { txtProj.Text = ""; txtProjSub.Text = ""; btnSub.Visibility = Visibility.Collapsed; }
        bool none = shown.Count == 0;
        emptyText.Text = q.Length > 0 ? "검색 결과가 없습니다." : "아직 세션이 없습니다.\n아래 '새 세션'으로 시작해 보세요.";
        empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        lbSess.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        UpdateButtons();
    }

    public void Rescan() { Rescan(false); }
    void Rescan(bool force) {
        if (scanning) return; scanning = true; lastScan = DateTime.Now;
        status.Text = "세션 목록을 읽는 중…";
        ThreadPool.QueueUserWorkItem(delegate {
            List<Session> list; try { list = Scanner.ScanAll(); } catch { list = new List<Session>(); }
            w.Dispatcher.BeginInvoke((Action)delegate {
                var cp = CurProj(); string keepProj = pendingProj ?? (cp != null ? cp.Name : null);
                var cs = CurSess(); string keepSess = cs != null ? cs.Id : null;
                all = list;
                BindTree(keepProj, keepSess);
                status.Text = "프로젝트 " + projects.Count(p => p.Depth == 0 && p.Name != ".") + "개  ·  세션 " + all.Count + "개  ·  " + DateTime.Now.ToString("HH:mm") + " 기준";
                scanning = false; lastScan = DateTime.Now;
                if (pendingProj != null) {
                    pendingProj = null;
                    if (pendingStart) { pendingStart = false; NewSession(); }
                }
                if (shot != null && !demo) {
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                    t.Tick += delegate { t.Stop(); Shot(); w.Close(); };
                    t.Start();
                }
            });
        });
    }

    public void Shot() {
        RECT r; GetWindowRect(new WindowInteropHelper(w).Handle, out r);
        using (var bmp = new System.Drawing.Bitmap(r.R - r.L, r.B - r.T))
        using (var g = System.Drawing.Graphics.FromImage(bmp)) {
            g.CopyFromScreen(r.L, r.T, 0, 0, bmp.Size);
            bmp.Save(shot);
        }
    }

    // ---------- 내장 터미널 ----------
    public async void InitWeb() {
        try {
            web = new WV2();
            web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string udf = System.IO.Path.Combine(baseDir, IsAdmin() ? "webdata-admin" : "webdata");
            var env = await CoreWebView2Environment.CreateAsync(null, udf);
            termHost.Children.Add(web);
            await web.EnsureCoreWebView2Async(env);
            var cw = web.CoreWebView2;
            cw.Settings.AreDefaultContextMenusEnabled = false;
            cw.Settings.AreDevToolsEnabled = false;
            cw.Settings.IsZoomControlEnabled = false;
            cw.Settings.AreBrowserAcceleratorKeysEnabled = false;
            cw.Settings.IsStatusBarEnabled = false;
            cw.PermissionRequested += (s, e) => { if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead) e.State = CoreWebView2PermissionState.Allow; };
            cw.SetVirtualHostNameToFolderMapping("launcher.local", System.IO.Path.Combine(baseDir, "web"), CoreWebView2HostResourceAccessKind.Allow);
            cw.WebMessageReceived += OnWebMessage;
            cw.Navigate("https://launcher.local/term.html");
        } catch (Exception ex) {
            status.Text = "내장 터미널을 준비하지 못했습니다: " + ex.Message;
        }
    }

    void Post(object msg) {
        string s = json.Serialize(msg);
        if (webReady && web != null && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(s);
        else queue.Add(s);
    }

    void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
        Dictionary<string, object> m;
        try { m = json.DeserializeObject(e.WebMessageAsJson) as Dictionary<string, object>; } catch { return; }
        if (m == null || !m.ContainsKey("t")) return;
        string t = m["t"] as string;
        if (t == "ready") {
            webReady = true;
            foreach (var q in queue) web.CoreWebView2.PostWebMessageAsJson(q);
            queue.Clear();
            return;
        }
        int id = Convert.ToInt32(m["id"]);
        var tab = tabs.FirstOrDefault(x => x.Id == id); if (tab == null) return;
        if (t == "i") { if (tab.Pty != null) tab.Pty.Write(m["d"] as string ?? ""); }
        else if (t == "r") {
            int cols = Convert.ToInt32(m["cols"]), rows = Convert.ToInt32(m["rows"]);
            if (!tab.Started) StartTab(tab, cols, rows); else if (tab.Pty != null) tab.Pty.Resize(cols, rows);
        }
    }

    void StartTab(TermTab tab, int cols, int rows) {
        tab.Started = true;
        var p = new Pty();
        p.Output = s => {
            lock (tab.Buf) { tab.Buf.Append(s); if (tab.FlushPending) return; tab.FlushPending = true; }
            w.Dispatcher.BeginInvoke((Action)delegate { Flush(tab); });
        };
        p.Exited = () => w.Dispatcher.BeginInvoke((Action)delegate {
            Flush(tab);
            tab.Exited = true;
            Post(new { t = "o", id = tab.Id, d = "\r\n\x1b[38;2;140;140;150m── 끝났습니다. 탭을 닫아도 됩니다. ──\x1b[0m\r\n" });
            RenderTabs();
        });
        tab.Pty = p;
        Environment.SetEnvironmentVariable("CLAUDE_LAUNCHER_TAB", tab.Key);
        try { p.Start(tab.Cmd, tab.Dir, cols, rows); }
        catch (Exception ex) {
            tab.Exited = true;
            Post(new { t = "o", id = tab.Id, d = "\x1b[31m" + ex.Message + "\x1b[0m\r\n" });
        }
        RenderTabs();
    }

    void Flush(TermTab tab) {
        string s;
        lock (tab.Buf) { s = tab.Buf.ToString(); tab.Buf.Clear(); tab.FlushPending = false; }
        if (s.Length > 0) Post(new { t = "o", id = tab.Id, d = s });
        if (LogPath != null && s.Length > 0) try { File.AppendAllText(LogPath + ".term" + tab.Id + ".txt", s); } catch { }
    }

    void OpenTab(string title, string dir, string cmd, string sessionId) {
        dir = (dir ?? "").TrimEnd('\\');
        if (!Directory.Exists(dir)) { MessageBox.Show(w, "폴더가 없어서 열 수 없습니다.\n" + dir, "Claude 런처", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (sessionId != null) {
            var ex = tabs.FirstOrDefault(x => x.SessionId == sessionId && !x.Exited);
            if (ex != null) { Activate(ex); return; }
        }
        if (web == null) { LaunchExternalCmd(dir, cmd, title); return; }
        var tab = new TermTab { Id = nextId++, Title = title ?? "Claude", Dir = dir, Cmd = cmd, SessionId = sessionId };
        tab.Key = Process.GetCurrentProcess().Id + "-" + tab.Id;
        tab.UltraPending = launchUltra || UltraRemembered(sessionId);
        tabs.Add(tab);
        Post(new { t = "create", id = tab.Id });
        Activate(tab);
        status.Text = "터미널에서 여는 중  ·  " + tab.Title;
    }

    void Activate(TermTab tab) {
        activeTab = tab;
        bool term = tab != null;
        listPane.Visibility = term ? Visibility.Collapsed : Visibility.Visible;
        termCard.Visibility = term ? Visibility.Visible : Visibility.Collapsed;
        Layout();
        if (term) {
            SyncProject(tab);
            Post(new { t = "show", id = tab.Id });
            w.Dispatcher.BeginInvoke((Action)delegate { if (web != null) { web.Focus(); Post(new { t = "focus" }); } }, DispatcherPriority.Input);
        }
        RenderTabs();
    }

    // 터미널 탭을 고르면 그 탭이 열린 폴더의 프로젝트를 왼쪽 목록에서 골라 둔다.
    // 가장 길게 겹치는 프로젝트 폴더를 고른다. 워크트리(.claude\worktrees\…)는 그 프로젝트로, 그 밖은 최상위 Claude 폴더로 간다.
    void SyncProject(TermTab tab) {
        if (tab == null || string.IsNullOrEmpty(tab.Dir) || lbProj.Items.Count == 0) return;
        string dir = tab.Dir.TrimEnd('\\') + "\\";
        int best = -1, len = -1;
        for (int i = 0; i < lbProj.Items.Count; i++) {
            var vm = lbProj.Items[i] as ProjVM; if (vm == null || vm.P == null || string.IsNullOrEmpty(vm.P.Dir)) continue;
            string pd = vm.P.Dir.TrimEnd('\\') + "\\";
            if (dir.StartsWith(pd, StringComparison.OrdinalIgnoreCase) && pd.Length > len) { best = i; len = pd.Length; }
        }
        if (best < 0 || lbProj.SelectedIndex == best) return;
        lbProj.SelectedIndex = best;
        lbProj.ScrollIntoView(lbProj.SelectedItem);
    }

    void CloseTab(TermTab tab) {
        if (tab.Pty != null) tab.Pty.Kill();
        tabs.Remove(tab);
        Post(new { t = "close", id = tab.Id });
        DeleteStatus(tab);
        if (activeTab == tab) Activate(tabs.Count > 0 ? tabs[tabs.Count - 1] : null);
        else RenderTabs();
    }

    void RenderTabs() {
        UpdateUltraVisual();
        tabStrip.Children.Clear();
        tabScroll.Visibility = tabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (tabs.Count == 0) return;
        tabStrip.Children.Add(MakeTab(null));
        foreach (var t in tabs) tabStrip.Children.Add(MakeTab(t));
    }

    FrameworkElement MakeTab(TermTab t) {
        bool active = t == activeTab;
        var b = new Border { CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 0, t == null ? 14 : 4, 0), Height = 36, Cursor = Cursors.Hand };
        b.Background = active ? B("#24FFFFFF") : Brushes.Transparent;
        b.BorderBrush = active ? (Brush)R("GlassEdge") : Brushes.Transparent;
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Brush fg = active ? (Brush)R("Ink") : (Brush)R("Dim");
        if (t == null) {
            sp.Children.Add(new TextBlock { Text = "\xE8FD", FontFamily = (FontFamily)R("Icons"), FontSize = 13, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = "세션 목록", Margin = new Thickness(8, 0, 0, 0), Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        } else {
            sp.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = t.Exited ? B("#55FFFFFF") : (Brush)R("AccentBrush"), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = t.Title, MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(9, 0, 0, 0), Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
            var x = new Button { Content = "\xE711", Style = (Style)R("IconBtn"), Width = 26, Height = 26, FontSize = 10, Margin = new Thickness(6, 0, 0, 0), ToolTip = "탭 닫기" };
            x.Click += (s, e) => { CloseTab(t); e.Handled = true; };
            sp.Children.Add(x);
            b.ToolTip = t.Title + "\n" + t.Dir;
        }
        b.Child = sp;
        b.MouseLeftButtonUp += (s, e) => { if (activeTab != t) Activate(t); };
        b.MouseEnter += delegate { if (activeTab != t) b.Background = B("#10FFFFFF"); };
        b.MouseLeave += delegate { if (activeTab != t) b.Background = Brushes.Transparent; };
        return b;
    }

    static string PsCmd(string claudeArgs) {
        return "powershell.exe -NoLogo -NoExit -Command claude" + (claudeArgs.Length > 0 ? " " + claudeArgs : "");
    }
    // 내장 터미널용: 상태줄을 런처로 돌리는 설정과 고른 모델·추론 수준을 붙인다.
    string InnerArgs(string resumeId, string newId) { return InnerArgs(resumeId, newId, launchModel, launchEffort); }
    string InnerArgs(string resumeId, string newId, string model, string effort) {
        var sb = new StringBuilder("--settings '" + System.IO.Path.Combine(BaseDir, "launcher-settings.json") + "'");
        if (model != null) sb.Append(" --model '" + model.Replace("'", "") + "'");
        if (effort != null) sb.Append(" --effort " + Regex.Replace(effort, "[^a-z]", ""));
        if (resumeId != null) sb.Append(" --resume " + resumeId);
        else if (newId != null) sb.Append(" --session-id " + newId);
        return sb.ToString();
    }

    void LaunchExternal(string dir, string claudeArgs, string title) {
        LaunchExternalCmd((dir ?? "").TrimEnd('\\'), PsCmd(claudeArgs), title);
    }
    void LaunchExternalCmd(string dir, string cmd, string title) {
        if (!Directory.Exists(dir)) return;
        string t = Regex.Replace(title ?? "Claude", "[\";]", " ");
        if (t.Length > 40) t = t.Substring(0, 40) + "…";
        Environment.SetEnvironmentVariable("CLAUDE_LAUNCHER", null);
        try { Process.Start(new ProcessStartInfo("wt.exe", "-w ClaudeLauncher new-tab --title \"" + t + "\" -d \"" + dir + "\" " + cmd) { UseShellExecute = true }); }
        catch { try { Process.Start(new ProcessStartInfo("cmd.exe", "/c start \"\" " + cmd) { UseShellExecute = true, WorkingDirectory = dir }); } catch { } }
        finally { Environment.SetEnvironmentVariable("CLAUDE_LAUNCHER", "1"); }
    }


    // ---------- 화면 배치 ----------
    void Layout() {
        bool term = activeTab != null;
        // 왼쪽 프로젝트 영역은 늘 보인다. 터미널을 쓰다가도 다른 프로젝트·세션으로 바로 옮겨 갈 수 있다.
        colProj.Width = new GridLength(270); colGap1.Width = new GridLength(18);
        projCard.Visibility = Visibility.Visible;
        bool showPanel = term && panelOn;
        colPanel.Width = new GridLength(showPanel ? 330 : 0); colGap2.Width = new GridLength(showPanel ? 18 : 0);
        panel.Visibility = showPanel ? Visibility.Visible : Visibility.Collapsed;
        btnPanel.Content = panelOn ? "\xE89F" : "\xE8A0";
        actions.Visibility = term ? Visibility.Collapsed : Visibility.Visible;
        UpdateChips(); RenderPanel(true);
    }

    // ---------- 모델·추론 수준 ----------
    static string Family(string id) { var m = Regex.Match(id ?? "", "opus|sonnet|haiku|fable", RegexOptions.IgnoreCase); return m.Success ? m.Value.ToLowerInvariant() : null; }
    static string ShortModel(string id, string name) {
        var m = Regex.Match(id ?? "", @"claude-([a-z]+)-(\d+)-(\d+)");
        if (m.Success) return char.ToUpper(m.Groups[1].Value[0]) + m.Groups[1].Value.Substring(1) + " " + m.Groups[2].Value + "." + m.Groups[3].Value;
        var f = Family(id); if (f != null) { var row = MODELS.FirstOrDefault(x => x[0] == f); if (row != null) return row[1]; }
        return name ?? id ?? "?";
    }
    static Brush ModelBrush(string id) {
        switch (Family(id)) {
            case "opus": return B("#D2A8FF"); case "sonnet": return B("#79C0FF");
            case "haiku": return B("#7EE0B0"); case "fable": return B("#FFB37A");
        }
        return (Brush)R("Ink");
    }
    static string EffortName(string lv) { var row = EFFORTS.FirstOrDefault(x => x[0] == lv); return row != null ? row[1] : lv; }

    void UpdateChips() {
        var t = activeTab;
        string modelKey, modelText, effText;
        if (t == null) {
            modelKey = launchModel;
            modelText = launchModel == null ? "기본 모델" : ShortModel(launchModel, null);
            effText = launchEffort == null ? "기본 수준" : EffortName(launchEffort);

        } else {
            string id = Str(t.St, "model", "id"), name = Str(t.St, "model", "display_name"), eff = Str(t.St, "effort", "level");
            bool fresh = (DateTime.Now - t.PendAt).TotalSeconds < 30;
            if (fresh && t.PendModel != null && Family(id) != t.PendModel) id = t.PendModel;
            if (fresh && t.PendEffort != null && eff != t.PendEffort) eff = t.PendEffort;
            if (id == null) id = launchModelOf(t);
            modelKey = id;
            modelText = id == null ? "모델 확인 중" : ShortModel(id, name);
            effText = eff == null ? "수준 확인 중" : EffortName(eff);

        }
        txtModel.Text = "◆ " + modelText;
        txtModel.Foreground = ModelBrush(modelKey);
        bool ultra = t == null ? launchUltra : (t.Ultra || t.UltraPending);
        txtEffort.Inlines.Clear();
        txtEffort.Inlines.Add(new Run(effText));
        if (ultra) { txtEffort.Inlines.Add(new Run("  ")); txtEffort.Inlines.Add(new Run("Ultracode") { Foreground = Rainbow(false), FontWeight = FontWeights.SemiBold }); }
        UpdateUltraVisual();
    }
    string launchModelOf(TermTab t) { var m = Regex.Match(t.Cmd ?? "", @"--model (\w+)"); return m.Success ? m.Groups[1].Value : null; }

    void ShowPicker(Button anchor, bool isModel) {
        // 열린 메뉴를 다시 누르면 닫는다. 메뉴 밖을 누르면 메뉴가 먼저 닫히므로, 방금 같은 메뉴가 닫혔으면 다시 열지 않는다.
        if (picker != null && picker.IsOpen) { bool same = pickerIsModel == isModel; picker.IsOpen = false; if (same) return; }
        if (pickerIsModel == isModel && (DateTime.Now - pickerClosedAt).TotalMilliseconds < 400) { pickerClosedAt = DateTime.MinValue; return; }
        var cm = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, VerticalOffset = -6 };
        cm.Closed += delegate { pickerClosedAt = DateTime.Now; if (picker == cm) picker = null; };
        picker = cm; pickerIsModel = isModel;
        var t = activeTab;
        string cur;
        if (t == null) cur = isModel ? launchModel : launchEffort;
        else cur = isModel ? Family(Str(t.St, "model", "id")) : Str(t.St, "effort", "level");
        if (t == null) {
            var d = new MenuItem { Header = isModel ? "기본값 (설정을 따름)" : "기본값 (설정을 따름)", Tag = cur == null ? "\xE73E" : "" };
            d.Click += delegate { Choose(isModel, null); };
            cm.Items.Add(d);
        } else if (t.Exited || t.Pty == null) { status.Text = "끝난 세션은 바꿀 수 없습니다."; return; }
        foreach (var row in isModel ? MODELS : EFFORTS) {
            string v = row[0];
            var mi = new MenuItem { Header = row[1], Tag = cur == v ? "\xE73E" : "" };
            mi.Click += delegate { Choose(isModel, v); };
            cm.Items.Add(mi);
        }
        if (!isModel) {
            bool on = t == null ? launchUltra : t.Ultra;
            cm.Items.Add(new Separator { Background = B("#22FFFFFF"), Margin = new Thickness(6, 4, 6, 4), Height = 1 });
            var uh = new TextBlock();
            uh.Inlines.Add(new Run("Ultracode") { Foreground = Rainbow(false), FontWeight = FontWeights.SemiBold });
            uh.Inlines.Add(new Run(on ? " 끄기" : " 켜기"));
            var u = new MenuItem { Header = uh, Tag = on ? "\xE73E" : "\xE945" };
            u.Click += delegate { ToggleUltra(); };
            cm.Items.Add(u);
        }
        cm.IsOpen = true;
    }

    void Choose(bool isModel, string v) {
        var t = activeTab;
        if (t == null) {
            if (isModel) launchModel = v; else launchEffort = v;
            UpdateChips(); return;
        }
        if (t.Pty == null || t.Exited || v == null) return;
        // 글자와 Enter를 나눠 보낸다. 한 번에 보내면 붙여넣기로 여겨 Enter가 줄바꿈이 될 수 있다.
        TypeLine(t, (isModel ? "/model " : "/effort ") + v);
        if (isModel) t.PendModel = v; else t.PendEffort = v;
        t.PendAt = DateTime.Now;
        status.Text = (isModel ? "모델을 " + ShortModel(v, v) : "추론 수준을 " + EffortName(v)) + "(으)로 바꾸는 중";
        UpdateChips();
        if (web != null) { web.Focus(); Post(new { t = "focus" }); }
    }

    // ---------- 상태 읽기 ----------
    static string StatusPath(string name) { return System.IO.Path.Combine(BaseDir, "status", name); }
    void DeleteStatus(TermTab t) {
        if (t.Key == null) return;
        try { File.Delete(StatusPath(t.Key + ".json")); } catch { }
        try { File.Delete(StatusPath(t.Key + "-pane.json")); } catch { }
        try { File.Delete(StatusPath(t.Key + "-ultra")); } catch { }
    }
    Dictionary<string, object> ReadJson(string path, ref DateTime stamp, Dictionary<string, object> old) {
        try {
            if (!File.Exists(path)) return old;
            var m = File.GetLastWriteTimeUtc(path);
            if (m == stamp && old != null) return old;
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
            var d = json.DeserializeObject(text) as Dictionary<string, object>;
            if (d == null) return old;
            stamp = m; return d;
        } catch { return old; }
    }
    void Tick() {
        CheckHandoffs();
        var t = activeTab;
        if (w.WindowState == WindowState.Minimized) return;
        if (t == null || t.Key == null) { RenderPanel(); return; }
        t.St = ReadJson(StatusPath(t.Key + ".json"), ref t.StM, t.St);
        t.Pane = ReadJson(StatusPath(t.Key + "-pane.json"), ref t.PaneM, t.Pane);
        // 울트라코드로 시작을 골랐으면 Claude가 처음 응답 준비를 마친 뒤(첫 상태 정보가 온 뒤) 켠다.
        if (t.UltraPending && t.St != null && t.Pty != null && !t.Exited) { t.UltraPending = false; t.Ultra = true; t.UltraSetAt = DateTime.Now; TypeLine(t, "/effort ultracode"); RememberUltra(t); }
        // 사용자가 직접 /effort ultracode 를 쳐도 모드가 알려 준다. 버튼으로 바꾼 직후 몇 초는 모드가 따라올 때까지 기다린다.
        foreach (var tt in tabs) ScanUltra(tt);
        UpdateChips();
        RenderPanel();
    }

    static object Get(object d, params string[] path) {
        object cur = d;
        foreach (var k in path) { var dict = cur as Dictionary<string, object>; if (dict == null || !dict.TryGetValue(k, out cur)) return null; }
        return cur;
    }
    static string Str(object d, params string[] path) { var v = Get(d, path); return v == null ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); }
    static double? Num(object d, params string[] path) {
        var v = Get(d, path);
        if (v == null || v is string || v is bool) return null;
        try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return null; }
    }
    static string KTok(double n) { return n >= 1e6 ? (n / 1e6).ToString("0.0") + "M" : n >= 1e3 ? Math.Round(n / 1e3) + "k" : ((long)n).ToString(); }
    static string Dur(double ms) {
        long s = (long)Math.Max(0, ms / 1000);
        if (s < 60) return s + "초";
        if (s < 3600) return (s / 60) + "분 " + (s % 60).ToString("00") + "초";
        return (s / 3600) + "시간 " + ((s % 3600) / 60) + "분";
    }
    static DateTime FromEpoch(double sec) { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(sec).ToLocalTime(); }
    static double NowMs() { return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

    // ---------- 오른쪽 대시보드 ----------
    TextBlock T(string text, double size, Brush fg, bool bold = false) {
        return new TextBlock { Text = text, FontSize = size, Foreground = fg, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    }
    void Section(string title, string note, bool first = false) {
        var g = new Grid { Margin = new Thickness(0, first ? 0 : 22, 0, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(T(title, 13, (Brush)R("Ink"), true));
        if (note != null) { var n = T(note, 11.5, (Brush)R("Faint")); n.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(n, 1); g.Children.Add(n); }
        panelBody.Children.Add(g);
    }
    static Brush Level(double p, Brush normal) { return p >= 90 ? B("#E08A7C") : p >= 70 ? B("#DDB06A") : normal; }
    FrameworkElement Bar(double? pct, Brush color, double height = 5) {
        var g = new Grid { Height = height, Margin = new Thickness(0, 6, 0, 0) };
        double p = Math.Max(0, Math.Min(100, pct ?? 0));
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(p, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - p, GridUnitType.Star) });
        var bg = new Border { CornerRadius = new CornerRadius(height / 2), Background = B("#1CFFFFFF") }; Grid.SetColumnSpan(bg, 2); g.Children.Add(bg);
        if (p > 0) g.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = color });
        return g;
    }
    void Meter(string label, double? pct, string tail, Brush color, bool stale = false) {
        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 12), Opacity = stale ? 0.6 : 1 };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(T(label, 12, (Brush)R("Dim")));
        var pt = T(pct == null ? "—" : Math.Round(pct.Value) + "%", 12.5, pct == null ? (Brush)R("Faint") : Level(pct.Value, (Brush)R("Ink")), true);
        Grid.SetColumn(pt, 1); top.Children.Add(pt);
        box.Children.Add(top);
        box.Children.Add(Bar(pct, pct == null ? B("#00FFFFFF") : Level(pct.Value, color)));
        if (!string.IsNullOrEmpty(tail)) { var tl = T(tail, 11, (Brush)R("Faint")); tl.Margin = new Thickness(0, 5, 0, 0); box.Children.Add(tl); }
        panelBody.Children.Add(box);
    }

    Dictionary<string, object> cachedLimits; DateTime limitsStamp;
    void RenderPanel() { RenderPanel(false); }
    void RenderPanel(bool force) {
        if (panelBody == null) return;
        var at = activeTab;
        bool live = at != null && at.Pane != null && (Get(at.Pane, "agents") as IEnumerable ?? new object[0]).Cast<object>().Any(a => Str(a, "status") == "running" || Str(a, "status") == "pending");
        string sig = (at == null ? "-" : at.Key + "|" + at.StM.Ticks + "|" + at.PaneM.Ticks + "|" + at.HandoffWaiting + "" + at.Exited + (at.St != null)) + "|" + (live ? DateTime.Now.ToString("HHmmss") : DateTime.Now.ToString("HHmm")) + "|" + limitsStamp.Ticks + "|" + panel.Visibility + "|" + panelCat + "|" + PlanStamp(at);
        if (!force && sig == lastSig) return;
        lastSig = sig;
        RenderFooter(at);
        if (panel.Visibility == Visibility.Visible) RenderBody(at);
    }

    // ---------- 하단: 사용량 ----------
    // 하단 칸 하나: 이름 · 게이지 · 수치 · 덧붙임. 창이 좁으면(footDense) 짧은 영문 이름, 짧은 게이지, 덧붙임 생략.
    FrameworkElement Mini(string label, string shortLabel, double? pct, string tail, Brush color, bool stale = false, bool higherIsGood = false, string valueText = null) {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = stale ? 0.6 : 1 };
        sp.Children.Add(T(footDense > 0 ? shortLabel : label, 12, (Brush)R("Dim")));
        Brush fill = pct == null ? B("#00FFFFFF") : higherIsGood ? (pct.Value < 50 ? B("#E08A7C") : pct.Value < 80 ? B("#DDB06A") : color) : Level(pct.Value, color);
        var bar = Bar(pct, fill, 4);
        bar.Width = footDense == 0 ? 54 : footDense == 1 ? 36 : 24; bar.Margin = new Thickness(footDense == 2 ? 5 : 8, 0, footDense == 2 ? 5 : 8, 0); bar.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(bar);
        Brush vb = pct == null ? (Brush)R("Faint") : higherIsGood ? (Brush)R("Ink") : Level(pct.Value, (Brush)R("Ink"));
        sp.Children.Add(T(valueText ?? (pct == null ? "—" : Math.Round(pct.Value) + "%"), 12, vb, true));
        if (!string.IsNullOrEmpty(tail) && footDense < 2) { var tl = T(tail, 11.5, (Brush)R("Faint")); tl.Margin = new Thickness(6, 0, 0, 0); sp.Children.Add(tl); }
        return sp;
    }
    FrameworkElement Pair(string label, string value, Brush valueBrush, string tail, Brush tailBrush) {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(T(label, 12, (Brush)R("Dim")));
        var v = T(value, 12.5, valueBrush, true); v.Margin = new Thickness(8, 0, 0, 0); sp.Children.Add(v);
        if (!string.IsNullOrEmpty(tail)) { var tl = T(tail, 11.5, tailBrush ?? (Brush)R("Faint")); tl.Margin = new Thickness(6, 0, 0, 0); sp.Children.Add(tl); }
        return sp;
    }
    void FootSep() { double g = footDense == 0 ? 14 : footDense == 1 ? 10 : 7; footUsage.Children.Add(new Border { Width = 1, Height = 14, Background = B("#22FFFFFF"), Margin = new Thickness(g, 0, g, 0), VerticalAlignment = VerticalAlignment.Center }); }
    void RenderFooter(TermTab t) {
        footUsage.Children.Clear();
        var st = t == null ? null : t.St;
        Brush mc = ModelBrush(Str(st, "model", "id") ?? (t == null ? launchModel : launchModelOf(t)));
        if (t != null) {
            double? total = Num(st, "context_window", "total_input_tokens"), size = Num(st, "context_window", "context_window_size");
            double? used = Num(st, "context_window", "used_percentage");
            // 압축 직후에는 다음 응답 전까지 쓴 양이 비어(0) 온다. 0/1.0M 대신 갱신을 기다린다고 알린다.
            bool waiting = st != null && (used == null || total == null || total.Value <= 0);
            footUsage.Children.Add(Mini("컨텍스트", "CTX", waiting ? null : used, waiting ? (st == null ? null : "다음 응답 뒤 갱신") : total != null && size != null ? KTok(total.Value) + "/" + KTok(size.Value) : null, mc));
            FootSep();
        }
        var rl = Get(st, "rate_limits") as Dictionary<string, object>;
        bool stale = false;
        if (rl == null || (Get(rl, "five_hour") == null && Get(rl, "seven_day") == null)) {
            cachedLimits = ReadJson(System.IO.Path.Combine(Scanner.ClaudeHome, @"mods\statusline\statusline-ratelimits.json"), ref limitsStamp, cachedLimits);
            rl = cachedLimits; stale = true;
        }
        double now = NowMs();
        double? r5 = Num(rl, "five_hour", "resets_at"), r7 = Num(rl, "seven_day", "resets_at");
        // 이미 다시 찬 창의 지난 값은 쓰지 않는다.
        if (r5 != null && r5.Value * 1000 < now) r5 = null;
        if (r7 != null && r7.Value * 1000 < now) r7 = null;
        double? p5 = r5 == null && stale ? (double?)null : Num(rl, "five_hour", "used_percentage");
        double? p7 = r7 == null && stale ? (double?)null : Num(rl, "seven_day", "used_percentage");
        footUsage.Children.Add(Mini("5시간 한도", "5H", p5, r5 == null ? null : FromEpoch(r5.Value).ToString("HH:mm"), mc, stale));
        FootSep();
        footUsage.Children.Add(Mini("7일 한도", "7D", p7, r7 == null ? null : FromEpoch(r7.Value).ToString(footDense > 0 ? "M/d" : "M/d HH:mm"), mc, stale));
        if (t == null) return;
        double? cost = Num(st, "cost", "total_cost_usd");
        FootSep();
        footUsage.Children.Add(Pair("비용", cost == null ? "—" : "$" + cost.Value.ToString("0.00"), (Brush)R("Ink"), null, null));
        var pc = Get(st, "prompt_cache");
        // 게이지는 캐시가 식기까지 남은 시간이다(시간이 지나면 줄고, 새 요청이 캐시를 쓰면 다시 찬다). 적중률은 덧붙인 글로만 보인다.
        double? leftPct = null; string val = "—", sub = null;
        if (pc != null && Get(pc, "caching_observed") is bool && (bool)Get(pc, "caching_observed")) {
            double? hit = Num(pc, "hit_ratio"), exp = Num(pc, "expires_at"), miss = Num(pc, "misses");
            bool warm = Get(pc, "warm") is bool && (bool)Get(pc, "warm");
            string ttl = Str(pc, "ttl");
            double ttlMin = ttl == "1h" ? 60 : ttl == "5m" ? 5 : 60;
            if (warm && exp != null) {
                double left = Math.Max(0, (exp.Value * 1000 - now) / 60000);
                leftPct = Math.Min(100, left * 100 / ttlMin);
                val = left >= 1 ? Math.Floor(left) + "분" : "1분 안";
            } else { leftPct = 0; val = "식음"; }
            if (hit != null) sub = "적중 " + Math.Round(hit.Value * 100) + "%";
            if (miss != null && miss.Value > 0 && footDense == 0) sub += " · 다시 읽음 " + miss.Value + "번";
        }
        FootSep();
        footUsage.Children.Add(Mini("프롬프트 캐시", "캐시", leftPct, sub, B("#7EE0B0"), false, true, val));
    }

    // ---------- 오른쪽: 에이전트 ----------
    // 에이전트 칸: 팀 구성(고양이 줄, 모델별 인원, 워크플로 묶음)과 에이전트 카드(진행, 컨텍스트, 지금 하는 일, 스킬)를 한곳에.
    void RenderAgents(TermTab t) {
        double off = 0; var sv = panelBody.Parent as ScrollViewer; if (sv != null) off = sv.VerticalOffset;
        panelBody.Children.Clear();
        UpdateHandoffButton(t);
        if (t == null) { panelBody.Children.Add(T("세션을 열면 보입니다", 12, (Brush)R("Faint"))); return; }
        var pane = t.Pane;
        Brush mc = ModelBrush(Str(t.St, "model", "id") ?? Str(pane, "model") ?? launchModelOf(t));
        var agents = (Get(pane, "agents") as IEnumerable ?? new object[0]).Cast<object>().ToList();
        var ACTIVE = new[] { "running", "pending" }; var FIN = new[] { "completed", "failed", "killed" };
        int fin = agents.Count(a => FIN.Contains(Str(a, "status"))), act = agents.Count(a => ACTIVE.Contains(Str(a, "status")));
        Section("에이전트", agents.Count == 0 ? null : "작동 " + act + " · 마침 " + fin + "/" + agents.Count, true);
        if (agents.Count == 0) { panelBody.Children.Add(T(pane == null ? "세션이 시작되면 보입니다" : "띄운 에이전트가 없습니다", 12, (Brush)R("Faint"))); return; }
        double now = NowMs();
        // 팀: 고양이 줄 + 모델별 인원
        var stage = new Border { CornerRadius = new CornerRadius(12), Background = B("#10FFFFFF"), BorderBrush = B("#1AFFFFFF"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 6), Margin = new Thickness(0, 0, 0, 10) };
        var stSp = new StackPanel(); stage.Child = stSp;
        var party = new WrapPanel();
        foreach (var a in agents.Take(24)) {
            string s1 = Str(a, "status");
            var pc = Pixel(Str(a, "model"), Str(a, "effort"), ACTIVE.Contains(s1), FIN.Contains(s1), 28, now);
            pc.ToolTip = Str(a, "description");
            var holder = new Grid { Width = 32, Height = 34, Margin = new Thickness(0, 0, 2, 2) }; holder.Children.Add(pc);
            party.Children.Add(holder);
        }
        stSp.Children.Add(party);
        var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var gm in agents.GroupBy(a => Str(a, "model") == null ? "?" : ShortModel(Str(a, "model"), Str(a, "model")))) {
            var pill = new Border { CornerRadius = new CornerRadius(9), Background = B("#14FFFFFF"), Padding = new Thickness(8, 2, 9, 3), Margin = new Thickness(0, 0, 6, 4) };
            var ps = new StackPanel { Orientation = Orientation.Horizontal };
            ps.Children.Add(T("◆ " + (gm.Key == "?" ? "모델 확인 중" : gm.Key), 11, ModelBrush(Str(gm.First(), "model")), true));
            ps.Children.Add(T("  " + gm.Count() + "명", 11, (Brush)R("Dim")));
            pill.Child = ps; chips.Children.Add(pill);
        }
        stSp.Children.Add(chips);
        panelBody.Children.Add(stage);
        Meter("전체 진행 (마친 에이전트 비율)", fin * 100.0 / agents.Count, null, mc);
        // 묶음: Ultracode 워크플로 → 직접 띄운 에이전트. 묶음마다 작동 중 먼저, 끝난 것은 최근 8개까지.
        int hidden = 0;
        var groups = agents.GroupBy(a => Str(a, "workflow") ?? "").OrderBy(g => g.Key == "" ? 1 : 0).ToList();
        foreach (var grp in groups) {
            if (groups.Count > 1 || grp.Key != "") {
                var head = new TextBlock { FontSize = 12, Margin = new Thickness(2, 8, 0, 8) };
                if (grp.Key == "") head.Inlines.Add(new Run("직접 띄운 에이전트") { Foreground = (Brush)R("Dim"), FontWeight = FontWeights.SemiBold });
                else { head.Inlines.Add(new Run("Ultracode 워크플로") { Foreground = Rainbow(false), FontWeight = FontWeights.SemiBold }); head.Inlines.Add(new Run("  " + (grp.Key.Length > 11 ? grp.Key.Substring(0, 11) : grp.Key)) { Foreground = (Brush)R("Faint") }); }
                panelBody.Children.Add(head);
            }
            var list = grp.ToList();
            var shownA = list.Where(a => !FIN.Contains(Str(a, "status"))).Concat(list.Where(a => FIN.Contains(Str(a, "status"))).Reverse().Take(8)).ToList();
            hidden += list.Count - shownA.Count;
            foreach (var a in shownA) panelBody.Children.Add(AgentCard(a, now, ACTIVE, FIN));
        }
        if (hidden > 0) panelBody.Children.Add(T("끝난 에이전트 " + hidden + "개는 줄였습니다", 11, (Brush)R("Faint")));
        if (sv != null) sv.ScrollToVerticalOffset(off);
    }


    // ---------- Ultracode 표시 ----------
    // 켜짐·꺼짐은 세션 기록에 남는 "/effort ultracode"의 답(Ultracode on / Ultracode off)으로 안다. 직접 입력해도 잡힌다.
    // 세션을 이어 열면 Ultracode는 꺼진 채 시작하므로, 처음 볼 때는 지난 기록을 읽지 않고 그 뒤에 새로 쌓인 것만 본다.
    static readonly Regex RxUltra = new Regex(@"""(?:content|text)"":""<local-command-stdout>\s*Ultracode (on|off)\b");
    // 세션마다 Ultracode를 켜 두었는지 기억한다(cache\ultra.txt). Ultracode는 "이 세션만" 켜져서 세션을 다시 열면 꺼지므로, 다시 열 때 런처가 다시 켠다.
    static HashSet<string> ultraMem;
    static string UltraFile { get { return System.IO.Path.Combine(BaseDir, @"cache\ultra.txt"); } }
    static bool UltraRemembered(string sid) {
        if (ultraMem == null) { ultraMem = new HashSet<string>(StringComparer.OrdinalIgnoreCase); try { foreach (var l in File.ReadAllLines(UltraFile)) if (l.Trim().Length > 0) ultraMem.Add(l.Trim()); } catch { } }
        return sid != null && ultraMem.Contains(sid);
    }
    void RememberUltra(TermTab t) {
        string sid = Str(t.St, "session_id") ?? t.SessionId;
        if (sid == null) return;
        UltraRemembered(null);
        bool changed = t.Ultra ? ultraMem.Add(sid) : ultraMem.Remove(sid);
        if (!changed) return;
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(UltraFile)); File.WriteAllLines(UltraFile, ultraMem); } catch { }
    }
    void ScanUltra(TermTab t) {
        string tp = Str(t.St, "transcript_path");
        if (string.IsNullOrEmpty(tp)) return;
        try {
            var fi = new FileInfo(tp); if (!fi.Exists) return;
            if (t.UltraScan < 0) { t.UltraScan = fi.Length; }
            else if (fi.Length > t.UltraScan) {
                string s;
                using (var fs = new FileStream(tp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { fs.Seek(t.UltraScan, SeekOrigin.Begin); var ms = new MemoryStream(); fs.CopyTo(ms); s = Encoding.UTF8.GetString(ms.ToArray()); }
                t.UltraScan = fi.Length;
                // "/effort ultracode"의 실제 답(local-command-stdout)만 본다. 대화나 도구 출력에 그 낱말이 섞여도 바뀌지 않게 한다.
                var ms2 = RxUltra.Matches(s);
                if (ms2.Count > 0) { t.Ultra = ms2[ms2.Count - 1].Groups[1].Value == "on"; t.UltraSetAt = DateTime.Now; RememberUltra(t); }
            } else if (fi.Length < t.UltraScan) t.UltraScan = fi.Length;
        } catch { }
        // 모드가 입력창 위에 표시를 그리도록 표시 파일을 둔다.
        if (t.Ultra != t.UltraFlag && t.Key != null) {
            try { var f = StatusPath(t.Key + "-ultra"); if (t.Ultra) File.WriteAllText(f, "1"); else if (File.Exists(f)) File.Delete(f); t.UltraFlag = t.Ultra; } catch { }
        }
    }
    // 줄바꿈과 여러 칸 띄움을 한 칸으로 줄이고, 길면 말줄임표를 붙인다. 줄바꿈이 남으면 목록 칸에서 둘째 줄이 잘려 글자 중간에서 끊겨 보인다.
    static string OneLine(string s, int max) { s = Regex.Replace(s ?? "", @"\s+", " ").Trim(); return s.Length > max ? s.Substring(0, max).TrimEnd() + "…" : s; }
    static readonly string[] RAINBOW = { "#EBA17E", "#D98AAE", "#A994F2", "#7FAEE8" };
    static LinearGradientBrush Rainbow(bool loop) {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        int n = RAINBOW.Length + (loop ? 1 : 0);
        for (int i = 0; i < n; i++) b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(RAINBOW[i % RAINBOW.Length]), i / (double)(n - 1)));
        return b;
    }
    void UpdateUltraVisual() {
        if (ultraGlow == null) return;
        var t = activeTab;
        bool on = t != null && t.Ultra && termCard.Visibility == Visibility.Visible;
        if (on == ultraShown) return;
        ultraShown = on;
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        ultraGlow.Visibility = vis; ultraEdge.Visibility = vis; ((FrameworkElement)ultraLabel.Parent).Visibility = vis;
        // 표시 문구는 터미널 안 입력창 위 줄에 모드가 그린다.
        if (on) {
            // 무지개 테두리가 천천히 돌고, 번짐이 숨 쉬듯 밝아졌다 흐려진다. 초당 30장만 그려 CPU를 아낀다.
            var glow = Rainbow(true); glow.StartPoint = new Point(0, 0); glow.EndPoint = new Point(1, 1);
            var rot = new RotateTransform(0, 0.5, 0.5); glow.RelativeTransform = rot;
            var edge = Rainbow(true); edge.StartPoint = new Point(0, 0); edge.EndPoint = new Point(1, 1);
            var rot2 = new RotateTransform(0, 0.5, 0.5); edge.RelativeTransform = rot2;
            ultraGlow.BorderBrush = glow; ultraEdge.BorderBrush = edge;
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(20)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, 30);
            rot.BeginAnimation(RotateTransform.AngleProperty, spin);
            rot2.BeginAnimation(RotateTransform.AngleProperty, spin);
            var pulse = new DoubleAnimation(0.08, 0.2, TimeSpan.FromSeconds(5)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            Timeline.SetDesiredFrameRate(pulse, 30);
            ultraGlow.BeginAnimation(UIElement.OpacityProperty, pulse);
            ultraEdge.Opacity = 0.42;
        } else {
            ultraGlow.BeginAnimation(UIElement.OpacityProperty, null);
            ultraGlow.BorderBrush = null; ultraEdge.BorderBrush = null;
        }
    }

    // ---------- 오른쪽 패널: 분류 ----------
    public void SetCat(string k) { panelCat = k; RenderPanel(true); }
    void RenderPanelTabs() {
        panelTabs.Children.Clear();
        foreach (var kv in new[] { new[] { "plan", "플랜" }, new[] { "agents", "에이전트" } }) {
            bool on = panelCat == kv[0];
            var tb = new TextBlock { Text = kv[1], FontSize = 12.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = on ? (Brush)R("Ink") : (Brush)R("Dim") };
            var b = new Border { Child = tb, Padding = new Thickness(13, 6, 13, 7), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 4, 0),
                Background = on ? B("#22FFFFFF") : Brushes.Transparent, BorderBrush = on ? (Brush)R("GlassEdge") : Brushes.Transparent, BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
            string key = kv[0];
            b.MouseEnter += delegate { if (panelCat != key) b.Background = B("#12FFFFFF"); };
            b.MouseLeave += delegate { if (panelCat != key) b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += delegate { if (panelCat == key) return; panelCat = key; var sv = panelBody.Parent as ScrollViewer; if (sv != null) sv.ScrollToTop(); RenderPanel(true); };
            panelTabs.Children.Add(b);
        }
    }
    void RenderBody(TermTab t) {
        RenderPanelTabs();
        if (panelCat == "agents") { RenderAgents(t); return; }
        double off = 0; var sv = panelBody.Parent as ScrollViewer; if (sv != null) off = sv.VerticalOffset;
        panelBody.Children.Clear();
        UpdateHandoffButton(t);
        RenderPlan(t);
        if (sv != null) sv.ScrollToVerticalOffset(off);
    }


    // ---------- 도트 캐릭터 ----------
    // 12×12 칸. o 테두리, s 얼굴, e 눈, h 모자(추론 수준 색), b 옷(모델 색), d 신발.
    // 고양이: f 털(모델 색), o 테두리, e 눈, n 코, h 목걸이(추론 수준 색), t 꼬리.
    static readonly string[] SPRITE = {
        "..o......o..", ".ofo....ofo.", ".offooooffo.", ".offffffffo.", ".ofeffffefo.", ".offfnnfffo.",
        "..offffffo.t", "..ohhhhhho.t", ".offffffffot", ".offffffffo.", "..ofo..ofo..", "..oo....oo.." };
    static readonly string[] LEGS_B = { "...ofo..ofo.", "...oo....oo." };
    static string EffortHat(string eff) {
        switch (eff) { case "medium": return "#A9AEB6"; case "high": return "#E58B66"; case "xhigh": return "#FFD95C"; case "max": return "#FF6B8B"; default: return null; }
    }
    static Color Hex(string h) { return (Color)ColorConverter.ConvertFromString(h); }
    FrameworkElement Pixel(string model, string eff, bool running, bool done, double size, double now) {
        var bc = ModelBrush(model) as SolidColorBrush;
        Color body = bc != null ? bc.Color : Hex("#B0B0B8");
        string hat = EffortHat(eff);
        bool stepB = running && ((long)(now / 1000)) % 2 == 1;
        var px = new byte[12 * 12 * 4];
        for (int y = 0; y < 12; y++) {
            string row = stepB && y >= 10 ? LEGS_B[y - 10] : SPRITE[y];
            // 걸을 때는 꼬리도 한 칸 위로 흔든다.
            if (stepB && y >= 5 && y <= 8) row = row.Substring(0, 11) + (y <= 7 ? "t" : SPRITE[y][11] == 't' ? "." : SPRITE[y].Substring(11));
            for (int x = 0; x < 12; x++) {
                char ch = row[x]; Color col; bool on = true;
                switch (ch) {
                    case 'o': col = Hex("#16161C"); break;
                    case 'e': col = Hex("#16161C"); break;
                    case 'n': col = Hex("#FF9AB0"); break;
                    case 'h': col = hat == null ? body : Hex(hat); break;
                    case 'f': col = body; break;
                    case 't': col = Color.FromRgb((byte)(body.R * 0.8), (byte)(body.G * 0.8), (byte)(body.B * 0.8)); break;
                    default: on = false; col = Colors.Transparent; break;
                }
                if (on && done) { byte gr = (byte)((col.R * 30 + col.G * 59 + col.B * 11) / 100); col = Color.FromRgb(gr, gr, gr); }
                int i = (y * 12 + x) * 4;
                px[i] = col.B; px[i + 1] = col.G; px[i + 2] = col.R; px[i + 3] = on ? (byte)255 : (byte)0;
            }
        }
        var bmp = BitmapSource.Create(12, 12, 96, 96, PixelFormats.Bgra32, null, px, 48);
        var img = new Image { Source = bmp, Width = size, Height = size, Opacity = done ? 0.55 : 1, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        // 일하는 중이면 한 칸씩 위아래로 뛴다.
        if (running && stepB) img.Margin = new Thickness(0, 0, 0, size / 12);
        else img.Margin = new Thickness(0, size / 12, 0, 0);
        return img;
    }


    // ---------- 세션 압축 설정 ----------
    // 저장: 런처 폴더의 compact.json (모드가 압축할 때 읽어 지시에 끼워 넣는다), 압축 시점은 ~/.claude/settings.json 의 autoCompactWindow.
    static readonly string[][] COMPACT_ITEMS = {
        new[] { "plan", "핵심 플랜", "사용자 요구와 단계별 진행 상태(완료·진행·대기). 런처 플랜 파일이 있으면 그 내용도." },
        new[] { "done", "완료된 작업 내용", "끝낸 일과 그 결과, 바꾼 파일(절대 경로)." },
        new[] { "now", "현재 진행 작업", "어디까지 했는지, 무슨 의도로, 왜 이 작업을 하고 있었는지, 바로 다음 단계. 비워 두지 말고, 플랜의 [진행] 단계나 마지막 사용자 요청에서 찾아 적는다." },
        new[] { "prefs", "사용자 지시와 선호", "사용자가 정한 규칙, 말투, 하지 말라고 한 것." },
        new[] { "why", "결정과 그 이유", "고른 방법과 버린 방법, 그 이유." },
        new[] { "errors", "오류와 해결", "겪은 오류, 원인, 고친 방법." } };
    static string CompactFile { get { return System.IO.Path.Combine(BaseDir, "compact.json"); } }
    static string UserSettings { get { return System.IO.Path.Combine(Scanner.ClaudeHome, "settings.json"); } }
    void LoadCompact() {
        try {
            var m = Regex.Match(File.ReadAllText(UserSettings), "\"autoCompactWindow\"\\s*:\\s*(\\d+)");
            if (m.Success) compactWindow = int.Parse(m.Groups[1].Value);
        } catch { }
        try {
            if (!File.Exists(CompactFile)) return;
            var o = json.DeserializeObject(File.ReadAllText(CompactFile));
            var items = Get(o, "items") as IEnumerable;
            if (items != null) compactItems = items.Cast<object>().Select(v => Convert.ToString(v)).ToList();
            compactCustom = Str(o, "custom") ?? "";
        } catch { }
    }
    static string BuildCompactNote(List<string> items, string custom) {
        var sb = new StringBuilder("[압축 지시] 이것은 요약을 쓰는 방법에 대한 사용자 지정 지시다. 요약 맨 앞에 \"## 인계 핵심\" 절을 따로 두고, 아래 항목마다 같은 이름의 소제목(###)을 달아 한국어로 구체적으로 쓴다. 다음 대화가 이 절만 보고 바로 이어 갈 수 있어야 한다. 그다음에 평소 요약을 쓴다.");
        foreach (var it in COMPACT_ITEMS) if (items.Contains(it[0])) sb.Append("\n- " + it[1] + ": " + it[2]);
        if (!string.IsNullOrWhiteSpace(custom)) sb.Append("\n- 사용자가 따로 정한 것: " + custom.Trim().Replace("\r", ""));
        return sb.ToString();
    }
    bool SaveCompact(int win, List<string> items, string custom) {
        try {
            File.WriteAllText(CompactFile, json.Serialize(new Dictionary<string, object> { { "window", win }, { "items", items }, { "custom", custom }, { "instructions", BuildCompactNote(items, custom) } }), new UTF8Encoding(false));
            string s = File.ReadAllText(UserSettings);
            string ns = Regex.IsMatch(s, "\"autoCompactWindow\"\\s*:\\s*\\d+")
                ? Regex.Replace(s, "(\"autoCompactWindow\"\\s*:\\s*)\\d+", "1" + win)
                : Regex.Replace(s, "^\\s*\\{", "{\n  \"autoCompactWindow\": " + win + ",");
            if (ns != s) { File.Copy(UserSettings, UserSettings + ".bak-launcher", true); File.WriteAllText(UserSettings, ns, new UTF8Encoding(false)); }
            compactWindow = win; compactItems = items; compactCustom = custom;
            UpdateCompactChip();
            return true;
        } catch (Exception ex) { status.Text = "압축 설정을 저장하지 못했습니다 · " + ex.Message; return false; }
    }
    void UpdateCompactChip() { if (txtCompact != null) txtCompact.Text = "압축 " + (compactWindow / 1000) + "k"; }
    public void OpenCompact() { ShowCompact(); }
    void ShowCompact() {
        if (compactPop != null && compactPop.IsOpen) { compactPop.IsOpen = false; return; }
        if ((DateTime.Now - compactClosedAt).TotalMilliseconds < 400) return;
        var sel = new List<string>(compactItems); int win = compactWindow;
        var root = new Border { Width = 400, CornerRadius = new CornerRadius(16), Background = B("#F21E1E24"), BorderBrush = (Brush)R("GlassEdge"), BorderThickness = new Thickness(1), Padding = new Thickness(18, 16, 18, 16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black } };
        var sp = new StackPanel(); root.Child = sp;
        sp.Children.Add(T("세션 압축 설정", 14, (Brush)R("Ink"), true));
        sp.Children.Add(new TextBlock { Text = "대화가 길어지면 Claude가 앞부분을 요약해 줄입니다. 언제 줄일지와, 줄인 뒤에도 꼭 남길 내용을 정합니다.", FontSize = 11.5, Foreground = (Brush)R("Faint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14) });
        // 압축 시점
        sp.Children.Add(T("자동 압축 시점 (컨텍스트 토큰)", 12, (Brush)R("Dim"), true));
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 14) };
        var box = new TextBox { Width = 70, Text = (win / 1000).ToString(), FontSize = 12.5, Foreground = (Brush)R("Ink"), Background = B("#22FFFFFF"), BorderBrush = (Brush)R("GlassEdge"), CaretBrush = Brushes.White, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        var presets = new List<Border>();
        Action paint = delegate { foreach (var p in presets) { bool on = (int)p.Tag == win; p.Background = on ? B("#33E58B66") : B("#14FFFFFF"); p.BorderBrush = on ? (Brush)R("AccentBrush") : B("#22FFFFFF"); } };
        foreach (int k in new[] { 300, 450, 600, 800 }) {
            var pb = new Border { Tag = k * 1000, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(11, 4, 11, 5), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, Child = T(k + "k", 12, (Brush)R("Ink")) };
            int kk = k;
            pb.MouseLeftButtonUp += delegate { win = kk * 1000; box.Text = kk.ToString(); paint(); };
            presets.Add(pb); row.Children.Add(pb);
        }
        box.TextChanged += delegate { int v; if (int.TryParse(box.Text.Trim(), out v)) { win = v * 1000; paint(); } };
        row.Children.Add(box); row.Children.Add(new TextBlock { Text = "k", FontSize = 12, Foreground = (Brush)R("Dim"), VerticalAlignment = VerticalAlignment.Center });
        paint();
        sp.Children.Add(row);
        // 남길 내용
        sp.Children.Add(T("압축 뒤 남길 내용", 12, (Brush)R("Dim"), true));
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 10) }; var draws = new List<Action>();
        foreach (var it in COMPACT_ITEMS) {
            string key = it[0];
            var rb = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 0, 0, 5), Cursor = Cursors.Hand };
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            var mark = new TextBlock { FontFamily = (FontFamily)R("Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            g.Children.Add(mark);
            var tsp = new StackPanel(); Grid.SetColumn(tsp, 1); g.Children.Add(tsp);
            tsp.Children.Add(T(it[1], 12.5, (Brush)R("Ink"), true));
            tsp.Children.Add(new TextBlock { Text = it[2], FontSize = 11, Foreground = (Brush)R("Faint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            rb.Child = g;
            Action draw = delegate { bool on = sel.Contains(key); mark.Text = on ? "\xE73A" : "\xE739"; mark.Foreground = on ? (Brush)R("AccentBrush") : (Brush)R("Faint"); rb.Background = on ? B("#14FFFFFF") : Brushes.Transparent; rb.BorderBrush = on ? B("#26FFFFFF") : B("#12FFFFFF"); };
            rb.MouseLeftButtonUp += delegate { if (sel.Contains(key)) sel.Remove(key); else sel.Add(key); draw(); };
            draw(); draws.Add(draw); list.Children.Add(rb);
        }
        sp.Children.Add(list);
        sp.Children.Add(T("더 남길 것 (직접 적기, 비워도 됨)", 12, (Brush)R("Dim"), true));
        var custom = new TextBox { Text = compactCustom, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 54, FontSize = 12, Foreground = (Brush)R("Ink"), Background = B("#22FFFFFF"), BorderBrush = (Brush)R("GlassEdge"), CaretBrush = Brushes.White, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 6, 0, 8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        sp.Children.Add(custom);
        var err = new TextBlock { FontSize = 11.5, Foreground = B("#E08A7C"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(err);
        sp.Children.Add(new TextBlock { Text = "주의: 압축 시점은 런처 밖의 Claude 세션까지 모두 바뀝니다(~/.claude/settings.json). 남길 내용은 모든 세션의 압축에 쓰입니다. 새로 여는 세션부터 적용됩니다.", FontSize = 11.5, Foreground = B("#DDB06A"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var bDef = new Button { Style = (Style)R("GlassBtn"), Content = "기본값" };
        var bCancel = new Button { Style = (Style)R("GlassBtn"), Content = "취소", Margin = new Thickness(8, 0, 0, 0) };
        var bOk = new Button { Style = (Style)R("AccentBtn"), Content = "저장", Margin = new Thickness(8, 0, 0, 0) };
        btns.Children.Add(bDef); btns.Children.Add(bCancel); btns.Children.Add(bOk);
        sp.Children.Add(btns);
        var pop = new System.Windows.Controls.Primitives.Popup { Child = root, PlacementTarget = btnCompact, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, VerticalOffset = -8, StaysOpen = false, AllowsTransparency = true };
        pop.Closed += delegate { compactClosedAt = DateTime.Now; if (compactPop == pop) compactPop = null; };
        bDef.Click += delegate { win = 450000; box.Text = "450"; sel.Clear(); sel.AddRange(new[] { "plan", "done", "now" }); custom.Text = ""; paint(); foreach (var d in draws) d(); };
        bCancel.Click += delegate { pop.IsOpen = false; };
        bOk.Click += delegate {
            int v;
            if (!int.TryParse(box.Text.Trim(), out v) || v < 100 || v > 1000) { err.Text = "압축 시점은 100k에서 1000k 사이의 숫자로 적어 주세요."; err.Visibility = Visibility.Visible; return; }
            if (sel.Count == 0 && string.IsNullOrWhiteSpace(custom.Text)) { err.Text = "남길 내용을 하나 이상 골라 주세요."; err.Visibility = Visibility.Visible; return; }
            var ordered = COMPACT_ITEMS.Select(i => i[0]).Where(k => sel.Contains(k)).ToList();
            if (SaveCompact(v * 1000, ordered, custom.Text)) { pop.IsOpen = false; status.Text = "압축 설정을 저장했습니다 · " + v + "k에서 자동 압축 · 남길 항목 " + ordered.Count + "개"; }
        };
        compactPop = pop; pop.IsOpen = true;
    }

    // ---------- 팀 구성 ----------
    void RenderTeam(TermTab t) {
        if (t == null) { panelBody.Children.Add(T("세션을 열면 보입니다", 12, (Brush)R("Faint"))); return; }
        var agents = (Get(t.Pane, "agents") as IEnumerable ?? new object[0]).Cast<object>().ToList();
        Section("팀 구성", agents.Count == 0 ? null : "에이전트 " + agents.Count + "명", true);
        if (agents.Count == 0) { panelBody.Children.Add(T(t.Pane == null ? "세션이 시작되면 보입니다" : "띄운 에이전트가 없습니다", 12, (Brush)R("Faint"))); return; }
        // 파티: 모든 에이전트를 도트 고양이로 한 줄에 세운다. 털은 모델 색, 목걸이는 추론 수준.
        double nowMs = NowMs();
        var stage = new Border { CornerRadius = new CornerRadius(12), Background = B("#10FFFFFF"), BorderBrush = B("#1AFFFFFF"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 10, 10, 8), Margin = new Thickness(0, 0, 0, 10) };
        var party = new WrapPanel();
        foreach (var a in agents.Take(24)) {
            string st0 = Str(a, "status");
            var pc = Pixel(Str(a, "model"), Str(a, "effort"), st0 == "running" || st0 == "pending", st0 == "completed" || st0 == "failed" || st0 == "killed", 30, nowMs);
            ((FrameworkElement)pc).ToolTip = (Str(a, "description") ?? "") + " · " + (Str(a, "model") == null ? "모델 확인 중" : ShortModel(Str(a, "model"), Str(a, "model"))) + " · " + (Str(a, "effort") == null || Str(a, "effort") == "unknown" ? "수준 확인 중" : EffortName(Str(a, "effort")));
            var holder = new Grid { Width = 34, Height = 36, Margin = new Thickness(0, 0, 2, 2) }; holder.Children.Add(pc);
            party.Children.Add(holder);
        }
        var stSp = new StackPanel(); stSp.Children.Add(party);
        stage.Child = stSp;
        panelBody.Children.Add(stage);
        // 모델별 인원
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var g in agents.GroupBy(a => Str(a, "model") == null ? "?" : ShortModel(Str(a, "model"), Str(a, "model")))) {
            var m = Str(g.First(), "model");
            var pill = new Border { CornerRadius = new CornerRadius(9), Background = B("#14FFFFFF"), BorderBrush = B("#1EFFFFFF"), BorderThickness = new Thickness(1), Padding = new Thickness(9, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(T("◆ " + (g.Key == "?" ? "모델 확인 중" : g.Key), 11.5, ModelBrush(m), true));
            sp.Children.Add(T("  " + g.Count() + "명", 11.5, (Brush)R("Dim")));
            pill.Child = sp; chips.Children.Add(pill);
        }
        panelBody.Children.Add(chips);
        // 묶음: Ultracode·워크플로로 띄운 팀, 그다음 직접 띄운 에이전트
        foreach (var grp in agents.GroupBy(a => Str(a, "workflow") ?? "").OrderBy(g => g.Key == "" ? 1 : 0)) {
            var head = new TextBlock { FontSize = 12, Margin = new Thickness(2, 6, 0, 8) };
            if (grp.Key == "") head.Inlines.Add(new Run("직접 띄운 에이전트") { Foreground = (Brush)R("Dim"), FontWeight = FontWeights.SemiBold });
            else {
                head.Inlines.Add(new Run("Ultracode 워크플로") { Foreground = Rainbow(false), FontWeight = FontWeights.SemiBold });
                head.Inlines.Add(new Run("  " + (grp.Key.Length > 11 ? grp.Key.Substring(0, 11) : grp.Key)) { Foreground = (Brush)R("Faint") });
            }
            panelBody.Children.Add(head);
            foreach (var a in grp) panelBody.Children.Add(TeamRow(a));
        }
    }
    FrameworkElement TeamRow(object a) {
        string stt = Str(a, "status"), model = Str(a, "model"), eff = Str(a, "effort");
        bool running = stt == "running" || stt == "pending", done = stt == "completed" || stt == "failed" || stt == "killed";
        Brush tint = ModelBrush(model);
        var card = new Border { CornerRadius = new CornerRadius(12), Background = B(done ? "#08FFFFFF" : "#12FFFFFF"), BorderBrush = B(running ? "#30FFFFFF" : "#14FFFFFF"), BorderThickness = new Thickness(1), Padding = new Thickness(12, 9, 10, 9), Margin = new Thickness(0, 0, 0, 7), Opacity = done ? 0.7 : 1 };
        var g = new Grid(); card.Child = g;
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var face = Pixel(model, eff, running, done, 36, NowMs()); face.VerticalAlignment = VerticalAlignment.Center;
        var fh = new Grid { Width = 40, Height = 40, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }; fh.Children.Add(face);
        g.Children.Add(fh);
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(sp, 1);
        var role = T(Str(a, "description") ?? "(역할 없음)", 12.5, (Brush)R("Ink"), true); role.TextTrimming = TextTrimming.CharacterEllipsis; sp.Children.Add(role);
        var r2 = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        r2.Inlines.Add(new Run("◆ " + (model == null ? "모델 확인 중" : ShortModel(model, model))) { Foreground = tint, FontWeight = FontWeights.SemiBold });
        string effText = eff == "unknown" || eff == null ? "수준 확인 중" : EffortName(eff);
        r2.Inlines.Add(new Run("  ·  " + effText) { Foreground = (Brush)R("Dim") });
        sp.Children.Add(r2);
        if (Str(a, "type") != null) card.ToolTip = "종류: " + Str(a, "type");
        g.Children.Add(sp);
        var skills = (Get(a, "skills") as IEnumerable ?? new object[0]).Cast<object>().Select(o => Convert.ToString(o)).Where(s => !string.IsNullOrEmpty(s)).ToList();
        var btn = new Button { Style = (Style)R("ChipBtn"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 5), IsEnabled = skills.Count > 0,
            Content = T(skills.Count == 0 ? "스킬 없음" : "스킬 " + skills.Count, 11.5, skills.Count == 0 ? (Brush)R("Faint") : (Brush)R("Ink")),
            ToolTip = skills.Count == 0 ? "이 에이전트가 쓴 스킬이 없습니다" : "이 에이전트가 쓴 스킬 보기" };
        string who = Str(a, "description") ?? "에이전트";
        btn.Click += delegate { ShowSkills(btn, who, skills); };
        Grid.SetColumn(btn, 2); g.Children.Add(btn);
        return card;
    }
    void ShowSkills(Button anchor, string who, List<string> skills) {
        var cm = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, VerticalOffset = 4 };
        cm.Items.Add(new MenuItem { Header = who + " · 쓴 스킬 " + skills.Count + "개", IsEnabled = false, Tag = "\xE8A5" });
        cm.Items.Add(new Separator { Background = B("#22FFFFFF"), Margin = new Thickness(6, 4, 6, 4), Height = 1 });
        foreach (var s in skills) cm.Items.Add(new MenuItem { Header = s, Tag = "\xE945", StaysOpenOnClick = true });
        cm.IsOpen = true;
    }

    // ---------- 플랜 ----------
    string PlanPath(TermTab t) {
        string sid = t == null ? null : (Str(t.St, "session_id") ?? Str(t.Pane, "session"));
        return sid == null ? null : System.IO.Path.Combine(BaseDir, "plans", Regex.Replace(sid, @"[^\w-]", "") + ".md");
    }
    long PlanStamp(TermTab t) {
        if (panelCat != "plan") return 0;
        var p = PlanPath(t);
        try { return p != null && File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks : 0; } catch { return 0; }
    }
    void RenderPlan(TermTab t) {
        if (t == null) { panelBody.Children.Add(T("세션을 열면 보입니다", 12, (Brush)R("Faint"))); return; }
        string path = PlanPath(t);
        string text = null;
        if (path != null && File.Exists(path)) {
            var m = File.GetLastWriteTimeUtc(path);
            if (planFor != path || m != planM) { try { using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sr = new StreamReader(fs, Encoding.UTF8)) planText = sr.ReadToEnd(); planFor = path; planM = m; } catch { } }
            if (planFor == path) text = planText;
        }
        if (string.IsNullOrWhiteSpace(text)) {
            Section("핵심 플랜", null, true);
            panelBody.Children.Add(new TextBlock { Text = "Claude가 여러 단계로 된 작업을 시작하면 여기에 플랜이 보입니다.\n사용자 요구와 단계별 진행이 계층으로 나옵니다.", FontSize = 12, Foreground = (Brush)R("Faint"), TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
            return;
        }
        string need = null;
        var steps = new List<Tuple<int, string, string, string, string>>(); // 깊이, 번호, 상태, 제목, 요약
        foreach (var raw in text.Replace("\r", "").Split('\n')) {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            var mn = Regex.Match(line, @"^\s*(?:[-*]\s*)?(?:요구|사용자 요구|핵심 플랜)\s*[:：]\s*(.+)$");
            if (mn.Success && need == null) { need = mn.Groups[1].Value.Trim(); continue; }
            var ms = Regex.Match(line, @"^(\s*)(?:[-*]\s*)?(\d+(?:\.\d+)*)\.?\s*(?:\[(완료|진행|대기|진행 중|끝)\]|\((완료|진행|대기|진행 중|끝)\))?\s*(.+)$");
            if (!ms.Success) continue;
            string num = ms.Groups[2].Value;
            int depth = num.Count(ch => ch == '.') + (ms.Groups[1].Value.Length >= 2 && !num.Contains(".") ? 1 : 0);
            string st = ms.Groups[3].Success ? ms.Groups[3].Value : ms.Groups[4].Success ? ms.Groups[4].Value : "대기";
            if (st == "끝") st = "완료"; if (st == "진행 중") st = "진행";
            string body = ms.Groups[5].Value.Trim(), title = body, sum = null;
            int dash = body.IndexOf(" — "); if (dash < 0) dash = body.IndexOf(" - ");
            if (dash > 0) { title = body.Substring(0, dash).Trim(); sum = body.Substring(dash + 3).Trim(); }
            steps.Add(Tuple.Create(depth, num, st, title, sum));
        }
        int top = steps.Count(s => s.Item1 == 0), fin = steps.Count(s => s.Item1 == 0 && s.Item3 == "완료");
        string ago = "";
        try { var mins = (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalMinutes; ago = mins < 1 ? "방금 갱신" : mins < 60 ? Math.Floor(mins) + "분 전 갱신" : File.GetLastWriteTime(path).ToString("M/d HH:mm") + " 갱신"; } catch { }
        Section("핵심 플랜", (top == 0 ? "" : "단계 " + fin + "/" + top + " · ") + ago, true);
        if (top > 0 && fin == top) panelBody.Children.Add(new TextBlock { Text = "이 플랜은 모두 끝났습니다. 새 요구로 여러 단계 작업을 시작하면 Claude가 새 플랜으로 바꿉니다.", FontSize = 11.5, Foreground = B("#DDB06A"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        if (need != null) {
            var card = new Border { CornerRadius = new CornerRadius(12), Background = B("#14FFFFFF"), BorderBrush = B("#22FFFFFF"), BorderThickness = new Thickness(1), Padding = new Thickness(13, 10, 13, 11), Margin = new Thickness(0, 0, 0, 12) };
            var sp = new StackPanel(); card.Child = sp;
            sp.Children.Add(T("사용자 요구", 11, (Brush)R("Faint")));
            sp.Children.Add(new TextBlock { Text = need, FontSize = 12.5, Foreground = (Brush)R("Ink"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), LineHeight = 19 });
            panelBody.Children.Add(card);
        }
        if (top > 0) { Meter("진행 (마친 단계)", fin * 100.0 / top, null, ModelBrush(Str(t.St, "model", "id"))); }
        foreach (var s in steps) panelBody.Children.Add(PlanRow(s.Item1, s.Item2, s.Item3, s.Item4, s.Item5));
    }
    FrameworkElement PlanRow(int depth, string num, string st, string title, string sum) {
        bool done = st == "완료", now = st == "진행";
        var g = new Grid { Margin = new Thickness(depth * 18, depth == 0 ? 8 : 4, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); g.ColumnDefinitions.Add(new ColumnDefinition());
        var mark = new TextBlock { FontFamily = (FontFamily)R("Icons"), FontSize = depth == 0 ? 12 : 10.5, Margin = new Thickness(0, 3, 0, 0),
            Text = done ? "\xE73E" : now ? "\xE768" : "\xE91F", Foreground = done ? B("#A8E6B8") : now ? (Brush)R("AccentBrush") : (Brush)R("Faint") };
        g.Children.Add(mark);
        var sp = new StackPanel(); Grid.SetColumn(sp, 1); g.Children.Add(sp);
        var tb = new TextBlock { FontSize = depth == 0 ? 12.5 : 12, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
        tb.Inlines.Add(new Run(num + ". ") { Foreground = (Brush)R("Faint") });
        var r = new Run(title) { Foreground = done ? (Brush)R("Faint") : now ? (Brush)R("Ink") : (Brush)R("Dim"), FontWeight = now || depth == 0 && !done ? FontWeights.SemiBold : FontWeights.Normal };
        if (done) r.TextDecorations = TextDecorations.Strikethrough;
        tb.Inlines.Add(r);
        tb.Inlines.Add(new Run(done ? "  완료" : now ? "  진행 중" : "") { Foreground = done ? B("#A8E6B8") : (Brush)R("AccentBrush"), FontSize = 11 });
        sp.Children.Add(tb);
        if (!string.IsNullOrEmpty(sum) && !done) sp.Children.Add(new TextBlock { Text = "└ " + sum, FontSize = 11.5, Foreground = (Brush)R("Dim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), LineHeight = 17 });
        return g;
    }

    // ---------- 울트라코드 ----------
    void ToggleUltra() {
        var t = activeTab;
        if (t == null) { launchUltra = !launchUltra; UpdateChips(); return; }
        if (t.Pty == null || t.Exited) return;
        if (t.UltraPending) { t.UltraPending = false; UpdateChips(); return; }
        t.Ultra = !t.Ultra; t.UltraSetAt = DateTime.Now;
        TypeLine(t, t.Ultra ? "/effort ultracode" : "/effort ultracode off");
        RememberUltra(t);
        status.Text = t.Ultra ? "Ultracode를 켭니다 (이 세션만)" : "Ultracode를 끕니다";
        UpdateChips();
        if (web != null) { web.Focus(); Post(new { t = "focus" }); }
    }

    // ---------- 새 세션에서 이어 하기 ----------
    // 지금 세션에 인계 기록을 답으로 쓰게 하고, 세션 기록(jsonl)에서 그 답을 읽어 파일로 남긴다.
    // 그다음 같은 폴더·모델·추론 수준으로 새 세션을 열어 그 파일을 읽게 하고, 이전 탭은 닫는다.
    // 도구를 쓰지 않으므로 권한 확인 창이 뜨지 않고, Jira나 .handoff 설정도 필요 없다.
    string HandoffPrompt() {
        var sb = new StringBuilder("[런처 인계] 이 세션을 닫고 새 세션에서 이어 갑니다. 도구는 쓰지 말고, 이 대화를 못 본 새 세션이 바로 이어 갈 수 있는 인계 기록을 한국어 마크다운으로 답하세요. 첫 줄은 <<<HANDOFF, 마지막 줄은 HANDOFF>>> 로 감쌉니다. 맨 위 제목(#)은 쓰지 말고, 아래 항목마다 같은 이름의 소제목(##)을 답니다.");
        foreach (var it in COMPACT_ITEMS) if (compactItems.Contains(it[0])) sb.Append(" ## " + it[1] + ": " + it[2]);
        if (!string.IsNullOrWhiteSpace(compactCustom)) sb.Append(" ## 따로 정한 것: " + compactCustom.Trim().Replace("\r", " ").Replace("\n", " "));
        sb.Append(" 현재 진행 작업은 비워 두지 말고, 플랜의 [진행] 단계나 마지막 사용자 요청에서 찾아 적습니다. CLAUDE.md 같은 지침 파일 내용은 새 세션도 자동으로 읽으므로 다시 적지 않습니다. 확인하지 못한 것은 확인 안 됨이라고 적습니다.");
        return sb.ToString();
    }
    const string HandoffPromptOld = "[런처 인계] 이 세션을 닫고 새 세션에서 이어 갑니다. 도구는 쓰지 말고, 이 대화를 못 본 새 세션이 바로 이어 갈 수 있는 인계 기록을 한국어 마크다운으로 답하세요. 첫 줄은 <<<HANDOFF, 마지막 줄은 HANDOFF>>> 로 감쌉니다. 담을 것: 1) 목표와 사용자 요구 2) 끝낸 일 3) 남은 일과 바로 다음 단계 4) 중요한 결정과 그 이유 5) 바꾼 파일(절대 경로) 6) 주의할 점과 사용자 선호. 확인하지 못한 것은 확인 안 됨이라고 적습니다.";
    void TypeLine(TermTab t, string text) {
        if (t == null || t.Pty == null || t.Exited) return;
        // 인자 없는 / 명령은 뒤에 공백을 붙인다. 붙이지 않으면 자동 완성 메뉴가 열린 채 Enter가 다른 명령(/compact → /autocompact)을 고를 수 있다.
        if (text.StartsWith("/") && text.IndexOf(' ') < 0) text += " ";
        // 글자와 Enter를 나눠 보낸다. 한 번에 보내면 붙여넣기로 여겨 Enter가 줄바꿈이 될 수 있다.
        t.Pty.Write(text);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += delegate { timer.Stop(); if (t.Pty != null) t.Pty.Write("\r"); };
        timer.Start();
    }
    void UpdateHandoffButton(TermTab t) {
        string tp = t == null ? null : Str(t.St, "transcript_path");
        bool ready = t != null && t.Pty != null && !t.Exited && !string.IsNullOrEmpty(tp);
        btnHandoff.IsEnabled = ready && !t.HandoffWaiting;
        txtHandoff.Text = t != null && t.HandoffWaiting ? "인계 기록을 쓰는 중…" : "새 세션에서 이어 진행하기";
        string note = null;
        if (t != null && t.HandoffWaiting) note = "Claude가 인계 기록을 쓰고 있습니다. 다 쓰면 이 탭을 닫고 새 탭에서 이어 갑니다.";
        else if (t != null && t.Exited) note = "끝난 세션입니다.";
        else if (t != null && !ready) note = "Claude가 준비되면 누를 수 있습니다.";
        else if (t != null) {
            double? ctx = Num(t.St, "context_window", "used_percentage");
            if (ctx != null && ctx.Value >= 70) note = "컨텍스트가 " + Math.Round(ctx.Value) + "% 찼습니다. 새 세션으로 이어 가면 비용이 줄어듭니다.";
        }
        handoffNote.Text = note ?? "";
        handoffNote.Visibility = note == null ? Visibility.Collapsed : Visibility.Visible;
    }
    public void Handoff() {
        var t = activeTab;
        if (t == null || t.Pty == null || t.Exited || t.HandoffWaiting) return;
        string tp = Str(t.St, "transcript_path");
        if (string.IsNullOrEmpty(tp) || !File.Exists(tp)) { status.Text = "세션 기록을 아직 찾지 못했습니다 · Claude가 준비된 뒤 다시 눌러 주세요"; return; }
        t.HandoffTranscript = tp;
        t.HandoffOffset = new FileInfo(tp).Length;
        t.HandoffWaiting = true; t.HandoffAt = DateTime.Now;
        TypeLine(t, HandoffPrompt());
        status.Text = "인계 기록을 쓰는 중 · " + t.Title;
        Log("handoff: prompt sent, transcript=" + tp + " offset=" + t.HandoffOffset);
        RenderPanel(true);
        if (web != null) { web.Focus(); Post(new { t = "focus" }); }
    }
    static string ReadHandoff(TermTab t) {
        var fi = new FileInfo(t.HandoffTranscript);
        if (!fi.Exists || fi.Length <= t.HandoffOffset) return null;
        string tail;
        using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            fs.Seek(t.HandoffOffset, SeekOrigin.Begin);
            var ms = new MemoryStream(); fs.CopyTo(ms);
            tail = Encoding.UTF8.GetString(ms.ToArray());
        }
        var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        foreach (var line in tail.Split('\n')) {
            if (!line.Contains("HANDOFF>>>") || !line.Contains("\"assistant\"")) continue;
            try {
                var o = ser.DeserializeObject(line);
                if (Str(o, "type") != "assistant") continue;
                var sb = new StringBuilder();
                foreach (var it in (Get(o, "message", "content") as IEnumerable ?? new object[0]).Cast<object>())
                    if (Str(it, "type") == "text") sb.Append(Str(it, "text"));
                string text = sb.ToString();
                int a = text.IndexOf("<<<HANDOFF"), b = text.LastIndexOf("HANDOFF>>>");
                if (a >= 0 && b > a) return text.Substring(a + 10, b - a - 10).Trim();
            } catch { }
        }
        return null;
    }
    void CheckHandoffs() {
        foreach (var t in tabs.ToList()) {
            if (!t.HandoffWaiting) continue;
            string text = null;
            try { text = ReadHandoff(t); } catch (Exception ex) { Log("handoff read error: " + ex.Message); }
            if (text != null) {
                t.HandoffWaiting = false;
                string id = Guid.NewGuid().ToString();
                string dir = System.IO.Path.Combine(BaseDir, "handoff");
                Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + id.Substring(0, 8) + ".md");
                string oldId = Str(t.St, "session_id") ?? t.SessionId;
                text = Regex.Replace(text, @"^\s*#\s+[^\n]*\n", "");
                File.WriteAllText(path, "# 인계 기록\n\n- 프로젝트 폴더: " + t.Dir + "\n- 이전 세션: " + oldId + "\n- 만든 때: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\n\n" + text + "\n", new UTF8Encoding(false));
                string model = Str(t.St, "model", "id") ?? launchModel;
                string effort = Str(t.St, "effort", "level") ?? launchEffort;
                string first = "이전 세션에서 넘겨받은 인계 기록입니다: " + path + " — 이 파일을 먼저 읽고, 남은 일과 바로 다음 단계를 짧게 정리해 보여 준 뒤 그 다음 단계를 이어서 진행하세요. 사용자 확인이 필요한 일(삭제, 배포, 에이전트 띄우기 등)이면 진행하기 전에 묻습니다.";
                string args = InnerArgs(null, id, model, effort) + " '" + first.Replace("'", "''") + "'";
                string name = System.IO.Path.GetFileName(t.Dir.TrimEnd('\\'));
                bool ultra = t.Ultra;
                Log("handoff: saved " + path + " (" + text.Length + " chars), opening " + id);
                OpenTab(name + " · 이어 진행", t.Dir, PsCmd(args), id);
                var nt = tabs.LastOrDefault(); if (nt != null && nt.SessionId == id) { nt.UltraPending = ultra; lastHandoffTab = nt; }
                CloseTab(t);
                status.Text = "인계를 마쳤습니다 · 이전 세션을 닫고 새 세션에서 이어 갑니다";
            } else if ((DateTime.Now - t.HandoffAt).TotalMinutes > 15) {
                t.HandoffWaiting = false;
                status.Text = "15분 동안 인계 기록이 나오지 않아 기다리기를 멈췄습니다";
                Log("handoff: timeout");
                RenderPanel(true);
            }
        }
    }

    // ---------- 자체 시험 (--selftest 폴더): 실제 세션을 열고 인계 버튼과 같은 길을 돌린다 ----------
    public static string LogPath;
    static string LastTerm(TermTab t) { try { var p = LogPath + ".term" + t.Id + ".txt"; return File.Exists(p) ? File.ReadAllText(p) : ""; } catch { return ""; } }
    TermTab lastHandoffTab;
    static void Log(string s) { if (LogPath != null) try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + s + "\r\n"); } catch { } }
    public static bool CompactTest, UltraTest, TeamTest;
    public void SelfTest(string dir) {
        launchModel = "haiku"; launchEffort = "low";
        string id = Guid.NewGuid().ToString();
        OpenTab("자체 시험", dir, PsCmd(InnerArgs(null, id) + (TeamTest ? " '시험입니다. 나는 사용자이고 워크플로 실행을 직접 요청하며 이 구성을 승인한다: 에이전트 1명, 모델 haiku, 추론 수준 low, 이유는 런처 팀 표시 시험. Workflow 도구로 스크립트를 하나 실행하라. 스크립트 본문은 agent 호출 한 번뿐이다: 프롬프트는 OK라고만 답하라, label은 시험 고양이, model은 haiku, effort는 low. 다른 일은 하지 말고 끝나면 끝났다고만 답하라.'" : CompactTest ? " '파일 정리 프로그램을 만든다고 하자. 1단계 폴더 읽기는 끝났고 2단계 이름 바꾸기를 하는 중이라고 기억해 두고, 알았다고만 답하세요'" : " '안녕 이라고 한 낱말로만 답하세요'")), id);
        var first = tabs.LastOrDefault();
        Log("selftest: opened " + id);
        int phase = 0; var t0 = DateTime.Now; var pt = DateTime.Now; bool trustSent = false;
        var tm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tm.Tick += delegate {
            double el = (DateTime.Now - t0).TotalSeconds, pel = (DateTime.Now - pt).TotalSeconds;
            if ((int)el % 15 == 0) Log("selftest: tick phase=" + phase + " st=" + (first.St != null) + " exited=" + first.Exited + " started=" + first.Started);
            if (phase == 0) {
                if (!trustSent && first.St == null && el > 10 && LastTerm(first).Contains("trust")) { trustSent = true; if (first.Pty != null) { first.Pty.Write("\x1b[B"); var tt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) }; tt.Tick += delegate { tt.Stop(); first.Pty.Write("\r"); }; tt.Start(); } Log("selftest: chose Yes on trust prompt"); }
                string tp = Str(first.St, "transcript_path");
                if (tp != null && File.Exists(tp) && File.ReadAllText(tp).Contains("\"assistant\"") && pel > 3) {
                    Log("selftest: first session answered, status ok, model=" + Str(first.St, "model", "id"));
                    if (CompactTest) { TypeLine(first, "/compact"); Log("selftest: sent /compact"); phase = 5; pt = DateTime.Now; }
                    else if (TeamTest) { Log("selftest: waiting for workflow agent"); phase = 7; pt = DateTime.Now; }
                    else if (UltraTest) { Activate(first); ToggleUltra(); Log("selftest: ultracode on"); phase = 6; pt = DateTime.Now; }
                    else { Activate(first); Handoff(); phase = 1; pt = DateTime.Now; }
                }
            } else if (phase == 1) {
                if (lastHandoffTab != null) {
                    Log("selftest: old tab closed=" + !tabs.Contains(first) + ", new tab=" + lastHandoffTab.Title + ", tabs=" + tabs.Count);
                    phase = 2; pt = DateTime.Now;
                } else if (!first.HandoffWaiting && pel > 3) { Log("selftest: FAIL handoff stopped"); phase = 9; }
            } else if (phase == 2) {
                var nt = lastHandoffTab;
                string tp = Str(nt.St, "transcript_path");
                if (tp != null && File.Exists(tp)) {
                    string all = File.ReadAllText(tp);
                    int n = Regex.Matches(all, "\"type\":\"assistant\"").Count;
                    if (all.Contains("\"end_turn\"") && pel > 5) { Log("selftest: new session answered (assistant lines " + n + "), transcript=" + tp); phase = 8; pt = DateTime.Now; }
                }
            } else if (phase == 5) {
                string tp = Str(first.St, "transcript_path");
                string all = tp != null && File.Exists(tp) ? File.ReadAllText(tp) : "";
                if (all.Contains("isCompactSummary")) {
                    var line = all.Split('\n').LastOrDefault(l => l.Contains("isCompactSummary")) ?? "";
                    Log("selftest: compacted. summary has 핵심 플랜=" + line.Contains("핵심 플랜") + " 완료된 작업=" + line.Contains("완료된 작업") + " 현재 진행=" + line.Contains("현재 진행"));
                    File.WriteAllText(LogPath + ".summary.txt", line);
                    phase = 8; pt = DateTime.Now;
                }
            } else if (phase == 6 && pel > 6 && pel < 7.5) { TypeLine(first, "좋아 라고만 답하세요");
            } else if (phase == 6 && pel > 16) {
                string term = Regex.Replace(LastTerm(first), @"\x1b\[[0-9;?]*[a-zA-Z]", "");
                Log("selftest: ultra label in terminal=" + term.Contains("ACTIVE") + ", pane ultracode=" + Str(first.Pane, "ultracode"));
                Shot(); phase = 8; pt = DateTime.Now;
            } else if (phase == 7) {
                var ags = (Get(first.Pane, "agents") as IEnumerable ?? new object[0]).Cast<object>().ToList();
                if ((int)pel % 10 == 0) Log("selftest: pane agents=" + ags.Count + " " + string.Join(" | ", ags.Select(x => Str(x, "description") + "/" + Str(x, "model") + "/" + Str(x, "effort") + "/" + Str(x, "status") + "/wf=" + Str(x, "workflow"))));
                if (ags.Any(x => Str(x, "workflow") != null && Str(x, "status") == "completed") || pel > 150) {
                    Log("selftest: final agents=" + string.Join(" | ", ags.Select(x => Str(x, "description") + "/" + Str(x, "model") + "/" + Str(x, "effort") + "/" + Str(x, "status") + "/wf=" + Str(x, "workflow"))));
                    SetCat("agents"); phase = 6; pt = DateTime.Now.AddSeconds(-14);
                }
            } else if (phase == 8 && pel > 3) { phase = 10; }
            if (el > 240 && phase < 8) { Log("selftest: FAIL timeout at phase " + phase); phase = 10; }
            if (phase >= 9) {
                tm.Stop(); Log("selftest: done");
                Shot();
                foreach (var t in tabs.ToList()) CloseTab(t);
                w.Close();
            }
        };
        tm.Start();
    }

    FrameworkElement AgentCard(object a, double now, string[] ACTIVE, string[] FIN) {
        string stt = Str(a, "status");
        bool done = FIN.Contains(stt), running = ACTIVE.Contains(stt);
        string model = Str(a, "model"), eff = Str(a, "effort");
        Brush tint = ModelBrush(model);
        var card = new Border { CornerRadius = new CornerRadius(12), Background = B(done ? "#08FFFFFF" : "#12FFFFFF"), BorderBrush = B(running ? "#30FFFFFF" : "#14FFFFFF"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 9, 12, 10), Margin = new Thickness(0, 0, 0, 8), Opacity = done ? 0.68 : 1 };
        if (Str(a, "type") != null) card.ToolTip = "종류: " + Str(a, "type");
        var outer = new Grid(); card.Child = outer;
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); outer.ColumnDefinitions.Add(new ColumnDefinition());
        // 왼쪽: 고양이(털 = 모델, 목걸이 = 추론 수준, 걸으면 작동 중, 회색이면 끝남)
        var cat = Pixel(model, eff, running, done, 34, now); cat.VerticalAlignment = VerticalAlignment.Top;
        var ch = new Grid { Width = 38, Height = 38, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top }; ch.Children.Add(cat);
        outer.Children.Add(ch);
        var sp = new StackPanel(); Grid.SetColumn(sp, 1); outer.Children.Add(sp);

        // 1줄: 맡은 일 + 상태·걸린 시간
        var r1 = new Grid();
        r1.ColumnDefinitions.Add(new ColumnDefinition()); r1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var desc = T(Str(a, "description") ?? "(이름 없음)", 12.5, (Brush)R("Ink"), true); desc.TextTrimming = TextTrimming.CharacterEllipsis; r1.Children.Add(desc);
        double? s0 = Num(a, "startedAt"), e0 = Num(a, "endedAt");
        string word = running ? "작동 중" : stt == "completed" ? "끝남" : stt == "failed" ? "실패" : stt == "killed" ? "멈춤" : "기다림";
        string took = s0 == null ? "" : " " + Dur((e0 ?? now) - s0.Value);
        var w = T(word + took, 11, running ? tint : stt == "failed" ? B("#E08A7C") : (Brush)R("Faint")); w.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(w, 1); r1.Children.Add(w);
        sp.Children.Add(r1);

        // 2줄: 모델 · 추론 수준 + 스킬
        string effText = eff == "unknown" || eff == null ? "수준 확인 중" : EffortName(eff);
        var r2 = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        r2.ColumnDefinitions.Add(new ColumnDefinition()); r2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mt = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        mt.Inlines.Add(new Run("◆ " + (model == null ? "모델 확인 중" : ShortModel(model, model))) { Foreground = tint, FontWeight = FontWeights.SemiBold });
        mt.Inlines.Add(new Run("  ·  " + effText) { Foreground = (Brush)R("Dim") });
        r2.Children.Add(mt);
        var skills = (Get(a, "skills") as IEnumerable ?? new object[0]).Cast<object>().Select(o => Convert.ToString(o)).Where(s => !string.IsNullOrEmpty(s)).ToList();
        if (skills.Count > 0) {
            var btn = new Button { Style = (Style)R("ChipBtn"), Height = 22, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Content = T("스킬 " + skills.Count, 10.5, (Brush)R("Ink")), ToolTip = "이 에이전트가 쓴 스킬 보기" };
            string who = Str(a, "description") ?? "에이전트";
            btn.Click += delegate { ShowSkills(btn, who, skills); };
            Grid.SetColumn(btn, 1); r2.Children.Add(btn);
        }
        sp.Children.Add(r2);

        // 3줄: 컨텍스트 게이지 (크기를 아는 에이전트만)
        double tok = Num(a, "tokens") ?? 0, size = Num(a, "size") ?? 0, outT = Num(a, "out") ?? 0;
        if (size > 0) {
            double pct = Math.Min(100, tok * 100 / size);
            var r3 = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            r3.ColumnDefinitions.Add(new ColumnDefinition()); r3.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var bar = Bar(pct, Level(pct, tint), 4); bar.Margin = new Thickness(0, 0, 10, 0); bar.VerticalAlignment = VerticalAlignment.Center;
            r3.Children.Add(bar);
            var nums = T(Math.Round(pct) + "%  " + KTok(tok) + "/" + KTok(size) + (outT > 0 ? " · 출력 " + KTok(outT) : ""), 11, (Brush)R("Dim"));
            Grid.SetColumn(nums, 1); r3.Children.Add(nums);
            sp.Children.Add(r3);
        } else if (outT > 0) { var o2 = T("출력 " + KTok(outT), 11, (Brush)R("Dim")); o2.Margin = new Thickness(0, 4, 0, 0); sp.Children.Add(o2); }

        // 4줄: 지금 하는 일 (작동 중일 때만)
        string doing = Str(a, "doing");
        if (!string.IsNullOrEmpty(doing) && !done) {
            var d = T("└ " + doing, 11, (Brush)R("Dim")); d.Margin = new Thickness(0, 4, 0, 0); d.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(d);
        }
        return card;
    }

    public void DemoData() {
        var t = activeTab; if (t == null) return;
        double now = NowMs(), nowS = now / 1000;
        Directory.CreateDirectory(StatusPath(""));
        File.WriteAllText(StatusPath(t.Key + ".json"), json.Serialize(new Dictionary<string, object> {
            { "model", new { id = "claude-opus-5-5", display_name = "Opus 5.5" } }, { "effort", new { level = "medium" } },
            { "context_window", new { used_percentage = 22, total_input_tokens = 223000, context_window_size = 1000000 } },
            { "rate_limits", new { five_hour = new { used_percentage = 6, resets_at = nowS + 3600 * 3 }, seven_day = new { used_percentage = 55, resets_at = nowS + 86400 * 2 } } },
            { "cost", new { total_cost_usd = 5.6 } },
            { "prompt_cache", new { caching_observed = true, hit_ratio = 0.97, warm = true, expires_at = nowS + 59 * 60, misses = 0 } } }));
        Directory.CreateDirectory(System.IO.Path.Combine(BaseDir, "plans"));
        File.WriteAllText(System.IO.Path.Combine(BaseDir, "plans", "demo-session.md"), "요구: 런처 오른쪽 패널에 에이전트·팀 구성·플랜 분류를 넣고, Ultracode가 켜지면 터미널 테두리를 무지개빛으로 빛나게 한다.\n1. [완료] 모드가 팀 정보 내보내기\n2. [진행] 런처 화면 만들기 — 팀 구성 도트 캐릭터를 그리는 중\n   2.1 [완료] 분류 탭\n   2.2 [진행] 팀 구성\n   2.3 [대기] 플랜\n3. [대기] 실제 세션에서 확인\n", new UTF8Encoding(false));
        t.Ultra = true;
        File.WriteAllText(StatusPath(t.Key + "-pane.json"), json.Serialize(new {
            model = "claude-opus-5-5", ultracode = true, session = "demo-session",
            plan = new { name = "docs/현황.md", title = "런처 대시보드 만들기", last = "오른쪽 패널에 사용량과 에이전트 게이지를 넣는 중" },
            agents = new object[] {
                new { id = "a1", description = "세션 목록 읽기 검토", type = "Explore", status = "running", model = "claude-sonnet-5-5", effort = "medium", tokens = 48000, size = 1000000, @out = 3100, startedAt = now - 83000, endedAt = (object)null, doing = "Read Program.cs", workflow = "wf_7f3a91c2d4", skills = new[] { "code-review", "simplify" } },
                new { id = "a4", description = "UI 검수", type = "general-purpose", status = "running", model = "claude-opus-5-5", effort = "high", tokens = 62000, size = 1000000, @out = 4100, startedAt = now - 50000, endedAt = (object)null, doing = "screenshot", workflow = "wf_7f3a91c2d4", skills = new[] { "ui-readability-pass" } },
                new { id = "a2", description = "아이콘 크기별 점검", type = "general-purpose", status = "running", model = "claude-haiku-5-5", effort = "low", tokens = 151000, size = 1000000, @out = 9800, startedAt = now - 241000, endedAt = (object)null, doing = "Bash ls assets" },
                new { id = "a3", description = "터미널 한글 폭 조사", type = "general-purpose", status = "completed", model = "claude-opus-5-5", effort = "high", tokens = 92000, size = 1000000, @out = 12000, startedAt = now - 600000, endedAt = now - 300000, doing = "" } } }));
    }

    void OpenSelected() { var s = CurSess(); if (s != null) OpenTab(s.Title, s.Cwd, PsCmd(InnerArgs(s.Id, null)), s.Id); }
    void NewSession() { var p = CurProj(); if (p == null) return; string id = Guid.NewGuid().ToString(); OpenTab(p.Display + " · 새 세션", p.Dir, PsCmd(InnerArgs(null, id)), id); }
    public void DemoNewProj() { ShowNewProj(true); newName.Text = "잘못된/이름"; CreateProject(); }
    public void Demo() {
        OpenTab("시험 터미널", Scanner.Root, "powershell.exe -NoLogo -NoProfile -Command \"Get-ChildItem | Format-Table Name, LastWriteTime; Write-Host '한글 출력 시험 ✻ 색깔' -ForegroundColor Yellow; Start-Sleep 30\"", null);
    }
}

static class App {
    // 런처 안 Claude 세션에 붙이는 설정 파일을 이 PC의 런처 폴더 경로로 매번 새로 쓴다.
    static void WriteClaudeSettings() {
        try {
            string b = Launcher.BaseDir.Replace('\\', '/');
            string perm = b.Length > 1 && b[1] == ':' ? "//" + char.ToLowerInvariant(b[0]) + b.Substring(2) : b;
            var o = new Dictionary<string, object> {
                { "statusLine", new Dictionary<string, object> { { "type", "command" }, { "command", "node \"" + b + "/status-tee.js\"" }, { "refreshInterval", 2 } } },
                { "permissions", new Dictionary<string, object> { { "allow", new[] { "Edit(" + perm + "/plans/**)" } } } }
            };
            string path = System.IO.Path.Combine(Launcher.BaseDir, "launcher-settings.json");
            string text = new JavaScriptSerializer().Serialize(o);
            if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != text) File.WriteAllText(path, text, new UTF8Encoding(false));
        } catch { }
    }
    static object Load(string name) {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) return XamlReader.Load(s);
    }
    [STAThread]
    static void Main(string[] args) {
        string shot = args.Length >= 2 && args[0] == "--shot" ? args[1] : null;
        bool demo = args.Contains("--demo") || args.Contains("--np") || args.Contains("--selftest");
        bool np = args.Contains("--np");
        int sti = Array.IndexOf(args, "--selftest");
        string selftest = sti >= 0 && sti + 1 < args.Length ? args[sti + 1] : null;
        Launcher.CompactTest = args.Contains("--compact-test"); Launcher.UltraTest = args.Contains("--ultra-test"); Launcher.TeamTest = args.Contains("--team-test");
        if (shot != null) Launcher.LogPath = shot + ".log.txt";
        // Claude 세션 안에서 런처를 띄웠을 때 그 세션의 표시를 물려주지 않는다. 물려주면 새 세션이 하위 세션으로 여겨져 기록 저장·상태줄이 꺼진다.
        foreach (var k in new[] { "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_SESSION_ID", "CLAUDECODE", "CLAUDE_PID", "CLAUDE_EFFORT", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_CHILD_NOTES_DIR", "CLAUDE_CODE_SESSION_ATTENDED", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH", "CLAUDE_LAUNCHER_TAB" })
            Environment.SetEnvironmentVariable(k, null);
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        Environment.SetEnvironmentVariable("CLAUDE_LAUNCHER", "1");
        Environment.SetEnvironmentVariable("CLAUDE_LAUNCHER_DIR", Launcher.BaseDir);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { File.AppendAllText(System.IO.Path.Combine(Launcher.BaseDir, "crash.txt"), DateTime.Now + " " + e.ExceptionObject + "\r\n"); } catch { } };
        if (shot == null) PendingMove.Run();
        try { Directory.CreateDirectory(Scanner.Root); } catch { }
        WriteClaudeSettings();
        var app = new Application();
        app.DispatcherUnhandledException += (s, e) => {
            if (shot != null) File.AppendAllText(shot + ".err.txt", e.Exception.ToString() + "\n");
            else MessageBox.Show(e.Exception.ToString(), "Claude 런처 오류");
            e.Handled = true;
        };
        try {
            app.Resources = (ResourceDictionary)Load("res.xaml");
            var w = (Window)Load("main.xaml");
            var l = new Launcher(w, shot, demo);
            int pji = Array.IndexOf(args, "--proj"); if (pji >= 0 && pji + 1 < args.Length) l.PreSelect(args[pji + 1]);
            int nwi = Array.IndexOf(args, "--width"); if (nwi >= 0 && nwi + 1 < args.Length) w.Width = double.Parse(args[nwi + 1]);
            w.Loaded += delegate {
                l.Rescan(); l.InitWeb();
                if (selftest != null) {
                    var t0 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
                    t0.Tick += delegate { t0.Stop(); l.SelfTest(selftest); };
                    t0.Start();
                } else if (demo && !args.Contains("--selftest")) {
                    var t1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                    t1.Tick += delegate { t1.Stop(); if (np) l.DemoNewProj(); else { l.Demo(); l.DemoData(); int ci = Array.IndexOf(args, "--cat"); if (ci >= 0) l.SetCat(args[ci + 1]); if (args.Contains("--compact")) l.OpenCompact(); } };
                    t1.Start();
                    var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(6000) };
                    t2.Tick += delegate { t2.Stop(); l.Shot(); w.Close(); };
                    t2.Start();
                }
            };
            app.Run(w);
        } catch (Exception ex) {
            if (shot != null) File.WriteAllText(shot + ".err.txt", ex.ToString());
            else MessageBox.Show(ex.ToString(), "Claude 런처 오류");
        }
    }
}


// 런처를 옮긴 뒤 처음 켤 때 한 번 하는 일: 세션 기록을 새 프로젝트로 옮기고 예전 프로그램 폴더를 지운다.
// 예전 런처가 아직 켜져 있으면(세션이 기록을 쓰는 중일 수 있으면) 다음에 켤 때 다시 한다.
static class PendingMove {
    public static void Run() {
        string file = Path.Combine(Launcher.BaseDir, "pending-move.json");
        if (!File.Exists(file)) return;
        var log = new StringBuilder();
        try {
            var ser = new JavaScriptSerializer();
            var o = ser.DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
            string oldDir = Convert.ToString(o["oldDir"]);
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("ClaudeLauncher")) {
                if (p.Id == me) continue;
                string path = null; try { path = p.MainModule.FileName; } catch { }
                if (path == null || path.StartsWith(oldDir, StringComparison.OrdinalIgnoreCase)) {
                    MessageBox.Show("예전 위치의 Claude 런처가 아직 켜져 있어 세션 기록을 옮기지 못했습니다.\n예전 런처를 닫은 뒤 이 런처를 다시 켜 주세요.", "Claude 런처", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            bool allOk = true;
            foreach (var s in ((object[])o["sessions"]).Cast<Dictionary<string, object>>()) {
                string id = Convert.ToString(s["id"]), from = Convert.ToString(s["fromDir"]), to = Convert.ToString(s["toDir"]);
                string oldCwd = Convert.ToString(s["oldCwd"]), newCwd = Convert.ToString(s["newCwd"]);
                string src = Path.Combine(from, id + ".jsonl"), dst = Path.Combine(to, id + ".jsonl");
                if (!File.Exists(src)) { log.AppendLine("없음(이미 옮김?): " + src); continue; }
                try {
                    Directory.CreateDirectory(to);
                    // 기록 안의 작업 폴더(cwd)를 새 폴더로 바꾼다. 정확히 그 폴더인 것만 바꾸고 하위 폴더는 건드리지 않는다.
                    string oldJ = "\"cwd\":\"" + oldCwd.Replace("\\", "\\\\") + "\"", newJ = "\"cwd\":\"" + newCwd.Replace("\\", "\\\\") + "\"";
                    string text = File.ReadAllText(src, Encoding.UTF8).Replace(oldJ, newJ);
                    File.WriteAllText(dst, text, new UTF8Encoding(false));
                    string srcDir = Path.Combine(from, id), dstDir = Path.Combine(to, id);
                    if (Directory.Exists(srcDir) && !Directory.Exists(dstDir)) Directory.Move(srcDir, dstDir);
                    File.Delete(src);
                    log.AppendLine("옮김: " + id + " → " + to);
                } catch (Exception ex) { allOk = false; log.AppendLine("실패: " + id + " · " + ex.Message); }
            }
            if (allOk && Directory.Exists(oldDir)) {
                try { Directory.Delete(oldDir, true); log.AppendLine("예전 폴더 지움: " + oldDir); }
                catch (Exception ex) { log.AppendLine("예전 폴더 일부를 지우지 못함: " + ex.Message); }
            }
            File.WriteAllText(Path.Combine(Launcher.BaseDir, "pending-move.log.txt"), log.ToString(), new UTF8Encoding(false));
            if (allOk) File.Move(file, Path.Combine(Launcher.BaseDir, "pending-move.done.json"));
        } catch (Exception ex) {
            try { File.WriteAllText(Path.Combine(Launcher.BaseDir, "pending-move.log.txt"), log + "\n오류: " + ex, new UTF8Encoding(false)); } catch { }
        }
    }
}
