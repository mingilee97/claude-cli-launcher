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


// 플랜 칸 고양이(사용자 고양이처럼 노르웨이 숲 장모 갈색 줄무늬, 흰 가슴·배·발, 목 갈기·귀 끝 털, 날씬하게): 일하는 동안은 안전모 쓰고 곡괭이질, 쉬는 동안은 방석 위에서 몸을 말고 낮잠.
// 뉴테이크 이미지 생성으로 뽑은 캐릭터 시트(위 줄 일하기 4칸, 아래 줄 자기 4칸, 칸마다 180x120)를 잘라 쓴다.
static class CatArt {
    const int FW = 180, FH = 120;
    const string Sheet = "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";
    static BitmapSource sheet;
    static BitmapSource Load() {
        if (sheet != null) return sheet;
        var bi = new BitmapImage();
        bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(Convert.FromBase64String(Sheet)); bi.EndInit(); bi.Freeze();
        return sheet = bi;
    }
    static BitmapSource[] Row(int r) {
        var s = Load(); var a = new BitmapSource[4];
        for (int i = 0; i < 4; i++) { var c = new CroppedBitmap(s, new Int32Rect(i * FW, r * FH, FW, FH)); c.Freeze(); a[i] = c; }
        return a;
    }
    public static BitmapSource[] Work() { return Row(0); }
    public static BitmapSource[] Rest() { return Row(1); }
}

class TermTab {
    public int Id; public string Title, Dir, Cmd, SessionId;
    public bool Started, Exited; public Pty Pty; public string Key;
    public Dictionary<string, object> St, Pane; public DateTime StM, PaneM;
    public string PendModel, PendEffort; public DateTime PendAt;
    public bool HandoffWaiting; public string HandoffTranscript; public long HandoffOffset; public DateTime HandoffAt;
    public bool Ultra, UltraPending; public DateTime UltraSetAt; public long UltraScan = -1; public bool UltraFlag;
    public readonly StringBuilder Buf = new StringBuilder(); public bool FlushPending; public DateTime LastOut;
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
        InitCat();
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
            tab.LastOut = DateTime.UtcNow;
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
        try { File.Delete(StatusPath(t.Key + "-busy")); } catch { }
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


    // ---------- 플랜 칸 고양이 ----------
    Image catImg; TextBlock catText; BitmapSource[] catWork, catRest; DispatcherTimer catTimer; int catFrame, catPoll = 99, catSlow; bool catBusy, catShown;
    void InitCat() {
        catImg = F<Image>("CatImg"); catText = F<TextBlock>("CatText");
        if (catImg == null) return;
        catWork = CatArt.Work(); catRest = CatArt.Rest();
        catImg.Source = catRest[0];
        catTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
        catTimer.Tick += delegate { CatTick(); };
        catTimer.Start();
    }
    // 훅이 남긴 표시(Claude가 입력을 받으면 생기고, 답을 마치면 지워짐)가 있고 터미널 출력이 30초 안에 있었으면 작업 중.
    bool TabBusy(TermTab t) {
        if (t == null || t.Exited || t.Pty == null || t.Key == null) return false;
        if ((DateTime.UtcNow - t.LastOut).TotalSeconds > 30) return false;
        try { return File.Exists(StatusPath(t.Key + "-busy")); } catch { return false; }
    }
    void CatTick() {
        if (panel == null || panel.Visibility != Visibility.Visible) return;
        if (++catPoll >= 5) {
            catPoll = 0; bool b = TabBusy(activeTab);
            if (b != catBusy || !catShown) {
                catBusy = b; catShown = true; catFrame = -1; catSlow = 0;
                catText.Text = b ? "열심히 작업 중…" : "쉬는 중";
                catText.Foreground = b ? B("#F2C94C") : (Brush)R("Faint");
                catImg.ToolTip = b ? "Claude가 일하고 있습니다" : "Claude가 답을 마치고 쉬고 있습니다";
            }
        }
        if (!catBusy && (catSlow++ % 4) != 0) return;
        var a = catBusy ? catWork : catRest;
        catFrame = (catFrame + 1) % a.Length; catImg.Source = a[catFrame];
    }

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
            var holder = new Grid { Width = 52, Height = 34, Margin = new Thickness(0, 0, 2, 2) }; holder.Children.Add(pc);
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
    // 40×23 칸. 사용자가 준 레퍼런스(엎드린 흰 고양이)를 그대로 옮기고 그늘 색만 모델 색으로 바꾼다.
    // 일하는 중이면 두 앞발이 각자 제자리에서 번갈아 오르내린다: 올린 발은 앞으로 내밀어 젤리가 보이고(원본 그림의 올린 발 모양), 내린 발은 몸 아래로 늘어져 젤리가 안 보인다(원본의 늘어진 발 모양). 1초마다 바뀐다.
    // o 테두리, w 흰 털, b 옅은 그늘, B 짙은 그늘, p 젤리.
    static readonly string[] SPRITE = { "......oo...........oo...................", ".....owwo.........owwo..................", ".....owwo.........owwo..................", ".....owwwo.ooooooowwwo..................", ".....owpooowwwwwwooowo..................", ".....owowwwwwwwwwwwwoo.........oooo.....", ".....oowwwwwwwwwwwwwwo........oBBBBo....", ".....owwwwwwwwwwwwwwwwo.......obbbbo....", ".....owwwwwwwwwwwwwwwwo.......obbbbBo...", ".....ooowowwwwwwwowwwwo........oobbBo...", "....owwwoowwwwwwwowwwwooooooo...obbBo...", "...owpwpwowwwpwwwwwpwwowwwwwwoooobbBo...", "...owwwwwowwowowwwwwwwowwwwwwwbboobBo...", "...opwwwpowwwwwwwwwwwowwwwwwwwbbbboBo...", "...owpppwobbbbbbbbbbowwwwooooobbbbboo...", "...owwpwwoobbbbbbooowwwwowwwwbobbbboo...", "....owwwowwoooooowwwwwwwowwwwbobbbboo...", ".....ooobbbbbbbbbbbbbbbbowwwwbobbbbo....", "........obbbbbbbbbbbbbbbowwwwbobbbo.....", ".........ooBBBBBBBBBBBBBowwwwboBoo......", "...........oooBBBBBBBBBBoBBBBBoo........", "..............ooooooooooooBBBo..........", "..........................ooo..........." };
    static readonly string[] SPRITE_B = { "......oo...........oo...................", ".....owwo.........owwo..................", ".....owwo.........owwo..................", ".....owwwo.ooooooowwwo..................", ".....owpooowwwwwwooowo..................", ".....owowwwwwwwwwwwwoo.........oooo.....", ".....oowwwwwwwwwwwwwwo........oBBBBo....", ".....owwwwwwwwwwwwwwwwo.......obbbbo....", ".....owwwwwwwwwwwwwwwwo.......obbbbBo...", ".....owwwowwwwwwwowwwwo...ooo..oobbBo...", ".....owwwowwwwwwwowwwwoooowwwo..obbBo...", ".....owpwwwwwpwwwwwpwwowowpwpwooobbBo...", ".....owwwwwwowowwwwwwwowowwwwwoboobBo...", "......owwwwwwwwwwwwwwowwopwwwpobbboBo...", "....ooooobbbbbbbbbbbowwwowpppwobbbboo...", "...owwwwboobbbbbbooowwwwowwpwwobbbboo...", "...owwwwbowoooooowwwwwwwwowwwobbbbboo...", "...owwwwbobbbbbbbbbbbbbbbbooobbbbbbo....", "...owwwwbobbbbbbbbbbbbbbbbbbbbbbbbo.....", "...owwwwbooBBBBBBBBBBBBBBBBBBBBBoo......", "...oBBBBBo.oooBBBBBBBBBBBBBBBooo........", "....oBBBo.....ooooooooooooooo...........", ".....ooo................................" };
    static string EffortHat(string eff) {
        switch (eff) { case "medium": return "#A9AEB6"; case "high": return "#E58B66"; case "xhigh": return "#FFD95C"; case "max": return "#FF6B8B"; default: return null; }
    }
    static Color Hex(string h) { return (Color)ColorConverter.ConvertFromString(h); }
    FrameworkElement Pixel(string model, string eff, bool running, bool done, double size, double now) {
        var bc = ModelBrush(model) as SolidColorBrush;
        Color body = bc != null ? bc.Color : Hex("#B0B0B8");
        string hat = EffortHat(eff);
        long ph = running ? ((long)(now / 1000)) % 2 : 0;
        var spr = ph == 1 ? SPRITE_B : SPRITE; const int SW = 40, SH = 23;
        Func<Color, double, Color> mix = (c, k) => Color.FromRgb((byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));
        var px = new byte[SW * SH * 4];
        for (int y = 0; y < SH; y++) {
            for (int x = 0; x < SW; x++) {
                char ch = spr[y][x]; Color col; bool on = true;
                switch (ch) {
                    case 'o': col = Hex("#16161C"); break;
                    case 'w': col = Colors.White; break;
                    case 'b': col = mix(body, 0.55); break;
                    case 'B': col = mix(body, 0.2); break;
                    case 'p': col = Hex("#F28FA2"); break;
                    default: on = false; col = Colors.Transparent; break;
                }
                if (on && done) { byte gr = (byte)((col.R * 30 + col.G * 59 + col.B * 11) / 100); col = Color.FromRgb(gr, gr, gr); }
                int i = (y * SW + x) * 4;
                px[i] = col.B; px[i + 1] = col.G; px[i + 2] = col.R; px[i + 3] = on ? (byte)255 : (byte)0;
            }
        }
        var bmp = BitmapSource.Create(SW, SH, 96, 96, PixelFormats.Bgra32, null, px, SW * 4);
        var img = new Image { Source = bmp, Width = size * SW / SH, Height = size, Opacity = done ? 0.55 : 1, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        // 일하는 중이면 한 칸씩 위아래로 뛴다.
        img.Margin = new Thickness(0, size / 12, 0, 0);
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
            var holder = new Grid { Width = 55, Height = 36, Margin = new Thickness(0, 0, 2, 2) }; holder.Children.Add(pc);
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
        var fh = new Grid { Width = 64, Height = 40, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }; fh.Children.Add(face);
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
        // 왼쪽: 고양이(그늘 색 = 모델, 다리를 흔들면 작동 중, 회색이면 끝남)
        var cat = Pixel(model, eff, running, done, 34, now); cat.VerticalAlignment = VerticalAlignment.Top;
        var ch = new Grid { Width = 61, Height = 38, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top }; ch.Children.Add(cat);
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
        if (Environment.GetCommandLineArgs().Contains("--busy")) File.WriteAllText(StatusPath(t.Key + "-busy"), "1");
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
                { "permissions", new Dictionary<string, object> { { "allow", new[] { "Edit(" + perm + "/plans/**)" } } } },
                { "hooks", new Dictionary<string, object> { { "UserPromptSubmit", new[] { new Dictionary<string, object> { { "hooks", new[] { new Dictionary<string, object> { { "type", "command" }, { "command", "node \"" + b + "/status-tee.js\" busy" } } } } } } }, { "Stop", new[] { new Dictionary<string, object> { { "hooks", new[] { new Dictionary<string, object> { { "type", "command" }, { "command", "node \"" + b + "/status-tee.js\" idle" } } } } } } } } }
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
