using System;
using System.Collections.Generic;
using System.Linq;

namespace StfKit.Core
{

/// <summary>Misst, wie genau das erzeugte Formular (.stf) das Layout des gesendeten PDFs trifft: Textzeilen werden über den (platzhalterfreien) Text gepaart,
/// dann die Abweichung von Position gemessen. Sinn: ein Formular, das nur die Texte der ähnlichsten Vorlage übernimmt, aber deren Geometrie behält
/// ("bescheid"/"inherit"), druckt anders als das Original - das soll nicht unbemerkt bleiben.</summary>
public sealed record LayoutResult(int OriginalLines, int Matched, int Within15, double Within15Share, double MedianDx, double MedianDy, double MaxAbsDy, double SlopeDyPerMm)
{
    public bool Poor => Matched >= 10 && Within15Share < 0.6;
    public string Describe() => $"{Within15} von {Matched} zuordenbaren Zeilen liegen innerhalb 1,5 mm am Original (Median dx {MedianDx.ToString("+0.0;-0.0;0.0")} mm, dy {MedianDy.ToString("+0.0;-0.0;0.0")} mm, größte vertikale Abweichung {MaxAbsDy:0.0} mm" + (Math.Abs(SlopeDyPerMm) > 0.02 ? $"; die vertikale Abweichung wächst um {SlopeDyPerMm * 100:0.0} mm je 100 mm" : "") + ")";
}

public static class LayoutCheck
{
    public static LayoutResult Measure(IrDocument original, IrDocument generated)
    {
        var dxs = new List<double>(); var dys = new List<double>(); var ys = new List<double>(); int origCount = 0, within = 0;
        for (int p = 0; p < Math.Min(original.Pages.Count, generated.Pages.Count); p++)
        {
            var o = original.Pages[p].Lines.Select(l => (L: l, N: Compare.Norm(l.Text))).Where(x => x.N.Length >= 6).ToList(); origCount += o.Count;
            var pairs = new List<(IrLine G, IrLine O, double D)>();
            foreach (var g in generated.Pages[p].Lines)
            {
                var gn = Compare.Norm(g.Text); if (gn.Length < 6) continue;
                foreach (var x in o.Where(x => x.N == gn)) { if (Math.Abs(x.L.Y - g.Y) > 40 || Math.Abs(x.L.X - g.X) > 40) continue; pairs.Add((g, x.L, Math.Abs(x.L.X - g.X) + Math.Abs(x.L.Y - g.Y))); }
            }
            var usedG = new HashSet<IrLine>(); var usedO = new HashSet<IrLine>();     // 1:1: jede Original-Zeile höchstens einmal zählen
            foreach (var pr in pairs.OrderBy(q => q.D))
            {
                if (usedG.Contains(pr.G) || usedO.Contains(pr.O)) continue; usedG.Add(pr.G); usedO.Add(pr.O);
                double dx = pr.G.X - pr.O.X, dy = pr.G.Y - pr.O.Y; dxs.Add(dx); dys.Add(dy); ys.Add(pr.O.Y);
                if (Math.Abs(dx) <= 1.5 && Math.Abs(dy) <= 1.5) within++;
            }
        }
        if (dys.Count == 0) return new LayoutResult(origCount, 0, 0, 0, 0, 0, 0, 0);
        double Med(List<double> v) { var s = v.OrderBy(x => x).ToList(); int n = s.Count; return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2; }
        double my = ys.Average(), md = dys.Average(), cov = 0, var_ = 0; for (int i = 0; i < ys.Count; i++) { cov += (ys[i] - my) * (dys[i] - md); var_ += (ys[i] - my) * (ys[i] - my); }
        return new LayoutResult(origCount, dys.Count, within, Math.Round((double)within / dys.Count, 3), Math.Round(Med(dxs), 2), Math.Round(Med(dys), 2), Math.Round(dys.Max(Math.Abs), 1), var_ > 1 ? Math.Round(cov / var_, 4) : 0);
    }
}
}