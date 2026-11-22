// ACHTUNG: In der Entwicklungsumgebung ohne NuGet-Zugriff geschrieben -> NICHT kompiliert/getestet.
// Gegen PdfPig 0.1.9 geschrieben; bei API-Abweichungen (Paths/GetBoundingRectangle) kleine Anpassungen nötig.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using StfKit.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace StfKit.Pdf
{

/// <summary>PDF → IR (mm, Ursprung links oben). Port von pdf2ir.py.
/// pdfplumber/pdfminer liefert Zeilenboxen in voller Schrifthöhe (Ober- bis Unterlänge); PdfPig-Glyphenboxen sind enger.
/// Wir emulieren deshalb die pdfminer-Box aus Grundlinie und Schriftgröße (Ascent≈0.79, Descent≈0.21),
/// damit die Kalibrierung (Dx/Dy in IrToStf) aus dem Python-Prototyp gültig bleibt.</summary>
public static class PdfExtractor
{
    const double Pt = 25.4 / 72.0;
    const double LineTol = 2.0;      // pt
    const double GapMm = 8.0;        // Spaltentrennung
    static readonly Regex Subset = new("^[A-Z]{6}\\+", RegexOptions.Compiled);

    sealed record W(string Text, double X0, double X1, double Top, double Bottom, double Size, string Font);

    /// <summary>Bild als PNG (Base64). PdfPig kann nur Flate-Bilder selbst nach PNG wandeln; JPEG-Bilder (DCTDecode, typisch für Logos/Briefköpfe)
    /// kommen als Rohbytes und werden hier über System.Drawing nach PNG umkodiert (Windows). Gelingt beides nicht: null (Bild bleibt "von Hand").</summary>
    static string? ToPngBase64(IPdfImage im)
    {
        try { if (im.TryGetPng(out var png) && png is { Length: > 0 }) return Convert.ToBase64String(png); } catch { }
        try
        {
            var raw = im.RawBytes.ToArray();
            if (raw.Length > 4 && raw[0] == 0xFF && raw[1] == 0xD8)                       // JPEG-Signatur
            {
                using var ms = new MemoryStream(raw);
                using var bmp = System.Drawing.Image.FromStream(ms);
                using var o = new MemoryStream();
                bmp.Save(o, System.Drawing.Imaging.ImageFormat.Png);
                return Convert.ToBase64String(o.ToArray());
            }
        }
        catch { }
        return null;
    }

    public static IrDocument Extract(string path)
    {
        using var pdf = PdfDocument.Open(path);
        var ir = new IrDocument { Source = path };
        foreach (var page in pdf.GetPages())
        {
            double H = page.Height;
            var pg = new IrPage { W = page.Width * Pt, H = H * Pt };

            var words = new List<W>();
            foreach (var w in page.GetWords())
            {
                if (w.Letters.Count == 0) continue;
                double size = w.Letters[0].PointSize;
                double baseline = H - w.Letters[0].StartBaseLine.Y;           // von oben
                words.Add(new W(w.Text, w.BoundingBox.Left, w.BoundingBox.Right,
                    baseline - 0.79 * size, baseline + 0.21 * size, Math.Round(size, 1),
                    Subset.Replace(w.FontName ?? "", "")));
            }

            foreach (var w in words) pg.Words.Add(new IrWord { Text = w.Text, X = w.X0 * Pt, Y = w.Top * Pt, W = (w.X1 - w.X0) * Pt, H = (w.Bottom - w.Top) * Pt });

            // Senkrechte Zellgrenzen (Tabellen): dünne senkrechte Linien und die linken/rechten Kanten von nicht gefüllten Kästen. Ein Wort-Abstand, in dem eine solche Grenze liegt,
            // trennt die Zeile in zwei Zellen (sonst wird "Benutzungsgebühr ... | 215,00 € | Anzahl" eine einzige Zeile). Gefüllte Flächen (Markierungen) zählen nicht.
            var vedges = new List<(double X, double Y0, double Y1)>();
            foreach (var vp in page.ExperimentalAccess.Paths)
            {
                var vb = vp.GetBoundingRectangle(); if (vb is null) continue;
                double wmm = vb.Value.Width * Pt, hmm = vb.Value.Height * Pt, y0 = (H - vb.Value.Top) * Pt;
                if (wmm < 1.0 && hmm >= 3.0) vedges.Add((vb.Value.Left * Pt + wmm / 2, y0, y0 + hmm));
                else if (!vp.IsFilled && wmm >= 5.0 && hmm >= 3.0 && hmm <= 80.0) { vedges.Add((vb.Value.Left * Pt, y0, y0 + hmm)); vedges.Add((vb.Value.Right * Pt, y0, y0 + hmm)); }
            }
            bool CellBorderBetween(W a, W b, double top, double bottom)
            {
                double gs = a.X1 * Pt - 0.3, ge = b.X0 * Pt + 0.3, mid = (top + bottom) / 2 * Pt;
                return vedges.Any(e => e.X > gs && e.X < ge && e.Y0 <= mid && mid <= e.Y1);
            }

            // Zeilen bilden
            var rows = new List<(double Top, double Bottom, List<W> Ws)>();
            foreach (var w in words.OrderBy(x => Math.Round(x.Top / LineTol)).ThenBy(x => x.X0))
            {
                int k = rows.FindIndex(r => Math.Abs(r.Top - w.Top) <= LineTol && Math.Abs(r.Bottom - w.Bottom) <= LineTol * 1.5);
                if (k >= 0) rows[k].Ws.Add(w); else rows.Add((w.Top, w.Bottom, new List<W> { w }));
            }
            foreach (var r in rows)
            {
                var ws = r.Ws.OrderBy(x => x.X0).ToList();
                var segs = new List<List<W>> { new() { ws[0] } };
                for (int i = 1; i < ws.Count; i++)
                    if ((ws[i].X0 - ws[i - 1].X1) * Pt > GapMm || CellBorderBetween(ws[i - 1], ws[i], r.Top, r.Bottom)) segs.Add(new() { ws[i] }); else segs[^1].Add(ws[i]);
                foreach (var s in segs)
                {
                    var f = s[0];
                    pg.Lines.Add(new IrLine
                    {
                        Text = string.Join(" ", s.Select(x => x.Text)),
                        X = f.X0 * Pt, Y = s.Min(x => x.Top) * Pt,
                        W = (s[^1].X1 - f.X0) * Pt, H = (s.Max(x => x.Bottom) - s.Min(x => x.Top)) * Pt,
                        Size = f.Size, Font = f.Font,
                        Bold = f.Font.Contains("Bold", StringComparison.OrdinalIgnoreCase),
                        Italic = f.Font.Contains("Italic", StringComparison.OrdinalIgnoreCase) || f.Font.Contains("Oblique", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            pg.Lines = pg.Lines.OrderBy(l => Math.Round(l.Y)).ThenBy(l => l.X).ToList();

            // Linien / Rechtecke
            foreach (var p in page.ExperimentalAccess.Paths)
            {
                var bb = p.GetBoundingRectangle();
                if (bb is null) continue;
                var b = new IrBox { X = bb.Value.Left * Pt, Y = (H - bb.Value.Top) * Pt, W = bb.Value.Width * Pt, H = bb.Value.Height * Pt };
                if (bb.Value.Height < 1.0 || bb.Value.Width < 1.0) { pg.Rules.Add(b); continue; }
                // Füllfarbe merken: markierte Stellen (dunkel/gelb/rot) sind die Platzhalter-Stellen (PdfHighlights)
                try
                {
                    if (p.IsFilled && p.FillColor != null)
                    {
                        var rgb = p.FillColor.ToRGBValues();
                        int R = (int)Math.Round(Convert.ToDouble(rgb.Item1) * 255), G = (int)Math.Round(Convert.ToDouble(rgb.Item2) * 255), B = (int)Math.Round(Convert.ToDouble(rgb.Item3) * 255);
                        b.Fill = Math.Clamp(R, 0, 255).ToString("X2") + Math.Clamp(G, 0, 255).ToString("X2") + Math.Clamp(B, 0, 255).ToString("X2");
                    }
                }
                catch { }
                pg.Rects.Add(b);
            }
            // Bilder (Logo/Wappen)
            // PNG wird mitgenommen, damit das Logo als @Picture in das Formular kommt; geht das nicht (z. B. JPEG-Bild), bleibt es "Bild von Hand".
            foreach (var im in page.GetImages())
            {
                double bx = im.Bounds.Left * Pt, by = (H - im.Bounds.Top) * Pt, bw = im.Bounds.Width * Pt, bh = im.Bounds.Height * Pt;
                // Seitenfüllendes Bild (Scan): PdfPig liefert bei gedrehten Scans Bounds außerhalb der Seite (z. B. 297x210 mm auf A4 hochkant).
                // Solche Bilder sind Hintergrund (kein Logo): auf die Seite begrenzen und als Scan markieren.
                double pw = pg.W, ph = pg.H, big = Math.Max(bw, bh), small = Math.Min(bw, bh);
                bool scan = (bw * bh >= 0.6 * pw * ph) || (big >= 0.9 * Math.Max(pw, ph) && small >= 0.9 * Math.Min(pw, ph));
                if (scan) { bx = 0; by = 0; bw = pw; bh = ph; }
                pg.Images.Add(new IrBox { X = bx, Y = by, W = bw, H = bh, Png = ToPngBase64(im), Background = scan });
            }

            // AcroForm-Widgets (Checkboxen quadratisch <= 8 mm, sonst Textfelder). Feldtyp ueber Groesse abgeleitet, damit keine AcroForm-API noetig ist.
            foreach (var an in page.ExperimentalAccess.GetAnnotations())
            {
                if (an.Type != UglyToad.PdfPig.Annotations.AnnotationType.Widget)
                {
                    // Kommentare/Markierungen, die jemand im PDF hinterlegt hat (Haftnotiz, Textfeld, Textmarker): wie Word-Kommentare/-Markierungen behandeln
                    try
                    {
                        var ar = an.Rectangle; double m = an.Type == UglyToad.PdfPig.Annotations.AnnotationType.Text ? 6.0 : 0.5;   // Haftnotiz-Symbol liegt neben dem Text
                        string Under() => string.Join(" ", words.Where(x => (x.X0 + x.X1) / 2 >= ar.Left - m && (x.X0 + x.X1) / 2 <= ar.Right + m && (H - (x.Top + x.Bottom) / 2) >= ar.Bottom - m && (H - (x.Top + x.Bottom) / 2) <= ar.Top + m)
                                            .OrderBy(x => Math.Round(x.Top / 2.0)).ThenBy(x => x.X0).Select(x => x.Text)).Trim();
                        var content = (an.Content ?? "").Trim();
                        var kind = an.Type;
                        if (content.Length > 0 && (kind == UglyToad.PdfPig.Annotations.AnnotationType.Text || kind == UglyToad.PdfPig.Annotations.AnnotationType.FreeText || kind == UglyToad.PdfPig.Annotations.AnnotationType.Highlight || kind == UglyToad.PdfPig.Annotations.AnnotationType.Underline || kind == UglyToad.PdfPig.Annotations.AnnotationType.Squiggly || kind == UglyToad.PdfPig.Annotations.AnnotationType.StrikeOut))
                        {
                            var anchor = kind == UglyToad.PdfPig.Annotations.AnnotationType.FreeText ? "" : Under();
                            ir.Notes.Comments.Add(new IrComment { Id = "pdf" + ir.Notes.Comments.Count, Author = "PDF-Kommentar (Seite " + (ir.Pages.Count + 1) + ")", Text = content, Anchor = anchor });
                            ir.Notes.CommentFromPdf = true;
                            var m1 = System.Text.RegularExpressions.Regex.Match(content, @"^\s*<([^<>]{2,60})>\s*$"); var m2 = System.Text.RegularExpressions.Regex.Match(content, @"<([A-Za-z0-9_ÄÖÜäöüß\-]{2,60})>");
                            var ph = m1.Success ? m1.Groups[1].Value : m2.Success ? m2.Groups[1].Value : null;
                            if (ph != null && kind != UglyToad.PdfPig.Annotations.AnnotationType.Text && anchor.Length > 0 && anchor.Length <= 80) ir.Notes.CommentPlaceholders[anchor.Trim().TrimEnd(':', ';', ',', ' ')] = ph;
                        }
                        if (kind == UglyToad.PdfPig.Annotations.AnnotationType.Highlight)
                        {
                            var t = Under();
                            if (t.Length > 1 && !ir.Notes.Highlights.Any(h => h.Color == "HL:yellow" && h.Text == t)) ir.Notes.Highlights.Add(new IrHighlight { Color = "HL:yellow", Text = t, Context = t });
                        }
                    }
                    catch { }
                    continue;
                }
                var r = an.Rectangle; double w = r.Width * Pt, h = r.Height * Pt;
                bool box = Math.Abs(w - h) < 1.0 && w <= 8.0;
                pg.Widgets.Add(new IrWidget { Kind = box ? "checkbox" : "text", Name = an.Name ?? "", X = r.Left * Pt, Y = (H - r.Top) * Pt, W = w, H = h });
            }
            ir.Pages.Add(pg);
        }
        PersonLabelRule.Apply(ir);     // Name einer Person hinter 'Sachbearbeiter/in' entfernen (Beschriftung bleibt)
        PlaceholderRepair.Apply(ir);   // durch Zeilenumbruch zerrissene <Platzhalter> in schmalen Zellen wieder zusammensetzen
        PdfHighlights.Apply(ir);       // dunkel/gelb/rot markierte Stellen -> Platzhalter-Kandidaten (wie Word-Markierungen), in CLI und API
        return ir;
    }
}
}