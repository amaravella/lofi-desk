// Окно настроек Lofi Desk: settings.html в WebView2 с живым предпросмотром сцены (сама scene.html в демо-режиме).
// Сохраняет settings.ini — комментарии и порядок строк остаются как были — и перезапускает обои.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class SettingsWindow
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

    // что окно может менять; остальные строки settings.ini не трогаем
    static readonly string[] Keys = { "lang", "scene", "art", "feed", "clock", "grain", "city", "lat", "lon", "limits", "updates" };
    static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };
    static readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }

    static Form form;
    static WebView2 web;
    static Action applied;

    // из трея: одно окно на процесс
    public static void Open(Action onApplied)
    {
        if (form != null && !form.IsDisposed) { form.WindowState = FormWindowState.Normal; form.Activate(); return; }
        Create(onApplied).Show();
    }

    public static Form Create(Action onApplied)
    {
        applied = onApplied;
        form = new Form
        {
            Text = Widgets.L("Lofi Desk — настройки", "Lofi Desk — settings"),
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = Color.FromArgb(22, 16, 18),
            Icon = Widgets.MascotIcon(),
            AutoScaleMode = AutoScaleMode.None
        };
        float k = 1;
        try { uint dpi = GetDpiForWindow(form.Handle); if (dpi > 0) k = dpi / 96f; } catch { }
        form.Size = new Size((int)(1180 * k), (int)(820 * k));
        form.MinimumSize = new Size((int)(900 * k), (int)(600 * k));
        int dark = 1;   // тёмный заголовок окна, как у остального
        try { DwmSetWindowAttribute(form.Handle, 20, ref dark, 4); } catch { }
        form.Load += delegate { Attach(); };
        form.FormClosed += delegate { Updater.Changed -= OnUpdate; form = null; web = null; };
        Updater.Changed += OnUpdate;
        return form;
    }

    static async void Attach()
    {
        try
        {
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(22, 16, 18) };
            form.Controls.Add(web);
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LofiDesk", "WebView2-settings");
            var env = await CoreWebView2Environment.CreateAsync(null, data, null);
            await web.EnsureCoreWebView2Async(env);
            var st = web.CoreWebView2.Settings;
            st.AreDefaultContextMenusEnabled = false;
            st.AreDevToolsEnabled = false;
            st.IsZoomControlEnabled = false;
            st.IsStatusBarEnabled = false;
            web.CoreWebView2.WebMessageReceived += OnMessage;
            // ссылки наружу — только в браузер и только на свой репозиторий
            web.CoreWebView2.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; Widgets.OpenUrl(e.Uri); };
            string page = Path.Combine(BaseDir, Widgets.En && File.Exists(Path.Combine(BaseDir, "settings.en.html")) ? "settings.en.html" : "settings.html");
            web.CoreWebView2.Navigate(new Uri(page).AbsoluteUri);
        }
        catch (Exception ex)
        {
            Widgets.LogText("[settings] " + ex);
            MessageBox.Show(Widgets.L("Не получилось открыть окно настроек. Можно поправить settings.ini вручную.", "Couldn't open the settings window. You can edit settings.ini by hand."), "Lofi Desk");
            Shell(Path.Combine(BaseDir, "settings.ini"));
            form.Close();
        }
    }

    static void Post(Dictionary<string, object> msg)
    {
        string s = js.Serialize(msg);
        if (form == null || form.IsDisposed) return;
        form.BeginInvoke((Action)delegate { if (web != null && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(s); });
    }

    static void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var m = js.DeserializeObject(e.WebMessageAsJson) as Dictionary<string, object>;
            if (m == null) return;
            string type = Convert.ToString(m["type"]);
            if (type == "ready") Post(InitData());
            else if (type == "geo") Geo(Convert.ToString(m["q"]));
            else if (type == "browse") Browse();
            else if (type == "check-update") Updater.CheckAsync();
            else if (type == "cancel") form.Close();
            else if (type == "open") OpenThing(Convert.ToString(m["what"]));
            else if (type == "save")
            {
                var vals = m["values"] as Dictionary<string, object>;
                if (vals == null) return;
                SaveIni(vals);
                var done = applied;
                form.Close();
                if (done != null) done();
            }
        }
        catch (Exception ex) { Widgets.LogText("[settings] " + ex); }
    }

    static Dictionary<string, object> InitData()
    {
        var cur = LofiSaver.LoadSettings();
        var s = new Dictionary<string, object>();
        foreach (var k in Keys) { string v; s[k] = cur.TryGetValue(k, out v) ? v : ""; }
        var d = new Dictionary<string, object>();
        d["type"] = "init";
        d["settings"] = s;
        d["arts"] = ArtList();
        d["version"] = Updater.Version;
        d["hasEn"] = File.Exists(Path.Combine(BaseDir, "scene.en.html"));
        d["screen"] = WallpaperSize();
        d["update"] = UpdateData();
        return d;
    }

    static List<object> ArtList()
    {
        var list = new List<object>();
        string dir = Path.Combine(BaseDir, "art");
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir))
            if (Array.IndexOf(ImageExt, Path.GetExtension(f).ToLowerInvariant()) >= 0) list.Add("art/" + Path.GetFileName(f));
        return list;
    }

    // размер сцены в CSS-пикселях: второй монитор (там живут обои), а если его нет — основной
    static Dictionary<string, object> WallpaperSize()
    {
        Screen scr = Screen.PrimaryScreen;
        foreach (Screen s in Screen.AllScreens) if (!s.Primary) { scr = s; break; }
        double k = 1;
        try
        {
            uint x, y;
            var mon = MonitorFromPoint(new Point(scr.Bounds.X + scr.Bounds.Width / 2, scr.Bounds.Y + scr.Bounds.Height / 2), 2);
            if (GetDpiForMonitor(mon, 0, out x, out y) == 0 && x > 0) k = x / 96.0;
        }
        catch { }
        var d = new Dictionary<string, object>();
        d["w"] = (int)Math.Round(scr.Bounds.Width / k);
        d["h"] = (int)Math.Round(scr.Bounds.Height / k);
        return d;
    }

    static Dictionary<string, object> UpdateData()
    {
        var d = new Dictionary<string, object>();
        d["state"] = Updater.State; d["latest"] = Updater.Latest;
        return d;
    }

    static void OnUpdate()
    {
        var d = UpdateData(); d["type"] = "update";
        Post(d);
    }

    // поиск города для погоды — геокодер Open-Meteo, без ключа
    static void Geo(string q)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2 || q.Length > 80) return;
        new Thread(delegate ()
        {
            var res = new Dictionary<string, object>(); res["type"] = "geo"; res["q"] = q;
            var items = new List<object>();
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                {
                    string url = "https://geocoding-api.open-meteo.com/v1/search?count=6&format=json&language=" + (Widgets.En ? "en" : "ru") + "&name=" + Uri.EscapeDataString(q);
                    var o = js.DeserializeObject(wc.DownloadString(url)) as Dictionary<string, object>;
                    object r;
                    if (o != null && o.TryGetValue("results", out r) && r is object[])
                        foreach (var it in (object[])r)
                        {
                            var g = it as Dictionary<string, object>;
                            if (g == null) continue;
                            var x = new Dictionary<string, object>();
                            object v;
                            x["name"] = Convert.ToString(g["name"]);
                            x["admin"] = g.TryGetValue("admin1", out v) ? Convert.ToString(v) : "";
                            x["country"] = g.TryGetValue("country", out v) ? Convert.ToString(v) : "";
                            x["lat"] = Math.Round(Convert.ToDouble(g["latitude"]), 2);
                            x["lon"] = Math.Round(Convert.ToDouble(g["longitude"]), 2);
                            items.Add(x);
                        }
                }
            }
            catch (Exception ex) { res["error"] = true; Widgets.LogText("[settings] geo: " + ex.Message); }
            res["items"] = items;
            Post(res);
        }) { IsBackground = true }.Start();
    }

    // своя картинка: копируем в art/, чтобы путь был относительным и папку можно было переносить
    static void Browse()
    {
        using (var dlg = new OpenFileDialog
        {
            Title = Widgets.L("Картинка для сцены", "Picture for the scene"),
            Filter = Widgets.L("Картинки", "Images") + "|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp"
        })
        {
            if (dlg.ShowDialog(form) != DialogResult.OK) return;
            string dir = Path.Combine(BaseDir, "art");
            Directory.CreateDirectory(dir);
            string src = Path.GetFullPath(dlg.FileName), name = Path.GetFileName(src), dst = Path.Combine(dir, name);
            if (!string.Equals(Path.GetDirectoryName(src).TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 2; File.Exists(dst); i++) dst = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "-" + i + Path.GetExtension(name));
                File.Copy(src, dst);
            }
            var d = new Dictionary<string, object>();
            d["type"] = "art-added"; d["path"] = "art/" + Path.GetFileName(dst); d["arts"] = ArtList();
            Post(d);
        }
    }

    static void OpenThing(string what)
    {
        if (what == "folder") Shell(BaseDir);
        else if (what == "ini") Shell(Path.Combine(BaseDir, "settings.ini"));
        else if (what == "screensaver") Shell("control.exe", "desk.cpl,,@screensaver");
        else if (what == "release") Widgets.OpenUrl(Updater.Url);
        else if (what == "repo") Widgets.OpenUrl("https://github.com/" + Updater.Repo + "/");
    }

    static void Shell(string file, string args = null)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args ?? "") { UseShellExecute = true }); }
        catch (Exception ex) { Widgets.LogText("[settings] " + ex.Message); }
    }

    // переписываем только свои ключи, на их же строках; новых ключей — в конец файла
    static void SaveIni(Dictionary<string, object> vals)
    {
        string p = Path.Combine(BaseDir, "settings.ini"), example = Path.Combine(BaseDir, "settings.example.ini");
        if (!File.Exists(p) && File.Exists(example)) File.Copy(example, p);
        var lines = File.Exists(p) ? new List<string>(File.ReadAllLines(p, Encoding.UTF8)) : new List<string>();
        var want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in Keys)
        {
            object v;
            if (!vals.TryGetValue(k, out v) || v == null) continue;
            string s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture).Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length > 260) s = s.Substring(0, 260);
            want[k] = s;
        }
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (t.Length == 0 || t[0] == '#' || t[0] == ';') continue;
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            string key = t.Substring(0, eq).Trim(), val;
            if (done.Contains(key) || !want.TryGetValue(key, out val)) continue;
            lines[i] = key + "=" + val;
            done.Add(key);
        }
        foreach (var kv in want) if (!done.Contains(kv.Key)) lines.Add(kv.Key + "=" + kv.Value);
        File.WriteAllLines(p, lines, new UTF8Encoding(false));
    }
}
