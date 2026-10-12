// Виджеты Lofi Desk: плеер, система, погода, быстрый запуск, заметки.
// Данные собираются в фоновых потоках и отправляются странице через Post (в UI-поток).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

static class Widgets
{
    static readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly Dictionary<string, string> lastJson = new Dictionary<string, string>();
    static SynchronizationContext ui;
    static Action<string> send;
    static Dictionary<string, string> settings;
    static string baseDir;
    static readonly CultureInfo inv = CultureInfo.InvariantCulture;

    // язык подписей хоста (трей, всплывающие окна) — тот же, что у страницы: lang=en | ru
    public static bool En = true;
    public static void Configure(Dictionary<string, string> cfg) { string l; En = !(cfg.TryGetValue("lang", out l) && l.Trim().ToLowerInvariant() == "ru"); }
    public static string L(string ru, string en) { return En ? en : ru; }

    public static void Start(Action<string> broadcast, Dictionary<string, string> cfg, string dir)
    {
        ui = SynchronizationContext.Current;
        send = broadcast; settings = cfg; baseDir = dir;
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Background(MediaLoop);
        Background(SysLoop);
        Background(WeatherLoop);
        Background(AudioLoop);
        Background(NetLoop);
        WatchDownloads();
        LoadNotes();
        var lt = new System.Windows.Forms.Timer { Interval = 20000 };
        lt.Tick += delegate { RefreshLaunch(false); };
        lt.Start();
        RefreshLaunch(true);
    }

    static void Background(ThreadStart fn)
    {
        var t = new Thread(delegate () { try { fn(); } catch (Exception ex) { LogErr(ex); } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }

    static void LogErr(Exception ex)
    {
        try
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LofiDesk");
            Directory.CreateDirectory(d);
            File.AppendAllText(Path.Combine(d, "log.txt"), DateTime.Now.ToString("s") + " [widgets] " + ex + "\r\n\r\n");
        }
        catch { }
    }

    // отправить (и запомнить для страниц, которые загрузятся позже)
    static void Push(string kind, object payload)
    {
        var d = payload as Dictionary<string, object> ?? new Dictionary<string, object>();
        d["type"] = kind;
        string s = js.Serialize(d);
        lock (lastJson)
        {
            string prev;
            if (lastJson.TryGetValue(kind, out prev) && prev == s) return;
            lastJson[kind] = s;
        }
        if (ui != null) ui.Post(delegate { send(s); }, null); else send(s);
    }

    public static void ResendAll()
    {
        List<string> all;
        lock (lastJson) all = new List<string>(lastJson.Values);
        foreach (var s in all) send(s);
    }

    static string S(object v) { return Convert.ToString(v, inv); }

    // ---------------- плеер (Windows media session: Spotify, браузер, любые плееры) ----------------
    static GlobalSystemMediaTransportControlsSessionManager mgr;
    static string artKey, artData;

    static T Await<T>(IAsyncOperation<T> op)
    {
        var ev = new ManualResetEvent(false);
        op.Completed = delegate { ev.Set(); };
        if (!ev.WaitOne(4000)) throw new TimeoutException();
        return op.GetResults();
    }

    static void MediaLoop()
    {
        while (true)
        {
            try { MediaTick(); }
            catch (Exception ex) { LogErr(ex); mgr = null; Thread.Sleep(3000); }
            Thread.Sleep(1000);
        }
    }

    static void MediaTick()
    {
        if (mgr == null) mgr = Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        var s = mgr.GetCurrentSession();
        var d = new Dictionary<string, object>();
        if (s == null) { d["has"] = false; Push("media", d); return; }
        var p = Await(s.TryGetMediaPropertiesAsync());
        var tl = s.GetTimelineProperties();
        var pb = s.GetPlaybackInfo();
        bool playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        double pos = tl.Position.TotalSeconds;
        if (playing && tl.LastUpdatedTime.Year > 2000) pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
        double dur = (tl.EndTime - tl.StartTime).TotalSeconds;
        string key = (p.Title ?? "") + "|" + (p.Artist ?? "");
        if (key != artKey)
        {
            artKey = key; artData = "";
            try
            {
                if (p.Thumbnail != null)
                {
                    var st = Await(p.Thumbnail.OpenReadAsync());
                    var reader = new DataReader(st.GetInputStreamAt(0));
                    uint n = Await(reader.LoadAsync((uint)Math.Min(st.Size, 4 * 1024 * 1024)));
                    var bytes = new byte[n]; reader.ReadBytes(bytes);
                    artData = "data:" + (string.IsNullOrEmpty(st.ContentType) ? "image/png" : st.ContentType) + ";base64," + Convert.ToBase64String(bytes);
                }
            }
            catch (Exception ex) { LogErr(ex); }
        }
        string app = s.SourceAppUserModelId ?? "";
        d["has"] = true;
        d["title"] = p.Title ?? ""; d["artist"] = p.Artist ?? ""; d["album"] = p.AlbumTitle ?? "";
        d["app"] = app.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) >= 0 ? "Spotify" : app.Contains("!") ? app.Substring(app.IndexOf('!') + 1) : Path.GetFileNameWithoutExtension(app);
        d["playing"] = playing;
        d["pos"] = Math.Round(Math.Max(0, pos)); d["dur"] = Math.Round(Math.Max(0, dur));
        d["art"] = artData;
        bool? sh = pb.IsShuffleActive; d["shuffle"] = sh.HasValue && sh.Value;
        var rp = pb.AutoRepeatMode; d["repeat"] = rp.HasValue ? rp.Value.ToString() : "None";
        d["canShuffle"] = pb.Controls.IsShuffleEnabled; d["canRepeat"] = pb.Controls.IsRepeatEnabled; d["canSeek"] = pb.Controls.IsPlaybackPositionEnabled;
        d["vol"] = Math.Round(volume, 2); d["muted"] = muted;
        mediaAumid = app; mediaTitle = (p.Artist ?? "") + " — " + (p.Title ?? ""); mediaPlaying = playing;
        Push("media", d);
    }

    static string mediaAumid = "", mediaTitle = "";
    static volatile bool mediaPlaying;

    public static void MediaCommand(string cmd, double arg)
    {
        // действия, которым не нужна сессия плеера
        if (cmd == "lyrics" && mediaTitle.Length > 3)
        {
            try { Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(mediaTitle + L(" текст песни", " lyrics"))) { UseShellExecute = true }); } catch (Exception ex) { LogErr(ex); }
            return;
        }
        if (cmd == "copy") { string t = mediaTitle; if (ui != null) ui.Post(delegate { try { Clipboard.SetText(t); } catch { } }, null); return; }
        if (cmd == "open" && mediaAumid.Length > 0)
        {
            try { Process.Start(new ProcessStartInfo("shell:AppsFolder\\" + mediaAumid) { UseShellExecute = true }); } catch (Exception ex) { LogErr(ex); }
            return;
        }
        if (cmd == "vol" || cmd == "mute") { Background(delegate () { SetVolume(cmd == "mute" ? -1 : arg); }); return; }
        Background(delegate ()
        {
            if (mgr == null) mgr = Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
            var s = mgr.GetCurrentSession();
            if (s == null) return;
            var pb = s.GetPlaybackInfo();
            if (cmd == "toggle") Await(s.TryTogglePlayPauseAsync());
            else if (cmd == "next") Await(s.TrySkipNextAsync());
            else if (cmd == "prev") Await(s.TrySkipPreviousAsync());
            else if (cmd == "shuffle") { bool? sh = pb.IsShuffleActive; Await(s.TryChangeShuffleActiveAsync(!(sh.HasValue && sh.Value))); }
            else if (cmd == "repeat")
            {
                var rp = pb.AutoRepeatMode;
                var cur = rp.HasValue ? rp.Value : Windows.Media.MediaPlaybackAutoRepeatMode.None;
                var next = cur == Windows.Media.MediaPlaybackAutoRepeatMode.None ? Windows.Media.MediaPlaybackAutoRepeatMode.List
                         : cur == Windows.Media.MediaPlaybackAutoRepeatMode.List ? Windows.Media.MediaPlaybackAutoRepeatMode.Track
                         : Windows.Media.MediaPlaybackAutoRepeatMode.None;
                Await(s.TryChangeAutoRepeatModeAsync(next));
            }
            else if (cmd == "seek") Await(s.TryChangePlaybackPositionAsync((long)(arg * 10000000)));
            Thread.Sleep(250); MediaTick();
        });
    }

    // ---------------- звук: громкость и уровень сигнала (Windows Core Audio) ----------------
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator { int EnumAudioEndpoints(int a, int b, out IntPtr c); [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice dev); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice { [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr p); int UnregisterControlChangeNotify(IntPtr p); int GetChannelCount(out uint c);
        int SetMasterVolumeLevel(float l, ref Guid g); int SetMasterVolumeLevelScalar(float l, ref Guid g);
        int GetMasterVolumeLevel(out float l); int GetMasterVolumeLevelScalar(out float l);
        int SetChannelVolumeLevel(uint ch, float l, ref Guid g); int SetChannelVolumeLevelScalar(uint ch, float l, ref Guid g);
        int GetChannelVolumeLevel(uint ch, out float l); int GetChannelVolumeLevelScalar(uint ch, out float l);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid g); int GetMute([MarshalAs(UnmanagedType.Bool)] out bool m);
    }
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation { int GetPeakValue(out float p); }

    static volatile float volume = 0.5f;
    static volatile bool muted;

    static T Endpoint<T>(Guid iid) where T : class
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev;
        if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return null; // eRender, eMultimedia
        object o; dev.Activate(ref iid, 23, IntPtr.Zero, out o);                        // CLSCTX_ALL
        return o as T;
    }

    static void SetVolume(double v)
    {
        try
        {
            var ev = Endpoint<IAudioEndpointVolume>(typeof(IAudioEndpointVolume).GUID);
            if (ev == null) return;
            var g = Guid.Empty;
            if (v < 0) { bool m; ev.GetMute(out m); ev.SetMute(!m, ref g); }
            else ev.SetMasterVolumeLevelScalar((float)Math.Max(0, Math.Min(1, v)), ref g);
            float lv; ev.GetMasterVolumeLevelScalar(out lv); volume = lv;
            bool mm; ev.GetMute(out mm); muted = mm;
            MediaTick();
        }
        catch (Exception ex) { LogErr(ex); }
    }

    static void AudioLoop()
    {
        IAudioMeterInformation meter = null; IAudioEndpointVolume vol = null;
        int tick = 0; float last = -1; int sinceSend = 0;
        while (true)
        {
            Thread.Sleep(50);
            try
            {
                // раз в 5 секунд переподключаемся: могли сменить устройство вывода
                if (meter == null || tick++ % 100 == 0)
                {
                    meter = Endpoint<IAudioMeterInformation>(typeof(IAudioMeterInformation).GUID);
                    vol = Endpoint<IAudioEndpointVolume>(typeof(IAudioEndpointVolume).GUID);
                }
                if (vol != null && tick % 10 == 0) { float lv; vol.GetMasterVolumeLevelScalar(out lv); volume = lv; bool m; vol.GetMute(out m); muted = m; }
                if (meter == null || !mediaPlaying) { if (last != 0) { last = 0; SendRaw("{\"type\":\"peak\",\"v\":0}"); } continue; }
                float pk; meter.GetPeakValue(out pk);
                sinceSend++;
                if (Math.Abs(pk - last) > 0.01 || sinceSend > 10)
                {
                    last = pk; sinceSend = 0;
                    SendRaw("{\"type\":\"peak\",\"v\":" + pk.ToString("0.000", inv) + "}");
                }
            }
            catch (Exception ex) { LogErr(ex); meter = null; vol = null; Thread.Sleep(3000); }
        }
    }

    static void SendRaw(string s) { if (ui != null) ui.Post(delegate { send(s); }, null); }

    // ---------------- система ----------------
    [StructLayout(LayoutKind.Sequential)]
    class MEMORYSTATUSEX { public uint dwLength = 64; public uint dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);

    static void SysLoop()
    {
        PerformanceCounter cpu = null;
        try { cpu = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); cpu.NextValue(); }
        catch { try { cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); cpu.NextValue(); } catch { cpu = null; } }
        string smi = FindNvidiaSmi();
        int tick = 0; double gpu = -1, gtemp = -1, vram = -1;
        while (true)
        {
            Thread.Sleep(2000);
            try
            {
                var d = new Dictionary<string, object>();
                d["cpu"] = cpu != null ? Math.Round(Math.Min(100, cpu.NextValue())) : -1;
                var m = new MEMORYSTATUSEX(); GlobalMemoryStatusEx(m);
                d["ram"] = m.dwMemoryLoad;
                d["ramUsed"] = Math.Round((m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0, 1);
                d["idle"] = Math.Round(MouseIdleSeconds());
                if (smi != null && tick++ % 2 == 0)
                {
                    var o = Run(smi, "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits");
                    var parts = (o ?? "").Split(',');
                    if (parts.Length >= 4)
                    {
                        gpu = double.Parse(parts[0].Trim(), inv); gtemp = double.Parse(parts[1].Trim(), inv);
                        vram = Math.Round(100 * double.Parse(parts[2].Trim(), inv) / double.Parse(parts[3].Trim(), inv));
                    }
                }
                d["gpu"] = gpu; d["gpuTemp"] = gtemp; d["vram"] = vram;
                Push("sys", d);
                // режим стрима: запущен OBS
                var st = new Dictionary<string, object>();
                st["on"] = Process.GetProcessesByName("obs64").Length > 0 || Process.GetProcessesByName("obs32").Length > 0;
                Push("stream", st);
                Badges();
                Usage();
            }
            catch (Exception ex) { LogErr(ex); Thread.Sleep(5000); }
        }
    }

    // лимиты подписки Claude: 5-часовое окно — из кэша приложения Claude (AppUsage),
    // недельное — из строки состояния Claude Code в терминале (statusline.ps1 → usage.json), если она запускалась
    static int usageTick;
    static string usageLast;
    static void Usage()
    {
        if (usageTick++ % 15 != 0) return;                       // раз в ~30 секунд
        var d = new Dictionary<string, object>();
        try
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LofiDesk", "usage.json");
            if (File.Exists(p))
            {
                var s = js.DeserializeObject(File.ReadAllText(p, Encoding.UTF8)) as Dictionary<string, object>;
                if (s != null) foreach (var k in new[] { "five_hour", "seven_day" }) if (s.ContainsKey(k)) d[k] = s[k];
            }
            // чтение кэша приложения Claude недокументировано — только если пользователь сам включил limits=1
            string lim; var app = settings.TryGetValue("limits", out lim) && lim == "1" ? AppUsage.Read() : null;
            if (app != null)
            {
                object cur; long curReset = 0; double curPct = -1;
                if (d.TryGetValue("five_hour", out cur)) { var cd = (Dictionary<string, object>)cur; curReset = Convert.ToInt64(cd["resets"]); curPct = Convert.ToDouble(cd["pct"]); }
                if ((long)app[0] > curReset || ((long)app[0] == curReset && app[1] * 100 > curPct))
                {
                    var w = new Dictionary<string, object>(); w["pct"] = Math.Round(app[1] * 100); w["resets"] = (long)app[0];
                    d["five_hour"] = w;
                }
            }
        }
        catch (Exception ex) { LogErr(ex); }
        if (d.Count == 0) return;
        string sig = js.Serialize(d);
        if (sig == usageLast) return;
        usageLast = sig;
        Push("usage", d);
    }
    // непрочитанное: многие мессенджеры пишут «(N) …» в заголовке окна (Telegram, Discord)
    delegate bool EnumWinProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWinProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    static readonly System.Text.RegularExpressions.Regex badgeRe = new System.Text.RegularExpressions.Regex(@"^\s*\((\d{1,5})\)");

    static void Badges()
    {
        var counts = new Dictionary<string, object>();
        foreach (var n in new[] { "telegram", "discord" }) counts[n] = 0;
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
            var m = badgeRe.Match(sb.ToString());
            if (!m.Success) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            try
            {
                string name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
                int n = int.Parse(m.Groups[1].Value);
                object prev; if (!counts.TryGetValue(name, out prev) || (int)prev < n) counts[name] = n;
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        var d = new Dictionary<string, object>(); d["items"] = counts;
        Push("badges", d);
    }

    // значок в трее: уведомления помодоро и быстрое меню
    static NotifyIcon tray;
    public static void Tray(Action restart)
    {
        try
        {
            tray = new NotifyIcon { Text = "Lofi Desk", Icon = MascotIcon(), Visible = true };
            var menu = new ContextMenuStrip();
            // режим «я ушёл» — тот же, что кнопкой на обоях
            menu.Items.Add(L("🚪 Я ушёл", "🚪 I'm away"), null, delegate { send("{\"type\":\"awaycmd\",\"on\":true}"); });
            menu.Items.Add(L("🏠 Я вернулся", "🏠 I'm back"), null, delegate { send("{\"type\":\"awaycmd\",\"on\":false}"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L("Перезапустить обои", "Restart wallpaper"), null, delegate { restart(); });
            menu.Items.Add(L("Открыть папку Lofi Desk", "Open Lofi Desk folder"), null, delegate { Process.Start(new ProcessStartInfo(baseDir) { UseShellExecute = true }); });
            menu.Items.Add(L("Настройки (settings.ini)", "Settings (settings.ini)"), null, delegate { Process.Start(new ProcessStartInfo(Path.Combine(baseDir, "settings.ini")) { UseShellExecute = true }); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L("Выключить живые обои", "Quit live wallpaper"), null, delegate { tray.Visible = false; Application.Exit(); });
            tray.ContextMenuStrip = menu;
        }
        catch (Exception ex) { LogErr(ex); }
    }

    public static void Notify(string title, string text)
    {
        if (tray == null) return;
        try { tray.ShowBalloonTip(6000, title, text, ToolTipIcon.None); } catch (Exception ex) { LogErr(ex); }
    }

    public static void HideTray() { if (tray != null) { tray.Visible = false; tray.Dispose(); } }

    // маленький пиксельный Claude для трея
    static Icon MascotIcon()
    {
        var b = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(b))
        {
            g.Clear(Color.Transparent);
            var o = new SolidBrush(Color.FromArgb(217, 119, 87)); var k = new SolidBrush(Color.FromArgb(27, 15, 11));
            // туловище 12×4 «пикселя» по 2 px ширины и 4 px высоты, как в терминале
            g.FillRectangle(o, 4, 6, 24, 16);
            g.FillRectangle(o, 0, 14, 4, 4); g.FillRectangle(o, 28, 14, 4, 4);
            foreach (int x in new[] { 6, 10, 20, 24 }) g.FillRectangle(o, x, 22, 2, 6);
            g.FillRectangle(k, 8, 10, 2, 4); g.FillRectangle(k, 22, 10, 2, 4);
        }
        return Icon.FromHandle(b.GetHicon());
    }

    static string FindNvidiaSmi()
    {
        foreach (var p in new[] { Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"), @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" })
            if (File.Exists(p)) return p;
        return null;
    }

    static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using (var pr = Process.Start(psi))
        {
            string o = pr.StandardOutput.ReadToEnd();
            pr.WaitForExit(3000);
            return o;
        }
    }

    // ---------------- погода (Open-Meteo, без ключа) ----------------
    static void WeatherLoop()
    {
        while (true)
        {
            int wait = 15 * 60 * 1000;
            try { WeatherTick(); }
            catch (Exception ex) { LogErr(ex); wait = 2 * 60 * 1000; }
            Thread.Sleep(wait);
        }
    }

    static void WeatherTick()
    {
        string lat, lon;
        if (!settings.TryGetValue("lat", out lat) || !settings.TryGetValue("lon", out lon) || lat == "" || lon == "") return;
        string url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat + "&longitude=" + lon
            + "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,is_day,relative_humidity_2m"
            + "&hourly=temperature_2m,weather_code,precipitation_probability"
            + "&daily=temperature_2m_max,temperature_2m_min,weather_code,sunrise,sunset,precipitation_probability_max"
            + "&timezone=auto&forecast_days=2";
        string body;
        using (var wc = new WebClient()) { wc.Encoding = Encoding.UTF8; wc.Headers["User-Agent"] = "LofiDesk/1.0"; body = wc.DownloadString(url); }
        var o = js.DeserializeObject(body) as Dictionary<string, object>;
        var cur = (Dictionary<string, object>)o["current"];
        var hourly = (Dictionary<string, object>)o["hourly"];
        var daily = (Dictionary<string, object>)o["daily"];
        var d = new Dictionary<string, object>();
        d["city"] = settings.ContainsKey("city") ? settings["city"] : "";
        d["t"] = cur["temperature_2m"]; d["feels"] = cur["apparent_temperature"]; d["code"] = cur["weather_code"];
        d["isDay"] = cur["is_day"]; d["wind"] = cur["wind_speed_10m"]; d["hum"] = cur["relative_humidity_2m"];
        d["hi"] = ((object[])daily["temperature_2m_max"])[0]; d["lo"] = ((object[])daily["temperature_2m_min"])[0];
        d["pp"] = ((object[])daily["precipitation_probability_max"])[0];
        d["tHi"] = ((object[])daily["temperature_2m_max"])[1]; d["tLo"] = ((object[])daily["temperature_2m_min"])[1];
        d["tCode"] = ((object[])daily["weather_code"])[1];
        d["sunset"] = S(((object[])daily["sunset"])[0]);
        d["sunrise"] = S(((object[])daily["sunrise"])[0]);
        d["sunrise2"] = S(((object[])daily["sunrise"])[1]);
        // ближайшие 12 часов
        var times = (object[])hourly["time"]; var ht = (object[])hourly["temperature_2m"];
        var hc = (object[])hourly["weather_code"]; var hp = (object[])hourly["precipitation_probability"];
        string nowKey = DateTime.Now.ToString("yyyy-MM-dd'T'HH':00'", inv);
        var hours = new List<object>();
        for (int i = 0; i < times.Length && hours.Count < 12; i++)
        {
            if (string.CompareOrdinal(S(times[i]), nowKey) < 0) continue;
            var h = new Dictionary<string, object>();
            h["h"] = S(times[i]).Substring(11, 2); h["t"] = ht[i]; h["code"] = hc[i]; h["pp"] = hp[i];
            hours.Add(h);
        }
        d["hours"] = hours;
        d["at"] = DateTime.Now.ToString("HH:mm");
        Push("weather", d);
    }

    // ---------------- быстрый запуск: ярлыки из папки launch ----------------
    static readonly List<string> launchItems = new List<string>();
    static string launchStamp;

    static void RefreshLaunch(bool force)
    {
        try
        {
            string dir = Path.Combine(baseDir, "launch");
            if (!Directory.Exists(dir)) return;
            var files = new List<string>();
            foreach (var f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".lnk" || ext == ".url" || ext == ".exe" || ext == ".app") files.Add(f);
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            foreach (var f in files) sb.Append(f).Append(File.GetLastWriteTimeUtc(f).Ticks).Append(';');
            string stamp = sb.ToString();
            if (!force && stamp == launchStamp) return;
            launchStamp = stamp;
            launchItems.Clear();
            var items = new List<object>();
            foreach (var f in files)
            {
                // .app — одна строка с адресом, например shell:AppsFolder\SpotifyAB.SpotifyMusic_…!Spotify
                string target = f;
                if (Path.GetExtension(f).ToLowerInvariant() == ".app") target = File.ReadAllText(f, Encoding.UTF8).Trim();
                launchItems.Add(target);
                var it = new Dictionary<string, object>();
                string name = Path.GetFileNameWithoutExtension(f);
                int dot = name.IndexOf(". "); if (dot > 0 && dot < 3) name = name.Substring(dot + 2); // «1. Spotify» → «Spotify»
                it["name"] = name;
                it["icon"] = IconData(target, 96);
                items.Add(it);
            }
            var d = new Dictionary<string, object>(); d["items"] = items;
            Push("launch", d);
        }
        catch (Exception ex) { LogErr(ex); }
    }

    public static void Launch(int i)
    {
        if (i < 0 || i >= launchItems.Count) return;
        try { Process.Start(new ProcessStartInfo(launchItems[i]) { UseShellExecute = true }); }
        catch (Exception ex) { LogErr(ex); }
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory ppv);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    // иконка в высоком разрешении с прозрачностью
    static string IconData(string path, int size)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            IShellItemImageFactory f;
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
            if (f.GetImage(new SIZE { cx = size, cy = size }, 0x4 | 0x1, out hbm) != 0 || hbm == IntPtr.Zero) return "";
            using (var raw = Image.FromHbitmap(hbm))
            {
                Bitmap result;
                var rect = new Rectangle(0, 0, raw.Width, raw.Height);
                var data = raw.LockBits(rect, ImageLockMode.ReadOnly, raw.PixelFormat);
                try
                {
                    using (var argb = new Bitmap(data.Width, data.Height, data.Stride, PixelFormat.Format32bppArgb, data.Scan0))
                        result = new Bitmap(argb);
                }
                finally { raw.UnlockBits(data); }
                // если альфы нет совсем — берём как есть
                bool anyAlpha = false;
                for (int y = 0; y < result.Height && !anyAlpha; y += 4)
                    for (int x = 0; x < result.Width; x += 4) if (result.GetPixel(x, y).A != 0) { anyAlpha = true; break; }
                if (!anyAlpha) { result.Dispose(); result = new Bitmap(raw); }
                using (result)
                using (var ms = new MemoryStream())
                {
                    result.Save(ms, ImageFormat.Png);
                    return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
        }
        catch (Exception ex) { LogErr(ex); return ""; }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
    }

    // ---------------- сколько пользователь не трогал мышь и клавиатуру ----------------
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    // простой по мыши: программы (запись игр, автоматизация) шлют синтетические нажатия клавиш,
    // и системный счётчик ввода у пользователя никогда не растёт — поэтому смотрим на курсор и кнопки мыши
    static Point lastCursor; static int lastMoveTick = Environment.TickCount;
    static double MouseIdleSeconds()
    {
        Point p = Cursor.Position;
        bool btn = (Control.MouseButtons & (MouseButtons.Left | MouseButtons.Right | MouseButtons.Middle)) != 0;
        if (btn || Math.Abs(p.X - lastCursor.X) > 3 || Math.Abs(p.Y - lastCursor.Y) > 3) { lastCursor = p; lastMoveTick = Environment.TickCount; }
        return unchecked((uint)(Environment.TickCount - lastMoveTick)) / 1000.0;
    }
    static double IdleSeconds()
    {
        var l = new LASTINPUTINFO { cbSize = 8 };
        if (!GetLastInputInfo(ref l)) return 0;
        return unchecked((uint)Environment.TickCount - l.dwTime) / 1000.0;
    }

    // ---------------- интернет: пинг, а если ICMP закрыт — TCP до 1.1.1.1:443 ----------------
    static void NetLoop()
    {
        int fails = 0; bool online = true;
        while (true)
        {
            if (NetOk()) { fails = 0; online = true; }
            else if (++fails >= 2) online = false;
            var d = new Dictionary<string, object>(); d["on"] = online;
            Push("net", d);
            Thread.Sleep(online ? 15000 : 5000);
        }
    }

    static bool NetOk()
    {
        foreach (var h in new[] { "1.1.1.1", "8.8.8.8" })
            try { using (var p = new System.Net.NetworkInformation.Ping()) if (p.Send(h, 2500).Status == System.Net.NetworkInformation.IPStatus.Success) return true; }
            catch { }
        try
        {
            using (var c = new System.Net.Sockets.TcpClient())
            {
                var ar = c.BeginConnect("1.1.1.1", 443, null, null);
                if (ar.AsyncWaitHandle.WaitOne(2500) && c.Connected) { c.EndConnect(ar); return true; }
            }
        }
        catch { }
        return false;
    }

    // ---------------- загрузки: новый файл в «Загрузках» ----------------
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
    static FileSystemWatcher dlWatch;
    static string lastDownload;
    static readonly HashSet<string> dlPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static string DownloadsDir()
    {
        IntPtr p;
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
        {
            string s = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p);
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    static void WatchDownloads()
    {
        try
        {
            string dir = DownloadsDir();
            if (!Directory.Exists(dir)) return;
            dlWatch = new FileSystemWatcher(dir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
            dlWatch.Created += delegate (object s, FileSystemEventArgs e) { DownloadSeen(e.FullPath); };
            dlWatch.Renamed += delegate (object s, RenamedEventArgs e) { DownloadSeen(e.FullPath); };
            dlWatch.EnableRaisingEvents = true;
        }
        catch (Exception ex) { LogErr(ex); }
    }

    static void DownloadSeen(string path)
    {
        string name = Path.GetFileName(path), ext = Path.GetExtension(path).ToLowerInvariant();
        if (name.StartsWith(".") || name.StartsWith("~$") || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return;
        if (Array.IndexOf(new[] { ".crdownload", ".tmp", ".part", ".partial", ".download", ".opdownload" }, ext) >= 0) return;
        lock (dlPending) { if (!dlPending.Add(path)) return; }
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                // браузер сначала создаёт пустую заглушку — ждём, пока размер перестанет меняться и файл отпустят
                long last = -1;
                for (int i = 0; ; i++)
                {
                    Thread.Sleep(2000);
                    if (!File.Exists(path) || i > 1800) return;
                    long len = new FileInfo(path).Length;
                    if (len > 0 && len == last)
                    {
                        try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { } break; }
                        catch { }
                    }
                    last = len;
                }
                lastDownload = path;
                var d = new Dictionary<string, object>();
                d["name"] = name; d["size"] = new FileInfo(path).Length; d["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Push("download", d);
            }
            catch (Exception ex) { LogErr(ex); }
            finally { lock (dlPending) dlPending.Remove(path); }
        });
    }

    // ---------------- действия из меню маскота ----------------
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    public static void DoAction(string what)
    {
        try
        {
            if (what == "lock") LockWorkStation();
            else if (what == "screensaver")       // погасить OLED: своя заставка (основной — чёрный, второй — сцена)
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "/s") { UseShellExecute = false });
            else if (what == "screenshot") Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
            else if (what == "downloads") Process.Start(new ProcessStartInfo(DownloadsDir()) { UseShellExecute = true });
            else if (what == "reveal")
            {
                string p = lastDownload;
                if (p != null && File.Exists(p)) Process.Start("explorer.exe", "/select,\"" + p + "\"");
                else Process.Start(new ProcessStartInfo(DownloadsDir()) { UseShellExecute = true });
            }
            else if (what == "clip-note")
            {
                string t = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                t = Regex.Replace(t ?? "", "\\s+", " ").Trim();
                if (t.Length == 0) { Say(L("в буфере нет текста", "no text in the clipboard")); return; }
                NoteAdd(t);
            }
        }
        catch (Exception ex) { LogErr(ex); }
    }

    // короткая реплика маскота от хоста
    static void Say(string text)
    {
        var d = new Dictionary<string, object>(); d["text"] = text; d["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Push("say", d);
    }

    // ---------------- заметки ----------------
    static List<Dictionary<string, object>> notes = new List<Dictionary<string, object>>();
    static string NotesPath { get { return Path.Combine(baseDir, "notes.json"); } }

    static void LoadNotes()
    {
        try
        {
            if (File.Exists(NotesPath))
            {
                var arr = js.DeserializeObject(File.ReadAllText(NotesPath, Encoding.UTF8)) as object[];
                notes.Clear();
                if (arr != null) foreach (var o in arr) { var d = o as Dictionary<string, object>; if (d != null) notes.Add(d); }
            }
        }
        catch (Exception ex) { LogErr(ex); }
        PushNotes();
    }

    static void SaveNotes()
    {
        try { File.WriteAllText(NotesPath, js.Serialize(notes), new UTF8Encoding(false)); } catch (Exception ex) { LogErr(ex); }
        PushNotes();
    }

    static void PushNotes() { var d = new Dictionary<string, object>(); d["items"] = notes; Push("notes", d); }

    public static void NoteToggle(string id)
    {
        foreach (var n in notes) if (S(n["id"]) == id) n["done"] = !(n.ContainsKey("done") && n["done"] is bool && (bool)n["done"]);
        SaveNotes();
    }

    public static void NoteDelete(string id)
    {
        notes.RemoveAll(n => S(n["id"]) == id);
        SaveNotes();
    }

    static void NoteAdd(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > 120) text = text.Substring(0, 120);
        var n = new Dictionary<string, object>();
        n["id"] = DateTime.UtcNow.Ticks.ToString(inv); n["text"] = text; n["done"] = false;
        notes.Add(n);
        SaveNotes();
    }

    // маленькое поле ввода рядом с доской заметок
    static Form notePopup;
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    public static void ShowNoteInput(Point screenPt, double scale)
    {
        if (notePopup != null && !notePopup.IsDisposed) { notePopup.Activate(); return; }
        int w = (int)(460 * scale), h = (int)(54 * scale);
        var f = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false, TopMost = true, BackColor = Color.FromArgb(255, 140, 110),
            Bounds = new Rectangle(screenPt.X - w / 2, screenPt.Y - h, w, h), Text = L("Заметка", "Note")
        };
        var inner = new Panel { BackColor = Color.FromArgb(28, 20, 24), Dock = DockStyle.Fill, Padding = new Padding((int)(14 * scale), (int)(12 * scale), (int)(14 * scale), 0) };
        f.Padding = new Padding((int)Math.Max(1, 1.5 * scale));
        var tb = new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(28, 20, 24), ForeColor = Color.FromArgb(250, 238, 230),
            Font = new Font("Segoe UI", 13f), Dock = DockStyle.Fill, MaxLength = 120
        };
        inner.Controls.Add(tb); f.Controls.Add(inner);
        bool saved = false;
        Action save = delegate { if (saved) return; saved = true; NoteAdd(tb.Text); };
        tb.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; save(); f.Close(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; saved = true; f.Close(); }
        };
        f.Deactivate += delegate { if (!f.IsDisposed) { save(); f.Close(); } };
        f.Shown += delegate
        {
            // фокус из фонового процесса: временно привязываемся к потоку активного окна
            uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
            AttachThreadInput(fg, me, true); SetForegroundWindow(f.Handle); f.Activate(); tb.Focus(); AttachThreadInput(fg, me, false);
        };
        notePopup = f;
        f.Show();
    }
}
