using System;
using System.Collections.Generic;
using System.Linq;

namespace StfKit.Core
{

/// <summary>Markierungen im PDF (Haneke: "Stellen, an denen Platzhalter eingefügt werden müssen, sind dunkel markiert").
/// Gefüllte Rechtecke in dunkler, gelber oder roter Farbe, hinter denen Text steht, werden wie Word-Markierungen (IrHighlight, Farbe "SHD:RRGGBB")
/// eingetragen - mit genau den markierten WÖRTERN (nicht der ganzen Zeile). Die Platzhalter-Zuordnung übernimmt HighlightPlaceholders
/// (Datum/Betrag/Jahr/Namensmuster); nicht erkennbare Werte werden gemeldet, nicht geraten.
/// Ignoriert werden: große Flächen (Seitenhintergrund, Kopfzeilen), helle Flächen, Flächen ohne Text (Logos, Linien).</summary>
public static class PdfHighlights
{
    const double MaxPageShare = 0.25;      // Rechteck größer als 25 % der Seite = Hintergrund, keine Markierung
    const double MergeGap = 1.6;           // mm: benachbarte Rechtecke gleicher Farbe in einer Zeile gehören zu einer Markierung

    public static int Apply(IrDocument ir)
    {
        int added = 0;
        foreach (var pg in ir.Pages)
        {
            double pageArea = Math.Max(1.0, pg.W * pg.H);
            var cand = pg.Rects.Where(r => !string.IsNullOrEmpty(r.Fill) && r.W * r.H <= MaxPageShare * pageArea && HighlightPlaceholders.IsYellowOrRed("SHD:" + r.Fill))
                               .Select(r => new IrBox { X = r.X, Y = r.Y, W = r.W, H = r.H, Fill = r.Fill!.ToUpperInvariant() }).ToList();
            foreach (var m in Merge(cand))
            {
                var inside = WordsIn(pg, m);
                if (inside.Count == 0) continue;
                var text = string.Join(" ", inside.Select(w => w.Trim())).Trim();
                if (text.Length == 0) continue;
                string col = "SHD:" + m.Fill;
                if (ir.Notes.Highlights.Any(h => h.Color == col && h.Text == text)) continue;
                var ctx = string.Join(" ", pg.Lines.Where(l => l.Y < m.Y + m.H && l.Y + l.H > m.Y).OrderBy(l => l.X).Select(l => l.Text.Trim()));
                ir.Notes.Highlights.Add(new IrHighlight { Color = col, Text = text, Context = ctx.Length > 160 ? ctx[..160] : ctx });
                added++;
            }
        }
        return added;
    }

    // benachbarte Rechtecke gleicher Farbe auf gleicher Höhe zusammenfassen
    static List<IrBox> Merge(List<IrBox> boxes)
    {
        var res = new List<IrBox>();
        foreach (var b in boxes.OrderBy(x => Math.Round(x.Y)).ThenBy(x => x.X))
        {
            var t = res.FirstOrDefault(r => r.Fill == b.Fill && Math.Abs(r.Y - b.Y) < 1.5 && Math.Abs(r.H - b.H) < 2.0 && b.X <= r.X + r.W + MergeGap && b.X + b.W >= r.X - MergeGap);
            if (t == null) { res.Add(b); continue; }
            double x0 = Math.Min(t.X, b.X), x1 = Math.Max(t.X + t.W, b.X + b.W);
            t.X = x0; t.W = x1 - x0; t.H = Math.Max(t.H, b.H);
        }
        return res;
    }

    // Wörter, deren Mittelpunkt im Rechteck liegt, in Lesereihenfolge; ohne Wortliste (alte IR) Textzeilen
    static List<string> WordsIn(IrPage pg, IrBox r)
    {
        const double tol = 0.6;
        bool In(double x, double y, double w, double h) { double cx = x + w / 2, cy = y + h / 2; return cx >= r.X - tol && cx <= r.X + r.W + tol && cy >= r.Y - tol && cy <= r.Y + r.H + tol; }
        if (pg.Words.Count > 0)
            return pg.Words.Where(w => In(w.X, w.Y, w.W, w.H)).OrderBy(w => Math.Round(w.Y / 2.0)).ThenBy(w => w.X).Select(w => w.Text).ToList();
        return pg.Lines.Where(l => In(l.X, l.Y, l.W, l.H)).OrderBy(l => Math.Round(l.Y)).ThenBy(l => l.X).Select(l => l.Text).ToList();
    }
}
}
