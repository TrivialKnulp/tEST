using System;
using System.Collections.Generic;
using System.Linq;

namespace StfKit.Core
{

public sealed record SnapResult(int Anchors, int MovedElements, int PagesAligned, List<string>? RemovedShadowed = null);

/// <summary>Richtet die Geometrie eines aus einer Vorlage erzeugten Formulars (Modus "bescheid"/"inherit") am gesendeten PDF aus.
/// Die Texte und Platzhalter der Vorlage bleiben, nur die Positionen ändern sich: Textboxen, deren Wortlaut im PDF wiedergefunden wird, rücken genau auf die PDF-Zeile;
/// alle übrigen Elemente (Platzhalter-Boxen, Linien, Bilder, Kästen) bekommen die an den Ankern auf ihrer Seite stückweise linear interpolierte vertikale Verschiebung.
/// Ausreißer (Anker, deren Verschiebung stark vom Umfeld abweicht) werden verworfen. Größe der Boxen bleibt unverändert.</summary>
public static class LayoutSnap
{
    const double Pt = 25.4 / 72.0, MaxShift = 40.0, OutlierTol = 8.0, Neighborhood = 35.0;

    public static SnapResult Apply(StfDocument doc, IrDocument ir)
    {
        var backup = StfDocument.Parse(doc.DumpBytes()); var pages = doc.Pages(); int anchorsTotal = 0, moved = 0, aligned = 0; var anchoredAll = new HashSet<Elem>(); var irByPage = new Dictionary<int, List<string>>();
        for (int p = 0; p < Math.Min(pages.Count, ir.Pages.Count); p++)
        {
            var irLines = ir.Pages[p].Lines.Select(l => (L: l, N: Compare.Norm(l.Text))).Where(x => x.N.Length >= 6).ToList();
            var anchors = new List<(Elem E, double Y, double Dx, double Dy)>();
            // Zuordnung 1:1 (gierig nach kleinstem Abstand): eine PDF-Zeile darf nur einem Block der Vorlage dienen - sonst landen z. B. beide Tabellenzeilen auf derselben PDF-Zeile
            var cands = new List<(Elem E, double Gx, double Gy, IrLine R, double D)>();
            foreach (var e in pages[p].Where(e => e.Type is "Text" or "String" && e.Box != null && e.Font != null))
            {
                var b = e.Box!.Value; double step = e.Font!.Size * 1.17 * Pt; var lines = e.Lines;
                for (int i = 0; i < lines.Count; i++)
                {
                    var n = Compare.Norm(lines[i]); if (n.Length < 6) continue;
                    double gx = b.X - IrToStf.Dx, gy = b.Y - IrToStf.Dy + i * step;
                    foreach (var x in irLines.Where(x => x.N == n))
                    {
                        if (Math.Abs(x.L.Y - gy) > MaxShift || Math.Abs(x.L.X - gx) > MaxShift) continue;
                        cands.Add((e, gx, gy, x.L, Math.Abs(x.L.X - gx) + Math.Abs(x.L.Y - gy)));
                    }
                }
            }
            var usedE = new HashSet<Elem>(); var usedR = new HashSet<IrLine>();
            foreach (var c in cands.OrderBy(c => c.D))
            {
                if (usedE.Contains(c.E) || usedR.Contains(c.R)) continue;
                usedE.Add(c.E); usedR.Add(c.R); anchors.Add((c.E, c.Gy, c.R.X - c.Gx, c.R.Y - c.Gy));
            }
            // Ausreißer verwerfen: Verschiebung weicht um mehr als OutlierTol vom Median der Nachbarn (auf der Seite, +-Neighborhood mm) ab
            var good = anchors.Where(a => { var nb = anchors.Where(o => o.E != a.E && Math.Abs(o.Y - a.Y) <= Neighborhood).Select(o => o.Dy).OrderBy(v => v).ToList(); return nb.Count < 2 || Math.Abs(a.Dy - nb[nb.Count / 2]) <= OutlierTol; }).ToList();
            if (good.Count < 3) continue;
            anchorsTotal += good.Count; aligned++; foreach (var g0 in good) anchoredAll.Add(g0.E); irByPage[p] = irLines.Select(x => x.N).ToList();
            var byY = good.GroupBy(a => Math.Round(a.Y, 1)).Select(g => (Y: g.Key, Dy: g.Average(a => a.Dy))).OrderBy(a => a.Y).ToList();
            double medDx = good.Select(a => a.Dx).OrderBy(v => v).ElementAt(good.Count / 2);
            double DyAt(double y)
            {
                if (y <= byY[0].Y) return byY[0].Dy; if (y >= byY[^1].Y) return byY[^1].Dy;
                for (int k = 1; k < byY.Count; k++) if (y <= byY[k].Y) { var a = byY[k - 1]; var c = byY[k]; return c.Y - a.Y < 1e-6 ? c.Dy : a.Dy + (c.Dy - a.Dy) * (y - a.Y) / (c.Y - a.Y); }
                return byY[^1].Dy;
            }
            var anch = good.ToDictionary(a => a.E, a => a);
            foreach (var e in pages[p])
            {
                if (e.Type is "Seite" or "Group" || e.Box == null) continue;
                var b = e.Box.Value;
                if (anch.TryGetValue(e, out var a)) e.SetBox(b.X + a.Dx, b.Y + a.Dy, b.W, b.H); else e.SetBox(b.X + medDx, b.Y + DyAt(b.Y), b.W, b.H);
                moved++;
            }
        }
        var removed = RemoveShadowed(doc, backup, pages, anchoredAll, irByPage);
        return new SnapResult(anchorsTotal, moved, aligned, removed);
    }

    /// <summary>Entfernt "Schatten" der Vorlage: statische Textboxen ohne Platzhalter, deren (längerer) Text im PDF NICHT vorkommt und die eine Textbox, die das PDF trifft,
    /// zu mindestens 30 % ihrer Fläche überdecken. Das sind typischerweise alte Vorlagetexte, die neben dem neu erzeugten Text des PDFs übrig geblieben sind.
    /// Boxen mit Platzhaltern, kurze Beschriftungen, Linien und Bilder bleiben. Group-Zähler werden angepasst; schlägt die Baumprüfung fehl, wird alles zurückgenommen.</summary>
    static List<string> RemoveShadowed(StfDocument doc, StfDocument backup, List<List<Elem>> pages, HashSet<Elem> anchored, Dictionary<int, List<string>> irByPage)
    {
        var removed = new List<string>(); var kill = new List<Elem>();
        for (int p = 0; p < pages.Count; p++)
        {
            if (!irByPage.TryGetValue(p, out var irNorms)) continue;
            var texts = pages[p].Where(e => e.Type is "Text" or "String" && e.Box != null).ToList();
            foreach (var b in texts)
            {
                if (anchored.Contains(b)) continue;
                var txt = string.Join(" ", b.Lines); if (txt.Contains('<') || txt.Contains("‹")) continue;
                var n = Compare.Norm(txt); if (n.Length < 12 || irNorms.Contains(n)) continue;
                var bb = b.Box!.Value; double areaB = Math.Max(0.01, bb.W * bb.H);
                bool shadow = texts.Any(a => a != b && anchored.Contains(a) && Overlap(a.Box!.Value, bb) / Math.Min(areaB, Math.Max(0.01, a.Box!.Value.W * a.Box!.Value.H)) >= 0.30);
                if (shadow) { kill.Add(b); removed.Add($"Seite {p + 1}: {txt.Replace("\n", " ")[..Math.Min(70, txt.Length)]}"); }
            }
        }
        if (kill.Count == 0) return removed;
        // Eltern-Groups finden und Zähler senken
        var parent = new Dictionary<Elem, Elem>(); int i = 0;
        void Walk(Elem g) { int n = int.Parse(g.Body[0]); for (int k = 0; k < n && i < doc.Items.Count; k++) { var c = doc.Items[i++]; parent[c] = g; if (c.Type == "Group") Walk(c); } }
        while (i < doc.Items.Count) { var e = doc.Items[i++]; if (e.Type == "Group") Walk(e); }
        foreach (var k in kill) { if (parent.TryGetValue(k, out var g)) g.Body[0] = (int.Parse(g.Body[0]) - 1).ToString(); doc.Items.Remove(k); }
        try { doc.ValidateTree(); }
        catch { doc.Items.Clear(); doc.Items.AddRange(backup.Items); doc.Header = backup.Header; return new List<string>(); }
        return removed;
    }

    static double Overlap((double X, double Y, double W, double H) a, (double X, double Y, double W, double H) b)
    {
        double w = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X), h = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }
}
}