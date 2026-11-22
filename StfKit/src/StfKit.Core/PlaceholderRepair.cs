using System;
using System.Collections.Generic;
using System.Linq;

namespace StfKit.Core
{

/// <summary>Repariert Platzhalter, die ein PDF-Zeilenumbruch in einer schmalen Tabellenzelle zerrissen hat
/// ("&lt;B_" / "Pers" / "onen&gt;" -> "&lt;B_Personen&gt;"). Eine Zeile mit offenem "&lt;" wird mit den darunter liegenden Zeilen derselben Zelle
/// (horizontal überlappend) verbunden, bis das "&gt;" kommt (höchstens 4 Teile). Ohne schließendes "&gt;" bleibt alles unverändert.</summary>
public static class PlaceholderRepair
{
    static bool Open(string t) => t.LastIndexOf('<') > t.LastIndexOf('>');
    static readonly System.Text.RegularExpressions.Regex NameRx = new(@"^[\p{L}\p{N}_ \-.|]{2,40}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    // Nur annehmen, wenn zwischen dem letzten "<" und dem folgenden ">" ein plausibler Platzhalter-Name steht (keine Klammern, Beträge, Sonderzeichen)
    static bool PlausibleName(string t) { int a = t.LastIndexOf('<'); int b = a < 0 ? -1 : t.IndexOf('>', a); return b > a && NameRx.IsMatch(t.Substring(a + 1, b - a - 1)) && t.Substring(a + 1, b - a - 1).Count(c => c == ' ') <= 4; }

    static IrLine? NextInCell(List<IrLine> lines, HashSet<IrLine> remove, List<IrLine> parts, IrLine last)
        => lines.Where(c => !remove.Contains(c) && !parts.Contains(c)
                && c.Y > last.Y + 0.3 * last.H && c.Y <= last.Y + 2.2 * Math.Max(last.H, 3.0)
                && c.X < last.X + last.W + 2.0 && c.X + c.W > last.X - 2.0)           // gleiche Zelle: horizontal überlappend
            .OrderBy(c => c.Y).ThenBy(c => Math.Abs(c.X - last.X)).FirstOrDefault();

    static readonly System.Text.RegularExpressions.Regex NameInText = new(@"<([A-Za-z0-9_ÄÖÜäöüß\-]{2,60})>", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Platzhalter-Namen aus dem Text eines Word-Dokuments (alles in &lt;...&gt;), als Referenz für ApplyKnown.</summary>
    public static HashSet<string> NamesFromDocx(string docxPath)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var z = System.IO.Compression.ZipFile.OpenRead(docxPath); var en = z.GetEntry("word/document.xml"); if (en == null) return res;
            using var sr = new System.IO.StreamReader(en.Open()); var x = sr.ReadToEnd();
            var text = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(x, "<[^>]+>", ""));
            foreach (System.Text.RegularExpressions.Match m in NameInText.Matches(text)) res.Add(m.Groups[1].Value);
        }
        catch { }
        return res;
    }

    /// <summary>Zweite Stufe: ein zerrissener Platzhalter, dessen Anfang+Fortsetzung genau einem BEKANNTEN Namen entspricht (aus dem Word-Text, Kommentaren oder dem Katalog),
    /// wird auch ohne das schließende "&gt;" repariert (das "&gt;" kann das PDF an eine andere Stelle gesetzt haben; ein verwaistes "&gt;" in der Nähe wird entfernt).</summary>
    public static int ApplyKnown(IrDocument ir, IEnumerable<string> known)
    {
        var names = known.Where(n => n.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); int fixedCount = 0;
        if (names.Count == 0) return 0;
        foreach (var pg in ir.Pages)
        {
            var lines = pg.Lines.OrderBy(l => l.Y).ThenBy(l => l.X).ToList(); var remove = new HashSet<IrLine>();
            foreach (var l in lines.ToList())
            {
                if (remove.Contains(l) || !Open(l.Text)) continue;
                int lt = l.Text.LastIndexOf('<'); var prefix = l.Text.Substring(0, lt); var frag = l.Text.Substring(lt + 1);
                if (frag.Length < 2 && frag.Length != 0) { }
                var cands = names.Where(n => n.StartsWith(frag, StringComparison.OrdinalIgnoreCase) && n.Length > frag.Length).ToList();
                if (cands.Count == 0) continue;
                var parts = new List<IrLine> { l }; var acc = frag; var last = l; string? hit = null;
                while (parts.Count < 5 && hit == null)
                {
                    var next = NextInCell(lines, remove, parts, last); if (next == null) break;
                    parts.Add(next); acc += next.Text.Trim(); last = next;
                    var exact = cands.Where(n => string.Equals(n, acc, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (exact.Count == 1) hit = exact[0]; else if (!cands.Any(n => n.StartsWith(acc, StringComparison.OrdinalIgnoreCase))) break;
                }
                if (hit == null || parts.Count == 1) continue;
                l.Text = prefix + "<" + hit + ">"; l.X = parts.Min(p => p.X); l.W = parts.Max(p => p.X + p.W) - l.X; l.H = parts.Max(p => p.Y + p.H) - l.Y;
                foreach (var p in parts.Skip(1)) remove.Add(p);
                // verwaistes ">" in der Nähe (gleiche Zelle/Zeile darunter) entfernen
                var stray = lines.Where(c => !remove.Contains(c) && c != l && c.Y >= l.Y - 1 && c.Y <= l.Y + 40 && c.Text.Count(ch => ch == '>') > c.Text.Count(ch => ch == '<')).OrderBy(c => c.Y).FirstOrDefault();
                if (stray != null) { int k = stray.Text.IndexOf('>'); stray.Text = System.Text.RegularExpressions.Regex.Replace(stray.Text.Remove(k, 1), @"\s{2,}", " ").Trim(); }
                fixedCount++;
            }
            if (remove.Count > 0) pg.Lines = pg.Lines.Where(x => !remove.Contains(x)).ToList();
        }
        return fixedCount;
    }

    public static int Apply(IrDocument ir)
    {
        int fixedCount = 0;
        foreach (var pg in ir.Pages)
        {
            var lines = pg.Lines.OrderBy(l => l.Y).ThenBy(l => l.X).ToList();
            var remove = new HashSet<IrLine>();
            foreach (var l in lines.ToList())
            {
                if (remove.Contains(l) || !Open(l.Text)) continue;
                var parts = new List<IrLine> { l }; var text = l.Text; var last = l;
                while (Open(text) && parts.Count < 5)
                {
                    var next = NextInCell(lines, remove, parts, last);
                    if (next == null) break;
                    parts.Add(next); text += next.Text; last = next;
                }
                if (Open(text) || parts.Count == 1 || !PlausibleName(text)) continue;                  // nicht schließbar -> nichts anfassen
                l.Text = text; l.W = parts.Max(p => p.X + p.W) - parts.Min(p => p.X); l.X = parts.Min(p => p.X);
                l.H = parts.Max(p => p.Y + p.H) - l.Y;
                foreach (var p in parts.Skip(1)) remove.Add(p);
                fixedCount++;
            }
            if (remove.Count > 0) pg.Lines = pg.Lines.Where(x => !remove.Contains(x)).ToList();
        }
        return fixedCount;
    }
}
}
