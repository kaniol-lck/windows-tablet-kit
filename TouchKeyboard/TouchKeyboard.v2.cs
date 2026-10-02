// TouchKeyboard.cs v2  —— 轻量触控屏幕键盘（自用增强版）
// 新增：① 自动呼出（Caret 感知） ② 拖动后自动吸附屏幕边缘 ③ 锁定贴底（全屏宽） ④ 外部文件驱动的可修改布局 + 托盘
// 兼容：C# 5 / .NET Framework 4.x（用 csc 或 Add-Type -OutputAssembly 编译）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

public class TouchKeyboard : Form
{
    // ============================ Win32 ============================
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] static extern uint GetWindowPid(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO gti);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScan(char ch);

    // ---- 全局鼠标点击探测：用于“手动关闭后，等用户再点一次输入框才恢复自动弹出” ----
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    const int WH_MOUSE_LL = 14, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;

    // ---- AppBar：贴底时让其他窗口自动让位（与系统触摸键盘同样机制） ----
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    // .NET 的 Screen 类会缓存屏幕信息，旋转后不刷新；这里永远向系统实时查询
    static Rectangle ScreenBounds() { return new Rectangle(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN)); }
    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SPI_SETWORKAREA = 0x002F, SPI_GETWORKAREA = 0x0030, SPIF_SENDCHANGE = 0x02, WM_SETTINGCHANGE = 0x001A;
    static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_GETTASKBARPOS = 5;
    const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
    const int WM_APPBAR = 0x8000 + 200;
    bool appbarRegistered = false;
    bool dockClosed = false;      // 贴底状态下用户已关闭键盘：占位已注销，未重新显示前不得再登记
    int dockHeight = 0;          // 贴底时的目标高度（避免被中途的 Size 变化影响）
    Timer appbarFix = null;      // 外壳会在登记后把我们叠到陈旧占位上，用一个短定时器复 assert 位置
    Rectangle appbarTarget = Rectangle.Empty;
    int appbarFixTries = 0;
    bool appbarPendingShow = false;   // 贴底期间先隐藏，摆稳后再出现（避免"先闪到上方再跳下来"）
    bool settling = false;

    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_EXTENDEDKEY = 1;
    const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
    const int WM_EXITSIZEMOVE = 0x0232, WM_NCHITTEST = 0x0084;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;   // 不出现在任务栏 / Alt+Tab / 任务视图
    const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    const int GUI_CARETBLINKING = 0x00000001;

    static readonly int InputSize = Marshal.SizeOf(typeof(INPUT));

    static void SendVk(ushort vk, bool up, bool ext)
    {
        INPUT[] a = new INPUT[1];
        a[0].type = INPUT_KEYBOARD;
        a[0].u.ki.wVk = vk;
        a[0].u.ki.wScan = 0;
        a[0].u.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0u) | (ext ? KEYEVENTF_EXTENDEDKEY : 0u);
        a[0].u.ki.time = 0;
        a[0].u.ki.dwExtraInfo = IntPtr.Zero;
        SendInput(1, a, InputSize);
    }

    static void Combo(ushort vk, bool shift, bool ctrl, bool alt, bool win, bool ext)
    {
        if (win) SendVk(0x5B, false, true);
        if (ctrl) SendVk(0x11, false, false);
        if (alt) SendVk(0x12, false, false);
        if (shift) SendVk(0x10, false, false);
        System.Threading.Thread.Sleep(6);
        SendVk(vk, false, ext);
        System.Threading.Thread.Sleep(6);
        SendVk(vk, true, ext);
        if (shift) SendVk(0x10, true, false);
        if (alt) SendVk(0x12, true, false);
        if (ctrl) SendVk(0x11, true, false);
        if (win) SendVk(0x5B, true, true);
    }

    // ============================ 布局模型 ============================
    class KeySpec
    {
        public string Text = ""; public ushort Vk = 0; public float W = 1f;
        public bool NeedShift = false, Extended = false, IsClose = false;
        public string Latch = null;   // shift / ctrl / alt / win
    }
    class KbLayout
    {
        public string Name = "";
        public List<List<KeySpec>> Rows = new List<List<KeySpec>>();
    }

    readonly List<KbLayout> layouts = new List<KbLayout>();
    int layoutIndex = 0;
    KbLayout Active { get { return layouts[layoutIndex]; } }

    // ============================ 状态与设置 ============================
    bool autoShow = true;      // 自动呼出
    bool snapEnabled = true;
    int stickyMode = 0;           // sticky: 0=off, 1=one-shot, 2=hold
    bool holdShift, holdCtrl, holdAlt, holdWin;
    Timer holdReleaseTimer = null;    // press-and-hold touch may become a right click; delay the release
    bool holdRightActive = false;     // right-button seen during hold -> finger still down, keep holding
    bool dockBottom = false;   // 锁定贴底（全屏宽）
    bool manualShow = false;   // 本次显示是否由用户手动触发（手动时不自动收起）
    bool userClosed = false;   // 用户手动关闭后进入抑制：直到再点一次输入框才恢复自动弹出
    IntPtr lastFocusFg = IntPtr.Zero, lastFocusHwnd = IntPtr.Zero;
    IntPtr mouseHook = IntPtr.Zero;
    LowLevelMouseProc mouseProc = null;
    bool latchShift, latchCtrl, latchAlt, latchWin;
    bool building = false;
    DateTime lastHide = DateTime.MinValue;
    Rectangle restoreBounds = Rectangle.Empty;

    readonly string dir = Path.GetDirectoryName(Application.ExecutablePath);
    string CfgPath { get { return Path.Combine(dir, "TouchKeyboard.ini"); } }
    string LayoutPath { get { return Path.Combine(dir, "layouts.ini"); } }

    Timer caretTimer;
    NotifyIcon tray;
    Panel strip;
    Label title;
    List<Button> latchButtons = new List<Button>();
    List<Button> headerButtons = new List<Button>();

    int gap = 7, stripH = 40, margin = 8;
    int chromeTop = 0;                 // 贴底时顶部多出一条"调整高度"条，整体内容下移
    const int dockGripH = 30;          // 该条的触摸高度
    int dockHeightUser = 0;            // 用户自定义的贴底高度（0=按布局自动算）
    Panel dockGrip = null;
    Panel cornerGrip = null;           // 未贴底时右下角"拖拽缩放"手柄
    bool resizingCorner = false;
    Point resizeStart = Point.Empty;
    Size resizeStartSize = Size.Empty;
    PreviewForm previewForm = null;      // 拖拽时只显示这个"大小预览框"，不动真窗口
    Rectangle previewRect = Rectangle.Empty;
    int previewDockH = 0;

    // 拖拽预览框：置顶、不抢焦点、鼠标穿透
    class PreviewForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000020 | 0x00000080; return cp; }   // NOACTIVATE | TRANSPARENT
        }
    }

    // ============================ 构造 ============================
    protected override CreateParams CreateParams
    {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW; return cp; }
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    public TouchKeyboard()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;                 // 键盘不该出现在任务栏 / Alt+Tab / 任务视图里
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "触控键盘";
        BackColor = Color.FromArgb(28, 28, 30);
        KeyPreview = false;
        MinimumSize = new Size(560, 200);

        LoadSettings();
        LoadLayouts();

        Rectangle scrB = ScreenBounds();     // ★ 屏幕尺寸不受 AppBar 占位影响，绝不用 WorkingArea 定初始尺寸
        int w;
        if (restoreBounds != Rectangle.Empty && restoreBounds.Width > 200) w = restoreBounds.Width;
        else w = (int)(scrB.Width * 0.86);
        if (w < 720) w = 720;                                   // ★ 下限保护（ClientSize 赋值会绕过 MinimumSize）
        if (w > scrB.Width) w = scrB.Width;
        ClientSize = new Size(w, 240);        // 先定宽度，再按布局算高度
        BuildChrome();                        // 顶部条（按钮 + 拖动区）
        ClientSize = new Size(w, DesiredHeight());
        BuildKeys();                          // 键位
        RestoreWorkArea(false);          // ★ 启动时先清掉可能残留的占位，避免用"缩小的工作区"去定位
        ApplyInitialPlacement();

        caretTimer = new Timer();
        caretTimer.Interval = 200;
        caretTimer.Tick += new EventHandler(CaretTick);
        caretTimer.Start();

        appbarFix = new Timer();
        appbarFix.Interval = 70;
        appbarFix.Tick += new EventHandler(AppBarFixTick);

        holdReleaseTimer = new Timer();
        holdReleaseTimer.Interval = 120;
        holdReleaseTimer.Tick += new EventHandler(HoldReleaseTick);

        dispFix = new Timer();
        dispFix.Interval = 300;
        dispFix.Tick += new EventHandler(DispFixTick);

        BuildTray();

        AutoShow = GetArg("-auto") ? true : autoShow;
        if (GetArg("-dock")) dockBottom = true;
        if (GetArg("-nosnap")) snapEnabled = false;
        ApplyDock(false);
        LayoutChrome();

        if (GetArg("-hidden")) startHidden = true;
        else if (GetArg("-show")) startHidden = false;
        else if (autoShow) startHidden = true;      // 自动呼出开启时：默认只驻留托盘，不主动弹窗
        InstallMouseHook();
        InstallKbHook();
        if (startHidden && tray != null)
        {
            try
            {
                tray.BalloonTipTitle = "触控键盘已在托盘";
                tray.BalloonTipText = "双击托盘图标可显示/隐藏；点击输入框会自动弹出。";
                tray.ShowBalloonTip(3000);
            }
            catch { }
        }
        RefreshHeader();
        RefreshTrayText();
    }

    bool startHidden = false;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (startHidden) { startHidden = false; if (dockBottom && appbarRegistered) { UnregisterAppBar(); RestoreWorkArea(true); dockClosed = true; } Hide(); }   // -hidden：启动即缩到托盘，等自动呼出
    }

    static bool GetArg(string name)
    {
        string[] a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length; i++) if (string.Equals(a[i], name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    static bool GetArgStatic(string name) { return GetArg(name); }

    // ============================ 设置读写 ============================
    void LoadSettings()
    {
        try
        {
            if (!File.Exists(CfgPath)) return;
            foreach (string line in File.ReadAllLines(CfgPath, Encoding.UTF8))
            {
                int p = line.IndexOf('=');
                if (p <= 0) continue;
                string k = line.Substring(0, p).Trim().ToLower();
                string v = line.Substring(p + 1).Trim();
                if (k == "auto") autoShow = (v == "1");
                else if (k == "snap") snapEnabled = (v == "1");
                else if (k == "sticky") { int sm; if (int.TryParse(v, out sm)) stickyMode = (sm <= 0 ? 0 : 1); else stickyMode = (v == "1") ? 1 : 0; }
                else if (k == "dock") dockBottom = (v == "1");
                else if (k == "dockh") { int dh; if (int.TryParse(v, out dh)) dockHeightUser = dh; }
                else if (k == "layout") { /* 布局名稍后匹配 */ pendingLayout = v; }
                else if (k == "x") { int x; if (int.TryParse(v, out x)) restoreBounds.X = x; }
                else if (k == "y") { int y; if (int.TryParse(v, out y)) restoreBounds.Y = y; }
                else if (k == "w") { int ww; if (int.TryParse(v, out ww)) restoreBounds.Width = ww; }
                else if (k == "h") { int hh; if (int.TryParse(v, out hh)) restoreBounds.Height = hh; }
            }
            if (restoreBounds.Width > 0) restoreBounds.Height = Math.Max(restoreBounds.Height, 200);
        }
        catch { }
    }
    string pendingLayout = "";

    void SaveSettings()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("auto=" + (autoShow ? "1" : "0"));
            sb.AppendLine("snap=" + (snapEnabled ? "1" : "0"));
            sb.AppendLine("sticky=" + stickyMode);
            sb.AppendLine("dock=" + (dockBottom ? "1" : "0"));
            sb.AppendLine("dockh=" + dockHeightUser);
            sb.AppendLine("layout=" + Active.Name);
            if (!dockBottom)
            {
                sb.AppendLine("x=" + Location.X);
                sb.AppendLine("y=" + Location.Y);
                sb.AppendLine("w=" + Width);
                sb.AppendLine("h=" + Height);
            }
            else if (restoreBounds.Width > 200)
            {
                sb.AppendLine("x=" + restoreBounds.X);
                sb.AppendLine("y=" + restoreBounds.Y);
                sb.AppendLine("w=" + restoreBounds.Width);
                sb.AppendLine("h=" + restoreBounds.Height);
            }
            File.WriteAllText(CfgPath, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    // ============================ 布局文件 ============================
    void LoadLayouts()
    {
        layouts.Clear();
        try
        {
            if (!File.Exists(LayoutPath)) File.WriteAllText(LayoutPath, DefaultLayoutsText(), Encoding.UTF8);
            KbLayout cur = null;
            foreach (string raw in File.ReadAllLines(LayoutPath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    cur = new KbLayout();
                    cur.Name = line.Substring(1, line.Length - 2).Trim();
                    layouts.Add(cur);
                    continue;
                }
                if (cur == null) continue;
                if (line.StartsWith("Row:") || line.StartsWith("row:"))
                {
                    List<KeySpec> row = new List<KeySpec>();
                    string body = line.Substring(4).Trim();
                    foreach (string tok in body.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        KeySpec ks = ParseKey(tok);
                        if (ks != null) row.Add(ks);
                    }
                    if (row.Count > 0) cur.Rows.Add(row);
                }
            }
        }
        catch { }
        if (layouts.Count == 0)
        {
            KbLayout l = new KbLayout(); l.Name = "默认";
            l.Rows.Add(ParseRow("Esc Tab 1 2 3 4 5 6 7 8 9 0 - = bk"));
            l.Rows.Add(ParseRow("q w e r t y u i o p [ ] \\ del*2"));
            l.Rows.Add(ParseRow("a s d f g h j k l ; ' enter*4"));
            l.Rows.Add(ParseRow("shift*2 z x c v b n m , . / shift*3"));
            l.Rows.Add(ParseRow("ctrl*1.5 zh*1.5 win alt*1.5 space*4.5 left up down right close"));
            layouts.Add(l);
            try { File.WriteAllText(LayoutPath, DefaultLayoutsText(), Encoding.UTF8); } catch { }
        }
        layoutIndex = 0;
        if (pendingLayout != null && pendingLayout.Length > 0)
            for (int i = 0; i < layouts.Count; i++) if (layouts[i].Name == pendingLayout) layoutIndex = i;
    }

    static List<KeySpec> ParseRow(string body)
    {
        List<KeySpec> row = new List<KeySpec>();
        foreach (string tok in body.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            KeySpec k = ParseKey(tok);
            if (k != null) row.Add(k);
        }
        return row;
    }

    static KeySpec ParseKey(string tok)
    {
        float w = 1f;
        int star = tok.LastIndexOf('*');
        if (star > 0)
        {
            string ws = tok.Substring(star + 1);
            float f;
            if (float.TryParse(ws, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) && f > 0.2f)
            { w = f; tok = tok.Substring(0, star); }
        }
        KeySpec k = new KeySpec();
        k.W = w;
        string t = tok.ToLower();
        switch (t)
        {
            case "esc": k.Vk = 0x1B; k.Text = "Esc"; break;
            case "tab": k.Vk = 0x09; k.Text = "Tab"; break;
            case "caps": k.Vk = 0x14; k.Text = "Caps"; break;
            case "enter": case "ret": k.Vk = 0x0D; k.Text = "Enter"; break;
            case "bk": case "back": case "backspace": k.Vk = 0x08; k.Text = "⌫"; break;
            case "del": case "delete": k.Vk = 0x2E; k.Text = "Del"; k.Extended = true; break;
            case "ins": case "insert": k.Vk = 0x2D; k.Text = "Ins"; k.Extended = true; break;
            case "home": k.Vk = 0x24; k.Text = "Home"; k.Extended = true; break;
            case "end": k.Vk = 0x23; k.Text = "End"; k.Extended = true; break;
            case "pgup": k.Vk = 0x21; k.Text = "PgUp"; k.Extended = true; break;
            case "pgdn": k.Vk = 0x22; k.Text = "PgDn"; k.Extended = true; break;
            case "shift": k.Vk = 0x10; k.Text = "⇧"; k.Latch = "shift"; break;
            case "ctrl": k.Vk = 0x11; k.Text = "Ctrl"; k.Latch = "ctrl"; break;
            case "alt": k.Vk = 0x12; k.Text = "Alt"; k.Latch = "alt"; break;
            case "win": k.Vk = 0x5B; k.Text = "Win"; k.Latch = "win"; break;
            case "space": k.Vk = 0x20; k.Text = "空格"; break;
            case "zh": k.Vk = 0x20; k.Text = "中英"; break;      // Ctrl+Space
            case "close": case "hide": k.Text = "✕"; k.IsClose = true; break;
            case "left": k.Vk = 0x25; k.Text = "←"; k.Extended = true; break;
            case "up": k.Vk = 0x26; k.Text = "↑"; k.Extended = true; break;
            case "right": k.Vk = 0x27; k.Text = "→"; k.Extended = true; break;
            case "down": k.Vk = 0x28; k.Text = "↓"; k.Extended = true; break;
            case "`": k.Vk = 0xC0; k.Text = "`"; break;
            default:
                if (t.Length >= 2 && t[0] == 'f' && char.IsDigit(t[1]))
                {
                    int n;
                    if (int.TryParse(t.Substring(1), out n) && n >= 1 && n <= 24)
                    { k.Vk = (ushort)(0x70 + n - 1); k.Text = "F" + n; break; }
                }
                if (tok.Length == 1)
                {
                    char c = tok[0];
                    short v = VkKeyScan(c);
                    if (v != -1)
                    {
                        k.Vk = (ushort)(v & 0xFF);
                        k.NeedShift = ((v >> 8) & 1) != 0;
                        k.Text = tok.ToUpper();
                        break;
                    }
                }
                return null;   // 无法识别
        }
        return k;
    }

        static string DefaultLayoutsText()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("; ================= 触控键盘布局文件 =================");
        sb.AppendLine("; 保存后，点键盘上的「布局」按钮即可切换（会重新读取本文件）");
        sb.AppendLine("; 每行一排：   Row: 键名[*宽度] 键名[*宽度] ...");
        sb.AppendLine("; 键名可用：");
        sb.AppendLine(";   单个字符：1 a ; / 等");
        sb.AppendLine(";   功能键  ：esc tab caps enter bk(退格) del ins home end pgup pgdn");
        sb.AppendLine(";             shift ctrl alt win space zh(中英=Ctrl+Space) close(✕)");
        sb.AppendLine(";             left up down right    f1..f12");
        sb.AppendLine("; 宽度：键名后加 *数字，默认 1（例如 space*4.6）");
        sb.AppendLine("; 说明：下面四个布局全部以「系统」的 26 键排布为蓝本，只是各自增加额外按键，");
        sb.AppendLine(";       所以切换布局时字母区位置不变，不会打乱手感。");
        sb.AppendLine("");
        sb.AppendLine("[系统]");
        sb.AppendLine("; 蓝本：参考 Windows 自带触摸键盘的排布");
        sb.AppendLine("Row: 1 2 3 4 5 6 7 8 9 0 - = bk*2");
        sb.AppendLine("Row: tab*1.5 q w e r t y u i o p [ ] \\*1.5");
        sb.AppendLine("Row: caps*1.8 a s d f g h j k l ; ' enter*2.2");
        sb.AppendLine("Row: shift*2.2 z x c v b n m , . / shift*1.8");
        sb.AppendLine("Row: ctrl*1.4 zh*1.3 win*1.3 alt*1.3 space*4.6 left up down right close");
        sb.AppendLine("");
        sb.AppendLine("[默认]");
        sb.AppendLine("; 系统蓝本 + 一排编辑/导航键（Esc Ins Home PgUp Del End PgDn）");
        sb.AppendLine("Row: esc ins home pgup del end pgdn");
        sb.AppendLine("Row: 1 2 3 4 5 6 7 8 9 0 - = bk*2");
        sb.AppendLine("Row: tab*1.5 q w e r t y u i o p [ ] \\*1.5");
        sb.AppendLine("Row: caps*1.8 a s d f g h j k l ; ' enter*2.2");
        sb.AppendLine("Row: shift*2.2 z x c v b n m , . / shift*1.8");
        sb.AppendLine("Row: ctrl*1.4 zh*1.3 win*1.3 alt*1.3 space*4.6 left up down right close");
        sb.AppendLine("");
        sb.AppendLine("[带Fn行]");
        sb.AppendLine("; 系统蓝本 + 顶部 F1~F12 功能键排");
        sb.AppendLine("Row: esc f1 f2 f3 f4 f5 f6 f7 f8 f9 f10 f11 f12 del");
        sb.AppendLine("Row: 1 2 3 4 5 6 7 8 9 0 - = bk*2");
        sb.AppendLine("Row: tab*1.5 q w e r t y u i o p [ ] \\*1.5");
        sb.AppendLine("Row: caps*1.8 a s d f g h j k l ; ' enter*2.2");
        sb.AppendLine("Row: shift*2.2 z x c v b n m , . / shift*1.8");
        sb.AppendLine("Row: ctrl*1.4 zh*1.3 win*1.3 alt*1.3 space*4.6 left up down right close");
        sb.AppendLine("");
        sb.AppendLine("[简洁]");
        sb.AppendLine("; 系统蓝本 + 只加 Esc 和 Del");
        sb.AppendLine("Row: esc 1 2 3 4 5 6 7 8 9 0 - = bk*1.5");
        sb.AppendLine("Row: tab*1.5 q w e r t y u i o p [ ] \\*1.5");
        sb.AppendLine("Row: caps*1.8 a s d f g h j k l ; ' enter*2.2");
        sb.AppendLine("Row: shift*2.2 z x c v b n m , . / shift*1.8");
        sb.AppendLine("Row: ctrl*1.4 zh*1.3 win*1.3 alt*1.3 space*4.6 left up down right close");
        sb.AppendLine("");
        return sb.ToString();
    }


    // ============================ 界面 ============================
    void BuildChrome()
    {
        strip = new Panel();
        strip.Height = stripH;
        strip.BackColor = Color.FromArgb(45, 45, 50);
        strip.MouseDown += new MouseEventHandler(HeaderDrag);
        Controls.Add(strip);

        title = new Label();
        title.Text = "触控键盘";
        title.ForeColor = Color.Gainsboro;
        title.Font = new Font("Microsoft YaHei UI", 9f);
        title.AutoSize = false;
        title.Location = new Point(8, 0);
        title.Size = new Size(120, stripH);
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.MouseDown += new MouseEventHandler(HeaderDrag);
        strip.Controls.Add(title);

        AddHeaderButton("自动", "auto");
        AddHeaderButton("吸附", "snap");
        AddHeaderButton("粘滞", "sticky");
        AddHeaderButton("贴底", "dock");
        AddHeaderButton("布局", "layout");
        AddHeaderButton("✕", "close");

        // ---------- 触摸友好的可视化调整手柄 ----------
        // ① 贴底时的顶部"高度"条（整条都能上下拖）
        dockGrip = new Panel();
        dockGrip.Height = dockGripH;
        dockGrip.BackColor = Color.FromArgb(70, 70, 82);
        dockGrip.Cursor = Cursors.SizeNS;
        dockGrip.Visible = false;
        dockGrip.Paint += new PaintEventHandler(DockGripPaint);
        dockGrip.MouseDown += new MouseEventHandler(DockGripDown);
        dockGrip.MouseMove += new MouseEventHandler(DockGripMove);
        dockGrip.MouseUp += new MouseEventHandler(DockGripUp);
        Controls.Add(dockGrip);

        // ② 未贴底时的"拖拽缩放"手柄（放在顶栏里，避免盖住右下角的 ✕ 键）
        cornerGrip = new Panel();
        cornerGrip.Size = new Size(96, stripH - 10);
        cornerGrip.BackColor = Color.FromArgb(74, 74, 86);
        cornerGrip.Cursor = Cursors.SizeNWSE;
        cornerGrip.Paint += new PaintEventHandler(CornerGripPaint);
        cornerGrip.MouseDown += new MouseEventHandler(CornerGripDown);
        cornerGrip.MouseMove += new MouseEventHandler(CornerGripMove);
        cornerGrip.MouseUp += new MouseEventHandler(CornerGripUp);
        strip.Controls.Add(cornerGrip);
    }

    // ============================ 手柄：外观与拖动 ============================
    void LayoutChrome()
    {
        try
        {
            chromeTop = dockBottom ? dockGripH : 0;
            if (strip != null) { strip.Dock = DockStyle.None; strip.Bounds = new Rectangle(0, chromeTop, ClientSize.Width, stripH); }
            if (dockGrip != null)
            {
                dockGrip.Visible = dockBottom;
                dockGrip.Bounds = new Rectangle(0, 0, ClientSize.Width, dockGripH);
            }
            if (cornerGrip != null)
            {
                cornerGrip.Visible = !dockBottom;
                cornerGrip.Size = new Size(96, stripH - 10);
                int left = (headerButtons.Count > 0) ? headerButtons[0].Left - 4 - 96 : ClientSize.Width - 200;
                cornerGrip.Location = new Point(Math.Max(120, left), 5);
                cornerGrip.BringToFront();
            }
            LayoutHeaderButtons();
        }
        catch { }
    }

    void DockGripPaint(object s, PaintEventArgs e)
    {
        try
        {
            using (Pen p = new Pen(Color.FromArgb(180, 180, 200), 2))
            {
                int cx = dockGrip.Width / 2;
                for (int i = -2; i <= 2; i++) e.Graphics.DrawLine(p, cx + i * 18 - 8, 9, cx + i * 18 - 8, dockGrip.Height - 9);
            }
            using (SolidBrush br = new SolidBrush(Color.FromArgb(228, 228, 238)))
            using (Font f = new Font("Microsoft YaHei UI", 9f))
            {
                int h = (dockHeightUser > 0) ? dockHeightUser : Height;
                e.Graphics.DrawString("按住这条上下拖动可调整贴底高度（当前 " + h + " px）", f, br, new PointF(14, 6));
            }
        }
        catch { }
    }

    void CornerGripPaint(object s, PaintEventArgs e)
    {
        try
        {
            using (SolidBrush br = new SolidBrush(Color.FromArgb(228, 228, 238)))
            using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                e.Graphics.DrawString("◢ 拖拽缩放", f, br, new PointF(8, 5));
        }
        catch { }
    }

    void EnsurePreview()
    {
        if (previewForm != null) return;
        previewForm = new PreviewForm();
        previewForm.FormBorderStyle = FormBorderStyle.None;
        previewForm.ShowInTaskbar = false;
        previewForm.TopMost = true;
        previewForm.StartPosition = FormStartPosition.Manual;
        previewForm.BackColor = Color.FromArgb(255, 150, 40);
        previewForm.Opacity = 0.30;
        previewForm.Paint += new PaintEventHandler(delegate(object s, PaintEventArgs e)
        {
            try
            {
                using (Pen p = new Pen(Color.FromArgb(255, 205, 100), 3))
                    e.Graphics.DrawRectangle(p, 1, 1, previewForm.Width - 4, previewForm.Height - 4);
            }
            catch { }
        });
    }

    void ShowPreview(Rectangle r)
    {
        try
        {
            EnsurePreview();
            previewRect = r;
            previewForm.Bounds = r;
            if (!previewForm.Visible) previewForm.Show();
            previewForm.BringToFront();
        }
        catch { }
    }

    void HidePreview()
    {
        try { if (previewForm != null && previewForm.Visible) previewForm.Hide(); } catch { }
    }

    void DockGripDown(object s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        dockGrip.Capture = true;
        previewDockH = 0;
    }

    void DockGripMove(object s, MouseEventArgs e)
    {
        if (!dockGrip.Capture) return;
        Screen scr = Screen.FromControl(this);
        int bt = AppBarBottom(scr.Bounds);
        int h = bt - Cursor.Position.Y;
        int max = scr.Bounds.Height - 160;
        if (h < 220) h = 220;
        if (h > max) h = max;
        previewDockH = h;
        ShowPreview(new Rectangle(scr.Bounds.Left, bt - h, scr.Bounds.Width, h));   // 只动预览框
    }

    void DockGripUp(object s, MouseEventArgs e)
    {
        if (!dockGrip.Capture) return;
        dockGrip.Capture = false;
        HidePreview();
        if (previewDockH > 0) SetDockHeight(previewDockH);                          // 松手才真正应用
        previewDockH = 0;
    }

    void SetDockHeight(int h)
    {
        try
        {
            Screen scr = Screen.FromControl(this);
            int max = scr.Bounds.Height - 160;
            if (h < 220) h = 220;
            if (h > max) h = max;
            dockHeightUser = h;
            if (dockBottom)
            {
                dockHeight = h;
                ClientSize = new Size(scr.Bounds.Width, h);
                int bt = AppBarBottom(scr.Bounds);
                Bounds = new Rectangle(scr.Bounds.Left, bt - h, scr.Bounds.Width, h);
                SetAppBarPos();
            }
            BuildKeys();
            LayoutChrome();
            Invalidate();
            SaveSettings();
            Dbg("dock height -> " + h);
        }
        catch { }
    }

    void CornerGripDown(object s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || dockBottom) return;
        resizingCorner = true;
        resizeStart = Cursor.Position;
        resizeStartSize = Size;
        cornerGrip.Capture = true;
    }

    void CornerGripMove(object s, MouseEventArgs e)
    {
        if (!resizingCorner) return;
        Point p = Cursor.Position;
        Screen scr = Screen.FromControl(this);
        int w = resizeStartSize.Width + (p.X - resizeStart.X);
        int h = resizeStartSize.Height + (p.Y - resizeStart.Y);
        if (w < MinimumSize.Width) w = MinimumSize.Width;
        if (h < MinimumSize.Height) h = MinimumSize.Height;
        if (w > scr.Bounds.Width) w = scr.Bounds.Width;
        if (h > scr.Bounds.Height - 60) h = scr.Bounds.Height - 60;
        ShowPreview(new Rectangle(Location, new Size(w, h)));   // ★ 拖拽期间只画框，不重建按键
    }

    void CornerGripUp(object s, MouseEventArgs e)
    {
        if (!resizingCorner) return;
        resizingCorner = false;
        cornerGrip.Capture = false;
        HidePreview();
        Size = previewRect.Size;          // 松手才真正应用一次
        BuildKeys();
        LayoutChrome();
        Invalidate();
        SnapToNearestEdge();
        SaveSettings();
    }

    void AddHeaderButton(string text, string role)
    {
        Button b = new Button();
        b.Text = text;
        b.Tag = role;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.TabStop = false;
        b.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        b.Size = new Size(role == "layout" ? 116 : (role == "sticky" ? 116 : 92), stripH - 10);
        b.Click += new EventHandler(HeaderClick);
        strip.Controls.Add(b);
        headerButtons.Add(b);
    }

    void LayoutHeaderButtons()
    {
        if (title == null || strip == null) return;   // 构造早期（BuildChrome 之前）可能被 OnResize 触发
        int right = ClientSize.Width - 6;
        for (int i = headerButtons.Count - 1; i >= 0; i--)
        {
            Button b = headerButtons[i];
            b.Left = right - b.Width;
            b.Top = 5;
            right = b.Left - 4;
        }
        title.Width = Math.Max(60, right - 12);
    }

    void HeaderDrag(object s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !dockBottom) { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero); }
    }

    void HeaderClick(object s, EventArgs e)
    {
        Button b = (Button)s;
        string role = (string)b.Tag;
        if (role == "auto") { autoShow = !autoShow; if (!autoShow) manualShow = true; }
        else if (role == "snap") snapEnabled = !snapEnabled;
        else if (role == "sticky") { stickyMode = (stickyMode + 1) % 2; }
        else if (role == "dock") { ToggleDock(); }
        else if (role == "layout") { CycleLayout(); }
        else if (role == "close") { HideKeyboard(true); }
        RefreshHeader();
        SaveSettings();
    }

    string lastLayoutName = "";

    void CycleLayout()
    {
        int target = (layouts.Count > 0) ? (layoutIndex + 1) % layouts.Count : 0;
        LoadLayouts();                       // 重新读取 layouts.ini（支持随时编辑后点“布局”生效）
        if (target >= layouts.Count) target = 0;
        layoutIndex = target;
        // 不同布局的关键排数可能不同：高度跟着调整（否则 6 排会被压扁）
        if (dockBottom) ApplyDock(true);
        else
        {
            int wantH = DesiredHeight();
            if (wantH > 120 && wantH != Height)
            {
                int oldBottom = Bottom;
                ClientSize = new Size(ClientSize.Width, wantH);
                Top = oldBottom - Height;            // 让底边保持不动，向上长高
            }
        }
        BuildKeys();
        LayoutChrome();
        RefreshHeader();
        Invalidate();
        SaveSettings();
    }

    void RefreshHeader()
    {
        lastLayoutName = Active.Name;
        foreach (Button b in headerButtons)
        {
            string role = (string)b.Tag;
            if (role == "close") { b.BackColor = Color.FromArgb(120, 45, 45); b.ForeColor = Color.White; continue; }
            if (role == "sticky")
            {
                b.Text = "粘滞:" + (stickyMode == 0 ? "关" : "单次");
                b.BackColor = stickyMode == 0 ? Color.FromArgb(58, 72, 100) : Color.FromArgb(200, 140, 40);
                b.ForeColor = Color.White;
                continue;
            }
            bool on = (role == "auto" && autoShow) || (role == "snap" && snapEnabled) || (role == "dock" && dockBottom);
            if (role == "layout") { b.Text = "布局:" + Active.Name; b.BackColor = Color.FromArgb(58, 72, 100); b.ForeColor = Color.White; continue; }
            b.Text = (role == "auto" ? "自动" : role == "snap" ? "吸附" : "贴底") + (on ? ":开" : ":关");
            b.BackColor = on ? Color.FromArgb(200, 140, 40) : Color.FromArgb(58, 72, 100);
            b.ForeColor = Color.White;
        }
        Text = "触控键盘  " + (autoShow ? "[自动呼出]" : "") + (dockBottom ? "[贴底]" : "");
    }

    void BuildKeys()
    {
        building = true;
        for (int i = Controls.Count - 1; i >= 0; i--) if (!(Controls[i] is Panel)) Controls.RemoveAt(i);
        latchButtons.Clear(); vkMap.Clear(); baseColors.Clear();

        int rows = Active.Rows.Count;
        if (rows == 0) { building = false; return; }
        int availH = ClientSize.Height - chromeTop - stripH - (rows + 1) * gap;
        int kh = availH / rows;
        if (kh < 26) kh = 26;
        int areaTop = chromeTop + stripH + gap;

        // 每排按键各自按“单位数”撑满宽度；行高统一由窗口高度决定（可自由纵向拉伸）
        for (int r = 0; r < rows; r++)
        {
            List<KeySpec> row = Active.Rows[r];
            float total = 0f;
            for (int i = 0; i < row.Count; i++) total += row[i].W;
            if (total <= 0f) continue;

            int usable = ClientSize.Width - 2 * margin - (int)Math.Round((double)((row.Count + 1) * gap));
            float unit = usable / total;
            int y = areaTop + r * (kh + gap);

            float x = margin + gap;
            for (int i = 0; i < row.Count; i++)
            {
                KeySpec k = row[i];
                int kw = (int)Math.Round(k.W * unit + (k.W - 1) * gap);
                Button b = new Button();
                b.Text = k.Text;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.TabStop = false;
                b.ForeColor = Color.White;
                b.Font = new Font("Segoe UI", Math.Max(9f, Math.Min(kh * 0.34f, 22f)) < 14f ? 11f : Math.Min(kh * 0.34f, 22f), FontStyle.Bold);
                b.Size = new Size(kw, kh);
                b.Location = new Point((int)Math.Round(x), y);
                b.BackColor = (k.Latch != null) ? Color.FromArgb(58, 72, 100) : (k.IsClose ? Color.FromArgb(110, 45, 45) : Color.FromArgb(62, 62, 68));
                b.Tag = k;
                if (k.Vk > 0) { List<Button> vl; if (!vkMap.TryGetValue(k.Vk, out vl)) { vl = new List<Button>(); vkMap[k.Vk] = vl; } vl.Add(b); }
                baseColors[b] = b.BackColor;
                b.MouseDown += new MouseEventHandler(KeyDown2);
                b.MouseUp += new MouseEventHandler(KeyUp2);
                Controls.Add(b);
                if (k.Latch != null) latchButtons.Add(b);
                x += kw + gap;
            }
        }
        UpdateLatchColors();
        LayoutChrome();
        building = false;
    }

    void KeyDown2(object s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Button b = (Button)s;
        KeySpec k = (KeySpec)b.Tag;
        if (k == null) return;

        if (k.IsClose) { HideKeyboard(true); return; }
        if (k.Latch != null)
        {
            if (stickyMode == 2)
            {
                if (k.Latch == "shift") { holdShift = true; }
                else if (k.Latch == "ctrl") holdCtrl = true;
                else if (k.Latch == "alt") holdAlt = true;
                else if (k.Latch == "win") holdWin = true;
                UpdateLatchColors();
                return;
            }
            if (k.Latch == "shift") latchShift = !latchShift;
            else if (k.Latch == "ctrl") latchCtrl = !latchCtrl;
            else if (k.Latch == "alt") latchAlt = !latchAlt;
            else if (k.Latch == "win") latchWin = !latchWin;
            UpdateLatchColors();
            return;
        }
                PressKey(k);
    }

    // sticky mode 2: release on mouse up; a short tap (<200ms) becomes one-shot
    void KeyUp2(object s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || stickyMode != 2) return;
        Button b = (Button)s;
        KeySpec k = (KeySpec)b.Tag;
        if (k == null || k.Latch == null) return;
        if (k.Latch == "shift") holdShift = false;
        else if (k.Latch == "ctrl") holdCtrl = false;
        else if (k.Latch == "alt") holdAlt = false;
        else if (k.Latch == "win") holdWin = false;
        UpdateLatchColors();
    }

    void ReleaseAllHolds()
    {
        if (holdReleaseTimer != null) holdReleaseTimer.Stop();
        holdRightActive = false;
        if (holdShift || holdCtrl || holdAlt || holdWin)
        {
            holdShift = holdCtrl = holdAlt = holdWin = false;
            UpdateLatchColors();
            Dbg("release all holds");
        }
    }

    void HoldReleaseTick(object s, EventArgs e)
    {
        try
        {
            holdReleaseTimer.Stop();
            if (holdRightActive) return;
            ReleaseAllHolds();
        }
        catch { }
    }

    void UpdateLatchColors()
    {
        foreach (Button b in latchButtons)
        {
            KeySpec k = (KeySpec)b.Tag;
            bool on = (k.Latch == "shift" && (latchShift || holdShift)) || (k.Latch == "ctrl" && (latchCtrl || holdCtrl)) ||
                      (k.Latch == "alt" && (latchAlt || holdAlt)) || (k.Latch == "win" && (latchWin || holdWin));
            b.BackColor = on ? Color.FromArgb(200, 140, 40) : Color.FromArgb(58, 72, 100);
        }
    }

    // ============================ 显示 / 隐藏 ============================
    void ApplyInitialPlacement()
    {
        Rectangle sb = ScreenBounds();
        int bottom = AppBarBottom(sb);                     // 屏幕底边/任务栏上沿，始终可靠
        int x, y;
        if (restoreBounds.Width > 200 && restoreBounds.X > -10000 && restoreBounds.Y > -10000)
        { x = restoreBounds.X; y = restoreBounds.Y; }
        else { x = sb.Left + (sb.Width - Width) / 2; y = bottom - Height - 4; }
        Location = new Point(x, y);
    }

    public bool AutoShow { get { return autoShow; } set { autoShow = value; } }

    void ShowKeyboard(bool manual)
    {
        if (manual) manualShow = true;
        dockClosed = false;               // 重新显示 → 恢复登记能力
        if (dockBottom && !appbarRegistered)
        {
            // 贴底状态但占位已注销（之前被关闭过）→ 先藏起来重新登记，摆稳后由 fix tick 显示
            settling = true; appbarPendingShow = true; Hide();
            RegisterAppBar(); SetAppBarPos();
        }
        else
        {
            if (dockBottom) ApplyDock(false);
            Show();
            BringToFront();
        }
        RefreshTrayText();
    }

    // byUser=true：用户主动关闭（✕ / 托盘） → 进入抑制状态，直到用户再点一次输入框
    void HideKeyboard(bool byUser)
    {
        manualShow = false;
        if (byUser) userClosed = true;
        lastHide = DateTime.Now;
        // ★ 贴底状态下关闭键盘，也要把占位还回去、工作区恢复
        if (dockBottom && appbarRegistered) { UnregisterAppBar(); RestoreWorkArea(true); dockClosed = true; }
        Hide();
        RefreshTrayText();
    }

    void RefreshTrayText()
    {
        try
        {
            if (tray == null) return;
            string s = "触控键盘";
            if (!autoShow) s += "（自动呼出:关）";
            else if (userClosed) s += "（已手动关闭，点输入框恢复）";
            else s += "（自动呼出:开）";
            if (dockBottom) s += "[贴底]";
            if (s.Length > 62) s = s.Substring(0, 62);
            tray.Text = s;
        }
        catch { }
    }

    int DesiredHeight()
    {
        int rows = Active.Rows.Count;
        if (rows <= 0) return 240;
        float maxUnits = 1f;
        for (int r = 0; r < rows; r++)
        {
            float t = 0f;
            List<KeySpec> row = Active.Rows[r];
            for (int i = 0; i < row.Count; i++) t += row[i].W;
            if (t > maxUnits) maxUnits = t;
        }
        int usable = ClientSize.Width - 2 * margin - (int)Math.Round((maxUnits + 1) * gap);
        float unit = usable / maxUnits;
        int kh = (int)(unit * 0.85f);
        if (kh < 26) kh = 26;
        int auto = chromeTop + stripH + gap + rows * (kh + gap) + gap;
        if (dockBottom && dockHeightUser > 0)          // 用户用顶部手柄调过高度就用他的
        {
            int max = ScreenBounds().Height - 160;
            return Math.Max(220, Math.Min(dockHeightUser, max));
        }
        return auto;
    }

    void ApplyDock(bool save)    {
        Screen scr = Screen.FromControl(this);
        if (dockBottom)
        {
            if (!dockBottomWasOn) { restoreBounds = Bounds; }
            if (Visible) { appbarPendingShow = true; settling = true; Hide(); }   // 先藏起来，摆稳后再出现
            Rectangle sb = scr.Bounds;                            // ★ 用整屏宽度，不用 WorkingArea
            ClientSize = new Size(sb.Width, ClientSize.Height);
            dockHeight = DesiredHeight();
            int bt = AppBarBottom(sb);                            // ★ 底部基准同样只看屏幕/任务栏
            Dbg(String.Format("dock on: clientW={0} dockHeight={1} bottom={2}", ClientSize.Width, dockHeight, bt));
            Bounds = new Rectangle(sb.Left, bt - dockHeight, sb.Width, dockHeight);
            RegisterAppBar();
            SetAppBarPos();
        }
        else
        {
            UnregisterAppBar();        // 取消占位
            RestoreWorkArea(false);         // ★ 主动恢复工作区并广播
            if (dockBottomWasOn && restoreBounds.Width > 200) Bounds = restoreBounds;
        }
        dockBottomWasOn = dockBottom;
        BuildKeys();
        LayoutHeaderButtons();
        Invalidate();
        if (save) SaveSettings();
    }
    bool dockBottomWasOn = false;

    void ToggleDock()
    {
        dockClosed = false;
        Dbg("toggle dock -> " + dockBottom);
        if (!dockBottom) { restoreBounds = Bounds; dockBottom = true; }
        else dockBottom = false;
        ApplyDock(true);
    }

    // ============================ 吸附 ============================
    void SnapToNearestEdge()
    {
        Dbg(String.Format("snap called: dock={0} snap={1} appbar={2} bounds={3}", dockBottom, snapEnabled, appbarRegistered, Bounds));
        if (!snapEnabled || dockBottom) { Dbg("snap skipped"); return; }
        Screen scr = Screen.FromControl(this);
        Rectangle wa = scr.WorkingArea;
        int thr = 40;
        Rectangle b = Bounds;

        int dl = Math.Abs(b.Left - wa.Left);
        int dr = Math.Abs(wa.Right - b.Right);
        int dt = Math.Abs(b.Top - wa.Top);
        int db = Math.Abs(wa.Bottom - b.Bottom);
        int min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));

        if (min <= thr)
        {
            if (min == dl) b.X = wa.Left;
            else if (min == dr) b.X = wa.Right - b.Width;
            else if (min == dt) b.Y = wa.Top;
            else b.Y = wa.Bottom - b.Height;
        }
        // 保证不出工作区
        if (b.Left < wa.Left) b.X = wa.Left;
        if (b.Top < wa.Top) b.Y = wa.Top;
        if (b.Right > wa.Right) b.X = wa.Right - b.Width;
        if (b.Bottom > wa.Bottom) b.Y = wa.Bottom - b.Height;
        Bounds = b;
    }

    // ============================ 自动呼出 ============================
    // ---------- UI Automation 兜底：浏览器/Office/Electron 等不上报 Win32 Caret 的输入框 ----------
    DateTime lastUiaCheck = DateTime.MinValue;
    IntPtr lastUiaFg = IntPtr.Zero;
    bool uiaFocusIsText = false;

    void UpdateUiaState(IntPtr fg)
    {
        try
        {
            if (fg == lastUiaFg && (DateTime.Now - lastUiaCheck).TotalMilliseconds < 700) return;
            lastUiaFg = fg; lastUiaCheck = DateTime.Now;
            uint fgPid = 0;
            GetWindowPid(fg, out fgPid);
            System.Windows.Automation.AutomationElement el = System.Windows.Automation.AutomationElement.FocusedElement;
            bool ok = false;
            if (el != null && el.Current.ProcessId == (int)fgPid)      // 必须与前台窗口同进程，避免"陈旧焦点"误判
            {
                object ct = el.GetCurrentPropertyValue(System.Windows.Automation.AutomationElement.ControlTypeProperty);
                if (ct == System.Windows.Automation.ControlType.Edit || ct == System.Windows.Automation.ControlType.ComboBox) ok = true;
            }
            uiaFocusIsText = ok;
        }
        catch { uiaFocusIsText = false; }
    }

    static string ClassOf(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(null)";
        StringBuilder sb = new StringBuilder(256);
        if (GetClassName(h, sb, sb.Capacity) <= 0) return "(?)";
        return sb.ToString();
    }

    static bool debugLog = false;
    static string logPath = "";
    DateTime lastLog = DateTime.MinValue;
    static void Dbg(string s)
    {
        try { if (debugLog && logPath.Length > 0) File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n", Encoding.UTF8); }
        catch { }
    }

    int noCaretTicks = 0;

    void CaretTick(object s, EventArgs e)
    {
        if (!autoShow) return;
        if (settling) return;                  // 贴底摆放期间不要弹出来
        try
        {
            if ((DateTime.Now - lastHide).TotalMilliseconds < 600) return;
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return;

            bool rawCaret = false, classHit = false;
            uint tid = GetWindowThreadProcessId(fg, IntPtr.Zero);
            GUITHREADINFO gti = new GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            string focusCls = "(无)";
            if (GetGUIThreadInfo(tid, ref gti))
            {
                if (gti.hwndCaret != IntPtr.Zero || (gti.flags & GUI_CARETBLINKING) != 0) rawCaret = true;
                IntPtr fh = gti.hwndFocus != IntPtr.Zero ? gti.hwndFocus : fg;
                focusCls = ClassOf(fh);
                if (IsInputClass(fh) || IsInputClass(fg)) classHit = true;
            }
            bool hasCaret = rawCaret || classHit;
            if (!hasCaret)
            {
                UpdateUiaState(fg);                 // 传统 Caret 探测不到时用 UIA 判断
                if (uiaFocusIsText) hasCaret = true;
            }
            if (debugLog && (DateTime.Now - lastLog).TotalMilliseconds > 400)
            {
                lastLog = DateTime.Now;
                Dbg(String.Format("fg={0} focus={1} hwndCaret={2} flags=0x{3:X} classHit={4} uia={5} => hasInput={6} visible={7}",
                    ClassOf(fg), focusCls, gti.hwndCaret, gti.flags, classHit, uiaFocusIsText, hasCaret, Visible));
            }
            IntPtr focusNow = gti.hwndFocus;
            if (fg != lastFocusFg || focusNow != lastFocusHwnd) userClosed = false;   // 焦点变化 → 恢复自动弹出
            lastFocusFg = fg; lastFocusHwnd = focusNow;
            if (hasCaret)
            {
                noCaretTicks = 0;
                if (!Visible && !userClosed) ShowKeyboard(false);                     // 手动关闭后不自动重开
            }
            else if (Visible && !manualShow)
            {
                noCaretTicks++;
                if (noCaretTicks >= 4) { noCaretTicks = 0; HideKeyboard(false); }     // 防抖：连续 ~800ms 无输入框才收起
            }
        }
        catch { }
    }

    static readonly string[] InputClasses = new string[] {
        "Edit","Rich","Scintilla","TextBox","Console","Omnibox","Search","InputSite",
        "Chrome_","Qt5","Afx","TXGuiFoundation","Internet Explorer_Server","Windows.UI.Core"
    };

    static bool IsInputClass(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        StringBuilder sb = new StringBuilder(256);
        if (GetClassName(h, sb, sb.Capacity) <= 0) return false;
        string c = sb.ToString();
        for (int i = 0; i < InputClasses.Length; i++) if (c.IndexOf(InputClasses[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // ============================ 托盘 ============================
    static NotifyIcon trayStatic = null;
    public static void HideTrayNow() { try { if (trayStatic != null) trayStatic.Visible = false; } catch { } }

    void BuildTray()
    {
        try
        {
            Icon ic = MakeIcon();
            tray = new NotifyIcon(); trayStatic = tray;
            tray.Icon = ic;
            tray.Text = "触控键盘";
            tray.Visible = true;
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("显示 / 隐藏", null, new EventHandler(delegate(object s, EventArgs e) { if (Visible) HideKeyboard(true); else ShowKeyboard(true); RefreshHeader(); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("自动呼出", null, new EventHandler(delegate(object s, EventArgs e) { autoShow = !autoShow; RefreshHeader(); SaveSettings(); if (!autoShow) manualShow = true; }));
            m.Items.Add("自动吸附边缘", null, new EventHandler(delegate(object s, EventArgs e) { snapEnabled = !snapEnabled; RefreshHeader(); SaveSettings(); }));
            m.Items.Add("锁定贴底（全屏宽）", null, new EventHandler(delegate(object s, EventArgs e) { ToggleDock(); RefreshHeader(); }));
            m.Items.Add("切换布局", null, new EventHandler(delegate(object s, EventArgs e) { CycleLayout(); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("退出", null, new EventHandler(delegate(object s, EventArgs e) { tray.Visible = false; Application.Exit(); }));
            tray.ContextMenuStrip = m;
            tray.DoubleClick += new EventHandler(delegate(object s, EventArgs e) { if (Visible) HideKeyboard(true); else ShowKeyboard(true); RefreshHeader(); });
        }
        catch { }
    }

    static Icon MakeIcon()
    {
        Bitmap bmp = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.FillRectangle(new SolidBrush(Color.FromArgb(0, 120, 212)), 1, 3, 14, 10);
            g.FillRectangle(Brushes.White, 3, 6, 2, 2);
            g.FillRectangle(Brushes.White, 7, 6, 2, 2);
            g.FillRectangle(Brushes.White, 11, 6, 2, 2);
            g.FillRectangle(Brushes.White, 4, 10, 8, 1);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    // ============================ 全局鼠标点击探测 ============================
    // ============ physical keyboard live highlight ============
    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    IntPtr kbHook = IntPtr.Zero;
    LowLevelKeyboardProc kbProc = null;
    Dictionary<int, List<Button>> vkMap = new Dictionary<int, List<Button>>();
    Dictionary<Button, Color> baseColors = new Dictionary<Button, Color>();

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetKbHook(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    void InstallKbHook()
    {
        try { kbProc = new LowLevelKeyboardProc(KbHookProc); kbHook = SetKbHook(WH_KEYBOARD_LL, kbProc, IntPtr.Zero, 0); }
        catch { }
    }

    void HighlightVk(int vk, bool down)
    {
        List<Button> list;
        if (!vkMap.TryGetValue(vk, out list)) return;
        for (int i = 0; i < list.Count; i++)
        {
            Button b = list[i];
            if (b == null || b.IsDisposed) continue;
            KeySpec k = b.Tag as KeySpec;
            if (down) b.BackColor = Color.FromArgb(238, 176, 70);
            else if (k != null && k.Latch != null) UpdateLatchColors();
            else { Color c; if (baseColors.TryGetValue(b, out c)) b.BackColor = c; }
        }
    }

    IntPtr KbHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                HighlightVk(Marshal.ReadInt32(lParam), msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
            }
        }
        catch { }
        return CallNextHookEx(kbHook, nCode, wParam, lParam);
    }
    // ============================ multi-touch (WM_TOUCH) ============================
    [StructLayout(LayoutKind.Sequential)]
    public struct TOUCHINPUT { public int x, y; public IntPtr hSource; public uint dwID, dwFlags, dwMask, dwTime; public IntPtr dwExtraInfo; public uint cxContact, cyContact; }
    [DllImport("user32.dll")] static extern bool RegisterTouchWindow(IntPtr hWnd, uint ulFlags);
    [DllImport("user32.dll")] static extern bool UnregisterTouchWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetTouchInputInfo(IntPtr hTouchInput, int cInputs, [Out] TOUCHINPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern bool CloseTouchInputHandle(IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr GetMessageExtraInfo();
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    const int WM_TOUCH = 0x0240, WM_TOUCHTEST_DOWN = 0x8000 + 7, WM_TOUCHTEST_UP = 0x8000 + 8;
    const uint TOUCHEVENTF_MOVE = 0x0001, TOUCHEVENTF_DOWN = 0x0002, TOUCHEVENTF_UP = 0x0004, TWF_WANTPALM = 0x00000002;
    const long TOUCH_SIG = 0xFF515700L, TOUCH_SIG_MASK = 0xFFFFFF00L;
    Dictionary<uint, Button> touchOwner = new Dictionary<uint, Button>();
    bool touchRoutingEnabled = false;   // rollback: raw-touch routing disabled

    static bool IsTouchMouse()
    {
        try { return (GetMessageExtraInfo().ToInt64() & TOUCH_SIG_MASK) == TOUCH_SIG; }
        catch { return false; }
    }

    void HandleTouch(IntPtr lParam, int count)
    {
        if (count <= 0 || count > 32) return;
        TOUCHINPUT[] ti = new TOUCHINPUT[count];
        if (!GetTouchInputInfo(lParam, count, ti, Marshal.SizeOf(typeof(TOUCHINPUT)))) return;
        try
        {
            for (int i = 0; i < count; i++)
            {
                TOUCHINPUT t = ti[i];
                bool down = (t.dwFlags & TOUCHEVENTF_DOWN) != 0;
                bool up = (t.dwFlags & TOUCHEVENTF_UP) != 0;
                bool mv = (t.dwFlags & TOUCHEVENTF_MOVE) != 0;
                if (!down && !up && !mv) continue;
                RouteTouch(t.dwID, new Point(Left + t.x / 100, Top + t.y / 100), down ? 0 : (up ? 2 : 1));
            }
        }
        finally { CloseTouchInputHandle(lParam); }
    }

    void RouteTouch(uint id, Point screenPt, int phase)
    {
        try
        {
            if (!touchRoutingEnabled) return;
            Point client = PointToClient(screenPt);
            Button b = FindKeyButton(client);
            if (b == null) { ForwardTouch(id, client, phase); return; }
            KeySpec k = b.Tag as KeySpec;
            if (k == null) return;
            if (phase == 0)
            {
                touchOwner[id] = b;
                TouchDown(k, b);
                Dbg("touch down id=" + id + " client=" + client.X + "," + client.Y + " key=" + k.Text);
            }
            else if (phase == 2)
            {
                touchOwner.Remove(id);
                TouchUp(k, b);
                Dbg("touch up id=" + id + " key=" + k.Text);
            }
        }
        catch { }
    }

    Dictionary<uint, Control> touchTarget = new Dictionary<uint, Control>();

    void ForwardTouch(uint id, Point client, int phase)
    {
        try
        {
            if (phase == 0)
            {
                Button hb = FindHeaderButton(client);
                if (hb != null) { HeaderClick(hb, EventArgs.Empty); Dbg("touch header -> " + hb.Tag); return; }
                Control c = HitTestControl(this, client);
                if (c == null) return;
                touchTarget[id] = c;
                SetCursorPos(Left + client.X, Top + client.Y);
                Point lp = new Point(client.X - c.Left, client.Y - c.Top);
                SendMessage(c.Handle, WM_LBUTTONDOWN, (IntPtr)1, (IntPtr)((lp.Y << 16) | (lp.X & 0xFFFF)));
                Dbg("touch fwd down -> " + c.GetType().Name + " role=" + (c.Tag == null ? "-" : c.Tag.ToString()));
            }
            else
            {
                Control c;
                if (!touchTarget.TryGetValue(id, out c)) return;
                SetCursorPos(Left + client.X, Top + client.Y);
                Point lp = new Point(client.X - c.Left, client.Y - c.Top);
                SendMessage(c.Handle, phase == 2 ? WM_LBUTTONUP : WM_MOUSEMOVE, (IntPtr)(phase == 2 ? 0 : 1), (IntPtr)((lp.Y << 16) | (lp.X & 0xFFFF)));
                if (phase == 2) touchTarget.Remove(id);
            }
        }
        catch { }
    }

    Button FindHeaderButton(Point client)
    {
        try
        {
            if (strip == null || !strip.Bounds.Contains(client)) return null;
            Point lp = new Point(client.X - strip.Left, client.Y - strip.Top);
            for (int i = 0; i < strip.Controls.Count; i++)
            {
                Button b = strip.Controls[i] as Button;
                if (b != null && b.Visible && !b.IsDisposed && b.Bounds.Contains(lp)) return b;
            }
        }
        catch { }
        return null;
    }

    Control HitTestControl(Control parent, Point client)
    {
        for (int i = parent.Controls.Count - 1; i >= 0; i--)
        {
            Control c = parent.Controls[i];
            if (!c.Visible || c.IsDisposed) continue;
            if (!c.Bounds.Contains(client)) continue;
            Control deep = HitTestControl(c, new Point(client.X - c.Left, client.Y - c.Top));
            return deep != null ? deep : c;
        }
        return null;
    }

    Button FindKeyButton(Point client)
    {
        for (int i = 0; i < Controls.Count; i++)
        {
            Button b = Controls[i] as Button;
            if (b == null || b.IsDisposed || !b.Visible) continue;
            if (b.Bounds.Contains(client)) return b;
        }
        return null;
    }

    void TouchDown(KeySpec k, Button b)
    {
        if (k.IsClose) { HideKeyboard(true); return; }
        if (k.Latch != null)
        {
            if (stickyMode == 2) SetHold(k.Latch, true); else ToggleLatch(k.Latch);
            UpdateLatchColors();
            return;
        }
        PressKey(k);
        if (b != null) b.BackColor = Color.FromArgb(238, 176, 70);
    }

    void TouchUp(KeySpec k, Button b)
    {
        if (k.Latch != null)
        {
            if (stickyMode == 2 && !OtherTouchOwns(k.Latch, b)) { SetHold(k.Latch, false); UpdateLatchColors(); }
            return;
        }
        if (b != null) { Color c; if (baseColors.TryGetValue(b, out c)) b.BackColor = c; }
    }

    bool OtherTouchOwns(string latch, Button except)
    {
        foreach (KeyValuePair<uint, Button> kv in touchOwner)
        {
            if (kv.Value == except) continue;
            KeySpec kk = kv.Value.Tag as KeySpec;
            if (kk != null && kk.Latch == latch) return true;
        }
        return false;
    }

    void SetHold(string latch, bool on)
    {
        if (latch == "shift") holdShift = on;
        else if (latch == "ctrl") holdCtrl = on;
        else if (latch == "alt") holdAlt = on;
        else if (latch == "win") holdWin = on;
    }

    void ToggleLatch(string latch)
    {
        if (latch == "shift") latchShift = !latchShift;
        else if (latch == "ctrl") latchCtrl = !latchCtrl;
        else if (latch == "alt") latchAlt = !latchAlt;
        else if (latch == "win") latchWin = !latchWin;
    }

    void PressKey(KeySpec k)
    {
        Dbg("press vk=" + k.Vk);
        bool ctrl = latchCtrl || holdCtrl, alt = latchAlt || holdAlt, win = latchWin || holdWin, shift = latchShift || holdShift || k.NeedShift;
        if (k.Text == "中英") Combo(0x20, false, true, false, false, false);
        else Combo(k.Vk, shift, ctrl, alt, win, k.Extended);
        if (latchCtrl || latchAlt || latchWin || ((stickyMode >= 1) && latchShift))
        {
            latchCtrl = latchAlt = latchWin = false;
            if (stickyMode >= 1) latchShift = false;
            UpdateLatchColors();
        }
    }
    void InstallMouseHook()
    {
        try
        {
            mouseProc = new LowLevelMouseProc(MouseHookProc);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, IntPtr.Zero, 0);
        }
        catch { }
    }

    static bool IsOurs(IntPtr h, IntPtr root)
    {
        while (h != IntPtr.Zero)
        {
            if (h == root) return true;
            h = GetParent(h);
        }
        return false;
    }

    IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam.ToInt32() == WM_LBUTTONDOWN)
            {
                if (holdReleaseTimer != null) holdReleaseTimer.Stop();
                holdRightActive = false;
                POINT p;
                p.X = Marshal.ReadInt32(lParam, 0);
                p.Y = Marshal.ReadInt32(lParam, 4);
                IntPtr hit = WindowFromPoint(p);
                if (hit != IntPtr.Zero && !IsOurs(hit, Handle))
                {
                    // 用户点了键盘以外的位置（例如又点了一次输入框）→ 解除抑制
                    if (userClosed) { userClosed = false; RefreshTrayText(); }
                }
            }
        }
        catch { }
        if (nCode >= 0 && stickyMode == 2)
        {
            int msg2 = wParam.ToInt32();
            POINT q;
            q.X = Marshal.ReadInt32(lParam, 0);
            q.Y = Marshal.ReadInt32(lParam, 4);
            bool insideKb = Bounds.Contains(q.X, q.Y);
            if (msg2 == WM_RBUTTONDOWN || msg2 == WM_RBUTTONUP)
            {
                if (msg2 == WM_RBUTTONDOWN)
                {
                    if (holdShift || holdCtrl || holdAlt || holdWin)
                    {
                        holdRightActive = true;
                        if (holdReleaseTimer != null) holdReleaseTimer.Stop();
                        Dbg("rbutton down during hold -> keep holds");
                    }
                }
                else
                {
                    if (holdRightActive) { holdRightActive = false; ReleaseAllHolds(); Dbg("rbutton up -> release holds"); }
                }
                if (insideKb) return (IntPtr)1;    // swallow it: no context menu over the keyboard
            }
            if (msg2 == WM_LBUTTONUP && (holdShift || holdCtrl || holdAlt || holdWin))
            {
                if (holdReleaseTimer != null) { holdReleaseTimer.Stop(); holdReleaseTimer.Start(); }
                else ReleaseAllHolds();
            }
        }
        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    static bool IsRealFullscreen(IntPtr fg)
    {
        try
        {
            if (fg == IntPtr.Zero) return false;
            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            Rectangle b = Screen.FromHandle(fg).Bounds;
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }
        catch { return false; }
    }
    // ============================ AppBar（贴底占位） ============================
    void RegisterAppBar()
    {
        if (appbarRegistered || dockClosed) return;
        try
        {
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            d.hWnd = Handle;
            d.uCallbackMessage = WM_APPBAR;
            SHAppBarMessage(ABM_NEW, ref d);
            appbarRegistered = true;
            SetAppBarPos();
        }
        catch { }
    }

    void UnregisterAppBar()
    {
        if (!appbarRegistered || dockClosed) return;
        try
        {
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            d.hWnd = Handle;
            SHAppBarMessage(ABM_REMOVE, ref d);
        }
        catch { }
        appbarRegistered = false;
    }

    // 贴底基准线 = 屏幕底边；任务栏若在底部则让开它（绝不使用 WorkingArea，避免把自己的占位算进去）
    Timer dispFix = null;
    int dispFixTries = 0;

    // 分辨率/方向刚变化时任务栏与工作区还在重排，隔几百毫秒多复算几次
    void DispFixTick(object s, EventArgs e)
    {
        try
        {
            if (dispFixTries <= 0) { dispFix.Stop(); return; }
            dispFixTries--;
            RestoreWorkArea(true);
            if (dockBottom) { RegisterAppBar(); SetAppBarPos(); }
            Dbg("disp fix -> workarea refreshed (" + dispFixTries + " left)");
        }
        catch { }
    }
    int AppBarBottom(Rectangle b)
    {
        int bottom = b.Bottom;
        try
        {
            APPBARDATA st = new APPBARDATA();
            st.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            uint abs = (uint)SHAppBarMessage(ABM_GETSTATE, ref st);
            if ((abs & 0x1) != 0) return b.Bottom;    // taskbar auto-hidden (tablet mode) -> flush to the screen bottom
            APPBARDATA tb = new APPBARDATA();
            tb.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            if (SHAppBarMessage(ABM_GETTASKBARPOS, ref tb) != IntPtr.Zero)
                if (tb.uEdge == ABE_BOTTOM && tb.rc.Top > b.Top && tb.rc.Top < b.Bottom && (b.Bottom - tb.rc.Top) <= 200) bottom = tb.rc.Top;
        }
        catch { }
        return bottom;
    }

    void SetAppBarPos()
    {
        if (!appbarRegistered || dockClosed) return;
        try
        {
            Screen scr = Screen.FromControl(this);
            Rectangle b = scr.Bounds;                    // ★ 用整屏
            int h = (dockHeight > 100) ? dockHeight : Height;

            // ★ 底部基准 = 屏幕底边；若任务栏也在底部则让开它。
            //   绝不能拿 ABM_QUERYPOS 的返回值来反推（它会把"我自己已占的位置"算进去 → 键盘会一路往上跳）
            int bottom = AppBarBottom(b);

            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            d.hWnd = Handle;
            d.uEdge = ABE_BOTTOM;
            d.rc = new RECT();
            d.rc.Left = b.Left; d.rc.Right = b.Right; d.rc.Bottom = bottom; d.rc.Top = bottom - h;
            SHAppBarMessage(ABM_QUERYPOS, ref d);        // 走个协商流程，但结果不采用
            d.rc.Left = b.Left; d.rc.Right = b.Right; d.rc.Bottom = bottom; d.rc.Top = bottom - h;
            SHAppBarMessage(ABM_SETPOS, ref d);
            // ★ 用自己算好的矩形定位窗口；不要用 SETPOS 回填的 rc
            //   （系统回填时会把我自己已有的占位再算一次，导致键盘被顶到 716-664=52 那种位置）
            Rectangle put = new Rectangle(b.Left, bottom - h, b.Width, h);
            appbarTarget = put;
            Dbg(String.Format("appbar: screen={0}x{1} h={2} bottom={3} -> request=({4},{5})-({6},{7})  put={8}", b.Width, b.Height, h, bottom, d.rc.Left, d.rc.Top, d.rc.Right, d.rc.Bottom, put));
            Bounds = put;
            // ★ 外壳可能因为"陈旧的 AppBar 占位"把我们叠到它上面（表现为键盘跑到半空）。
            //   这里直接强制就位，位置以我们自己算的为准。
            SetWindowPos(Handle, new IntPtr(-1), put.Left, put.Top, put.Width, put.Height, SWP_NOACTIVATE);
            // ★ 外壳往往在我们登记后几十毫秒才"A重新摆放"，所以再延迟复 assert 几次，确保停在底部
            appbarFixTries = 6;
            appbarFix.Stop();
            appbarFix.Start();
        }
        catch { }
    }

    void AppBarFixTick(object s, EventArgs e)
    {
        try
        {
            if (!dockBottom || appbarFixTries <= 0)
            {
                appbarFix.Stop();
                settling = false;
                if (appbarPendingShow) { appbarPendingShow = false; Show(); BringToFront(); }
                return;
            }
            appbarFixTries--;
            Bounds = appbarTarget;
            SetWindowPos(Handle, new IntPtr(-1), appbarTarget.Left, appbarTarget.Top, appbarTarget.Width, appbarTarget.Height, SWP_NOACTIVATE);
            Dbg("appbar fix -> " + Bounds);
            // ★ 第一拍（≈120ms）就显示，之后的复 assert 是"原地重复"，看不见，所以既快又稳
            if (appbarPendingShow) { appbarPendingShow = false; settling = false; Show(); BringToFront(); Dbg("shown at " + Bounds); }
            if (appbarFixTries <= 0) appbarFix.Stop();
        }
        catch { }
    }

    // 退出/取消贴底后，主动把工作区恢复成"整屏去掉任务栏"，并广播给所有窗口
    void RestoreWorkArea(bool force)
    {
        try
        {
            Rectangle b = ScreenBounds();
            int bottom = b.Bottom;
            APPBARDATA tb = new APPBARDATA();
            tb.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            if (SHAppBarMessage(ABM_GETTASKBARPOS, ref tb) != IntPtr.Zero)
                if (tb.uEdge == ABE_BOTTOM && tb.rc.Top > b.Top && tb.rc.Top < b.Bottom && (b.Bottom - tb.rc.Top) <= 200) bottom = tb.rc.Top;   // 旋转瞬间可能返回陈旧矩形，任务栏不可能有 200px 以上
            RECT cur = new RECT();
            SystemParametersInfo(SPI_GETWORKAREA, 0, ref cur, 0);
            if (!force && cur.Left == b.Left && cur.Top == b.Top && cur.Right == b.Right && cur.Bottom == bottom) return;  // 已经正常就别动它
            RECT r = new RECT();
            r.Left = b.Left; r.Top = b.Top; r.Right = b.Right; r.Bottom = bottom;
            SystemParametersInfo(SPI_SETWORKAREA, 0, ref r, SPIF_SENDCHANGE);   // SPIF_SENDCHANGE 本身就会广播，无需再手动广播
            Dbg(String.Format("workarea restored -> {0},{1}-{2},{3}", r.Left, r.Top, r.Right, r.Bottom));
        }
        catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // rollback: raw touch not registered (mouse promotion path only)
    }

    // ============================ 窗口消息 ============================
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_APPBAR)
        {
            uint code = (uint)m.WParam.ToInt32();
            if (code == 1) { SetAppBarPos(); }                                  // ABN_POSCHANGED
            else if (code == 2)                                                  // ABN_FULLSCREENAPP
            {
                bool fs = (m.LParam != IntPtr.Zero) && IsRealFullscreen(GetForegroundWindow());
                if (fs) { UnregisterAppBar(); if (Visible && dockBottom) Hide(); }   // 全屏程序：让出整屏
                else if (dockBottom) { Show(); RegisterAppBar(); }
            }
            return;
        }
        if (m.Msg == WM_TOUCH)
        {
            try { HandleTouch(m.LParam, (int)(m.WParam.ToInt64() & 0xFFFF)); } catch { }
            base.WndProc(ref m);
            return;                       // promoted mouse messages are filtered out for key buttons
        }
        if (debugLog && (m.Msg == WM_TOUCHTEST_DOWN || m.Msg == WM_TOUCHTEST_UP))
        {
            int tx = m.LParam.ToInt32() & 0xFFFF, ty = (m.LParam.ToInt32() >> 16) & 0xFFFF;
            try { RouteTouch((uint)m.WParam.ToInt32(), PointToScreen(new Point(tx, ty)), m.Msg == WM_TOUCHTEST_DOWN ? 0 : 2); } catch { }
            return;
        }        if (m.Msg == 0x007E)   // WM_DISPLAYCHANGE：分辨率或屏幕方向变了（例如旋转平板）
        {
            try
            {
                RestoreWorkArea(true);                      // ★ 强制按新分辨率重算工作区（否则会留着横向的死值）
                if (dockBottom) { RegisterAppBar(); SetAppBarPos(); }
                else if (Visible)
                {
                    Rectangle sb = ScreenBounds();   // 旋转后把窗口拉回屏幕内
                    int x = Math.Min(Math.Max(Left, sb.Left), Math.Max(sb.Left, sb.Right - Width));
                    int y = Math.Min(Math.Max(Top, sb.Top), Math.Max(sb.Top, sb.Bottom - Height));
                    Location = new Point(x, y);
                }
                dispFixTries = 6; dispFix.Stop(); dispFix.Start();   // 等外壳摆稳后再复算几次
                Dbg("display change -> workarea refreshed");
            }
            catch { }
        }
        if (m.Msg == WM_NCHITTEST && !building)
        {
            base.WndProc(ref m);
            int r = m.Result.ToInt32();
            if (r == 1 /*HTCLIENT*/ && !dockBottom)
            {
                Point p = PointToClient(new Point(m.LParam.ToInt32()));
                int b = 8;
                bool L = p.X < b, R = p.X >= ClientSize.Width - b, T = p.Y < b, B = p.Y >= ClientSize.Height - b;
                if (L && T) m.Result = (IntPtr)HTTOPLEFT;
                else if (R && T) m.Result = (IntPtr)HTTOPRIGHT;
                else if (L && B) m.Result = (IntPtr)HTBOTTOMLEFT;
                else if (R && B) m.Result = (IntPtr)HTBOTTOMRIGHT;
                else if (L) m.Result = (IntPtr)HTLEFT;
                else if (R) m.Result = (IntPtr)HTRIGHT;
                else if (T) m.Result = (IntPtr)HTTOP;
                else if (B) m.Result = (IntPtr)HTBOTTOM;
                else if (p.Y < stripH) { m.Result = (IntPtr)HTCAPTION; }
            }
            return;
        }
        if (m.Msg == WM_EXITSIZEMOVE)
        {
            Dbg(String.Format("EXITSIZEMOVE: dock={0} bounds={1}", dockBottom, Bounds));
            SnapToNearestEdge();
            BuildKeys();
            LayoutHeaderButtons();
            Invalidate();
            SaveSettings();
        }
        base.WndProc(ref m);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        Dbg(String.Format("moved -> {0}  dock={1} appbar={2}", Bounds, dockBottom, appbarRegistered));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (building || strip == null) return;
        BuildKeys();
        LayoutHeaderButtons();
        Invalidate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { if (!dockBottom) restoreBounds = Bounds; SaveSettings(); } catch { }
        try { HidePreview(); } catch { }
        try { UnregisterAppBar(); RestoreWorkArea(false); } catch { }
        try { UnregisterTouchWindow(Handle); } catch { }
        try { if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; } if (kbHook != IntPtr.Zero) { UnhookWindowsHookEx(kbHook); kbHook = IntPtr.Zero; } } catch { }
        try { if (tray != null) tray.Visible = false; } catch { }
        base.OnFormClosing(e);
    }

    // ============================ 入口 ============================
    [STAThread]
    public static void Main()
    {
        bool created = false;
        System.Threading.Mutex mtx = new System.Threading.Mutex(true, "TouchKeyboard_SingleInstance", out created);
        if (!created)
        {
            MessageBox.Show("触控键盘已在运行（右下角托盘图标）。", "触控键盘", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        debugLog = GetArgStatic("-debug");
        if (debugLog)
        {
            try
            {
                logPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "kb-debug.log");
                File.WriteAllText(logPath, "=== 启动 " + DateTime.Now.ToString("HH:mm:ss") + " ===\r\n", Encoding.UTF8);
            }
            catch { debugLog = false; }
        }
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e) { HideTrayNow(); };
        AppDomain.CurrentDomain.ProcessExit += delegate(object s, EventArgs e) { HideTrayNow(); };
        Application.ApplicationExit += delegate(object s, EventArgs e) { HideTrayNow(); };
        Application.Run(new TouchKeyboard());
        GC.KeepAlive(mtx);
    }
}
