// MouseOverlay - tray-only cursor overlay with CPU/GPU temperature readout.
// Build with build.cmd (uses the C# compiler that ships with Windows, no SDK needed).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Shown by Task Manager / file properties; without these the process has a blank name.
[assembly: System.Reflection.AssemblyTitle("Mouse Overlay")]
[assembly: System.Reflection.AssemblyDescription("Cursor overlay with CPU/GPU temperature readout")]
[assembly: System.Reflection.AssemblyProduct("Mouse Overlay")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]

namespace MouseOverlay
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (Mutex m = new Mutex(true, "MouseOverlay_SingleInstance_7F3A", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                using (App app = new App())
                    Application.Run(app);
            }
        }
    }

    enum CursorShape { VArrow, Circle }

    // What happens to the real Windows cursor.
    //  Replace:   system cursors take the overlay shape (hardware-drawn: zero lag, visible even over
    //             Start menu / volume flyout where no window can draw). The overlay only appears when
    //             an app forces its own cursor, and the real cursor is hidden while it does.
    //  ArrowOnly: like Replace, but text / resize / busy cursors keep their normal look.
    //  Hide:      real cursor hidden, overlay drawn on top.
    //  AsIs:      real cursor untouched, overlay drawn on top.
    // In every mode, when the pointer is over something the overlay cannot draw above (Windows 11
    // shell flyouts, exclusive-fullscreen games) the real cursor is left visible instead.
    enum RealCursorMode { Replace, ArrowOnly, Hide, AsIs }

    // ------------------------------------------------------------------ settings

    sealed class Settings
    {
        public bool Enabled = true;
        public CursorShape Shape = CursorShape.VArrow;
        public int Size = 32;
        public int BorderWidth = 2;
        public Color Center = Color.FromArgb(255, 215, 0);
        public Color Border = Color.Black;
        public bool NoFill = false;
        public int Opacity = 100;
        public RealCursorMode RealCursor = RealCursorMode.Replace;
        public bool AutoHide = false;
        public bool ShowTemps = true;
        public int TempFontSize = 9;

        static string path;

        // Next to the exe when that folder is writable, otherwise %LOCALAPPDATA%\MouseOverlay.
        static string FilePath
        {
            get
            {
                if (path != null) return path;
                string exeIni = Path.ChangeExtension(Application.ExecutablePath, ".ini");
                string appIni = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MouseOverlay"), "MouseOverlay.ini");
                if (File.Exists(appIni)) return path = appIni;
                try
                {
                    using (File.Open(exeIni, FileMode.Append, FileAccess.Write)) { }
                    return path = exeIni;
                }
                catch
                {
                    try { Directory.CreateDirectory(Path.GetDirectoryName(appIni)); } catch { }
                    return path = appIni;
                }
            }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "Enabled": s.Enabled = v == "1"; break;
                        case "Shape": s.Shape = v == "Circle" ? CursorShape.Circle : CursorShape.VArrow; break;
                        case "Size": s.Size = Clamp(ParseInt(v, s.Size), 8, 256); break;
                        case "BorderWidth": s.BorderWidth = Clamp(ParseInt(v, s.BorderWidth), 0, 16); break;
                        case "Center": s.Center = ParseColor(v, s.Center); break;
                        case "Border": s.Border = ParseColor(v, s.Border); break;
                        case "NoFill": s.NoFill = v == "1"; break;
                        case "Opacity": s.Opacity = Clamp(ParseInt(v, s.Opacity), 10, 100); break;
                        case "RealCursor":
                            foreach (RealCursorMode m in Enum.GetValues(typeof(RealCursorMode)))
                                if (string.Equals(m.ToString(), v, StringComparison.OrdinalIgnoreCase)) s.RealCursor = m;
                            break;
                        case "HideSystemCursor": break; // pre-1.1 key; Replace mode supersedes it
                        case "AutoHide": s.AutoHide = v == "1"; break;
                        case "ShowTemps": s.ShowTemps = v == "1"; break;
                        case "TempFontSize": s.TempFontSize = Clamp(ParseInt(v, s.TempFontSize), 6, 32); break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, new string[] {
                    "Enabled=" + (Enabled ? "1" : "0"),
                    "Shape=" + Shape,
                    "Size=" + Size,
                    "BorderWidth=" + BorderWidth,
                    "Center=" + ColorTranslator.ToHtml(Color.FromArgb(Center.R, Center.G, Center.B)),
                    "Border=" + ColorTranslator.ToHtml(Color.FromArgb(Border.R, Border.G, Border.B)),
                    "NoFill=" + (NoFill ? "1" : "0"),
                    "Opacity=" + Opacity,
                    "RealCursor=" + RealCursor,
                    "AutoHide=" + (AutoHide ? "1" : "0"),
                    "ShowTemps=" + (ShowTemps ? "1" : "0"),
                    "TempFontSize=" + TempFontSize
                });
            }
            catch { }
        }

        static int ParseInt(string v, int def)
        {
            int r;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        static Color ParseColor(string v, Color def)
        {
            try { return ColorTranslator.FromHtml(v); } catch { return def; }
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    // ------------------------------------------------------------------ native

    static class Native
    {
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                         WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        public const int WM_ENDSESSION = 0x16, WM_SETTINGCHANGE = 0x1A, WM_DISPLAYCHANGE = 0x7E,
                         WM_DPICHANGED = 0x02E0, WM_THEMECHANGED = 0x031A, WM_APP = 0x8000;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOSENDCHANGING = 0x400;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const int ULW_ALPHA = 2;
        public const int CURSOR_SHOWING = 1;
        public const uint GW_HWNDPREV = 3, GA_ROOT = 2;
        public const int DWMWA_CLOAKED = 14;
        public const uint SPI_SETCURSORS = 0x57;
        public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        public const int MDT_EFFECTIVE_DPI = 0;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; public POINT(int x, int y) { this.x = x; this.y = y; } }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; } }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)] public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [StructLayout(LayoutKind.Sequential)] public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr proc, uint flags, System.Text.StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CopyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr h, out ICONINFO info);
        [DllImport("user32.dll")] public static extern IntPtr CreateIconIndirect(ref ICONINFO info);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr inst, IntPtr id);
        [DllImport("user32.dll")] public static extern bool SetSystemCursor(IntPtr hCursor, int id);
        [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow")] public static extern bool MoveLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, IntPtr size, IntPtr srcDc, IntPtr src, int key, IntPtr blend, int flags);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr h, int size, out BITMAP bm);
        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();

        [DllImport("Magnification.dll")] public static extern bool MagInitialize();
        [DllImport("Magnification.dll")] public static extern bool MagUninitialize();
        [DllImport("Magnification.dll")] public static extern bool MagShowSystemCursor(bool show);
    }

    // ------------------------------------------------------------------ system cursors

    // The standard cursors are shared handles: LoadCursor(NULL, id) returns the same HCURSOR in every
    // process, and SetSystemCursor swaps the image behind that handle. So "is the current cursor one
    // of these handles" tells us whether an app has forced a cursor of its own.
    static class SystemCursors
    {
        public const int OCR_NORMAL = 32512;
        public static readonly int[] AllIds = {
            32512, 32513, 32514, 32515, 32516, 32640, 32641, 32642, 32643, 32644, 32645, 32646,
            32648, 32649, 32650, 32651, 32671, 32672 };
        static readonly HashSet<IntPtr> handles = new HashSet<IntPtr>();

        static SystemCursors()
        {
            foreach (int id in AllIds)
            {
                IntPtr h = Native.LoadCursor(IntPtr.Zero, new IntPtr(id));
                if (h != IntPtr.Zero) handles.Add(h);
            }
        }

        public static bool IsSystem(IntPtr hCursor) { return handles.Contains(hCursor); }

        // Reload the user's cursor scheme from the registry (undoes any Replace).
        public static void Restore()
        {
            Native.SystemParametersInfo(Native.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        }

        public static void Replace(Bitmap bmp, int hotX, int hotY, int[] ids)
        {
            IntPtr master = CreateCursor(bmp, hotX, hotY);
            if (master == IntPtr.Zero) return;
            foreach (int id in ids)
            {
                IntPtr copy = Native.CopyIcon(master);            // SetSystemCursor destroys what it is given
                if (copy != IntPtr.Zero && !Native.SetSystemCursor(copy, id)) Native.DestroyIcon(copy);
            }
            Native.DestroyIcon(master);
        }

        // True while the arrow still carries our image (Windows reloads the scheme on theme,
        // pointer-size and DPI changes without telling anyone).
        public static bool ArrowMatches(int width, int hotX, int hotY)
        {
            Native.ICONINFO ii;
            if (!Native.GetIconInfo(Native.LoadCursor(IntPtr.Zero, new IntPtr(OCR_NORMAL)), out ii)) return true;
            Native.BITMAP bm = new Native.BITMAP();
            IntPtr bitmap = ii.hbmColor != IntPtr.Zero ? ii.hbmColor : ii.hbmMask;
            bool ok = Native.GetObject(bitmap, Marshal.SizeOf(typeof(Native.BITMAP)), out bm) != 0
                      && bm.bmWidth == width && ii.xHotspot == hotX && ii.yHotspot == hotY;
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
            return ok;
        }

        static IntPtr CreateCursor(Bitmap bmp, int hotX, int hotY)
        {
            IntPtr hIcon = bmp.GetHicon();                        // 32-bit icon with alpha
            Native.ICONINFO ii;
            IntPtr cursor = IntPtr.Zero;
            if (Native.GetIconInfo(hIcon, out ii))
            {
                ii.fIcon = false;
                ii.xHotspot = hotX;
                ii.yHotspot = hotY;
                cursor = Native.CreateIconIndirect(ref ii);
                if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
            }
            Native.DestroyIcon(hIcon);
            return cursor;
        }
    }

    // ------------------------------------------------------------------ overlay window

    // Click-through, always-on-top, per-pixel-alpha window. Content is pushed once as a
    // bitmap; after that moving it is a single position-only UpdateLayeredWindow call.
    class LayeredWindow : NativeWindow, IDisposable
    {
        static readonly HashSet<IntPtr> own = new HashSet<IntPtr>();
        readonly HashSet<IntPtr> unbeatable = new HashSet<IntPtr>();
        int x = -32000, y = -32000;
        bool visible;

        public LayeredWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "MouseOverlay";
            cp.Style = Native.WS_POPUP;
            cp.ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST |
                         Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            cp.X = x; cp.Y = y; cp.Width = 1; cp.Height = 1;
            CreateHandle(cp);
            own.Add(Handle);
        }

        public static bool IsOwn(IntPtr h) { return own.Contains(h); }

        public void SetBitmap(Bitmap bmp)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDc, hBmp);
                Native.SIZE size = new Native.SIZE(bmp.Width, bmp.Height);
                Native.POINT src = new Native.POINT(0, 0);
                Native.POINT dst = new Native.POINT(x, y);
                Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
                bf.SourceConstantAlpha = 255;
                bf.AlphaFormat = 1; // AC_SRC_ALPHA
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref bf, Native.ULW_ALPHA);
            }
            finally
            {
                if (hBmp != IntPtr.Zero)
                {
                    Native.SelectObject(memDc, old);
                    Native.DeleteObject(hBmp);
                }
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // Position-only UpdateLayeredWindow: measured ~20x cheaper than SetWindowPos. Safe to call
        // from any thread.
        public void MoveTo(int nx, int ny)
        {
            x = nx; y = ny;
            Native.POINT dst = new Native.POINT(nx, ny);
            Native.MoveLayeredWindow(Handle, IntPtr.Zero, ref dst, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero, 0);
        }

        // True when the given top-level window sits above us in z-order. Any thread.
        public bool IsBelow(IntPtr root)
        {
            IntPtr h = Native.GetWindow(Handle, Native.GW_HWNDPREV);
            for (int i = 0; h != IntPtr.Zero && i < 500; i++)
            {
                if (h == root) return true;
                h = Native.GetWindow(h, Native.GW_HWNDPREV);
            }
            return false;
        }

        // Keeps the overlay above menus, tooltips and other topmost windows. SetWindowPos is
        // expensive, so it only runs when a foreign visible window actually sits above us.
        public bool RaiseIfCovered()
        {
            if (!visible || FirstWindowAbove() == IntPtr.Zero) return false;
            Raise();
            // Whatever is still above us lives in a higher system band; stop fighting it.
            if (unbeatable.Count > 256) unbeatable.Clear();
            IntPtr h;
            while ((h = FirstWindowAbove()) != IntPtr.Zero) unbeatable.Add(h);
            return true;
        }

        public void Raise()
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE | Native.SWP_NOSENDCHANGING);
        }

        public void ForgetUnbeatable() { unbeatable.Clear(); }

        IntPtr FirstWindowAbove()
        {
            IntPtr h = Native.GetWindow(Handle, Native.GW_HWNDPREV);
            for (int i = 0; h != IntPtr.Zero && i < 200; i++)
            {
                if (!own.Contains(h) && !unbeatable.Contains(h) && Native.IsWindowVisible(h) && !IsCloakedOrEmpty(h)) return h;
                h = Native.GetWindow(h, Native.GW_HWNDPREV);
            }
            return IntPtr.Zero;
        }

        static bool IsCloakedOrEmpty(IntPtr h)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r) || r.right <= r.left || r.bottom <= r.top) return true;
            int cloaked;
            return Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0;
        }

        // UI thread only.
        public bool Visible
        {
            get { return visible; }
            set
            {
                if (visible == value) return;
                visible = value;
                Native.ShowWindow(Handle, value ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
            }
        }

        public void Dispose() { own.Remove(Handle); DestroyHandle(); }
    }

    sealed class CursorWindow : LayeredWindow
    {
        public const int WM_APP_STATE = Native.WM_APP + 1, WM_APP_RAISE = Native.WM_APP + 2;
        readonly App app;

        public CursorWindow(App app) { this.app = app; }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            switch (m.Msg)
            {
                case WM_APP_STATE: app.ApplyState((int)m.WParam.ToInt64()); break;
                case WM_APP_RAISE: app.RaiseRequested(); break;
                case Native.WM_DISPLAYCHANGE:
                case Native.WM_DPICHANGED:
                case Native.WM_THEMECHANGED:
                case Native.WM_SETTINGCHANGE: app.OnSystemChange(); break;
                case Native.WM_ENDSESSION: if (m.WParam != IntPtr.Zero) app.ExitThread(); break;
            }
        }
    }

    // Polls the cursor from its own thread on a high-resolution timer. Cheaper than raw input
    // (a 1000 Hz mouse would otherwise push 1000 messages/s through the UI loop) and, unlike
    // hooks, adds nothing to the system's input path.
    sealed class Tracker
    {
        const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerEx(IntPtr sa, string name, uint flags, uint access);
        [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr h, ref long due, int periodMs, IntPtr cb, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        sealed class Session { public volatile bool Active = true; }

        readonly Action tick;
        readonly int periodMs;
        Session current;

        public Tracker(Action tick, int periodMs) { this.tick = tick; this.periodMs = periodMs; }

        public void Start()
        {
            if (current != null && current.Active) return;
            Session s = new Session();
            current = s;
            Thread t = new Thread(delegate() { Loop(s); });
            t.IsBackground = true;
            t.Priority = ThreadPriority.AboveNormal;
            t.Start();
        }

        // The old thread notices on its next tick and exits; no join, so no risk of
        // deadlocking against a window call it is in the middle of.
        public void Stop() { if (current != null) current.Active = false; }

        void Loop(Session s)
        {
            IntPtr timer = CreateWaitableTimerEx(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (timer != IntPtr.Zero)
            {
                long due = -1;
                if (!SetWaitableTimer(timer, ref due, periodMs, IntPtr.Zero, IntPtr.Zero, false)) { CloseHandle(timer); timer = IntPtr.Zero; }
            }
            while (s.Active)
            {
                if (timer != IntPtr.Zero) WaitForSingleObject(timer, 100); else Thread.Sleep(periodMs);
                try { tick(); } catch { }
            }
            if (timer != IntPtr.Zero) CloseHandle(timer);
        }
    }

    // ------------------------------------------------------------------ temperature sensors

    // CPU: ACPI thermal zones through the PDH performance counter (no admin, no driver).
    sealed class CpuTemp : IDisposable
    {
        const uint PDH_FMT_DOUBLE = 0x200;
        const uint PDH_MORE_DATA = 0x800007D2;

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint fmt, ref uint size, out uint count, IntPtr buffer);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

        IntPtr query, counter;
        double scale = 0.1;
        IntPtr buf = IntPtr.Zero;
        uint bufSize;

        public CpuTemp()
        {
            try
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
                if (PdhAddEnglishCounter(query, @"\Thermal Zone Information(*)\High Precision Temperature", IntPtr.Zero, out counter) != 0)
                {
                    scale = 1.0;
                    if (PdhAddEnglishCounter(query, @"\Thermal Zone Information(*)\Temperature", IntPtr.Zero, out counter) != 0)
                    {
                        PdhCloseQuery(query);
                        query = IntPtr.Zero;
                    }
                }
            }
            catch { query = IntPtr.Zero; }
        }

        // Hottest thermal zone in deg C, or NaN when unavailable.
        public double Read()
        {
            if (query == IntPtr.Zero) return double.NaN;
            try
            {
                if (PdhCollectQueryData(query) != 0) return double.NaN;
                uint size = bufSize, count;
                uint r = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out count, buf);
                if (r == PDH_MORE_DATA)
                {
                    if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                    buf = Marshal.AllocHGlobal((int)size);
                    bufSize = size;
                    r = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out count, buf);
                }
                if (r != 0) return double.NaN;
                // PDH_FMT_COUNTERVALUE_ITEM is 24 bytes on x86 and x64: name ptr, status @8, double @16.
                double max = double.NaN;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(buf.ToInt64() + i * 24);
                    if (Marshal.ReadInt32(item, 8) != 0) continue;
                    double kelvin = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16)) * scale;
                    double c = kelvin - 273.15;
                    if (c > 0 && c < 150 && (double.IsNaN(max) || c > max)) max = c;
                }
                return max;
            }
            catch { return double.NaN; }
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; }
            if (buf != IntPtr.Zero) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; }
        }
    }

    // GPU: NVIDIA NVML (ships with the driver). Reads the sensor directly, no process spawn.
    sealed class GpuTemp : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);
        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] static extern int nvmlShutdown();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);

        IntPtr device;
        bool ok;

        public GpuTemp()
        {
            try
            {
                if (LoadLibrary("nvml.dll") == IntPtr.Zero)
                {
                    string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    LoadLibrary(Path.Combine(pf, @"NVIDIA Corporation\NVSMI\nvml.dll"));
                }
                ok = nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out device) == 0;
            }
            catch { ok = false; }
        }

        public double Read()
        {
            if (!ok) return double.NaN;
            try
            {
                uint t;
                return nvmlDeviceGetTemperature(device, 0, out t) == 0 ? t : double.NaN;
            }
            catch { return double.NaN; }
        }

        public void Dispose()
        {
            if (ok) { try { nvmlShutdown(); } catch { } ok = false; }
        }
    }

    // ------------------------------------------------------------------ app

    sealed class App : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string RunName = "MouseOverlay";
        const int TrackPeriodMs = 8; // ~125 position updates/s; the compositor can't show much more anyway
        const int STATE_OVERLAY = 1, STATE_HIDE_REAL = 2;

        readonly Settings cfg;
        readonly CursorWindow cursorWin;
        readonly LayeredWindow tempWin;
        readonly NotifyIcon tray;
        readonly Tracker tracker;
        readonly System.Windows.Forms.Timer zTimer, tempTimer, reapplyTimer;
        CpuTemp cpu;
        GpuTemp gpu;

        // UI thread state
        int zTicks, tempTicks, cursorWidth, hotX, hotY;
        bool magInit, magHidden, cursorsReplaced;
        string lastTempText = "";
        Size tempSize;
        IntPtr trayIconHandle = IntPtr.Zero;

        // Shared with the tracker thread
        long hotspot;                          // (hotX << 32) | hotY, published atomically
        volatile int lastState = -1;           // tracker thread's last posted state
        volatile int zGeneration;              // bumped by the UI thread after every raise

        // Tracker-thread-only cache for the "covered" test
        int trackTicks, lastX = int.MinValue, lastY = int.MinValue;
        const int COVERED_BY_WINDOW = 1, COVERED_D3D = 2;
        IntPtr coveredRoot, raiseRoot;
        int coveredCached, coveredTick = -1000, coveredGen = -1, raiseGen;
        static readonly int[] MediaKeys = { 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3 }; // volume mute/down/up, media next/prev/stop/play
        static readonly string[] ShellHosts = { "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe",
                                                "searchapp.exe", "shellhost.exe", "textinputhost.exe" };
        readonly Dictionary<uint, bool> shellPids = new Dictionary<uint, bool>();
        IntPtr lastFg;
        bool lastFgShell;
        int mediaKeyTick = -100000, shellPidsClearedTick;
        readonly HashSet<IntPtr> unbeatableRoots = new HashSet<IntPtr>();
        int unbeatableClearedTick;

        public App()
        {
            cfg = Settings.Load();
            tempWin = new LayeredWindow();      // created first so the pointer window starts above it
            cursorWin = new CursorWindow(this);

            tray = new NotifyIcon();
            tray.Text = "Mouse Overlay";
            tray.ContextMenu = BuildMenu();
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { cfg.Enabled = !cfg.Enabled; Changed(); }
            };

            tracker = new Tracker(Sync, TrackPeriodMs);

            // Pushes the overlay back above newly opened menus / topmost windows.
            zTimer = new System.Windows.Forms.Timer();
            zTimer.Interval = 250;
            zTimer.Tick += delegate { ZTick(); };

            tempTimer = new System.Windows.Forms.Timer();
            tempTimer.Interval = 2000;
            tempTimer.Tick += delegate { UpdateTemps(); };

            // Windows re-applies the cursor scheme a little after it broadcasts the change.
            reapplyTimer = new System.Windows.Forms.Timer();
            reapplyTimer.Interval = 600;
            reapplyTimer.Tick += delegate { reapplyTimer.Stop(); EnsureReplacement(); OnDisplayChange(); };

            // Clean up if a previous instance died mid-way.
            SystemCursors.Restore();
            if (Native.MagInitialize()) { magInit = true; Native.MagShowSystemCursor(true); }

            Apply();
            tray.Visible = true;

            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }

        // ---- cursor tracking

        void ZTick()
        {
            zTicks++;
            if ((zTicks & 127) == 0) cursorWin.ForgetUnbeatable(); // window handles get recycled
            if (cursorWin.RaiseIfCovered()) zGeneration++;
            if (magHidden) Native.MagShowSystemCursor(false);       // in case something re-showed it
            if ((zTicks & 15) == 0) EnsureReplacement();            // every 4 s
        }

        // Runs on the tracker thread: decides overlay / real-cursor state, moves the overlay.
        void Sync()
        {
            trackTicks++;
            Native.CURSORINFO ci = new Native.CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO));
            if (!Native.GetCursorInfo(ref ci)) return; // e.g. secure desktop up; keep current state
            bool showing = (ci.flags & Native.CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero;
            bool foreign = !SystemCursors.IsSystem(ci.hCursor);

            RealCursorMode mode = cfg.RealCursor;
            bool replacing = mode == RealCursorMode.Replace || mode == RealCursorMode.ArrowOnly;
            bool overlay = showing ? (!replacing || foreign) : !cfg.AutoHide;
            bool hideReal = mode == RealCursorMode.Hide || (replacing && showing && foreign);
            // Never take the real cursor away where the overlay cannot be drawn.
            bool shellUi = ShellUiUp();
            if (shellUi) { overlay = false; hideReal = false; }
            else if (overlay || hideReal)
            {
                if (Covered(ci.pt) == COVERED_BY_WINDOW) { overlay = false; hideReal = false; }
            }

            int state = (overlay ? STATE_OVERLAY : 0) | (hideReal ? STATE_HIDE_REAL : 0);
            if (state != lastState)
            {
                lastState = state;
                Native.PostMessage(cursorWin.Handle, CursorWindow.WM_APP_STATE, new IntPtr(state), IntPtr.Zero);
            }
            if (!overlay) return;

            if (ci.pt.x != lastX || ci.pt.y != lastY)
            {
                long hs = Interlocked.Read(ref hotspot);
                lastX = ci.pt.x; lastY = ci.pt.y;
                cursorWin.MoveTo(ci.pt.x - (int)(hs >> 32), ci.pt.y - (int)(hs & 0xFFFFFFFF));
            }
        }

        // Tracker thread. Windows 11 shell surfaces (Start, Quick Settings, notification centre,
        // the volume / media OSD, touch keyboard) live in a z-band that no ordinary window can reach,
        // and they are invisible to WindowFromPoint / EnumWindows from here. Two things do work:
        // the interactive ones take the foreground, and the OSD follows a media key press.
        bool ShellUiUp()
        {
            foreach (int vk in MediaKeys)
                if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) { mediaKeyTick = trackTicks; break; }
            if (trackTicks - mediaKeyTick < 500) return true;      // OSD stays up ~3-4 s after the key

            IntPtr fg = Native.GetForegroundWindow();
            if (fg != lastFg)
            {
                lastFg = fg;
                lastFgShell = fg != IntPtr.Zero && IsShellWindow(fg);
            }
            return lastFgShell;
        }

        bool IsShellWindow(IntPtr h)
        {
            System.Text.StringBuilder cls = new System.Text.StringBuilder(64);
            Native.GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() == "XamlExplorerHostIslandWindow") return true; // Alt-Tab / Task View
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (trackTicks - shellPidsClearedTick > 7500) { shellPids.Clear(); shellPidsClearedTick = trackTicks; }
            bool shell;
            if (shellPids.TryGetValue(pid, out shell)) return shell;
            shell = false;
            IntPtr proc = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc != IntPtr.Zero)
            {
                System.Text.StringBuilder path = new System.Text.StringBuilder(1024);
                int len = path.Capacity;
                if (Native.QueryFullProcessImageName(proc, 0, path, ref len))
                    shell = Array.IndexOf(ShellHosts, Path.GetFileName(path.ToString()).ToLowerInvariant()) >= 0;
                Native.CloseHandle(proc);
            }
            if (shellPids.Count > 64) shellPids.Clear();
            shellPids[pid] = shell;
            return shell;
        }

        // Tracker thread. COVERED_BY_WINDOW when the window under the pointer sits above the overlay
        // in z-order (Windows 11 shell flyouts, Start, etc.); COVERED_D3D when the shell reports a
        // D3D full-screen app (the overlay may be bypassed). Cached per window and re-checked every
        // ~256 ms or after the UI thread raised us.
        int Covered(Native.POINT pt)
        {
            IntPtr root = Native.GetAncestor(Native.WindowFromPoint(pt), Native.GA_ROOT);
            int gen = zGeneration;
            if (root == coveredRoot && gen == coveredGen && trackTicks - coveredTick < 32) return coveredCached;
            if (trackTicks - unbeatableClearedTick > 4000) { unbeatableRoots.Clear(); unbeatableClearedTick = trackTicks; }

            int covered = 0;
            if (root != IntPtr.Zero && !LayeredWindow.IsOwn(root) && cursorWin.IsBelow(root))
            {
                covered = COVERED_BY_WINDOW;
                if (root == raiseRoot && gen != raiseGen) unbeatableRoots.Add(root); // UI raised us; still below
                else if (root != raiseRoot && !unbeatableRoots.Contains(root))
                {
                    raiseRoot = root; raiseGen = gen;
                    Native.PostMessage(cursorWin.Handle, CursorWindow.WM_APP_RAISE, IntPtr.Zero, IntPtr.Zero);
                }
            }
            coveredRoot = root; coveredGen = gen; coveredTick = trackTicks; coveredCached = covered;
            return covered;
        }

        // UI thread, from WM_APP_RAISE: try to get above whatever the tracker found covering us.
        public void RaiseRequested()
        {
            if (!cfg.Enabled) return;
            cursorWin.Raise();
            zGeneration++;
        }

        // UI thread, from WM_APP_STATE. Ordered so there is never a moment with no pointer at all.
        public void ApplyState(int state)
        {
            if (!cfg.Enabled) return;
            bool show = (state & STATE_OVERLAY) != 0, hide = (state & STATE_HIDE_REAL) != 0;
            if (show && !cursorWin.Visible)
            {
                cursorWin.Visible = true;
                cursorWin.Raise();   // ShowWindow keeps the old z-position; newer topmost windows may sit above
                zGeneration++;
            }
            SetRealCursorHidden(hide);
            if (!show) cursorWin.Visible = false;
        }

        public void OnSystemChange()
        {
            reapplyTimer.Stop();
            reapplyTimer.Start();
        }

        void OnDisplayChange()
        {
            lastTempText = "";
            if (cfg.ShowTemps) UpdateTemps();
        }

        // ---- applying settings

        void Changed()
        {
            cfg.Save();
            Apply();
        }

        void Apply()
        {
            int hx, hy;
            using (Bitmap bmp = RenderCursor(cfg, cfg.Size, out hx, out hy))
            {
                cursorWin.SetBitmap(bmp);
                cursorWidth = bmp.Width;
                hotX = hx; hotY = hy;
                Interlocked.Exchange(ref hotspot, ((long)hx << 32) | (uint)hy);

                if (cursorsReplaced) { SystemCursors.Restore(); cursorsReplaced = false; }
                if (cfg.Enabled && ReplaceIds() != null)
                {
                    SystemCursors.Replace(bmp, hx, hy, ReplaceIds());
                    cursorsReplaced = true;
                }
            }

            lastX = int.MinValue;
            lastState = -1;
            zTimer.Enabled = cfg.Enabled;
            if (cfg.Enabled)
            {
                tracker.Start();
            }
            else
            {
                tracker.Stop();
                cursorWin.Visible = false;
                SetRealCursorHidden(false);
            }

            if (cfg.ShowTemps)
            {
                if (cpu == null) cpu = new CpuTemp();
                if (gpu == null) gpu = new GpuTemp();
                lastTempText = "";
                UpdateTemps();
                tempWin.Visible = true;
                tempTimer.Start();
            }
            else
            {
                tempTimer.Stop();
                tempWin.Visible = false;
                if (cpu != null) { cpu.Dispose(); cpu = null; }
                if (gpu != null) { gpu.Dispose(); gpu = null; }
            }

            cursorWin.Raise(); // pointer stays above the temperature label
            UpdateTrayIcon();
            SyncMenuChecks(tray.ContextMenu.MenuItems);
        }

        int[] ReplaceIds()
        {
            if (cfg.RealCursor == RealCursorMode.Replace) return SystemCursors.AllIds;
            if (cfg.RealCursor == RealCursorMode.ArrowOnly) return new int[] { SystemCursors.OCR_NORMAL };
            return null;
        }

        // Re-applies the replaced cursors if Windows reloaded its scheme behind our back.
        void EnsureReplacement()
        {
            if (!cursorsReplaced || SystemCursors.ArrowMatches(cursorWidth, hotX, hotY)) return;
            int hx, hy;
            using (Bitmap bmp = RenderCursor(cfg, cfg.Size, out hx, out hy))
                SystemCursors.Replace(bmp, hx, hy, ReplaceIds());
        }

        // UI thread only. Magnification API hides the cursor system-wide; state is not
        // reference counted, so it is simply re-applied when needed.
        void SetRealCursorHidden(bool hide)
        {
            try
            {
                if (hide)
                {
                    if (!magInit) magInit = Native.MagInitialize();
                    if (magInit) Native.MagShowSystemCursor(false);
                    magHidden = true;
                }
                else if (magHidden)
                {
                    if (magInit) Native.MagShowSystemCursor(true);
                    magHidden = false;
                }
            }
            catch { magInit = false; magHidden = false; }
        }

        // ---- drawing

        static Bitmap RenderCursor(Settings s, int size, out int hx, out int hy)
        {
            float bw = s.BorderWidth * (size / (float)Math.Max(s.Size, 1));
            int pad = (int)Math.Ceiling(bw) + 2;
            int dim = size + pad * 2;
            int alpha = s.Opacity * 255 / 100;
            Bitmap bmp = new Bitmap(dim, dim, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            using (GraphicsPath path = new GraphicsPath())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                if (s.Shape == CursorShape.Circle)
                {
                    path.AddEllipse(pad, pad, size, size);
                    hx = hy = pad + size / 2;
                }
                else
                {
                    // Notched "V" arrowhead, tip on the hotspot, pointing up-left like the stock arrow.
                    float k = size;
                    path.AddPolygon(new PointF[] {
                        new PointF(pad, pad),
                        new PointF(pad + k * 1.00f, pad + k * 0.41f),
                        new PointF(pad + k * 0.50f, pad + k * 0.50f),
                        new PointF(pad + k * 0.41f, pad + k * 1.00f)
                    });
                    hx = hy = pad;
                }
                if (!s.NoFill)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, s.Center)))
                        g.FillPath(b, path);
                if (bw > 0)
                    using (Pen pen = new Pen(Color.FromArgb(alpha, s.Border), bw))
                    {
                        pen.LineJoin = LineJoin.Round;
                        g.DrawPath(pen, path);
                    }
            }
            return bmp;
        }

        void UpdateTrayIcon()
        {
            int hx, hy;
            using (Bitmap shape = RenderCursor(cfg, 24, out hx, out hy))
            using (Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(shape, new Rectangle(0, 0, 32, 32));
                    if (!cfg.Enabled)
                        using (Pen p = new Pen(Color.Red, 3)) g.DrawLine(p, 3, 29, 29, 3);
                }
                IntPtr h = bmp.GetHicon();
                tray.Icon = Icon.FromHandle(h);
                if (trayIconHandle != IntPtr.Zero) Native.DestroyIcon(trayIconHandle);
                trayIconHandle = h;
            }
            tray.Text = cfg.Enabled ? "Mouse Overlay" : "Mouse Overlay (off)";
        }

        static string Fmt(double t)
        {
            return double.IsNaN(t) ? "--" : ((int)Math.Round(t)).ToString(CultureInfo.InvariantCulture) + "°";
        }

        static Color TempColor(double t)
        {
            if (double.IsNaN(t)) return Color.Silver;
            if (t >= 90) return Color.FromArgb(255, 80, 80);
            if (t >= 80) return Color.FromArgb(255, 200, 60);
            return Color.White;
        }

        void UpdateTemps()
        {
            if (cpu == null || gpu == null) return;
            double c = cpu.Read(), gp = gpu.Read();
            string cs = "CPU " + Fmt(c), gs = "GPU " + Fmt(gp);
            string text = cs + "|" + gs;
            if (text != lastTempText)
            {
                lastTempText = text;
                using (Bitmap bmp = RenderTemps(cs, TempColor(c), gs, TempColor(gp)))
                {
                    tempSize = bmp.Size;
                    PlaceTempWindow();
                    tempWin.SetBitmap(bmp);
                }
            }
            else PlaceTempWindow(); // work area may have changed (taskbar auto-hide etc.)
            if ((++tempTicks & 15) == 0) tempWin.ForgetUnbeatable();
            if (tempWin.RaiseIfCovered()) { cursorWin.Raise(); zGeneration++; } // keep the pointer above the label
        }

        void PlaceTempWindow()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            tempWin.MoveTo(wa.Right - tempSize.Width - 4, wa.Bottom - tempSize.Height - 2);
        }

        static float PrimaryDpi()
        {
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                uint dx, dy;
                IntPtr mon = Native.MonitorFromPoint(new Native.POINT(wa.Right - 1, wa.Bottom - 1), 1 /* MONITOR_DEFAULTTOPRIMARY */);
                if (Native.GetDpiForMonitor(mon, Native.MDT_EFFECTIVE_DPI, out dx, out dy) == 0 && dy > 0) return dy;
            }
            catch { }
            using (Graphics g0 = Graphics.FromHwnd(IntPtr.Zero)) return g0.DpiY;
        }

        Bitmap RenderTemps(string a, Color ca, string b, Color cb)
        {
            float em = cfg.TempFontSize * PrimaryDpi() / 72f;
            using (FontFamily ff = new FontFamily("Segoe UI"))
            using (GraphicsPath pa = new GraphicsPath())
            using (GraphicsPath pb = new GraphicsPath())
            {
                pa.AddString(a, ff, (int)FontStyle.Bold, em, new PointF(0, 0), StringFormat.GenericTypographic);
                pb.AddString(b, ff, (int)FontStyle.Bold, em, new PointF(0, 0), StringFormat.GenericTypographic);
                RectangleF ra = pa.GetBounds(), rb = pb.GetBounds();
                float gap = em * 0.7f, pad = 3f;
                int w = (int)Math.Ceiling(ra.Width + gap + rb.Width + pad * 2);
                int h = (int)Math.Ceiling(em * 1.35f + pad * 2);
                Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                using (Pen outline = new Pen(Color.FromArgb(230, 0, 0, 0), Math.Max(2f, em * 0.22f)))
                {
                    outline.LineJoin = LineJoin.Round;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TranslateTransform(pad - ra.X, pad);
                    g.DrawPath(outline, pa);
                    using (SolidBrush br = new SolidBrush(ca)) g.FillPath(br, pa);
                    g.TranslateTransform(ra.X + ra.Width + gap - rb.X, 0);
                    g.DrawPath(outline, pb);
                    using (SolidBrush br = new SolidBrush(cb)) g.FillPath(br, pb);
                }
                return bmp;
            }
        }

        // ---- tray menu

        // Each checkable item carries a Func<bool> in Tag so one pass can refresh all check marks.
        MenuItem Item(string text, Func<bool> isChecked, Action onClick, bool radio)
        {
            MenuItem mi = new MenuItem(text);
            mi.RadioCheck = radio;
            mi.Tag = isChecked;
            mi.Click += delegate { onClick(); Changed(); };
            return mi;
        }

        MenuItem IntChoice(string title, int[] values, string suffix, Func<int> get, Action<int> set)
        {
            MenuItem parent = new MenuItem(title);
            foreach (int v in values)
            {
                int val = v;
                parent.MenuItems.Add(Item(val + suffix, delegate { return get() == val; }, delegate { set(val); }, true));
            }
            return parent;
        }

        MenuItem ModeItem(string text, RealCursorMode mode)
        {
            return Item(text, delegate { return cfg.RealCursor == mode; }, delegate { cfg.RealCursor = mode; }, true);
        }

        ContextMenu BuildMenu()
        {
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add(Item("Overlay enabled", delegate { return cfg.Enabled; }, delegate { cfg.Enabled = !cfg.Enabled; }, false));
            menu.MenuItems.Add("-");

            MenuItem shape = new MenuItem("Shape");
            shape.MenuItems.Add(Item("V arrow", delegate { return cfg.Shape == CursorShape.VArrow; }, delegate { cfg.Shape = CursorShape.VArrow; }, true));
            shape.MenuItems.Add(Item("Circle", delegate { return cfg.Shape == CursorShape.Circle; }, delegate { cfg.Shape = CursorShape.Circle; }, true));
            menu.MenuItems.Add(shape);

            menu.MenuItems.Add(IntChoice("Size", new int[] { 12, 16, 24, 32, 48, 64, 96, 128 }, " px",
                delegate { return cfg.Size; }, delegate(int v) { cfg.Size = v; }));
            menu.MenuItems.Add(IntChoice("Border width", new int[] { 0, 1, 2, 3, 4, 6, 8 }, " px",
                delegate { return cfg.BorderWidth; }, delegate(int v) { cfg.BorderWidth = v; }));

            MenuItem center = new MenuItem("Center color...");
            center.Click += delegate { Color c; if (PickColor(cfg.Center, out c)) { cfg.Center = c; cfg.NoFill = false; Changed(); } };
            menu.MenuItems.Add(center);
            MenuItem border = new MenuItem("Border color...");
            border.Click += delegate { Color c; if (PickColor(cfg.Border, out c)) { cfg.Border = c; Changed(); } };
            menu.MenuItems.Add(border);
            menu.MenuItems.Add(Item("Hollow center (border only)", delegate { return cfg.NoFill; }, delegate { cfg.NoFill = !cfg.NoFill; }, false));

            menu.MenuItems.Add(IntChoice("Opacity", new int[] { 100, 80, 60, 40, 25 }, " %",
                delegate { return cfg.Opacity; }, delegate(int v) { cfg.Opacity = v; }));

            menu.MenuItems.Add("-");
            MenuItem real = new MenuItem("Real cursor");
            real.MenuItems.Add(ModeItem("Replace with overlay shape (recommended)", RealCursorMode.Replace));
            real.MenuItems.Add(ModeItem("Replace arrow only, keep text / resize cursors", RealCursorMode.ArrowOnly));
            real.MenuItems.Add(ModeItem("Hide it, draw overlay only", RealCursorMode.Hide));
            real.MenuItems.Add(ModeItem("Leave it, draw overlay on top", RealCursorMode.AsIs));
            menu.MenuItems.Add(real);
            menu.MenuItems.Add(Item("Hide overlay when app hides cursor", delegate { return cfg.AutoHide; }, delegate { cfg.AutoHide = !cfg.AutoHide; }, false));

            menu.MenuItems.Add("-");
            menu.MenuItems.Add(Item("Show CPU / GPU temperature", delegate { return cfg.ShowTemps; }, delegate { cfg.ShowTemps = !cfg.ShowTemps; }, false));
            menu.MenuItems.Add(IntChoice("Temperature text size", new int[] { 7, 8, 9, 10, 12, 14 }, " pt",
                delegate { return cfg.TempFontSize; }, delegate(int v) { cfg.TempFontSize = v; }));

            menu.MenuItems.Add("-");
            menu.MenuItems.Add(Item("Start with Windows", IsAutoStart, delegate { SetAutoStart(!IsAutoStart()); }, false));
            MenuItem exit = new MenuItem("Exit");
            exit.Click += delegate { ExitThread(); };
            menu.MenuItems.Add(exit);
            return menu;
        }

        static void SyncMenuChecks(Menu.MenuItemCollection items)
        {
            foreach (MenuItem mi in items)
            {
                Func<bool> f = mi.Tag as Func<bool>;
                if (f != null) mi.Checked = f();
                if (mi.MenuItems.Count > 0) SyncMenuChecks(mi.MenuItems);
            }
        }

        static bool PickColor(Color current, out Color picked)
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.Color = current;
                dlg.FullOpen = true;
                bool ok = dlg.ShowDialog() == DialogResult.OK;
                picked = ok ? dlg.Color : current;
                return ok;
            }
        }

        // Task Manager disables startup entries by writing a record under StartupApproved (first
        // byte odd = disabled) rather than deleting the Run value, so both places are checked.
        static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    if (k == null || k.GetValue(RunName) == null) return false;
                using (RegistryKey a = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                {
                    byte[] rec = a == null ? null : a.GetValue(RunName) as byte[];
                    return rec == null || rec.Length == 0 || (rec[0] & 1) == 0;
                }
            }
            catch { return false; }
        }

        static void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
                using (RegistryKey a = Registry.CurrentUser.CreateSubKey(ApprovedKey))
                {
                    if (on) a.SetValue(RunName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                    else a.DeleteValue(RunName, false);
                }
            }
            catch { }
        }

        // ---- shutdown

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tracker.Stop();
                SetRealCursorHidden(false);
                if (magInit) { Native.MagUninitialize(); magInit = false; }
                if (cursorsReplaced) { SystemCursors.Restore(); cursorsReplaced = false; }
                zTimer.Dispose();
                tempTimer.Dispose();
                reapplyTimer.Dispose();
                tray.Visible = false;
                tray.Dispose();
                if (trayIconHandle != IntPtr.Zero) { Native.DestroyIcon(trayIconHandle); trayIconHandle = IntPtr.Zero; }
                if (cpu != null) cpu.Dispose();
                if (gpu != null) gpu.Dispose();
                cursorWin.Dispose();
                tempWin.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
