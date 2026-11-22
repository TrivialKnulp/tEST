using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace StfKit.Core
{

/// <summary>Gemeinsame Logik für CLI und HTTP-Dienst.</summary>
public static class Operations
{
    public static readonly string[] Versions = { "DB2014", "DB2025", "DB2026" };

    public sealed record DocGroup(string BaseName, string? Dir, string? Pdf, string? Docx, string? Doc);

    public static List<DocGroup> Scan(string dir, bool recursive)
    {
        var ext = new[] { ".pdf", ".docx", ".doc", ".rtf", ".odt" };
        return Directory.EnumerateFiles(dir, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => ext.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("~$"))
            .GroupBy(f => Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase)
            .Select(g => new DocGroup(Path.GetFileNameWithoutExtension(g.Key), Path.GetDirectoryName(g.Key),
                g.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)),
                g.FirstOrDefault(f => f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)),
                g.FirstOrDefault(f => f.EndsWith(".doc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".odt", StringComparison.OrdinalIgnoreCase))))
            .OrderBy(x => x.BaseName).ToList();
    }

    /// <summary>Mapping: {"Feld":"Platzhalter"} | {"mapping":[{field,placeholder}]} | [{field,placeholder}]</summary>
    public static Dictionary<string, string> ParseMap(JsonElement root)
    {
        var res = new Dictionary<string, string>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("mapping", out var m)) root = m;
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var it in root.EnumerateArray())
            {
                var f = it.GetProperty("field").GetString(); var p = NormPh(it.GetProperty("placeholder").GetString());
                if (!string.IsNullOrWhiteSpace(f) && !string.IsNullOrWhiteSpace(p)) res[f!] = p!;
            }
        else if (root.ValueKind == JsonValueKind.Object)
            foreach (var kv in root.EnumerateObject())
            {
                var p = kv.Value.ValueKind == JsonValueKind.String ? NormPh(kv.Value.GetString()) : null;
                if (!string.IsNullOrWhiteSpace(p)) res[kv.Name] = p!;
            }
        return res;
    }

    /// <summary>"&lt;A&gt;" -> "A"; mehrere Platzhalter ("&lt;A&gt; &lt;B&gt;") bleiben unverändert.</summary>
    static string? NormPh(string? v)
    {
        v = v?.Trim(); if (string.IsNullOrEmpty(v)) return v;
        return System.Text.RegularExpressions.Regex.IsMatch(v, @"^<[^<>]+>$") ? v[1..^1] : v;
    }

    public static Dictionary<string, string> ParseMap(string json) { using var d = JsonDocument.Parse(json); return ParseMap(d.RootElement); }

    public static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(Path.GetFileName(name).Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(s) ? "formular" : s;
    }

    /// <summary>Vorhandene Dateien nie überschreiben (z. B. von Hand korrigierte .stf): name.stf, name_KI.stf, name_KI2.stf …</summary>
    public static string UniquePath(string dir, string baseName, string ext)
    {
        var p = Path.Combine(dir, baseName + ext);
        for (int i = 1; File.Exists(p); i++) p = Path.Combine(dir, baseName + (i == 1 ? "_KI" : "_KI" + i) + ext);
        return p;
    }

    public sealed record GenResult(string Stf, string Version, int Pages, int Texts, List<string> Placeholders,
        List<string> UnmappedFields, int LinesTodo, int ImagesTodo, int Lines = 0, int SlotsFilled = 0, AdaptReport? Adapt = null, int PicturesMade = 0, int TextMapped = 0,
        List<string>? AutoPlaceholders = null, List<string>? UnassignedHighlights = null, List<string>? JoinedPlaceholders = null);

    static bool m1x(KnowledgeBase? kb) => kb != null;

    public static GenResult Generate(IrDocument ir, string outPath, string version, IDictionary<string, string> map, string eol = "\r\n", KnowledgeBase? kb = null, IDictionary<string, SlotDef>? slots = null,
        StfDocument? template = null, string mode = "scratch", string templateName = "", IDictionary<string, string>? textMap = null, IEnumerable<string>? catalog = null)
    {
        if (!Versions.Contains(version)) throw new ArgumentException("Unbekannte Version: " + version);
        kb?.Apply();                                    // gelernte Kalibrierung aktivieren
        // Kommentare mit Platzhalter-Angabe sind eindeutig: sie gelten vor allem anderen (explizites textMap hat Vorrang)
        var tm = new Dictionary<string, string>(ir.Notes.CommentPlaceholders);
        if (textMap != null) foreach (var kv in textMap) tm[kv.Key] = kv.Value;
        // Gelbe/rote Markierungen ohne Kommentar: Platzhalter nach Wertetyp (Datum/Betrag/Jahr); alles Übrige wird gemeldet, nicht geraten
        // Word-Kommentare sind sichere Belege: im Gedächtnis ablegen, damit dieselbe Formulierung später auch in PDFs ohne Kommentar erkannt wird
        if (kb != null && ir.Notes.CommentPlaceholders.Count > 0) { foreach (var kv in ir.Notes.CommentPlaceholders) kb.PhraseLearn(kv.Key, kv.Value, "Word-Kommentar"); kb.Save(); }
        var auto = HighlightPlaceholders.Derive(ir, tm);
        foreach (var kv in auto.Map) tm[kv.Key] = kv.Value;
        // Markierungen mit Platzhalter in Worten: 1) Gedächtnis, 2) eindeutige Ähnlichkeit zum Katalog; sonst Vorschlag im Bericht (nichts wird geraten)
        var catNames = catalog?.ToList() ?? new List<string>(); var stillOpen = new List<string>(); var memAssigned = new List<string>();
        foreach (var t in auto.OpenTexts ?? new List<string>())
        {
            var mem = kb?.PhraseLookup(t);
            if (mem != null) { tm[t] = mem; memAssigned.Add($"{t} → <{mem}> (aus Gedächtnis)"); continue; }
            // Eine Markierung kann mehrere <Platzhalter in Worten> enthalten ("<erster Tag> - <Tag vor dem Umzug>:"): jeden Teil einzeln zuordnen
            var segs = System.Text.RegularExpressions.Regex.Matches(t, "<([^<>]{2,80})>").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
            if (segs.Count > 1)
            {
                var ok = new List<string>(); var bad = new List<string>();
                foreach (var sg in segs)
                {
                    var m1 = kb?.PhraseLookup(sg); var c1 = PhraseMatcher.Suggest(sg, catNames); var s1 = m1 == null ? PhraseMatcher.Confident(c1) : null;
                    var name = m1 ?? s1?.Name; if (name != null) { tm["<" + sg + ">"] = name; ok.Add($"{sg} → <{name}>"); } else bad.Add(sg);
                }
                if (ok.Count > 0) memAssigned.Add(string.Join("; ", ok) + (m1x(kb) ? " (aus Gedächtnis/Katalog)" : ""));
                if (bad.Count == 0) continue;
                stillOpen.Add(t + " → noch offen: " + string.Join(", ", bad)); continue;
            }
            var cand = PhraseMatcher.Suggest(t, catNames); var sure = PhraseMatcher.Confident(cand);
            if (sure != null) { tm[t] = sure.Name; memAssigned.Add($"{t} → <{sure.Name}> (Katalog-Treffer {sure.Score:0.00})"); continue; }
            var parts = PhraseMatcher.Parts(t, catNames);
            if (parts.Count > 1 && parts.Any(x => x.Best != null)) { stillOpen.Add($"{t} → Teile: " + string.Join(" | ", parts.Select(x => x.Best != null ? $"{x.Part} → {x.Best.Name} ({x.Best.Score:0.00})" : $"{x.Part} → ?"))); continue; }
            stillOpen.Add(cand.Count > 0 ? $"{t} → Vorschlag: " + string.Join(", ", cand.Select(c => $"{c.Name} ({c.Score:0.00})")) : t);
        }
        if (auto.OpenTexts != null) { auto.Unassigned.RemoveAll(u => auto.OpenTexts.Any(t => u.StartsWith(t + " ["))); auto.Unassigned.AddRange(stillOpen); auto.Assigned.AddRange(memAssigned); }
        StfDocument doc; IrToStf.Report rep; AdaptReport? ad = null;
        if (template != null && mode == "textonly")
        {
            // Word ohne PDF: nur Text. Vorlage bleibt (Positionen, Linien, Bilder), statische Texte werden aus dem Word aktualisiert.
            var paras = ir.Paragraphs.Select(t => IrToStf.TransformText(t, map, tm)).ToList();
            (doc, ad) = FormAdapter.TextOnly(template, paras, templateName);
            var open = new SortedSet<string>(paras.SelectMany(t => System.Text.RegularExpressions.Regex.Matches(t, "«([^»]+)»").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value)));
            rep = new IrToStf.Report(doc.Items.Count(e => e.Type is "Text" or "String"), 0, 0, open, 0, 0, 0, 0, 0, null);
        }
        else (doc, rep) = IrToStf.Convert(ir, version, map, eol, true, kb, slots, tm);
        if (template != null && mode == "inherit") (doc, ad) = FormAdapter.Inherit(doc, template, templateName);
        else if (template != null && mode == "bescheid") (doc, ad) = FormAdapter.EditTemplate(doc, template, templateName);
        else if (template != null && mode == "reuse") (doc, ad) = FormAdapter.EditTemplate(doc, template, templateName, true, "reuse");     // (nahezu) gleiches Formular vorhanden: Vorlage vollständig behalten
        else if (mode == "reuse") throw new ArgumentException("Modus reuse: es muss eine vorhandene Vorlage angegeben werden");
        else if (mode == "bescheid") throw new ArgumentException("Gebührenbescheid: es muss eine vorhandene Vorlage angegeben werden (Formular lässt sich in Aeneas nicht neu anlegen)");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        doc.Save(outPath);
        return new GenResult(outPath, doc.Version, doc.Pages().Count, rep.Texts,
            doc.Items.SelectMany(e => e.Placeholders).Distinct().ToList(),
            ir.Fields.Where(f => !map.ContainsKey(f)).ToList(), rep.LinesTodo, rep.ImagesTodo, rep.Lines, rep.SlotsFilled, ad, rep.PicturesMade, rep.TextMapped,
            auto.Assigned, auto.Unassigned, rep.Joined);
    }

    static string Q(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";

    /// <summary>Zeile in manifest.csv ergänzen (Eingabe für FormulareImport in Aeneas).</summary>
    public static string AppendManifest(string outDir, string file, string name, string art, string? kategorie, string modus, string? ziel, string? vorlage, double? sim, string hinweise)
    {
        var mf = Path.Combine(outDir, "manifest.csv");
        var sb = new System.Text.StringBuilder();
        if (!File.Exists(mf)) sb.Append("Datei;Name;Art;Kategorie;Modus;Ziel;Vorlage;Aehnlichkeit;Hinweise\r\n");
        sb.Append(string.Join(";", Q(file), Q(name), Q(art), Q(kategorie), Q(modus), Q(ziel), Q(vorlage), sim?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "", Q(hinweise.Replace("\r", " ").Replace("\n", " ")))).Append("\r\n");
        File.AppendAllText(mf, sb.ToString(), new System.Text.UTF8Encoding(true));
        return mf;
    }
}
}