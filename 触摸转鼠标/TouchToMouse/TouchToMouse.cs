// TouchToMouse - 把"触摸派生"的鼠标消息改写成干净的鼠标左键（可带诊断日志）
//
// 背景：Windows 对触摸屏会把主触点"提升(promotion)"成一条带签名(0xFF515700)的旧式鼠标消息。
//   1) 有些游戏不读鼠标消息（用 RawInput / DirectInput / 自己轮询），这条消息它看不见；
//   2) 有些游戏会检查 GetMessageExtraInfo() 的触摸签名，主动忽略这类点击；
//   3) 有些引擎自己处理 WM_POINTER*，Windows 就不再提升，连鼠标消息都没有。
// 本工具用 WH_MOUSE_LL 低级鼠标钩子观察每一条鼠标消息的 dwExtraInfo：
//   - 探针模式：只记录，判定这台机器/这个游戏属于哪一类；
//   - 转换模式：把触摸派生的左键"吞掉"，再用 SendInput 重新注入一条 extraInfo=0 的干净左键
//     （注入的输入对游戏来说就是一次真实鼠标输入，RawInput/DirectInput 也能收到）。
//
// 关键实现约束（踩过的坑）：低级钩子回调里**绝对不能**调 SendInput/SetCursorPos。
//   钩子未返回时系统会等它，注入的输入排在后面 → 直接死锁。
//   所以回调只做"判断 + 入队 + return 1（吞掉）"，真正的注入和写日志都交给 UI 定时器。
//
// 编译（Windows PowerShell 5.1，C# 5 语法；本机 Add-Type 把警告当错误）：
//   Add-Type -TypeDefinition (Get-Content .\TouchToMouse.cs -Raw -Encoding UTF8) -Language CSharp `
//     -OutputAssembly .\TouchToMouse.exe -OutputType ConsoleApplication `
//     -ReferencedAssemblies System.Windows.Forms,System.Drawing

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TouchToMouse
{
    internal static class Native
    {
        internal const int WH_MOUSE_LL = 14;

        internal const int WM_MOUSEMOVE = 0x0200;
        internal const int WM_LBUTTONDOWN = 0x0201;
        internal const int WM_LBUTTONUP = 0x0202;
        internal const int WM_RBUTTONDOWN = 0x0204;
        internal const int WM_RBUTTONUP = 0x0205;
        internal const int WM_MBUTTONDOWN = 0x0207;
        internal const int WM_MBUTTONUP = 0x0208;
        internal const int WM_MOUSEWHEEL = 0x020A;
        internal const int WM_MOUSEHWHEEL = 0x020E;
        internal const int WM_LBUTTONDBLCLK = 0x0203;

        internal const uint MOUSEEVENTF_MOVE = 0x0001;
        internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
        internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        internal const int SM_XVIRTUALSCREEN = 76;
        internal const int SM_YVIRTUALSCREEN = 77;
        internal const int SM_CXVIRTUALSCREEN = 78;
        internal const int SM_CYVIRTUALSCREEN = 79;

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
        internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        internal static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();

        // 环境诊断用：进程在哪个桌面、当前"输入桌面"是哪个（不一致的话全局钩子收不到真实输入）
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetThreadDesktop(uint dwThreadId);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetProcessWindowStation();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        // 完整性级别（判断是否以管理员运行，UIPI 相关）
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr hProcess, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
    }

    internal sealed class TestForm : Form
    {        internal int Down;
        internal int Up;
        internal int DblClk;
        internal int MoveCount;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_LBUTTONDOWN) Down++;
            else if (m.Msg == Native.WM_LBUTTONUP) Up++;
            else if (m.Msg == Native.WM_LBUTTONDBLCLK) DblClk++;
            else if (m.Msg == Native.WM_MOUSEMOVE) MoveCount++;
            base.WndProc(ref m);
        }
    }

    internal sealed class Work
    {
        internal string Log = null;        // 要写进日志/控制台的一行（可为 null）
        internal uint ButtonFlags = 0;     // 非 0 = 先定位光标再注入这个按键事件
        internal bool MoveAbsolute = false;// true = 只注入一次绝对移动
        internal Native.POINT Pt;
    }

    internal static class Program
    {
        // 触摸/笔签名的官方判据：GetMessageExtraInfo() & 0xFFFFFF00 == 0xFF515700，
        // 第 0x80 位 = 1 表示触摸、0 表示笔（MSDN "System Events and Mouse Messages"）。
        private const uint TOUCH_SIGNATURE = 0xFF515700;
        private const uint SIGNATURE_MASK = 0xFFFFFF00;
        private const uint SELFTEST_MAGIC = 0xF0F0F0F0;

        private static readonly object QueueLock = new object();
        private static readonly Queue<Work> Queue = new Queue<Work>();
        private static readonly object LogLock = new object();

        private static IntPtr _hook = IntPtr.Zero;
        private static Native.LowLevelMouseProc _hookProc = null;
        private static StreamWriter _log = null;
        private static string _logPath = null;
        private static System.Windows.Forms.Timer _pump = null;

        private static bool _convert = false;
        private static bool _add = false;
        private static bool _selfTest = false;
        private static readonly List<string> _only = new List<string>();

        private static int _cMouse = 0;
        private static int _cTouch = 0;
        private static int _cTouchDown = 0;
        private static int _cPen = 0;
        private static int _cInject = 0;
        private static int _cSwallow = 0;
        private static int _cError = 0;
        private static int _cQueued = 0;
        private static bool _leftFromTouch = false;
        private static DateTime _startTime = DateTime.Now;

        [STAThread]
        private static int Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); }
            catch { }

            string dir = AppDir();
            _logPath = Path.Combine(dir, "TouchToMouse.log");
            int autoStopSeconds = 0;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "-convert") _convert = true;
                else if (a == "-add") _add = true;
                else if (a == "-injectmove") { InjectMoveChild(); return 0; }
                else if (a == "-selftest") { _selfTest = true; _convert = true; }
                else if (a == "-log" && i + 1 < args.Length) { _logPath = args[++i]; }
                else if (a == "-seconds" && i + 1 < args.Length)
                {
                    int v;
                    if (int.TryParse(args[++i], out v)) autoStopSeconds = v;
                }
                else if (a == "-only")
                {
                    // 吃掉后面所有非 - 开头的参数（支持 -only a.exe b.exe 和 -only a.exe,b.exe 两种写法）
                    for (int k = i + 1; k < args.Length; k++)
                    {
                        if (args[k].StartsWith("-")) break;
                        string[] parts = args[k].Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int m = 0; m < parts.Length; m++)
                        {
                            string s = parts[m].Trim().ToLowerInvariant();
                            if (s.EndsWith(".exe")) s = s.Substring(0, s.Length - 4);   // 允许写 xxx.exe
                            if (s.Length > 0) _only.Add(s);
                        }
                        i = k;
                    }
                }
            }

            OpenLog();
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch { }

            Banner();
            Diag();

            if (_selfTest) return RunSelfTest();

            if (!InstallHook()) return 2;

            _pump = new System.Windows.Forms.Timer();
            _pump.Interval = 8;
            _pump.Tick += PumpQueue;
            _pump.Start();

            // 心跳：证明"钩子接上了输入链"以及"当时前台是谁"。每 5 秒打一行。
            _startTime = DateTime.Now;
            System.Windows.Forms.Timer hb = new System.Windows.Forms.Timer();
            hb.Interval = 5000;
            hb.Tick += delegate
            {
                LogLine("[心跳 " + (int)(DateTime.Now - _startTime).TotalSeconds + "s] mouse=" + _cMouse
                        + " touch=" + _cTouch + " pen=" + _cPen + " fg=" + ForegroundProc());
            };
            hb.Start();

            // 钩子自检必须等消息循环真正跑起来（低级钩子回调靠本线程的消息循环送达），
            // 所以用一次性定时器延迟到 0.8s 后再做。
            System.Windows.Forms.Timer live = new System.Windows.Forms.Timer();
            live.Interval = 800;
            live.Tick += delegate
            {
                live.Stop();
                live.Dispose();
                HookLiveCheck();
            };
            live.Start();

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                Shutdown("被用户中止(Ctrl+C)");
            };

            if (autoStopSeconds > 0)
            {
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = autoStopSeconds * 1000;
                t.Tick += delegate { Shutdown("自动停止（-seconds " + autoStopSeconds + "）"); };
                t.Start();
            }

            Application.Run(new ApplicationContext());
            return 0;
        }

        private static string AppDir()
        {
            string p = null;
            try { p = System.Reflection.Assembly.GetExecutingAssembly().Location; }
            catch { }
            if (p == null || p.Length == 0) p = Application.ExecutablePath;
            string d = Path.GetDirectoryName(p);
            if (d == null || d.Length == 0) d = ".";
            return d;
        }

        private static void Banner()
        {
            Console.WriteLine("TouchToMouse —— 触摸鼠标消息观察/改写工具");
            Console.WriteLine("模式：" + (_convert ? "转换（吞掉触摸按键，再用 SendInput 重注入干净鼠标事件）" : "探针（只记录，不改动输入）"));
            if (_only.Count > 0) Console.WriteLine("仅对前台进程生效：" + string.Join(", ", _only.ToArray()));
            Console.WriteLine("日志：" + _logPath);
            Console.WriteLine("用手指点几下要测试的游戏，然后回到这个窗口按 Ctrl+C 结束并看统计。");
            Console.WriteLine(new string('-', 64));
        }

        private static bool InstallHook()
        {
            _hookProc = HookProc;
            _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _hookProc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine("SetWindowsHookEx 失败，错误码 " + err);
                LogLine("ERROR SetWindowsHookEx failed err=" + err);
                return false;
            }
            LogLine("START mode=" + (_convert ? "convert" : "probe") + " add=" + _add
                    + " only=" + string.Join(",", _only.ToArray()));
            return true;
        }

        // ---------- 钩子回调：只做判断与入队，绝不调用会阻塞/会注入的 API ----------
        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

            Native.MSLLHOOKSTRUCT info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            uint extra = 0;
            try { extra = unchecked((uint)info.dwExtraInfo.ToInt64()); }
            catch { extra = 0; }

            bool isTouch = IsTouch(extra);
            bool isPen = IsPen(extra);
            if (isTouch)
            {
                _cTouch++;
                if (msg == Native.WM_LBUTTONDOWN) _cTouchDown++;
            }
            else if (isPen) _cPen++;
            else _cMouse++;

            // 触摸/笔事件带上"当时的前台进程"，便于判定是哪个游戏、以及 -only 该填什么名字
            string fg = "";
            if (isTouch || isPen) fg = " fg=" + ForegroundProc() + "#" + _fgPid;

            bool swallow = false;
            uint buttonFlags = 0;
            bool moveAbsolute = false;
            string note = "";
            string line = null;

            if (isTouch && _convert)
            {
                if (_cSwallow > 5000)
                {
                    _convert = false;
                    note = "LOOP-GUARD-STOP";
                    Enqueue(new Work { Log = "ERROR 事件数异常，已自动停止转换（防死循环）" });
                }
                else if (_only.Count > 0 && !_only.Contains(ForegroundProc()))
                {
                    note = "SKIP(前台进程不在名单)";
                }
                else if (TryMapButton(msg, out buttonFlags))
                {
                    swallow = true;
                    moveAbsolute = true;
                    note = _add ? "CONVERT(+原事件也放行)" : "CONVERT";
                }
                else if (msg == Native.WM_MOUSEMOVE && _leftFromTouch)
                {
                    swallow = true;
                    moveAbsolute = true;
                    note = "CONVERT(拖动中)";
                }
            }

            bool interesting = isTouch || isPen || swallow || msg != Native.WM_MOUSEMOVE;
            if (interesting)
            {
                line = Kind(isTouch, isPen) + " " + MsgName(msg) + " (" + info.pt.X + "," + info.pt.Y + ")"
                       + " extra=0x" + extra.ToString("X8") + " id=" + (extra & 0x7F) + fg
                       + (note.Length > 0 ? "  -> " + note : "");
            }

            if (swallow || line != null)
            {
                Work w = new Work();
                w.Log = line;
                w.Pt = info.pt;
                if (swallow)
                {
                    w.ButtonFlags = buttonFlags;
                    w.MoveAbsolute = moveAbsolute;
                }
                Enqueue(w);
            }

            if (swallow)
            {
                if (buttonFlags == Native.MOUSEEVENTF_LEFTDOWN) _leftFromTouch = true;
                else if (buttonFlags == Native.MOUSEEVENTF_LEFTUP) _leftFromTouch = false;
                _cSwallow++;
                if (_add) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                return (IntPtr)1;
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static void Enqueue(Work w)
        {
            lock (QueueLock)
            {
                if (Queue.Count > 512) return;   // 兜底，防止洪水
                Queue.Enqueue(w);
                _cQueued++;
            }
        }

        // ---------- 注入/写日志都在这里做（UI 线程定时器，已脱离钩子回调） ----------
        private static void PumpQueue(object sender, EventArgs e)
        {
            for (int budget = 0; budget < 128; budget++)
            {
                Work w;
                lock (QueueLock)
                {
                    if (Queue.Count == 0) return;
                    w = Queue.Dequeue();
                }
                if (w.Log != null) LogLine(w.Log);

                if (w.ButtonFlags != 0)
                {
                    Native.POINT cur;
                    if (Native.GetCursorPos(out cur) && (cur.X != w.Pt.X || cur.Y != w.Pt.Y))
                    {
                        Native.SetCursorPos(w.Pt.X, w.Pt.Y);
                    }
                    InjectMouse(w.ButtonFlags, 0, 0, 0, false);
                    _cInject++;
                    if (w.ButtonFlags == Native.MOUSEEVENTF_LEFTDOWN) _leftFromTouch = true;
                    else if (w.ButtonFlags == Native.MOUSEEVENTF_LEFTUP) _leftFromTouch = false;
                }
                else if (w.MoveAbsolute)
                {
                    InjectAbsoluteMove(w.Pt, false);
                    _cInject++;
                }
            }
        }

        // 自检用：跑 n 拍消息泵（每拍 20ms），让 UI 定时器把注入队列排空
        private static void Pump(int n)
        {
            for (int i = 0; i < n; i++)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(20);
            }
        }

        private static bool IsTouch(uint extra)
        {
            if (_selfTest && extra == SELFTEST_MAGIC) return true;
            return (extra & SIGNATURE_MASK) == TOUCH_SIGNATURE && (extra & 0x80) != 0;
        }

        private static bool IsPen(uint extra)
        {
            return (extra & SIGNATURE_MASK) == TOUCH_SIGNATURE && (extra & 0x80) == 0;
        }

        private static string Kind(bool touch, bool pen)
        {
            if (touch) return "TOUCH";
            if (pen) return "PEN  ";
            return "MOUSE";
        }

        private static string MsgName(int msg)
        {
            switch (msg)
            {
                case Native.WM_MOUSEMOVE: return "MOVE    ";
                case Native.WM_LBUTTONDOWN: return "L-DOWN  ";
                case Native.WM_LBUTTONUP: return "L-UP    ";
                case Native.WM_LBUTTONDBLCLK: return "L-DBLCLK";
                case Native.WM_RBUTTONDOWN: return "R-DOWN  ";
                case Native.WM_RBUTTONUP: return "R-UP    ";
                case Native.WM_MBUTTONDOWN: return "M-DOWN  ";
                case Native.WM_MBUTTONUP: return "M-UP    ";
                case Native.WM_MOUSEWHEEL: return "WHEEL   ";
                case Native.WM_MOUSEHWHEEL: return "HWHEEL  ";
                default: return "MSG_" + msg.ToString("X4");
            }
        }

        private static bool TryMapButton(int msg, out uint flags)
        {
            flags = 0;
            if (msg == Native.WM_LBUTTONDOWN) { flags = Native.MOUSEEVENTF_LEFTDOWN; return true; }
            if (msg == Native.WM_LBUTTONUP) { flags = Native.MOUSEEVENTF_LEFTUP; return true; }
            if (msg == Native.WM_RBUTTONDOWN) { flags = Native.MOUSEEVENTF_RIGHTDOWN; return true; }
            if (msg == Native.WM_RBUTTONUP) { flags = Native.MOUSEEVENTF_RIGHTUP; return true; }
            if (msg == Native.WM_MBUTTONDOWN) { flags = Native.MOUSEEVENTF_MIDDLEDOWN; return true; }
            if (msg == Native.WM_MBUTTONUP) { flags = Native.MOUSEEVENTF_MIDDLEUP; return true; }
            return false;
        }

        private static void InjectMouse(uint flags, int dx, int dy, uint mouseData, bool magical)
        {
            Native.INPUT[] arr = new Native.INPUT[1];
            arr[0].type = 0;   // INPUT_MOUSE
            arr[0].mi.dx = dx;
            arr[0].mi.dy = dy;
            arr[0].mi.mouseData = mouseData;
            arr[0].mi.dwFlags = flags;
            arr[0].mi.time = 0;
            arr[0].mi.dwExtraInfo = magical ? new IntPtr(unchecked((int)SELFTEST_MAGIC)) : IntPtr.Zero;
            uint sent = Native.SendInput(1, arr, Marshal.SizeOf(typeof(Native.INPUT)));
            if (sent == 0) _cError++;
        }

        private static void InjectAbsoluteMove(Native.POINT pt, bool magical)
        {
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            if (vw < 2) vw = 2;
            if (vh < 2) vh = 2;
            int ax = (int)Math.Round((pt.X - vx) * 65535.0 / (vw - 1));
            int ay = (int)Math.Round((pt.Y - vy) * 65535.0 / (vh - 1));
            InjectMouse(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK,
                        ax, ay, 0, magical);
        }

        // 前台进程名带缓存：钩子回调里做进程查询要尽量少，且绝不能阻塞
        private static int _fgPid = 0;
        private static string _fgName = "";
        private static string _fgTitle = "";
        private static int _fgStamp = 0;

        private static void RefreshForeground()
        {
            int now = Environment.TickCount;
            if (_fgStamp != 0 && unchecked(now - _fgStamp) < 1000) return;
            _fgStamp = now;
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) { _fgPid = 0; _fgName = ""; _fgTitle = ""; return; }
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == 0) { _fgPid = 0; _fgName = ""; _fgTitle = ""; return; }
            _fgPid = (int)pid;
            try
            {
                using (Process p = Process.GetProcessById(_fgPid)) _fgName = p.ProcessName.ToLowerInvariant();
            }
            catch { _fgName = "?"; }
            StringBuilder sb = new StringBuilder(160);
            Native.GetWindowText(h, sb, 160);
            string t = sb.ToString();
            if (t.Length > 40) t = t.Substring(0, 40);
            _fgTitle = t;
        }

        private static string ForegroundProc()
        {
            RefreshForeground();
            if (_fgName == "?" && _fgTitle.Length > 0) return "?[" + _fgTitle + "]";
            return _fgName;
        }

        private static string ObjectName(IntPtr h)
        {
            if (h == IntPtr.Zero) return "?";
            StringBuilder sb = new StringBuilder(256);
            int need;
            if (!Native.GetUserObjectInformation(h, 2, sb, 512, out need)) return "?";
            return sb.ToString();
        }

        private static string IntegrityLevel()
        {
            IntPtr tok;
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x0008, out tok)) return "?";
            try
            {
                int size;
                Native.GetTokenInformation(tok, 25, IntPtr.Zero, 0, out size);
                if (size <= 0) return "?";
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (!Native.GetTokenInformation(tok, 25, buf, size, out size)) return "?";
                    IntPtr sid = Marshal.ReadIntPtr(buf);
                    if (sid == IntPtr.Zero) return "?";
                    int count = Marshal.ReadByte(sid, 1);
                    if (count < 1) return "?";
                    int rid = Marshal.ReadInt32(IntPtr.Add(sid, 8 + (count - 1) * 4));
                    if (rid == 0x0000) return "Untrusted";
                    if (rid == 0x1000) return "Low";
                    if (rid == 0x2000) return "Medium";
                    if (rid == 0x2100) return "MediumPlus";
                    if (rid == 0x3000) return "High(管理员)";
                    if (rid == 0x4000) return "System";
                    return "0x" + rid.ToString("X");
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { Native.CloseHandle(tok); }
        }

        // 这条诊断很关键：如果"进程所在桌面"和"当前输入桌面"不是同一个，
        // 全局钩子照样会被 SetWindowsHookEx 接受，但永远收不到真实输入（隔离桌面/低完整性就会这样）。
        private static void Diag()
        {
            string ownDesk = ObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
            string wsta = ObjectName(Native.GetProcessWindowStation());
            IntPtr hIn = Native.OpenInputDesktop(0, false, 0x0001);
            string inDesk = ObjectName(hIn);
            if (hIn != IntPtr.Zero) Native.CloseDesktop(hIn);

            LogLine("环境：窗口站=" + wsta + " 进程桌面=" + ownDesk + " 输入桌面=" + inDesk
                    + " 桌面一致=" + (ownDesk == inDesk) + " 完整性=" + IntegrityLevel());
            if (ownDesk != inDesk)
            {
                LogLine("⚠ 进程桌面与输入桌面不一致：钩子收不到真实触摸/鼠标输入，请把这一行发我。");
            }
        }

        // 启动自检：本进程注入 2 次"真位移" + 让"另一个进程"也注入 2 次，看钩子各能收到几次。
        // 本进程能收到但别的进程收不到 → 就是权限/沙箱问题（请以管理员身份运行），不是游戏的问题。
        private static void HookLiveCheck()
        {
            int own = MoveAndCount();
            LogLine("钩子自检[本进程注入]：收到 " + own + " 次事件 → " + (own >= 2 ? "OK" : "⚠ 失败"));

            int cross = -1;
            try
            {
                int before = _cMouse + _cTouch + _cPen;
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "-injectmove");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process child = Process.Start(psi);
                child.WaitForExit(3000);
                Pump(35);
                cross = (_cMouse + _cTouch + _cPen) - before;
            }
            catch { cross = -1; }

            if (cross < 0)
            {
                LogLine("钩子自检[另一进程注入]：无法启动子进程做这次检查（跳过）");
            }
            else if (cross >= 2)
            {
                LogLine("钩子自检[另一进程注入]：收到 " + cross + " 次事件 → OK，钩子能收到别的进程/真实路径的输入。");
            }
            else
            {
                LogLine("钩子自检[另一进程注入]：⚠ 只收到 " + cross + " 次事件 → 本环境下钩子收不到别的进程的输入。");
                LogLine("        请以管理员身份运行本工具（右键 → 以管理员身份运行）后再跑一次。");
            }
            RestoreCursor();
        }

        private static Native.POINT _savedCursor;
        private static bool _cursorSaved = false;

        private static void SaveCursor()
        {
            Native.POINT p;
            if (Native.GetCursorPos(out p)) { _savedCursor = p; _cursorSaved = true; }
        }

        private static void RestoreCursor()
        {
            if (_cursorSaved) Native.SetCursorPos(_savedCursor.X, _savedCursor.Y);
        }

        // 把光标挪到屏幕中心，做两次相对位移（保证真的产生位移，不受屏幕边缘钳制），返回钩子看到的事件数
        private static int MoveAndCount()
        {
            SaveCursor();
            int cx = Native.GetSystemMetrics(0) / 2;
            int cy = Native.GetSystemMetrics(1) / 2;
            Native.SetCursorPos(cx, cy);
            Pump(3);
            int before = _cMouse + _cTouch + _cPen;
            InjectMouse(Native.MOUSEEVENTF_MOVE, 6, 0, 0, false);
            Pump(4);
            InjectMouse(Native.MOUSEEVENTF_MOVE, -6, 0, 0, false);
            Pump(10);
            return (_cMouse + _cTouch + _cPen) - before;
        }

        // 子进程模式：只注入两次相对位移然后退出（用来验证"钩子能否收到别的进程的输入"）
        private static void InjectMoveChild()
        {
            Native.POINT cur;
            if (!Native.GetCursorPos(out cur)) return;
            InjectMouse(Native.MOUSEEVENTF_MOVE, 6, 0, 0, false);
            Pump(3);
            InjectMouse(Native.MOUSEEVENTF_MOVE, -6, 0, 0, false);
            Pump(3);
        }

        private static int RunSelfTest()
        {
            if (!InstallHook()) return 2;

            _pump = new System.Windows.Forms.Timer();
            _pump.Interval = 8;
            _pump.Tick += PumpQueue;
            _pump.Start();

            TestForm f = new TestForm();
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(70, 70);
            f.Size = new Size(460, 320);
            f.Text = "TouchToMouse self-test";
            f.TopMost = true;
            f.Show();
            for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(25); }
            f.Activate();

            const int N = 3;       // 需要"被钩子看到"的点击次数
            const int MAXATT = 10; // 最多注入多少次（系统偶发丢弃注入事件时就重试）
            int cx = f.Location.X + 220;
            int cy = f.Location.Y + 160;
            int attempts = 0;
            int seen = 0;

            // 自适应：注入直到钩子确实看到 N 次"假触摸按下"。
            // 真正要验证的不变量是「钩子看到几次 → 目标窗口就只能收到几次」（不能 0 次也不能 2 次）。
            while (seen < N && attempts < MAXATT)
            {
                attempts++;
                int before = _cTouchDown;
                Native.POINT p;
                p.X = cx;
                p.Y = cy;
                InjectAbsoluteMove(p, true);                              // 假装"触摸"把光标挪过去
                Pump(5);
                InjectMouse(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, true);   // 假装"触摸"按下
                Pump(5);
                InjectMouse(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, true);     // 假装"触摸"抬起
                Pump(45);   // ~900ms：拉开双击间隔，并等队列排空
                if (_cTouchDown > before) seen++;
            }
            Pump(20);

            int deliveredDowns = f.Down + f.DblClk;
            bool pass = seen >= N && deliveredDowns == seen && f.Up == seen && _cError == 0;

            Console.WriteLine("自检：用内部注入的假触摸事件验证『吞掉 + 重注入』通路");
            Console.WriteLine("  钩子看到的触摸按下 = " + seen + "（期望 " + N + "，共注入 " + attempts + " 次）");
            Console.WriteLine("  测试窗口收到按下   = " + f.Down + " + 双击 " + f.DblClk + " = " + deliveredDowns
                              + "（必须等于上面那个数：多 1 次=没吞掉，会双击；少 1 次=重注入没送到）");
            Console.WriteLine("  测试窗口收到抬起   = " + f.Up + "（必须等于 " + seen + "）");
            Console.WriteLine("  重注入事件数        = " + _cInject + "（期望 >= " + (2 * seen) + "）");
            Console.WriteLine("  已吞掉事件数        = " + _cSwallow);
            Console.WriteLine("  SendInput 失败次数  = " + _cError);
            if (seen < N) Console.WriteLine("  ⚠ 系统丢弃了注入事件（" + attempts + " 次注入只被看到 " + seen + " 次），必要时再跑一次");
            Console.WriteLine("  结果：" + (pass ? "PASS" : "FAIL"));
            LogLine("SELFTEST " + (pass ? "PASS" : "FAIL") + " attempts=" + attempts + " seen=" + seen
                     + " down=" + f.Down + " dblclk=" + f.DblClk + " up=" + f.Up
                     + " injected=" + _cInject + " swallowed=" + _cSwallow + " errors=" + _cError);

            Shutdown("自检" + (pass ? "通过" : "失败"), pass ? 0 : 1);
            return pass ? 0 : 1;
        }

        private static void Shutdown(string reason)
        {
            Shutdown(reason, 0);
        }

        private static void Shutdown(string reason, int exitCode)
        {
            if (_pump != null) { _pump.Stop(); _pump = null; }
            PumpQueue(null, EventArgs.Empty);
            if (_hook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            Console.WriteLine(new string('-', 64));
            Console.WriteLine("结束：" + reason);
            Console.WriteLine("真实鼠标消息: " + _cMouse);
            Console.WriteLine("触摸派生消息: " + _cTouch);
            Console.WriteLine("笔派生消息  : " + _cPen);
            Console.WriteLine("吞掉并重注入: " + _cSwallow + "（注入 " + _cInject + " 次，失败 " + _cError + " 次，排队 " + _cQueued + "）");

            if (_cTouch == 0)
            {
                Console.WriteLine("判读：本次没有出现任何触摸派生鼠标消息。");
                Console.WriteLine("      → 若你确实在前台程序里点过，说明那个程序把触摸当指针输入自己吃掉了");
                Console.WriteLine("        （Windows 不再做 touch→mouse 提升），钩子这条路线对它无效。");
                Console.WriteLine("      → 若你只在桌面点了点，则数据不足，请在目标游戏里再点一次。");
            }
            else
            {
                Console.WriteLine("判读：看到 " + _cTouch + " 条触摸派生鼠标消息 → 该程序走鼠标消息通路。");
                if (_convert) Console.WriteLine("      转换模式已把它们改写成干净的鼠标事件。");
                else Console.WriteLine("      加 -convert 重新运行即可把它们改写成干净的鼠标事件。");
            }

            if (_cMouse + _cTouch + _cPen == 0)
            {
                int secs = (int)(DateTime.Now - _startTime).TotalSeconds;
                Console.WriteLine("⚠ 这 " + secs + " 秒里钩子一条鼠标消息都没收到（连真实鼠标事件也没有）。");
                Console.WriteLine("  请做对照测试：探针运行时，先用手指点几下【桌面空白处或这个控制台窗口本身】，再点游戏。");
                Console.WriteLine("    · 桌面那几下也没有 TOUCH 行 → 钩子没接上输入链，把日志发我查；");
                Console.WriteLine("    · 桌面有 TOUCH、游戏没有   → 游戏自己吃掉了指针输入（第①类，本工具救不了）。");
            }
            Console.WriteLine("逐条日志：" + _logPath);

            LogLine("STOP " + reason + " mouse=" + _cMouse + " touch=" + _cTouch + " pen=" + _cPen
                     + " swallow=" + _cSwallow + " inject=" + _cInject + " errors=" + _cError);
            lock (LogLock) { if (_log != null) { _log.Flush(); _log.Dispose(); _log = null; } }
            Environment.Exit(exitCode);
        }

        private static void OpenLog()
        {
            try
            {
                _log = new StreamWriter(_logPath, false, new UTF8Encoding(false));
                _log.AutoFlush = true;
            }
            catch
            {
                _log = null;
            }
        }

        private static void LogLine(string s)
        {
            Console.WriteLine(s);
            lock (LogLock)
            {
                if (_log == null) return;
                try { _log.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + s); }
                catch { }
            }
        }
    }
}
