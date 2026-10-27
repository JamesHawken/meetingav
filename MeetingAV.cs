// MeetingAV.exe - the Meeting AV page as a desktop app.
//   - Opens the page in its own window (WebView2, Edge's engine built into Windows; an Edge app
//     window if that's missing) and lives in the tray while it's open
//   - Gets the PC ready (OBS and its virtual camera, JW Library, and the Zoom meeting from the
//     link in Settings > This PC) by running Start-Meeting.ps1; progress shows in the page
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
    static volatile bool StartingUp;   // the setup run at start-up, until the windows are arranged: the page waits behind "Getting ready"
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
        // Something going wrong in the window is noted in log.txt and the app carries on; on another
        // thread it can't carry on, so it starts again (see Crashed).
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => Log("window: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Crashed(e.ExceptionObject as Exception);
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
        Background("tablet server", () => Serve(listener));
        Background("JW Library media", WatchJwMedia);
        Background("JW Library start", WatchJwStart);
        Background("microphone", HoldMicSteady);
        Background("OBS", WatchObs);

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
        // Before the window opens: starting up, it covers the PC's screen while the others open behind it.
        if (setup) StartingUp = true;
        OpenWindow();
        ShowTimerScreen("auto", true);   // where it was last time, if that screen is still there
        CountdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        CountdownTimer.Tick += (s, e) => WatchCountdown();
        CountdownTimer.Start();
        if (setup)
        {
            RunSetup(SetupMeeting);
            // However the start-up goes, the PC's screen isn't covered for more than 3 minutes.
            var giveUp = new System.Windows.Forms.Timer { Interval = 180000 };
            giveUp.Tick += (s, e) => { giveUp.Stop(); Uncover(); StartingUp = false; };
            giveUp.Start();
        }
        Application.Run();
        GC.KeepAlive(Single);
        return 0;
    }

    static Mutex Single;   // only one copy runs

    // ---- Staying up ---------------------------------------------------------------
    // What went wrong goes in log.txt (in the data folder): started afresh at 200 KB, the one before
    // kept as log.old.txt.

    static readonly object LogLock = new object();
    static string LogFile { get { return Path.Combine(DataDir, "log.txt"); } }

    static void Log(string what)
    {
        lock (LogLock)
        {
            try
            {
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 200000)
                {
                    File.Copy(LogFile, Path.Combine(DataDir, "log.old.txt"), true);
                    File.Delete(LogFile);
                }
                File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + what + Environment.NewLine);
            }
            catch { }
        }
    }

    // A thread of its own, whose failure is noted in the log rather than closing the app.
    static void Background(string name, Action work)
    {
        new Thread(() =>
        {
            try { work(); }
            catch (Exception e) { Log(name + ": " + e); }
        }) { IsBackground = true }.Start();
    }

    // Something failed that the app can't carry on from: a new copy starts in its place, without the
    // setup (OBS, JW Library and Zoom are left as they are) - but not twice within 2 minutes, so a
    // problem at start-up can't keep it starting over.
    static void Crashed(Exception e)
    {
        Log("stopped: " + e);
        try
        {
            string last = Path.Combine(DataDir, "last-crash.txt");
            if (File.Exists(last) && DateTime.Now - File.GetLastWriteTime(last) < TimeSpan.FromMinutes(2)) return;
            Touch(last);
            if (Tray != null) Tray.Visible = false;
            Process.Start(Application.ExecutablePath, "-NoSetup -after " + Process.GetCurrentProcess().Id);
        }
        catch { }
    }

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
        AppWindow w = new AppWindow(StartingUp ? Url + "?starting" : Url, userData, new Icon(new MemoryStream(Resource("MeetingAV.ico"))));
        w.Failed += () => { w.Close(); Window = null; OpenInEdge(); };   // no WebView2 on this PC
        w.FormClosed += (s, e) => { if (!w.HasFailed) Quit(); };
        Window = w;
        w.Message += OnPageMessage;
        if (StartingUp) w.Cover(true);
        else w.WindowState = FormWindowState.Maximized;   // the whole screen, still a window (after the setup: see ArrangeWhenOpen)
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

    // Runs Start-Meeting.ps1 hidden; its output lines are shown by the page. obsOnly: just OBS, with
    // JW Library, Zoom and the windows left as they are.
    static void RunSetup(string meeting, bool obsOnly = false)
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
                if (obsOnly) arguments += " -NoApps";
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
            try { if (!obsOnly) ArrangeWhenOpen(); }
            catch (Exception e) { Log("arranging: " + e); }
            finally { Uncover(); StartingUp = false; }
        }) { IsBackground = true }.Start();
    }

    // Once what the setup opened has its windows up (JW Library on the meeting's media, Zoom in the
    // meeting), Zoom's camera is switched on and they're arranged as by the Arrange button. Gives up
    // waiting after 90 seconds. Meeting AV's window, covering the PC's screen meanwhile, goes back to
    // normal before arranging - or after 30 seconds, in case Zoom is asking something (a passcode,
    // waiting for the host) that has to be seen.
    static void ArrangeWhenOpen()
    {
        bool wantJw, wantZoom, inMeeting;
        lock (SetupLock)
        {
            wantJw = SetupLines.Exists(l => l.Contains("JW Library") && l.Contains("opened"));
            wantZoom = SetupLines.Exists(l => l.Contains("Zoom meeting") && l.Contains("joining"));
            inMeeting = SetupLines.Exists(l => l.Contains("Zoom meeting") && l.Contains("already in a meeting"));
        }
        bool zoom = false;
        for (int i = 0; i < 45; i++)
        {
            bool jw = false;
            try { lock (JwLock) jw = JwMainWindow() != null; } catch { }
            zoom = false;
            foreach (Screen sc in Screen.AllScreens) zoom |= FindTopWindow("ConfMultiTabContentWndClass", sc) != IntPtr.Zero;
            if (wantZoom && !zoom) try { JoinFromPreview(); } catch { }
            if ((jw || !wantJw) && (zoom || !wantZoom)) break;
            if (i == 15) Uncover();
            Thread.Sleep(2000);
        }
        // The camera on in the meeting joined, or the one Zoom was already in.
        if (wantZoom && zoom || inMeeting)
        {
            string camera = "couldn't find Zoom's video button";
            try { camera = ZoomCameraOn(); } catch { }
            AddSetupLine(string.Format("  {0,-34}[{1}] {2}", "Zoom camera", camera.StartsWith("couldn't") ? "!!" : "ok", camera));
        }
        // JW Library going to the meeting's media first (WatchJwStart notices it within 2 seconds):
        // arranging moves it and takes it out of the front, which would stop it opening the card.
        if (wantJw)
        {
            Thread.Sleep(2500);
            for (int i = 0; i < 60 && JwStarting; i++) Thread.Sleep(500);
        }
        Uncover();
        try { OneAtATime(ArrangeWindows); } catch { }
        // At start-up Meeting AV then takes the whole screen (JW Library and Zoom shown in it, and
        // arranged on the right behind it); Arrange puts it back beside them.
        OnUi(() => { if (Window != null && !Window.IsDisposed) Window.WindowState = FormWindowState.Maximized; });
        AddSetupLine(string.Format("  {0,-34}[ok] done", "Windows arranged"));
    }

    // Meeting AV's window back to normal from covering the PC's screen (from any thread).
    static void Uncover()
    {
        try { if (Window != null && !Window.IsDisposed) Window.Invoke((Action)(() => Window.Cover(false))); } catch { }
    }

    static void AddSetupLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (SetupLock) SetupLines.Add(line.TrimEnd());
    }

    static string SetupJson()
    {
        lock (SetupLock)
            return "{\"running\":" + (SetupRunning ? "true" : "false") + ",\"startup\":" + (StartingUp ? "true" : "false") + ",\"lines\":[" +
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
                   ",\"jwOff\":" + (JwDisplayOff ? "true" : "false") + ",\"tv\":\"" + (TvShowsZoom ? "zoom" : "jw") + "\"" +
                   ",\"zoomMutedAt\":" + ZoomMutedAt + ",\"zoomMute\":" + Json(ZoomMuteResult) + ",\"zoomMics\":" + Json(ZoomMics) + "}";
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
    static string ZoomLinkFile { get { return Path.Combine(DataDir, "zoom-link.txt"); } }   // read by Start-Meeting.ps1

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
               ",\"zoomLink\":" + Json(File.Exists(ZoomLinkFile) ? File.ReadAllText(ZoomLinkFile).Trim() : "") +
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
        if (ZoomMuteOn && MeetingAt > 0 && left > 0 && left <= 30000 && MutedFor != MeetingAt)
        {
            MutedFor = MeetingAt;
            Background("muting Zoom", () => { ZoomMuteResult = MuteZoomEveryone(); ZoomMutedAt = NowMs(); });
        }
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
            Background("countdown", () =>
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
            });
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
        Background("JW Library's screen back", () =>
        {
            try { if (!JwRunning() || JwSetSecondDisplay(true)) { JwDisplayOff = false; TvShowsZoom = false; try { File.Delete(JwOffFile); } catch { } } }
            finally { JwBusy = false; }
        });
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

    static IntPtr JwMainSeen;   // JW Library's main window, the last time it was found

    static AutomationElement JwMainWindow()
    {
        var menu = new PropertyCondition(AutomationElement.AutomationIdProperty, "HamburgerRadioButton");
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children,
                     new PropertyCondition(AutomationElement.NameProperty, "JW Library")))
            if (w.FindFirst(TreeScope.Descendants, menu) != null) { JwMainSeen = new IntPtr(w.Current.NativeWindowHandle); return w; }
        return null;
    }

    // JW Library's main window, brought back (not made active) if it's minimised: minimised, it shows
    // Windows nothing, not even its menu. The one last seen is brought back; if it hasn't been seen
    // yet, each minimised JW Library window, until it's found (the others minimised again).
    static AutomationElement JwMainWindowRestored()
    {
        AutomationElement w = JwMainWindow();
        if (w != null || !JwRunning()) return w;
        var tried = new List<IntPtr>();
        if (JwMainSeen != IntPtr.Zero && IsWindow(JwMainSeen) && IsIconic(JwMainSeen)) tried.Add(JwMainSeen);
        else
            foreach (AutomationElement named in AutomationElement.RootElement.FindAll(TreeScope.Children,
                         new PropertyCondition(AutomationElement.NameProperty, "JW Library")))
            {
                IntPtr h = new IntPtr(named.Current.NativeWindowHandle);
                if (IsIconic(h)) tried.Add(h);
            }
        foreach (IntPtr h in tried)
        {
            ShowWindow(h, 4);   // restored, not activated
            w = WaitFor(JwMainWindow, 1500);
            if (w != null && new IntPtr(w.Current.NativeWindowHandle) == h) return w;
            ShowWindow(h, 7);   // not this one (its second display): minimised again
            if (w != null) return w;
        }
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
                AutomationElement w = JwMainWindowRestored();   // minimised: brought back
                if (w == null) return false;
                AutomationElement item = JwMenuItem(w, JwMeetingsName, 3);
                if (item == null) return false;
                Press(item);
                JwMain = null;   // its page changed: looked up afresh
                return JwOpenMeetingMedia(w);
            }
            catch { return false; }
        }
    }

    static bool JwOpenMeetingMedia(AutomationElement w)
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
        if (card == null) return false;
        // A card only opens with Enter, so JW Library is brought in front for it - and the key is sent
        // only if it really is in front, never into another program - then the window that was in
        // front gets it straight back.
        IntPtr jw = new IntPtr(w.Current.NativeWindowHandle), was = GetForegroundWindow();
        JwFrontWanted = true;
        try
        {
            card.SetFocus();
            Thread.Sleep(300);
            if (GetForegroundWindow() != jw) { BringToFront(jw); Thread.Sleep(300); card.SetFocus(); Thread.Sleep(200); }
            if (GetForegroundWindow() != jw) return false;
            SendKeys.SendWait("{ENTER}");
            Thread.Sleep(150);
        }
        finally { JwFrontWanted = false; }
        if (was != IntPtr.Zero && was != jw) BringToFront(was);
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
        return media != null;
    }

    // Meeting media and Playlists, from Meeting AV's buttons: JW Library goes to them without coming
    // to the front. It makes itself the active window as it changes page, so whatever was in front
    // (Meeting AV) is put back each time, while it's going there and for 3 seconds after - except
    // for the moment a meeting's card needs JW Library in front, to open it with Enter.
    static volatile bool JwFrontWanted;

    static bool JwKeepingFront(Func<bool> go)
    {
        IntPtr front = GetForegroundWindow();
        var title = new StringBuilder(32);
        if (front != IntPtr.Zero) { GetWindowText(front, title, 32); if (title.ToString() == "JW Library") front = IntPtr.Zero; }
        var done = new ManualResetEvent(false);
        if (front != IntPtr.Zero)
            new Thread(() =>
            {
                var t = new StringBuilder(32);
                DateTime until = DateTime.MaxValue;
                while (DateTime.Now < until)
                {
                    if (until == DateTime.MaxValue && done.WaitOne(15)) until = DateTime.Now.AddSeconds(3);
                    else if (until != DateTime.MaxValue) Thread.Sleep(15);
                    if (JwFrontWanted) continue;
                    IntPtr now = GetForegroundWindow();
                    if (now == front || now == IntPtr.Zero) continue;
                    t.Clear();
                    GetWindowText(now, t, 32);
                    if (t.ToString() == "JW Library") BringToFront(front);
                }
                done.Dispose();
            }) { IsBackground = true }.Start();
        // Minimised, JW Library is brought back out of sight to be driven, and minimised again after.
        IntPtr jw = JwMainHandle != IntPtr.Zero && IsWindow(JwMainHandle) ? JwMainHandle : JwMainSeen;
        bool minimised = jw != IntPtr.Zero && IsWindow(jw) && IsIconic(jw);
        WINDOWPLACEMENT was = minimised ? JwOutOfSight(jw) : default(WINDOWPLACEMENT);
        try { return go(); }
        finally
        {
            if (minimised) { Thread.Sleep(800); JwBackToMinimised(jw, was); }
            // Not taking the front, JW Library can still have come up over it: put back behind.
            else if (front != IntPtr.Zero && jw != IntPtr.Zero && IsWindow(jw) && Above(jw, front))
                SetWindowPos(jw, front, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // just under it; no size, move or activate
            if (front != IntPtr.Zero) done.Set(); else done.Dispose();
        }
    }

    // Whether window a is in front of window b.
    static bool Above(IntPtr a, IntPtr b)
    {
        for (IntPtr h = GetWindow(b, 3); h != IntPtr.Zero; h = GetWindow(h, 3))   // GW_HWNDPREV: the one in front
            if (h == a) return true;
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

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
                AutomationElement w = JwMainWindowRestored();
                if (w == null) return false;
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
        Background("JW Library tidy-up", () => JwTidyUp(w, jwh, on, thenMeetingMedia, section));
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

    // ---- Zoom: everyone muted 30 seconds before the start ---------------------------------------
    // Zoom's meeting window on this PC's screen (never its window on the hall's TV) comes to the
    // front; "Mute all" at the bottom of its Participants panel is clicked (the panel opened with
    // Alt+U if it's closed); in the box Zoom shows to confirm, "Allow participants to unmute
    // themselves" is unticked and Yes clicked. Zoom shows none of this to Windows' accessibility
    // interface, so it's found by its colours and clicked with the mouse, which is put back after,
    // as is the keyboard. It needs Meeting AV's user to be host or co-host.

    static bool ZoomMuteOn = true;
    static volatile string ZoomMics = "";   // what Meeting AV last left: "muted", "open", or "" (not known)
    static long MutedFor, ZoomMutedAt;
    static string ZoomMuteResult = "";

    // One at a time (with the JW Library buttons too): a second request while one is under way waits
    // for it (up to 10 seconds) rather than running at the same time, with both moving the mouse.
    static string MuteZoomEveryone() { return OneAtATime(() => KeepingWindows(() => TwiceIfNeeded(MuteZoomEveryoneHere, "muted"))); }

    static string OneAtATime(Func<string> work)
    {
        int tries = 0;
        while (Interlocked.Exchange(ref JwNavigating, 1) == 1)
            if (++tries > 100) return "busy - try again"; else Thread.Sleep(100);
        try { return work(); }
        finally { JwNavigating = 0; }
    }

    static string MuteZoomEveryoneHere()
    {
        Screen tv = JwTargetScreen();
        IntPtr zoom = IntPtr.Zero;
        EnumWindows((h, p) =>
        {
            var c = new StringBuilder(64);
            GetClassName(h, c, 64);
            if (c.ToString() == "ConfMultiTabContentWndClass" && IsWindowVisible(h) &&
                (tv == null || Screen.FromHandle(h).DeviceName != tv.DeviceName)) { zoom = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (zoom == IntPtr.Zero) return "no Zoom meeting open";
        IntPtr was = GetForegroundWindow();
        BringToFront(zoom);
        for (int i = 0; i < 10 && GetForegroundWindow() != zoom; i++) Thread.Sleep(50);
        if (GetForegroundWindow() != zoom) return "couldn't bring Zoom forward";
        // "Mute all" at the bottom of Zoom's Participants panel (Alt+M would be a toggle: pressed
        // when everyone is muted, it asks them all to unmute). The panel is opened if it's closed.
        Point mouseAt = Cursor.Position;
        IntPtr host;
        Point[] buttons = OpenZoomPanel(zoom, out host);
        Point muteAll = buttons == null ? Point.Empty : buttons[1];
        if (muteAll.IsEmpty) { if (was != IntPtr.Zero && was != zoom) BringToFront(was); return "couldn't find Zoom's Mute all"; }
        if (!ClickZoom(host, muteAll)) { if (was != IntPtr.Zero && was != zoom) BringToFront(was); return "couldn't click Zoom's Mute all"; }
        Cursor.Position = mouseAt;
        // Zoom asks to confirm, with "Allow participants to unmute themselves": its box shows no
        // controls to Windows' accessibility interface, so it's read by colour - the tickbox (left)
        // is blue only when ticked, the Yes button (right) is blue - and clicked with the mouse.
        string result = "muted";
        IntPtr box = IntPtr.Zero;
        for (int i = 0; i < 30 && box == IntPtr.Zero; i++)
        {
            Thread.Sleep(100);
            IntPtr d = FindWindow("zChangeNameWndClass", null);
            if (d != IntPtr.Zero && IsWindowVisible(d)) box = d;
        }
        if (box != IntPtr.Zero)
        {
            Point mouseWas = Cursor.Position;
            // Read and clicked only where nothing covers it: pinned on top of everything meanwhile.
            BringToFront(box);
            SetWindowPos(box, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
            Thread.Sleep(300);   // drawn
            Rectangle tick, yes;
            FindBlue(box, out tick, out yes);
            if (!tick.IsEmpty)
            {
                Click(tick.X + tick.Width / 2, tick.Y + tick.Height / 2);
                Thread.Sleep(300);
                FindBlue(box, out tick, out yes);
            }
            if (!tick.IsEmpty) result = "muted, but couldn't untick unmuting";
            else { result = "muted, unmuting not allowed"; ZoomMics = "muted"; }
            for (int i = 0; i < 2 && !yes.IsEmpty && IsWindowVisible(box); i++)
            {
                Click(yes.X + yes.Width / 2, yes.Y + yes.Height / 2);
                Thread.Sleep(600);
            }
            if (yes.IsEmpty) result = "couldn't find Zoom's Yes button";
            else if (IsWindowVisible(box)) result = "couldn't press Zoom's Yes button";
            Cursor.Position = mouseWas;
            if (IsWindowVisible(box)) SetWindowPos(box, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        Thread.Sleep(200);
        if (was != IntPtr.Zero && was != zoom) BringToFront(was);
        return result;
    }

    // Runs something that works in Zoom's window, then puts the PC's screen back as it was: every
    // window on it the same size and place, in the same order front to back, and the same one active.
    // Zoom's Participants panel, if it had to be opened, is closed again.
    static string KeepingWindows(Func<string> work)
    {
        IntPtr active = GetForegroundWindow();
        var windows = new List<IntPtr>();   // front to back
        var where = new Dictionary<IntPtr, RECT>();
        var maxed = new HashSet<IntPtr>();
        Screen pc = Screen.PrimaryScreen;
        EnumWindows((h, p) =>
        {
            RECT r;
            if (IsWindowVisible(h) && !IsIconic(h) && GetWindowRect(h, out r) && r.Right - r.Left > 50 &&
                Screen.FromHandle(h).DeviceName == pc.DeviceName)
            {
                windows.Add(h); where[h] = r;
                if (IsZoomed(h)) maxed.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        IntPtr zoom = FindTopWindow("ConfMultiTabContentWndClass", pc);
        bool popupWasOpen = ZoomPopup() != IntPtr.Zero, toolsWindowWasOpen = ZoomHostTools() != IntPtr.Zero;
        bool panelWasOpen = zoom != IntPtr.Zero && (popupWasOpen || FindPanelButtons(zoom) != null);
        bool toolsWereOpen = panelWasOpen && !HostToolsClose(zoom).IsEmpty;
        string result = work();
        // Panes opened for the work closed again: Host tools first (found from the Participants
        // panel's buttons), then the Participants panel.
        if (zoom != IntPtr.Zero && !toolsWereOpen && !HostToolsClose(zoom).IsEmpty)
        {
            BringToFront(zoom);
            Thread.Sleep(150);
            CloseHostTools(zoom);
            Thread.Sleep(400);
        }
        IntPtr toolsWindow = ZoomHostTools();
        if (!toolsWindowWasOpen && toolsWindow != IntPtr.Zero)
        {
            PostMessage(toolsWindow, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE, as its ✕
            Thread.Sleep(300);
        }
        IntPtr popup = ZoomPopup();
        if (zoom != IntPtr.Zero && !popupWasOpen && popup != IntPtr.Zero)
        {
            PostMessage(popup, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE, as its ✕
            Thread.Sleep(300);
        }
        else if (zoom != IntPtr.Zero && !panelWasOpen && FindPanelButtons(zoom) != null)
        {
            BringToFront(zoom);
            Thread.Sleep(150);
            keybd_event(0x12, 0, 0, UIntPtr.Zero);     // Alt+U closes the Participants panel again
            keybd_event(0x55, 0, 0, UIntPtr.Zero);
            keybd_event(0x55, 0, 2, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            Thread.Sleep(300);
        }
        // Back to front: each brought up in turn, so the front one ends up in front.
        for (int i = windows.Count - 1; i >= 0; i--)
        {
            IntPtr h = windows[i];
            if (!IsWindow(h)) continue;
            RECT r = where[h];
            if (maxed.Contains(h)) SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            else SetWindowPos(h, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0010);
        }
        if (active != IntPtr.Zero && IsWindow(active)) BringToFront(active);
        return result;
    }

    // Windows only lets the program in front choose what comes to the front, so this joins in with
    // the window in front for a moment to do it.
    static void BringToFront(IntPtr h)
    {
        IntPtr front = GetForegroundWindow();
        uint dummy, me = GetCurrentThreadId(), them = front == IntPtr.Zero ? 0 : GetWindowThreadProcessId(front, out dummy);
        bool attached = them != 0 && them != me && AttachThreadInput(me, them, true);
        AllowForeground();
        SetForegroundWindow(h);
        BringWindowToTop(h);
        if (attached) AttachThreadInput(me, them, false);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);

    // Zoom's Participants panel ends in a row of three grey buttons - Invite, Mute all, More - on the
    // panel's darker grey. Found from the bottom of Zoom's window up; the middle one's centre, in screen
    // pixels, or empty when the panel isn't open.
    static Point FindMuteAll(IntPtr zoom)
    {
        Point[] b = FindPanelButtons(zoom);
        return b == null ? Point.Empty : b[1];
    }

    // Invite, Mute all, More: their centres, or null.
    static Point[] FindPanelButtons(IntPtr zoom)
    {
        Rectangle z = ZoomArea(zoom);
        if (z.Width < 100 || z.Height < 100) return null;
        // The right half, top to bottom: Host tools open under the panel push its buttons up. A panel
        // in a window of its own fills it.
        bool own = ClassOf(zoom) == ZoomPopupClass;
        var area = Rectangle.FromLTRB(own ? z.Left : z.Left + z.Width / 2, z.Top + (own ? 30 : 60), z.Right, z.Bottom);
        using (var b = new Bitmap(area.Width, area.Height))
        {
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            return PanelButtonsIn(b, area.Location);
        }
    }

    // The same, in a picture of that part of Zoom's window, whose top left is at "at".
    static Point[] PanelButtonsIn(Bitmap b, Point at)
    {
        for (int y = b.Height - 4; y > 0; y -= 2)
        {
            List<int[]> row = ButtonRow(b, y);
            if (row == null) continue;
            // The row's middle, from the middle button's top and bottom edges (where the panel's
            // grey starts): the bottom edge found first is just below Zoom's menu when it's open.
            int cx = (row[1][0] + row[1][1]) / 2, top = y, bottom = y;
            Func<Color, bool> panel = c => c.R >= 22 && c.R <= 36 && Math.Abs(c.R - c.G) < 6 && Math.Abs(c.G - c.B) < 8;
            while (top > 0 && !panel(b.GetPixel(cx - 20, top - 1))) top--;
            while (bottom < b.Height - 1 && !panel(b.GetPixel(cx - 20, bottom + 1))) bottom++;
            int mid = (top + bottom) / 2;
            return row.ConvertAll(m => new Point(at.X + (m[0] + m[1]) / 2, at.Y + mid)).ToArray();
        }
        return null;
    }

    // The middle of the lowest line of white words in an area of the screen, or empty.
    static Point LowestTextLine(Rectangle area)
    {
        using (var b = new Bitmap(area.Width, area.Height))
        {
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            int bottom = -1, top = -1;
            for (int y = b.Height - 1; y >= 0; y--)
            {
                int bright = 0;
                for (int x = 0; x < b.Width; x++) { Color c = b.GetPixel(x, y); if (c.R > 170 && c.G > 170 && c.B > 170) bright++; }
                if (bright >= 3) { if (bottom < 0) bottom = y; top = y; }
                else if (bottom >= 0 && bottom - y > 2) break;   // past the line
            }
            return bottom < 0 ? Point.Empty : new Point(area.X + b.Width / 2, area.Y + (top + bottom) / 2);
        }
    }

    // The three buttons' left and right edges on one line of the picture, or null.
    static List<int[]> ButtonRow(Bitmap b, int y)
    {
        Func<Color, bool> button = c => c.R >= 37 && c.R <= 56 && Math.Abs(c.R - c.G) < 6 && Math.Abs(c.G - c.B) < 8;
        Func<Color, bool> panel = c => c.R >= 22 && c.R <= 36 && Math.Abs(c.R - c.G) < 6 && Math.Abs(c.G - c.B) < 8;
        var runs = new List<int[]>();
        int start = -1;
        for (int x = 0; x < b.Width; x++)
        {
            Color c = b.GetPixel(x, y);
            bool isButton = button(c) || (start >= 0 && !panel(c) && c.R > 56);   // words on a button
            if (isButton) { if (start < 0) start = x; }
            else if (start >= 0) { if (x - start >= 40) runs.Add(new[] { start, x }); start = -1; }
        }
        if (start >= 0 && b.Width - start >= 40) runs.Add(new[] { start, b.Width });
        // Words inside a button split it into pieces: pieces closer than 4px are one button.
        var merged = new List<int[]>();
        foreach (int[] run in runs)
            if (merged.Count > 0 && run[0] - merged[merged.Count - 1][1] < 4) merged[merged.Count - 1][1] = run[1];
            else merged.Add(run);
        return merged.Count == 3 && merged.TrueForAll(m => m[1] - m[0] >= 60) ? merged : null;
    }

    // ---- Zoom: everyone allowed to unmute themselves (without unmuting anyone) -------------------
    // In Zoom's Host tools pane, under "Participant permissions", the first switch is "Unmute
    // themselves". The pane is opened from the Participants panel's More menu (its last item) if it's
    // closed. Read by colour and clicked with the mouse, like the mute.
    static string AllowZoomUnmute() { return OneAtATime(() => KeepingWindows(() => TwiceIfNeeded(AllowZoomUnmuteHere, "allowed"))); }

    // Zoom isn't always ready the first time (still coming to the front, a pane still opening): a
    // second go, a moment later, unless there's no meeting at all.
    static string TwiceIfNeeded(Func<string> work, string success)
    {
        string result = work();
        if (result.StartsWith(success) && result != "muted, but couldn't untick unmuting" || result.StartsWith("no ") || result.StartsWith("nobody ")) return result;
        Thread.Sleep(600);
        return work();
    }

    static string AllowZoomUnmuteHere()
    {
        Screen tv = JwTargetScreen();
        IntPtr zoom = IntPtr.Zero;
        EnumWindows((h, p) =>
        {
            var c = new StringBuilder(64);
            GetClassName(h, c, 64);
            if (c.ToString() == "ConfMultiTabContentWndClass" && IsWindowVisible(h) &&
                (tv == null || Screen.FromHandle(h).DeviceName != tv.DeviceName)) { zoom = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (zoom == IntPtr.Zero) return "no Zoom meeting open";
        IntPtr was = GetForegroundWindow();
        Point mouseAt = Cursor.Position;
        BringToFront(zoom);
        Thread.Sleep(300);
        FitZoom(zoom);
        string result;
        try
        {
            bool on;
            // Participants or Host tools in a window of its own: Host tools is a window of its own then
            // too (and if it's already open, its switch is all there is to do).
            if (ZoomPopup() != IntPtr.Zero || ZoomHostTools() != IntPtr.Zero) return AllowUnmutePoppedOut(zoom, mouseAt);
            Point sw = FindUnmuteSwitchScrolling(zoom, out on);
            if (sw.IsEmpty)
            {
                Point[] buttons = FindPanelButtons(zoom);
                if (buttons == null)
                {
                    keybd_event(0x12, 0, 0, UIntPtr.Zero);     // Alt+U: Participants
                    keybd_event(0x55, 0, 0, UIntPtr.Zero);
                    keybd_event(0x55, 0, 2, UIntPtr.Zero);
                    keybd_event(0x12, 0, 2, UIntPtr.Zero);
                    for (int i = 0; i < 10 && buttons == null; i++) { Thread.Sleep(200); FitZoom(zoom); buttons = FindPanelButtons(zoom); }
                }
                if (buttons == null) return "couldn't find Zoom's Participants panel";
                for (int attempt = 0; attempt < 2 && sw.IsEmpty; attempt++)
                {
                    if (!ClickZoom(zoom, buttons[2])) break;   // More
                    Thread.Sleep(900);                   // the menu finishes appearing
                    // The menu opens over the buttons, its last item (Host tools) level with them. Only
                    // clicked once the buttons are covered - never Mute all by mistake.
                    if (FindPanelButtons(zoom) != null) continue;
                    // Host tools: the menu's last line of words, found by its white text.
                    Rectangle look = Rectangle.Intersect(new Rectangle(buttons[0].X, buttons[1].Y - 160, buttons[2].X + 40 - buttons[0].X, 180), ZoomArea(zoom));
                    Point item = look.IsEmpty ? Point.Empty : LowestTextLine(look);
                    if (item.IsEmpty || !ClickZoom(zoom, new Point(buttons[1].X, item.Y))) { keybd_event(0x1B, 0, 0, UIntPtr.Zero); keybd_event(0x1B, 0, 2, UIntPtr.Zero); continue; }
                    Thread.Sleep(300);
                    IntPtr oops = FindWindow("zChangeNameWndClass", null);   // Mute all after all: cancelled
                    if (oops != IntPtr.Zero && IsWindowVisible(oops))
                    {
                        BringToFront(oops);
                        keybd_event(0x1B, 0, 0, UIntPtr.Zero); keybd_event(0x1B, 0, 2, UIntPtr.Zero);
                        return "couldn't open Zoom's Host tools";
                    }
                    Thread.Sleep(800);
                    sw = FindUnmuteSwitchScrolling(zoom, out on);
                }
                if (sw.IsEmpty) { keybd_event(0x1B, 0, 0, UIntPtr.Zero); keybd_event(0x1B, 0, 2, UIntPtr.Zero); return "couldn't find Zoom's Host tools"; }
            }
            if (!on)
            {
                if (!ClickZoom(zoom, sw)) return "couldn't reach the switch";
                Cursor.Position = mouseAt;   // the pointer over it changes its colours
                // Zoom slides the switch across, and may move the pane: looked at for up to 2 seconds.
                for (int i = 0; i < 10 && !on; i++)
                {
                    Thread.Sleep(200);
                    bool now;
                    on = BlueNear(sw) || (!FindUnmuteSwitch(zoom, out now).IsEmpty && now);
                }
            }
            result = on ? "allowed" : "couldn't switch it on";
        }
        finally
        {
            Cursor.Position = mouseAt;
            if (was != IntPtr.Zero && was != zoom) BringToFront(was);
        }
        return result;
    }

    // With the Participants panel in a window of its own, Host tools opens in one too
    // ("HostToolPopupContainerWndClass"), opened from the panel's More menu, its last line (the menu
    // read as for lowering hands). The switch is read and clicked in that window the same way.
    static string AllowUnmutePoppedOut(IntPtr zoom, Point mouseAt)
    {
        bool on;
        IntPtr tools = ZoomHostTools();
        Point sw = Point.Empty;
        if (tools != IntPtr.Zero) { BringToFront(tools); Thread.Sleep(300); sw = FindUnmuteSwitch(tools, out on); }
        if (sw.IsEmpty)
        {
            IntPtr host;
            Point[] buttons = OpenZoomPanel(zoom, out host);
            if (buttons == null) return "couldn't find Zoom's Participants panel";
            if (!ClickZoom(host, buttons[2])) return "couldn't click Zoom's More";
            Cursor.Position = mouseAt;
            Thread.Sleep(900);
            Rectangle look; int[] after;
            Rectangle menu = FindZoomMenu(out look, out after);
            List<int> lines = menu.IsEmpty ? null : MenuLines(look, after, menu, false);
            // Its last line is Host tools; never one in the first group (asking all to unmute).
            if (lines == null || lines.Count < 3 || MenuLines(look, after, menu, true).Contains(lines[lines.Count - 1]))
            {
                keybd_event(0x1B, 0, 0, UIntPtr.Zero); keybd_event(0x1B, 0, 2, UIntPtr.Zero);
                return "couldn't open Zoom's Host tools";
            }
            Click(menu.Left + menu.Width / 4, lines[lines.Count - 1]);
            Cursor.Position = mouseAt;
            for (int i = 0; i < 10 && tools == IntPtr.Zero; i++) { Thread.Sleep(200); tools = ZoomHostTools(); }
            if (tools == IntPtr.Zero) return "couldn't open Zoom's Host tools";
            BringToFront(tools);
            Thread.Sleep(500);
            sw = FindUnmuteSwitch(tools, out on);
            if (sw.IsEmpty) return "couldn't find the switch in Zoom's Host tools";
        }
        else FindUnmuteSwitch(tools, out on);
        if (!on)
        {
            if (!ClickZoom(tools, sw)) return "couldn't reach the switch";
            Cursor.Position = mouseAt;
            for (int i = 0; i < 10 && !on; i++)
            {
                Thread.Sleep(200);
                bool now;
                on = BlueNear(sw) || (!FindUnmuteSwitch(tools, out now).IsEmpty && now);
            }
        }
        return on ? "allowed" : "couldn't switch it on";
    }

    // Host tools in a window of its own, or none.
    static IntPtr ZoomHostTools()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) =>
        {
            if (IsWindowVisible(h) && !IsIconic(h) && ClassOf(h) == "HostToolPopupContainerWndClass") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // Closes the Host tools pane with the ✕ at the right of its heading: under the Participants
    // panel's buttons, the first line with light icons at the pane's right edge, its rightmost one
    // (the one left of it opens the pane in a window of its own, so that's never the one clicked).
    static void CloseHostTools(IntPtr zoom)
    {
        Point x = HostToolsClose(zoom);
        if (!x.IsEmpty) ClickZoom(zoom, x);
    }

    // Where the Host tools pane's ✕ is, or empty when it isn't open (or can't be told).
    static Point HostToolsClose(IntPtr zoom)
    {
        Point[] buttons = FindPanelButtons(zoom);
        if (buttons == null) return HostToolsAloneClose(zoom);
        Rectangle z = ZoomArea(zoom);
        if (buttons == null || z.IsEmpty) return Point.Empty;
        var look = Rectangle.Intersect(Rectangle.FromLTRB(z.Right - 70, buttons[1].Y + 20, z.Right - 2, buttons[1].Y + 110), z);
        if (look.Width < 20 || look.Height < 20) return Point.Empty;
        using (var b = new Bitmap(look.Width, look.Height))
        {
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(look.Location, Point.Empty, look.Size);
            Func<Color, bool> icon = c => c.R > 140 && Math.Abs(c.R - c.G) < 12 && Math.Abs(c.G - c.B) < 14;
            for (int y = 0; y < b.Height; y++)
            {
                int right = -1, count = 0;
                for (int x = 0; x < b.Width; x++) if (icon(b.GetPixel(x, y))) { right = x; count++; }
                if (count < 2) continue;
                // That line's rightmost icon: its pixels within 14px left of the rightmost one.
                int left = right;
                for (int x = right; x >= Math.Max(0, right - 14); x--) if (icon(b.GetPixel(x, y))) left = x;
                return new Point(look.X + (left + right) / 2, look.Y + y + 5);
            }
        }
        return Point.Empty;
    }

    // Host tools open without the Participants panel: found by its "Suspend all" heading, and its ✕
    // on the first line above that with two light icons at the pane's right edge (⧉ ✕).
    static Point HostToolsAloneClose(IntPtr zoom)
    {
        Rectangle z = ZoomArea(zoom);
        if (z.Width < 200 || z.Height < 100) return Point.Empty;
        var area = Rectangle.FromLTRB(z.Right - 170, z.Top + 30, z.Right - 2, z.Bottom - 2);
        using (var b = new Bitmap(area.Width, area.Height))
        {
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            Func<Color, bool> pill = c => c.R >= 44 && c.R <= 58 && Math.Abs(c.R - c.G) < 5 && Math.Abs(c.G - c.B) < 6;
            Func<Color, bool> blueWord = c => c.B > 150 && c.B - c.R > 70;
            Func<Color, bool> icon = c => c.R > 140 && Math.Abs(c.R - c.G) < 12 && Math.Abs(c.G - c.B) < 14;
            int heading = -1;
            for (int y = 0; y < b.Height && heading < 0; y++)
            {
                int pills = 0, words = 0;
                for (int x = 0; x < b.Width; x++) { Color c = b.GetPixel(x, y); if (pill(c)) pills++; else if (blueWord(c)) words++; }
                if (pills >= 30 && words >= 3) heading = y;
            }
            if (heading < 0) return Point.Empty;
            int strip = b.Width - 70;
            for (int y = heading - 12; y >= 0; y--)
            {
                // Light pixels in the strip at the right, in two groups at least 10px apart.
                int first = -1, last = -1, groups = 0, prev = -100;
                for (int x = strip; x < b.Width; x++)
                    if (icon(b.GetPixel(x, y))) { if (x - prev > 10) groups++; if (first < 0) first = x; last = x; prev = x; }
                if (groups < 2) continue;
                int left = last;
                for (int x = last; x >= Math.Max(strip, last - 14); x--) if (icon(b.GetPixel(x, y))) left = x;
                return new Point(area.X + (left + last) / 2, area.Y + y + 5);
            }
        }
        return Point.Empty;
    }

    // In a short Zoom window the Host tools pane is cut off before its permissions: it's scrolled
    // down, a few steps at a time, until the switch shows.
    static Point FindUnmuteSwitchScrolling(IntPtr zoom, out bool on)
    {
        Point sw = FindUnmuteSwitch(zoom, out on);
        RECT r;
        if (!sw.IsEmpty || !GetWindowRect(zoom, out r)) return sw;
        Point[] buttons = FindPanelButtons(zoom);
        if (buttons == null) return sw;   // no Participants panel, so no Host tools under it
        // Under the panel's buttons is where Host tools sit.
        Rectangle z = ZoomArea(zoom);
        var over = new Point(buttons[1].X, Math.Min(z.Bottom - 30, buttons[1].Y + 120));
        if (over.Y <= buttons[1].Y + 40 || !z.Contains(over)) return sw;
        for (int i = 0; i < 6 && sw.IsEmpty; i++)
        {
            Cursor.Position = over;
            Thread.Sleep(80);
            mouse_event(0x0800, 0, 0, unchecked((uint)-360), UIntPtr.Zero);   // wheel down
            Thread.Sleep(300);
            sw = FindUnmuteSwitch(zoom, out on);
        }
        return sw;
    }

    // Whether Zoom's blue is within 14px either side of a point on the screen.
    static bool BlueNear(Point p)
    {
        using (var b = new Bitmap(29, 5))
        {
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(p.X - 14, p.Y - 2, 0, 0, b.Size);
            int n = 0;
            for (int x = 0; x < b.Width; x++) for (int y = 0; y < b.Height; y++) { Color c = b.GetPixel(x, y); if (c.B > 190 && c.B - c.R > 120 && c.B - c.G > 60) n++; }
            return n >= 10;
        }
    }

    // The "Unmute themselves" switch: the first switch under the permissions heading, whose "Suspend
    // all" sits in a grey pill with blue words at the pane's right edge. Its centre, and whether it's
    // on (Zoom's blue, knob right) or off (dark, knob left); empty when the pane isn't open.
    static Point FindUnmuteSwitch(IntPtr zoom, out bool on)
    {
        on = false;
        RECT r;
        if (!GetWindowRect(zoom, out r)) return Point.Empty;
        var area = Rectangle.FromLTRB(r.Right - 170, r.Top + 40, r.Right - 4, r.Bottom - 4);
        using (var b = new Bitmap(area.Width, area.Height))
        {
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            Func<Color, bool> pill = c => c.R >= 44 && c.R <= 58 && Math.Abs(c.R - c.G) < 5 && Math.Abs(c.G - c.B) < 6;
            Func<Color, bool> blueWord = c => c.B > 150 && c.B - c.R > 70;
            Func<Color, bool> blue = c => c.R < 50 && c.G > 85 && c.G < 135 && c.B > 195;
            Func<Color, bool> white = c => c.R > 235 && c.G > 235 && c.B > 235;
            Func<Color, bool> knob = c => c.R >= 205 && c.R <= 240 && Math.Abs(c.R - c.B) < 14;
            Func<Color, bool> track = c => c.R >= 31 && c.R <= 40 && Math.Abs(c.R - c.G) < 4 && Math.Abs(c.G - c.B) < 5;
            int heading = -1;
            for (int y = 0; y < b.Height && heading < 0; y++)
            {
                int pills = 0, words = 0;
                for (int x = 0; x < b.Width; x++) { Color c = b.GetPixel(x, y); if (pill(c)) pills++; else if (blueWord(c)) words++; }
                if (pills >= 30 && words >= 3) heading = y;
            }
            if (heading < 0) return Point.Empty;
            for (int y = heading + 14; y < b.Height; y++)
            {
                // A switch: a run of blue then white (on), or of knob grey then track (off).
                for (int x = 0; x + 20 < b.Width; x++)
                {
                    Color c = b.GetPixel(x, y);
                    if (blue(c) && blue(b.GetPixel(x + 6, y)) && (white(b.GetPixel(x + 14, y)) || white(b.GetPixel(x + 18, y))))
                    { on = true; return new Point(area.X + x + 10, area.Y + y); }
                    if (knob(c) && knob(b.GetPixel(x + 3, y)) && track(b.GetPixel(x + 12, y)) && track(b.GetPixel(x + 18, y)))
                    { on = false; return new Point(area.X + x + 10, area.Y + y); }
                }
            }
        }
        return Point.Empty;
    }

    // Zoom's blue: the blue area in the left third of the box (a ticked tickbox, a lighter blue), and in
    // the rest (the Yes button), in screen pixels; empty where there's none.
    static void FindBlue(IntPtr box, out Rectangle left, out Rectangle right)
    {
        left = right = Rectangle.Empty;
        RECT r;
        if (!GetWindowRect(box, out r)) return;
        // Inside the box's panel only: its window has see-through edges showing what's behind.
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var area = Rectangle.FromLTRB(r.Left + w / 16, r.Top + h / 9, r.Right - w / 16, r.Bottom - h / 9);
        using (var b = new Bitmap(area.Width, area.Height))
        {
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            int l1 = int.MaxValue, t1 = int.MaxValue, r1 = -1, b1 = -1, l2 = int.MaxValue, t2 = int.MaxValue, r2 = -1, b2 = -1, n1 = 0, n2 = 0;
            // Zoom's blues (the button, a ticked box), solid over 4×4 pixels: the blue fringes text
            // smoothing leaves on letters don't count.
            Func<int, int, bool> blue = (px, py) => { Color c = b.GetPixel(px, py); return c.B > 180 && c.B - c.R > 80 && c.B - c.G > 40; };
            for (int y = 0; y + 3 < b.Height; y += 2)
                for (int x = 0; x + 3 < b.Width; x += 2)
                {
                    if (!(blue(x, y) && blue(x + 3, y) && blue(x, y + 3) && blue(x + 3, y + 3))) continue;
                    // The tickbox: left third, middle band. The buttons: bottom row, right of it.
                    if (x < b.Width / 3 && y > b.Height * 3 / 10 && y < b.Height * 7 / 10) { n1++; l1 = Math.Min(l1, x); t1 = Math.Min(t1, y); r1 = Math.Max(r1, x); b1 = Math.Max(b1, y); }
                    else if (x >= b.Width / 3 && y > b.Height * 6 / 10) { n2++; l2 = Math.Min(l2, x); t2 = Math.Min(t2, y); r2 = Math.Max(r2, x); b2 = Math.Max(b2, y); }
                }
            if (n1 >= 6) left = new Rectangle(area.X + l1, area.Y + t1, r1 - l1 + 1, b1 - t1 + 1);
            if (n2 >= 20) right = new Rectangle(area.X + l2, area.Y + t2, r2 - l2 + 1, b2 - t2 + 1);
        }
    }

    // Zoom grows its window when a panel opens, which can push the panel's buttons under the
    // taskbar: the window is moved up, or made shorter, to sit above it. (Put back afterwards.)
    static void FitZoom(IntPtr zoom)
    {
        RECT r;
        if (!GetWindowRect(zoom, out r)) return;
        Rectangle wa = Screen.FromHandle(zoom).WorkingArea;
        const int border = 7;   // Windows' invisible border, below the visible edge
        if (r.Bottom - border <= wa.Bottom && r.Top >= wa.Top) return;
        int h = Math.Min(r.Bottom - r.Top, wa.Height + border);
        int top = Math.Max(wa.Top, wa.Bottom + border - h);
        SetWindowPos(zoom, IntPtr.Zero, r.Left, top, r.Right - r.Left, h, 0x0004 | 0x0010);
        Thread.Sleep(300);
    }

    // The part of Zoom's window that can be seen and clicked: inside the window and above the taskbar.
    static Rectangle ZoomArea(IntPtr zoom)
    {
        RECT r;
        if (!GetWindowRect(zoom, out r)) return Rectangle.Empty;
        return Rectangle.Intersect(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), Screen.FromHandle(zoom).WorkingArea);
    }

    // A click inside Zoom's window only: anywhere else (the taskbar, another program) is refused.
    static bool ClickZoom(IntPtr zoom, Point p)
    {
        Rectangle a = ZoomArea(zoom);
        a.Inflate(-2, -2);
        if (p.IsEmpty || !a.Contains(p)) return false;
        Click(p.X, p.Y);
        return true;
    }

    // Zoom only takes a click on something the mouse has been seen to arrive over, so the pointer
    // comes in from beside it, waits a moment, then presses and lets go.
    static void Click(int x, int y)
    {
        Cursor.Position = new Point(x - 12, y - 6);
        Thread.Sleep(120);
        Cursor.Position = new Point(x, y);
        Thread.Sleep(250);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);   // left down
        Thread.Sleep(80);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);   // left up
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    // ---- Zoom's Participants panel: popped out, raised hands, the waiting room -------------------
    // The panel can sit in Zoom's meeting window or in a window of its own ("Participants (3)"); its
    // buttons (Invite, Mute all, More) are found the same way in either. Read by colour and clicked
    // with the mouse, which is put back after, like the mute: Zoom shows none of it to Windows.

    const string ZoomPopupClass = "ZPlistPopupContainerWndClass";

    static string ClassOf(IntPtr h)
    {
        var c = new StringBuilder(64);
        GetClassName(h, c, 64);
        return c.ToString();
    }

    // The panel in a window of its own on this PC's screen, or none.
    static IntPtr ZoomPopup()
    {
        Screen tv = JwTargetScreen();
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) =>
        {
            RECT r;
            if (IsWindowVisible(h) && !IsIconic(h) && ClassOf(h) == ZoomPopupClass && GetWindowRect(h, out r) &&
                r.Right - r.Left > 150 && r.Bottom - r.Top > 150 &&
                (tv == null || Screen.FromHandle(h).DeviceName != tv.DeviceName)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // The panel's buttons, with the window they're in (host), Zoom being in front: the panel opened
    // with Alt+U if it's closed. Null if it can't be found. A popped-out panel that can't be read is
    // left alone - Alt+U then would close it.
    static Point[] OpenZoomPanel(IntPtr zoom, out IntPtr host)
    {
        host = ZoomPopup();
        if (host != IntPtr.Zero)
        {
            BringToFront(host);
            Thread.Sleep(250);
            return FindPanelButtons(host);
        }
        host = zoom;
        FitZoom(zoom);
        Point[] buttons = FindPanelButtons(zoom);
        if (buttons != null) return buttons;
        keybd_event(0x12, 0, 0, UIntPtr.Zero);     // Alt+U: Participants
        keybd_event(0x55, 0, 0, UIntPtr.Zero);
        keybd_event(0x55, 0, 2, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        for (int i = 0; i < 10 && buttons == null; i++)
        {
            Thread.Sleep(200);
            IntPtr popup = ZoomPopup();
            if (popup != IntPtr.Zero) { host = popup; BringToFront(popup); Thread.Sleep(250); }
            else FitZoom(zoom);
            buttons = FindPanelButtons(host);
        }
        return buttons;
    }

    // Whether anyone has a hand up: the panel shows a yellow ✋ and a count above Invite.
    static bool HandsUp(Point[] buttons)
    {
        int half = (buttons[1].X - buttons[0].X) / 2;
        var area = new Rectangle(buttons[0].X - half, buttons[1].Y - 60, half + 10, 42);
        using (var b = new Bitmap(area.Width, area.Height))
        {
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            int n = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++) { Color c = b.GetPixel(x, y); if (c.R > 230 && c.G > 150 && c.G < 225 && c.B < 110) n++; }
            return n >= 12;
        }
    }

    // ---- Everyone's hands lowered --------------------------------------------------------------
    // In the panel's More menu, its first group is "Ask all to unmute" and, while anyone has a hand
    // up or a reaction, "Clear all feedback". The menu is a window of its own - a box of the menu's
    // grey with lines between its groups - and the second line
    // of words in its first group is clicked, only if that group has two. In any language.
    static string LowerZoomHands() { return OneAtATime(() => KeepingWindows(() => TwiceIfNeeded(LowerZoomHandsHere, "lowered"))); }

    static string LowerZoomHandsHere()
    {
        IntPtr zoom = ZoomMeetingHere();
        if (zoom == IntPtr.Zero) return "no Zoom meeting open";
        IntPtr was = GetForegroundWindow();
        Point mouseAt = Cursor.Position;
        try
        {
            BringToFront(zoom);
            for (int i = 0; i < 10 && GetForegroundWindow() != zoom; i++) Thread.Sleep(50);
            if (GetForegroundWindow() != zoom) return "couldn't bring Zoom forward";
            IntPtr host;
            Point[] buttons = OpenZoomPanel(zoom, out host);
            if (buttons == null) return "couldn't find Zoom's Participants panel";
            if (!HandsUp(buttons)) return "no hands up";
            if (!ClickZoom(host, buttons[2])) return "couldn't click Zoom's More";
            Cursor.Position = mouseAt;   // not over the menu, which would light up a line
            Thread.Sleep(900);
            Rectangle look; int[] after;
            Rectangle menu = FindZoomMenu(out look, out after);
            List<int> lines = menu.IsEmpty ? null : MenuLines(look, after, menu, true);
            if (lines == null || lines.Count != 2)
            {
                keybd_event(0x1B, 0, 0, UIntPtr.Zero); keybd_event(0x1B, 0, 2, UIntPtr.Zero);
                return menu.IsEmpty ? "couldn't find Zoom's More menu" : "couldn't find Clear all feedback in Zoom's menu";
            }
            Click(menu.Left + menu.Width / 4, lines[1]);
            Cursor.Position = mouseAt;
            Thread.Sleep(700);
            return HandsUp(buttons) ? "couldn't lower them" : "lowered";
        }
        finally
        {
            Cursor.Position = mouseAt;
            if (was != IntPtr.Zero && was != zoom) BringToFront(was);
        }
    }

    // Zoom's menus are windows of their own ("WCN_ModelessWnd"), always on top, a little bigger than
    // the menu (its shadow). The menu inside: from the middle of the window out to its 1-pixel
    // border (49,50,53) at each side, then up and down a column a little inside the left border
    // (clear of the words) over the menu's grey (34,35,37) and its separator lines - the same grey
    // as the border, but with the menu's grey past them, where past the border is the shadow. What's
    // behind the menu, even the same grey, isn't taken for part of it. Empty if no menu is open; px
    // is the window's part of the screen.
    static Rectangle FindZoomMenu(out Rectangle area, out int[] px)
    {
        area = Rectangle.Empty; px = null;
        IntPtr menu = IntPtr.Zero;
        var zoom = new HashSet<uint>();
        foreach (Process p in Process.GetProcessesByName("Zoom")) zoom.Add((uint)p.Id);
        EnumWindows((win, l) =>
        {
            uint pid;
            GetWindowThreadProcessId(win, out pid);
            if (zoom.Contains(pid) && IsWindowVisible(win) && ClassOf(win) == "WCN_ModelessWnd") { menu = win; return false; }
            return true;
        }, IntPtr.Zero);
        RECT r;
        if (menu == IntPtr.Zero || !GetWindowRect(menu, out r) || r.Right - r.Left < 100 || r.Bottom - r.Top < 40) return Rectangle.Empty;
        area = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        px = Grab(area);
        int w = area.Width, h = area.Height, midY = h / 2;
        int[] p_ = px;
        Func<int, int, bool> border = (x, y) => Near(p_[y * w + x], 49, 50, 53, 4);
        Func<int, int, bool> inside = (x, y) => Near(p_[y * w + x], 34, 35, 37, 4) || border(x, y);
        int left = -1, right = -1;
        for (int x = 0; x < w / 2 && left < 0; x++) if (border(x, midY) && inside(x + 2, midY)) left = x;
        for (int x = w - 1; x > w / 2 && right < 0; x--) if (border(x, midY) && inside(x - 2, midY)) right = x;
        if (left < 0 || right < 0 || right - left < 100) return Rectangle.Empty;
        int col = left + 11, top = midY, bottom = midY;
        Func<int, bool> grey = y => y >= 0 && y < h && Near(p_[y * w + col], 34, 35, 37, 1);
        Func<int, bool> line = y => y >= 0 && y < h && Near(p_[y * w + col], 49, 50, 53, 2);
        if (!grey(midY)) return Rectangle.Empty;
        while (true)
        {
            if (grey(top - 1)) top--;
            else if (line(top - 1) && (grey(top - 2) || line(top - 2) && grey(top - 3))) top -= line(top - 2) ? 3 : 2;
            else break;
        }
        while (true)
        {
            if (grey(bottom + 1)) bottom++;
            else if (line(bottom + 1) && (grey(bottom + 2) || line(bottom + 2) && grey(bottom + 3))) bottom += line(bottom + 2) ? 3 : 2;
            else break;
        }
        if (bottom - top < 30) return Rectangle.Empty;
        return Rectangle.FromLTRB(area.X + left + 1, area.Y + top + 1, area.X + right, area.Y + bottom);
    }

    // A part of the screen, as one number per pixel (0xAARRGGBB), row by row.
    static int[] Grab(Rectangle area)
    {
        using (var b = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            BitmapData d = b.LockBits(new Rectangle(Point.Empty, area.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new int[area.Width * area.Height];
            for (int y = 0; y < area.Height; y++) Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), px, y * area.Width, area.Width);
            b.UnlockBits(d);
            return px;
        }
    }

    static bool Near(int px, int r, int g, int b, int by)
    {
        return Math.Abs(((px >> 16) & 255) - r) <= by && Math.Abs(((px >> 8) & 255) - g) <= by && Math.Abs((px & 255) - b) <= by;
    }

    // The middles (screen y) of the lines of words in the menu, or in its first group only: above its
    // first separator (a row mostly of the lines' grey, 49,50,53).
    static List<int> MenuLines(Rectangle area, int[] px, Rectangle menu, bool firstGroup)
    {
        int w = area.Width, x0 = menu.Left - area.X + 4, x1 = menu.Right - area.X - 4;
        var lines = new List<int>();
        int start = -1, last = -100;
        for (int y = menu.Top - area.Y + 2; y < menu.Bottom - area.Y; y++)
        {
            int sep = 0, bright = 0;
            for (int x = x0; x < x1; x++)
            {
                int c = px[y * w + x];
                if (Near(c, 49, 50, 53, 3)) sep++;
                else if (((c >> 16) & 255) > 190 && ((c >> 8) & 255) > 190 && (c & 255) > 190) bright++;
            }
            if (sep * 10 >= (x1 - x0) * 7) { if (firstGroup) break; continue; }   // a separator
            if (bright < 2) continue;
            if (y - last > 3) { if (start >= 0) lines.Add(area.Y + (start + last) / 2); start = y; }
            last = y;
        }
        if (start >= 0) lines.Add(area.Y + (start + last) / 2);
        return lines;
    }

    // ---- The waiting room, person by person -----------------------------------------------------
    // Zoom tells Windows nothing, not even names, so who's waiting is read from a picture of the
    // Participants panel (taken with PrintWindow, so Zoom can be behind other windows): the waiting
    // room heads the panel's list, its heading - a band of lighter grey - with a blue link at the
    // right ("Message", "Admit all"); under it, a line per person, as tall as the heading, down to
    // the next heading. Each person's line - avatar and name - is cut out and shown in Meeting AV
    // with its own Admit. Admitting someone: their line is found again by its picture, pointed at,
    // which shows Zoom's blue Admit button on it, and that's clicked.

    class ZoomWaiter { public int Y; public long Hash; public string Png; }   // Y: the line's middle, in the window's pixels
    static volatile List<ZoomWaiter> ZoomWaiting = new List<ZoomWaiter>();

    // A picture of a whole window, even behind others; null if it can't be had.
    static Bitmap PictureOf(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r) || r.Right - r.Left < 100 || r.Bottom - r.Top < 100) return null;
        var b = new Bitmap(r.Right - r.Left, r.Bottom - r.Top, PixelFormat.Format32bppArgb);
        bool ok;
        using (Graphics g = Graphics.FromImage(b)) { IntPtr dc = g.GetHdc(); ok = PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
        if (!ok) { b.Dispose(); return null; }
        return b;
    }

    static int[] PixelsOf(Bitmap b)
    {
        BitmapData d = b.LockBits(new Rectangle(Point.Empty, b.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new int[b.Width * b.Height];
        for (int y = 0; y < b.Height; y++) Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), px, y * b.Width, b.Width);
        b.UnlockBits(d);
        return px;
    }

    // The panel's list in a picture of the window it's in: its left and right, from its top to just
    // above its buttons. Empty if the panel isn't there.
    static Rectangle ZoomListIn(Bitmap b, bool popup)
    {
        var area = popup ? new Rectangle(0, 30, b.Width, b.Height - 30) : Rectangle.FromLTRB(b.Width / 2, 60, b.Width, b.Height);
        Point[] buttons;
        using (Bitmap part = b.Clone(area, b.PixelFormat)) buttons = PanelButtonsIn(part, area.Location);
        if (buttons == null) return Rectangle.Empty;
        int left = popup ? 0 : Math.Max(0, buttons[0].X - (buttons[1].X - buttons[0].X) / 2 - 8);
        var list = Rectangle.FromLTRB(left, area.Top, b.Width, buttons[1].Y - 50);
        return list.Width < 150 || list.Height < 60 ? Rectangle.Empty : list;
    }

    // Who's waiting, in a picture of the window the panel is in (popup: the panel's own window).
    static List<ZoomWaiter> ZoomWaitingIn(Bitmap b, bool popup)
    {
        var found = new List<ZoomWaiter>();
        Rectangle list = ZoomListIn(b, popup);
        if (list.IsEmpty) return found;
        int[] px = PixelsOf(b);
        int w = b.Width;
        Func<int, int, bool> blueWord = (x, y) => { int c = px[y * w + x]; return (c & 255) > 150 && (c & 255) - ((c >> 16) & 255) > 70; };
        Func<int, int, bool> white = (x, y) => { int c = px[y * w + x]; return ((c >> 16) & 255) > 190 && ((c >> 8) & 255) > 190 && (c & 255) > 190; };
        int split = list.Left + list.Width * 6 / 10;
        Func<int, bool> words = y => { int n = 0; for (int x = list.Left; x < split; x++) if (white(x, y)) n++; return n >= 2; };
        Func<int, int, bool> blueIn = (y0, y1) =>
        {
            int n = 0;
            for (int y = y0; y < y1; y++) for (int x = split; x < list.Right; x++) if (blueWord(x, y)) n++;
            return n >= 3;
        };
        // The headings: bands of a lighter grey (36,36,36) across the list - looked at around its middle,
        // clear of the heading's words (at the left) and its link (at the right), and of the panel's
        // edges (a docked panel has a border and a margin inside it).
        int midA = list.Left + list.Width / 2, midB = list.Left + list.Width * 58 / 100;
        Func<int, bool> band = y => Near(px[y * w + midA], 36, 36, 36, 3) && Near(px[y * w + midB], 36, 36, 36, 3);
        var bands = new List<int[]>();
        for (int y = list.Top, start = -1; y <= list.Bottom; y++)
        {
            bool on = y < list.Bottom && band(y);
            if (on && start < 0) start = y;
            else if (!on && start >= 0) { if (y - start >= 15) bands.Add(new[] { start, y }); start = -1; }
        }
        // The waiting room's heading has its blue link; its people are the lines of the same height
        // under it, down to the next heading.
        int waitingAt = bands.FindIndex(bn => blueIn(bn[0], bn[1]));
        if (waitingAt < 0) return found;
        int rowH = bands[waitingAt][1] - bands[waitingAt][0];
        int end = waitingAt + 1 < bands.Count ? bands[waitingAt + 1][0] : list.Bottom;
        for (int y = bands[waitingAt][1]; y + rowH <= end + 2; y += rowH)
        {
            int mid = y + rowH / 2;
            bool named = false;
            for (int yy = y; yy < Math.Min(end, y + rowH) && !named; yy++) named = words(yy);
            if (!named) break;
            // The avatar and name: the left half of the line (Admit appears on the right when pointed at).
            var cut = Rectangle.Intersect(new Rectangle(list.Left + 6, mid - 17, list.Width * 52 / 100, 34), list);
            long hash = 1469598103934665603;
            for (int yy = cut.Top; yy < cut.Bottom; yy++)
                for (int x = cut.Left; x < cut.Right; x++) hash = (hash ^ (px[yy * w + x] & 0xFFFFFF)) * 1099511628211;
            string png;
            using (Bitmap c = b.Clone(cut, b.PixelFormat))
            using (var m = new MemoryStream()) { c.Save(m, ImageFormat.Png); png = Convert.ToBase64String(m.ToArray()); }
            found.Add(new ZoomWaiter { Y = mid, Hash = hash, Png = png });
        }
        return found;
    }

    // Who's waiting now: read from the panel when it's open (popped out, or in the meeting's window).
    static List<ZoomWaiter> ReadZoomWaiting()
    {
        IntPtr zoom = ZoomMeetingHere();
        if (zoom == IntPtr.Zero) return new List<ZoomWaiter>();
        IntPtr popup = ZoomPopup(), host = popup != IntPtr.Zero ? popup : zoom;
        using (Bitmap b = PictureOf(host))
            return b == null ? new List<ZoomWaiter>() : ZoomWaitingIn(b, popup != IntPtr.Zero);
    }

    // Someone let in: the one whose line looks like this (hash), or with 0 the first waiting.
    static string AdmitZoom(long hash) { return OneAtATime(() => KeepingWindows(() => AdmitZoomHere(hash))); }

    static string AdmitZoomHere(long hash)
    {
        IntPtr zoom = ZoomMeetingHere();
        if (zoom == IntPtr.Zero) return "no Zoom meeting open";
        IntPtr was = GetForegroundWindow();
        Point mouseAt = Cursor.Position;
        try
        {
            BringToFront(zoom);
            for (int i = 0; i < 10 && GetForegroundWindow() != zoom; i++) Thread.Sleep(50);
            if (GetForegroundWindow() != zoom) return "couldn't bring Zoom forward";
            IntPtr host;
            Point[] buttons = OpenZoomPanel(zoom, out host);
            if (buttons == null) return "couldn't find Zoom's Participants panel";
            bool popup = host != zoom;
            RECT r;
            if (!GetWindowRect(host, out r)) return "couldn't find Zoom's Participants panel";
            List<ZoomWaiter> waiting;
            Rectangle list;
            using (Bitmap b = PictureOf(host))
            {
                if (b == null) return "couldn't see Zoom's Participants panel";
                waiting = ZoomWaitingIn(b, popup);
                list = ZoomListIn(b, popup);
            }
            if (waiting.Count == 0) return "nobody waiting";
            ZoomWaiter who = hash == 0 ? waiting[0] : waiting.Find(x => x.Hash == hash);
            if (who == null) return "they're no longer waiting";
            // Pointed at, then its blue button looked for along that line.
            int nameY = r.Top + who.Y;
            var at = new Point(r.Left + list.Left + list.Width / 3, nameY);
            Cursor.Position = new Point(at.X - 12, at.Y - 6);
            Thread.Sleep(120);
            Cursor.Position = at;
            Thread.Sleep(500);
            var band = new Rectangle(r.Left + list.Left, nameY - 10, list.Width, 21);
            int[] row = Grab(band);
            int bl = int.MaxValue, br = -1, count = 0;
            for (int y = 0; y < band.Height; y++)
                for (int x = 0; x < band.Width; x++)
                {
                    int c = row[y * band.Width + x], cr = (c >> 16) & 255, cg = (c >> 8) & 255, cb = c & 255;
                    if (cr < 50 && cg > 85 && cg < 135 && cb > 195) { count++; bl = Math.Min(bl, x); br = Math.Max(br, x); }
                }
            if (count < 150 || br - bl < 30) return "couldn't find Zoom's Admit button";
            if (!ClickZoom(host, new Point(band.X + (bl + br) / 2, nameY))) return "couldn't click Zoom's Admit button";
            Thread.Sleep(600);
            return "admitted";
        }
        finally
        {
            Cursor.Position = mouseAt;
            if (was != IntPtr.Zero && was != zoom) BringToFront(was);
        }
    }

    static int CountWhere(int n, Func<int, bool> f) { int k = 0; for (int i = 0; i < n; i++) if (f(i)) k++; return k; }

    // ---- Zoom: the camera on in the meeting at start-up ------------------------------------------
    // Whatever Zoom's "Turn off my video when joining" says, with Zoom in English or French. The video
    // button in Zoom's toolbar tells Windows' accessibility interface what it would do - "Start my
    // video, Alt+V" / "Démarrer ma vidéo" while the camera is off, "Stop my video" / "Arrêter ma
    // vidéo" while it's on - and is pressed through it, so Zoom isn't made active (see ArrangeWindows).
    // But Zoom hides its toolbar while the mouse is still, and then shows none of it: the state is read
    // from this PC's own video tile instead ("James(Host, me), ..., Video on" / "(Hôte, moi), ...,
    // Vidéo activée") and the camera switched on with Zoom's own key, Alt+V, Zoom brought in front for
    // it and the window that was in front put back after. Only ever to start the camera, never to stop it.

    // Zoom's meeting window on this PC's screen, never its window on the hall's TV.
    static IntPtr ZoomMeetingHere()
    {
        Screen tv = JwTargetScreen();
        IntPtr zoom = IntPtr.Zero;
        EnumWindows((h, p) =>
        {
            var c = new StringBuilder(64);
            GetClassName(h, c, 64);
            if (c.ToString() == "ConfMultiTabContentWndClass" && IsWindowVisible(h) &&
                (tv == null || Screen.FromHandle(h).DeviceName != tv.DeviceName)) { zoom = h; return false; }
            return true;
        }, IntPtr.Zero);
        return zoom;
    }

    // The video button in a window of Zoom's (its meeting, or the video preview before joining), with
    // what it says: "on", "off", or null if it isn't there or its language isn't known.
    static AutomationElement ZoomVideoButton(AutomationElement window, out string state)
    {
        state = null;
        foreach (AutomationElement b in window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        {
            string n = b.Current.Name.Trim().ToLowerInvariant();
            if (!n.Contains("alt+v") && !n.StartsWith("start video") && !n.StartsWith("stop video")) continue;
            state = StartsWithAny(n, "stop", "arrêter", "arreter") ? "on" : StartsWithAny(n, "start", "démarrer", "demarrer") ? "off" : null;
            if (state != null) return b;
        }
        return null;
    }

    // The camera: from the toolbar's button if it's showing (button set), or else this PC's video tile.
    static string ZoomCameraState(IntPtr zoom, out AutomationElement button)
    {
        AutomationElement w = AutomationElement.FromHandle(zoom);
        string state;
        button = ZoomVideoButton(w, out state);
        if (state != null) return state;
        foreach (AutomationElement t in w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "VideoRenderWndClass")))
        {
            string n = t.Current.Name.ToLowerInvariant();
            if (!Regex.IsMatch(n, @"\((?:[^()]*,\s*)?(me|moi)\)")) continue;
            return Regex.IsMatch(n, "vidéo activée|video on") ? "on" : Regex.IsMatch(n, "vidéo désactivée|video off") ? "off" : null;
        }
        return null;
    }

    static bool StartsWithAny(string s, params string[] starts)
    {
        foreach (string p in starts) if (s.StartsWith(p)) return true;
        return false;
    }

    static string ZoomCameraOn()
    {
        IntPtr zoom = ZoomMeetingHere();
        if (zoom == IntPtr.Zero) return "couldn't find Zoom's meeting";
        // Zoom shows the camera's state once it has connected, which can take a while.
        for (int i = 0; i < 20; i++)
        {
            AutomationElement button;
            string state = ZoomCameraState(zoom, out button);
            if (state == "on") return i == 0 ? "already on" : "on";
            if (state == "off")
            {
                object pattern;
                if (button != null && button.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) ((InvokePattern)pattern).Invoke();
                else
                {
                    // The toolbar hidden: Zoom's key for it, pressed only with Zoom really in front.
                    IntPtr was = GetForegroundWindow();
                    BringToFront(zoom);
                    for (int j = 0; j < 10 && GetForegroundWindow() != zoom; j++) Thread.Sleep(50);
                    if (GetForegroundWindow() != zoom) return "couldn't bring Zoom forward";
                    keybd_event(0x12, 0, 0, UIntPtr.Zero);   // Alt+V
                    keybd_event(0x56, 0, 0, UIntPtr.Zero);
                    keybd_event(0x56, 0, 2, UIntPtr.Zero);
                    keybd_event(0x12, 0, 2, UIntPtr.Zero);
                    Thread.Sleep(300);
                    if (was != IntPtr.Zero && was != zoom) BringToFront(was);
                }
                for (int j = 0; j < 10; j++)
                {
                    Thread.Sleep(300);
                    if (ZoomCameraState(zoom, out button) == "on") return "on";
                }
                return "couldn't switch it on";   // pressed once only: a second press could turn it off
            }
            Thread.Sleep(1000);
        }
        // What Zoom does show, for finding the button in a Zoom that names it differently.
        try
        {
            var names = new List<string>();
            foreach (AutomationElement e in AutomationElement.FromHandle(zoom).FindAll(TreeScope.Descendants, Condition.TrueCondition))
                if (e.Current.Name != "") names.Add(e.Current.ControlType.ProgrammaticName + " | " + e.Current.Name + " | " + e.Current.ClassName);
            File.WriteAllLines(Path.Combine(DataDir, "zoom-buttons.txt"), names.ToArray());
        }
        catch { }
        return "couldn't find Zoom's video button";
    }

    // With "Always show video preview" ticked in Zoom, joining waits in a preview of the camera: its
    // camera is switched on there, then Join pressed ("Join with Video" in older Zoom; "Rejoindre").
    static void JoinFromPreview()
    {
        var zoomPids = new HashSet<uint>();
        foreach (Process p in Process.GetProcessesByName("Zoom")) zoomPids.Add((uint)p.Id);
        if (zoomPids.Count == 0) return;
        var windows = new List<IntPtr>();
        EnumWindows((h, p) =>
        {
            uint pid;
            var c = new StringBuilder(64);
            GetClassName(h, c, 64);
            string cls = c.ToString();
            if (IsWindowVisible(h) && GetWindowThreadProcessId(h, out pid) != 0 && zoomPids.Contains(pid) &&
                cls != "ConfMultiTabContentWndClass" && cls != "ZPPTMainFrmWndClassEx") windows.Add(h);
            return true;
        }, IntPtr.Zero);
        foreach (IntPtr h in windows)
        {
            AutomationElement w = AutomationElement.FromHandle(h);
            AutomationElement join = null;
            foreach (AutomationElement b in w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                string n = b.Current.Name.Trim().ToLowerInvariant();
                if (n == "join with video" || n == "rejoindre avec la vidéo") { join = b; break; }
                if (n == "join" || n == "rejoindre") join = b;
            }
            if (join == null) continue;
            string camState;
            AutomationElement video = ZoomVideoButton(w, out camState);
            if (video != null && camState == "off") { Press(video); Thread.Sleep(800); }
            Press(join);
            return;
        }
    }

    // ---- The hall's TV: JW Library, or Zoom's second window --------------------------------------
    // Zoom in "dual monitors" mode keeps its second window (the gallery) on the screen JW Library
    // uses, under JW Library's own. Showing Zoom there switches JW Library's second display off and
    // brings that window up, full screen; showing JW Library switches its display back on.

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
            ZoomFullScreen(z, tv);
            TvShowsZoom = true;
            return true;
        }
        if (JwRunning() && !JwSetSecondDisplay(true, false)) return false;
        TvShowsZoom = false;
        return true;
    }

    // Zoom's window on the TV filling it, without its title bar and toolbar: Zoom's own full screen
    // (Alt+F), with the window in front for the key (JW Library's display is off by then, so a window
    // on that screen being active doesn't matter), and the window that was in front put back. If
    // Zoom doesn't go full screen, the window is stretched over the whole TV instead.
    static void ZoomFullScreen(IntPtr z, Screen tv)
    {
        Func<bool> full = () =>
        {
            RECT r;
            return GetWindowRect(z, out r) && r.Left <= tv.Bounds.Left && r.Top <= tv.Bounds.Top &&
                   r.Right >= tv.Bounds.Right && r.Bottom >= tv.Bounds.Bottom;
        };
        if (full()) return;
        IntPtr was = GetForegroundWindow();
        BringToFront(z);
        for (int i = 0; i < 10 && GetForegroundWindow() != z; i++) Thread.Sleep(50);
        if (GetForegroundWindow() == z)
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero);     // Alt+F: Zoom's full screen
            keybd_event(0x46, 0, 0, UIntPtr.Zero);
            keybd_event(0x46, 0, 2, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            for (int i = 0; i < 15 && !full(); i++) Thread.Sleep(100);
        }
        if (!full())
        {
            if (IsZoomed(z)) ShowWindow(z, 9);
            SetWindowPos(z, IntPtr.Zero, tv.Bounds.X, tv.Bounds.Y, tv.Bounds.Width, tv.Bounds.Height, 0x0010);
        }
        if (was != IntPtr.Zero && was != z) BringToFront(was);
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
        // Running but its window not found: it's changing page (or still starting), so it's looked for again.
        for (int i = 0; i < 5 && jw == IntPtr.Zero; i++)
        {
            try { lock (JwLock) { AutomationElement w = JwMainWindowRestored(); if (w != null) jw = new IntPtr(w.Current.NativeWindowHandle); } } catch { }
            if (jw == IntPtr.Zero) { if (!JwRunning()) break; Thread.Sleep(1000); }
        }
        // Zoom's meeting window on this screen - in "dual monitors" mode it has a second one on the
        // hall's TV, which is left alone - or else Zoom's home window.
        IntPtr zoom = FindTopWindow("ConfMultiTabContentWndClass", screen);
        if (zoom == IntPtr.Zero) zoom = FindTopWindow("ZPPTMainFrmWndClassEx", screen);
        // With a meeting open, Zoom's home window (which it opens on starting) is only in the way.
        else
            EnumWindows((h, p) =>
            {
                var c = new StringBuilder(64);
                GetClassName(h, c, 64);
                if (c.ToString() == "ZPPTMainFrmWndClassEx" && IsWindowVisible(h) && !IsIconic(h)) ShowWindow(h, 7);   // minimised, not made active
                return true;
            }, IntPtr.Zero);
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
        if (zoom != IntPtr.Zero)
        {
            PlaceWindow(zoom, jw != IntPtr.Zero ? rightBottom : Rectangle.Union(rightTop, rightBottom));
            // Windows ignores "to the front" for a program that isn't active: on top of everything
            // and straight back lifts it over the other windows without making it active.
            SetWindowPos(zoom, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // topmost; no size, move or activate
            SetWindowPos(zoom, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // not topmost, staying in front
        }
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
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);

    static volatile bool JwStarting;   // JW Library just opened, on its way to the meeting's media

    // JW Library opened while Meeting AV is running goes straight to the meeting's media.
    static void WatchJwStart()
    {
        bool wasRunning = JwRunning();
        bool zoomWasOnTv = false;
        DateTime zoomLeftAt = DateTime.MinValue, jwRefittedAt = DateTime.MinValue;
        int jwInWindow = 0;
        // Left switched off by a Meeting AV that stopped mid-countdown: switched back on.
        if (File.Exists(JwOffFile)) { JwDisplayOff = true; if (!wasRunning) { try { File.Delete(JwOffFile); } catch { } JwDisplayOff = false; } }
        while (true)
        {
            Thread.Sleep(2000);
            bool running = wasRunning;
            // JW Library or Zoom closing a window just as it's looked at: tried again next time round.
            try
            {
                running = JwRunning();
                if (running && !wasRunning)
                {
                    JwStarting = true;
                    try
                    {
                        for (int i = 0; i < 30 && JwMainWindow() == null; i++) Thread.Sleep(1000);
                        Thread.Sleep(3000);   // its first page settles
                        // Still busy starting (checking for new publications, opening its second display),
                        // it can miss the first try.
                        for (int i = 0; i < 3 && OneAtATime(() => JwOpenMeetings() ? "ok" : "") != "ok"; i++) Thread.Sleep(3000);
                    }
                    finally { JwStarting = false; }
                }
                if (!running && JwDisplayOff && !JwBusy) { JwDisplayOff = false; try { File.Delete(JwOffFile); } catch { } }
                // Zoom's window on the hall TV closing (the meeting ended, or Zoom quit) takes JW Library's
                // picture there away with it, leaving the desktop: JW Library's second display is switched
                // off and on a few seconds later, which brings it back.
                Screen tvScreen = JwTargetScreen();
                // JW Library opened its second display while the TV wasn't connected (or was asleep), so
                // it's a window on the PC's screen and the TV shows the desktop. With a TV, JW Library fills
                // it instead of opening a window: switched off and on, it goes there. Not more than once a
                // minute, in case it keeps the window.
                bool quiet = !JwDisplayOff && !JwBusy && !JwStarting && JwNavigating == 0 && !TvShowsZoom && (CountdownForm == null || CountdownForm.IsDisposed);
                jwInWindow = running && tvScreen != null && quiet && FindJwSecondDisplay() != IntPtr.Zero ? jwInWindow + 1 : 0;
                if (jwInWindow >= 2 && DateTime.Now - jwRefittedAt > TimeSpan.FromMinutes(1))
                {
                    jwRefittedAt = DateTime.Now;
                    jwInWindow = 0;
                    if (JwSetSecondDisplay(false, false)) { Thread.Sleep(500); JwSetSecondDisplay(true, false); }
                }
                bool zoomOnTv = tvScreen != null && FindTopWindow("ConfMultiTabContentWndClass", tvScreen) != IntPtr.Zero;
                if (zoomWasOnTv && !zoomOnTv) zoomLeftAt = DateTime.Now;
                zoomWasOnTv = zoomOnTv;
                if (zoomLeftAt != DateTime.MinValue && DateTime.Now - zoomLeftAt > TimeSpan.FromSeconds(3))
                {
                    if (running && !JwDisplayOff && !JwBusy && JwNavigating == 0 && (CountdownForm == null || CountdownForm.IsDisposed))
                    {
                        TvShowsZoom = false;
                        if (JwSetSecondDisplay(false, false)) { Thread.Sleep(500); JwSetSecondDisplay(true, false); }
                    }
                    zoomLeftAt = DateTime.MinValue;
                }
            }
            catch (Exception e) { Log("JW Library start: " + e.Message); }
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
                // Which they are: set to VB-CABLE by mistake, nothing comes out of the hall's speakers.
                string name = "";
                try { string id; speakers.GetId(out id); string[] named = AudioDevices(0).Find(d => d[0] == id); if (named != null) name = named[1]; }
                catch { }
                json.Append(",\"speakersName\":" + Json(name));
            }
            catch { json.Append("null"); }   // no speakers
            // The microphone: Windows' default recording device (what Zoom uses unless told otherwise).
            json.Append(",\"mic\":");
            try
            {
                IAudioEndpointVolume volume; IAudioMeterInformation meter;
                Microphone(out volume, out meter);
                float level, peak; bool mute;
                volume.GetMasterVolumeLevelScalar(out level); volume.GetMute(out mute); meter.GetPeakValue(out peak);
                json.Append(AudioLevel(level, mute, peak));
            }
            catch { json.Append("null"); }   // no microphone
            json.Append(",\"micHold\":" + (MicHold >= 0 ? MicHold.ToString() : "null"));
            float tz = ToZoomPeak();
            json.Append(",\"toZoom\":" + (tz < 0 ? "null" : tz.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
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
                if (target == "mic")
                {
                    IAudioEndpointVolume v; IAudioMeterInformation m;
                    Microphone(out v, out m);
                    if (volume.HasValue)
                    {
                        v.SetMasterVolumeLevelScalar(volume.Value / 100f, ref none);
                        if (MicHold >= 0) SaveMicHold(volume.Value);   // held at the new level
                    }
                    if (mute.HasValue) v.SetMute(mute.Value, ref none);
                }
                else if (target == "system")
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

    // ---- The microphone held steady ---------------------------------------------------------------
    // Zoom (and Windows) can turn the microphone up and down by themselves. Held, Meeting AV puts it
    // back to the chosen level within half a second, whatever changes it. The level is kept in a file,
    // so it stays held after a restart.
    static int MicHold = ReadMicHold();   // 0-100, or -1 when not held
    static string MicHoldFile { get { return Path.Combine(DataDir, "mic-hold.txt"); } }

    static int ReadMicHold()
    {
        int v;
        try { if (File.Exists(MicHoldFile) && int.TryParse(File.ReadAllText(MicHoldFile).Trim(), out v) && v >= 0 && v <= 100) return v; } catch { }
        return -1;
    }

    static void SaveMicHold(int level)
    {
        MicHold = level;
        try { if (level < 0) File.Delete(MicHoldFile); else File.WriteAllText(MicHoldFile, level.ToString()); } catch { }
    }

    static void Microphone(out IAudioEndpointVolume volume, out IAudioMeterInformation meter)
    {
        IMMDevice mic;
        Enumerator().GetDefaultAudioEndpoint(1, 0, out mic);   // eCapture, eConsole
        volume = (IAudioEndpointVolume)Activate(mic, typeof(IAudioEndpointVolume).GUID);
        meter = (IAudioMeterInformation)Activate(mic, typeof(IAudioMeterInformation).GUID);
    }

    static void HoldMicSteady()
    {
        Guid none = Guid.Empty;
        while (true)
        {
            Thread.Sleep(500);
            if (MicHold < 0) continue;
            lock (AudioLock)
            {
                try
                {
                    IAudioEndpointVolume v; IAudioMeterInformation m;
                    Microphone(out v, out m);
                    float now;
                    v.GetMasterVolumeLevelScalar(out now);
                    if (Math.Abs(now * 100 - MicHold) >= 1) v.SetMasterVolumeLevelScalar(MicHold / 100f, ref none);
                }
                catch { }
            }
        }
    }

    // ---- Sound devices, and the sound going to Zoom ------------------------------------------------
    // Zoom's sound can come levelled from OBS through VB-CABLE, a free virtual cable: OBS plays it
    // into "CABLE Input" and Zoom's microphone is "CABLE Output".

    static List<string[]> AudioDevices(int flow)   // [id, name] of each working device; 0 = outputs, 1 = inputs
    {
        var list = new List<string[]>();
        lock (AudioLock)
        {
            try
            {
                IMMDeviceCollection all;
                Enumerator().EnumAudioEndpoints(flow, 1, out all);
                int n; all.GetCount(out n);
                var nameKey = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };   // friendly name
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        IMMDevice d; all.Item(i, out d);
                        string id; d.GetId(out id);
                        IPropertyStore store; d.OpenPropertyStore(0, out store);
                        PropVariant v; store.GetValue(ref nameKey, out v);
                        list.Add(new[] { id, v.vt == 31 ? Marshal.PtrToStringUni(v.p) : id });
                    }
                    catch { }
                }
            }
            catch { }
        }
        return list;
    }

    static string[] CableDevice(int flow)   // VB-CABLE's input (an output for Windows) or output (an input)
    {
        foreach (string[] d in AudioDevices(flow))
            if (d[1].IndexOf(flow == 0 ? "CABLE Input" : "CABLE Output", StringComparison.OrdinalIgnoreCase) >= 0) return d;
        return null;
    }

    static string AudioDevicesJson()
    {
        var sb = new StringBuilder("{\"inputs\":[");
        bool first = true;
        foreach (string[] d in AudioDevices(1))
        {
            if (d[1].IndexOf("CABLE Output", StringComparison.OrdinalIgnoreCase) >= 0) continue;   // that's Zoom's side
            sb.Append((first ? "" : ",") + "{\"id\":" + Json(d[0]) + ",\"name\":" + Json(d[1]) + "}");
            first = false;
        }
        string[] cin = CableDevice(0), cout = CableDevice(1);
        sb.Append("],\"cableIn\":" + (cin == null ? "null" : "{\"id\":" + Json(cin[0]) + ",\"name\":" + Json(cin[1]) + "}"));
        sb.Append(",\"cableOut\":" + (cout == null ? "null" : Json(cout[1])) + "}");
        return sb.ToString();
    }

    // How loud the sound arriving at Zoom is (VB-CABLE's output), 0-1; -1 without VB-CABLE.
    static float ToZoomPeak()
    {
        string[] cout = CableDevice(1);
        if (cout == null) return -1;
        lock (AudioLock)
        {
            try
            {
                IMMDeviceCollection all;
                Enumerator().EnumAudioEndpoints(1, 1, out all);
                int n; all.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IMMDevice d; all.Item(i, out d);
                    string id; d.GetId(out id);
                    if (id != cout[0]) continue;
                    var meter = (IAudioMeterInformation)Activate(d, typeof(IAudioMeterInformation).GUID);
                    float p; meter.GetPeakValue(out p);
                    return p;
                }
            }
            catch { }
        }
        return -1;
    }

    // OBS writes the monitoring device it opened into its log when it starts ("Audio monitoring
    // device:" then "name: ..."): the newest log says where monitored sound really goes.
    static string ObsMonitoringDevice()
    {
        try
        {
            var logs = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"obs-studio\logs"));
            FileInfo newest = null;
            foreach (FileInfo f in logs.GetFiles("*.txt")) if (newest == null || f.LastWriteTime > newest.LastWriteTime) newest = f;
            if (newest == null) return "";
            string found = "";
            using (var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs))
            {
                bool next = false;
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.Contains("Audio monitoring device:")) { next = true; continue; }
                    if (next) { int i = line.IndexOf("name:"); if (i >= 0) found = line.Substring(i + 5).Trim(); next = false; }
                }
            }
            return found;
        }
        catch { return ""; }
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
            try { if (DateTime.Now - JwStateAskedAt < TimeSpan.FromSeconds(5)) JwMedia = JwMediaOpen(); }
            catch (Exception e) { Log("JW Library media: " + e.Message); Thread.Sleep(1000); }
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

    // A video's play/pause button, the picture viewer, and the top bar every normal page has - but a
    // playlist's page has its list instead (which goes while one of its items is showing).
    static readonly Condition MediaMarkers = new OrCondition(
        new PropertyCondition(AutomationElement.AutomationIdProperty, "TogglePlaybackButton"),
        new PropertyCondition(AutomationElement.AutomationIdProperty, "ImageRenderer"),
        new PropertyCondition(AutomationElement.AutomationIdProperty, "TopCommandBar"),
        new PropertyCondition(AutomationElement.AutomationIdProperty, "List"));

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

    // ---- JW Library and Zoom, live, in Meeting AV's window ---------------------------
    // The page leaves a box for each and says where it is (in the window's pixels); Windows' own
    // thumbnails (as in the taskbar's previews) show the program's window there, moving as it does,
    // whatever is in front of it. Only to be seen: clicking a box brings that program to the front.
    // The page leaves out a box something of its own covers (a menu, a dialog, a note), as the
    // thumbnail is drawn over the page. A minimised window can't be shown: the page says so instead.
    // Each shows the part that matters: JW Library its page (not its title bar and side menu), Zoom
    // its side panel (Participants) when that's open.

    static readonly LiveView JwView = new LiveView(), ZoomView = new LiveView();
    static System.Windows.Forms.Timer ViewsTimer;
    static string ViewsState = "";

    static void OnPageMessage(string message)
    {
        if (message.StartsWith("views"))
        {
            JwView.Box = Box(message, "jw");
            ZoomView.Box = Box(message, "zoom");
            if (ViewsTimer == null)
            {
                ViewsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                ViewsTimer.Tick += (s, e) => UpdateViews();
                ViewsTimer.Start();
                Background("live views", FindViewSources);
            }
            if (message == "views?") { ViewsState = ""; UpdateViews(); }   // the page (re)loaded: told the state again
            else if (Window != null && !Window.IsDisposed && Window.IsHandleCreated)
            {
                // Just moved (the page scrolled or changed): only the pictures follow, straight away.
                JwView.Update(Window);
                ZoomView.Update(Window);
            }
        }
        else if (message == "bring jw" || message == "bring zoom")
        {
            IntPtr h = message == "bring jw" ? JwMainHandle : ZoomView.Source;
            if (h == IntPtr.Zero && message == "bring zoom") h = FindWindow("ZPPTMainFrmWndClassEx", null);   // not in a meeting: its home window
            if (h == IntPtr.Zero) return;
            Background("bringing a window", () =>
            {
                if (IsIconic(h)) ShowWindow(h, 9);   // restored
                BringToFront(h);
            });
        }
    }

    static Rectangle Box(string message, string name)
    {
        Match m = Regex.Match(message, @"\b" + name + @"=(-?\d+),(-?\d+),(\d+),(\d+)");
        if (!m.Success) return Rectangle.Empty;
        return new Rectangle(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
    }

    // Every second: the thumbnails kept up with their windows, and the page told when one closes,
    // is minimised or comes back.
    static void UpdateViews()
    {
        if (Window == null || Window.IsDisposed || !Window.IsHandleCreated) return;
        JwView.Update(Window);
        ZoomView.Update(Window);
        bool zoomRunning = Process.GetProcessesByName("Zoom").Length > 0;
        var items = new StringBuilder();
        foreach (JwItem it in JwItems)
            items.Append(items.Length == 0 ? "" : ",").Append("{\"n\":" + Json(it.Name) + ",\"t\":" + Json(it.Title) + ",\"g\":" + Json(it.Group) +
                                                             ",\"k\":" + Json(it.Kind) + ",\"l\":" + Json(it.Length) + ",\"s\":" + (it.Saved ? "true" : "false") + "}");
        string state = "{\"type\":\"views\",\"jw\":" + Json(JwView.State(JwRunning() ? "opening" : "closed")) +
                       ",\"zoom\":" + Json(ZoomView.State(zoomRunning ? "none" : "closed")) +
                       ",\"jwItems\":[" + items + "],\"jwOpen\":" + Json(JwViewer) + ",\"jwShowing\":" + Json(JwShowing) +
                       ",\"zoomPanel\":" + (ZoomPanelOpen ? "true" : "false") + ",\"jwOnTv\":" + (JwOnTv ? "true" : "false") +
                       ",\"zoomWaiting\":[" + string.Join(",", ZoomWaiting.ConvertAll(x => "{\"h\":\"" + x.Hash + "\",\"img\":\"" + x.Png + "\"}")) + "]}";
        if (state != ViewsState) { ViewsState = state; Window.Post(state); }
    }

    // Which windows to show, looked for in the background (finding JW Library's takes a moment).
    static void FindViewSources()
    {
        while (true)
        {
            try
            {
                IntPtr jw = JwMainHandle;
                if (jw == IntPtr.Zero || !IsWindow(jw))
                {
                    jw = IntPtr.Zero;
                    if (JwRunning() && Monitor.TryEnter(JwLock, 0))
                        try
                        {
                            AutomationElement w = JwMainWindow();
                            if (w != null) jw = new IntPtr(w.Current.NativeWindowHandle);
                            else if (JwMainSeen != IntPtr.Zero && IsWindow(JwMainSeen)) jw = JwMainSeen;   // minimised
                            else
                                // Minimised, its menu can't be seen: the minimised one of its windows.
                                foreach (AutomationElement named in AutomationElement.RootElement.FindAll(TreeScope.Children,
                                             new PropertyCondition(AutomationElement.NameProperty, "JW Library")))
                                    if (IsIconic(new IntPtr(named.Current.NativeWindowHandle))) jw = new IntPtr(named.Current.NativeWindowHandle);
                        }
                        finally { Monitor.Exit(JwLock); }
                    JwMainHandle = jw;
                }
                // JW Library minimised can't be shown (Windows draws nothing of a minimised window): its
                // second display - what's on the TV - is shown instead, while that's up.
                IntPtr tv = jw != IntPtr.Zero && IsIconic(jw) ? FindJwSecondDisplay() : IntPtr.Zero;
                JwView.Source = tv != IntPtr.Zero ? tv : jw;
                JwOnTv = tv != IntPtr.Zero;
                // Zoom: its Participants panel when that's in a window of its own, else its meeting.
                IntPtr zoom = ZoomMeetingHere(), popup = zoom == IntPtr.Zero ? IntPtr.Zero : ZoomPopup();
                ZoomView.Source = popup != IntPtr.Zero ? popup : zoom;
                // What JW Library has open, its page's media, and the part of each window to show.
                JwViewer = DateTime.Now - JwStateAskedAt < TimeSpan.FromSeconds(5) ? JwMedia : JwMediaOpen();
                Rectangle jwCrop = Rectangle.Empty;
                if (jw != IntPtr.Zero && !IsIconic(jw) && JwNavigating == 0)
                {
                    AutomationElement w = AutomationElement.FromHandle(jw);
                    List<JwItem> items = JwMediaItems(w).ConvertAll(x => x.Key);
                    // While a picture or video is open the page's list can be hidden: the last one is kept,
                    // and only let go once JW Library, asked afresh, has nothing open.
                    if (items.Count > 0 || JwMediaOpen() == "none") JwItems = items;
                    jwCrop = JwPageArea(w, jw);
                }
                else if (jw == IntPtr.Zero) JwItems = new List<JwItem>();
                if (JwViewer == "none" || jw == IntPtr.Zero) JwShowing = "";
                Rectangle zoomCrop = zoom == IntPtr.Zero || IsIconic(zoom) || popup != IntPtr.Zero ? Rectangle.Empty : ZoomSidePanel(zoom);
                ZoomPanelOpen = popup != IntPtr.Zero || !zoomCrop.IsEmpty;
                ZoomWaiting = ZoomPanelOpen ? ReadZoomWaiting() : new List<ZoomWaiter>();
                OnUi(() => { JwView.Crop = jwCrop; ZoomView.Crop = zoomCrop; });
            }
            catch (Exception e) { Log("live views: " + e.Message); }
            Thread.Sleep(2000);
        }
    }

    // ---- What can be done under the views -----------------------------------------------------
    // Under JW Library: a button for each picture and video on the page it's on (this week's
    // meeting media, its songs too), which shows it on the TV, and Stop, which closes the one open.
    // Pressed through Windows' accessibility interface, so the mouse and keyboard are left alone.
    // Under Zoom: its Participants panel opened or closed, and the mics switch.

    class JwItem { public string Name, Title, Group, Kind, Length; public bool Saved = true; }
    static volatile List<JwItem> JwItems = new List<JwItem>();
    static volatile string JwViewer = "none";   // what JW Library has open, as JwMediaOpen says
    static volatile string JwShowing = "";      // the item last shown from here, while something is open
    static volatile bool ZoomPanelOpen;
    static volatile IntPtr JwMainHandle;   // JW Library's main window (minimised or not), or none
    static volatile bool JwOnTv;           // the JW Library picture shows its second display (the main window's minimised)

    static readonly Condition JwGrids = new OrCondition(
        new PropertyCondition(AutomationElement.AutomationIdProperty, "LibraryItemsGrid"),   // the songs
        new PropertyCondition(AutomationElement.AutomationIdProperty, "MediaItemsGrid"),     // pictures and videos, under their headings
        new PropertyCondition(AutomationElement.AutomationIdProperty, "List"));              // a playlist's items (and other lists: see below)
    // A video says what it is and how long: '125. "Happy Are the Merciful" Videos. Duration 2:50.'
    // (in French "Vidéos. Durée 2:50.").
    static readonly Regex JwVideoName = new Regex(@"^(.*?)\s+[^\s.]+\.\s+[^\s.]+\s+(\d+:\d\d(?::\d\d)?)\.?\s*$");
    static readonly Regex JwLength = new Regex(@"^\d+:\d\d(?::\d\d)?$");
    static readonly Condition JwCard = new PropertyCondition(AutomationElement.AutomationIdProperty, "CardView");

    // The media on JW Library's page, in order, each with its element; none when the page has none.
    // Only those JW Library has made so far: a long list makes the rest as it's scrolled.
    static List<KeyValuePair<JwItem, AutomationElement>> JwMediaItems(AutomationElement w)
    {
        var found = new List<KeyValuePair<JwItem, AutomationElement>>();
        TreeWalker walk = TreeWalker.ControlViewWalker;
        Action<AutomationElement, string> add = (el, group) =>
        {
            string name = el.Current.Name.Trim();
            if (name == "") return;
            Match v = JwVideoName.Match(name);
            found.Add(new KeyValuePair<JwItem, AutomationElement>(new JwItem
            {
                Name = name, Group = group, Kind = v.Success ? "video" : "image",
                Title = v.Success ? v.Groups[1].Value : name, Length = v.Success ? v.Groups[2].Value : "",
                Saved = JwDownloadButton(el) == null,
            }, el));
        };
        foreach (AutomationElement grid in w.FindAll(TreeScope.Descendants, JwGrids))
            if (grid.Current.AutomationId == "List") JwPlaylistItems(grid, found);
            else for (AutomationElement c = walk.GetFirstChild(grid); c != null; c = walk.GetNextSibling(c))
            {
                if (c.Current.ControlType == ControlType.ListItem) add(c, "");
                else if (c.Current.ControlType == ControlType.Group)
                {
                    string group = c.Current.Name.Trim();
                    for (AutomationElement it = walk.GetFirstChild(c); it != null; it = walk.GetNextSibling(it))
                        if (it.Current.ControlType == ControlType.ListItem) add(it, group);
                }
            }
        return found;
    }

    // A playlist: each item a card, its name the item's, with a length if it's a video ("2:34"). Only
    // items with a card - not "Import a file" at the end, nor a list of something else.
    static void JwPlaylistItems(AutomationElement list, List<KeyValuePair<JwItem, AutomationElement>> found)
    {
        TreeWalker walk = TreeWalker.ControlViewWalker;
        for (AutomationElement it = walk.GetFirstChild(list); it != null; it = walk.GetNextSibling(it))
        {
            if (it.Current.ControlType != ControlType.ListItem) continue;
            AutomationElement card = it.FindFirst(TreeScope.Children, JwCard);
            string name = it.Current.Name.Trim();
            if (card == null || name == "") continue;
            string length = "";
            foreach (AutomationElement t in card.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
                if (JwLength.IsMatch(t.Current.Name.Trim())) { length = t.Current.Name.Trim(); break; }
            found.Add(new KeyValuePair<JwItem, AutomationElement>(new JwItem
            {
                Name = name, Group = "", Kind = length != "" ? "video" : "image", Title = name, Length = length,
                Saved = JwDownloadButton(it) == null,
            }, it));
        }
    }

    // ---- The page's media downloaded, in the best quality ------------------------------------------
    // An item not yet downloaded has a Download button (no id in a meeting's media, "InstallationState
    // Button" in a playlist); pressed, it offers the qualities ("720p", "480p"...), each a menu item
    // whose id is the quality, and the highest is chosen. Only a button called Download is pressed:
    // while downloading, the same button cancels.
    static readonly Regex JwDownloadName = new Regex("^(Download|T\u00e9l\u00e9charger|Descargar|Baixar|Transferir|Herunterladen|Scarica|Downloaden|Pobierz|\u0421\u043a\u0430\u0447\u0430\u0442\u044c)$", RegexOptions.IgnoreCase);
    static readonly Regex JwQuality = new Regex(@"^(\d{3,4})p$");

    static AutomationElement JwDownloadButton(AutomationElement item)
    {
        foreach (AutomationElement b in item.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        {
            string id = b.Current.AutomationId;
            if ((id == "" || id == "InstallationStateButton") && JwDownloadName.IsMatch(b.Current.Name.Trim())) return b;
        }
        return null;
    }

    // Downloads what's on JW Library's page (this week's meeting media first, if the page has none):
    // how many downloads were started, -1 if JW Library couldn't be reached, -3 if it's showing
    // something (which going to the meeting's media would take off the TV).
    static int JwDownloadAll()
    {
        lock (JwLock)
        {
            AutomationElement w = JwMainWindowRestored();
            if (w == null) return -1;
            if (JwMediaItems(w).Count == 0)
            {
                // Something showing on the TV hides the list: never taken off to download.
                string open = JwMediaOpen();
                if (open == "image" || open == "video" || open == "unknown") return -3;
                if (!JwOpenMeetings()) return -1;
                Thread.Sleep(1000);
            }
            int started = 0;
            foreach (KeyValuePair<JwItem, AutomationElement> it in JwMediaItems(w))
            {
                try
                {
                    AutomationElement b = JwDownloadButton(it.Value);
                    if (b == null) continue;
                    object p;
                    if (it.Value.TryGetCurrentPattern(ScrollItemPattern.Pattern, out p)) ((ScrollItemPattern)p).ScrollIntoView();
                    Press(b);
                    // The qualities offered: the highest chosen. None (a single file): it's started already.
                    AutomationElement best = null;
                    int bestP = 0;
                    WaitFor(() =>
                    {
                        foreach (AutomationElement m in w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)))
                        {
                            Match q = JwQuality.Match(m.Current.AutomationId);
                            if (q.Success && int.Parse(q.Groups[1].Value) > bestP) { best = m; bestP = int.Parse(q.Groups[1].Value); }
                        }
                        return best;
                    }, 1500);
                    if (best != null) Press(best);
                    started++;
                    Thread.Sleep(400);
                }
                catch (Exception e) { Log("JW Library download: " + e.Message); }
            }
            JwMain = null;
            return started;
        }
    }

    // Plays one of the page's items. Meeting media can be pressed through the accessibility interface;
    // a playlist's items can't (and Enter doesn't play them), so its picture is clicked: JW Library
    // brought in front for it (only clicked if it really is), then the mouse and the window that was
    // in front put back.
    static bool JwPress(AutomationElement item, IntPtr jw)
    {
        object p;
        if (item.TryGetCurrentPattern(InvokePattern.Pattern, out p))
        {
            IntPtr front = GetForegroundWindow();
            HoldFront();
            ((InvokePattern)p).Invoke();
            KeepInFront(front, jw);
            return true;
        }
        if (item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out p)) { ((ScrollItemPattern)p).ScrollIntoView(); Thread.Sleep(250); }
        System.Windows.Rect r = item.Current.BoundingRectangle;
        if (r.IsEmpty || r.Width < 60 || r.Height < 30) return false;
        IntPtr was = GetForegroundWindow();
        Point mouseAt = Cursor.Position;
        try
        {
            if (GetForegroundWindow() != jw) { BringToFront(jw); Thread.Sleep(300); }
            if (GetForegroundWindow() != jw) return false;
            // The picture, at the card's left: clear of its Stop tick and its menu.
            Click((int)r.Left + (int)Math.Min(50, r.Width / 6), (int)(r.Top + r.Height / 2));
            Thread.Sleep(400);
            return true;
        }
        finally
        {
            Cursor.Position = mouseAt;
            if (was != IntPtr.Zero && was != jw) BringToFront(was);
        }
    }

    // Opening or closing media, JW Library makes itself the active window. Meeting AV, in front when
    // its button is clicked, first stops other programs taking the front (Windows' own lock, while
    // JW Library is pressed); should JW Library get there all the same, whatever was in front is put
    // back each time it does, for the next 3 seconds.
    static void HoldFront() { LockSetForegroundWindow(1); }

    static void KeepInFront(IntPtr front, IntPtr jw)
    {
        try
        {
            if (front == IntPtr.Zero || front == jw) return;
            // Looked at often, so a moment in front is too short to notice. Any of JW Library's
            // windows counts: its second display can make itself active too.
            var title = new StringBuilder(32);
            for (DateTime until = DateTime.Now.AddSeconds(3); DateTime.Now < until; Thread.Sleep(15))
            {
                IntPtr now = GetForegroundWindow();
                if (now == front || now == IntPtr.Zero) continue;
                title.Clear();
                GetWindowText(now, title, 32);
                if (now == jw || title.ToString() == "JW Library") BringToFront(front);
            }
        }
        finally { LockSetForegroundWindow(2); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool LockSetForegroundWindow(uint code);

    // A minimised JW Library is brought back to be driven (minimised, it shows Windows nothing) - but
    // off the screens to the right, and not made active, so it isn't seen - then minimised again, its
    // place on the screen as it was.
    static WINDOWPLACEMENT JwOutOfSight(IntPtr h)
    {
        var was = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
        GetWindowPlacement(h, ref was);
        WINDOWPLACEMENT away = was;
        Rectangle all = SystemInformation.VirtualScreen;
        int w = Math.Max(800, was.normal.Right - was.normal.Left), ht = Math.Max(600, was.normal.Bottom - was.normal.Top);
        away.normal = new RECT { Left = all.Right + 100, Top = all.Top, Right = all.Right + 100 + w, Bottom = all.Top + ht };
        away.flags = 0;
        away.showCmd = 4;   // restored, not activated
        SetWindowPlacement(h, ref away);
        Thread.Sleep(800);
        return was;
    }

    static void JwBackToMinimised(IntPtr h, WINDOWPLACEMENT was)
    {
        was.showCmd = 7;   // minimised, not activated
        SetWindowPlacement(h, ref was);
    }

    struct WINDOWPLACEMENT { public int length, flags, showCmd; public Point minPosition, maxPosition; public RECT normal; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);

    // JW Library's page in its window (from the window's top left): under its title bar, right of its
    // side menu. Empty if it can't be told.
    static Rectangle JwPageArea(AutomationElement w, IntPtr h)
    {
        RECT r;
        AutomationElement core = w.FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.ClassNameProperty, "Windows.UI.Core.CoreWindow"));
        if (core == null || !GetWindowRect(h, out r)) return Rectangle.Empty;
        System.Windows.Rect c = core.Current.BoundingRectangle;
        if (c.IsEmpty || c.Width < 100 || c.Height < 100) return Rectangle.Empty;
        double left = c.Left;
        AutomationElement menu = core.FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "PaneRoot"));
        if (menu != null && !menu.Current.IsOffscreen)
        {
            System.Windows.Rect m = menu.Current.BoundingRectangle;
            if (!m.IsEmpty && m.Width < c.Width / 3) left = m.Right;
        }
        return new Rectangle((int)left - r.Left, (int)c.Top - r.Top, (int)(c.Right - left), (int)c.Height);
    }

    // Shows one of the page's items on the TV: the one at that place in the list, if it still has that
    // name. Something already open (over the list) is closed first.
    static string JwShow(int index, string name)
    {
        IntPtr h = JwMainHandle;
        if (h == IntPtr.Zero || !IsWindow(h)) return "JW Library isn't open";
        lock (JwLock)
        {
            try
            {
                // Minimised, JW Library shows Windows nothing: brought back (not made active) for the
                // moment, and minimised again after - what it shows stays on the TV.
                bool minimised = IsIconic(h);
                WINDOWPLACEMENT was = minimised ? JwOutOfSight(h) : default(WINDOWPLACEMENT);
                try { return JwShowHere(h, index, name); }
                finally { if (minimised) { Thread.Sleep(600); JwBackToMinimised(h, was); } }
            }
            catch (Exception e) { Log("JW Library show: " + e.Message); return "couldn't reach JW Library"; }
        }
    }

    static string JwShowHere(IntPtr h, int index, string name)
    {
        AutomationElement w = AutomationElement.FromHandle(h);
        Func<AutomationElement> find = () =>
        {
            List<KeyValuePair<JwItem, AutomationElement>> items = JwMediaItems(w);
            if (index >= 0 && index < items.Count && items[index].Key.Name == name) return items[index].Value;
            return items.Find(x => x.Key.Name == name).Value;
        };
        // Something open (a picture over a playlist hides the list, a meeting's leaves it
        // behind) is closed first, so the click or press reaches the list.
        string open = JwMediaOpen();
        if (open == "image" || open == "video" || open == "unknown")
            if (!JwClose(w)) return "couldn't close what JW Library has open";
        AutomationElement item = WaitFor(find, 3000);
        if (item == null) return "it isn't on JW Library's page any more";
        if (!JwPress(item, h)) return "couldn't bring JW Library forward to open it";
        JwShowing = name;
        JwMain = null;   // its page changed
        return "ok";
    }

    // Closes the picture or video JW Library has open, with its Back button, back to the page it was
    // opened from. Only when something is open: Back on a page would leave it.
    static string JwStop()
    {
        IntPtr h = JwMainHandle;
        if (h == IntPtr.Zero || !IsWindow(h)) return "JW Library isn't open";
        lock (JwLock)
        {
            bool minimised = IsIconic(h);   // brought back (out of sight) for its Back button, minimised again after
            WINDOWPLACEMENT was = default(WINDOWPLACEMENT);
            try
            {
                if (minimised) was = JwOutOfSight(h);
                if (JwMediaOpen() == "none") { JwShowing = ""; return "ok"; }
                if (!JwClose(AutomationElement.FromHandle(h))) return "couldn't find JW Library's Back button";
                JwShowing = "";
                return "ok";
            }
            catch (Exception e) { Log("JW Library stop: " + e.Message); return "couldn't reach JW Library"; }
            finally { if (minimised) { Thread.Sleep(300); JwBackToMinimised(h, was); } }
        }
    }

    static bool JwClose(AutomationElement w)
    {
        AutomationElement back = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "Back"));
        if (back == null || back.Current.IsOffscreen || !back.Current.IsEnabled) return false;
        IntPtr front = GetForegroundWindow();
        HoldFront();
        Press(back);
        JwMain = null;
        KeepInFront(front, new IntPtr(w.Current.NativeWindowHandle));   // closing, it makes itself active too
        return true;
    }

    // Zoom's side panel (Participants, or Chat) in its window, from the window's top left; empty when
    // none is open. Zoom's video sits in its "ContentLeftPanel", which narrows to make room for the
    // panel; failing that, the Participants panel is found by its row of buttons (on the screen, so
    // only when Zoom isn't covered there).
    static Rectangle ZoomSidePanel(IntPtr zoom)
    {
        RECT r;
        if (!GetWindowRect(zoom, out r)) return Rectangle.Empty;
        int left = -1;
        try
        {
            AutomationElement video = AutomationElement.FromHandle(zoom).FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.NameProperty, "ContentLeftPanel"));
            if (video != null)
            {
                System.Windows.Rect v = video.Current.BoundingRectangle;
                if (!v.IsEmpty && r.Right - (int)v.Right >= 150) left = (int)v.Right;
            }
        }
        catch (ElementNotAvailableException) { }
        if (left < 0)
        {
            Point[] buttons = FindPanelButtons(zoom);
            if (buttons != null) left = Math.Max(r.Left, buttons[0].X - (buttons[1].X - buttons[0].X) / 2 - 12);
        }
        if (left < 0) return Rectangle.Empty;
        return new Rectangle(left - r.Left, 0, r.Right - left, r.Bottom - r.Top);
    }

    // Zoom's Participants panel opened or closed with Zoom's own key, Alt+U: Zoom is brought in front
    // for it (the key is only sent if it is), and the window that was in front put back after. Zoom
    // widens its window for the panel, so its size is put back too, the video making room.
    static string ToggleZoomPanel() { return OneAtATime(ToggleZoomPanelHere); }

    static string ToggleZoomPanelHere()
    {
        IntPtr zoom = ZoomMeetingHere();
        if (zoom == IntPtr.Zero) return "no Zoom meeting open";
        IntPtr popup = ZoomPopup();
        if (popup != IntPtr.Zero)   // in a window of its own: closed as its ✕ would
        {
            PostMessage(popup, 0x0010, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(400);
            ZoomPanelOpen = false;
            OnUi(() => { ZoomView.Source = zoom; ZoomView.Crop = Rectangle.Empty; UpdateViews(); });
            return "ok";
        }
        RECT r;
        bool keepSize = GetWindowRect(zoom, out r) && !IsZoomed(zoom);
        IntPtr was = GetForegroundWindow();
        BringToFront(zoom);
        for (int i = 0; i < 10 && GetForegroundWindow() != zoom; i++) Thread.Sleep(50);
        if (GetForegroundWindow() != zoom) return "couldn't bring Zoom forward";
        keybd_event(0x12, 0, 0, UIntPtr.Zero);     // Alt+U: Participants
        keybd_event(0x55, 0, 0, UIntPtr.Zero);
        keybd_event(0x55, 0, 2, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        Thread.Sleep(600);
        if (keepSize) SetWindowPos(zoom, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0004 | 0x0010);
        IntPtr opened = ZoomPopup();
        if (opened != IntPtr.Zero) Thread.Sleep(300);
        if (was != IntPtr.Zero && was != zoom && was != opened) BringToFront(was);
        Rectangle panel = opened != IntPtr.Zero ? Rectangle.Empty : ZoomSidePanel(zoom);
        ZoomPanelOpen = opened != IntPtr.Zero || !panel.IsEmpty;
        OnUi(() => { ZoomView.Source = opened != IntPtr.Zero ? opened : zoom; ZoomView.Crop = panel; UpdateViews(); });
        return "ok";
    }

    // ---- OBS coming back by itself ------------------------------------------------
    // Should OBS close or crash around a meeting (from 45 minutes before it starts until 2 hours
    // after), it's started again with its virtual camera after 10 seconds, by the setup with just
    // OBS. Not while the setup is running (it may be restarting a stuck OBS itself), and not more
    // than once every 3 minutes, in case it can't start.

    static void WatchObs()
    {
        DateTime goneSince = DateTime.MinValue, triedAt = DateTime.MinValue;
        while (true)
        {
            Thread.Sleep(2000);
            try
            {
                long now = NowMs();
                bool nearMeeting = MeetingAt > 0 && now > MeetingAt - 45 * 60000 && now < MeetingAt + 2 * 3600000;
                bool busy;
                lock (SetupLock) busy = SetupRunning;
                if (NoObs || !nearMeeting || busy || Process.GetProcessesByName("obs64").Length > 0) { goneSince = DateTime.MinValue; continue; }
                if (goneSince == DateTime.MinValue) goneSince = DateTime.Now;
                if (DateTime.Now - goneSince < TimeSpan.FromSeconds(10) || DateTime.Now - triedAt < TimeSpan.FromMinutes(3)) continue;
                triedAt = DateTime.Now;
                Log("OBS isn't running: starting it again");
                RunSetup(null, true);
            }
            catch (Exception e) { Log("OBS: " + e.Message); }
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
            try
            {
                TcpClient client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => Handle(client));
            }
            catch (Exception e) { Log("tablet server: " + e.Message); Thread.Sleep(1000); }
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
            else if (path == "/setup/skip" && local)
            {
                // "Show the windows" on the start-up screen: the PC's screen back now, arranged when it's all open.
                StartingUp = false;
                Uncover();
                SendJson(stream, SetupJson());
            }
            else if (path == "/setup/run" && local)
            {
                Match m = Regex.Match(query, "meeting=(Midweek|Weekend)");
                RunSetup(m.Success ? m.Groups[1].Value : null, query.Contains("obs=1"));
                SendJson(stream, SetupJson());
            }
            else if (path == "/app" && local)
                SendJson(stream, AppJson());
            else if (path == "/app/set" && local)
            {
                Match st = Regex.Match(query, "startup=([01])"), up = Regex.Match(query, "updates=([01])"), zl = Regex.Match(query, "zoom=([^&]*)");
                try
                {
                    if (st.Success) SetStartsWithWindows(st.Groups[1].Value == "1");
                    if (up.Success) { if (up.Groups[1].Value == "1") File.Delete(NoUpdatesFile); else File.WriteAllText(NoUpdatesFile, "off"); }
                    if (zl.Success)
                    {
                        string link = Uri.UnescapeDataString(zl.Groups[1].Value.Replace('+', ' ')).Trim();
                        if (link == "") File.Delete(ZoomLinkFile); else File.WriteAllText(ZoomLinkFile, link);
                    }
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
                ZoomMuteOn = !query.Contains("mute=0");
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
                    bool ok = JwKeepingFront(path == "/jw/meetings" ? (Func<bool>)JwOpenMeetings : JwOpenPlaylists);
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
            else if (path == "/zoom/allow-unmute" && local)
            {
                string result = AllowZoomUnmute();
                if (result == "allowed") ZoomMics = "open";
                SendJson(stream, "{\"result\":" + Json(result) + ",\"mics\":" + Json(ZoomMics) + "}");
            }
            else if (path == "/zoom/mute" && local)   // the same as 30 seconds before, now
            {
                ZoomMuteResult = MuteZoomEveryone(); ZoomMutedAt = NowMs();
                SendJson(stream, "{\"result\":" + Json(ZoomMuteResult) + ",\"mics\":" + Json(ZoomMics) + "}");
            }
            else if (path == "/jw/show" && local)   // i=<its place in the list>&n=<its name>
            {
                Match i = Regex.Match(query, "(?:^|&)i=(\\d+)"), n = Regex.Match(query, "(?:^|&)n=([^&]*)");
                string result = JwShow(i.Success ? int.Parse(i.Groups[1].Value) : -1,
                                       n.Success ? Uri.UnescapeDataString(n.Groups[1].Value.Replace('+', ' ')) : "");
                SendJson(stream, "{\"result\":" + Json(result) + "}");
            }
            else if (path == "/jw/download" && local)   // the page's media (or this week's), best quality
            {
                if (Interlocked.Exchange(ref JwNavigating, 1) == 1) { SendJson(stream, "{\"started\":-2}"); return; }
                try
                {
                    int started = -1;
                    JwKeepingFront(() => (started = JwDownloadAll()) >= 0);
                    SendJson(stream, "{\"started\":" + started + "}");
                }
                finally { JwNavigating = 0; }
            }
            else if (path == "/jw/stop" && local)
                SendJson(stream, "{\"result\":" + Json(JwStop()) + "}");
            else if (path == "/zoom/lower-hands" && local)
                SendJson(stream, "{\"result\":" + Json(LowerZoomHands()) + "}");
            else if (path == "/zoom/admit" && local)   // who=<their line's hash>, or the first waiting
            {
                Match who = Regex.Match(query, "(?:^|&)who=(-?\\d+)");
                long hash = who.Success ? long.Parse(who.Groups[1].Value) : 0;
                string result = AdmitZoom(hash);
                ZoomWaiting = ReadZoomWaiting();
                OnUi(UpdateViews);
                SendJson(stream, "{\"result\":" + Json(result) + "}");
            }
            else if (path == "/zoom/panel" && local)   // Participants opened or closed
                SendJson(stream, "{\"result\":" + Json(ToggleZoomPanel()) + ",\"open\":" + (ZoomPanelOpen ? "true" : "false") + "}");
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
            else if (path == "/obs/started" && local)   // when OBS last started (ms since 1970), 0 if it isn't running
            {
                long started = 0;
                foreach (Process p in Process.GetProcessesByName("obs64"))
                    try { started = Math.Max(started, new DateTimeOffset(p.StartTime).ToUnixTimeMilliseconds()); } catch { }
                SendJson(stream, "{\"started\":" + started + "}");
            }
            else if (path == "/obs/monitoring" && local)   // the monitoring device OBS is really using, from its log
                SendJson(stream, "{\"device\":" + Json(ObsMonitoringDevice()) + "}");
            else if (path == "/audio/devices" && local)
                SendJson(stream, AudioDevicesJson());
            else if (path == "/audio/hold" && local)   // on=1 holds the microphone at its level now, on=0 lets go
            {
                if (query.Contains("on=0")) SaveMicHold(-1);
                else
                {
                    try
                    {
                        IAudioEndpointVolume v; IAudioMeterInformation m;
                        lock (AudioLock) Microphone(out v, out m);
                        float now; v.GetMasterVolumeLevelScalar(out now);
                        SaveMicHold((int)Math.Round(now * 100));
                    }
                    catch { }
                }
                SendJson(stream, AudioJson());
            }
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
    WebView2 view;
    readonly string url, userData;

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
        this.url = url;
        this.userData = userData;
        Load += (s, e) => StartView();
    }

    async void StartView()
    {
        try
        {
            view = await AppWindow.NewView(this, userData, () => BeginInvoke((Action)StartView));
            view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            view.Source = new Uri(url);
        }
        catch { Close(); }
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
    WebView2 view;
    readonly string url, userData;
    public event Action Failed;
    public event Action<string> Message;   // from the page (chrome.webview.postMessage)
    public bool HasFailed;

    public AppWindow(string url, string userData, Icon icon)
    {
        Text = "Meeting AV";
        Icon = icon;
        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(area.X + area.Width / 10, area.Y + area.Height / 10, area.Width * 4 / 5, area.Height * 4 / 5);
        this.url = url;
        this.userData = userData;
        Load += (s, e) => StartView();
    }

    async void StartView()
    {
        try
        {
            view = await NewView(this, userData, () => BeginInvoke((Action)StartView));
            view.CoreWebView2.NewWindowRequested += (o, a) => { a.Handled = true; try { Process.Start(a.Uri); } catch { } };
            view.CoreWebView2.WebMessageReceived += (o, a) =>
            {
                string m = null;
                try { m = a.TryGetWebMessageAsString(); } catch { }
                if (m != null && Message != null) Message(m);
            };
            view.Source = new Uri(url);
        }
        catch
        {
            HasFailed = true;
            if (Failed != null) Failed();
        }
    }

    // A WebView2 filling the window, in place of the one before. Should the page stop (its process
    // crashed or hung) it's reloaded; should Edge's engine itself stop, that WebView2 can't be used
    // again, so after a second restart() makes a new one.
    public static async System.Threading.Tasks.Task<WebView2> NewView(Form form, string userData, Action restart)
    {
        var old = new List<Control>();
        foreach (Control c in form.Controls) if (c is WebView2) old.Add(c);
        foreach (Control c in old) { form.Controls.Remove(c); c.Dispose(); }
        var view = new WebView2 { Dock = DockStyle.Fill };
        if (form is TimerWindow) view.DefaultBackgroundColor = Color.Black;
        form.Controls.Add(view);
        CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userData);
        await view.EnsureCoreWebView2Async(env);
        view.CoreWebView2.Settings.IsStatusBarEnabled = false;
        view.CoreWebView2.ProcessFailed += async (o, a) =>
        {
            if (a.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                await System.Threading.Tasks.Task.Delay(1000);
                if (!form.IsDisposed) restart();
            }
            else if (a.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                     a.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                try { view.CoreWebView2.Reload(); } catch { }
        };
        return view;
    }

    // While Meeting AV starts up, its window covers the whole of the PC's screen, on top, so the
    // programs opening behind it (OBS, JW Library, Zoom) aren't seen jumping about; then back as it was.
    Rectangle normal;
    public bool Covering { get; private set; }
    public void Cover(bool on)
    {
        if (on == Covering) return;
        Covering = on;
        if (on)
        {
            normal = Bounds;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.PrimaryScreen.Bounds;
            TopMost = true;
        }
        else
        {
            TopMost = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = normal;
        }
    }

    // To the page, as a message event.
    public void Post(string json)
    {
        try { if (view != null && view.CoreWebView2 != null) view.CoreWebView2.PostWebMessageAsJson(json); } catch { }
    }

    public void Bring()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }
}

// One program's window shown live in Meeting AV's window, by Windows' thumbnails. Its source is
// set from the background, its box by the page; Update (on the window's thread) puts it right.
class LiveView
{
    public volatile IntPtr Source;    // the window shown, or none
    public Rectangle Box;             // where, in Meeting AV's window (its pixels); empty: not shown
    public Rectangle Crop;            // the part of the window shown (its pixels, from its top left); empty: all of it
    IntPtr thumb, of;                 // the thumbnail, and the window it's of

    // "shown", "minimised", or what to say when there's no window to show.
    public string State(string none)
    {
        IntPtr h = Source;
        return h == IntPtr.Zero || !IsWindow(h) ? none : IsIconic(h) ? "minimised" : "shown";
    }

    public void Update(Form into)
    {
        IntPtr h = Source;
        if (Box.IsEmpty || h == IntPtr.Zero || !IsWindow(h) || IsIconic(h)) { Clear(); return; }
        if (of != h)
        {
            Clear();
            if (DwmRegisterThumbnail(into.Handle, h, out thumb) != 0) { thumb = IntPtr.Zero; return; }
            of = h;
        }
        Size size;
        if (DwmQueryThumbnailSourceSize(thumb, out size) != 0 || size.Width <= 0 || size.Height <= 0) return;
        // The window (or the part of it cropped to), as big as fits in the box, in its middle...
        Rectangle src = Rectangle.Intersect(Crop.IsEmpty ? new Rectangle(Point.Empty, size) : Crop, new Rectangle(Point.Empty, size));
        if (src.Width < 20 || src.Height < 20) src = new Rectangle(Point.Empty, size);
        double scale = Math.Min((double)Box.Width / src.Width, (double)Box.Height / src.Height);
        int w = (int)(src.Width * scale), ht = (int)(src.Height * scale);
        var fit = new Rectangle(Box.X + (Box.Width - w) / 2, Box.Y + (Box.Height - ht) / 2, w, ht);
        // ...but only the part inside Meeting AV's window, with the page scrolled.
        Rectangle part = Rectangle.Intersect(fit, into.ClientRectangle);
        var props = new ThumbnailProperties { flags = 1 | 2 | 8, visible = part.Width > 0 && part.Height > 0 };
        if (props.visible)
        {
            props.destination = new RECT { Left = part.Left, Top = part.Top, Right = part.Right, Bottom = part.Bottom };
            props.source = new RECT
            {
                Left = src.X + (int)((part.Left - fit.Left) / scale), Top = src.Y + (int)((part.Top - fit.Top) / scale),
                Right = src.X + (int)((part.Right - fit.Left) / scale), Bottom = src.Y + (int)((part.Bottom - fit.Top) / scale),
            };
        }
        DwmUpdateThumbnailProperties(thumb, ref props);
    }

    void Clear()
    {
        if (thumb != IntPtr.Zero) DwmUnregisterThumbnail(thumb);
        thumb = of = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct ThumbnailProperties
    {
        public int flags;   // 1 destination, 2 source, 4 opacity, 8 visible, 16 client area only
        public RECT destination, source;
        public byte opacity;
        public bool visible, clientAreaOnly;
    }
    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref ThumbnailProperties props);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out Size size);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
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
    void OpenPropertyStore(int access, out IPropertyStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    void GetCount(out int count);
    void GetAt(int index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
}

[StructLayout(LayoutKind.Sequential)]
struct PropertyKey { public Guid fmtid; public int pid; }

[StructLayout(LayoutKind.Sequential)]
struct PropVariant { public short vt; short r1, r2, r3; public IntPtr p; IntPtr p2; }

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
