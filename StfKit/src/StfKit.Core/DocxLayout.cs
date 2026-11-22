using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace StfKit.Core
{

/// <summary>Word/RTF/ODT OHNE PDF (kein LibreOffice): nur der Text der Absätze wird gelesen. Positionen gibt es nicht - sie kommen aus einem ähnlichen vorhandenen Formular
/// (FormAdapter.TextOnly). Gibt es keine Vorlage, wird ein einfacher Fließtext (A4, linker Rand, feste Zeilenhöhe) erzeugt, der in Aeneas von Hand gestaltet werden muss.</summary>
public static class DocxLayout
{
    public sealed record Para(string Text, bool Bold, bool PageBreakBefore);

    // künstlicher Fließtext (mm)
    const double PageW = 210, PageH = 297, MarginL = 20, MarginT = 20, MarginB = 20, TextW = 170, FontSize = 10, Pt = 25.4 / 72.0;
    static readonly double CharW = FontSize * 0.5 * Pt;                 // gleiche Schätzung wie StfToIr
    static readonly double Step = FontSize * 1.17 * Pt;

    /// <summary>Absätze aus einem .docx (Tabellenzellen und Textfelder eingeschlossen). Gelöschter Text (Änderungsverfolgung) wird ignoriert.</summary>
    public static List<Para> ReadParagraphs(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        var e = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("word/document.xml fehlt");
        using var s = e.Open(); var x = XDocument.Load(s);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var res = new List<Para>(); bool pendingBreak = false;
        XNamespace mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
        // Textfelder/Formen stehen in mc:AlternateContent doppelt (Choice + Fallback): nur die Choice-Fassung lesen, sonst erscheint der Text zweimal
        foreach (var p in x.Descendants(w + "p").Where(q => !q.Ancestors(mc + "Fallback").Any()))
        {
            var sb = new StringBuilder(); bool allBold = true, any = false; bool breakBefore = pendingBreak, brInPara = false; pendingBreak = false;
            if (p.Element(w + "pPr")?.Element(w + "pageBreakBefore") != null) breakBefore = true;
            foreach (var r in p.Descendants(w + "r").Where(r => r.Ancestors(w + "p").First() == p))
            {
                var b = r.Element(w + "rPr")?.Element(w + "b");
                bool bold = b != null && !string.Equals((string?)b.Attribute(w + "val"), "0", StringComparison.Ordinal) && !string.Equals((string?)b.Attribute(w + "val"), "false", StringComparison.OrdinalIgnoreCase);
                foreach (var n in r.Elements())
                {
                    if (n.Name == w + "t") { sb.Append(n.Value); if (n.Value.Trim().Length > 0) { any = true; if (!bold) allBold = false; } }
                    else if (n.Name == w + "tab") sb.Append("  ");
                    else if (n.Name == w + "br")
                    {
                        if ((string?)n.Attribute(w + "type") == "page") { res.Add(new Para(sb.ToString().Trim(), any && allBold, breakBefore)); sb.Clear(); any = false; allBold = true; breakBefore = true; brInPara = true; }
                        else sb.Append('\n');
                    }
                }
            }
            if (brInPara && sb.ToString().Trim().Length == 0) pendingBreak = true;       // Umbruch am Absatzende: gilt für den nächsten Absatz
            else res.Add(new Para(sb.ToString().Trim(), any && allBold, breakBefore));
        }
        // leere Absätze hintereinander zusammenfassen
        var outp = new List<Para>();
        foreach (var q in res) { if (q.Text.Length == 0 && !q.PageBreakBefore && outp.Count > 0 && outp[^1].Text.Length == 0) continue; outp.Add(q); }
        while (outp.Count > 0 && outp[^1].Text.Length == 0) outp.RemoveAt(outp.Count - 1);
        return outp;
    }

    /// <summary>Absätze aus Klartext (RTF/ODT über CorpusLoader.OtherText).</summary>
    public static List<Para> FromPlainText(string text) =>
        text.Replace("\r", "").Split('\n').Select(l => new Para(l.Trim(), false, false)).Where((q, i) => q.Text.Length > 0 || i > 0).ToList();

    /// <summary>Zeilenumbruch an Wortgrenzen (Wörter über maxChars bleiben ganz auf einer Zeile). Zeilenumbrüche im Text bleiben erhalten.</summary>
    public static List<string> Wrap(string text, int maxChars)
    {
        var res = new List<string>(); maxChars = Math.Max(8, maxChars);
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var cur = new StringBuilder();
            foreach (var word in raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (cur.Length > 0 && cur.Length + 1 + word.Length > maxChars) { res.Add(cur.ToString()); cur.Clear(); }
                if (cur.Length > 0) cur.Append(' ');
                cur.Append(word);
            }
            res.Add(cur.ToString());
        }
        return res;
    }

    /// <summary>IR für den Nur-Text-Modus: Absätze (Grundlage für den Abgleich mit der Vorlage) + künstlicher Fließtext (Grundlage für Typ-Erkennung, Suche und den Fall ohne Vorlage).</summary>
    public static IrDocument ToIr(IList<Para> paras, string source)
    {
        var ir = new IrDocument { Source = source, TextOnly = true };
        ir.Paragraphs = paras.Select(p => p.Text).Where(t => t.Length > 0).ToList();
        int cpl = (int)Math.Floor(TextW / CharW);
        var pg = new IrPage { W = PageW, H = PageH }; double y = MarginT;
        foreach (var p in paras)
        {
            if (p.PageBreakBefore && pg.Lines.Count > 0) { ir.Pages.Add(pg); pg = new IrPage { W = PageW, H = PageH }; y = MarginT; }
            if (p.Text.Length == 0) { y += Step * 0.6; continue; }
            foreach (var l in Wrap(p.Text, cpl))
            {
                if (y + Step > PageH - MarginB && pg.Lines.Count > 0) { ir.Pages.Add(pg); pg = new IrPage { W = PageW, H = PageH }; y = MarginT; }
                pg.Lines.Add(new IrLine { Text = l, X = MarginL, Y = y, W = Math.Max(l.Length * CharW, 1), H = FontSize * Pt, Size = FontSize, Font = "Arial", Bold = p.Bold });
                y += Step;
            }
            y += Step * 0.4;                                              // Absatzabstand
        }
        if (pg.Lines.Count > 0 || ir.Pages.Count == 0) ir.Pages.Add(pg);
        return ir;
    }
}
}
