// Lofi Desk i18n tool.
//   i18n extract <scene.html> <strings.json>             — collect every user-visible Cyrillic string
//   i18n apply   <scene.html> <dict.json> <out.html>     — write a translated copy of the page
//
// The page is written in Russian. Instead of wrapping hundreds of literals in a t() call, the build
// translates the source text itself: JS string/template chunks, HTML text nodes, attribute values and
// CSS strings are replaced by their dictionary entries. Comments are left alone.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

static class I18n
{
    static readonly Regex Cyr = new Regex("[А-Яа-яЁё]");

    // a piece of source text that may be translated: [start, end) and the quote char around it (or '<' for HTML text)
    struct Span { public int S, E; public char Q; }

    static int Main(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: i18n extract <in> <out.json> | apply <in> <dict.json> <out>"); return 2; }
        string src = File.ReadAllText(args[1], Encoding.UTF8);
        var spans = Scan(src).Where(sp => Cyr.IsMatch(src.Substring(sp.S, sp.E - sp.S))).ToList();
        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        if (args[0] == "extract")
        {
            var keys = new List<string>();
            foreach (var sp in spans) { string k = src.Substring(sp.S, sp.E - sp.S).Trim(); if (!keys.Contains(k)) keys.Add(k); }
            var sb = new StringBuilder("{\n");
            for (int i = 0; i < keys.Count; i++) sb.Append("  ").Append(js.Serialize(keys[i])).Append(": \"\"").Append(i < keys.Count - 1 ? ",\n" : "\n");
            sb.Append("}\n");
            File.WriteAllText(args[2], sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine(keys.Count + " strings");
            return 0;
        }
        if (args[0] == "apply")
        {
            var dict = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(args[2], Encoding.UTF8));
            var outp = new StringBuilder(); int pos = 0, missing = 0;
            foreach (var sp in spans)
            {
                string raw = src.Substring(sp.S, sp.E - sp.S), key = raw.Trim();
                object v; string tr = dict.TryGetValue(key, out v) ? v as string : null;
                if (string.IsNullOrEmpty(tr)) { missing++; Console.Error.WriteLine("missing: " + key); continue; }
                // keep the original surrounding whitespace, escape the quote that delimits this literal
                string lead = raw.Substring(0, raw.Length - raw.TrimStart().Length), trail = raw.Substring(raw.TrimEnd().Length);
                if (sp.Q == '\'' || sp.Q == '"') tr = Regex.Replace(tr, "(?<!\\\\)" + Regex.Escape(sp.Q.ToString()), "\\" + sp.Q);
                if (sp.Q == '`') tr = tr.Replace("`", "\\`");
                outp.Append(src, pos, sp.S - pos).Append(lead).Append(tr).Append(trail);
                pos = sp.E;
            }
            outp.Append(src, pos, src.Length - pos);
            File.WriteAllText(args[3], outp.ToString(), new UTF8Encoding(false));
            Console.WriteLine("translated " + (spans.Count - missing) + "/" + spans.Count + (missing > 0 ? " (" + missing + " missing)" : ""));
            return missing > 0 ? 1 : 0;
        }
        return 2;
    }

    // ---- scanner: HTML outside <script>/<style>, CSS inside <style>, JS inside <script> ----
    static List<Span> Scan(string s)
    {
        var res = new List<Span>();
        int i = 0;
        while (i < s.Length)
        {
            int sc = s.IndexOf("<script", i, StringComparison.Ordinal), st = s.IndexOf("<style", i, StringComparison.Ordinal);
            int next = Math.Min(sc < 0 ? s.Length : sc, st < 0 ? s.Length : st);
            ScanHtml(s, i, next, res);
            if (next >= s.Length) break;
            bool isScript = next == sc;
            int bodyStart = s.IndexOf('>', next) + 1;
            int bodyEnd = s.IndexOf(isScript ? "</script>" : "</style>", bodyStart, StringComparison.Ordinal);
            if (bodyEnd < 0) bodyEnd = s.Length;
            if (isScript) ScanJs(s, bodyStart, bodyEnd, res); else ScanCss(s, bodyStart, bodyEnd, res);
            i = bodyEnd;
        }
        return res;
    }

    static void ScanHtml(string s, int a, int b, List<Span> res)
    {
        string part = s.Substring(a, b - a);
        foreach (Match m in Regex.Matches(part, ">([^<>]+)<")) if (m.Groups[1].Value.Trim().Length > 0) res.Add(new Span { S = a + m.Groups[1].Index, E = a + m.Groups[1].Index + m.Groups[1].Length, Q = '<' });
        foreach (Match m in Regex.Matches(part, "(?:title|placeholder|alt|aria-label)=\"([^\"]*)\"")) res.Add(new Span { S = a + m.Groups[1].Index, E = a + m.Groups[1].Index + m.Groups[1].Length, Q = '"' });
        res.Sort((x, y) => x.S.CompareTo(y.S));
    }

    static void ScanCss(string s, int a, int b, List<Span> res)
    {
        int i = a;
        while (i < b)
        {
            if (s[i] == '/' && i + 1 < b && s[i + 1] == '*') { int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? b : e + 2; continue; }
            if (s[i] == '\'' || s[i] == '"') { char q = s[i]; int j = i + 1; while (j < b && s[j] != q) { if (s[j] == '\\') j++; j++; } res.Add(new Span { S = i + 1, E = j, Q = q }); i = j + 1; continue; }
            i++;
        }
    }

    static void ScanJs(string s, int a, int b, List<Span> res)
    {
        int i = a;
        ScanJsCode(s, ref i, b, res, false);
    }

    // scans JS code; when inTemplateExpr is true, returns at the '}' that closes a ${...}
    static void ScanJsCode(string s, ref int i, int b, List<Span> res, bool inTemplateExpr)
    {
        int depth = 0;
        while (i < b)
        {
            char c = s[i];
            if (c == '/' && i + 1 < b && s[i + 1] == '/') { int e = s.IndexOf('\n', i); i = e < 0 ? b : e; continue; }
            if (c == '/' && i + 1 < b && s[i + 1] == '*') { int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? b : e + 2; continue; }
            if (c == '\'' || c == '"')
            {
                int j = i + 1; while (j < b && s[j] != c && s[j] != '\n') { if (s[j] == '\\') j++; j++; }
                res.Add(new Span { S = i + 1, E = j, Q = c }); i = j + 1; continue;
            }
            if (c == '`') { i++; ScanTemplate(s, ref i, b, res); continue; }
            if (c == '/' && IsRegexStart(s, i, a: 0)) { i = SkipRegex(s, i, b); continue; }
            if (inTemplateExpr)
            {
                if (c == '{') depth++;
                else if (c == '}') { if (depth == 0) { i++; return; } depth--; }
            }
            i++;
        }
    }

    static void ScanTemplate(string s, ref int i, int b, List<Span> res)
    {
        int chunk = i;
        while (i < b)
        {
            char c = s[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '`') { res.Add(new Span { S = chunk, E = i, Q = '`' }); i++; return; }
            if (c == '$' && i + 1 < b && s[i + 1] == '{')
            {
                res.Add(new Span { S = chunk, E = i, Q = '`' });
                i += 2; ScanJsCode(s, ref i, b, res, true); chunk = i; continue;
            }
            i++;
        }
    }

    // a '/' starts a regex literal when the previous meaningful token can't end an expression
    static bool IsRegexStart(string s, int i, int a)
    {
        int j = i - 1;
        while (j >= 0 && (s[j] == ' ' || s[j] == '\t' || s[j] == '\n' || s[j] == '\r')) j--;
        if (j < 0) return true;
        char p = s[j];
        if ("(,=:[!&|?{};+-*%<>~^".IndexOf(p) >= 0) return true;
        // keywords like return / typeof before a regex
        int k = j; while (k >= 0 && char.IsLetter(s[k])) k--;
        string w = s.Substring(k + 1, j - k);
        return w == "return" || w == "typeof" || w == "case" || w == "in" || w == "of";
    }

    static int SkipRegex(string s, int i, int b)
    {
        int j = i + 1; bool cls = false;
        while (j < b && s[j] != '\n')
        {
            if (s[j] == '\\') { j += 2; continue; }
            if (s[j] == '[') cls = true; else if (s[j] == ']') cls = false;
            else if (s[j] == '/' && !cls) { j++; break; }
            j++;
        }
        while (j < b && char.IsLetter(s[j])) j++;
        return j;
    }
}
