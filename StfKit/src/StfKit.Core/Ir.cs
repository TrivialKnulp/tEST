using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StfKit.Core
{

public sealed class IrLine
{
    public string Text { get; set; } = "";
    public double X { get; set; } public double Y { get; set; } public double W { get; set; } public double H { get; set; }
    public double Size { get; set; } public string Font { get; set; } = "";
    public bool Bold { get; set; } public bool Italic { get; set; }
}
public sealed class IrWord { public string Text { get; set; } = ""; public double X { get; set; } public double Y { get; set; } public double W { get; set; } public double H { get; set; } }   // einzelnes Wort (mm), für Markierungen im PDF
public sealed class IrBox { public double X { get; set; } public double Y { get; set; } public double W { get; set; } public double H { get; set; }
    public bool Background { get; set; }       // seitenfüllender Scan (kein Logo): wird nicht als @Picture übernommen
    public string? Fill { get; set; }         // nur Rects aus PDF: Füllfarbe als RRGGBB (für Markierungen in PDF), sonst null
    public string? Png { get; set; }          // nur Images: PNG als Base64 (Logo/Wappen) -> wird als @Picture eingefuegt
}
public sealed class IrWidget { public string Kind { get; set; } = ""; public string Name { get; set; } = ""; public double X { get; set; } public double Y { get; set; } public double W { get; set; } public double H { get; set; } }
public sealed class IrPage
{
    public double W { get; set; } public double H { get; set; }          // mm
    public List<IrLine> Lines { get; set; } = new();
    public List<IrBox> Rules { get; set; } = new();
    public List<IrBox> Rects { get; set; } = new();
    public List<IrWord> Words { get; set; } = new();
    public List<IrBox> Images { get; set; } = new();
    public List<IrWidget> Widgets { get; set; } = new();     // AcroForm-Felder (Checkboxen, Textfelder)
}
public sealed class IrDocument
{
    public string Source { get; set; } = "";
    public List<IrPage> Pages { get; set; } = new();
    public List<string> Fields { get; set; } = new();    // Kunden-Feldnamen aus docx (MERGEFIELD)
    public IrNotes Notes { get; set; } = new();           // Kommentare, Markierungen, Druckregeln aus docx
    public bool Ocr { get; set; }                         // Text stammt aus OCR (Scan): Positionen sind künstlich
    public bool TextOnly { get; set; }                    // Word/RTF/ODT OHNE PDF: nur Text, keine echten Positionen (Seiten/Zeilen sind ein künstlicher Fließtext)
    public List<string> Paragraphs { get; set; } = new(); // bei TextOnly: die Absätze des Dokuments (Grundlage für den Abgleich mit einer Vorlage)
    public static readonly JsonSerializerOptions Json = new()
    { WriteIndented = true, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    public static IrDocument Load(string path) => JsonSerializer.Deserialize<IrDocument>(File.ReadAllText(path), Json)!;
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
}

/// <summary>Absatzblock aus mehreren Zeilen.</summary>
public sealed class IrBlock
{
    public double X, Y, W, H; public List<IrLine> Lines = new();
}

public static class IrToStf
{
    // Kalibrierung aus dem Hagenbach-Vergleich: Aeneas-Box = PDF-Position + (0.2, 0.3) mm
    public static double Dx = 0.2, Dy = 0.3, Pad = 1.5;

    static readonly System.Text.RegularExpressions.Regex OpenTail = new(@"<[^<>\r\n]*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex CloseHead = new(@"^\s*([^<>\s]*)>", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Zeilen mit offenem "&lt;" (Platzhalter am Zeilenende abgerissen, z. B. "Nr. &lt;DEBITORKEN") mit dem Rest der nächsten Zeile ("NUNG&gt;") verbinden.
    /// Der PDF-Umbruch kann mitten im Wort liegen; Aeneas erkennt nur ganze Platzhalter. Der Rest wird aus der Folgezeile entfernt (bleibt dort nichts übrig, entfällt sie).</summary>
    public static List<IrLine> JoinSplitPlaceholders(IEnumerable<IrLine> lines, out List<string> joined)
    {
        var res = lines.Select(l => new IrLine { Text = l.Text, X = l.X, Y = l.Y, W = l.W, H = l.H, Size = l.Size, Font = l.Font, Bold = l.Bold, Italic = l.Italic }).ToList();
        joined = new List<string>();
        for (int i = 0; i < res.Count; i++)
        {
            var a = res[i]; if (!OpenTail.IsMatch(a.Text)) continue;
            // nächste Zeile darunter, die mit dem Rest eines Platzhalters beginnt ("NUNG>"); höchstens zwei Zeilenhöhen Abstand
            IrLine? b = null;
            foreach (var c in res)
            {
                if (ReferenceEquals(c, a) || c.Y <= a.Y + a.H * 0.3 || c.Y - (a.Y + a.H) > a.H * 2.0) continue;
                if (!CloseHead.IsMatch(c.Text)) continue;
                if (b == null || c.Y < b.Y || (Math.Abs(c.Y - b.Y) < 0.5 && Math.Abs(c.X - a.X) < Math.Abs(b.X - a.X))) b = c;
            }
            if (b == null) continue;
            var m = CloseHead.Match(b.Text); var rest = b.Text[m.Length..].TrimStart();
            joined.Add(a.Text.Trim() + m.Value.Trim());
            a.Text = a.Text + m.Value.Trim(); a.W = Math.Max(a.W, b.W);
            if (rest.Length == 0) res.Remove(b); else b.Text = rest;
            if (res.IndexOf(a) < i) i--;
        }
        return res;
    }

    public static List<IrBlock> Blocks(IEnumerable<IrLine> lines, double paraGap = 1.6)
    {
        var outp = new List<IrBlock>();
        foreach (var l in lines)
        {
            bool merged = false;
            for (int k = outp.Count - 1; k >= Math.Max(0, outp.Count - 6); k--)
            {
                var b = outp[k]; var last = b.Lines[^1];
                if (Math.Abs(l.X - b.X) < 2.0 && l.Y - (last.Y + last.H) >= 0 && l.Y - (last.Y + last.H) < last.H * paraGap * 0.6
                    && l.Size == last.Size && l.Bold == last.Bold
                    && Math.Abs((l.Y - b.Lines[0].Y) - b.Lines.Count * last.Size * 1.17 * 25.4 / 72.0) <= 0.4)      // Eine Aeneas-Textbox setzt Zeilen im festen Schritt (Größe*1.17): weicht der Abstand im PDF ab, wäre die Box nach wenigen Zeilen verschoben -> eigener Block
                {
                    b.Lines.Add(l); b.H = l.Y + l.H - b.Y; b.W = Math.Max(b.W, l.W); merged = true; break;
                }
            }
            if (!merged) outp.Add(new IrBlock { X = l.X, Y = l.Y, W = l.W, H = l.H, Lines = { l } });
        }
        return outp;
    }

    /// <summary>Markierter/kommentierter Text (z. B. "Vorname Nachname", "PLZ Ort") -> Platzhalter. Nur ganze Wörter; längste Texte zuerst.
    /// Wert ohne spitze Klammern = ein Platzhalter; mit Klammern (&lt;A&gt; &lt;B&gt;) wird er unverändert übernommen.</summary>
    /// <summary>Kundenfeld («Name») → Platzhalter und markierter Text → Platzhalter, wie bei der normalen Erzeugung (gilt auch für den Nur-Text-Modus).</summary>
    public static string TransformText(string t, IDictionary<string, string>? fieldMap, IDictionary<string, string>? textMap)
    {
        if (fieldMap != null) foreach (var (k, v) in fieldMap) t = t.Replace($"«{k}»", $"<{v}>");
        if (textMap != null && textMap.Count > 0) t = ApplyTextMap(t, textMap);
        return t;
    }

    public static string ApplyTextMap(string t, IDictionary<string, string> map)
    {
        foreach (var (lit, ph) in map.OrderByDescending(kv => kv.Key.Length))
        {
            if (string.IsNullOrWhiteSpace(lit) || string.IsNullOrWhiteSpace(ph)) continue;
            var rep = ph.Contains('<') ? ph : $"<{ph}>";
            t = System.Text.RegularExpressions.Regex.Replace(t, @"(?<![\p{L}\p{N}<])(?<!\d[.,/])" + System.Text.RegularExpressions.Regex.Escape(lit) + @"(?![\p{L}\p{N}>])(?![.,/]\d)", m => rep);
        }
        return t;
    }

    public sealed record Report(int Texts, int LinesTodo, int ImagesTodo, SortedSet<string> Fields, int Lines = 0, int Rects = 0, int SlotsFilled = 0, int PicturesMade = 0, int TextMapped = 0, List<string>? Joined = null);

    /// <param name="fieldMap">Kundenfeld («Anschrift1») → Aeneas-Platzhalter (F_AnredeN), ohne spitze Klammern.</param>
    public static (StfDocument Doc, Report Report) Convert(IrDocument ir, string version = "DB2026",
        IDictionary<string, string>? fieldMap = null, string eol = "\r\n", bool withLines = true, KnowledgeBase? kb = null, IDictionary<string, SlotDef>? slots = null,
        IDictionary<string, string>? textMap = null)
    {
        fieldMap ??= new Dictionary<string, string>();
        var doc = StfDocument.New(version, eol);
        int texts = 0, lines = 0, imgs = 0, madeLines = 0, madeRects = 0, slotsFilled = 0, pics = 0, mapped = 0; var seen = new SortedSet<string>(); var joinedAll = new List<string>();
        for (int pi = 0; pi < ir.Pages.Count; pi++)
        {
            var pg = ir.Pages[pi];
            if (pi > 0) doc.Add(StfDocument.MakeSeite());
            // Logos/Wappen zuerst (liegen hinter dem Text)
            foreach (var im in pg.Images)
            {
                if (im.Background) continue;                      // Scan: der Text kommt per OCR, das Bild gehört nicht ins Formular
                if (!string.IsNullOrEmpty(im.Png)) { try { doc.Add(StfDocument.MakePicture(System.Convert.FromBase64String(im.Png), im.X, im.Y, im.W, im.H)); pics++; continue; } catch { } }
                imgs++;
            }
            var pageLines = JoinSplitPlaceholders(pg.Lines, out var joinedPage); joinedAll.AddRange(joinedPage.Select(j => $"Seite {pi + 1}: {j}"));
            foreach (var b in Blocks(pageLines))
            {
                var ls = new List<string>();
                foreach (var l in b.Lines)
                {
                    string t = l.Text;
                    var learned = kb?.Rewrite(t);                     // aus Korrekturen gelernte Zeile (enthält bereits Auszeichnung/Platzhalter)
                    if (learned != null) { ls.Add(learned); continue; }
                    foreach (var (k, v) in fieldMap) t = t.Replace($"«{k}»", $"<{v}>");
                    if (textMap != null && textMap.Count > 0) { var t2 = ApplyTextMap(t, textMap); if (t2 != t) { mapped++; t = t2; } }
                    t = l.Bold ? $"[f]{t}[]" : l.Italic ? $"[k]{t}[]" : t;
                    ls.Add(t);
                }
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(string.Join("\n", ls), "«([^»]+)»"))
                    seen.Add(m.Groups[1].Value);
                var f = b.Lines[0];
                doc.Add(StfDocument.MakeText(version, b.X + Dx, b.Y + Dy, b.W + Pad, Math.Max(b.H, f.Size * 0.4), ls, "Arial", f.Size));
                texts++;
            }
            // Eingabefelder mit bekannter Beschriftung -> Text mit Platzhaltern in das Feld setzen (Anfrage-Zuordnung vor gelerntem Wissen)
            foreach (var sl in FieldSlots.Find(pg))
            {
                SlotDef? sd = null;
                if (slots != null && slots.TryGetValue(sl.Key, out var s1)) sd = s1; else if (kb != null && kb.Slots.TryGetValue(sl.Key, out var s2)) sd = s2;
                if (sd == null || string.IsNullOrWhiteSpace(sd.Text)) continue;
                doc.Add(StfDocument.MakeText(version, sl.Widget.X + sd.Dx, sl.Widget.Y + sd.Dy, sd.W > 0 ? sd.W : sl.Widget.W, sd.H > 0 ? sd.H : sl.Widget.H,
                    sd.Text.Split('\n'), "Arial", sd.Size));
                texts++; slotsFilled++;
            }
            if (withLines)
            {
                var lb = LineBuilder.FromRules(pg);
                foreach (var e in lb.Elems) doc.Add(e);
                madeLines += lb.Elems.Count; madeRects += lb.Rects;
            }
            else lines += pg.Rules.Count;
            lines += pg.Rects.Count;                        // gefüllte/sonstige Rechtecke: noch nicht umgesetzt
        }
        return (doc, new Report(texts, lines, imgs, seen, madeLines, madeRects, slotsFilled, pics, mapped, joinedAll));
    }
}
}