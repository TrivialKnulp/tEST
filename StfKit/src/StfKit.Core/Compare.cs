using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace StfKit.Core
{

/// <summary>Vergleicht eine erzeugte .stf mit einer von Hand gebauten Referenz (Texte + Linien/Rahmen).
/// Messinstrument für die Qualität der automatischen Erzeugung.</summary>
public static class Compare
{
    public sealed record LineStat(string Kind, int Reference, int Generated, int Matched);
    public sealed record TextStat(int ReferenceBoxes, int GeneratedBoxes, int Candidates, int Found, int Within, double MedianDx, double MedianDy);
    public sealed record PhStat(int ReferenceBoxes, int GeneratedBoxes, int Matched, int RefPlaceholders, int FoundPlaceholders);
    public sealed record Result(List<LineStat> Lines, TextStat Text, double TolMm, PhStat? Ph = null);

    public static string Kind(Elem e)
    {
        var b = e.Body;
        return b[4] == "TRUE" && b[8] == "TRUE" ? "Rect" : b[4] == "TRUE" ? "Horiz" : b[8] == "FALSE" ? "Vert" : "Fill?";
    }

    static readonly Regex Markup = new(@"\[[a-z#0-9A-F]*\]", RegexOptions.Compiled), Ph = new("<[^>]*>", RegexOptions.Compiled), Ws = new(@"\s+", RegexOptions.Compiled);
    public static string Norm(string s) => Ws.Replace(Ph.Replace(Markup.Replace(s, ""), ""), " ").Trim().ToLowerInvariant();

    static double Median(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.OrderBy(x => x).ToList(); int n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
    }

    public static Result Run(StfDocument reference, StfDocument generated, double tol = 1.5)
    {
        var rp = reference.Pages(); var gp = generated.Pages(); int pages = Math.Min(rp.Count, gp.Count);
        var tot = new Dictionary<string, int>(); var hit = new Dictionary<string, int>(); var gtot = new Dictionary<string, int>();
        void Inc(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
        var dxs = new List<double>(); var dys = new List<double>(); int n = 0, found = 0, within = 0;

        for (int p = 0; p < pages; p++)
        {
            // ---- Linien
            var R = rp[p].Where(e => e.Type == "Line").Select(e => (K: Kind(e), B: e.Box!.Value)).ToList();
            var G = gp[p].Where(e => e.Type == "Line").Select(e => (K: Kind(e), B: e.Box!.Value)).ToList();
            var used = new HashSet<int>();
            foreach (var (k, b) in R)
            {
                Inc(tot, k); int best = -1; double bd = double.MaxValue;
                for (int i = 0; i < G.Count; i++)
                {
                    if (used.Contains(i) || G[i].K != k) continue;
                    var g = G[i].B;
                    double d = Math.Sqrt(Math.Pow(g.X + g.W / 2 - b.X - b.W / 2, 2) + Math.Pow(g.Y + g.H / 2 - b.Y - b.H / 2, 2));
                    bool ok = d < 3 && (k == "Vert" || Math.Abs(g.W - b.W) < 4) && (k == "Horiz" || Math.Abs(g.H - b.H) < 4);
                    if (ok && d < bd) { bd = d; best = i; }
                }
                if (best >= 0) { Inc(hit, k); used.Add(best); }
            }
            foreach (var g in G) Inc(gtot, g.K);

            // ---- Texte: erste Zeile der Referenz-Box <-> nächste erzeugte Box mit gleicher erster Zeile
            var GT = gp[p].Where(e => e.Type is "Text" or "String" && e.Lines.Count > 0).Select(e => (T: Norm(e.Lines[0]), B: e.Box!.Value)).ToList();
            foreach (var e in rp[p].Where(e => e.Type is "Text" or "String"))
            {
                var first = e.Lines.Select(Norm).FirstOrDefault(l => l.Length >= 3);
                if (first == null) continue;
                n++;
                var c = GT.Where(g => g.T.Length > 0 && (g.T == first || g.T.StartsWith(first, StringComparison.Ordinal) || (first.StartsWith(g.T, StringComparison.Ordinal) && g.T.Length >= 6))).ToList();
                if (c.Count == 0) continue;
                found++; var rb = e.Box!.Value;
                var b = c.Select(x => x.B).OrderBy(x => Math.Pow(x.X - rb.X, 2) + Math.Pow(x.Y - rb.Y, 2)).First();
                dxs.Add(b.X - rb.X); dys.Add(b.Y - rb.Y);
                if (Math.Abs(b.X - rb.X) <= tol && Math.Abs(b.Y - rb.Y) <= tol) within++;
            }
        }
        // ---- Platzhalter-Boxen: gleiche Platzhaltermenge, Mittelpunkt < 4 mm
        int phRef = 0, phGen = 0, phHit = 0; var rset = new HashSet<string>(); var gset = new HashSet<string>();
        for (int p = 0; p < pages; p++)
        {
            var RP = rp[p].Where(e => e.Type is "Text" or "String" && e.Placeholders.Any()).ToList();
            var GP = gp[p].Where(e => e.Type is "Text" or "String" && e.Placeholders.Any()).ToList(); var usedG = new HashSet<Elem>();
            phRef += RP.Count; phGen += GP.Count;
            foreach (var e in RP)
            {
                foreach (var x in e.Placeholders) rset.Add(x);
                var rb = e.Box!.Value; var rs = e.Placeholders.OrderBy(x => x).ToList();
                var m = GP.FirstOrDefault(g => !usedG.Contains(g) && g.Placeholders.OrderBy(x => x).SequenceEqual(rs)
                    && Math.Sqrt(Math.Pow(g.Box!.Value.X + g.Box!.Value.W / 2 - rb.X - rb.W / 2, 2) + Math.Pow(g.Box!.Value.Y + g.Box!.Value.H / 2 - rb.Y - rb.H / 2, 2)) < 4);
                if (m != null) { phHit++; usedG.Add(m); }
            }
            foreach (var g in GP) foreach (var x in g.Placeholders) gset.Add(x);
        }
        var ph = new PhStat(phRef, phGen, phHit, rset.Count, rset.Count(x => gset.Contains(x)));
        var lines = new[] { "Rect", "Horiz", "Vert", "Fill?" }.Where(k => tot.ContainsKey(k) || gtot.ContainsKey(k))
            .Select(k => new LineStat(k, tot.GetValueOrDefault(k), gtot.GetValueOrDefault(k), hit.GetValueOrDefault(k))).ToList();
        return new Result(lines, new TextStat(reference.Items.Count(e => e.Type == "Text"), generated.Items.Count(e => e.Type == "Text"), n, found, within, Median(dxs), Median(dys)), tol, ph);
    }

    public static string Format(Result r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("LINIEN  Typ     Referenz  erzeugt  passend  Recall");
        foreach (var l in r.Lines) sb.AppendLine($"        {l.Kind,-6} {l.Reference,8} {l.Generated,8} {l.Matched,8}   {(l.Reference == 0 ? 0 : (double)l.Matched / l.Reference):P0}");
        var t = r.Text;
        sb.AppendLine($"TEXTE   Referenz-Boxen {t.ReferenceBoxes} | erzeugte Boxen {t.GeneratedBoxes} (Fragmentierung x{(double)t.GeneratedBoxes / Math.Max(t.ReferenceBoxes, 1):F1})");
        sb.AppendLine($"        gefunden {t.Found}/{t.Candidates}, davon Position <= {r.TolMm}mm: {t.Within} | Median-Abweichung dx={t.MedianDx:+0.00;-0.00} dy={t.MedianDy:+0.00;-0.00} mm");
        if (r.Ph is { } q) sb.AppendLine($"PLATZH. Referenz-Boxen mit Platzhaltern {q.ReferenceBoxes} | erzeugt {q.GeneratedBoxes} | auf gleicher Stelle mit gleichem Inhalt {q.Matched} | Platzhalter-Namen {q.FoundPlaceholders}/{q.RefPlaceholders} gefunden");
        return sb.ToString();
    }
}
}