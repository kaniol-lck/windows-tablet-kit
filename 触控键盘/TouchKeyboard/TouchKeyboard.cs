// TouchKeyboard.cs —— 轻量触控屏幕键盘（为无 TabTip/osK 可用的 Windows 10 平板编写）
// 特点：无边框置顶、WS_EX_NOACTIVATE 不抢焦点（按键直接送进当前焦点窗口）、大按键、Shift/Ctrl/Alt/Win 可锁定
// 编译：Add-Type -OutputAssembly ... -OutputType WindowsApplication
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class TouchKeyboard : Form
{
    // ---------------- Win32 ----------------
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int cbSize);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 2;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private static readonly int InputSize = Marshal.SizeOf(typeof(INPUT));

    private static void SendKey(ushort vk, bool up, bool extended)
    {
        INPUT[] inp = new INPUT[1];
        inp[0].type = INPUT_KEYBOARD;
        inp[0].u.ki.wVk = vk;
        inp[0].u.ki.wScan = 0;
        inp[0].u.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0u) | (extended ? KEYEVENTF_EXTENDEDKEY : 0u);
        inp[0].u.ki.time = 0;
        inp[0].u.ki.dwExtraInfo = IntPtr.Zero;
        SendInput(1, inp, InputSize);
    }

    private static void Combo(ushort vk, bool shift, bool ctrl, bool alt, bool win, bool extended)
    {
        if (win) SendKey(0x5B, false, true);
        if (ctrl) SendKey(0x11, false, false);
        if (alt) SendKey(0x12, false, false);
        if (shift) SendKey(0x10, false, false);
        System.Threading.Thread.Sleep(8);
        SendKey(vk, false, extended);
        System.Threading.Thread.Sleep(8);
        SendKey(vk, true, extended);
        if (shift) SendKey(0x10, true, false);
        if (alt) SendKey(0x12, true, false);
        if (ctrl) SendKey(0x11, true, false);
        if (win) SendKey(0x5B, true, true);
    }

    // ---------------- 布局定义 ----------------
    private class KeyDef
    {
        public string Text; public ushort Vk; public double W; public bool Extended; public string Latch;
        public KeyDef(string t, ushort vk, double w) { Text = t; Vk = vk; W = w; }
    }

    private readonly int gap = 7;
    private int unit = 110, keyH = 90, stripH = 38;
    private bool latchShift, latchCtrl, latchAlt, latchWin;
    private System.Collections.Generic.List<Button> latchButtons = new System.Collections.Generic.List<Button>();
    private string posFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TouchKeyboard.pos");

    protected override CreateParams CreateParams
    {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= WS_EX_NOACTIVATE; return cp; }
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    private KeyDef[] Row1()
    {
        var l = new System.Collections.Generic.List<KeyDef>();
        l.Add(new KeyDef("Esc", 0x1B, 1));
        l.Add(new KeyDef("Tab", 0x09, 1));
        for (int i = 1; i <= 9; i++) l.Add(new KeyDef(i.ToString(), (ushort)(0x30 + i), 1));
        l.Add(new KeyDef("0", 0x30, 1));
        l.Add(new KeyDef("-", 0xBD, 1));
        l.Add(new KeyDef("=", 0xBB, 1));
        l.Add(new KeyDef("⌫", 0x08, 1));
        return l.ToArray();
    }
    private KeyDef[] Row2()
    {
        string[] ch = { "q","w","e","r","t","y","u","i","o","p" };
        var l = new System.Collections.Generic.List<KeyDef>();
        foreach (string c in ch) l.Add(new KeyDef(c.ToUpper(), (ushort)char.ToUpper(c[0]), 1));
        l.Add(new KeyDef("[", 0xDB, 1));
        l.Add(new KeyDef("]", 0xDD, 1));
        l.Add(new KeyDef("\\", 0xDC, 1));
        l.Add(new KeyDef("Del", 0x2E, 2));
        return l.ToArray();
    }
    private KeyDef[] Row3()
    {
        string[] ch = { "a","s","d","f","g","h","j","k","l" };
        var l = new System.Collections.Generic.List<KeyDef>();
        foreach (string c in ch) l.Add(new KeyDef(c.ToUpper(), (ushort)char.ToUpper(c[0]), 1));
        l.Add(new KeyDef(";", 0xBA, 1));
        l.Add(new KeyDef("'", 0xDE, 1));
        l.Add(new KeyDef("⏎ Enter", 0x0D, 4));
        return l.ToArray();
    }
    private KeyDef[] Row4()
    {
        string[] ch = { "z","x","c","v","b","n","m" };
        var l = new System.Collections.Generic.List<KeyDef>();
        var s1 = new KeyDef("⇧", 0x10, 2); s1.Latch = "shift"; l.Add(s1);
        foreach (string c in ch) l.Add(new KeyDef(c.ToUpper(), (ushort)char.ToUpper(c[0]), 1));
        l.Add(new KeyDef(",", 0xBC, 1));
        l.Add(new KeyDef(".", 0xBE, 1));
        l.Add(new KeyDef("/", 0xBF, 1));
        var s2 = new KeyDef("⇧", 0x10, 3); s2.Latch = "shift"; l.Add(s2);
        return l.ToArray();
    }
    private KeyDef[] Row5()
    {
        var l = new System.Collections.Generic.List<KeyDef>();
        var c = new KeyDef("Ctrl", 0x11, 1.5); c.Latch = "ctrl"; l.Add(c);
        l.Add(new KeyDef("中英", 0x20, 1.5));            // Ctrl+Space 切换中英文
        var w = new KeyDef("Win", 0x5B, 1); w.Latch = "win"; l.Add(w);
        var a = new KeyDef("Alt", 0x12, 1.5); a.Latch = "alt"; l.Add(a);
        l.Add(new KeyDef("空格", 0x20, 4.5));
        var lf = new KeyDef("←", 0x25, 1); lf.Extended = true; l.Add(lf);
        var up = new KeyDef("↑", 0x26, 1); up.Extended = true; l.Add(up);
        var dn = new KeyDef("↓", 0x28, 1); dn.Extended = true; l.Add(dn);
        var rt = new KeyDef("→", 0x27, 1); rt.Extended = true; l.Add(rt);
        l.Add(new KeyDef("✕", 0, 1));                    // 关闭
        return l.ToArray();
    }

    public TouchKeyboard()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "触控键盘";
        BackColor = Color.FromArgb(28, 28, 30);
        KeyPreview = false;

        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        unit = (int)((wa.Width * 0.86 - 16.0 * gap) / 15.0);
        if (unit < 56) unit = 56;
        keyH = (int)(unit * 0.80);
        int width = 15 * unit + 16 * gap;
        int height = stripH + 5 * (keyH + gap) + gap;
        ClientSize = new Size(width, height);

        int left = wa.Left + (wa.Width - width) / 2;
        int top = wa.Bottom - height - 4;
        try
        {
            if (File.Exists(posFile))
            {
                string[] parts = File.ReadAllText(posFile).Split(',');
                int x, y;
                if (parts.Length == 2 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y))
                {
                    Rectangle test = new Rectangle(x, y, width, height);
                    if (wa.IntersectsWith(test)) { left = x; top = y; }
                }
            }
        }
        catch { }
        Location = new Point(left, top);

        // ---- 顶部拖动条 ----
        Panel strip = new Panel();
        strip.Height = stripH;
        strip.Width = width;
        strip.Location = new Point(0, 0);
        strip.BackColor = Color.FromArgb(45, 45, 50);
        strip.MouseDown += delegate (object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero); }
        };
        Controls.Add(strip);

        Label title = new Label();
        title.Text = "触控键盘  (拖动此条移动)";
        title.ForeColor = Color.Gainsboro;
        title.Font = new Font("Microsoft YaHei UI", Math.Max(9f, unit * 0.11f));
        title.AutoSize = true;
        title.Location = new Point(gap + 2, (stripH - title.Height) / 2);
        title.MouseDown += delegate (object s, MouseEventArgs e) { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero); };
        strip.Controls.Add(title);

        Button close = new Button();
        close.Text = "✕";
        close.FlatStyle = FlatStyle.Flat;
        close.FlatAppearance.BorderSize = 0;
        close.BackColor = Color.FromArgb(120, 45, 45);
        close.ForeColor = Color.White;
        close.Font = new Font("Segoe UI", Math.Max(9f, unit * 0.13f), FontStyle.Bold);
        close.Size = new Size(38, stripH - 8);
        close.Location = new Point(width - 42, 4);
        close.TabStop = false;
        close.Click += delegate (object s, EventArgs e) { Close(); };
        strip.Controls.Add(close);

        // ---- 主键区 ----
        KeyDef[][] rows = new KeyDef[][] { Row1(), Row2(), Row3(), Row4(), Row5() };
        Font keyFont = new Font("Segoe UI", Math.Max(10f, unit * 0.24f), FontStyle.Bold);
        Font smallFont = new Font("Microsoft YaHei UI", Math.Max(9f, unit * 0.17f), FontStyle.Bold);
        for (int r = 0; r < rows.Length; r++)
        {
            double xUnits = 0;
            foreach (KeyDef k in rows[r])
            {
                Button b = new Button();
                b.Text = k.Text;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.TabStop = false;
                b.BackColor = (k.Latch != null) ? Color.FromArgb(58, 72, 100) : Color.FromArgb(62, 62, 68);
                b.ForeColor = Color.White;
                b.Font = (k.Text.Length > 2) ? smallFont : keyFont;
                b.Size = new Size((int)Math.Round(k.W * unit + (k.W - 1) * gap), keyH);
                b.Location = new Point(gap + (int)Math.Round(xUnits * (unit + gap)), stripH + gap + r * (keyH + gap));
                b.MouseDown += MakeHandler(k, b);
                Controls.Add(b);
                if (k.Latch != null) { latchButtons.Add(b); b.Tag = k.Latch; }
                xUnits += k.W;
            }
        }
        UpdateLatchColors();
    }

    private MouseEventHandler MakeHandler(KeyDef k, Button b)
    {
        return delegate (object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (k.Vk == 0) { Close(); return; }                       // ✕
            if (k.Latch != null)                                      // 锁定键
            {
                if (k.Latch == "shift") latchShift = !latchShift;
                if (k.Latch == "ctrl") latchCtrl = !latchCtrl;
                if (k.Latch == "alt") latchAlt = !latchAlt;
                if (k.Latch == "win") latchWin = !latchWin;
                UpdateLatchColors();
                return;
            }
            bool ctrl = latchCtrl, alt = latchAlt, win = latchWin, shift = latchShift;
            if (k.Text == "中英") { Combo(0x20, false, true, false, false, false); }
            else if (k.Text == "空格") { Combo(0x20, false, false, false, false, false); }
            else { Combo(k.Vk, shift, ctrl, alt, win, k.Extended); }
            // Ctrl/Alt/Win 是一次性修饰键；Shift 保持锁定（便于连续输入大写/符号）
            if (latchCtrl || latchAlt || latchWin) { latchCtrl = latchAlt = latchWin = false; UpdateLatchColors(); }
        };
    }

    private void UpdateLatchColors()
    {
        foreach (Button b in latchButtons)
        {
            string t = (string)b.Tag;
            bool on = (t == "shift" && latchShift) || (t == "ctrl" && latchCtrl) || (t == "alt" && latchAlt) || (t == "win" && latchWin);
            b.BackColor = on ? Color.FromArgb(200, 140, 40) : Color.FromArgb(58, 72, 100);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { File.WriteAllText(posFile, Location.X + "," + Location.Y); } catch { }
        base.OnFormClosing(e);
    }

    [STAThread]
    public static void Main()
    {
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TouchKeyboard());
    }
}
