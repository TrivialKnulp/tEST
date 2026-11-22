using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace StfKit.Core
{

/// <summary>Auswertung an einer Prüfliste (CSV: Dokument;Erwartet - erwartetes Formular, leer = es gibt kein passendes). Liefert Precision@1, Recall@5, Trefferquote "kein Treffer",
/// falsche Treffer und eine Schwellen-Tabelle, aus der man 0.75/0.45 an echten Dokumenten kalibriert (Lastenheft 7.1).</summary>
public static class Eval
{
    public sealed record Case(string Doc, string Expected, List<(string Name, double Score)> Hits);

    public static List<(string Doc, string Expected)> ReadExpected(string csv) =>
        File.ReadLines(csv, Encoding.UTF8).Skip(1).Where(l => l.Trim().Length > 0).Select(l => { var c = l.Split(';'); return (c[0].Trim('"', ' '), c.Length > 1 ? c[1].Trim('"', ' ') : ""); }).ToList();

    static bool Same(string hit, string expected) { var a = Compare.Norm(hit.Replace('_', ' ')); var b = Compare.Norm(expected.Replace('_', ' ')); return a == b || b.Length > 0 && a.StartsWith(b + " "); }   // erwartet darf der Basisname sein (Treffer heißen Name_Nr_Mandant)

    public static string Report(List<Case> cases, double high, double min)
    {
        var sb = new StringBuilder(); var pos = cases.Where(c => c.Expected != "").ToList(); var neg = cases.Where(c => c.Expected == "").ToList();
        int p1 = pos.Count(c => c.Hits.Count > 0 && Same(c.Hits[0].Name, c.Expected)), r5 = pos.Count(c => c.Hits.Any(h => Same(h.Name, c.Expected)));
        string P(int a, int b) => b == 0 ? "-" : (100.0 * a / b).ToString("0.#", CultureInfo.InvariantCulture) + " %";
        sb.AppendLine($"Dokumente: {cases.Count} (mit erwarteter Vorlage {pos.Count}, ohne passende Vorlage {neg.Count})");
        sb.AppendLine($"Precision@1: {P(p1, pos.Count)} ({p1}/{pos.Count})   Recall@5: {P(r5, pos.Count)} ({r5}/{pos.Count})");
        int fpAuto = cases.Count(c => c.Hits.Count > 0 && c.Hits[0].Score >= high && (c.Expected == "" || !Same(c.Hits[0].Name, c.Expected)));
        sb.AppendLine($"Falsche Treffer mit Stufe 'hoch' (>= {high:0.00}): {fpAuto}   <- sollte 0 sein, sonst Schwelle erhöhen");
        int nm = neg.Count(c => c.Hits.Count == 0 || c.Hits[0].Score < min);
        sb.AppendLine($"'Kein Treffer' richtig erkannt (< {min:0.00}): {P(nm, neg.Count)} ({nm}/{neg.Count})");
        sb.AppendLine("\nSchwelle;Treffer>=Schwelle;davon richtig;Precision;Abgedeckt");
        foreach (var t in new[] { 0.3, 0.4, 0.45, 0.5, 0.6, 0.7, 0.75, 0.8, 0.85, 0.9 })
        {
            var sel = cases.Where(c => c.Hits.Count > 0 && c.Hits[0].Score >= t).ToList(); int ok = sel.Count(c => c.Expected != "" && Same(c.Hits[0].Name, c.Expected));
            sb.AppendLine($"{t:0.00};{sel.Count};{ok};{(sel.Count == 0 ? "-" : P(ok, sel.Count))};{P(sel.Count, cases.Count)}");
        }
        sb.AppendLine("\nFalsch / nicht gefunden:");
        foreach (var c in pos.Where(c => c.Hits.Count == 0 || !Same(c.Hits[0].Name, c.Expected)))
            sb.AppendLine($"- {c.Doc}: erwartet '{c.Expected}', Treffer: {(c.Hits.Count == 0 ? "-" : string.Join(" | ", c.Hits.Take(3).Select(h => $"{h.Name} {h.Score:0.00}")))}{(c.Hits.Any(h => Same(h.Name, c.Expected)) ? "  (in Top 5)" : "")}");
        return sb.ToString();
    }
}
}
