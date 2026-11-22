using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace StfKit.Core
{

/// <summary>Strukturierter Vergleich Kundenoriginal gegen gewählte Vorlage (Lastenheft 11): Element | Existing | Original | Aktion | Begründung.
/// Aktionen: Übernehmen, Ändern, Ersetzen, Hinzufügen, Entfernen, Prüfen. Grundlage ist der Text (normalisiert ohne Platzhalter/Markup) und grobe Strukturzahlen.</summary>
public static class Vergleich
{
    public sealed record Row(string Element, string Existing, string Original, string Aktion, string Grund);

    static double Jaccard(string a, string b)
    {
        var x = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries)); var y = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (x.Count == 0 || y.Count == 0) return 0; int i = x.Count(y.Contains); return (double)i / (x.Count + y.Count - i);
    }

    public static List<Row> Run(IrDocument ir, StfDocument tpl, double changeThreshold = 0.55)
    {
        var rows = new List<Row>();
        // Einheiten = Absätze/Textfelder (nicht Zeilen): Zeilenumbrüche im PDF dürfen keine Scheinunterschiede erzeugen
        var tplLines = tpl.Items.Where(e => e.Type is "Text" or "String").Select(e => string.Join(" ", e.Lines.Select(l => l.Trim()).Where(l => l.Length > 0))).Where(l => l.Length > 0).ToList();
        var used = new bool[tplLines.Count]; var tn = tplLines.Select(l => Compare.Norm(l)).ToList();
        var blocks = new List<(int Page, string Text, double Size)>();
        for (int p = 0; p < ir.Pages.Count; p++)
        {
            IrLine? prev = null; var sb = new StringBuilder(); double size = 0;
            foreach (var l in ir.Pages[p].Lines.Where(x => x.Text.Trim().Length > 0).OrderBy(x => x.Y).ThenBy(x => x.X))
            {
                bool cont = prev != null && l.Y - prev.Y < prev.H * 1.6 + 0.5 && l.Y - prev.Y > -prev.H * 0.5 && Math.Abs(l.X - prev.X) < 12 && Math.Abs(l.Size - prev.Size) < 1.5;
                if (!cont && sb.Length > 0) { blocks.Add((p + 1, sb.ToString().Trim(), size)); sb.Clear(); }
                if (sb.Length == 0) size = l.Size; sb.Append(l.Text.Trim()).Append(' '); prev = l;
            }
            if (sb.Length > 0) blocks.Add((p + 1, sb.ToString().Trim(), size));
        }
        var titleBlock = blocks.Where(b => b.Page == 1).OrderByDescending(b => b.Size).Select(b => b.Text).FirstOrDefault();

        foreach (var (page, text, _) in blocks)
        {
            var on = Compare.Norm(text); bool isTitle = text == titleBlock && text.Length > 3;
            string kind = isTitle ? "Titel" : text.Contains('<') ? "Platzhalter" : "Textblock";
            if (on.Length < 3 && !text.Contains('<')) continue;                                    // Zierzeichen/Seitenzahlen
            int hit = -1;
            for (int i = 0; i < tn.Count && hit < 0; i++) if (!used[i] && tn[i] == on) hit = i;
            if (hit >= 0) { used[hit] = true; rows.Add(new Row(kind, tplLines[hit], text, "Übernehmen", "Wortlaut gleich")); continue; }
            double best = 0; int bi = -1;
            for (int i = 0; i < tn.Count; i++) if (!used[i]) { var j = Jaccard(tn[i], on); if (j > best) { best = j; bi = i; } }
            if (bi >= 0 && best >= changeThreshold) { used[bi] = true; rows.Add(new Row(kind, tplLines[bi], text, isTitle ? "Ändern" : "Ersetzen", $"ähnlich ({best:0.00}), Wortlaut weicht ab")); }
            else rows.Add(new Row(kind, "", text, "Hinzufügen", $"S{page}: kein Gegenstück in der Vorlage"));
        }
        for (int i = 0; i < tplLines.Count; i++)
        {
            if (used[i]) continue;
            bool ph = tplLines[i].Contains('<');
            if (tn[i].Length < 3 && !ph) continue;
            rows.Add(ph ? new Row("Platzhalter", tplLines[i], "", "Prüfen", "Vorlage hat Platzhalter, im Original keine passende Stelle")
                        : new Row("Textblock", tplLines[i], "", "Entfernen", "kommt im Original nicht vor"));
        }
        // Struktur
        int tPages = tpl.Pages().Count, oPages = Math.Max(1, ir.Pages.Count);
        rows.Add(new Row("Seiten", tPages.ToString(), oPages.ToString(), tPages == oPages ? "Übernehmen" : "Prüfen", tPages == oPages ? "gleiche Seitenzahl" : "Seitenzahl weicht ab"));
        int tLines = tpl.Items.Count(e => e.Type == "Line"), oLines = ir.Pages.Sum(p => p.Rules.Count + p.Rects.Count);
        rows.Add(new Row("Linien/Rahmen", tLines.ToString(), oLines.ToString(), Math.Abs(tLines - oLines) <= Math.Max(2, oLines / 10) ? "Übernehmen" : "Prüfen", "Anzahl Linien und Rahmen"));
        int tPics = tpl.Items.Count(e => e.Type is "Picture" or "PPicture"), oPics = ir.Pages.Sum(p => p.Images.Count(i => !i.Background));
        rows.Add(new Row("Bilder", tPics.ToString(), oPics.ToString(), tPics == oPics ? "Übernehmen" : "Prüfen", "Logos/Bilder"));
        return rows;
    }

    public static string Summary(List<Row> rows) => string.Join(", ", rows.Where(r => r.Aktion != "Übernehmen").GroupBy(r => r.Aktion).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}")) is { Length: > 0 } s ? s : "alles übernommen";

    public static void Save(List<Row> rows, string path)
    {
        string Q(string v) => "\"" + v.Replace("\"", "'").Replace("\r", " ").Replace("\n", " / ") + "\"";
        var sb = new StringBuilder("Element;Existing;Original;Aktion;Begruendung\r\n");
        foreach (var r in rows) sb.Append(string.Join(";", Q(r.Element), Q(r.Existing), Q(r.Original), r.Aktion, Q(r.Grund))).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
}
