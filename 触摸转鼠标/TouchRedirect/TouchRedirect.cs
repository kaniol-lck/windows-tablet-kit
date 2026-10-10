// TouchRedirect —— 一个程序，两种触摸解决方案，按前台游戏自动选最省事的那种
//
//   [convert]  列表：**改写触摸派生的鼠标消息**（吞掉带 0xFF515700 签名的点击，重注入干净的鼠标事件）。
//                    适用：游戏走鼠标消息通路、只是忽略触摸签名或不读鼠标消息（例：Stacklands）。
//                    优点：不需要任何特权，其他程序完全不受影响。
//
//   [redirect] 列表：**全局触摸重定向**（拿到 UI Access 后 RegisterPointerInputTarget(PT_TOUCH)，
//                    把整个桌面的触摸先收到自己窗口，再转成真实鼠标事件）。
//                    适用：引擎自己处理 WM_POINTER*、Windows 不再做提升的程序（例：Unreal 的叛逆神魂）。
//
// 两种模式互斥，由前台进程决定：名单外一律不插手（触摸保持原生）。暂停/退出都会还原。
//
// 另外：redirect 模式下可以隐藏系统光标（选项 hidecursor），免得模拟出来的鼠标指针在手指底下晃。
//
// 配置与日志：%LOCALAPPDATA%\TouchRedirect\（games.ini / TouchRedirect.log）
// 需要 UI Access → 必须由 install.ps1 签名并装到 Program Files 后运行。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TouchRedirect
{
    internal static class Native
    {
        // ---- 指针 ----
        internal const int PT_TOUCH = 2;
        internal const int WM_POINTERUPDATE = 0x0245;
        internal const int WM_POINTERDOWN = 0x0246;
        internal const int WM_POINTERUP = 0x0247;
        internal const int WM_POINTERCAPTURECHANGED = 0x024C;

        // ---- 鼠标消息 / 注入 ----
        internal const int WH_MOUSE_LL = 14;
        internal const int WM_MOUSEMOVE = 0x0200;
        internal const int WM_LBUTTONDOWN = 0x0201;
        internal const int WM_LBUTTONUP = 0x0202;
        internal const int WM_RBUTTONDOWN = 0x0204;
        internal const int WM_RBUTTONUP = 0x0205;

        internal const uint MOUSEEVENTF_MOVE = 0x0001;
        internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
        internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        internal const int SM_XVIRTUALSCREEN = 76;
        internal const int SM_YVIRTUALSCREEN = 77;
        internal const int SM_CXVIRTUALSCREEN = 78;
        internal const int SM_CYVIRTUALSCREEN = 79;

        internal const uint TOUCH_SIGNATURE = 0xFF515700;
        internal const uint SIGNATURE_MASK = 0xFFFFFF00;

        // ---- 光标 ----
        internal const uint SPI_SETCURSORS = 0x0057;

        // ---- token ----
        internal const int TokenUIAccess = 26;
        internal const uint TOKEN_QUERY = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterPointerInputTarget(IntPtr hwnd, int pointerType);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnregisterPointerInputTarget(IntPtr hwnd, int pointerType);

        [DllImport("user32.dll")]
        internal static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetModuleHandle(string lpModuleName);

        // 隐藏/恢复系统光标
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight,
                                                   byte[] pvANDPlane, byte[] pvXORPlane);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetSystemCursor(IntPtr hcur, uint id);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr LoadCursorFromFile(string lpFileName);

        // 让窗口声明"我这里没有光标"（WM_SETCURSOR 里配合 SetCursor(NULL)）
        [DllImport("user32.dll")]
        internal static extern IntPtr SetCursor(IntPtr hCursor);

        // SetSystemCursor 会销毁传进去的句柄，所以要传副本；CopyCursor 是宏，实际调用 CopyIcon
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CopyIcon(IntPtr hIcon);

        // ---- 跨进程：置空窗口类光标 / 改写导入表 ----
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        internal static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        [DllImport("user32.dll")]
        internal static extern bool DestroyCursor(IntPtr hCursor);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr hProcess, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
                                                       out int tokenInformation, int tokenInformationLength,
                                                       out int returnLength);
    }

    internal sealed class HiddenWindow : Form
    {
        internal HiddenWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            Text = "TouchRedirect";
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // WM_SETCURSOR：声明本窗口没有光标。
            // 触摸时触点是**被这个隐藏窗口捕获**的，Windows 可能因此按我们窗口的光标来画指针
            // —— 显式回 WM_SETCURSOR + SetCursor(NULL) 就能避免"手指底下冒出一个箭头"。
            if (m.Msg == 0x0020)
            {
                Native.SetCursor(IntPtr.Zero);
                m.Result = (IntPtr)1;   // TRUE：已处理，不要再让系统设类光标
                Program.OnSetCursorMessage();
                return;
            }
            Program.OnPointerMessage(ref m);
            base.WndProc(ref m);
        }
    }

    internal sealed class TestForm : Form
    {
        internal int Down;
        internal int Up;
        internal int DblClk;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0201) Down++;
            else if (m.Msg == 0x0202) Up++;
            else if (m.Msg == 0x0203) DblClk++;
            base.WndProc(ref m);
        }
    }

    internal sealed class Work
    {
        internal string Log = null;
        internal uint ButtonFlags = 0;
        internal Native.POINT Pt;
        internal bool HasPoint = false;
    }

    internal static class Program
    {
        // ---- 配置 ----
        private static string _dataDir;
        private static string _cfgPath;
        private static string _logPath;
        private static readonly List<string> _convertList = new List<string>();
        private static readonly List<string> _redirectList = new List<string>();
        private static int _hideCursorMode = 0;         // 0=不隐藏 1=redirect 游戏里一直隐藏 2=只在手指按下时隐藏
        private static DateTime _cfgStamp = DateTime.MinValue;
        private static bool _cfgLoaded = false;

        // ---- 运行状态 ----
        private static HiddenWindow _win;
        private static NotifyIcon _tray;
        private static MenuItem _stateItem;
        private static MenuItem _pauseItem;
        private static Timer _poll;

        private static bool _paused = false;
        private static bool _registered = false;         // redirect 是否生效
        private static bool _convertNow = false;         // convert 是否生效
        private static int _registerRetryAt = 0;
        private static string _fg = "";

        private static int _activePointerId = 0;
        private static int _uiAccess = -1;
        private static int _cRedirectClicks = 0;
        private static int _cConvertSwallow = 0;
        private static int _cInjectErrors = 0;
        private static bool _cursorHidden = false;
        private static int _cursorRestoreAt = 0;         // hidecursor=2/3 时，到这个时刻之后恢复光标
        private static int _lastSetCursorLog = 0;

        // ---- convert 的钩子 ----
        private static IntPtr _hook = IntPtr.Zero;
        private static Native.LowLevelMouseProc _hookProc = null;
        private static readonly Queue<Work> _queue = new Queue<Work>();
        private static readonly object _queueLock = new object();
        private static Timer _pump;
        private static bool _leftDownFromTouch = false;
        private static bool _selfTest = false;
        private static int _selfSeenDown = 0;
        private const uint SELFTEST_MAGIC = 0xF0F0F0F0;

        [STAThread]
        private static int Main(string[] args)
        {
            bool once = false;
            bool restoreOnly = false;
            int patchTestPid = 0;
            string dataDirOverride = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "-once") once = true;
                else if (a == "-selftest") _selfTest = true;
                else if (a == "-datadir" && i + 1 < args.Length) dataDirOverride = args[++i];
                else if (a == "-restorecursor") restoreOnly = true;   // 应急：把系统光标恢复回来
                else if (a == "-patchtest" && i + 1 < args.Length)
                {
                    int tp;
                    if (int.TryParse(args[++i], out tp)) patchTestPid = tp;   // 诊断：拿指定进程试一遍"拦光标"，随后还原
                }
            }

            try { Native.SetProcessDPIAware(); }
            catch { }

            _dataDir = (dataDirOverride != null && dataDirOverride.Length > 0)
                ? dataDirOverride
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TouchRedirect");
            _cfgPath = Path.Combine(_dataDir, "games.ini");
            _logPath = Path.Combine(_dataDir, "TouchRedirect.log");
            EnsureDataFiles();

            // 上次异常退出可能把光标留在隐藏状态，这里无条件恢复一次（无害）
            RestoreCursors("启动兜底");
            if (restoreOnly) return 0;
            if (patchTestPid != 0)
            {
                Log("=== -patchtest 诊断：目标 pid=" + patchTestPid + " ===");
                SuppressTargetCursor(patchTestPid);
                UndoSuppressCursor();
                Log("=== -patchtest 结束（已还原）===");
                return 0;
            }

            _uiAccess = ReadUiAccess();
            Log("=== TouchRedirect 启动 ===");
            Log("exe = " + Application.ExecutablePath);
            Log("TokenUIAccess = " + _uiAccess + (_uiAccess == 1 ? "（有 UI Access）" : "（没有 UI Access —— redirect 会注册失败）"));
            LoadConfig(true);

            _win = new HiddenWindow();
            Log("隐藏窗口 hwnd = 0x" + _win.Handle.ToInt64().ToString("X"));

            if (once)
            {
                Log("-once：当前前台 = " + ForegroundProc()
                    + "，convert 匹配 = " + MatchList(_convertList, ForegroundProc())
                    + "，redirect 匹配 = " + MatchList(_redirectList, ForegroundProc()));
                return 0;
            }

            InstallHook();

            _pump = new Timer();
            _pump.Interval = 8;
            _pump.Tick += delegate { PumpQueue(); };
            _pump.Start();

            if (_selfTest)
            {
                int rc = RunSelfTest();
                Cleanup();
                return rc;
            }

            BuildTray();

            _poll = new Timer();
            _poll.Interval = 500;
            _poll.Tick += delegate { Poll(); };
            _poll.Start();

            Application.ApplicationExit += delegate { Cleanup(); };
            Application.Run(new ApplicationContext());
            Cleanup();
            return 0;
        }

        // ---------- 配置 ----------
        private static void EnsureDataFiles()
        {
            try
            {
                if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);
                if (!File.Exists(_cfgPath))
                {
                    // 旧版（v1）用的是 games.txt，一行一个进程名 → 迁进 [redirect]
                    List<string> legacy = new List<string>();
                    try
                    {
                        string old = Path.Combine(_dataDir, "games.txt");
                        if (File.Exists(old))
                        {
                            foreach (string raw in File.ReadAllLines(old, Encoding.UTF8))
                            {
                                string s = raw.Trim();
                                if (s.Length == 0 || s.StartsWith(";") || s.StartsWith("#")) continue;
                                if (!legacy.Contains(s)) legacy.Add(s);
                            }
                        }
                    }
                    catch { }

                    List<string> lines = new List<string>();
                    lines.Add("; TouchRedirect 配置：两张内置列表 + 选项。改完在托盘菜单点「重新加载配置」即可，不用重启。");
                    lines.Add("; 一行一个进程名（写 xxx.exe 也行），支持 * 和 ? 通配，; 或 # 开头是注释。");
                    lines.Add("; 名单外的程序一律不插手，触摸保持原生（双指缩放、长按右键都还在）。");
                    lines.Add("");
                    lines.Add("; ---- 转换模式：改写触摸派生的鼠标消息（不需要特权，最省事）----");
                    lines.Add("; 适用：游戏走鼠标消息通路、只是忽略触摸签名或不读鼠标消息");
                    lines.Add("[convert]");
                    lines.Add("stacklands");
                    lines.Add("");
                    lines.Add("; ---- 重定向模式：全局触摸重定向（需要 UI Access，治引擎自己吃掉指针输入的游戏）----");
                    lines.Add("; 适用：Unreal 引擎等自己处理 WM_POINTER* 的游戏");
                    lines.Add("[redirect]");
                    lines.Add("godsoul*");
                    for (int i = 0; i < legacy.Count; i++)
                    {
                        string low = legacy[i].ToLowerInvariant();
                        if (low.EndsWith(".exe")) low = low.Substring(0, low.Length - 4);
                        if (low == "stacklands" || low == "godsoul*") continue;   // 已预置，别重复
                        lines.Add(legacy[i]);
                    }
                    lines.Add("");
                    lines.Add("; ---- 选项 ----------------");
                    lines.Add("; hidecursor: 0=不隐藏（默认：靠 suppresscursor 拦游戏自己的光标就够了）");
                    lines.Add(";            1=在该游戏里一直隐藏系统光标  2=只在手指按住时隐藏  3=按住时隐藏、松开约2秒后恢复");
                    lines.Add("; 注意：本机 SPI_SETCURSORS 失效，隐藏后只能用原始 .cur 恢复，会丢掉可访问性指针的缩放与颜色，");
                    lines.Add(";       所以默认不用它；真要修外观，见 修复光标外观.cmd");
                    lines.Add("[options]");
                    lines.Add("hidecursor=0");
                    lines.Add("; suppresscursor: 1=拦掉目标游戏自己的光标（默认）  0=不拦");
                    lines.Add("suppresscursor=1");
                    lines.Add("");
                    File.WriteAllLines(_cfgPath, lines.ToArray(), new UTF8Encoding(false));
                    if (legacy.Count > 0) Log("已从旧 games.txt 迁入 " + legacy.Count + " 条到 [redirect]");
                }
            }
            catch { }
        }

        private static void LoadConfig(bool force)
        {
            try
            {
                DateTime stamp = File.GetLastWriteTimeUtc(_cfgPath);
                if (!force && _cfgLoaded && stamp == _cfgStamp) return;
                _cfgStamp = stamp;
                _cfgLoaded = true;

                _convertList.Clear();
                _redirectList.Clear();
                _hideCursorMode = 1;

                string section = "redirect";     // 兼容旧格式：没有分节时就当 redirect 列表
                if (File.Exists(_cfgPath))
                {
                    foreach (string raw in File.ReadAllLines(_cfgPath, Encoding.UTF8))
                    {
                        string s = raw.Trim();
                        if (s.Length == 0 || s.StartsWith(";") || s.StartsWith("#")) continue;
                        if (s.StartsWith("[") && s.EndsWith("]"))
                        {
                            section = s.Substring(1, s.Length - 2).Trim().ToLowerInvariant();
                            continue;
                        }
                        if (section == "options")
                        {
                            int eq = s.IndexOf('=');
                            if (eq > 0)
                            {
                                string k = s.Substring(0, eq).Trim().ToLowerInvariant();
                                string v = s.Substring(eq + 1).Trim();
                                if (k == "hidecursor")
                                {
                                    int n;
                                    if (int.TryParse(v, out n) && n >= 0 && n <= 3) _hideCursorMode = n;
                                }
                                else if (k == "suppresscursor")
                                {
                                    int n;
                                    if (int.TryParse(v, out n)) _suppressOn = (n != 0);
                                }
                            }
                            continue;
                        }
                        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
                        s = s.ToLowerInvariant();
                        if (section == "convert") _convertList.Add(s);
                        else _redirectList.Add(s);
                    }
                }
                Log("配置已加载  convert=[" + Join(_convertList) + "]  redirect=[" + Join(_redirectList)
                    + "]  hidecursor=" + _hideCursorMode + "  suppresscursor=" + (_suppressOn ? 1 : 0));
            }
            catch (Exception ex) { Log("读配置失败：" + ex.Message); }
        }

        private static string Join(List<string> l)
        {
            return l.Count == 0 ? "空" : string.Join(", ", l.ToArray());
        }

        private static void Log(string s)
        {
            try
            {
                FileInfo fi = new FileInfo(_logPath);
                if (fi.Exists && fi.Length > 512 * 1024) File.Delete(_logPath);
                File.AppendAllText(_logPath,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + s + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static int ReadUiAccess()
        {
            IntPtr tok;
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_QUERY, out tok)) return -1;
            try
            {
                int value;
                int ret;
                if (Native.GetTokenInformation(tok, Native.TokenUIAccess, out value, sizeof(int), out ret)) return value;
                return -1;
            }
            finally { Native.CloseHandle(tok); }
        }

        private static string ForegroundProc()
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == 0) return "";
            try
            {
                using (Process p = Process.GetProcessById((int)pid)) return p.ProcessName.ToLowerInvariant();
            }
            catch { return ""; }
        }

        private static int ForegroundPid()
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            return (int)pid;
        }

        private static bool MatchList(List<string> list, string proc)
        {
            if (proc.Length == 0) return false;
            for (int i = 0; i < list.Count; i++) if (Wildcard(list[i], proc)) return true;
            return false;
        }

        private static bool Wildcard(string pattern, string text)
        {
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
                return string.Equals(pattern, text, StringComparison.Ordinal);
            int p = 0, t = 0, star = -1, mark = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t])) { p++; t++; }
                else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
                else if (star >= 0) { p = star + 1; t = ++mark; }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        // ---------- 状态机：按前台进程决定用哪个模式 ----------
        private static void Poll()
        {
            LoadConfig(false);
            string fg = ForegroundProc();
            bool changed = fg != _fg;
            _fg = fg;

            bool wantRedirect = !_paused && MatchList(_redirectList, fg);
            bool wantConvert = !_paused && MatchList(_convertList, fg);
            if (wantRedirect && wantConvert) wantConvert = false;   // 同时命中时优先重定向

            // 重定向生效时不会产生"触摸派生的鼠标消息"，所以转换模式必须在未注册时才可能工作
            _convertNow = wantConvert && !_registered;

            if (wantRedirect != _registered)
            {
                if (wantRedirect)
                {
                    if (unchecked(Environment.TickCount - _registerRetryAt) >= 0)
                    {
                        if (Native.RegisterPointerInputTarget(_win.Handle, Native.PT_TOUCH))
                        {
                            _registered = true;
                            Log("重定向已注册（前台=" + fg + "）");
                            if (_hideCursorMode == 1) HideSystemCursor();
                            SuppressTargetCursor(ForegroundPid());
                        }
                        else
                        {
                            int err = Marshal.GetLastWin32Error();
                            _registerRetryAt = Environment.TickCount + 5000;
                            Log("重定向注册失败 err=" + err + "（前台=" + fg + "）—— 5 秒后重试");
                        }
                    }
                }
                else
                {
                    ReleaseActivePointer();
                    Native.UnregisterPointerInputTarget(_win.Handle, Native.PT_TOUCH);
                    _registered = false;
                    _convertNow = wantConvert;
                    UndoSuppressCursor();
                    if (_cursorHidden) RestoreCursors("注销重定向");
                    Log("重定向已注销（前台=" + fg + "）");
                }
                changed = true;
            }

            // hidecursor=2/3：手指抬起后（3 再多留 1 秒）把光标还回来 —— 这样触摸板照常看得见指针
            if (_cursorHidden && _hideCursorMode >= 2 && _activePointerId == 0
                && unchecked(Environment.TickCount - _cursorRestoreAt) >= 0)
            {
                RestoreCursors("触摸结束");
                changed = true;
            }

            // 周期性复拦：游戏重新设置类光标（例如进入加载状态时的沙漏）时再次置空
            if (_registered && _suppressOn) NullClassCursors(ForegroundPid());

            if (changed) UpdateStateItem();
        }

        private static void ReleaseActivePointer()
        {
            if (_activePointerId != 0)
            {
                InjectMouse(Native.MOUSEEVENTF_LEFTUP, 0, 0, IntPtr.Zero);
                _activePointerId = 0;
                if (_hideCursorMode >= 2) _cursorRestoreAt = Environment.TickCount;
                Log("（注销前补发左键抬起，避免按键卡住）");
            }
        }

        // ---------- redirect：触摸消息 → 鼠标 ----------
        internal static void OnSetCursorMessage()
        {
            int now = Environment.TickCount;
            if (unchecked(now - _lastSetCursorLog) > 5000)
            {
                _lastSetCursorLog = now;
                Log("隐藏窗口收到 WM_SETCURSOR → 已声明本窗口无光标（说明系统确实在向本窗口要光标）");
            }
        }

        internal static void OnPointerMessage(ref Message m)
        {
            if (m.Msg == Native.WM_POINTERCAPTURECHANGED)
            {
                ReleaseActivePointer();
                return;
            }
            if (!_registered) return;

            long lp = m.LParam.ToInt64();
            int x = (short)(lp & 0xFFFF);
            int y = (short)((lp >> 16) & 0xFFFF);
            int id = (int)(m.WParam.ToInt64() & 0xFFFF);

            if (m.Msg == Native.WM_POINTERDOWN)
            {
                if (_activePointerId == 0)                   // 只用第一个触点
                {
                    _activePointerId = id;
                    if (_hideCursorMode >= 2) HideSystemCursor();
                    MoveCursor(x, y);
                    InjectMouse(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, IntPtr.Zero);
                    _cRedirectClicks++;
                    Log("触摸 (" + x + "," + y + ") → 左键按下（系统光标已隐藏=" + _cursorHidden + "）");
                }
            }
            else if (m.Msg == Native.WM_POINTERUPDATE)
            {
                if (id == _activePointerId) MoveCursor(x, y);
            }
            else if (m.Msg == Native.WM_POINTERUP)
            {
                if (id == _activePointerId)
                {
                    MoveCursor(x, y);
                    InjectMouse(Native.MOUSEEVENTF_LEFTUP, 0, 0, IntPtr.Zero);
                    _activePointerId = 0;
                    // 2 = 立刻还回来；3 = 再多留 1 秒（连点时不闪）
                    if (_hideCursorMode >= 2)
                        _cursorRestoreAt = Environment.TickCount + (_hideCursorMode >= 3 ? 2000 : 0);
                }
            }
        }

        private static void MoveCursor(int x, int y)
        {
            Native.SetCursorPos(x, y);
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            if (vw < 2) vw = 2;
            if (vh < 2) vh = 2;
            int ax = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
            int ay = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
            InjectMouse(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, ax, ay, IntPtr.Zero);
        }

        private static void InjectMouse(uint flags, int dx, int dy, IntPtr extra)
        {
            Native.INPUT[] arr = new Native.INPUT[1];
            arr[0].type = 0;
            arr[0].mi.dx = dx;
            arr[0].mi.dy = dy;
            arr[0].mi.dwFlags = flags;
            arr[0].mi.dwExtraInfo = extra;
            if (Native.SendInput(1, arr, Marshal.SizeOf(typeof(Native.INPUT))) == 0)
            {
                _cInjectErrors++;
                Log("SendInput 失败 err=" + Marshal.GetLastWin32Error());
            }
        }

        // ---------- 隐藏/恢复系统光标 ----------
        // 实测坑（2026-10-06）：
        //   1) SetSystemCursor(h, 0) 的"全部替换"在本机失败（err=203）→ 只能逐个替换 OCR_*；
        //   2) SPI_SETCURSORS 在本机返回 False → 恢复必须再按 HKCU\Control Panel\Cursors 的方案文件逐个回写；
        //   3) 只替换 3 种（箭头/工字形/手形）不够：游戏可能给窗体设别的系统光标（如十字准星），那种仍显示原图
        //      → 现在替换方案里定义的全部 OCR_*（本机 Windows Aero 17 种都有文件，可完整还原）。
        // 只动"注册表里有路径"的那些，保证一定能还原；还原不了的种类一律不碰。
        private static readonly uint[] CursorIds = new uint[]
        {
            32512, 32651, 32650, 32514, 32515, 32513, 32631, 32648, 32645,
            32644, 32642, 32643, 32646, 32516, 32649, 32671, 32672
        };
        private static readonly string[] CursorNames = new string[]
        {
            "Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen", "No", "SizeNS",
            "SizeWE", "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand", "Pin", "Person"
        };

        // 性能：把 17 个光标文件在首次使用时读进来缓存成句柄，之后每次隐藏/恢复只做内存里的 CopyIcon
        // （原来每轮触摸都重新读 17 个 .cur/.ani，本机合计最多近 9 MB，在 eMMC 平板上会让本进程明显卡顿，
        //   而"窗口不响应"正是 Windows 画沙漏的时机之一）。
        private static IntPtr _blankCursor = IntPtr.Zero;
        private static readonly IntPtr[] _cursorCache = new IntPtr[17];
        private static readonly object _cursorLock = new object();

        private static IntPtr GetBlankCursor()
        {
            lock (_cursorLock)
            {
                if (_blankCursor != IntPtr.Zero) return _blankCursor;
                byte[] and = new byte[32 * 32 / 8];
                for (int j = 0; j < and.Length; j++) and[j] = 0xFF;   // AND=1 保留屏幕
                byte[] xor = new byte[32 * 32 / 8];                   // XOR=0 不改色 → 什么都画不出来
                _blankCursor = Native.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor);
                return _blankCursor;
            }
        }

        private static IntPtr GetCachedCursor(int i, string path)
        {
            lock (_cursorLock)
            {
                if (_cursorCache[i] != IntPtr.Zero) return _cursorCache[i];
                if (string.IsNullOrEmpty(path)) return IntPtr.Zero;
                _cursorCache[i] = Native.LoadCursorFromFile(path);
                return _cursorCache[i];
            }
        }

        private static void HideSystemCursor()
        {
            if (_cursorHidden) return;
            int ok = 0;
            int skipped = 0;
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors"))
                {
                    for (int i = 0; i < CursorIds.Length; i++)
                    {
                        string path = (k == null) ? null : (k.GetValue(CursorNames[i]) as string);
                        if (string.IsNullOrEmpty(path)) { skipped++; continue; }   // 不能还原的就不动
                        IntPtr h = Native.CopyIcon(GetBlankCursor());              // SetSystemCursor 会销毁传入句柄 → 传副本
                        if (h == IntPtr.Zero)
                        {
                            byte[] and = new byte[32 * 32 / 8];
                            for (int j = 0; j < and.Length; j++) and[j] = 0xFF;
                            byte[] xor = new byte[32 * 32 / 8];
                            h = Native.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor);
                        }
                        if (h == IntPtr.Zero) continue;
                        if (Native.SetSystemCursor(h, CursorIds[i])) ok++;
                        else Native.DestroyCursor(h);
                    }
                }
            }
            catch { }
            if (ok > 0)
            {
                _cursorHidden = true;
                Log("已隐藏系统光标（替换了 " + ok + "/" + CursorIds.Length + " 个"
                    + (skipped > 0 ? "，跳过 " + skipped + " 个方案里没定义的" : "") + "）");
            }
            else
            {
                Log("隐藏系统光标失败（SetSystemCursor 全部返回失败，err=" + Marshal.GetLastWin32Error() + "）");
            }
        }

        private static void RestoreCursors(string why)
        {
            bool viaSpi = Native.SystemParametersInfo(Native.SPI_SETCURSORS, 0, IntPtr.Zero, 0x1);
            int byReg = 0;
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors"))
                {
                    if (k != null)
                    {
                        for (int i = 0; i < CursorNames.Length; i++)
                        {
                            string path = k.GetValue(CursorNames[i]) as string;
                            if (path == null || path.Length == 0) continue;
                            IntPtr cached = GetCachedCursor(i, path);
                            IntPtr h = (cached != IntPtr.Zero) ? Native.CopyIcon(cached) : Native.LoadCursorFromFile(path);
                            if (h == IntPtr.Zero) continue;
                            if (Native.SetSystemCursor(h, CursorIds[i])) byReg++;
                            else Native.DestroyCursor(h);
                        }
                    }
                }
            }
            catch { }
            _cursorHidden = false;
            Log("已恢复系统光标（" + why + "）：SPI_SETCURSORS=" + viaSpi + "，按注册表方案回写 " + byReg + " 个");
        }

        // ---------- convert：低级鼠标钩子 ----------
        private static void InstallHook()
        {
            _hookProc = HookProc;
            _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _hookProc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Log("SetWindowsHookEx 失败 err=" + Marshal.GetLastWin32Error() + "（转换模式不可用，重定向模式不受影响）");
                return;
            }
            Log("鼠标钩子已就绪（转换模式）");
        }

        // 钩子回调只做判断 + 入队，绝不在此注入输入（那会死锁）
        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            if (!_convertNow) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

            Native.MSLLHOOKSTRUCT info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            uint extra = unchecked((uint)info.dwExtraInfo.ToInt64());
            bool isTouch = (extra & Native.SIGNATURE_MASK) == Native.TOUCH_SIGNATURE && (extra & 0x80) != 0;
            if (_selfTest && extra == SELFTEST_MAGIC) isTouch = true;
            if (!isTouch) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

            uint flags;
            bool swallow = false;
            if (TryMapButton(msg, out flags))
            {
                swallow = true;
                if (_selfTest && flags == Native.MOUSEEVENTF_LEFTDOWN) _selfSeenDown++;
                Work w = new Work();
                w.ButtonFlags = flags;
                w.Pt = info.pt;
                w.HasPoint = true;
                w.Log = "TOUCH " + MsgName(msg) + " (" + info.pt.X + "," + info.pt.Y + ") -> 改写为干净鼠标事件";
                Enqueue(w);
            }
            else if (msg == Native.WM_MOUSEMOVE && _leftDownFromTouch)
            {
                swallow = true;
                Work w = new Work();
                w.ButtonFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
                w.Pt = info.pt;
                w.HasPoint = true;
                Enqueue(w);
            }

            if (swallow)
            {
                if (flags == Native.MOUSEEVENTF_LEFTDOWN) _leftDownFromTouch = true;
                else if (flags == Native.MOUSEEVENTF_LEFTUP) _leftDownFromTouch = false;
                _cConvertSwallow++;
                return (IntPtr)1;
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool TryMapButton(int msg, out uint flags)
        {
            flags = 0;
            if (msg == Native.WM_LBUTTONDOWN) { flags = Native.MOUSEEVENTF_LEFTDOWN; return true; }
            if (msg == Native.WM_LBUTTONUP) { flags = Native.MOUSEEVENTF_LEFTUP; return true; }
            if (msg == Native.WM_RBUTTONDOWN) { flags = Native.MOUSEEVENTF_RIGHTDOWN; return true; }
            if (msg == Native.WM_RBUTTONUP) { flags = Native.MOUSEEVENTF_RIGHTUP; return true; }
            return false;
        }

        private static string MsgName(int msg)
        {
            if (msg == Native.WM_LBUTTONDOWN) return "L-DOWN";
            if (msg == Native.WM_LBUTTONUP) return "L-UP";
            if (msg == Native.WM_RBUTTONDOWN) return "R-DOWN";
            if (msg == Native.WM_RBUTTONUP) return "R-UP";
            if (msg == Native.WM_MOUSEMOVE) return "MOVE";
            return "MSG_" + msg.ToString("X4");
        }

        private static void Enqueue(Work w)
        {
            lock (_queueLock)
            {
                if (_queue.Count > 512) return;
                _queue.Enqueue(w);
            }
        }

        private static void PumpQueue()
        {
            for (int budget = 0; budget < 128; budget++)
            {
                Work w;
                lock (_queueLock)
                {
                    if (_queue.Count == 0) return;
                    w = _queue.Dequeue();
                }
                if (w.Log != null) Log(w.Log);
                if (w.ButtonFlags == 0) continue;

                if (w.HasPoint)
                {
                    if (w.ButtonFlags == (Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK))
                    {
                        MoveCursor(w.Pt.X, w.Pt.Y);
                        continue;
                    }
                    Native.POINT cur;
                    Native.GetCursorPos(out cur);
                    if (cur.X != w.Pt.X || cur.Y != w.Pt.Y) Native.SetCursorPos(w.Pt.X, w.Pt.Y);
                }
                InjectMouse(w.ButtonFlags, 0, 0, IntPtr.Zero);
            }
        }

        // ---------- 自检：不用手指，用带标记的注入事件冒充"触摸"，验证吞掉 + 重注入 ----------
        private static void Pump(int n)
        {
            for (int i = 0; i < n; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
        }

        private static void InjectAbsoluteMove(Native.POINT pt, IntPtr extra)
        {
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            if (vw < 2) vw = 2;
            if (vh < 2) vh = 2;
            int ax = (int)Math.Round((pt.X - vx) * 65535.0 / (vw - 1));
            int ay = (int)Math.Round((pt.Y - vy) * 65535.0 / (vh - 1));
            InjectMouse(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, ax, ay, extra);
        }

        private static int RunSelfTest()
        {
            _convertNow = true;                                   // 让钩子进入"转换"状态
            IntPtr magic = new IntPtr(unchecked((int)SELFTEST_MAGIC));

            TestForm f = new TestForm();
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(70, 70);
            f.Size = new Size(460, 320);
            f.Text = "TouchRedirect self-test";
            f.TopMost = true;
            f.Show();
            Pump(20);
            f.Activate();

            const int N = 3;         // 需要被钩子看到的次数
            const int MAXATT = 10;   // 系统偶尔会丢弃注入事件，重试
            int cx = f.Location.X + 220;
            int cy = f.Location.Y + 160;
            int attempts = 0;
            int seen = 0;

            while (seen < N && attempts < MAXATT)
            {
                attempts++;
                int before = _selfSeenDown;
                Native.POINT p;
                p.X = cx;
                p.Y = cy;
                InjectAbsoluteMove(p, magic);
                Pump(5);
                InjectMouse(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, magic);
                Pump(5);
                InjectMouse(Native.MOUSEEVENTF_LEFTUP, 0, 0, magic);
                Pump(45);                                          // 拉开双击间隔并等队列排空
                if (_selfSeenDown > before) seen++;
            }
            Pump(20);

            int delivered = f.Down + f.DblClk;
            // 要验证的不变量是"钩子看到几次 → 窗口就只能收到几次"；
            // 系统偶尔成批丢弃注入事件（seen 远小于 attempts）属于环境问题，不算失败。
            bool pass = seen >= 1 && delivered == seen && f.Up == seen && _cInjectErrors == 0;
            Log("自检：钩子看到的假触摸按下 = " + seen + " 次（尝试注入 " + attempts + " 次，被系统丢弃 "
                + (attempts - seen) + " 次）");
            Log("自检：测试窗口收到 按下 " + f.Down + " + 双击 " + f.DblClk + " = " + delivered + "（必须等于 " + seen + "）");
            Log("自检：测试窗口收到 抬起 " + f.Up + "（必须等于 " + seen + "）；注入失败 " + _cInjectErrors + " 次");
            Log("自检结果：" + (pass ? "PASS" : "FAIL"));
            try { f.Close(); }
            catch { }
            return pass ? 0 : 1;
        }

        // ---------- 让目标进程不再显示它自己的光标 ----------
        // 实测（2026-10-06）：GodSoul 的指针是**硬件光标**（GDI 截图里不含它）却用了游戏自己的图片 →
        // 替换系统光标表对它无效。两条外部手段，一起用：
        //   A) SetClassLongPtr(hwnd, GCLP_HCURSOR, NULL)：把窗口类光标置空（跨进程可用，不必注入代码）
        //   B) 改写目标进程主模块导入表里 user32!SetCursor 那一项，指向我们写在目标进程里的 14 字节小桩：
        //        31 C9              xor ecx, ecx          ; hCursor = NULL
        //        48 B8 <真实地址>   mov rax, <真 SetCursor>
        //        FF E0              jmp rax               ; 尾调用，栈不动
        //      于是"游戏设置自己的光标"变成"设置 NULL" → 不画任何指针。
        // 两条都保留原值，注销/暂停/退出时原样还原；改 IAT 前会校验该地址确实落在目标的 user32 模块范围内。
        private const int GCLP_HCURSOR = -12;
        private static bool _suppressOn = true;
        private static int _suppressedPid = 0;
        private static IntPtr _supProcess = IntPtr.Zero;
        private static IntPtr _supIatSlot = IntPtr.Zero;
        private static IntPtr _supIatOld = IntPtr.Zero;
        private static IntPtr _supStub = IntPtr.Zero;
        private static readonly List<IntPtr> _supClassHwnd = new List<IntPtr>();
        private static readonly List<IntPtr> _supClassOld = new List<IntPtr>();

        private static IntPtr Add(IntPtr p, long off)
        {
            return new IntPtr(p.ToInt64() + off);
        }

        private static byte[] ReadProc(IntPtr h, IntPtr addr, int size)
        {
            byte[] buf = new byte[size];
            IntPtr read;
            if (!Native.ReadProcessMemory(h, addr, buf, size, out read)) return null;
            return buf;
        }

        private static string ReadProcString(IntPtr h, IntPtr addr, int max)
        {
            byte[] buf = ReadProc(h, addr, max);
            if (buf == null) return "";
            int n = 0;
            while (n < buf.Length && buf[n] != 0) n++;
            return Encoding.ASCII.GetString(buf, 0, n);
        }

        private static bool GetModuleInfo(int pid, out IntPtr baseAddr, out long imgSize,
                                          out IntPtr user32Base, out long user32Size)
        {
            baseAddr = IntPtr.Zero; imgSize = 0; user32Base = IntPtr.Zero; user32Size = 0;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    string mainPath = p.MainModule.FileName;
                    foreach (ProcessModule m in p.Modules)
                    {
                        if (m.ModuleName.Equals("user32.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            user32Base = m.BaseAddress; user32Size = m.ModuleMemorySize;
                        }
                        if (string.Equals(m.FileName, mainPath, StringComparison.OrdinalIgnoreCase))
                        {
                            baseAddr = m.BaseAddress; imgSize = m.ModuleMemorySize;
                        }
                    }
                }
            }
            catch (Exception ex) { Log("取模块信息失败：" + ex.Message); return false; }
            return baseAddr != IntPtr.Zero && imgSize > 0 && user32Base != IntPtr.Zero;
        }

        // 在主模块导入表里找 funcName（不区分大小写）的 IAT 项地址与当前值
        private static bool FindIatEntry(IntPtr h, IntPtr baseAddr, long imgSize, string funcLower,
                                         IntPtr user32Base, long user32Size, out IntPtr slot, out IntPtr realAddr)
        {
            slot = IntPtr.Zero; realAddr = IntPtr.Zero;
            byte[] dos = ReadProc(h, baseAddr, 0x40);
            if (dos == null) return false;
            int lfanew = BitConverter.ToInt32(dos, 0x3C);
            byte[] nt = ReadProc(h, Add(baseAddr, lfanew), 0x120);
            if (nt == null || nt[0] != (byte)'P' || nt[1] != (byte)'E') return false;
            ushort magic = BitConverter.ToUInt16(nt, 24);
            int ddOff = (magic == 0x20B) ? 24 + 0x70 : 24 + 0x60;      // PE32+ / PE32 的 DataDirectory 起点
            int importRva = BitConverter.ToInt32(nt, ddOff + 8);
            if (importRva == 0) return false;

            for (int i = 0; i < 512; i++)
            {
                byte[] desc = ReadProc(h, Add(baseAddr, importRva + i * 20), 20);
                if (desc == null) return false;
                int oft = BitConverter.ToInt32(desc, 0);
                int nameRva = BitConverter.ToInt32(desc, 12);
                int ft = BitConverter.ToInt32(desc, 16);
                if (nameRva == 0 && oft == 0 && ft == 0) return false;               // 描述符数组结束
                string modName = ReadProcString(h, Add(baseAddr, nameRva), 64);
                if (!string.Equals(modName, "user32.dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (oft == 0) { Log("IAT：user32 这一项没有 OriginalFirstThunk，无法按名字定位"); continue; }

                for (int k = 0; k < 8192; k++)
                {
                    byte[] intEntry = ReadProc(h, Add(baseAddr, oft + k * 8), 8);
                    if (intEntry == null) return false;
                    long v = BitConverter.ToInt64(intEntry, 0);
                    if (v == 0) break;
                    if ((v & unchecked((long)0x8000000000000000)) != 0) continue;     // 序号导入
                    string fn = ReadProcString(h, Add(baseAddr, v + 2), 64);          // 跳过 2 字节 hint
                    if (!string.Equals(fn, funcLower, StringComparison.OrdinalIgnoreCase)) continue;

                    IntPtr s = Add(baseAddr, ft + k * 8);
                    byte[] cur = ReadProc(h, s, 8);
                    if (cur == null) return false;
                    long ra = BitConverter.ToInt64(cur, 0);
                    if (ra < user32Base.ToInt64() || ra >= user32Base.ToInt64() + user32Size) return false;  // 校验
                    slot = s;
                    realAddr = new IntPtr(ra);
                    return true;
                }
            }
            return false;
        }

        private static bool PatchSetCursorIat(int pid)
        {
            const uint access = 0x0008 | 0x0010 | 0x0020 | 0x0400;   // VM_OPERATION|VM_READ|VM_WRITE|QUERY_INFORMATION
            IntPtr h = Native.OpenProcess(access, false, pid);
            if (h == IntPtr.Zero) { Log("IAT：OpenProcess 失败 err=" + Marshal.GetLastWin32Error()); return false; }
            bool keep = false;
            try
            {
                IntPtr baseAddr; long imgSize; IntPtr u32Base; long u32Size;
                if (!GetModuleInfo(pid, out baseAddr, out imgSize, out u32Base, out u32Size)) return false;

                IntPtr slot; IntPtr realAddr;
                if (!FindIatEntry(h, baseAddr, imgSize, "setcursor", u32Base, u32Size, out slot, out realAddr))
                {
                    Log("IAT：主模块导入表里没找到 user32!SetCursor（可能不是按名字导入）");
                    return false;
                }

                IntPtr stub = Native.VirtualAllocEx(h, IntPtr.Zero, new IntPtr(64), 0x1000 | 0x2000, 0x40);
                if (stub == IntPtr.Zero) { Log("IAT：VirtualAllocEx 失败 err=" + Marshal.GetLastWin32Error()); return false; }

                byte[] code = new byte[14];
                code[0] = 0x31; code[1] = 0xC9;                       // xor ecx, ecx
                code[2] = 0x48; code[3] = 0xB8;                       // mov rax, imm64
                BitConverter.GetBytes(realAddr.ToInt64()).CopyTo(code, 4);
                code[12] = 0xFF; code[13] = 0xE0;                     // jmp rax
                IntPtr written;
                if (!Native.WriteProcessMemory(h, stub, code, code.Length, out written))
                {
                    Log("IAT：写桩失败 err=" + Marshal.GetLastWin32Error());
                    Native.VirtualFreeEx(h, stub, IntPtr.Zero, 0x8000);
                    return false;
                }

                uint oldProt;
                if (!Native.VirtualProtectEx(h, slot, new IntPtr(8), 0x04, out oldProt))
                {
                    Log("IAT：VirtualProtectEx 失败 err=" + Marshal.GetLastWin32Error());
                    Native.VirtualFreeEx(h, stub, IntPtr.Zero, 0x8000);
                    return false;
                }
                byte[] newVal = BitConverter.GetBytes(stub.ToInt64());
                bool ok = Native.WriteProcessMemory(h, slot, newVal, 8, out written);
                uint ignored;
                Native.VirtualProtectEx(h, slot, new IntPtr(8), oldProt, out ignored);
                if (!ok)
                {
                    Log("IAT：改写失败 err=" + Marshal.GetLastWin32Error());
                    Native.VirtualFreeEx(h, stub, IntPtr.Zero, 0x8000);
                    return false;
                }

                _supProcess = h;
                _supIatSlot = slot;
                _supIatOld = realAddr;
                _supStub = stub;
                keep = true;
                Log("IAT 已改写：user32!SetCursor 的导入项 0x" + slot.ToInt64().ToString("X")
                    + " → 桩 0x" + stub.ToInt64().ToString("X") + "（原值 0x" + realAddr.ToInt64().ToString("X") + "）");
                return true;
            }
            finally { if (!keep) Native.CloseHandle(h); }
        }

        // 把目标进程所有顶层窗口的类光标置空。
        // 会周期性重复调用：游戏可能在加载/切场景时**重新设置自己的类光标**（沙漏就是这么冒出来的），
        // 一次性的置空挡不住它。原始值只记第一次，供还原用。
        private static int NullClassCursors(int pid)
        {
            if (pid == 0) return 0;
            List<IntPtr> hwnds = new List<IntPtr>();
            int want = pid;
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr l)
            {
                uint p;
                Native.GetWindowThreadProcessId(hwnd, out p);
                if ((int)p == want) hwnds.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            int acted = 0;
            for (int i = 0; i < hwnds.Count; i++)
            {
                IntPtr old = Native.GetClassLongPtr(hwnds[i], GCLP_HCURSOR);
                if (old == IntPtr.Zero) continue;
                if (Native.SetClassLongPtr(hwnds[i], GCLP_HCURSOR, IntPtr.Zero) != IntPtr.Zero)
                {
                    acted++;
                    if (!_supClassHwnd.Contains(hwnds[i]))
                    {
                        _supClassHwnd.Add(hwnds[i]);
                        _supClassOld.Add(old);
                    }
                }
            }
            return acted;
        }

        private static void SuppressTargetCursor(int pid)
        {
            if (!_suppressOn || pid == 0) return;
            if (_suppressedPid == pid) return;
            UndoSuppressCursor();

            int acted = NullClassCursors(pid);
            Log("类光标已置空：" + acted + " 个窗口（累计 " + _supClassHwnd.Count + " 个）");

            // 改 IAT：让游戏设置光标变成设置 NULL
            PatchSetCursorIat(pid);
            _suppressedPid = pid;
        }

        private static void UndoSuppressCursor()
        {
            if (_supProcess != IntPtr.Zero && _supIatSlot != IntPtr.Zero)
            {
                uint oldProt;
                if (Native.VirtualProtectEx(_supProcess, _supIatSlot, new IntPtr(8), 0x04, out oldProt))
                {
                    IntPtr written;
                    byte[] v = BitConverter.GetBytes(_supIatOld.ToInt64());
                    if (Native.WriteProcessMemory(_supProcess, _supIatSlot, v, 8, out written))
                        Log("IAT 已还原（SetCursor）");
                    uint ignored;
                    Native.VirtualProtectEx(_supProcess, _supIatSlot, new IntPtr(8), oldProt, out ignored);
                }
            }
            if (_supStub != IntPtr.Zero && _supProcess != IntPtr.Zero)
                Native.VirtualFreeEx(_supProcess, _supStub, IntPtr.Zero, 0x8000);
            if (_supProcess != IntPtr.Zero) { Native.CloseHandle(_supProcess); _supProcess = IntPtr.Zero; }
            _supIatSlot = IntPtr.Zero; _supIatOld = IntPtr.Zero; _supStub = IntPtr.Zero;

            if (_supClassHwnd.Count > 0)
            {
                for (int i = 0; i < _supClassHwnd.Count; i++)
                    Native.SetClassLongPtr(_supClassHwnd[i], GCLP_HCURSOR, _supClassOld[i]);
                Log("已还原窗口类光标：" + _supClassHwnd.Count + " 个");
            }
            _supClassHwnd.Clear();
            _supClassOld.Clear();
            _suppressedPid = 0;
        }

        // ---------- 托盘 ----------
        private static void BuildTray()
        {
            _stateItem = new MenuItem("状态：空闲");
            _stateItem.Enabled = false;
            _pauseItem = new MenuItem("暂停（触摸保持原生）");
            _pauseItem.Click += delegate
            {
                _paused = !_paused;
                _pauseItem.Text = _paused ? "继续" : "暂停（触摸保持原生）";
                Log(_paused ? "用户暂停" : "用户继续");
                if (_paused)
                {
                    ReleaseActivePointer();
                    if (_registered)
                    {
                        Native.UnregisterPointerInputTarget(_win.Handle, Native.PT_TOUCH);
                        _registered = false;
                        Log("重定向已注销（暂停）");
                    }
                    _convertNow = false;
                    UndoSuppressCursor();
                    if (_cursorHidden) RestoreCursors("注销重定向");
                }
                UpdateStateItem();
            };

            MenuItem reload = new MenuItem("重新加载配置");
            reload.Click += delegate { LoadConfig(true); Poll(); };
            MenuItem openCfg = new MenuItem("打开配置文件");
            openCfg.Click += delegate { TryOpen(_cfgPath); };
            MenuItem openLog = new MenuItem("打开日志");
            openLog.Click += delegate { TryOpen(_logPath); };
            MenuItem quit = new MenuItem("退出");
            quit.Click += delegate { Application.Exit(); };

            ContextMenu menu = new ContextMenu(new MenuItem[]
            {
                _stateItem, _pauseItem, reload, openCfg, openLog, quit
            });

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "TouchRedirect";
            _tray.ContextMenu = menu;
            _tray.Visible = true;
            Log("托盘图标已就绪");
            UpdateStateItem();
        }

        private static void TryOpen(string path)
        {
            try { Process.Start("notepad.exe", "\"" + path + "\""); }
            catch { }
        }

        private static void UpdateStateItem()
        {
            string mode = _paused ? "暂停"
                        : (_registered ? "重定向生效"
                        : (_convertNow ? "转换生效" : "空闲"));
            string detail = mode + "（前台=" + (_fg.Length == 0 ? "?" : _fg) + "）"
                          + " 重定向点击 " + _cRedirectClicks + " / 改写 " + _cConvertSwallow;
            if (_stateItem != null) _stateItem.Text = "状态：" + detail;
            if (_tray != null)
            {
                string tip = "TouchRedirect：" + mode + (_cursorHidden ? "（光标已隐藏）" : "");
                if (tip.Length > 62) tip = tip.Substring(0, 62);
                _tray.Text = tip;
            }
        }

        private static void Cleanup()
        {
            try
            {
                ReleaseActivePointer();
                if (_registered)
                {
                    Native.UnregisterPointerInputTarget(_win.Handle, Native.PT_TOUCH);
                    _registered = false;
                    Log("重定向已注销（退出）");
                }
                UndoSuppressCursor();
                if (_cursorHidden) RestoreCursors("注销重定向");
                if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                Log("已退出：重定向点击 " + _cRedirectClicks + " 次，转换改写 " + _cConvertSwallow
                    + " 次，注入失败 " + _cInjectErrors + " 次");
            }
            catch { }
        }
    }
}
