// Проверка обновлений: раз в сутки спрашиваем GitHub о последнем релизе.
// Ничего не скачивает и не ставит само — только говорит «вышла версия X» (трей, маскот, окно настроек).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

static class Updater
{
    public const string Version = "1.1.0";
    public const string Repo = "amaravella/lofi-desk";
    public static string ReleasesUrl { get { return "https://github.com/" + Repo + "/releases"; } }

    // idle | checking | latest | available | error
    public static string State = "idle";
    public static string Latest = "", Url = "";
    public static event Action Changed;

    static readonly object gate = new object();
    static DateTime lastCheck = DateTime.MinValue;
    static string notified = "";
    static string StatePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LofiDesk", "update.txt"); } }

    // фоновая проверка: при старте (если прошлой не было сутки) и дальше раз в час смотрим, не пора ли
    public static void Start(bool enabled, Action<string, string> onNew)
    {
        Load();
        if (Newer(Latest, Version)) { State = "available"; Fire(); }
        if (!enabled) return;
        var t = new Thread(delegate ()
        {
            Thread.Sleep(30000);   // не мешаем старту обоев
            while (true)
            {
                if ((DateTime.UtcNow - lastCheck).TotalHours >= 20) Check();
                if (State == "available" && notified != Latest)
                {
                    notified = Latest; Save();
                    try { onNew(Latest, Url); } catch { }
                }
                Thread.Sleep(3600 * 1000);
            }
        }) { IsBackground = true };
        t.Start();
    }

    public static void CheckAsync()
    {
        new Thread(delegate () { Check(); }) { IsBackground = true }.Start();
    }

    static void Check()
    {
        lock (gate)
        {
            State = "checking"; Fire();
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "LofiDesk/" + Version;
                    wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    var o = new JavaScriptSerializer().DeserializeObject(wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest")) as Dictionary<string, object>;
                    string tag = o != null ? o["tag_name"] as string : null, url = o != null ? o["html_url"] as string : null;
                    if (string.IsNullOrEmpty(tag)) throw new Exception("no tag_name");
                    Latest = tag.TrimStart('v', 'V');
                    // открываем только страницы своего репозитория
                    Url = url != null && url.StartsWith("https://github.com/" + Repo + "/") ? url : ReleasesUrl;
                }
                State = Newer(Latest, Version) ? "available" : "latest";
                lastCheck = DateTime.UtcNow;
                Save();
            }
            catch (Exception ex)
            {
                State = "error";
                Widgets.LogText("[update] " + ex.Message);
            }
            Fire();
        }
    }

    public static bool Newer(string a, string b)
    {
        System.Version va, vb;
        return System.Version.TryParse(a ?? "", out va) && System.Version.TryParse(b ?? "", out vb) && va > vb;
    }

    static void Fire() { var h = Changed; if (h != null) try { h(); } catch { } }

    static void Load()
    {
        try
        {
            var l = File.ReadAllLines(StatePath);
            lastCheck = new DateTime(long.Parse(l[0]), DateTimeKind.Utc);
            Latest = l[1]; Url = l[2]; notified = l.Length > 3 ? l[3] : "";
            if (!Url.StartsWith("https://github.com/" + Repo + "/")) Url = ReleasesUrl;
        }
        catch { }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            File.WriteAllLines(StatePath, new[] { lastCheck.Ticks.ToString(), Latest, Url, notified });
        }
        catch { }
    }
}
