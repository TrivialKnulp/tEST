using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StfKit.Core
{

public sealed class IrComment
{
    public string Id { get; set; } = ""; public string Author { get; set; } = "";
    public string Text { get; set; } = "";      // Kommentartext
    public string Anchor { get; set; } = "";    // Text, auf den sich der Kommentar bezieht
}
public sealed class IrHighlight
{
    public string Color { get; set; } = "";     // HL:yellow | HL:red | SHD:FFFF00 ...
    public string Text { get; set; } = "";      // markierter Text (zusammenhängende Läufe gleicher Farbe)
    public string Context { get; set; } = "";   // Absatz, in dem der Text steht (gekürzt)
}
/// <summary>Hinweise, die der Kunde/Kollege in Word hinterlegt hat: Kommentare, farbige Markierungen, abgeleitete Regeln.</summary>
public sealed class IrNotes
{
    public List<IrComment> Comments { get; set; } = new();
    public List<IrHighlight> Highlights { get; set; } = new();
    public List<string> RemovedPersonNames { get; set; } = new();      // Namen echter Personen aus dem Kundenmuster, die entfernt wurden (PersonLabelRule)
    public bool CommentFromPdf { get; set; }                    // Kommentare stammen aus PDF-Anmerkungen (nicht aus Word)
    /// <summary>Aus Kommentaren abgeleiteter Formulartyp (z. B. "Dieses Formular wird als Gebührenbescheid gedruckt").</summary>
    public string? KindHint { get; set; }
    /// <summary>Kommentare mit Druck-/Anhänge-Regeln (wann wird das Formular gedruckt, wann unterdrückt, woran angehängt).</summary>
    public List<string> PrintRules { get; set; } = new();
    /// <summary>Kommentar nennt direkt einen Platzhalter: markierter Text → Platzhalter (ohne spitze Klammern).</summary>
    public Dictionary<string, string> CommentPlaceholders { get; set; } = new();
    /// <summary>Das Word-Dokument enthält ein Datumsfeld (TIME/DATE/…): beim PDF-Druck steht dort das Druckdatum, nicht der Text aus dem docx.</summary>
    public bool HasDateField { get; set; }
    /// <summary>Alle Datumsangaben (TT.MM.JJJJ) im Text des docx, auch die zwischengespeicherten Feldergebnisse.</summary>
    public List<string> DocDates { get; set; } = new();
    public bool Any => Comments.Count > 0 || Highlights.Count > 0;
}

/// <summary>Liest Kommentare, Textmarker/Schattierungen und daraus ableitbare Regeln aus einem docx.</summary>
public static class DocxNotes
{
    static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly Regex PhOnly = new(@"^\s*<?\s*([A-Za-z]{1,6}_[A-Za-z0-9_]+)\s*>?\s*$", RegexOptions.Compiled);
    static readonly Regex PhIn = new(@"<([A-Za-z]{1,6}_[A-Za-z0-9_]+)>", RegexOptions.Compiled);
    static readonly Regex DateFieldRx = new(@"^\s*(TIME|DATE|CREATEDATE|SAVEDATE|PRINTDATE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex PrintRx = new(@"gedruckt|drucken|ausdruck|unterdrück|angehängt|anhängen", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex AsBescheidRx = new(@"als\s+Gebührenbescheid\s+gedruckt|Gebührenberechnung\s+durchgeführt", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Schattierungen, die Word nur als Feldhintergrund anzeigt bzw. neutrale Füllfarben: keine Markierung
    static readonly HashSet<string> NeutralShd = new(StringComparer.OrdinalIgnoreCase) { "auto", "FFFFFF", "D9D9D9", "BFBFBF", "F2F2F2", "EAF1DD" };

    public static IrNotes Read(string docx)
    {
        var n = new IrNotes();
        using var z = ZipFile.OpenRead(docx);
        var doc = z.GetEntry("word/document.xml"); if (doc == null) return n;
        XDocument dx; using (var s = doc.Open()) dx = XDocument.Load(s);

        n.HasDateField = dx.Descendants(W + "instrText").Any(i => DateFieldRx.IsMatch(i.Value))
                         || dx.Descendants(W + "fldSimple").Any(f => DateFieldRx.IsMatch((string?)f.Attribute(W + "instr") ?? ""));
        n.DocDates = Regex.Matches(string.Concat(dx.Descendants(W + "t").Where(t => !t.Ancestors(MC + "Fallback").Any()).Select(t => t.Value)), @"\b\d{1,2}\.\d{1,2}\.(?:19|20)\d{2}\b").Select(m => m.Value).Distinct().ToList();

        var texts = new Dictionary<string, (string Author, string Text)>();
        var ce = z.GetEntry("word/comments.xml");
        if (ce != null)
        {
            XDocument cx; using (var s = ce.Open()) cx = XDocument.Load(s);
            foreach (var c in cx.Descendants(W + "comment"))
            {
                var id = (string?)c.Attribute(W + "id") ?? "";
                var txt = string.Join("\n", c.Descendants(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value))).Where(t => t.Length > 0));
                texts[id] = ((string?)c.Attribute(W + "author") ?? "", txt.Trim());
            }
        }

        // Anker-Text je Kommentar: Läufe zwischen commentRangeStart und commentRangeEnd
        var anchors = new Dictionary<string, StringBuilder>(); var open = new HashSet<string>(); XElement? lastP = null;
        foreach (var e in dx.Descendants().Where(q => !q.Ancestors(MC + "Fallback").Any()))
        {
            if (e.Name == W + "commentRangeStart") { var id = (string?)e.Attribute(W + "id") ?? ""; open.Add(id); anchors.TryAdd(id, new StringBuilder()); }
            else if (e.Name == W + "commentRangeEnd") open.Remove((string?)e.Attribute(W + "id") ?? "");
            else if (e.Name == W + "t" && open.Count > 0)
            {
                var p = e.Ancestors(W + "p").FirstOrDefault();
                foreach (var id in open) { if (p != lastP && anchors[id].Length > 0) anchors[id].Append(' '); anchors[id].Append(e.Value); }
                lastP = p;
            }
        }
        foreach (var (id, (author, text)) in texts)
        {
            if (text.Length == 0) continue;
            var anchor = anchors.TryGetValue(id, out var sb) ? Regex.Replace(sb.ToString(), @"\s+", " ").Trim() : "";
            n.Comments.Add(new IrComment { Id = id, Author = author, Text = text, Anchor = anchor });
            if (PrintRx.IsMatch(text)) n.PrintRules.Add(text);
            if (AsBescheidRx.IsMatch(text)) n.KindHint = "Gebührenbescheid";
            var m = PhOnly.Match(text); var m2 = PhIn.Match(text);
            string? ph = m.Success ? m.Groups[1].Value : m2.Success ? m2.Groups[1].Value : null;
            if (ph != null && anchor.Length > 0 && anchor.Length <= 80) n.CommentPlaceholders[Strip(anchor)] = ph;
        }

        // Markierungen (Textmarker, Lauf-Schattierung); Tabellenzellen-/Absatzschattierung wird ignoriert
        foreach (var p in dx.Descendants(W + "p").Where(q => !q.Ancestors(MC + "Fallback").Any()))
        {
            string? cur = null; var sb = new StringBuilder(); var pText = Regex.Replace(string.Concat(p.Descendants(W + "t").Select(t => t.Value)), @"\s+", " ").Trim();
            void Flush() { var t = Regex.Replace(sb.ToString(), @"\s+", " ").Trim(); if (cur != null && t.Length > 1) AddHl(n, cur, t, pText); cur = null; sb.Clear(); }
            foreach (var r in p.Elements().SelectMany(el => el.Name == W + "r" ? new[] { el } : el.Descendants(W + "r")))
            {
                var rp = r.Element(W + "rPr"); string? col = null;
                var hl = (string?)rp?.Element(W + "highlight")?.Attribute(W + "val");
                var sh = (string?)rp?.Element(W + "shd")?.Attribute(W + "fill");
                if (hl != null && hl != "none") col = "HL:" + hl; else if (sh != null && !NeutralShd.Contains(sh)) col = "SHD:" + sh;
                var t = string.Concat(r.Elements(W + "t").Select(x => x.Value));
                if (t.Length == 0 && col == cur) continue;
                if (col != cur) Flush();
                cur = col; if (col != null) sb.Append(t);
            }
            Flush();
        }

        // Zellen-/Absatzschattierung: nur gelb/rot/dunkel und kurzer Text (Wert in einer markierten Zelle). Kopfzeilen von Tabellen werden übersprungen.
        foreach (var shd in dx.Descendants(W + "shd").Where(e => !e.Ancestors(MC + "Fallback").Any() && (e.Parent?.Name == W + "tcPr" || e.Parent?.Name == W + "pPr")))
        {
            var fill = (string?)shd.Attribute(W + "fill"); if (fill == null || fill.Length != 6 || NeutralShd.Contains(fill)) continue;
            var color = "SHD:" + fill.ToUpperInvariant(); if (!HighlightPlaceholders.IsYellowOrRed(color)) continue;
            var holder = shd.Parent!.Parent!;
            if (holder.Name == W + "tc") { var row = holder.Parent; var tbl = row?.Parent; if (row != null && tbl != null && tbl.Elements(W + "tr").Count() > 1 && tbl.Elements(W + "tr").First() == row) continue; }
            var text = Regex.Replace(string.Concat(holder.Descendants(W + "t").Select(t => t.Value)), @"\s+", " ").Trim();
            if (text.Length < 2 || text.Length > 60) continue;
            AddHl(n, color, text, text);
        }
        return n;
    }

    static string Strip(string s) => s.Trim().TrimEnd(':', ';', ',', ' ');

    static void AddHl(IrNotes n, string color, string text, string ctx)
    {
        if (n.Highlights.Any(h => h.Color == color && h.Text == text)) return;      // gleiche Markierung mehrfach (z. B. "2024") nur einmal
        n.Highlights.Add(new IrHighlight { Color = color, Text = text, Context = ctx.Length > 160 ? ctx[..160] : ctx });
    }
}
}