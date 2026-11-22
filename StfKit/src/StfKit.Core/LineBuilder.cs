using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace StfKit.Core
{

/// <summary>Leitet @Line-Elemente aus den Linien/Rahmen einer PDF-Seite ab.
/// Rahmen (4 Kanten) → Rect; übrige waagerechte/senkrechte Linien → Horizontal/Vertical (5 mm hohe/breite Box um die Linie).
/// Offsets sind aus dem Vergleich mit von Hand gebauten Formularen (Hagenbach) geschätzt.</summary>
public static class LineBuilder
{
    public static double RectDx = 0.1, RectDy = 0.7, RectDw = -0.7, RectDh = 0.3;   // Rahmen: Box relativ zur PDF-Kante
    public static double HMid = 0.3, VMid = 0.2;                                      // Linienmitte = PDF-Linie + Offset
    public static double BoxDx = 0.1, BoxDy = 0.7;                                   // Checkbox-Rahmen relativ zum PDF-Widget (Median aus 50 Paaren Hagenbach)
    public const double Band = 5.0, Thickness = 0.2, Tol = 0.8;

    sealed class Seg { public double C, A, B; public bool Used; }   // C = feste Koordinate, [A,B] = Ausdehnung

    static List<Seg> Merge(IEnumerable<Seg> segs)
    {
        var res = new List<Seg>();
        foreach (var g in segs.OrderBy(s => s.C).ThenBy(s => s.A))
        {
            var last = res.LastOrDefault(r => Math.Abs(r.C - g.C) <= 0.3 && g.A <= r.B + 0.5);
            if (last != null) last.B = Math.Max(last.B, g.B); else res.Add(new Seg { C = g.C, A = g.A, B = g.B });
        }
        return res;
    }

    static double Cover(IEnumerable<Seg> segs, double c, double a, double b, double tol = Tol)
    {
        double len = 0;
        foreach (var s in segs) if (Math.Abs(s.C - c) <= tol) len += Math.Max(0, Math.Min(s.B, b) - Math.Max(s.A, a));
        return b - a <= 0 ? 0 : len / (b - a);
    }

    public sealed record Result(List<Elem> Elems, int Rects, int Horizontals, int Verticals, int Checkboxes = 0, int TextFields = 0);

    public static Result FromRules(IrPage pg)
    {
        var hs = Merge(pg.Rules.Where(r => r.H < 0.6 && r.W >= 0.8).Select(r => new Seg { C = r.Y, A = r.X, B = r.X + r.W }));
        var vs = Merge(pg.Rules.Where(r => r.W < 0.6 && r.H >= 0.8).Select(r => new Seg { C = r.X, A = r.Y, B = r.Y + r.H }));
        var elems = new List<Elem>(); int nr = 0, nh = 0, nv = 0;

        // Rahmen-Kandidaten: zwei Waagerechte mit gleicher Ausdehnung + zwei Senkrechte, die die Höhe abdecken
        var cands = new List<(Seg Top, Seg Bot, Seg L, Seg R, double Area)>();
        foreach (var t in hs)
            foreach (var b in hs.Where(b => b.C > t.C + 1.5 && Math.Abs(b.A - t.A) <= Tol && Math.Abs(b.B - t.B) <= Tol))
            {
                var l = vs.FirstOrDefault(v => Math.Abs(v.C - t.A) <= Tol && v.A <= t.C + Tol && v.B >= b.C - Tol);
                var r = vs.FirstOrDefault(v => Math.Abs(v.C - t.B) <= Tol && v.A <= t.C + Tol && v.B >= b.C - Tol);
                if (l != null && r != null) cands.Add((t, b, l, r, (t.B - t.A) * (b.C - t.C)));
            }
        foreach (var c in cands.OrderByDescending(c => c.Area))
        {
            int fresh = new[] { c.Top.Used, c.Bot.Used, c.L.Used, c.R.Used }.Count(u => !u);
            if (fresh < 3) continue;                          // Zellen innerhalb eines schon erzeugten Rahmens überspringen
            c.Top.Used = c.Bot.Used = c.L.Used = c.R.Used = true;
            double x = c.Top.A + RectDx, y = c.Top.C + RectDy, w = (c.Top.B - c.Top.A) + RectDw, h = (c.Bot.C - c.Top.C) + RectDh;
            elems.Add(StfDocument.MakeLine(x, y, w, h, Thickness, "", StfDocument.LineKind.Rect)); nr++;
        }
        foreach (var h in hs.Where(s => !s.Used && s.B - s.A >= 2.0))
        {
            elems.Add(StfDocument.MakeLine(h.A, h.C + HMid - Band / 2, h.B - h.A, Band, Thickness, "", StfDocument.LineKind.Horizontal)); nh++;
        }
        foreach (var v in vs.Where(s => !s.Used && s.B - s.A >= 2.0))
        {
            elems.Add(StfDocument.MakeLine(v.C + VMid - Band / 2, v.A, Band, v.B - v.A, Thickness, "", StfDocument.LineKind.Vertical)); nv++;
        }
        // AcroForm-Checkboxen (5x5 mm) -> Rahmen wie von Hand gebaut
        int nc = 0;
        foreach (var w in pg.Widgets.Where(w => w.Kind == "checkbox"))
        {
            elems.Add(StfDocument.MakeLine(w.X + BoxDx, w.Y + BoxDy, w.W, w.H, Thickness, "", StfDocument.LineKind.Rect)); nc++;
        }
        return new Result(elems, nr, nh, nv, nc, pg.Widgets.Count(w => w.Kind == "text"));
    }
}
}