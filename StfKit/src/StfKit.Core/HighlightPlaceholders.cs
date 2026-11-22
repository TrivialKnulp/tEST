using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace StfKit.Core
{

/// <summary>Gelbe/rote Markierungen aus Word OHNE Kommentar bekommen einen Platzhalter nach Wertetyp:
/// Datum → &lt;DATUM&gt;, Betrag → &lt;BETRAG&gt;, Jahr → &lt;JAHR&gt;. Kommentar-Platzhalter und explizites textMapping haben Vorrang.
/// Nicht erkennbare Werte werden nicht geraten, sondern gemeldet.</summary>
public static class HighlightPlaceholders
{
    // Namen der Standard-Platzhalter (gegen den Aeneas-Katalog prüfen!)
    public static string Datum = "DATUM", Betrag = "BETRAG", Jahr = "JAHR";

    static readonly Regex DateRx = new(@"\b\d{1,2}\.\d{1,2}\.(?:19|20)\d{2}\b", RegexOptions.Compiled);
    static readonly Regex AmountRx = new(@"(?<![\d.,])\d{1,3}(?:\.\d{3})*,\d{2}(?![\d,])(?:\s?(?:€|EUR|Euro))?|(?<![\d.,])\d+,\d{2}(?![\d,])(?:\s?(?:€|EUR|Euro))?", RegexOptions.Compiled);
    static readonly Regex YearRx = new(@"(?<![\d.,/])(?:19|20)\d{2}(?![\d.,/]\d|\d)", RegexOptions.Compiled);

    /// <summary>Markierung = Stelle, an der ein Platzhalter hin soll: Textmarker in jeder Farbe außer weiß/hellgrau, bzw. Schattierung, die gelb/rot oder dunkel ist
    /// (bei Haneke sind die Stellen meist dunkel markiert, teils gelb/rot). Name bleibt aus Kompatibilitätsgründen IsYellowOrRed.</summary>
    public static bool IsYellowOrRed(string color)
    {
        if (string.IsNullOrEmpty(color)) return false;
        if (color.StartsWith("HL:", StringComparison.OrdinalIgnoreCase))
            return !(color[3..].ToLowerInvariant() is "none" or "white" or "lightgray");
        if (color.StartsWith("SHD:", StringComparison.OrdinalIgnoreCase) && color.Length == 10 &&
            int.TryParse(color[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            int r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
            bool yellow = r >= 200 && g >= 200 && b <= 130, red = r >= 170 && g <= 90 && b <= 90;
            double lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            return yellow || red || lum < 0.5;                       // dunkel markiert
        }
        return false;
    }

    /// <summary>Feste Muster für Adress-/Namensfelder (Platzhalter-Beispieltexte im Word): Text → Ersatz im .stf (mit spitzen Klammern).</summary>
    static readonly Dictionary<string, string> NameRules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Vorname Nachname"] = "<VORNAME> <NAME>", ["Nachname, Vorname"] = "<NAME>, <VORNAME>", ["Nachname Vorname"] = "<NAME> <VORNAME>",
        ["Vorname"] = "<VORNAME>", ["Nachname"] = "<NAME>", ["Straße Hausnummer"] = "<STRASSE>", ["Strasse Hausnummer"] = "<STRASSE>",
        ["PLZ Ort"] = "<PLZ> <ORT>",
    };

    public sealed record Result(Dictionary<string, string> Map, List<string> Assigned, List<string> Unassigned, List<string>? OpenTexts = null);

    /// <param name="existing">Schon bekannte Zuordnungen (Kommentare, textMapping): deren Texte werden nicht überschrieben.</param>
    public static Result Derive(IrDocument ir, IDictionary<string, string> existing)
    {
        var notes = ir.Notes;
        var map = new Dictionary<string, string>(); var assigned = new List<string>(); var open = new List<string>(); var openTexts = new List<string>();
        foreach (var h in notes.Highlights.Where(h => IsYellowOrRed(h.Color)))
        {
            var text = h.Text.Trim();
            if (existing.Keys.Any(k => string.Equals(k, text, StringComparison.OrdinalIgnoreCase) || text.Contains(k, StringComparison.Ordinal))) continue;   // durch Kommentar bereits zugeordnet
            if (NameRules.TryGetValue(text, out var nameRepl)) { map[text] = nameRepl.Trim('<', '>'); assigned.Add($"{text} → {nameRepl}"); continue; }
            var found = new List<(string Lit, string Ph)>(); string rest = text;
            foreach (Match m in DateRx.Matches(text)) found.Add((m.Value, Datum));
            rest = DateRx.Replace(rest, " ");
            foreach (Match m in AmountRx.Matches(rest)) found.Add((m.Value.Trim(), Betrag));
            rest = AmountRx.Replace(rest, " ");
            foreach (Match m in YearRx.Matches(rest)) found.Add((m.Value, Jahr));
            if (found.Count == 0) { open.Add($"{text} [{h.Color}]"); openTexts.Add(text); continue; }
            foreach (var (lit, ph) in found.Distinct())
                if (!map.ContainsKey(lit) && !existing.ContainsKey(lit)) { map[lit] = ph; assigned.Add($"{lit} → <{ph}>"); }
        }
        // Datumsfeld (TIME/DATE) im Word: im PDF steht das Druckdatum (z. B. 04.11.2026), im docx nur das alte Feldergebnis (11.12.2024).
        // Datumsangaben im PDF, die im docx-Text gar nicht vorkommen, stammen aus dem Feld -> ebenfalls <DATUM>.
        if (notes.HasDateField && notes.Highlights.Any(h => IsYellowOrRed(h.Color) && DateRx.IsMatch(h.Text)))
            foreach (var l in ir.Pages.SelectMany(p => p.Lines))
                foreach (Match m in DateRx.Matches(l.Text))
                    if (!notes.DocDates.Contains(m.Value) && !map.ContainsKey(m.Value) && !existing.ContainsKey(m.Value))
                    { map[m.Value] = Datum; assigned.Add($"{m.Value} → <{Datum}> (Druckdatum aus Word-Datumsfeld)"); }
        return new Result(map, assigned, open, openTexts);
    }
}
}
