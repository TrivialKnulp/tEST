using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace StfKit.Core
{

/// <summary>Ordnet eine Markierung, in der ein Platzhalter in Worten steht ("&lt;Datum letzter Gebührenbescheid&gt;"), einem echten Aeneas-Platzhalter zu.
/// Reihenfolge: 1) Gedächtnis (frühere Word-Kommentare/Korrekturen, KnowledgeBase.Phrases) -> sicher; 2) Ähnlichkeit zu den Platzhaltern im Korpus
/// (Katalog) -> nur bei hoher, eindeutiger Übereinstimmung automatisch, sonst als Vorschlag im Bericht. Es wird nie ohne Beleg geraten.</summary>
public static class PhraseMatcher
{
    public const double AutoThreshold = 0.85, MinGap = 0.15, SuggestThreshold = 0.40;

    public sealed record Candidate(string Name, double Score);

    static readonly string[][] Synonyms = {
        new[] { "datum", "date", "stichtag" }, new[] { "betrag", "summe", "gesamtbetrag" }, new[] { "strasse", "str", "anschrift" },
        new[] { "nummer", "nr", "no" }, new[] { "telefon", "tel", "festnetz", "fernsprecher" }, new[] { "mail", "email" }, new[] { "plz", "postleitzahl" },
        new[] { "ort", "stadt", "wohnort" }, new[] { "raum", "zimmer" }, new[] { "flaeche", "qm", "wohnflaeche" }, new[] { "gebuehr", "gebuehren", "nutzungsgebuehr" },
        new[] { "anrede", "anredeform" }, new[] { "name", "nachname", "familienname" }, new[] { "vorname", "rufname" }, new[] { "tag", "tage" },
    };
    static readonly Dictionary<string, string> Canon = Synonyms.SelectMany(g => g.Select(w => (w, g[0]))).GroupBy(x => x.w).ToDictionary(g => g.Key, g => g.First().Item2);
    static readonly Regex CamelRx = new(@"(?<=[a-zäöüß])(?=[A-ZÄÖÜ])", RegexOptions.Compiled);

    /// <summary>Text normalisieren (Gedächtnis-Schlüssel): ohne &lt;&gt;, Satzzeichen am Rand, Kleinschreibung, einfache Leerzeichen.</summary>
    public static string Norm(string s) => Regex.Replace((s ?? "").Replace('<', ' ').Replace('>', ' ').Trim().Trim(':', ';', ',', '.', '-', ' ').ToLowerInvariant(), @"\s+", " ");

    static string Fold(string s) => s.ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");

    static List<string> Tokens(string s)
    {
        s = CamelRx.Replace(s ?? "", " ");                                  // BescheidDatum -> Bescheid Datum
        var res = new List<string>();
        foreach (var t0 in Regex.Split(Fold(s), @"[^a-z0-9]+").Where(x => x.Length > 0))
        {
            if (Canon.TryGetValue(t0, out var c0)) { res.Add(c0); continue; }      // Synonym vor dem Kürzen prüfen (nachname -> name)
            var t = t0; if (t.Length > 4) foreach (var suf in new[] { "en", "er", "es", "e", "s", "n" }) if (t.EndsWith(suf) && t.Length - suf.Length >= 4) { t = t.Substring(0, t.Length - suf.Length); break; }
            if (t.Length < 2 || t.All(char.IsDigit)) continue;                // Einzelzeichen und reine Zahlen ('1' in K_1_Name) sind keine Wörter
            res.Add(Canon.TryGetValue(t, out var c) ? c : t);
        }
        return res;
    }

    static double TokenScore(string a, string b) => a == b ? 1.0 : (a.Length >= 5 && b.Length >= 5 && (a.Contains(b) || b.Contains(a))) ? 0.6 : 0.0;   // Datum~Datum, Gebührenbescheid~Bescheid

    /// <summary>Ähnlichkeit 0..1 zwischen einem Text in Worten und einem Platzhalter-Namen. Gewicht liegt auf den Wörtern des Textes (Kürzel wie UVOR_/B_ im Namen stören nicht stark).</summary>
    public static double Score(string phrase, string placeholderName)
    {
        var p = Tokens(Norm(phrase)).Distinct().ToList(); var n = Tokens(placeholderName).Distinct().ToList();
        if (p.Count == 0 || n.Count == 0 || !Norm(phrase).Any(char.IsLetter)) return 0;
        if (Fold(Norm(phrase)).Replace(" ", "") == Fold(placeholderName).Replace("_", "").ToLowerInvariant()) return 1.0;
        double inter = p.Sum(t => n.Max(u => TokenScore(t, u)));
        double recall = inter / p.Count, precision = inter / n.Count;
        return Math.Round(0.7 * recall + 0.3 * precision, 3);
    }

    public static List<Candidate> Suggest(string phrase, IEnumerable<string> catalogNames, int top = 3)
        => catalogNames.Where(n => !string.IsNullOrWhiteSpace(n) && !n.Any(char.IsWhiteSpace)).Distinct().Select(nm => new Candidate(nm, Score(phrase, nm))).Where(c => c.Score >= SuggestThreshold).OrderByDescending(c => c.Score).Take(top).ToList();

    /// <summary>Eindeutiger Treffer, bei dem man automatisch zuordnen darf (hohe Übereinstimmung und deutlicher Abstand zum Zweitbesten).</summary>
    /// <summary>Zusammengesetzte Beschriftung ("Vorname / Nachname", "PLZ/ Ort"): jeder Teil einzeln, jeweils bester Katalog-Treffer. Nur als Hinweis für den Menschen.</summary>
    public static List<(string Part, Candidate? Best)> Parts(string phrase, IEnumerable<string> catalogNames)
    {
        var parts = Regex.Split(Norm(phrase), @"\s*/\s*|\s*,\s*|\s+und\s+").Where(x => x.Length >= 2 && x.Any(char.IsLetter)).ToList();
        if (parts.Count < 2) return new();
        var names = catalogNames.ToList();
        return parts.Select(pt => (pt, Suggest(pt, names, 1).FirstOrDefault())).ToList();
    }

    public static Candidate? Confident(List<Candidate> c)
        => c.Count > 0 && c[0].Score >= AutoThreshold && (c.Count == 1 || c[0].Score - c[1].Score >= MinGap) ? c[0] : null;
}
}
