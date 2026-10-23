// MeetingAV.exe - the Meeting AV page as a desktop app.
//   - Opens the page in its own window (WebView2, Edge's engine built into Windows; an Edge app
//     window if that's missing) and lives in the tray while it's open
//   - Gets the PC ready (OBS and its virtual camera) by running Start-Meeting.ps1;
//     progress shows in the page
//   - Serves the page to tablets on the hall Wi-Fi (port 8080, or the next free one, remembered)
//   - Tells the page OBS's websocket port and password, so nobody has to type them
//   - Sets the volume of the speakers, Zoom and JW Library (a small mixer in the page)
//   - Keeps a timer for each part and shows it big on a screen the speakers can see (like OnlyT)
//   - Starts with Windows, and updates itself from the GitHub repository (Settings > App)
//   - Shows the page what JW Library's second display is showing (OBS only captures a screen
//     while its scene is on air, so it can't be used to spot media starting)
// Closing the Meeting AV window quits the app.
// Options: Midweek | Weekend   force a meeting instead of going by the day
//          -NoSetup            don't run the setup (just serve and open the page)
//          -NoObs              don't start OBS
// obs-remote.html, Start-Meeting.ps1 and the lib folder (people detector, QR codes) are built
// into the exe, so it needs no internet. A copy placed next to the exe is used instead, so the
// page can be changed without rebuilding.
// Build with build.bat.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class MeetingAV
{
    const int FirstPort = 8080, LastPort = 8089;
    static int Port;
    static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string DataDir = DataFolder();
    // Its settings, the window's saved page settings, the parts... Kept in "MeetingCamera" before the
    // app was renamed, and moved over the first time.
    static string DataFolder()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(local, "MeetingAV"), old = Path.Combine(local, "MeetingCamera");
        if (!Directory.Exists(dir) && Directory.Exists(old))
        {
            try { Directory.Move(old, dir); }
            catch { return old; }   // still in use (the old app running): carry on with it as it is
        }
        return dir;
    }
    // 127.0.0.1 rather than localhost, which Windows may send to IPv6 first.
    static string Url { get { return "http://127.0.0.1:" + Port + "/"; } }

    // Setup progress, read by the page through /setup.
    static readonly object SetupLock = new object();
    static readonly List<string> SetupLines = new List<string>();
    static bool SetupRunning;
    static string SetupMeeting;
    static bool NoObs;

    static NotifyIcon Tray;
    static System.Windows.Forms.Timer QuitTimer;
    static readonly HashSet<string> OpenWindows = new HashSet<string>();   // ids of open Meeting AV windows
    static Control UiThread;

    [STAThread]
    static int Main(string[] args)
    {
        // Real pixel sizes on every screen, even when they use different scaling.
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        // Restarting to update: wait for the copy that's closing to be gone (it holds the port).
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i].TrimStart('-', '/').ToLowerInvariant() == "after")
                try { Process.GetProcessById(int.Parse(args[i + 1])).WaitForExit(15000); } catch { }
        if (ApplyUpdate(args)) return 0;
        UnpackWebView2();
        bool setup = true;
        foreach (string arg in args)
        {
            string a = arg.TrimStart('-', '/').ToLowerInvariant();
            if (a == "midweek") SetupMeeting = "Midweek";
            else if (a == "weekend") SetupMeeting = "Weekend";
            else if (a == "nosetup") setup = false;
            else if (a == "noobs") NoObs = true;
        }
        Directory.CreateDirectory(DataDir);
        string portFile = Path.Combine(DataDir, "port.txt");

        // Already running: just show its window.
        bool first;
        Single = new Mutex(true, @"Local\MeetingAV", out first);
        if (!first)
        {
            int.TryParse(File.Exists(portFile) ? File.ReadAllText(portFile) : "", out Port);
            if (Port > 0) AskToShow();
            return 0;
        }

        TcpListener listener = StartListener();
        if (listener == null)
        {
            if (Port > 0) { OpenInEdge(); return 0; }   // the old Serve-Remote.ps1 is serving the page
            MessageBox.Show("Ports " + FirstPort + "-" + LastPort + " are all in use by other programs.",
                            "Meeting AV", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        File.WriteAllText(portFile, Port.ToString());
        new Thread(() => Serve(listener)) { IsBackground = true }.Start();
        new Thread(WatchJwMedia) { IsBackground = true }.Start();
        new Thread(WatchJwStart) { IsBackground = true }.Start();

        Application.EnableVisualStyles();
        UiThread = new Control();
        UiThread.CreateControl();
        Tray = new NotifyIcon
        {
            Icon = new Icon(new MemoryStream(Resource("MeetingAV.ico"))),
            Text = "Meeting AV",
            Visible = true,
            ContextMenu = new ContextMenu(new[] {
                new MenuItem("Show Meeting AV", (s, e) => OpenWindow()) { DefaultItem = true },
                new MenuItem("Get ready again", (s, e) => RunSetup(null)),
                new MenuItem("-"),
                new MenuItem("Quit", (s, e) => Quit()),
            }),
        };
        Tray.DoubleClick += (s, e) => OpenWindow();
        // Each window says hello when it opens and goodbye when it closes. When the last one has
        // gone, quit - unless one comes back within a few seconds (a reload).
        QuitTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        QuitTimer.Tick += (s, e) => { if (OpenWindows.Count == 0) Quit(); };

        MakeDesktopShortcut();
        StartWithWindowsFirstTime();
        CheckForUpdate(false);
        OpenWindow();
        ShowTimerScreen("auto", true);   // where it was last time, if that screen is still there
        CountdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        CountdownTimer.Tick += (s, e) => WatchCountdown();
        CountdownTimer.Start();
        if (setup) RunSetup(SetupMeeting);
        Application.Run();
        GC.KeepAlive(Single);
        return 0;
    }

    static Mutex Single;   // only one copy runs

    static void Quit()
    {
        if (JwDisplayOff && !JwBusy && JwSetSecondDisplay(true)) { try { File.Delete(JwOffFile); } catch { } }
        Tray.Visible = false;
        Application.Exit();
        Environment.Exit(0);
    }

    // ---- Port -------------------------------------------------------------------

    // Tries last time's port first so the tablet link stays the same. Returns null with Port set
    // if a copy of this app is already serving the page, or with Port = 0 if nothing is free.
    static TcpListener StartListener()
    {
        List<int> ports = new List<int>();
        int saved;
        try { if (int.TryParse(File.ReadAllText(Path.Combine(DataDir, "port.txt")), out saved)) ports.Add(saved); } catch { }
        for (int p = FirstPort; p <= LastPort; p++) if (!ports.Contains(p)) ports.Add(p);

        foreach (int p in ports)
        {
            // Something else may hold the port on just one loopback address (WSL does this), which
            // would quietly win over a listener on all addresses - so check both first.
            if (CanBind(IPAddress.Loopback, p) && CanBind(IPAddress.IPv6Loopback, p))
            {
                TcpListener listener = new TcpListener(IPAddress.IPv6Any, p);   // IPv6 and IPv4
                try { listener.Server.DualMode = true; listener.Start(); Port = p; return listener; }
                catch (SocketException) { }
            }
            else if (IsOurs(p)) { Port = p; return null; }
        }
        Port = 0;
        return null;
    }

    static bool CanBind(IPAddress address, int port)
    {
        TcpListener test = new TcpListener(address, port);
        try { test.Start(); return true; }
        catch (SocketException) { return false; }
        finally { test.Stop(); }
    }

    // Asks whatever has the port for /info: our server (or Serve-Remote.ps1) answers with "ips".
    static bool IsOurs(int port)
    {
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/info");
            req.Timeout = req.ReadWriteTimeout = 2000;
            req.Proxy = null;
            using (WebResponse res = req.GetResponse())
            using (StreamReader r = new StreamReader(res.GetResponseStream()))
                return r.ReadToEnd().Contains("\"ips\"");
        }
        catch { return false; }
    }

    // ---- Window -----------------------------------------------------------------

    // Edge's app mode gives the page a window of its own, without tabs or an address bar.
    // Its own profile folder keeps the page's settings apart from normal browsing.
    // ---- Window -----------------------------------------------------------------

    static AppWindow Window;

    static void OpenWindow()
    {
        if (Window != null && !Window.IsDisposed) { Window.Bring(); return; }
        try { ShowAppWindow(); }
        catch { Window = null; OpenInEdge(); }   // WebView2's files couldn't be loaded
    }

    // Separate so that WebView2 not loading is caught by OpenWindow.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void ShowAppWindow()
    {
        string userData = Path.Combine(DataDir, "WebView2");
        MoveEdgeStorage(userData);
        LoaderReady();
        AppWindow w = new AppWindow(Url, userData, new Icon(new MemoryStream(Resource("MeetingAV.ico"))));
        w.Failed += () => { w.Close(); Window = null; OpenInEdge(); };   // no WebView2 on this PC
        w.FormClosed += (s, e) => { if (!w.HasFailed) Quit(); };
        Window = w;
        w.Show();
    }

    static bool loaderSet;
    static void LoaderReady()
    {
        if (!loaderSet) { CoreWebView2Environment.SetLoaderDllFolderPath(WebView2Dir); loaderSet = true; }
    }

    // Started again while running: the running copy shows its window.
    static void AskToShow()
    {
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Url + "show");
            req.Headers.Add("X-Meeting-Camera", "1");
            req.Timeout = 3000;
            req.GetResponse().Close();
        }
        catch { OpenInEdge(); }
    }

    // WebView2's files are built into the exe and unpacked next to its data on first use.
    const string WebView2Version = "1.0.4191.47";
    static string WebView2Dir { get { return Path.Combine(DataDir, "webview2-" + WebView2Version); } }

    static void UnpackWebView2()
    {
        try
        {
            Directory.CreateDirectory(WebView2Dir);
            foreach (string name in new[] { "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll" })
            {
                string path = Path.Combine(WebView2Dir, name);
                if (!File.Exists(path)) File.WriteAllBytes(path, Resource("webview2/" + name));
            }
        }
        catch { }
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string file = Path.Combine(WebView2Dir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        };
    }

    // What the page saved (in its browser storage) while it ran in an Edge window moves over, once.
    static void MoveEdgeStorage(string userData)
    {
        string from = Path.Combine(DataDir, @"Window\Default\Local Storage");
        string to = Path.Combine(userData, @"EBWebView\Default\Local Storage");
        if (!Directory.Exists(from) || Directory.Exists(to)) return;
        try { CopyFolder(from, to); } catch { }
    }

    static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from))
            if (Path.GetFileName(f) != "LOCK") File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (string d in Directory.GetDirectories(from)) CopyFolder(d, Path.Combine(to, Path.GetFileName(d)));
    }

    static void OpenInEdge()
    {
        string edge = FindEdge();
        string profile = Path.Combine(DataDir, "Window");
        try
        {
            if (edge == null) throw new FileNotFoundException();
            Process.Start(edge, "--app=" + Url + " --user-data-dir=\"" + profile + "\"" +
                                " --no-first-run --no-default-browser-check --window-size=1280,900");
        }
        catch { Process.Start(Url); }   // no Edge: use the default browser
    }

    static string FindEdge()
    {
        foreach (string root in new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" })
        {
            string path = Microsoft.Win32.Registry.GetValue(root + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", "", null) as string;
            if (path != null && File.Exists(path)) return path;
        }
        foreach (string dir in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
        {
            if (dir == null) continue;
            string path = Path.Combine(dir, @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // A desktop shortcut the first time the app runs (not again if it's deleted).
    static void MakeDesktopShortcut()
    {
        string flag = Path.Combine(DataDir, "shortcut-made");
        if (File.Exists(flag)) return;
        try
        {
            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Meeting AV.lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type t = sc.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { Application.ExecutablePath });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { AppDir });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Get ready for the meeting and control the camera" });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            File.WriteAllText(flag, DateTime.Now.ToString("s"));
        }
        catch { }
    }

    // ---- Setup ------------------------------------------------------------------

    // Runs Start-Meeting.ps1 hidden; its output lines are shown by the page.
    static void RunSetup(string meeting)
    {
        lock (SetupLock)
        {
            if (SetupRunning) return;
            SetupRunning = true;
            SetupLines.Clear();
        }
        new Thread(() =>
        {
            try
            {
                string script = Path.Combine(AppDir, "Start-Meeting.ps1");
                if (!File.Exists(script))
                {
                    script = Path.Combine(DataDir, "Start-Meeting.ps1");
                    File.WriteAllBytes(script, Resource("Start-Meeting.ps1"));
                }
                string arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -AppMode";
                if (meeting != null) arguments += " -Meeting " + meeting;
                if (NoObs) arguments += " -NoObs";
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (Process p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => AddSetupLine(e.Data);
                    p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AddSetupLine("  Error [!!] " + e.Data.Trim()); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                }
            }
            catch (Exception e) { AddSetupLine("  Setup [!!] could not run: " + e.Message); }
            lock (SetupLock) SetupRunning = false;
        }) { IsBackground = true }.Start();
    }

    static void AddSetupLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (SetupLock) SetupLines.Add(line.TrimEnd());
    }

    static string SetupJson()
    {
        lock (SetupLock)
            return "{\"running\":" + (SetupRunning ? "true" : "false") + ",\"lines\":[" +
                   string.Join(",", SetupLines.ConvertAll(Json)) + "]}";
    }


    // ---- Timer --------------------------------------------------------------------
    // Like OnlyT: the page loads a part's length and starts it as the speaker begins; the timer
    // screen (its own window filling a screen the speakers can see) counts down. The time is kept
    // here so the page and the timer screen always agree.

    static readonly object TimerLock = new object();
    static bool TimerRunning;
    static long TimerEndsAt;   // while running: when it reaches 0:00 (ms since 1970)
    static long TimerLeft;     // while stopped: what's left, below 0 once over time (ms)
    static long TimerLength;   // the part's length (ms); 0 = no part loaded, the screen shows the time of day
    static string TimerLabel = "";
    static long MeetingAt;     // when the next meeting starts (ms since 1970), told by the page: the
                               // timer screen counts down to it in the last 5 minutes
    static TimerWindow TimerForm;
    static string TimerScreen = "";   // the screen it's on, while shown
    static string TimerScreenFile { get { return Path.Combine(DataDir, "timer-screen.txt"); } }

    static long NowMs() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

    static string TimerJson()
    {
        lock (TimerLock)
            return "{\"running\":" + (TimerRunning ? "true" : "false") + ",\"endsAt\":" + TimerEndsAt +
                   ",\"left\":" + TimerLeft + ",\"length\":" + TimerLength + ",\"label\":" + Json(TimerLabel) +
                   ",\"now\":" + NowMs() + ",\"shown\":" + (TimerScreen != "" ? "true" : "false") +
                   ",\"screen\":" + Json(TimerScreen) + ",\"meetingAt\":" + MeetingAt +
                   ",\"jwOff\":" + (JwDisplayOff ? "true" : "false") + ",\"tv\":\"" + (TvShowsZoom ? "zoom" : "jw") + "\"}";
    }

    // set (load a part of secs), start, stop, reset (back to the part's length), add (secs, may be
    // negative), clear (no part: the time of day).
    static void TimerDo(string action, int secs, string label)
    {
        lock (TimerLock)
        {
            long now = NowMs(), left = TimerRunning ? TimerEndsAt - now : TimerLeft;
            switch (action)
            {
                case "set": TimerRunning = false; TimerLength = TimerLeft = secs * 1000L; TimerLabel = label ?? ""; break;
                case "start": if (!TimerRunning && TimerLength > 0) { TimerEndsAt = now + left; TimerRunning = true; } break;
                case "stop": if (TimerRunning) { TimerLeft = left; TimerRunning = false; } break;
                case "reset": TimerRunning = false; TimerLeft = TimerLength; break;
                case "add":
                    if (TimerLength == 0) break;
                    TimerLength = Math.Max(60000, TimerLength + secs * 1000L);
                    if (TimerRunning) TimerEndsAt += secs * 1000L; else TimerLeft += secs * 1000L;
                    break;
                case "clear": TimerRunning = false; TimerLength = TimerLeft = 0; TimerLabel = ""; break;
            }
        }
    }

    // Screens for the timer, with which one JW Library uses (not a good choice) and the main one.
    // Each screen's real resolution and where it is (as Windows has them, whatever the scaling: the
    // window's own measurements are stretched by the main screen's scaling), numbered as Windows does.
    static string ScreensJson()
    {
        Screen jw = JwTargetScreen();
        DisplayMode main = new DisplayMode();
        foreach (Screen sc in Screen.AllScreens) if (sc.Primary) main = Mode(sc.DeviceName);
        List<string> list = new List<string>();
        foreach (Screen sc in Screen.AllScreens)
        {
            DisplayMode m = Mode(sc.DeviceName);
            string where = sc.Primary ? "main" : Where(m, main);
            list.Add("{\"name\":" + Json(sc.DeviceName) + ",\"number\":" + Regex.Match(sc.DeviceName, "\\d+$").Value +
                     ",\"main\":" + (sc.Primary ? "true" : "false") + ",\"where\":" + Json(where) +
                     ",\"jw\":" + (jw != null && jw.DeviceName == sc.DeviceName ? "true" : "false") +
                     ",\"w\":" + m.dmPelsWidth + ",\"h\":" + m.dmPelsHeight + "}");
        }
        string saved = "";
        try { if (File.Exists(TimerScreenFile)) saved = File.ReadAllText(TimerScreenFile).Trim(); } catch { }
        return "{\"screens\":[" + string.Join(",", list) + "],\"saved\":" + Json(saved) + "}";
    }

    // Which side of the main screen another one is on, by where its middle is.
    static string Where(DisplayMode m, DisplayMode main)
    {
        double dx = (m.dmPositionX + m.dmPelsWidth / 2.0) - (main.dmPositionX + main.dmPelsWidth / 2.0);
        double dy = (m.dmPositionY + m.dmPelsHeight / 2.0) - (main.dmPositionY + main.dmPelsHeight / 2.0);
        if (Math.Abs(dy) > Math.Abs(dx)) return dy < 0 ? "above" : "below";
        return dx < 0 ? "left" : "right";
    }

    static DisplayMode Mode(string device)
    {
        DisplayMode m = new DisplayMode();
        m.dmSize = (short)Marshal.SizeOf(typeof(DisplayMode));
        EnumDisplaySettings(device, -1, ref m);   // ENUM_CURRENT_SETTINGS
        return m;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode m);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayMode   // DEVMODE, as used for screens
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    // Shows the timer screen on the named screen ("" hides it). "auto": the one used last time, or
    // else a screen that's neither the main one nor JW Library's. Called on the UI thread.
    // Returns false if there's no such screen.
    static bool ShowTimerScreen(string name, bool onlyIfSaved = false)
    {
        if (name == "")
        {
            if (TimerForm != null) TimerForm.Close();
            TimerForm = null; TimerScreen = "";
            try { File.WriteAllText(TimerScreenFile, ""); } catch { }
            return true;
        }
        string saved = "";
        try { if (File.Exists(TimerScreenFile)) saved = File.ReadAllText(TimerScreenFile).Trim(); } catch { }
        if (name == "auto")
        {
            if (onlyIfSaved && saved == "") return false;
            name = saved;
            Screen jw = JwTargetScreen();
            if (Array.Find(Screen.AllScreens, sc => sc.DeviceName == name) == null)
            {
                Screen spare = Array.Find(Screen.AllScreens, sc => !sc.Primary && (jw == null || sc.DeviceName != jw.DeviceName));
                if (spare == null || onlyIfSaved) return false;
                name = spare.DeviceName;
            }
        }
        Screen target = Array.Find(Screen.AllScreens, sc => sc.DeviceName == name);
        if (target == null) return false;
        try { File.WriteAllText(TimerScreenFile, name); } catch { }
        if (TimerForm != null && !TimerForm.IsDisposed)
        {
            if (TimerScreen != name) TimerForm.MoveTo(target);
        }
        else
        {
            try
            {
                LoaderReady();
                TimerForm = new TimerWindow(Url + "timer-screen", Path.Combine(DataDir, "WebView2"), target);
                TimerForm.FormClosed += (s, e) => { if (TimerForm == s) { TimerForm = null; TimerScreen = ""; } };
                TimerForm.Show();
            }
            catch { TimerForm = null; return false; }
        }
        TimerScreen = name;
        return true;
    }

    // Runs on the UI thread (the timer window lives there) and waits for the answer.
    static T OnUi<T>(Func<T> f)
    {
        return (T)UiThread.Invoke(f);
    }

    // ---- Starting with Windows ----------------------------------------------------------
    // On by default (the first time this version runs), switched in Settings > App.

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static bool StartsWithWindows()
    {
        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue("MeetingAV") != null;
    }

    static void SetStartsWithWindows(bool on)
    {
        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue("MeetingAV", "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue("MeetingAV", false);
        }
    }

    static void StartWithWindowsFirstTime()
    {
        string flag = Path.Combine(DataDir, "startup-set");
        try
        {
            if (!File.Exists(flag)) { SetStartsWithWindows(true); File.WriteAllText(flag, "on by default"); }
            else if (StartsWithWindows()) SetStartsWithWindows(true);   // keep the path right if the exe moved
        }
        catch { }
    }

    // ---- Updates ------------------------------------------------------------------------
    // New versions are MeetingAV.exe committed to the GitHub repository. At start-up the latest
    // commit of it is compared with this file: only a newer one is fetched (so a build that's newer
    // than GitHub's is never replaced), checked against GitHub's own fingerprint of the file, and
    // kept as MeetingAV.new.exe. Restarting puts it in place.

    const string Repo = "JamesHawken/meetingav";
    static volatile string UpdateState = "";   // "", checking, ready, up to date, or what went wrong
    static string NewExe { get { return Path.Combine(AppDir, "MeetingAV.new.exe"); } }
    static string NoUpdatesFile { get { return Path.Combine(DataDir, "no-updates"); } }

    static void CheckForUpdate(bool asked)
    {
        if (!asked && File.Exists(NoUpdatesFile)) return;
        if (UpdateState == "checking" || UpdateState == "ready") return;
        UpdateState = "checking";
        new Thread(() =>
        {
            try
            {
                string commits = Download("https://api.github.com/repos/" + Repo + "/commits?path=MeetingAV.exe&per_page=1");
                Match date = Regex.Match(commits, "\"committer\"\\s*:\\s*\\{[^}]*?\"date\"\\s*:\\s*\"([^\"]+)\"");
                if (!date.Success) throw new Exception("no version found on GitHub");
                DateTime committed = DateTime.Parse(date.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                byte[] mine = File.ReadAllBytes(Application.ExecutablePath);
                if (committed <= File.GetLastWriteTimeUtc(Application.ExecutablePath)) { UpdateState = "up to date"; return; }
                string meta = Download("https://api.github.com/repos/" + Repo + "/contents/MeetingAV.exe");
                string sha = Regex.Match(meta, "\"sha\"\\s*:\\s*\"([0-9a-f]{40})\"").Groups[1].Value;
                if (sha == "" ) throw new Exception("GitHub didn't say which version it has");
                if (sha == GitBlobSha(mine)) { UpdateState = "up to date"; return; }
                byte[] exe = DownloadBytes("https://raw.githubusercontent.com/" + Repo + "/main/MeetingAV.exe");
                if (GitBlobSha(exe) != sha) throw new Exception("the download didn't match GitHub's copy");
                File.WriteAllBytes(NewExe, exe);
                UpdateState = "ready";
            }
            catch (Exception e) { UpdateState = "Couldn't check for updates: " + e.Message; }
        }) { IsBackground = true }.Start();
    }

    // How git names a file's contents (what GitHub reports as its "sha").
    static string GitBlobSha(byte[] data)
    {
        using (System.Security.Cryptography.SHA1 sha = System.Security.Cryptography.SHA1.Create())
        {
            byte[] head = Encoding.ASCII.GetBytes("blob " + data.Length + "\0");
            sha.TransformBlock(head, 0, head.Length, null, 0);
            sha.TransformFinalBlock(data, 0, data.Length);
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }
    }

    // A downloaded update goes in place before anything else starts: this file moves aside (allowed
    // while it runs), the new one takes its name and starts instead. Returns true if it did.
    static bool ApplyUpdate(string[] args)
    {
        string self = Application.ExecutablePath, old = Path.Combine(AppDir, "MeetingAV.old.exe");
        try { if (File.Exists(old)) File.Delete(old); } catch { }   // left from the last update
        if (!File.Exists(NewExe) || !string.Equals(Path.GetFileName(self), "MeetingAV.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            File.Move(self, old);
            File.Move(NewExe, self);
            Process.Start(self, string.Join(" ", args));
            return true;
        }
        catch
        {
            try { if (!File.Exists(self) && File.Exists(old)) File.Move(old, self); } catch { }
            return false;
        }
    }

    // Restart now (to update): the new copy waits for this one to close, then starts without setup.
    static void Restart()
    {
        Tray.Visible = false;
        try { Single.ReleaseMutex(); Single.Close(); } catch { }
        Process.Start(Application.ExecutablePath, "-NoSetup -after " + Process.GetCurrentProcess().Id);
        Environment.Exit(0);
    }

    static string AppJson()
    {
        return "{\"startup\":" + (StartsWithWindows() ? "true" : "false") +
               ",\"updates\":" + (File.Exists(NoUpdatesFile) ? "false" : "true") +
               ",\"installed\":" + Json(File.GetLastWriteTime(Application.ExecutablePath).ToString("d MMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture)) +
               ",\"update\":" + Json(UpdateState) + "}";
    }

    // ---- Countdown on JW Library's screen ------------------------------------------------
    // In the last 5 minutes before a meeting, a countdown fills the screen JW Library shows on (the
    // hall's TV), so everyone knows when it starts. JW Library's own screen covers any other window,
    // so for the countdown Meeting AV switches JW Library's second display off, and back on as it
    // reaches 0:00 - leaving JW Library on the meeting's media, ready for the song. The page can
    // hand the screen back sooner. Settings > Meeting can switch the countdown off.

    static bool CountdownOn = true;
    static long CountdownDismissedFor;   // the meeting whose countdown was handed back early
    static TimerWindow CountdownForm;
    static System.Windows.Forms.Timer CountdownTimer;
    static volatile bool JwDisplayOff;   // switched off by Meeting AV for the countdown
    static volatile bool JwBusy;         // switching it, on a background thread
    static string JwOffFile { get { return Path.Combine(DataDir, "jw-display-off"); } }

    static void WatchCountdown()   // every second, on the UI thread
    {
        long left = MeetingAt - NowMs();
        bool soon = CountdownOn && MeetingAt > 0 && left > 0 && left <= 5 * 60000 && CountdownDismissedFor != MeetingAt;
        Screen screen = soon ? JwTargetScreen() : null;
        bool want = screen != null;
        bool shown = CountdownForm != null && !CountdownForm.IsDisposed;
        // Switching JW Library's screen back on takes a few seconds, so it starts just before 0:00:
        // the yeartext takes over from the countdown as it ends.
        if (JwDisplayOff && !JwBusy && (!want || left <= JwBackLeadMs)) GiveJwItsScreenBack();
        if (JwBusy) return;
        if (want && !shown && left > JwBackLeadMs)
        {
            JwBusy = true;
            new Thread(() =>
            {
                try { if (JwRunning() && JwSetSecondDisplay(false)) { JwDisplayOff = true; Touch(JwOffFile); } }
                finally
                {
                    OnUi(() =>
                    {
                        try
                        {
                            LoaderReady();
                            CountdownForm = new TimerWindow(Url + "timer-screen?countdown", Path.Combine(DataDir, "WebView2"), screen, true);
                            CountdownForm.Show();
                        }
                        catch { CountdownForm = null; }
                        JwBusy = false;
                    });
                }
            }) { IsBackground = true }.Start();
        }
        else if (!want && shown)
        {
            CountdownForm.Close();
            CountdownForm = null;
        }
    }

    const long JwBackLeadMs = 7000;

    static void GiveJwItsScreenBack()
    {
        if (!JwDisplayOff || JwBusy) return;
        JwBusy = true;
        new Thread(() =>
        {
            try { if (!JwRunning() || JwSetSecondDisplay(true)) { JwDisplayOff = false; TvShowsZoom = false; try { File.Delete(JwOffFile); } catch { } } }
            finally { JwBusy = false; }
        }) { IsBackground = true }.Start();
    }

    static void Touch(string file) { try { File.WriteAllText(file, DateTime.Now.ToString("s")); } catch { } }
    static void OnUi(Action a) { try { UiThread.BeginInvoke(a); } catch { } }
    static bool JwRunning() { return Process.GetProcessesByName("JWLibrary").Length > 0; }

    // ---- Driving JW Library: its Meetings page and its second display -----------------------
    // Through Windows UI Automation, like a screen reader - no mouse or keyboard. Names in English
    // and French, with the menu's order as a fallback for other languages.

    static readonly Regex JwHomeName = new Regex("^(Home|Accueil|Inicio|In\u00edcio|Startseite|Home page)$", RegexOptions.IgnoreCase);
    static readonly Regex JwMeetingsName = new Regex("^(Meetings|R\u00e9unions|Reuniones|Reuni\u00f5es|Zusammenk\u00fcnfte|Riunioni|Vergaderingen)$", RegexOptions.IgnoreCase);
    static readonly Regex JwSettingsName = new Regex("^(Settings|Param\u00e8tres|Configuraci\u00f3n|Ajustes|Configura\u00e7\u00f5es|Einstellungen|Impostazioni|Instellingen)$", RegexOptions.IgnoreCase);
    static readonly Regex JwSecondName = new Regex("second|deuxi\u00e8me|segund|zweit|tweede", RegexOptions.IgnoreCase);
    static readonly Regex JwMediaName = new Regex(@"\bm[e\u00e9]di(a|as|en|os)\b", RegexOptions.IgnoreCase);

    static AutomationElement JwMainWindow()
    {
        var menu = new PropertyCondition(AutomationElement.AutomationIdProperty, "HamburgerRadioButton");
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children,
                     new PropertyCondition(AutomationElement.NameProperty, "JW Library")))
            if (w.FindFirst(TreeScope.Descendants, menu) != null) return w;
        return null;
    }

    // Pressing something that opens another page can report an error as the old page goes away,
    // although it worked: that's not counted as a failure.
    static void Press(AutomationElement el)
    {
        try { PressOrThrow(el); } catch (ElementNotAvailableException) { }
    }

    static void PressOrThrow(AutomationElement el)
    {
        object p;
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out p)) ((InvokePattern)p).Invoke();
        else if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p)) ((SelectionItemPattern)p).Select();
        else if (el.TryGetCurrentPattern(TogglePattern.Pattern, out p)) ((TogglePattern)p).Toggle();
    }

    // An item of JW Library's side menu: by name, or by its place in the menu. In a narrow window the
    // menu is folded away behind its button, which opens it.
    static readonly string[] JwSections = { "navigation_home", "navigation_bible", "navigation_library", "navigation_meetings", "navigation_personal_study" };

    // Which section of JW Library is open (its place in the menu), or -1 if the menu can't be seen.
    static int JwCurrentSection(AutomationElement w)
    {
        for (int i = 0; i < JwSections.Length; i++)
        {
            AutomationElement g = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, JwSections[i]));
            AutomationElement item = g == null ? null : TreeWalker.ControlViewWalker.GetParent(g);
            object p;
            if (item != null && item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p) && ((SelectionItemPattern)p).Current.IsSelected) return i;
        }
        return -1;
    }

    static AutomationElement JwMenuItem(AutomationElement w, Regex name, int index)
    {
        var listItem = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
        string id = index >= 0 && index < JwSections.Length ? JwSections[index] : null;
        for (int pass = 0; pass < 2; pass++)
        {
            // Each item has a fixed name inside, the same in every language.
            AutomationElement fixedId = id == null ? null : w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            if (fixedId != null)
            {
                AutomationElement item = TreeWalker.ControlViewWalker.GetParent(fixedId);
                if (item != null && item.Current.ControlType == ControlType.ListItem) return item;
            }
            AutomationElementCollection items = w.FindAll(TreeScope.Descendants, listItem);
            foreach (AutomationElement it in items)
                if (name.IsMatch(it.Current.Name)) return it;
            if (pass == 1) return items.Count > index ? items[index] : null;
            AutomationElement ham = w.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "HamburgerRadioButton"));
            if (ham == null) return null;
            Press(ham);
            Thread.Sleep(800);
        }
        return null;
    }

    // JW Library's Meetings page, then this week's meeting - Saturday and Sunday the Watchtower Study,
    // Monday to Friday the midweek meeting - and its media, ready to show.
    static bool JwOpenMeetings()
    {
        lock (JwLock)
        {
            try
            {
                AutomationElement w = JwMainWindow();
                if (w == null) return false;
                IntPtr h = new IntPtr(w.Current.NativeWindowHandle);
                if (IsIconic(h)) { ShowWindow(h, 9); Thread.Sleep(800); }   // minimised: brought back
                AutomationElement item = JwMenuItem(w, JwMeetingsName, 3);
                if (item == null) return false;
                Press(item);
                JwMain = null;   // its page changed: looked up afresh
                JwOpenMeetingMedia(w);
                return true;
            }
            catch { return false; }
        }
    }

    static void JwOpenMeetingMedia(AutomationElement w)
    {
        DayOfWeek d = DateTime.Now.DayOfWeek;
        bool weekend = d == DayOfWeek.Saturday || d == DayOfWeek.Sunday;
        string header = weekend ? "WatchtowerHeader" : "MidweekMeetingHeader";
        AutomationElement card = null;
        for (int i = 0; i < 20 && card == null; i++)
        {
            Thread.Sleep(250);
            AutomationElement head = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, header));
            if (head == null) continue;
            AutomationElement section = TreeWalker.ControlViewWalker.GetParent(head);
            if (section != null) card = section.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "MeetingDocumentCard"));
        }
        if (card == null) return;
        // A card only opens with Enter, so JW Library is brought in front for it - and the key is sent
        // only if it really is in front, never into another program - then the focus goes back.
        IntPtr jw = new IntPtr(w.Current.NativeWindowHandle), was = GetForegroundWindow();
        card.SetFocus();
        Thread.Sleep(300);
        if (GetForegroundWindow() != jw) { AllowForeground(); SetForegroundWindow(jw); Thread.Sleep(300); card.SetFocus(); Thread.Sleep(200); }
        if (GetForegroundWindow() != jw) return;
        SendKeys.SendWait("{ENTER}");
        // The article: in a narrow window its study notes open over it first.
        AutomationElement media = null;
        for (int i = 0; i < 24 && media == null; i++)
        {
            Thread.Sleep(250);
            AutomationElement pane = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StudyPaneButton"));
            object tp;
            if (pane != null && i == 4 && pane.TryGetCurrentPattern(TogglePattern.Pattern, out tp) && ((TogglePattern)tp).Current.ToggleState == ToggleState.On)
                ((TogglePattern)tp).Toggle();
            media = JwFindMediaButton(w);
            if (media == null && i >= 4 && i % 4 == 0)   // under "..."
                foreach (AutomationElement more in w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "MoreButton")))
                    if (!more.Current.IsOffscreen) { Press(more); break; }
        }
        if (media != null) Press(media);
        if (was != IntPtr.Zero && was != jw) { Thread.Sleep(500); SetForegroundWindow(was); }
    }

    // Personal Study > Playlists.
    static readonly Regex JwPlaylistsName = new Regex("^(Playlists|Listes de lecture|Listas de reproducci\u00f3n|Wiedergabelisten|Playlist)", RegexOptions.IgnoreCase);
    static Rectangle JwWindowWas;   // JW Library's window before its second display was switched
    static bool JwWasZoomed;
    static int JwNavigating;   // 1 while a button's trip through JW Library is under way
    static bool JwOpenPlaylists()
    {
        lock (JwLock)
        {
            try
            {
                AutomationElement w = JwMainWindow();
                if (w == null) return false;
                IntPtr h = new IntPtr(w.Current.NativeWindowHandle);
                if (IsIconic(h)) { ShowWindow(h, 9); Thread.Sleep(800); }
                if (w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SearchBar")) != null &&
                    JwFindNamed(w, JwPlaylistsName) != null) return true;   // already there
                AutomationElement study = JwMenuItem(w, new Regex("^(Personal Study|\u00c9tude individuelle)$", RegexOptions.IgnoreCase), 4);
                if (study == null) return false;
                Press(study);
                JwMain = null;
                // Personal Study opens on the page it was last on: its overview with a Playlists heading
                // to press, or the playlists already (they have a search bar).
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(250);
                    if (i >= 2 && w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SearchBar")) != null)
                        return true;
                    foreach (AutomationElement b in w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                        if (JwPlaylistsName.IsMatch(b.Current.Name)) { Press(b); return true; }
                }
                return false;
            }
            catch { return false; }
        }
    }

    static AutomationElement JwFindMediaButton(AutomationElement w)
    {
        foreach (AutomationElement b in w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            if (JwMediaName.IsMatch(b.Current.Name) && !b.Current.IsOffscreen) return b;
        return null;
    }

    // Lets this program bring another window to the front (Windows allows it after a key event).
    static void AllowForeground() { keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); }

    // JW Library's Settings > Second display, switched on or off. That's done as soon as the switch
    // flips (the TV changes then); tidying up follows in the background: Settings closed, JW
    // Library's window put back where it was, and JW Library left on the meeting's media (for the
    // countdown) or back on the page it was on (for the TV switch).
    static bool JwSetSecondDisplay(bool on, bool thenMeetingMedia = true)
    {
        AutomationElement w;
        IntPtr jwh;
        bool done;
        int section = -1;
        lock (JwLock)
        {
            try
            {
                w = JwMainWindow();
                if (w == null) return false;
                jwh = new IntPtr(w.Current.NativeWindowHandle);
                if (IsIconic(jwh)) ShowWindow(jwh, 4);   // restored, not activated
                // JW Library puts its window back to a small default when its second display is
                // switched, so where it was (before the countdown switched it off) is put back after.
                RECT was;
                if (!on || JwWindowWas.IsEmpty)
                    if (GetWindowRect(jwh, out was)) { JwWindowWas = Rectangle.FromLTRB(was.Left, was.Top, was.Right, was.Bottom); JwWasZoomed = IsZoomed(jwh); }
                section = JwCurrentSection(w);   // to go back to afterwards
                AutomationElement home = JwMenuItem(w, JwHomeName, 0);
                if (home == null) return false;
                Press(home);
                var button = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
                Func<AutomationElement> findSettings = () =>
                {
                    foreach (AutomationElement b in w.FindAll(TreeScope.Descendants, button))
                        if (JwSettingsName.IsMatch(b.Current.Name) && !b.Current.IsOffscreen) return b;
                    return null;
                };
                AutomationElement settings = WaitFor(findSettings, 2500);
                if (settings == null)   // a narrow window keeps it under "..."
                {
                    foreach (AutomationElement more in w.FindAll(TreeScope.Descendants,
                                 new PropertyCondition(AutomationElement.AutomationIdProperty, "MoreButton")))
                        if (!more.Current.IsOffscreen) { Press(more); break; }
                    settings = WaitFor(findSettings, 1500);
                }
                if (settings == null) return false;
                Press(settings);
                var toggles = new PropertyCondition(AutomationElement.IsTogglePatternAvailableProperty, true);
                DateTime nameless = DateTime.Now.AddMilliseconds(1200);
                AutomationElement toggle = WaitFor(() =>
                {
                    AutomationElement first = null;
                    foreach (AutomationElement t in w.FindAll(TreeScope.Descendants, toggles))
                    {
                        if (t.Current.AutomationId == "HamburgerRadioButton") continue;
                        if (first == null) first = t;
                        if (JwSecondName.IsMatch(t.Current.Name)) return t;
                    }
                    return DateTime.Now > nameless ? first : null;   // its name in another language
                }, 4000);
                if (toggle == null) return false;
                var tp = (TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern);
                if ((tp.Current.ToggleState == ToggleState.On) != on) tp.Toggle();
                Thread.Sleep(150);
                done = (tp.Current.ToggleState == ToggleState.On) == on;
            }
            catch { return false; }
        }
        new Thread(() => JwTidyUp(w, jwh, on, thenMeetingMedia, section)) { IsBackground = true }.Start();
        return done;
    }

    static void JwTidyUp(AutomationElement w, IntPtr jwh, bool on, bool thenMeetingMedia, int section)
    {
        lock (JwLock)
        {
            try
            {
                // Settings is a dialog with a Close button in a narrow window, a page with Back in a wide one.
                AutomationElement close = w.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "CloseButton"));
                bool dialog = close != null;
                if (close == null) close = w.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "Back"));
                if (close != null) { Press(close); Thread.Sleep(400); }
                if (thenMeetingMedia)
                {
                    AutomationElement meetings = JwMenuItem(w, JwMeetingsName, 3);
                    if (meetings != null) { Press(meetings); JwOpenMeetingMedia(w); }
                }
                else if (section > 0)
                {
                    // The section it was in, which opens on the page it was last on there.
                    AutomationElement item = JwMenuItem(w, new Regex("^$"), section);
                    if (item != null) Press(item);
                }
                JwMain = null;
                Thread.Sleep(300);
                if (!JwWindowWas.IsEmpty)
                {
                    if (JwWasZoomed) ShowWindow(jwh, 3);   // maximised again
                    else SetWindowPos(jwh, IntPtr.Zero, JwWindowWas.X, JwWindowWas.Y, JwWindowWas.Width, JwWindowWas.Height, 0x0004 | 0x0010);
                    if (on) JwWindowWas = Rectangle.Empty;
                }
            }
            catch { }
        }
    }

    // Looks again every tenth of a second until it's there, or the time is up.
    static AutomationElement WaitFor(Func<AutomationElement> find, int ms)
    {
        DateTime until = DateTime.Now.AddMilliseconds(ms);
        while (true)
        {
            AutomationElement e = null;
            try { e = find(); } catch (ElementNotAvailableException) { }
            if (e != null || DateTime.Now > until) return e;
            Thread.Sleep(100);
        }
    }

    static AutomationElement JwFindNamed(AutomationElement w, Regex name)
    {
        foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            if (name.IsMatch(e.Current.Name) && !e.Current.IsOffscreen) return e;
        return null;
    }

    // ---- The hall's TV: JW Library, or Zoom's second window --------------------------------------
    // Zoom in "dual monitors" mode keeps its second window (the gallery) on the screen JW Library
    // uses, under JW Library's own. Showing Zoom there switches JW Library's second display off and
    // brings that window up (never made active, which JW Library reacts to); showing JW Library
    // switches its display back on.

    static volatile bool TvShowsZoom;

    static bool ShowOnTv(bool zoom)
    {
        Screen tv = JwTargetScreen();
        if (zoom)
        {
            IntPtr z = tv == null ? IntPtr.Zero : FindTopWindow("ConfMultiTabContentWndClass", tv);
            if (z == IntPtr.Zero) return false;   // no Zoom window on that screen
            if (JwRunning() && !JwSetSecondDisplay(false, false)) return false;
            SetWindowPos(z, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // to the front, not active
            TvShowsZoom = true;
            return true;
        }
        if (JwRunning() && !JwSetSecondDisplay(true, false)) return false;
        TvShowsZoom = false;
        return true;
    }

    // ---- Arranging the PC's screen: Meeting AV on the left, JW Library and Zoom on the right ----

    static string ArrangeWindows()
    {
        IntPtr me = IntPtr.Zero;
        try { if (Window != null && !Window.IsDisposed) me = (IntPtr)Window.Invoke((Func<IntPtr>)(() => Window.Handle)); } catch { }
        Screen screen = me != IntPtr.Zero ? Screen.FromHandle(me) : Screen.PrimaryScreen;
        Rectangle a = screen.WorkingArea;
        int half = a.Width / 2;
        IntPtr jw = IntPtr.Zero;
        try { lock (JwLock) { AutomationElement w = JwMainWindow(); if (w != null) jw = new IntPtr(w.Current.NativeWindowHandle); } } catch { }
        // Zoom's meeting window on this screen - in "dual monitors" mode it has a second one on the
        // hall's TV, which is left alone - or else Zoom's home window.
        IntPtr zoom = FindTopWindow("ConfMultiTabContentWndClass", screen);
        if (zoom == IntPtr.Zero) zoom = FindTopWindow("ZPPTMainFrmWndClassEx", screen);
        var left = new Rectangle(a.X, a.Y, half, a.Height);
        var rightTop = new Rectangle(a.X + half, a.Y, a.Width - half, a.Height / 2);
        var rightBottom = new Rectangle(a.X + half, a.Y + a.Height / 2, a.Width - half, a.Height - a.Height / 2);
        // Meeting AV active first: Windows only lets the active program bring others to the front.
        AllowForeground();
        if (me != IntPtr.Zero)
        {
            SetForegroundWindow(me);
            // On its own thread, so the page inside resizes with it.
            try { Window.Invoke((Action)(() => PlaceWindow(me, left))); } catch { }
        }
        // Only one of them: it has the whole right side.
        if (jw != IntPtr.Zero) PlaceWindow(jw, zoom != IntPtr.Zero ? rightTop : Rectangle.Union(rightTop, rightBottom));
        if (zoom != IntPtr.Zero) PlaceWindow(zoom, jw != IntPtr.Zero ? rightBottom : Rectangle.Union(rightTop, rightBottom));
        // In front of anything else on the screen, Meeting AV last so it keeps the keyboard. Zoom is
        // only brought forward, never made active: that would bring its window on the TV up too, and
        // JW Library takes its second display away when a window on that screen becomes active.
        foreach (IntPtr h in new[] { jw, me })
            if (h != IntPtr.Zero) { SetForegroundWindow(h); Thread.Sleep(80); }
        return "{\"jw\":" + (jw != IntPtr.Zero ? "true" : "false") + ",\"zoom\":" + (zoom != IntPtr.Zero ? "true" : "false") + "}";
    }

    static IntPtr FindTopWindow(string className, Screen on)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) =>
        {
            var c = new StringBuilder(64);
            GetClassName(h, c, 64);
            if (c.ToString() == className && IsWindowVisible(h) &&
                Screen.FromHandle(h).DeviceName == on.DeviceName) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // Puts a window exactly on an area, in front: un-maximised, and allowing for the invisible border
    // Windows draws around windows, so they meet edge to edge.
    static void PlaceWindow(IntPtr h, Rectangle r)
    {
        if (IsIconic(h) || IsZoomed(h)) ShowWindow(h, 9);
        SetWindowPos(h, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, 0x0010);   // to the front, without taking the keyboard
        RECT outer, frame;
        if (GetWindowRect(h, out outer) && DwmGetWindowAttribute(h, 9, out frame, Marshal.SizeOf(typeof(RECT))) == 0)
        {
            int l = frame.Left - outer.Left, t = frame.Top - outer.Top, rr = outer.Right - frame.Right, b = outer.Bottom - frame.Bottom;
            SetWindowPos(h, IntPtr.Zero, r.X - l, r.Y - t, r.Width + l + rr, r.Height + t + b, 0x0004 | 0x0010);
        }
    }

    delegate bool EnumWindowsProc(IntPtr h, IntPtr p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);

    // JW Library opened while Meeting AV is running goes straight to the meeting's media.
    static void WatchJwStart()
    {
        bool wasRunning = JwRunning();
        // Left switched off by a Meeting AV that stopped mid-countdown: switched back on.
        if (File.Exists(JwOffFile)) { JwDisplayOff = true; if (!wasRunning) { try { File.Delete(JwOffFile); } catch { } JwDisplayOff = false; } }
        while (true)
        {
            Thread.Sleep(2000);
            bool running = JwRunning();
            if (running && !wasRunning)
            {
                for (int i = 0; i < 30 && JwMainWindow() == null; i++) Thread.Sleep(1000);
                Thread.Sleep(3000);   // its first page settles
                JwOpenMeetings();
            }
            if (!running && JwDisplayOff && !JwBusy) { JwDisplayOff = false; try { File.Delete(JwOffFile); } catch { } }
            wasRunning = running;
        }
    }

    // ---- Meeting parts, for the timer ------------------------------------------------
    // A midweek meeting's parts and their lengths come from that week's Life and Ministry Meeting
    // Workbook on wol.jw.org, as OnlyT does. Kept once fetched, so it also works offline at the hall.

    static string PartsJson(int year, int week)
    {
        string file = Path.Combine(DataDir, "parts-" + year + "-" + week + ".json");
        try { if (File.Exists(file)) return File.ReadAllText(file); } catch { }
        try
        {
            string json = FetchParts(year, week);
            File.WriteAllText(file, json);
            return json;
        }
        catch (Exception e)
        {
            return "{\"error\":" + Json("Couldn't get the parts from wol.jw.org (" + e.Message + ")") + "}";
        }
    }

    static string FetchParts(int year, int week)
    {
        // The week's page links to its workbook article first, then the Watchtower.
        string page = Download("https://wol.jw.org/en/wol/meetings/r1/lp-e/" + year + "/" + week);
        Match doc = Regex.Match(page, "href=\"(/en/wol/d/r1/lp-e/\\d+)\"");
        if (!doc.Success) throw new Exception("no workbook for that week");
        string weekName = CleanText(Regex.Match(page, "<title>(.*?)(&mdash;|—)", RegexOptions.Singleline).Groups[1].Value);
        string article = Download("https://wol.jw.org" + doc.Groups[1].Value);
        int a = article.IndexOf("<article"), b = article.IndexOf("</article>");
        if (a >= 0 && b > a) article = article.Substring(a, b - a);

        // A part is a heading "3. Bible Reading" followed by "(4 min.) ...", or a heading with the
        // length in it: "Song 21 and Prayer | Opening Comments (1 min.)".
        List<string> parts = new List<string>();
        string pending = null;
        foreach (Match m in Regex.Matches(article, "<(h[1-6])[^>]*>(.*?)</\\1>|<p[^>]*>(.*?)</p>", RegexOptions.Singleline))
        {
            bool heading = m.Groups[1].Success;
            string text = CleanText(heading ? m.Groups[2].Value : m.Groups[3].Value);
            if (text == "") continue;
            Match length = Regex.Match(text, "\\((\\d+) min\\.\\)");
            if (heading)
            {
                pending = null;
                if (Regex.IsMatch(text, "^\\d+\\.\\s")) pending = Regex.Replace(text, "\\s*\\(\\d+ min\\.\\)\\s*$", "");
                else if (length.Success)
                    foreach (string piece in text.Split('|'))
                        if (piece.Contains(length.Value)) parts.Add(Part(piece.Replace(length.Value, "").Trim(), length));
                if (pending != null && length.Success) { parts.Add(Part(pending, length)); pending = null; }
            }
            else if (pending != null && text.StartsWith(length.Success ? length.Value : "\u0001"))
            {
                parts.Add(Part(pending, length));
                pending = null;
            }
        }
        if (parts.Count == 0) throw new Exception("no parts found in the workbook");
        return "{\"week\":" + Json(weekName) + ",\"parts\":[" + string.Join(",", parts) + "]}";
    }

    static string Part(string title, Match length)
    {
        return "{\"title\":" + Json(title) + ",\"min\":" + length.Groups[1].Value + "}";
    }

    static string CleanText(string html)
    {
        string t = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));
        return Regex.Replace(t.Replace("\u200b", ""), "\\s+", " ").Trim();
    }

    static byte[] DownloadBytes(string url)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MeetingAV";
        req.Timeout = 120000;
        using (WebResponse resp = req.GetResponse())
        using (MemoryStream m = new MemoryStream())
        {
            resp.GetResponseStream().CopyTo(m);
            return m.ToArray();
        }
    }

    static string Download(string url)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MeetingAV";
        req.Timeout = 15000;
        using (WebResponse resp = req.GetResponse())
        using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return r.ReadToEnd();
    }

    // ---- Sound ------------------------------------------------------------------
    // Windows' own volume (the default speakers), and Zoom's and JW Library's on their own as in
    // Windows' Volume mixer, each with a level for the page's meters. An app's sound is found on
    // whichever speakers it plays to.

    static readonly object AudioLock = new object();
    static readonly Dictionary<string, string> AudioApps = new Dictionary<string, string> {
        { "zoom", "Zoom" }, { "jw", "JWLibrary" },   // page name -> process name
    };

    static string AudioJson()
    {
        lock (AudioLock)
        {
            StringBuilder json = new StringBuilder("{\"system\":");
            try
            {
                IMMDevice speakers;
                Enumerator().GetDefaultAudioEndpoint(0, 1, out speakers);   // eRender, eMultimedia
                IAudioEndpointVolume volume = (IAudioEndpointVolume)Activate(speakers, typeof(IAudioEndpointVolume).GUID);
                IAudioMeterInformation meter = (IAudioMeterInformation)Activate(speakers, typeof(IAudioMeterInformation).GUID);
                float level, peak; bool mute;
                volume.GetMasterVolumeLevelScalar(out level); volume.GetMute(out mute); meter.GetPeakValue(out peak);
                json.Append(AudioLevel(level, mute, peak));
            }
            catch { json.Append("null"); }   // no speakers
            foreach (var app in AudioApps)
            {
                json.Append(",\"" + app.Key + "\":");
                float level = 0, peak = 0; bool mute = false, found = false;
                foreach (IAudioSessionControl2 session in Sessions(app.Value))
                {
                    try
                    {
                        float p;
                        ((IAudioMeterInformation)session).GetPeakValue(out p);
                        if (!found) { level = SessionVolume(session, out mute); found = true; }
                        peak = Math.Max(peak, p);
                    }
                    catch { }
                }
                if (found && !mute && level == 0 && MutedAt.ContainsKey(app.Key)) mute = true;
                json.Append(found ? AudioLevel(level, mute, peak) : "null");   // not playing anything yet
            }
            return json.Append("}").ToString();
        }
    }

    // target: system, zoom or jw. volume: 0-100, or null to leave it; mute: true/false, or null.
    static void SetAudio(string target, int? volume, bool? mute)
    {
        Guid none = Guid.Empty;
        lock (AudioLock)
        {
            try
            {
                if (target == "system")
                {
                    IMMDevice speakers;
                    Enumerator().GetDefaultAudioEndpoint(0, 1, out speakers);
                    IAudioEndpointVolume v = (IAudioEndpointVolume)Activate(speakers, typeof(IAudioEndpointVolume).GUID);
                    if (volume.HasValue) v.SetMasterVolumeLevelScalar(volume.Value / 100f, ref none);
                    if (mute.HasValue) v.SetMute(mute.Value, ref none);
                }
                else if (AudioApps.ContainsKey(target))
                {
                    List<IAudioSessionControl2> sessions = Sessions(AudioApps[target]);
                    if (sessions.Count == 0) return;
                    bool wasMuted;
                    float before = SessionVolume(sessions[0], out wasMuted);
                    foreach (IAudioSessionControl2 session in sessions)
                    {
                        try
                        {
                            ISimpleAudioVolume simple = session as ISimpleAudioVolume;
                            if (volume.HasValue) SetSessionVolume(session, volume.Value / 100f);
                            if (mute.HasValue && simple != null) simple.SetMute(mute.Value, ref none);
                            // Without Windows' own mute for apps, muting is volume 0, and unmuting puts it back.
                            else if (mute == true) SetSessionVolume(session, 0);
                            else if (mute == false) SetSessionVolume(session, MutedAt.ContainsKey(target) ? MutedAt[target] : 1);
                        }
                        catch { }
                    }
                    if (volume.HasValue || mute == false) MutedAt.Remove(target);
                    else if (mute == true && sessions[0] as ISimpleAudioVolume == null && before > 0) MutedAt[target] = before;
                }
            }
            catch { }
        }
    }

    // The level each app had when it was muted by setting its volume to 0, to put back when unmuted.
    static readonly Dictionary<string, float> MutedAt = new Dictionary<string, float>();

    // An app's volume as in Windows' Volume mixer. Some Windows versions don't offer the simple
    // control for other apps' sound, only one per channel (left, right...), which is used then.
    static float SessionVolume(IAudioSessionControl2 session, out bool mute)
    {
        mute = false;
        ISimpleAudioVolume simple = session as ISimpleAudioVolume;
        if (simple != null) { float v; simple.GetMasterVolume(out v); simple.GetMute(out mute); return v; }
        IChannelAudioVolume channels = (IChannelAudioVolume)session;
        uint n; channels.GetChannelCount(out n);
        float level = 0;
        for (uint c = 0; c < n; c++) { float v; channels.GetChannelVolume(c, out v); level = Math.Max(level, v); }
        return level;
    }

    static void SetSessionVolume(IAudioSessionControl2 session, float level)
    {
        Guid none = Guid.Empty;
        ISimpleAudioVolume simple = session as ISimpleAudioVolume;
        if (simple != null) { simple.SetMasterVolume(level, ref none); return; }
        IChannelAudioVolume channels = (IChannelAudioVolume)session;
        uint n; channels.GetChannelCount(out n);
        for (uint c = 0; c < n; c++) channels.SetChannelVolume(c, level, ref none);
    }

    static string AudioLevel(float volume, bool mute, float peak)
    {
        return "{\"vol\":" + volume.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
               ",\"mute\":" + (mute ? "true" : "false") +
               ",\"peak\":" + peak.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "}";
    }

    static IMMDeviceEnumerator Enumerator()
    {
        return (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
    }

    static object Activate(IMMDevice device, Guid iid)
    {
        object o;
        device.Activate(ref iid, 23, IntPtr.Zero, out o);   // CLSCTX_ALL
        return o;
    }

    // An app's sound sessions on every working output (an app can play to other speakers).
    static List<IAudioSessionControl2> Sessions(string processName)
    {
        List<IAudioSessionControl2> found = new List<IAudioSessionControl2>();
        HashSet<int> pids = new HashSet<int>();
        foreach (Process p in Process.GetProcessesByName(processName)) pids.Add(p.Id);
        if (pids.Count == 0) return found;
        try
        {
            IMMDeviceCollection outputs;
            Enumerator().EnumAudioEndpoints(0, 1, out outputs);   // eRender, DEVICE_STATE_ACTIVE
            int count;
            outputs.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    IMMDevice device;
                    outputs.Item(i, out device);
                    IAudioSessionEnumerator list;
                    ((IAudioSessionManager2)Activate(device, typeof(IAudioSessionManager2).GUID)).GetSessionEnumerator(out list);
                    int n;
                    list.GetCount(out n);
                    for (int j = 0; j < n; j++)
                    {
                        IAudioSessionControl2 session;
                        list.GetSession(j, out session);
                        int state; uint pid;
                        session.GetState(out state);
                        session.GetProcessId(out pid);
                        if (state != 2 && pids.Contains((int)pid)) found.Add(session);   // 2: expired
                    }
                }
                catch { }
            }
        }
        catch { }
        return found;
    }

    // ---- JW Library's second display ---------------------------------------------

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    struct RECT { public int Left, Top, Right, Bottom; }

    static readonly object JwLock = new object();
    static IntPtr JwWindow;
    static DateTime JwCheckedAt;

    // With its second display on, JW Library has two windows called "JW Library"; the one without
    // the menu button is the second display. Looked up again every few seconds.
    static IntPtr FindJwSecondDisplay()
    {
        if (JwWindow != IntPtr.Zero && IsWindow(JwWindow) && DateTime.Now - JwCheckedAt < TimeSpan.FromSeconds(5)) return JwWindow;
        JwWindow = IntPtr.Zero;
        JwCheckedAt = DateTime.Now;
        var named = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.NameProperty, "JW Library"));
        var menu = new PropertyCondition(AutomationElement.AutomationIdProperty, "HamburgerRadioButton");
        if (named.Count < 2) return IntPtr.Zero;
        // The one without the menu - but not a minimised main window, whose menu can't be seen either.
        foreach (AutomationElement w in named)
        {
            IntPtr h = new IntPtr(w.Current.NativeWindowHandle);
            if (!IsIconic(h) && w.FindFirst(TreeScope.Descendants, menu) == null) { JwWindow = h; break; }
        }
        return JwWindow;
    }

    static AutomationElement JwMain;
    static volatile string JwMedia = "none";   // what JW Library has open: none, image, video, unknown, hidden or closed
    static DateTime JwStateAskedAt = DateTime.MinValue;

    // Looking through JW Library's window takes up to a second, so it's done here in the background
    // (only while the page is asking) and /jw/state answers straight away with the latest result.
    static void WatchJwMedia()
    {
        while (true)
        {
            if (DateTime.Now - JwStateAskedAt < TimeSpan.FromSeconds(5)) JwMedia = JwMediaOpen();
            Thread.Sleep(100);
        }
    }

    // Asked of JW Library's main window, which shows what's playing whichever screen it's shown on:
    // a video (playing or paused) has its play/pause button; a picture has the picture viewer.
    // Asking JW Library beats guessing from the screen, which can't tell a still moment in a video
    // (a title, a scripture) from a picture, and sees the desktop when JW Library isn't showing.
    // While JW Library changes page its window can look empty for a moment: that's "unknown", not
    // "none", so the camera doesn't cut away from a song that's just starting. A playing video looks
    // empty too once its controls hide; the page counts a lasting "unknown" as a video. Minimised,
    // the window looks empty as well, so that's "hidden" (keep the shot) rather than "unknown".
    static string JwMediaOpen()
    {
        if (Process.GetProcessesByName("JWLibrary").Length == 0) { JwMain = null; return "closed"; }
        lock (JwLock)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (JwMain == null)
                    {
                        var menu = new PropertyCondition(AutomationElement.AutomationIdProperty, "HamburgerRadioButton");
                        bool minimised = false;
                        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children,
                                     new PropertyCondition(AutomationElement.NameProperty, "JW Library")))
                        {
                            if (IsIconic(new IntPtr(w.Current.NativeWindowHandle))) { minimised = true; continue; }
                            if (w.FindFirst(TreeScope.Descendants, menu) != null) { JwMain = w; break; }
                        }
                        if (JwMain == null) return minimised ? "hidden" : "none";
                    }
                    if (IsIconic(new IntPtr(JwMain.Current.NativeWindowHandle))) return "hidden";
                    // One pass through the window for all three: a song's page is big, and looking
                    // three times made the cut to the screen noticeably late.
                    bool video = false, image = false, page = false;
                    foreach (AutomationElement el in JwMain.FindAll(TreeScope.Descendants, MediaMarkers))
                    {
                        if (el.Current.IsOffscreen) continue;
                        string id = el.Current.AutomationId;
                        if (id == "TogglePlaybackButton") video = true;
                        else if (id == "ImageRenderer") image = true;
                        else page = true;
                    }
                    if (video) return "video";
                    if (image) return "image";
                    if (page) return "none";
                    JwMain = null;   // looked up afresh next time, in case JW Library rebuilt its window
                    return "unknown";
                }
                catch { JwMain = null; }   // JW Library was closed or restarted: look it up again
            }
            return "none";
        }
    }

    // A video's play/pause button, the picture viewer, and the top bar every normal page has.
    static readonly Condition MediaMarkers = new OrCondition(
        new PropertyCondition(AutomationElement.AutomationIdProperty, "TogglePlaybackButton"),
        new PropertyCondition(AutomationElement.AutomationIdProperty, "ImageRenderer"),
        new PropertyCondition(AutomationElement.AutomationIdProperty, "TopCommandBar"));

    // With extra screens, JW Library fills one of them instead of opening a window: the only extra
    // screen, or with several, the one named in this file (put a name like \\.\DISPLAY2 in it).
    static string JwDisplayFile { get { return Path.Combine(DataDir, "jw-display.txt"); } }

    static Screen JwTargetScreen()
    {
        string saved = null;
        try { if (File.Exists(JwDisplayFile)) saved = File.ReadAllText(JwDisplayFile).Trim(); } catch { }
        Screen only = null;
        int extra = 0;
        foreach (Screen s in Screen.AllScreens)
        {
            if (s.Primary) continue;
            if (s.DeviceName == saved) return s;
            only = s; extra++;
        }
        return extra == 1 ? only : null;   // several and none saved: can't tell which
    }

    // A small picture of what the second display shows: read from JW Library's window when it has
    // one (works even when other windows cover it), otherwise from the screen JW Library fills.
    static byte[] JwScreen(int width, int height)
    {
        width = Math.Max(8, Math.Min(width, 1280)); height = Math.Max(8, Math.Min(height, 720));
        // Without JW Library the screen shows the desktop, which would look like a picture.
        if (Process.GetProcessesByName("JWLibrary").Length == 0) return null;
        lock (JwLock)
        {
            try
            {
                IntPtr hwnd = FindJwSecondDisplay();
                RECT r;
                Rectangle area;
                if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out r) && r.Right > r.Left && r.Bottom > r.Top)
                    area = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                else
                {
                    hwnd = IntPtr.Zero;
                    Screen screen = JwTargetScreen();
                    if (screen == null) return null;
                    area = screen.Bounds;
                }
                using (Bitmap full = new Bitmap(area.Width, area.Height))
                using (Bitmap small = new Bitmap(width, height))
                using (MemoryStream png = new MemoryStream())
                {
                    using (Graphics g = Graphics.FromImage(full))
                    {
                        bool ok = false;
                        if (hwnd != IntPtr.Zero)
                        {
                            IntPtr dc = g.GetHdc();
                            ok = PrintWindow(hwnd, dc, 2);   // PW_RENDERFULLCONTENT: includes video and web content
                            g.ReleaseHdc(dc);
                        }
                        // No window to read, or it couldn't be read: what's on its screen.
                        if (!ok) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                    }
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(full, 0, 0, width, height);
                    }
                    small.Save(png, ImageFormat.Png);
                    return png.ToArray();
                }
            }
            catch { JwWindow = IntPtr.Zero; return null; }
        }
    }

    // ---- OBS websocket settings -------------------------------------------------

    // OBS keeps its websocket port and password in plugin_config\obs-websocket\config.json.
    static string ObsJson()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   @"obs-studio\plugin_config\obs-websocket\config.json");
        string text;
        try { text = File.ReadAllText(path); } catch { return "{\"found\":false}"; }
        Match enabled = Regex.Match(text, "\"server_enabled\"\\s*:\\s*(true|false)");
        Match auth = Regex.Match(text, "\"auth_required\"\\s*:\\s*(true|false)");
        Match port = Regex.Match(text, "\"server_port\"\\s*:\\s*(\\d+)");
        Match pw = Regex.Match(text, "\"server_password\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        bool needsPw = !auth.Success || auth.Groups[1].Value == "true";
        return "{\"found\":true,\"enabled\":" + (enabled.Success ? enabled.Groups[1].Value : "true") +
               ",\"port\":" + (port.Success ? port.Groups[1].Value : "4455") +
               ",\"password\":\"" + (needsPw && pw.Success ? pw.Groups[1].Value : "") + "\"}";   // already JSON-escaped
    }

    // ---- Web server -------------------------------------------------------------

    static List<string> LanIPs()
    {
        // Addresses a tablet can reach: adapters that are up and have a router (skips WSL/VPN-only adapters).
        List<string> ips = new List<string>();
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            IPInterfaceProperties props = ni.GetIPProperties();
            bool hasGateway = false;
            foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
                if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) hasGateway = true;
            if (!hasGateway) continue;
            foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork) ips.Add(u.Address.ToString());
        }
        return ips;
    }

    static byte[] Resource(string name)
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (MemoryStream m = new MemoryStream())
        {
            if (s == null) return null;
            s.CopyTo(m);
            return m.ToArray();
        }
    }

    // A file from the lib folder, e.g. "lib/model/model.json"; null if there's no such file.
    static byte[] LibFile(string path)
    {
        if (!Regex.IsMatch(path, @"^lib(/[\w.-]+)+$") || path.Contains("..")) return null;
        string local = Path.Combine(AppDir, path.Replace('/', '\\'));
        return File.Exists(local) ? File.ReadAllBytes(local) : Resource(path);
    }

    static readonly Dictionary<string, string> Types = new Dictionary<string, string> {
        { ".js", "text/javascript" }, { ".json", "application/json" }, { ".wasm", "application/wasm" },
        { ".png", "image/png" }, { ".webmanifest", "application/manifest+json" },
    };

    static byte[] Page(string file = "obs-remote.html")
    {
        string local = Path.Combine(AppDir, file);
        return File.Exists(local) ? File.ReadAllBytes(local) : Resource(file);
    }

    static string Json(string s)
    {
        StringBuilder b = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') b.Append('\\').Append(c);
            else if (c < ' ') b.AppendFormat("\\u{0:x4}", (int)c);
            else b.Append(c);
        }
        return b.Append('"').ToString();
    }

    static void Serve(TcpListener listener)
    {
        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            ThreadPool.QueueUserWorkItem(_ => Handle(client));
        }
    }

    static void Handle(TcpClient client)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 3000;
            StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 4096);
            string request = reader.ReadLine() ?? "";
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (string line; !string.IsNullOrEmpty(line = reader.ReadLine()); )
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            string[] parts = request.Split(' ');
            string[] target = (parts.Length > 1 ? parts[1] : "").Split('?');
            string path = target[0], query = target.Length > 1 ? target[1] : "";
            byte[] body;
            // Only the Meeting AV window on this PC may read the OBS password or control the app:
            // the request must come from this PC, be addressed to localhost (so another website can't
            // borrow the address) and carry a header that other websites can't add without asking first.
            string host, marker;
            IPAddress from = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
            if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
            bool local = IPAddress.IsLoopback(from) && headers.TryGetValue("Host", out host) &&
                         (host == "127.0.0.1:" + Port || host == "localhost:" + Port || host == "[::1]:" + Port) &&
                         headers.TryGetValue("X-Meeting-Camera", out marker);

            if (path == "/" || path == "/index.html")
                Send(stream, "200 OK", "text/html; charset=utf-8", Page());
            else if (path == "/timer-screen")
                Send(stream, "200 OK", "text/html; charset=utf-8", Page("timer.html"));
            else if (path.StartsWith("/lib/") && (body = LibFile(path.Substring(1))) != null)
            {
                string type;
                if (!Types.TryGetValue(Path.GetExtension(path), out type)) type = "application/octet-stream";
                Send(stream, "200 OK", type, body);
            }
            else if (path == "/info")
                SendJson(stream, "{\"ips\":[" + string.Join(",", LanIPs().ConvertAll(Json)) + "],\"port\":" + Port + ",\"app\":true}", true);
            else if (path == "/obs" && local)
                SendJson(stream, ObsJson());
            else if (path == "/setup" && local)
                SendJson(stream, SetupJson());
            else if (path == "/jw/screen" && local)
            {
                Match w = Regex.Match(query, "w=(\\d+)"), h = Regex.Match(query, "h=(\\d+)");
                byte[] png = JwScreen(w.Success ? int.Parse(w.Groups[1].Value) : 64, h.Success ? int.Parse(h.Groups[1].Value) : 36);
                if (png != null) Send(stream, "200 OK", "image/png", png);
                else Send(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("JW Library's second display isn't open"));
            }
            else if (path == "/jw/state" && local)
            {
                JwStateAskedAt = DateTime.Now;
                SendJson(stream, "{\"media\":" + Json(JwMedia) + "}");
            }
            else if (path == "/jw/info" && local)
            {
                Screen sc = JwTargetScreen();
                SendJson(stream, "{\"window\":" + (FindJwSecondDisplay() != IntPtr.Zero ? "true" : "false") + ",\"screen\":" +
                    (sc == null ? "null" : "{\"name\":" + Json(sc.DeviceName) + ",\"x\":" + sc.Bounds.X + ",\"y\":" + sc.Bounds.Y +
                                           ",\"w\":" + sc.Bounds.Width + ",\"h\":" + sc.Bounds.Height + "}") + "}");
            }
            else if (path == "/setup/run" && local)
            {
                Match m = Regex.Match(query, "meeting=(Midweek|Weekend)");
                RunSetup(m.Success ? m.Groups[1].Value : null);
                SendJson(stream, SetupJson());
            }
            else if (path == "/app" && local)
                SendJson(stream, AppJson());
            else if (path == "/app/set" && local)
            {
                Match st = Regex.Match(query, "startup=([01])"), up = Regex.Match(query, "updates=([01])");
                try
                {
                    if (st.Success) SetStartsWithWindows(st.Groups[1].Value == "1");
                    if (up.Success) { if (up.Groups[1].Value == "1") File.Delete(NoUpdatesFile); else File.WriteAllText(NoUpdatesFile, "off"); }
                }
                catch { }
                SendJson(stream, AppJson());
            }
            else if (path == "/app/check" && local)
            {
                CheckForUpdate(true);
                SendJson(stream, AppJson());
            }
            else if (path == "/app/restart" && local)
            {
                SendJson(stream, "{}");
                UiThread.BeginInvoke((Action)Restart);
            }
            else if (path == "/timer" && local)
                SendJson(stream, TimerJson());
            else if (path == "/timer/meeting" && local)
            {
                Match at = Regex.Match(query, "at=(\\d+)");
                MeetingAt = at.Success ? long.Parse(at.Groups[1].Value) : 0;
                CountdownOn = !query.Contains("countdown=0");
                SendJson(stream, "{}");
            }
            else if (path == "/jw/screen-back" && local)   // the countdown hands JW Library its screen back now
            {
                CountdownDismissedFor = MeetingAt;
                UiThread.BeginInvoke((Action)WatchCountdown);
                SendJson(stream, "{}");
            }
            else if ((path == "/jw/meetings" || path == "/jw/playlists") && local)
            {
                // One at a time: a second click while JW Library is still being moved is ignored,
                // rather than queued up to run straight after.
                if (Interlocked.Exchange(ref JwNavigating, 1) == 1) { SendJson(stream, "{\"ok\":false,\"busy\":true}"); return; }
                try
                {
                    bool ok = path == "/jw/meetings" ? JwOpenMeetings() : JwOpenPlaylists();
                    SendJson(stream, "{\"ok\":" + (ok ? "true" : "false") + "}");
                }
                finally { JwNavigating = 0; }
            }
            else if (path == "/tv" && local)   // show=zoom or show=jw
            {
                if (Interlocked.Exchange(ref JwNavigating, 1) == 1) { SendJson(stream, "{\"ok\":false,\"busy\":true}"); return; }
                try
                {
                    bool ok = ShowOnTv(query.Contains("show=zoom"));
                    SendJson(stream, "{\"ok\":" + (ok ? "true" : "false") + ",\"tv\":\"" + (TvShowsZoom ? "zoom" : "jw") + "\"}");
                }
                finally { JwNavigating = 0; }
            }
            else if (path == "/windows/arrange" && local)
                SendJson(stream, ArrangeWindows());
            else if (path == "/jw/second-display" && local)   // on=1 or on=0
            {
                bool ok = JwSetSecondDisplay(!query.Contains("on=0"));
                SendJson(stream, "{\"ok\":" + (ok ? "true" : "false") + "}");
            }
            else if (path == "/timer/set" && local)
            {
                Match a = Regex.Match(query, "action=(\\w+)"), n = Regex.Match(query, "secs=(-?\\d+)"), l = Regex.Match(query, "label=([^&]*)");
                TimerDo(a.Groups[1].Value, n.Success ? int.Parse(n.Groups[1].Value) : 0,
                        l.Success ? Uri.UnescapeDataString(l.Groups[1].Value.Replace('+', ' ')) : null);
                SendJson(stream, TimerJson());
            }
            else if (path == "/timer/show" && local)
            {
                Match m = Regex.Match(query, "screen=([^&]*)");
                string name = m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : "";
                OnUi(() => ShowTimerScreen(name));
                SendJson(stream, TimerJson());
            }
            else if (path == "/timer/parts" && local)
            {
                Match y = Regex.Match(query, "year=(\\d{4})"), w = Regex.Match(query, "week=(\\d{1,2})");
                if (y.Success && w.Success) SendJson(stream, PartsJson(int.Parse(y.Groups[1].Value), int.Parse(w.Groups[1].Value)));
                else Send(stream, "400 Bad Request", "text/plain", Encoding.ASCII.GetBytes("year and week needed"));
            }
            else if (path == "/screens" && local)
                SendJson(stream, OnUi(() => ScreensJson()));
            else if (path == "/audio" && local)
                SendJson(stream, AudioJson());
            else if (path == "/audio/set" && local)
            {
                Match t = Regex.Match(query, "target=(\\w+)"), v = Regex.Match(query, "vol=(\\d+)"), m = Regex.Match(query, "mute=([01])");
                SetAudio(t.Groups[1].Value, v.Success ? (int?)Math.Min(100, int.Parse(v.Groups[1].Value)) : null,
                         m.Success ? (bool?)(m.Groups[1].Value == "1") : null);
                SendJson(stream, AudioJson());
            }
            else if (path == "/show" && local)
            {
                UiThread.BeginInvoke((Action)OpenWindow);
                SendJson(stream, "{}");
            }
            else if ((path == "/hello" || path == "/bye") && local)
            {
                bool bye = path == "/bye";
                string id = Regex.Match(query, "id=(\\w+)").Groups[1].Value;
                UiThread.BeginInvoke((Action)(() =>
                {
                    if (bye) OpenWindows.Remove(id); else OpenWindows.Add(id);
                    QuitTimer.Stop();
                    if (OpenWindows.Count == 0) QuitTimer.Start();
                }));
                SendJson(stream, "{}");
            }
            else
                Send(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("Not found"));
        }
        catch { }
        finally { client.Close(); }
    }

    // Only /info may be read by other pages (the old page, opened as a file, asks it for the tablet address).
    static void SendJson(Stream stream, string json, bool shared = false)
    {
        Send(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(json), shared);
    }

    static void Send(Stream stream, string status, string type, byte[] body, bool shared = false)
    {
        string head = "HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                      "\r\nCache-Control: no-store\r\n" + (shared ? "Access-Control-Allow-Origin: *\r\n" : "") +
                      "Connection: close\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(head);
        stream.Write(bytes, 0, bytes.Length);
        stream.Write(body, 0, body.Length);
    }
}

// The timer screen: a borderless window filling a screen the speakers can see, showing
// timer.html. It never takes the keyboard from the window being used.
class TimerWindow : Form
{
    readonly WebView2 view = new WebView2 { Dock = DockStyle.Fill };

    public TimerWindow(string url, string userData, Screen screen, bool onTop = false)
    {
        Text = onTop ? "Meeting AV countdown" : "Meeting AV timer";
        TopMost = onTop;   // the countdown goes over JW Library's own screen
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        want = screen.Bounds;
        Bounds = want;
        // Windows resizes a window that lands on a screen with a different scaling (125%, 150%…), which
        // pushed it partly off its screen: it's put back on exactly the screen's own pixels.
        Shown += (s, e) => Place();
        DpiChanged += (s, e) => { e.Cancel = true; BeginInvoke((Action)Place); };
        if (onTop)
        {
            // Kept on top while it's up - without ever taking the focus: making another window active on
            // JW Library's screen makes JW Library take its second display away.
            var keepOnTop = new System.Windows.Forms.Timer { Interval = 2000 };
            keepOnTop.Tick += (s, e) => { if (IsHandleCreated) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); };
            keepOnTop.Start();
            FormClosed += (s, e) => keepOnTop.Dispose();
        }
        view.DefaultBackgroundColor = Color.Black;
        Controls.Add(view);
        Load += async (s, e) =>
        {
            try
            {
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userData);
                await view.EnsureCoreWebView2Async(env);
                view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                view.CoreWebView2.ProcessFailed += (o, a) => { try { view.CoreWebView2.Reload(); } catch { } };
                view.Source = new Uri(url);
            }
            catch { Close(); }
        };
    }

    Rectangle want;
    public void MoveTo(Screen screen) { want = screen.Bounds; Bounds = want; Place(); }

    void Place()
    {
        if (IsHandleCreated) SetWindowPos(Handle, IntPtr.Zero, want.X, want.Y, want.Width, want.Height, 0x0004 | 0x0010);   // no z-order change, no activate
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);

    protected override bool ShowWithoutActivation { get { return true; } }
}

// Meeting AV's own window: the page in a WebView2 (Edge's engine, part of Windows), so it has
// its own taskbar button and icon. Links that open a new window go to the normal browser.
class AppWindow : Form
{
    readonly WebView2 view = new WebView2 { Dock = DockStyle.Fill };
    public event Action Failed;
    public bool HasFailed;

    public AppWindow(string url, string userData, Icon icon)
    {
        Text = "Meeting AV";
        Icon = icon;
        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(area.X + area.Width / 10, area.Y + area.Height / 10, area.Width * 4 / 5, area.Height * 4 / 5);
        Controls.Add(view);
        Load += async (s, e) =>
        {
            try
            {
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userData);
                await view.EnsureCoreWebView2Async(env);
                view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                view.CoreWebView2.NewWindowRequested += (o, a) => { a.Handled = true; try { Process.Start(a.Uri); } catch { } };
                view.CoreWebView2.ProcessFailed += (o, a) => { try { view.CoreWebView2.Reload(); } catch { } };
                view.Source = new Uri(url);
            }
            catch
            {
                HasFailed = true;
                if (Failed != null) Failed();
            }
        };
    }

    public void Bring()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }
}

// ---- Windows Core Audio (the parts the sound mixer uses) ---------------------------

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    void GetCount(out int count);
    void Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IntPtr notify);
    void UnregisterControlChangeNotify(IntPtr notify);
    void GetChannelCount(out uint count);
    void SetMasterVolumeLevel(float levelDb, ref Guid context);
    void SetMasterVolumeLevelScalar(float level, ref Guid context);
    void GetMasterVolumeLevel(out float levelDb);
    void GetMasterVolumeLevelScalar(out float level);
    void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
    void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    void GetChannelVolumeLevel(uint channel, out float levelDb);
    void GetChannelVolumeLevelScalar(uint channel, out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation
{
    void GetPeakValue(out float peak);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    void GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
    void GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
    void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    void GetCount(out int count);
    void GetSession(int index, out IAudioSessionControl2 session);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    void GetState(out int state);
    void GetDisplayName(out IntPtr name);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
    void GetIconPath(out IntPtr path);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
    void GetGroupingParam(out Guid group);
    void SetGroupingParam(ref Guid group, ref Guid context);
    void RegisterAudioSessionNotification(IntPtr notify);
    void UnregisterAudioSessionNotification(IntPtr notify);
    void GetSessionIdentifier(out IntPtr id);
    void GetSessionInstanceIdentifier(out IntPtr id);
    void GetProcessId(out uint pid);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6F031A5A4C8F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, ref Guid context);
    void GetMasterVolume(out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("1C158861-B533-4B30-B1CF-E853E51C59B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IChannelAudioVolume
{
    void GetChannelCount(out uint count);
    void SetChannelVolume(uint index, float level, ref Guid context);
    void GetChannelVolume(uint index, out float level);
}
