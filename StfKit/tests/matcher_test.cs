using System; using System.Linq; using StfKit.Core;
var cat = new[] { "F_Vorname", "F_Name", "SB_Vorname", "F_U_PLZ", "F_U_Ort", "F_KennNummer", "K_1_Name", "K_1_Funktion", "UVOR_BescheidDatum", "Datum", "B_Personen", "F_DebitorKennung" };
foreach (var ph in new[] { "1", "Vorname / Nachname", "PLZ/ Ort", "BG-Nummer", "Datum letzter Gebührenbescheid", "Debitor", "2" })
{
    var c = PhraseMatcher.Suggest(ph, cat); var parts = PhraseMatcher.Parts(ph, cat);
    Console.WriteLine($"{ph,-32} Vorschlag: {(c.Count == 0 ? "-" : string.Join(", ", c.Select(x => x.Name + " " + x.Score.ToString("0.00"))))}   Teile: {(parts.Count == 0 ? "-" : string.Join(" | ", parts.Select(x => x.Part + "→" + (x.Best?.Name ?? "?"))))}");
}
