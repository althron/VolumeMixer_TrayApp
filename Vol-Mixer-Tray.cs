using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;                 // embedded resources
using System.Runtime.InteropServices;    // DPI + Win32 helpers
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;                   // theme from registry
// Disambiguate Timer
using Timer = System.Windows.Forms.Timer;

namespace VolMixerTray
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { /* best-effort */ }

            try
            {
                // Defaults (can be overridden via args)
                int timeoutMs        = 30000; // watch time
                int pollMs           = 250;   // timer interval
                int distancePx       = 300;   // auto-close distance
                int pad              = 8;     // edge padding
                bool useMouseMonitor = true;  // choose monitor under cursor

                foreach (string a in args)
                {
                    if (a.StartsWith("--timeoutMs=", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(a.Substring(12), out timeoutMs);
                    else if (a.StartsWith("--distancePx=", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(a.Substring(13), out distancePx);
                    else if (a.StartsWith("--pad=", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(a.Substring(6), out pad);
                    else if (a.StartsWith("--monitor=", StringComparison.OrdinalIgnoreCase))
                        useMouseMonitor = !a.EndsWith("primary", StringComparison.OrdinalIgnoreCase);
                }

                bool first;

                // Per-user/session mutex (no Global\ → no special privileges needed)
                using (var m = new Mutex(true, @"VolMixerTray_Mutex", out first))
                {
                    if (!first) return;

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    // Run an ApplicationContext, NOT a Form -> no visible window
                    Application.Run(new TrayContext(timeoutMs, pollMs, distancePx, pad, useMouseMonitor));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("VolMixerTray failed to start:\r\n\r\n" + ex,
                    "VolMixerTray Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // Tray-only app: no Form, no Alt+Tab entry
    sealed class TrayContext : ApplicationContext
    {
        // Embedded resource logical names (provided at compile time with /resource:)
        const string LightIconRes = "VolMixerTray.Icons.Light.ico"; // volmixer_black.ico
        const string DarkIconRes  = "VolMixerTray.Icons.Dark.ico";  // volmixer.ico

        enum IconThemeMode { Auto = 0, Light = 1, Dark = 2 }
        enum AppLanguage   { English = 0, Spanish = 1 }

        const string RegLangKeyPath  = @"Software\VolMixerTray";
        const string RegLangValue    = "Language"; // "en" / "es"

        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly Timer timer;            // watcher
        readonly Timer themeTimer;       // theme polling when Auto
        readonly int totalWatchMs, pollMs, distancePx, pad;
        readonly bool useMouseMonitor;

        // State
        Point anchor;              // where we want SndVol (physical px)
        Point startMouse;          // mouse at launch (physical px)
        DateTime startedUtc;
        DateTime graceUntilUtc;
        Rectangle activeBounds;    // monitor bounds
        Rectangle safeZoneBRQ;     // bottom-right quarter
        Process sndVol;

        // Icons
        Icon exeFallbackIcon, lightIcon, darkIcon;

        // Theme radio
        IconThemeMode iconMode = IconThemeMode.Auto;
        int lastObservedAppsThemeLight = -1;

        // Menu items (so we can localize them)
        ToolStripMenuItem miOpenMixer;
        ToolStripMenuItem miThemeRoot, miThemeAuto, miThemeLight, miThemeDark;
        ToolStripMenuItem miLanguageRoot, miLangEn, miLangEs;
        ToolStripMenuItem miExit;

        AppLanguage currentLanguage;

        // ===== Win32 helpers =====
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        static readonly IntPtr HWND_TOP = IntPtr.Zero;
        const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        static IntPtr FindTopLevelWindowByPid(int pid, int maxTries, int sleepMs)
        {
            for (int t = 0; t < maxTries; t++)
            {
                IntPtr found = IntPtr.Zero;
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    uint procId;
                    GetWindowThreadProcessId(h, out procId);
                    if (procId == (uint)pid && IsWindowVisible(h))
                    {
                        found = h;
                        return false; // stop enum
                    }
                    return true; // continue
                }, IntPtr.Zero);

                if (found != IntPtr.Zero) return found;
                System.Threading.Thread.Sleep(sleepMs);
            }
            return IntPtr.Zero;
        }

        static void MoveWindowBottomRight(IntPtr hWnd, Rectangle screenBounds, Point anchorBR)
        {
            RECT r;
            if (!GetWindowRect(hWnd, out r)) return;
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;

            // place so window's bottom-right == anchorBR
            int x = anchorBR.X - w;
            int y = anchorBR.Y - h;

            // clamp to monitor bounds
            if (x < screenBounds.Left) x = screenBounds.Left;
            if (y < screenBounds.Top)  y = screenBounds.Top;
            if (x + w > screenBounds.Right)  x = screenBounds.Right - w;
            if (y + h > screenBounds.Bottom) y = screenBounds.Bottom - h;

            SetWindowPos(hWnd, HWND_TOP, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        static string GetSndVolPath()
        {
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            bool wow64 = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess;
            return System.IO.Path.Combine(windir, wow64 ? @"Sysnative\SndVol.exe" : @"System32\SndVol.exe");
        }
        // ==========================

        public TrayContext(int totalWatchMs, int pollMs, int distancePx, int pad, bool useMouseMonitor)
        {
            this.totalWatchMs     = totalWatchMs;
            this.pollMs           = pollMs;
            this.distancePx       = distancePx;
            this.pad              = pad;
            this.useMouseMonitor  = useMouseMonitor;

            try { exeFallbackIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { exeFallbackIcon = SystemIcons.Application; }

            lightIcon = LoadEmbeddedIcon(LightIconRes);
            darkIcon  = LoadEmbeddedIcon(DarkIconRes);

            // Context menu
            menu = new ContextMenuStrip();

            miOpenMixer = new ToolStripMenuItem("Open Volume Mixer", null, new EventHandler(OpenClick));

            miThemeRoot = new ToolStripMenuItem("Tray Icon Theme");
            miThemeAuto  = new ToolStripMenuItem("Follow Windows theme (Auto)", null, delegate { SetIconMode(IconThemeMode.Auto); });
            miThemeLight = new ToolStripMenuItem("Use Dark Icon",               null, delegate { SetIconMode(IconThemeMode.Light); });
            miThemeDark  = new ToolStripMenuItem("Use Light Icon",              null, delegate { SetIconMode(IconThemeMode.Dark); });
            miThemeRoot.DropDownItems.Add(miThemeAuto);
            miThemeRoot.DropDownItems.Add(miThemeLight);
            miThemeRoot.DropDownItems.Add(miThemeDark);

            // Language submenu
            miLanguageRoot = new ToolStripMenuItem("Language");
            miLangEn = new ToolStripMenuItem("English", null, delegate { SetLanguage(AppLanguage.English); });
            miLangEs = new ToolStripMenuItem("Spanish", null, delegate { SetLanguage(AppLanguage.Spanish); });
            miLanguageRoot.DropDownItems.Add(miLangEn);
            miLanguageRoot.DropDownItems.Add(miLangEs);

            miExit = new ToolStripMenuItem("Exit", null, new EventHandler(ExitClick));

            menu.Items.Add(miOpenMixer);
            menu.Items.Add(miThemeRoot);
            menu.Items.Add(miLanguageRoot);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);

            tray = new NotifyIcon();
            tray.Icon = exeFallbackIcon;
            tray.Visible = true;
            tray.ContextMenuStrip = menu;
            tray.MouseClick += new MouseEventHandler(TrayClick);

            // Timers must be created before SetIconMode / UpdateThemeTimer
            timer = new Timer();
            timer.Interval = this.pollMs;
            timer.Tick += new EventHandler(TimerTick);

            themeTimer = new Timer();
            themeTimer.Interval = 1500;
            themeTimer.Tick += new EventHandler(ThemeTimerTick);

            // Initial theme + language
            SetIconMode(IconThemeMode.Auto);
            currentLanguage = DetectInitialLanguage();
            ApplyLanguage();         // set texts according to currentLanguage

            tray.ShowBalloonTip(1500);
        }

        // ===== Language helpers =====
        static AppLanguage DetectInitialLanguage()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegLangKeyPath))
                {
                    if (k != null)
                    {
                        object raw = k.GetValue(RegLangValue);
                        string v = raw as string;
                        if (!string.IsNullOrEmpty(v))
                        {
                            if (string.Equals(v, "es", StringComparison.OrdinalIgnoreCase))
                                return AppLanguage.Spanish;
                            if (string.Equals(v, "en", StringComparison.OrdinalIgnoreCase))
                                return AppLanguage.English;
                        }
                    }
                }
            }
            catch { }

            // Fallback to OS UI language
            try
            {
                CultureInfo ui = CultureInfo.InstalledUICulture; // or CurrentUICulture
                string two = ui.TwoLetterISOLanguageName;
                if (string.Equals(two, "es", StringComparison.OrdinalIgnoreCase))
                    return AppLanguage.Spanish;
            }
            catch { }

            return AppLanguage.English;
        }

        static void SaveLanguage(AppLanguage lang)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegLangKeyPath))
                {
                    if (k != null)
                    {
                        string code = (lang == AppLanguage.Spanish) ? "es" : "en";
                        k.SetValue(RegLangValue, code, RegistryValueKind.String);
                    }
                }
            }
            catch { }
        }

        void SetLanguage(AppLanguage lang)
        {
            currentLanguage = lang;
            SaveLanguage(lang);
            ApplyLanguage();
        }

        void ApplyLanguage()
        {
            bool es = currentLanguage == AppLanguage.Spanish;

            // Menu texts
            if (miOpenMixer != null)
                miOpenMixer.Text = es ? "Abrir mezclador de volumen" : "Open Volume Mixer";

            if (miThemeRoot != null)
                miThemeRoot.Text = es ? "Tema del icono de bandeja" : "Tray Icon Theme";
            if (miThemeAuto != null)
                miThemeAuto.Text = es ? "Seguir tema de Windows (Auto)" : "Follow Windows theme (Auto)";
            if (miThemeLight != null)
                miThemeLight.Text = es ? "Usar icono oscuro" : "Use Dark Icon";
            if (miThemeDark != null)
                miThemeDark.Text = es ? "Usar icono claro" : "Use Light Icon";

            if (miLanguageRoot != null)
                miLanguageRoot.Text = es ? "Idioma" : "Language";
            if (miLangEn != null)
                miLangEn.Text = es ? "Inglés" : "English";
            if (miLangEs != null)
                miLangEs.Text = es ? "Español" : "Spanish";

            if (miExit != null)
                miExit.Text = es ? "Salir (Cerrar App)" : "Exit (Close App)";

            // Check marks for language
            if (miLangEn != null) miLangEn.Checked = (currentLanguage == AppLanguage.English);
            if (miLangEs != null) miLangEs.Checked = (currentLanguage == AppLanguage.Spanish);

            // Tray tooltip + balloon
            if (tray != null)
            {
                tray.Text = es ? "Mezclador de volumen" : "Volume Mixer";
                tray.BalloonTipTitle = es ? "Mezclador de volumen" : "Volume Mixer";
                tray.BalloonTipText  = es
                    ? "La bandeja está activa. Haz clic izquierdo para mostrar el mezclador clásico."
                    : "Tray is running. Left-click to toggle the classic mixer.";
            }
        }

        // ==========================

        static Icon LoadEmbeddedIcon(string resourceName)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (System.IO.Stream s = asm.GetManifestResourceStream(resourceName))
                {
                    if (s != null) return new Icon(s);
                }
            }
            catch { }
            return null;
        }

        void SetIconMode(IconThemeMode mode)
        {
            iconMode = mode;
            if (miThemeAuto  != null) miThemeAuto.Checked  = (mode == IconThemeMode.Auto);
            if (miThemeLight != null) miThemeLight.Checked = (mode == IconThemeMode.Light);
            if (miThemeDark  != null) miThemeDark.Checked  = (mode == IconThemeMode.Dark);
            ApplyTrayIcon();
            UpdateThemeTimer();
        }

        void ApplyTrayIcon()
        {
            Icon target = exeFallbackIcon;
            if (iconMode == IconThemeMode.Light)
            {
                if (lightIcon != null) target = lightIcon;
            }
            else if (iconMode == IconThemeMode.Dark)
            {
                if (darkIcon != null) target = darkIcon;
            }
            else
            {
                int theme = ReadAppsUseLightTheme();
                if (theme == 1) { if (lightIcon != null) target = lightIcon; }
                else            { if (darkIcon  != null) target = darkIcon;  } // default to dark icon
            }
            try { tray.Icon = target; } catch { }
        }

        void UpdateThemeTimer()
        {
            if (themeTimer == null) return; // safety guard

            if (iconMode == IconThemeMode.Auto)
            {
                lastObservedAppsThemeLight = ReadAppsUseLightTheme();
                themeTimer.Start();
            }
            else
            {
                themeTimer.Stop();
            }
        }

        void ThemeTimerTick(object sender, EventArgs e)
        {
            if (iconMode != IconThemeMode.Auto) return;
            int cur = ReadAppsUseLightTheme();
            if (cur != lastObservedAppsThemeLight)
            {
                lastObservedAppsThemeLight = cur;
                ApplyTrayIcon();
            }
        }

        static int ReadAppsUseLightTheme()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int)
                        {
                            int iv = (int)v;
                            if (iv == 0) return 0; // Dark
                            if (iv == 1) return 1; // Light
                        }
                    }
                }
            }
            catch { }
            return -1;
        }

        // === UI events ===
        void TrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) Toggle();
        }

        void OpenClick(object s, EventArgs e)
        {
            OpenVolumeMixerSettings();
        }

        void ExitClick(object s, EventArgs e)
        {
            try { tray.Visible = false; } catch { }
            ExitThread();   // closes the message loop and ends the app
        }

        // Settings > Sound > Volume Mixer
        static void OpenVolumeMixerSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true });
                return;
            }
            catch { }

            foreach (string uri in new[] { "ms-settings:sound", "ms-settings:system-sound" })
            {
                try
                {
                    Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                    return;
                }
                catch { }
            }
        }

        // === Classic SndVol toggle (robust) ===
        void Toggle()
        {
            try
            {
                // Close existing mixers
                foreach (Process proc in Process.GetProcessesByName("SndVol"))
                {
                    try { proc.Kill(); } catch { }
                }

                if (sndVol != null && !sndVol.HasExited)
                {
                    try { sndVol.Kill(); } catch { }
                    sndVol = null;
                    return;
                }

                // Determine target screen using PHYSICAL cursor position
                POINT pnt; if (!GetCursorPos(out pnt)) pnt = new POINT { X = 0, Y = 0 };
                Point physMouse = new Point(pnt.X, pnt.Y);

                Screen scr = useMouseMonitor ? Screen.FromPoint(physMouse) : Screen.PrimaryScreen;
                activeBounds = scr.Bounds;
                safeZoneBRQ = new Rectangle(
                    activeBounds.Left + activeBounds.Width / 2,
                    activeBounds.Top  + activeBounds.Height / 2,
                    activeBounds.Width / 2,
                    activeBounds.Height / 2
                );

                // Bottom-right anchor inside WorkingArea
                anchor = ComputeAnchor(scr, pad);

                // Pack (y<<16)|x for -t
                int packed = ((anchor.Y & 0xFFFF) << 16) | (anchor.X & 0xFFFF);

                var si = new ProcessStartInfo
                {
                    FileName = GetSndVolPath(),
                    Arguments = "-t " + packed,
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                    WindowStyle     = ProcessWindowStyle.Hidden
                };

                startMouse = physMouse;
                graceUntilUtc = DateTime.UtcNow.AddMilliseconds(250);

                sndVol = Process.Start(si);
                startedUtc = DateTime.UtcNow;
                timer.Start();

                // If -t is ignored on this device, force-move the window to bottom-right
                IntPtr h = FindTopLevelWindowByPid(sndVol.Id, maxTries: 60, sleepMs: 50); // wait up to ~3s
                if (h != IntPtr.Zero) MoveWindowBottomRight(h, activeBounds, anchor);
            }
            catch (Exception ex)
            {
                try
                {
                    string title = (currentLanguage == AppLanguage.Spanish)
                        ? "Mezclador de volumen"
                        : "Volume Mixer";

                    string prefix = (currentLanguage == AppLanguage.Spanish)
                        ? "No se pudo abrir SndVol: "
                        : "Failed to open SndVol: ";

                    tray.ShowBalloonTip(
                        2500,
                        title,
                        prefix + ex.Message,
                        ToolTipIcon.Error);
                }
                catch { }
            }
        }

        static Point ComputeAnchor(Screen scr, int pad)
        {
            Rectangle wa = scr.WorkingArea;
            Rectangle b  = scr.Bounds;

            bool bottom = wa.Bottom < b.Bottom;
            bool top    = wa.Top    > b.Top;
            bool right  = wa.Right  < b.Right;
            bool left   = wa.Left   > b.Left;

            if (bottom || right) return new Point(wa.Right - pad, wa.Bottom - pad);
            if (top)             return new Point(wa.Right - pad, wa.Top + pad);
            if (left)            return new Point(wa.Left + pad,  wa.Bottom - pad);
            return new Point(wa.Right - pad, wa.Bottom - pad);
        }

        void TimerTick(object sender, EventArgs e)
        {
            try
            {
                if (sndVol == null || sndVol.HasExited ||
                    (DateTime.UtcNow - startedUtc).TotalMilliseconds > totalWatchMs)
                {
                    timer.Stop();
                    return;
                }

                // Use PHYSICAL mouse pos
                POINT pnt; if (!GetCursorPos(out pnt)) return;
                Point pos = new Point(pnt.X, pnt.Y);

                if (safeZoneBRQ.Contains(pos)) return;

                if (DateTime.UtcNow >= graceUntilUtc)
                {
                    int dx = Math.Abs(pos.X - startMouse.X);
                    int dy = Math.Abs(pos.Y - startMouse.Y);
                    if (dx > distancePx || dy > distancePx)
                    {
                        try { sndVol.Kill(); } catch { }
                        timer.Stop();
                    }
                }
            }
            catch
            {
                // keep tray alive on any watcher error
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (themeTimer != null) themeTimer.Dispose();
                if (timer != null) timer.Dispose();
                if (menu != null)  menu.Dispose();
                if (tray != null)  { try { tray.Visible = false; } catch { } tray.Dispose(); }
                if (sndVol != null) sndVol.Dispose();
                if (lightIcon != null) lightIcon.Dispose();
                if (darkIcon  != null) darkIcon.Dispose();
                if (exeFallbackIcon != null) exeFallbackIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
