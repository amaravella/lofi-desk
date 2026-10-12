// Lofi Desk.
//   /s — заставка: основной монитор (OLED) чистый чёрный, остальные — сцена scene.html
//   /w — живые обои: сцена за иконками рабочего стола на неосновных мониторах
//   /t — тестовый показ заставки на 8 секунд
// В оба режима раз в 2 секунды отправляется лента активности Claude Code (~/.claude/projects).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class LofiSaver
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    static readonly List<WebView2> views = new List<WebView2>();
    static readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static Dictionary<string, string> settings;

    [STAThread]
    static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant().Trim() : "/c";
        if (mode.StartsWith("/p")) return;
        if (mode.StartsWith("/c"))
        {
            settings = LoadSettings(); Widgets.Configure(settings);
            MessageBox.Show(Widgets.L("Lofi Desk\n\nOLED — чёрный экран, второй монитор — сцена.\nНастройки: ", "Lofi Desk\n\nPrimary (OLED) monitor goes pure black, the others show the scene.\nSettings: ") + Path.Combine(baseDir, "settings.ini"),
                "Lofi Desk", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        bool wallpaper = mode.StartsWith("/w");
        bool testMode = mode.StartsWith("/t");

        mutex = new Mutex(false, wallpaper ? "LofiDeskWallpaper" : "LofiDeskScreensaver");
        try { if (!mutex.WaitOne(wallpaper ? 10000 : 0)) return; }
        catch (AbandonedMutexException) { }
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // PER_MONITOR_AWARE_V2
        Application.EnableVisualStyles();
        // никаких диалогов с ошибками: пишем в лог, обои перезапускаются, заставка закрывается
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
        {
            Log(e.Exception);
            if (wallpaper) RestartWallpaper(); else Application.Exit();
        };
        AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { Log(e.ExceptionObject as Exception); };
        settings = LoadSettings(); Widgets.Configure(settings);

        if (wallpaper) RunWallpaper(); else RunScreensaver(testMode);
    }

    static Mutex mutex;
    static bool restarting;

    static void Log(Exception ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LofiDesk");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "log.txt"), DateTime.Now.ToString("s") + " " + ex + "\r\n\r\n");
        }
        catch { }
    }

    // explorer умер/перезапустился и снёс наши окна — запускаем себя заново
    static void RestartWallpaper()
    {
        if (restarting) return;
        restarting = true;
        Widgets.HideTray();
        try { mutex.ReleaseMutex(); } catch { }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "/w") { UseShellExecute = false };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Log(ex); }
        Environment.Exit(0);
    }

    // ---------------- заставка ----------------
    static void RunScreensaver(bool testMode)
    {
        var forms = new List<Form>();
        foreach (Screen s in Screen.AllScreens)
        {
            var f = NewForm(s.Bounds);
            f.TopMost = true;
            var bounds = s.Bounds;
            f.Shown += delegate { f.Bounds = bounds; Cursor.Hide(); };
            if (!s.Primary) AttachScene(f, "screensaver");
            forms.Add(f);
        }
        foreach (var f in forms) f.Show();

        // выходим, только если сдвинули мышь или нажали её кнопку: программы (запись игр, автоматизация)
        // постоянно шлют синтетические нажатия клавиш, и по «любому вводу» заставка гасла бы сразу
        Point startPos = Cursor.Position;
        int startTick = Environment.TickCount;
        var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += delegate
        {
            if (testMode) { if (Environment.TickCount - startTick > 8000) Application.Exit(); return; }
            Point now = Cursor.Position;
            // первые 1.5 с игнорируем — даём окнам спокойно появиться
            if (Environment.TickCount - startTick < 1500) { startPos = now; return; }
            bool moved = Math.Abs(now.X - startPos.X) > 12 || Math.Abs(now.Y - startPos.Y) > 12;
            bool clicked = (Control.MouseButtons & (MouseButtons.Left | MouseButtons.Right | MouseButtons.Middle)) != 0;
            if (moved || clicked) Application.Exit();
        };
        timer.Start();
        StartFeed();
        Widgets.Start(Broadcast, settings, baseDir);
        Application.Run();
    }

    static uint LastInput()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        GetLastInputInfo(ref lii);
        return lii.dwTime;
    }

    // ---------------- живые обои ----------------
    static readonly List<KeyValuePair<Form, string>> wallForms = new List<KeyValuePair<Form, string>>();
    static IntPtr desktopHost, desktopInsertAfter;

    static void RunWallpaper()
    {
        // ждём, пока поднимется рабочий стол (после старта системы или перезапуска explorer)
        bool waited = false;
        for (int i = 0; i < 120 && !Desktop.Ready(); i++) { waited = true; Thread.Sleep(500); }
        if (waited) Thread.Sleep(3000);
        foreach (Screen s in Screen.AllScreens)
        {
            if (s.Primary) continue;
            var f = NewForm(s.Bounds);
            AttachScene(f, "wallpaper");
            f.Show();
            wallForms.Add(new KeyValuePair<Form, string>(f, s.DeviceName));
        }
        if (wallForms.Count == 0) return;
        AttachAllToDesktop();

        bool paused = false;
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += delegate
        {
            // explorer перезапустился — окна уничтожены вместе с ним, начинаем заново
            bool lost = !Desktop.IsWindow(desktopHost);
            foreach (var kv in wallForms) if (kv.Key.IsDisposed || !Desktop.IsWindow(kv.Key.Handle)) lost = true;
            if (lost) { RestartWallpaper(); return; }
            // explorer мог положить статичный фон поверх нас (смена обоев, тема) — возвращаем наверх
            foreach (var kv in wallForms) Desktop.KeepOnTop(kv.Key.Handle, desktopInsertAfter);
            bool p = Desktop.ShouldPause();
            if (p != paused) { paused = p; Broadcast("{\"type\":\"pause\",\"value\":" + (p ? "true" : "false") + "}"); }
        };
        timer.Start();
        // смена разрешения/расположения мониторов — через 2 с переставляем окна
var relayout = new System.Windows.Forms.Timer { Interval = 2000 };
        relayout.Tick += delegate { relayout.Stop(); try { AttachAllToDesktop(); } catch (Exception ex) { Log(ex); RestartWallpaper(); } };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate { relayout.Stop(); relayout.Start(); };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += delegate { relayout.Stop(); relayout.Start(); };
        StartMouse();
        StartFeed();
        Widgets.Start(Broadcast, settings, baseDir);
        Widgets.Tray(delegate { RestartWallpaper(); });
        Application.ApplicationExit += delegate { Widgets.HideTray(); };
        Application.Run();
    }

    // Обои лежат под иконками и кликов не получают: пересылаем им курсор, когда он над голым рабочим столом
    static void StartMouse()
    {
        string last = null;
        var t = new System.Windows.Forms.Timer { Interval = 33 };
        t.Tick += delegate
        {
            try
            {
                var p = Desktop.Cursor();
                string msg = null; WebView2 target = null;
                foreach (var kv in wallForms)
                {
                    Screen scr = null;
                    foreach (Screen s in Screen.AllScreens) if (s.DeviceName == kv.Value) scr = s;
                    if (scr == null || !scr.Bounds.Contains(p)) continue;
                    target = kv.Key.Tag as WebView2;
                    if (Desktop.OverDesktop(p))
                    {
                        msg = "{\"type\":\"mouse\",\"over\":true,\"px\":" + (p.X - scr.Bounds.X)
                            + ",\"py\":" + (p.Y - scr.Bounds.Y)
                            + ",\"down\":" + (Desktop.LeftDown() ? "true" : "false") + "}";
                    }
                }
                if (msg == null) msg = "{\"type\":\"mouse\",\"over\":false}";
                if (msg == last) return;
                last = msg;
                if (target != null) { if (target.CoreWebView2 != null) target.CoreWebView2.PostWebMessageAsJson(msg); }
                else Broadcast(msg);
            }
            catch { }
        };
        t.Start();
    }

    static void AttachAllToDesktop()
    {
        IntPtr insertAfter;
        desktopHost = Desktop.GetHost(out insertAfter);
        desktopInsertAfter = insertAfter;
        foreach (var kv in wallForms)
        {
            Screen scr = null;
            foreach (Screen s in Screen.AllScreens) if (s.DeviceName == kv.Value) scr = s;
            if (scr == null) { kv.Key.Hide(); continue; }
            Desktop.Attach(kv.Key.Handle, desktopHost, insertAfter, scr.Bounds);
        }
    }

    // ---------------- общее ----------------
    static Form NewForm(Rectangle bounds)
    {
        return new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            BackColor = Color.Black,
            StartPosition = FormStartPosition.Manual,
            Bounds = bounds,
            ShowInTaskbar = false,
            Text = "Lofi Desk"
        };
    }

    static Dictionary<string, string> LoadSettings()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        d["scene"] = "lofi"; d["feed"] = "1"; d["art"] = ""; d["lang"] = "en"; d["limits"] = "0";
        string p = Path.Combine(baseDir, "settings.ini");
        if (File.Exists(p))
            foreach (var line in File.ReadAllLines(p, Encoding.UTF8))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";")) continue;
                int eq = t.IndexOf('=');
                if (eq > 0) d[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
            }
        return d;
    }

    static async void AttachScene(Form f, string mode)
    {
        // язык интерфейса: scene.html — русский оригинал, scene.en.html собирается build.ps1 из словаря i18n/en.json
        string scene = Path.Combine(baseDir, "scene.html"), en = Path.Combine(baseDir, "scene.en.html");
        if (Widgets.En && File.Exists(en)) scene = en;
        if (!File.Exists(scene)) return;
        try
        {
            var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
            f.Controls.Add(web);
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LofiDesk", "WebView2-" + mode);
            var env = await CoreWebView2Environment.CreateAsync(null, data, new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
            await web.EnsureCoreWebView2Async(env);
            var st = web.CoreWebView2.Settings;
            st.AreDefaultContextMenusEnabled = false;
            st.AreDevToolsEnabled = false;
            st.IsZoomControlEnabled = false;
            st.IsStatusBarEnabled = false;
            string url = new Uri(scene).AbsoluteUri + "?mode=" + mode;
            foreach (var kv in settings) url += "&" + Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value);
            web.CoreWebView2.NavigationCompleted += delegate { PushFeed(); Widgets.ResendAll(); };
            web.CoreWebView2.WebMessageReceived += OnPageMessage;
            f.Tag = web;
            web.CoreWebView2.Navigate(url);
            views.Add(web);
        }
        catch
        {
            // если WebView2 не завёлся — остаётся чёрный экран
        }
    }

    // страница просит открыть сессию в приложении Claude (клик по строке ленты)
    static void OnPageMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var m = json.DeserializeObject(e.WebMessageAsJson) as Dictionary<string, object>;
            if (m == null) return;
            string type = m["type"] as string;
            if (type == "media") { object av; Widgets.MediaCommand(Convert.ToString(m["cmd"]), m.TryGetValue("arg", out av) && av != null ? Convert.ToDouble(av) : 0); return; }
            if (type == "launch") { Widgets.Launch(Convert.ToInt32(m["i"])); return; }
            if (type == "note-toggle") { Widgets.NoteToggle(Convert.ToString(m["id"])); return; }
            if (type == "notify") { Widgets.Notify(Convert.ToString(m["title"]), Convert.ToString(m["text"])); return; }
            if (type == "note-del") { Widgets.NoteDelete(Convert.ToString(m["id"])); return; }
            if (type == "act") { Widgets.DoAction(Convert.ToString(m["what"])); return; }
            if (type == "note-add")
            {
                // координаты страницы → экран монитора, на котором эта страница
                foreach (var kv in wallForms)
                {
                    var wv = kv.Key.Tag as WebView2;
                    if (wv == null || wv.CoreWebView2 != sender) continue;
                    foreach (Screen s in Screen.AllScreens)
                        if (s.DeviceName == kv.Value)
                        {
                            double dpr = Convert.ToDouble(m["dpr"]);
                            var pt = new Point(s.Bounds.X + (int)(Convert.ToDouble(m["x"]) * dpr), s.Bounds.Y + (int)(Convert.ToDouble(m["y"]) * dpr));
                            Widgets.ShowNoteInput(pt, dpr);
                        }
                }
                return;
            }
            if (type != "open") return;
            string id = m["id"] as string;
            if (id == null || !Regex.IsMatch(id, "^local_[0-9a-f-]{36}$")) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("claude://claude.ai/epitaxy/" + id) { UseShellExecute = true });
        }
        catch (Exception ex) { Log(ex); }
    }

    static void Broadcast(string message)
    {
        foreach (var v in views)
            try { if (v.CoreWebView2 != null) v.CoreWebView2.PostWebMessageAsJson(message); } catch { }
    }

    static void StartFeed()
    {
        if (settings["feed"] != "1") return;
        var t = new System.Windows.Forms.Timer { Interval = 2000 };
        t.Tick += delegate { PushFeed(); };
        t.Start();
    }

    static void PushFeed()
    {
        if (settings["feed"] != "1") return;
        try
        {
            var msg = new Dictionary<string, object>();
            msg["type"] = "feed";
            msg["sessions"] = ClaudeFeed.Build();
            Broadcast(json.Serialize(msg));
        }
        catch { }
    }
}

// Прикрепление окна за иконками рабочего стола (как у Lively / Wallpaper Engine).
static class Desktop
{
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT pt, uint count);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint ini);

    const int GWL_STYLE = -16;
    const long WS_CHILD = 0x40000000, WS_POPUP = 0x80000000;

    public static bool Ready()
    {
        IntPtr progman = FindWindow("Progman", null);
        return progman != IntPtr.Zero && FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
    }

    public static IntPtr GetHost(out IntPtr insertAfter)
    {
        IntPtr progman = FindWindow("Progman", null), res;
        // просим explorer создать WorkerW за иконками
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0, 1000, out res);
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out res);
        // Windows 11 24H2+: WorkerW и SHELLDLL_DefView — дети Progman
        IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        IntPtr childWorker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (defView != IntPtr.Zero && childWorker != IntPtr.Zero) { insertAfter = defView; return progman; }
        // старая схема: отдельный WorkerW после того, в котором лежат иконки
        IntPtr worker = IntPtr.Zero;
        EnumWindows(delegate (IntPtr top, IntPtr l)
        {
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                worker = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        insertAfter = IntPtr.Zero;
        return worker != IntPtr.Zero ? worker : progman;
    }

    public static void Attach(IntPtr hwnd, IntPtr host, IntPtr insertAfter, Rectangle screenBounds)
    {
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style = (style & ~WS_POPUP) | WS_CHILD;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
        SetParent(hwnd, host);
        var pt = new POINT { X = screenBounds.X, Y = screenBounds.Y };
        MapWindowPoints(IntPtr.Zero, host, ref pt, 1);
        const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        SetWindowPos(hwnd, insertAfter, pt.X, pt.Y, screenBounds.Width, screenBounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);

    public static Point Cursor() { POINT p; GetCursorPos(out p); return new Point(p.X, p.Y); }
    public static bool LeftDown() { return (GetAsyncKeyState(0x01) & 0x8000) != 0; }
    public static double Dpi(IntPtr h) { uint d = GetDpiForWindow(h); return d == 0 ? 96 : d; }

    // курсор над рабочим столом (иконки, фон), а не над окном программы
    public static bool OverDesktop(Point pt)
    {
        IntPtr w = WindowFromPoint(new POINT { X = pt.X, Y = pt.Y });
        if (w == IntPtr.Zero) return false;
        IntPtr root = GetAncestor(w, 2); // GA_ROOT
        var sb = new StringBuilder(64); GetClassName(root, sb, 64);
        string cls = sb.ToString();
        return cls == "Progman" || cls == "WorkerW";
    }

    public static void KeepOnTop(IntPtr hwnd, IntPtr insertAfter)
    {
        // SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
        SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, 0x2 | 0x1 | 0x10);
    }

    // пауза, когда что-то запущено на весь экран (игры, видео) или идёт заставка
    public static bool ShouldPause()
    {
        bool ss = false;
        SystemParametersInfo(0x0072, 0, ref ss, 0); // SPI_GETSCREENSAVERRUNNING
        if (ss) return true;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var sb = new StringBuilder(64); GetClassName(fg, sb, 64);
        string cls = sb.ToString();
        if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
        // развёрнутые окна с заголовком — не полноэкранный режим (панель задач у нас автоскрываемая)
        if (IsZoomed(fg)) return false;
        long style = GetWindowLongPtr(fg, GWL_STYLE).ToInt64();
        if ((style & 0x00C00000L) == 0x00C00000L) return false; // WS_CAPTION
        RECT r; if (!GetWindowRect(fg, out r)) return false;
        Rectangle b = Screen.FromHandle(fg).Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }
}

// Лента активности Claude Code из ~/.claude/projects/*/*.jsonl
static class ClaudeFeed
{
    static readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    class Cached { public long Len; public DateTime Write; public Dictionary<string, object> Data; }
    static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>();

    public static List<object> Build()
    {
        var result = new List<object>();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(root)) return result;
        // кандидаты — логи, которые трогали за последние 6 часов (фоновые задачи бывают долгими)
        var candidates = new List<Dictionary<string, object>>();
        foreach (var dir in Directory.GetDirectories(root))
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.jsonl"))
            {
                if ((DateTime.Now - f.LastWriteTime).TotalHours > 6) continue;
                Cached c;
                if (!cache.TryGetValue(f.FullName, out c) || c.Len != f.Length)
                {
                    c = new Cached { Len = f.Length, Write = f.LastWriteTime, Data = Parse(f) };
                    cache[f.FullName] = c;
                }
                // свежесть — по последнему настоящему событию, а не по служебным записям в логе
                long ticks = (long)c.Data["lastEvent"];
                if (ticks == 0) continue;
                double age = (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
                int agents = ActiveAgents(f), bg = BackgroundTasks(f, c.Data);
                string state = State(c.Data, age, agents, bg);
                bool working = state == "working";
                // простаивающие показываем 30 минут, ждущие ответа — 3 часа
                if (state == "idle" && age > 30 * 60) continue;
                if (!working && age > 3 * 3600) continue;
                var d = new Dictionary<string, object>(c.Data);
                foreach (var k in new[] { "bgPending", "bgFinished", "lastEvent", "lastStop", "lastMain", "openTools" }) d.Remove(k);
                d["age"] = (int)age; d["agents"] = agents; d["bg"] = bg; d["working"] = working; d["state"] = state;
                d["app"] = AppSessions.Lookup((string)c.Data["id"]) ?? "";
                candidates.Add(d);
            }
        // сначала работающие, потом самые свежие
        candidates.Sort((a, b) =>
        {
            int w = Rank((string)a["state"]).CompareTo(Rank((string)b["state"]));
            return w != 0 ? w : ((int)a["age"]).CompareTo((int)b["age"]);
        });
        for (int i = 0; i < candidates.Count && i < 4; i++) result.Add(candidates[i]);
        return result;
    }

    static int Rank(string s) { return s == "working" ? 0 : s == "asking" ? 1 : s == "waiting" ? 2 : 3; }

    // working — Claude что-то делает; asking — задал вопрос; waiting — закончил ход и ждёт; idle — прервано/забыто
    static string State(Dictionary<string, object> d, double age, int agents, int bg)
    {
        string stop = (string)d["lastStop"], main = (string)d["lastMain"];
        var open = (List<string>)d["openTools"];
        // вопрос пользователю важнее фоновых задач: Claude стоит и ждёт ответа
        bool asking = open.Contains("AskUserQuestion") || open.Contains("ExitPlanMode");
        if (asking && main != "prompt") return "asking";
        if (agents > 0 || bg > 0) return "working";
        if (main == "assistant" && stop == "end_turn" && open.Count == 0) return "waiting";
        if (main == "prompt") return age < 600 ? "working" : "idle";          // запрос отправлен, ответ вот-вот начнётся
        if (open.Count > 0) return age < 1200 ? "working" : "idle";          // долгая команда или ждёт разрешения
        return age < 120 ? "working" : "idle";                               // посреди ответа
    }

    static int ActiveAgents(FileInfo f)
    {
        string sub = Path.Combine(f.DirectoryName, Path.GetFileNameWithoutExtension(f.Name), "subagents");
        if (!Directory.Exists(sub)) return 0;
        int n = 0;
        foreach (var a in new DirectoryInfo(sub).GetFiles("*.jsonl"))
            if ((DateTime.Now - a.LastWriteTime).TotalSeconds < 30) n++;
        return n;
    }

    // фоновые задачи (рендер, долгие команды): запущены, но уведомления о завершении ещё не было
    static int BackgroundTasks(FileInfo f, Dictionary<string, object> data)
    {
        string tasks = Path.Combine(Path.GetTempPath(), "claude", f.Directory.Name, Path.GetFileNameWithoutExtension(f.Name), "tasks");
        if (!Directory.Exists(tasks)) return 0;
        var running = new HashSet<string>();
        object ids; 
        if (data.TryGetValue("bgPending", out ids))
            foreach (var id in (List<string>)ids)
                if (File.Exists(Path.Combine(tasks, id + ".output"))) running.Add(id);
        object fin; data.TryGetValue("bgFinished", out fin);
        var finished = fin as HashSet<string> ?? new HashSet<string>();
        foreach (var o in new DirectoryInfo(tasks).GetFiles("*.output"))
        {
            string id = Path.GetFileNameWithoutExtension(o.Name);
            if (!finished.Contains(id) && (DateTime.Now - o.LastWriteTime).TotalSeconds < 60) running.Add(id);
        }
        return running.Count;
    }

    static readonly Regex launchRe = new Regex(@"running in background with ID: ([A-Za-z0-9_-]+)");
    static readonly Regex doneRe = new Regex(@"<task-id>([A-Za-z0-9_-]+)</task-id>.{0,800}?<status>(\w+)</status>");
    static readonly Regex tsRe = new Regex("\"timestamp\":\"([^\"]+)\"");

    static string Tail(string path, int bytes)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            long start = Math.Max(0, fs.Length - bytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[fs.Length - start];
            int read = 0;
            while (read < buf.Length) { int r = fs.Read(buf, read, buf.Length - read); if (r <= 0) break; read += r; }
            string s = Encoding.UTF8.GetString(buf, 0, read);
            if (start > 0) { int nl = s.IndexOf('\n'); s = nl >= 0 ? s.Substring(nl + 1) : ""; }
            return s;
        }
    }

    static string Clean(string s, int max)
    {
        if (s == null) return "";
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > max ? s.Substring(0, max - 1) + "…" : s;
    }

    static string Str(Dictionary<string, object> d, string key)
    {
        object v; return d != null && d.TryGetValue(key, out v) && v is string ? (string)v : null;
    }

    static Dictionary<string, object> Parse(FileInfo f)
    {
        string title = null;
        var events = new List<Dictionary<string, object>>();
        var launched = new List<string>();
        long lastEvent = 0;
        var finished = new HashSet<string>();
        string lastStop = null, lastMain = null;                 // lastMain: prompt | assistant | result
        var openTools = new Dictionary<string, string>();        // tool_use id -> имя, ещё без результата
        foreach (var line in Tail(f.FullName, 1024 * 1024).Split('\n'))
        {
            if (line.Length < 2 || line[0] != '{') continue;
            foreach (Match m in doneRe.Matches(line)) finished.Add(m.Groups[1].Value);
            var lm = launchRe.Match(line);
            if (lm.Success)
            {
                // запуски старше 6 часов не считаем — скорее всего, это забытые серверы
                var tm = tsRe.Match(line); DateTime lt;
                if (!tm.Success || !DateTime.TryParse(tm.Groups[1].Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out lt) || (DateTime.UtcNow - lt.ToUniversalTime()).TotalHours < 6)
                    launched.Add(lm.Groups[1].Value);
            }
            Dictionary<string, object> o;
            try { o = js.DeserializeObject(line) as Dictionary<string, object>; } catch { continue; }
            if (o == null) continue;
            string type = Str(o, "type");
            if (type == "custom-title") { title = Str(o, "customTitle") ?? title; continue; }
            if (type != "assistant" && type != "user") continue;
            object sc; if (o.TryGetValue("isSidechain", out sc) && sc is bool && (bool)sc) continue;
            string ts = Str(o, "timestamp");
            string time = "";
            DateTime dt; if (ts != null && DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out dt)) { time = dt.ToLocalTime().ToString("HH:mm"); lastEvent = Math.Max(lastEvent, dt.ToUniversalTime().Ticks); }
            var msg = o.ContainsKey("message") ? o["message"] as Dictionary<string, object> : null;
            if (msg == null) continue;
            object content; msg.TryGetValue("content", out content);
            if (type == "user")
            {
                object meta; if (o.TryGetValue("isMeta", out meta) && meta is bool && (bool)meta) continue;
                if (content is string)
                {
                    var t = (string)content; if (!t.StartsWith("<")) Add(events, time, "user", "", Widgets.L("новый запрос", "new prompt"));
                    lastMain = "prompt"; openTools.Clear();
                }
                else if (content is object[])
                {
                    bool prompt = false, result = false;
                    foreach (var it in (object[])content)
                    {
                        var item = it as Dictionary<string, object>;
                        string itype = Str(item, "type");
                        if (itype == "tool_result") { result = true; string tid = Str(item, "tool_use_id"); if (tid != null) openTools.Remove(tid); }
                        else if (itype == "text") { prompt = true; if (!(Str(item, "text") ?? "").StartsWith("<")) { Add(events, time, "user", "", Widgets.L("новый запрос", "new prompt")); } }
                    }
                    if (prompt && !result) { lastMain = "prompt"; openTools.Clear(); }
                    else if (result) lastMain = "result";
                }
                continue;
            }
            lastMain = "assistant";
            lastStop = Str(msg, "stop_reason") ?? lastStop;
            var arr = content as object[];
            if (arr == null) continue;
            foreach (var it in arr)
            {
                var item = it as Dictionary<string, object>;
                string itype = Str(item, "type");
                if (itype == "text")
                {
                    string t = Clean(Str(item, "text"), 110);
                    if (t.Length > 0) Add(events, time, "text", "", t);
                }
                else if (itype == "tool_use")
                {
                    string name = Str(item, "name") ?? "tool";
                    string toolId = Str(item, "id"); if (toolId != null) openTools[toolId] = name;
                    int k = name.LastIndexOf("__"); if (k >= 0) name = name.Substring(k + 2);
                    var input = item.ContainsKey("input") ? item["input"] as Dictionary<string, object> : null;
                    string d = Str(input, "description") ?? Str(input, "command") ?? Str(input, "file_path")
                        ?? Str(input, "pattern") ?? Str(input, "url") ?? Str(input, "query") ?? Str(input, "skill") ?? Str(input, "prompt") ?? "";
                    if (d.Contains("\\") && d.Length > 40 && !d.Contains(" ")) d = Path.GetFileName(d);
                    Add(events, time, "tool", name, Clean(d, 90));
                }
            }
        }
        var r = new Dictionary<string, object>();
        r["id"] = Path.GetFileNameWithoutExtension(f.Name);
        r["title"] = Clean(title ?? Widgets.L("сессия", "session"), 60);
        r["events"] = events;
        var pending = new List<string>();
        foreach (var id in launched) if (!finished.Contains(id) && !pending.Contains(id)) pending.Add(id);
        r["bgPending"] = pending;
        r["bgFinished"] = finished;
        r["lastEvent"] = lastEvent;
        r["lastStop"] = lastStop ?? "";
        r["lastMain"] = lastMain ?? "";
        r["openTools"] = new List<string>(openTools.Values);
        return r;
    }

    static void Add(List<Dictionary<string, object>> list, string time, string kind, string name, string text)
    {
        var e = new Dictionary<string, object>();
        e["time"] = time; e["kind"] = kind; e["name"] = name; e["text"] = text;
        list.Add(e);
        if (list.Count > 5) list.RemoveAt(0);
    }
}

// Соответствие ID сессии из лога (cliSessionId) и ID сессии в приложении Claude (local_…)
static class AppSessions
{
    static readonly Dictionary<string, string> map = new Dictionary<string, string>();
    static readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>();
    static DateTime lastScan = DateTime.MinValue;
    static readonly Regex cliRe = new Regex("\"cliSessionId\"\\s*:\\s*\"([0-9a-f-]{36})\"");
    static readonly Regex appRe = new Regex("\"sessionId\"\\s*:\\s*\"(local_[0-9a-f-]{36})\"");

    public static string Lookup(string cliId)
    {
        if ((DateTime.Now - lastScan).TotalSeconds > 30) { lastScan = DateTime.Now; try { Scan(); } catch { } }
        string v; return map.TryGetValue(cliId, out v) ? v : null;
    }

    static IEnumerable<string> Roots()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(roaming, "Claude", "claude-code-sessions");
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
            foreach (var p in Directory.GetDirectories(packages, "Claude_*"))
                yield return Path.Combine(p, "LocalCache", "Roaming", "Claude", "claude-code-sessions");
    }

    static void Scan()
    {
        foreach (var root in Roots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.GetFiles(root, "local_*.json", SearchOption.AllDirectories))
            {
                var w = File.GetLastWriteTime(file);
                DateTime prev;
                if (seen.TryGetValue(file, out prev) && prev == w) continue;
                seen[file] = w;
                string head;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buf = new byte[Math.Min(8192, (int)fs.Length)];
                    int n = fs.Read(buf, 0, buf.Length);
                    head = Encoding.UTF8.GetString(buf, 0, n);
                }
                var cm = cliRe.Match(head); var am = appRe.Match(head);
                if (cm.Success && am.Success) map[cm.Groups[1].Value] = am.Groups[1].Value;
            }
        }
    }
}