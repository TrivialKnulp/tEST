using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StfKit.Core
{

/// <summary>Merkmale eines Formulars (aus .stf oder aus der PDF-IR) für Klassifikation und Ähnlichkeitssuche.</summary>
public sealed class FormFeatures
{
    public string Name { get; set; } = ""; public string? Path { get; set; }
    public List<string> Words { get; set; } = new();                 // Tokens der statischen Texte
    public HashSet<string> Placeholders { get; set; } = new();
    public int Pages { get; set; } = 1;
    public int TextBoxes { get; set; }
    public int Lines { get; set; }
    public int Checkboxes { get; set; }
    public int Tables { get; set; }
    public int Pictures { get; set; }
    public string? AliasOf { get; set; }                             // Kopfzeile "Alias=Name"
    public bool IsAliasStub => AliasOf != null && TextBoxes <= 3;       // nur Verweis auf ein anderes Formular
    public string? Art { get; set; }                                 // aus Ordner/Dateipräfix (ra_, uk_, fl_, ex_, dv_, sc_)
    public string Version { get; set; } = "";
    public string RawText { get; set; } = "";
    public string? HintKind { get; set; }                            // Formulartyp laut Kommentar im Word-Dokument (hat Vorrang)

    static readonly HashSet<string> Stop = new() { "der", "die", "das", "und", "für", "mit", "von", "den", "dem", "ein", "eine", "ist", "zur", "zum", "vom", "auf", "sie", "ihr", "ihre", "ihnen", "wir", "wird", "werden", "bei", "des", "sich", "nicht", "auch", "oder", "als", "wie", "nach", "über", "aus", "ihrer", "einer", "eines", "sind", "dass", "sowie", "diese", "dieser", "diesem", "den" };
    static readonly Regex Tok = new(@"[a-zäöüß]{3,}", RegexOptions.Compiled);
    static readonly Regex Mark = new(@"\[[a-z#0-9A-F]*\]", RegexOptions.Compiled), Ph = new("<[^<>\n]+>", RegexOptions.Compiled), Fld = new("«[^»]*»", RegexOptions.Compiled);

    public static List<string> Tokens(string text)
    {
        var t = Tok.Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => !Stop.Contains(w)).ToList();
        var res = new List<string>(t);
        for (int i = 0; i + 1 < t.Count; i++) res.Add(t[i] + "_" + t[i + 1]);        // Bigramme für Phrasen
        return res;
    }

    public static string NiceName(string file)
    {
        var s = Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(file), "#U([0-9a-fA-F]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        if (s.Contains('Ã') || s.Contains('Â')) try { s = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(s)); } catch { }
        return s;
    }

    public static FormFeatures FromStf(StfDocument d, string? path = null)
    {
        var f = new FormFeatures { Path = path, Name = path == null ? "" : NiceName(path), Version = d.Version, Pages = d.Pages().Count };
        if (d.Header.Count > 17 && d.Header[17].StartsWith("Alias=", StringComparison.OrdinalIgnoreCase)) f.AliasOf = d.Header[17][6..].Trim();
        var sb = new StringBuilder();
        foreach (var e in d.Items)
        {
            switch (e.Type)
            {
                case "Text" or "String":
                    f.TextBoxes++; foreach (var p in e.Placeholders) f.Placeholders.Add(p.Split('|')[0]);
                    sb.AppendLine(Ph.Replace(Mark.Replace(e.Text, ""), " ")); break;
                case "Line":
                    var b = e.Box!.Value; if (Compare.Kind(e) == "Rect" && b.W <= 8 && b.H <= 8) f.Checkboxes++; else f.Lines++; break;
                case "VarTabelle": f.Tables++; break;
                case "Picture" or "PPicture": f.Pictures++; break;
            }
        }
        f.RawText = sb.ToString(); f.Words = Tokens(f.RawText);
        if (path != null)
        {
            var n = System.IO.Path.GetFileName(path).ToLowerInvariant();
            f.Art = n.StartsWith("ra_") ? "RaumFormular" : n.StartsWith("uk_") ? "UnterkunftFormular" : n.StartsWith("fl_") ? "FluechtlingsFormular" : n.StartsWith("ex_") ? "ExportVorlage" : n.StartsWith("dv_") ? "DVJob" : n.StartsWith("sc_") ? "KassenExport" : "Formular";
        }
        return f;
    }

    public static FormFeatures FromIr(IrDocument ir, string name)
    {
        var f = new FormFeatures { Name = name, Pages = Math.Max(1, ir.Pages.Count), HintKind = ir.Notes.KindHint };
        var sb = new StringBuilder();
        foreach (var p in ir.Pages)
        {
            foreach (var l in p.Lines) { sb.AppendLine(Fld.Replace(l.Text, " ")); f.TextBoxes++; }
            f.Lines += p.Rules.Count; f.Checkboxes += p.Widgets.Count(w => w.Kind == "checkbox"); f.Pictures += p.Images.Count;
        }
        f.RawText = sb.ToString(); f.Words = Tokens(f.RawText); return f;
    }
}

public sealed record KindScore(string Kind, double Score);
public sealed record Classification(string Kind, double Confidence, string SuggestedArt, bool IsBescheid, List<KindScore> Ranking, List<string> Signals);

/// <summary>Regelbasierte Klassifikation (transparent: jede Entscheidung nennt ihre Signale). Gewichte sind Schätzungen, keine trainierten Werte.</summary>
public static class FormClassifier
{
    sealed record Rule(string Kind, string Art, (string Pattern, double W)[] Terms);
    static readonly List<Rule> Rules = new()
    {
        new("Anordnung","Formular",new[]{("anordnung",4.0),("kasse wird angewiesen",5.0),("produktsachkonto",4.0),("erhebungssoll",4.0),("einzahlungspflichtige",3.0),("haushaltsjahr",2.0),("absetzung",3.0),("eingangsvermerk",2.0)}),
        new("Kostenanforderung","Formular",new[]{("kostenanforderung",6.0),("art der schuld",5.0),("endbetrag",2.0),("anforderung",1.5)}),
        new("Antrag","Formular",new[]{("antrag auf",4.0),("antragsteller",2.0),("beantrage",3.0),("antrag",1.5)}),
        new("Gebührenbescheid","Formular",new[]{("gebührenbescheid",5.0),("sollstellung",3.0),("kassenzeichen",3.0),("benutzungsgebühr",3.0),("gebühr",1.5),("monatlich",0.7),("zahlungspflichtig",1.5),("fällig",1.0),("rechtsbehelfsbelehrung",1.0)}),
        new("Unterbringungsbescheid","Formular",new[]{("einweisung",3.0),("wechsel",1.5),("umsetzung",3.0),("zuweisung",2.5),("obdachlos",2.0),("ordnungsverfügung",2.5),("unterbringung",1.5),("verfügung",1.0),("wohnraum",1.0)}),
        new("Krankenschein","FluechtlingsFormular",new[]{("krankenschein",5.0),("behandlungsschein",5.0),("krankenkasse",2.0),("krankenversicherung",2.0),("arzt",1.5),("behandlung",1.0),("asylbewerberleistungsgesetz",1.0)}),
        new("Protokoll/Checkliste","Formular",new[]{("protokoll",3.0),("übergabe",2.0),("checkliste",3.0),("zustand",1.5),("mängel",1.5),("schlüssel",1.0),("zählerstand",1.5),("abnahme",1.5)}),
        new("Vertrag","Formular",new[]{("vertrag",2.5),("mietvertrag",3.0),("nutzungsvertrag",3.0),("kündigung",1.5),("vermieter",1.0),("kaution",1.5)}),
        new("Bescheinigung","Formular",new[]{("bescheinigung",4.0),("bestätigung",2.5),("bescheinigt",3.0),("hiermit",0.8)}),
        new("Brief/Anschreiben","Formular",new[]{("sehr geehrte",3.0),("mit freundlichen grüßen",3.0),("freundlichen grüßen",2.0),("betreff",1.0),("ihr zeichen",1.0),("auskunft erteilt",1.5)}),
        new("Liste/Übersicht","Formular",new[]{("liste",2.0),("übersicht",2.0),("belegung",2.0),("auswertung",1.5),("statistik",1.5)}),
        new("Schild/Aushang","RaumFormular",new[]{("hausordnung",3.0),("türschild",4.0),("aushang",3.0),("information",0.5)}),
        new("Kassenbeleg","KassenExport",new[]{("sollstellungsbeleg",8.0),("stornobeleg",8.0),("beleg",2.0),("buchung",1.0)}),
        new("Abrechnung","Formular",new[]{("abrechnung",3.0),("nebenkosten",3.0),("betriebskosten",2.0),("verbrauch",1.5),("guthaben",1.5),("nachzahlung",1.5)}),
        new("Stammdaten/Profil/Ausweis","FluechtlingsFormular",new[]{("stammdaten",4.0),("profilbogen",4.0),("ausweis",3.0),("deckblatt",3.0),("qualifikation",2.0),("sozialhilfeakte",3.0),("geburtsdatum",1.0),("staatsangehörigkeit",1.0)}),
        new("Einladung/Mitteilung","Formular",new[]{("einladung",3.0),("sprachkurs",2.0),("termin",1.2),("hiermit laden",3.0),("mitteilung",1.5)}),
        // Fallakte / Beratungsakte der Sozialarbeit (PZ-Tabelle, Beratungsverlauf, Kontaktpersonen ...). Muss "Bescheinigung" (Wort steckt in "Meldebescheinigung") und "Stammdaten" schlagen.
        new("Fallakte","Formular",new[]{("fallakte",6.0),("beratungsverlauf",4.0),("sozialarbeitende",3.0),("sozialarbeit",2.0),("kontaktpersonen",2.5),("soziales netzwerk",2.5),("erstgespräch",2.5),("antrag bei behörden",2.5),("einkommensart",2.0),("integration",1.0),("dokumentation",1.0),("todo",1.0)}),
    };

    /// <summary>Formularname nennt den Typ ausdrücklich: starkes Signal (ein "Gebührenbescheid" enthält oft Anordnungs-Seiten und umgekehrt).</summary>
    static readonly List<(string Kind, string Key, double W)> NameKeys = new()
    {
        ("Gebührenbescheid","gebührenbescheid",12), ("Anordnung","anordnung",12), ("Kostenanforderung","kostenanforderung",12), ("Antrag","antrag",8),
        ("Unterbringungsbescheid","unterbringung",6), ("Unterbringungsbescheid","einweisungsbescheid",12), ("Unterbringungsbescheid","einweisungsverfügung",12),
        ("Fallakte","fallakte",12),
    };

    /// <summary>Zusätzliche Formulartypen aus einer JSON-Datei (ohne Neukompilieren), z. B. <c>kinds.json</c>:
    /// <c>[{"kind":"Fallakte","art":"Formular","terms":[{"t":"fallakte","w":6}],"nameKeys":[{"key":"fallakte","w":12}]}]</c>.
    /// Gleicher Typname wie ein vorhandener: die Begriffe werden ergänzt. Gibt die Zahl der geladenen Typen zurück.</summary>
    /// <summary>Typ nicht nur aus festen Regeln, sondern aus den ähnlichsten Formularen des Bestands (bei tausenden Formularen verschiedenster Art):
    /// ist die Regel unsicher (&lt; 0,60 oder "Sonstiges") oder ein fast identisches Vorbild (&gt;= 0,80) hat anderen Typ, entscheidet die gewichtete Mehrheit der Nachbarn.
    /// Der Typname ist dann der des Bestands - beliebige Typen, nicht nur die der Regelliste.</summary>
    public static Classification Refine(Classification c, IList<SimilarHit> hits, string? hintKind = null, double minSim = 0.5)
    {
        if (!string.IsNullOrEmpty(hintKind) && hintKind == c.Kind)
            return c with { Confidence = Math.Max(c.Confidence, 0.9), Signals = c.Signals.Contains("Kommentar im Dokument") ? c.Signals : c.Signals.Append("Kommentar im Dokument").ToList() };      // Typ per Word-Kommentar festgelegt: hat Vorrang vor der Nachbarn-Mehrheit
        var votes = hits.Where(h => h.Score >= minSim && !string.IsNullOrEmpty(h.Kind) && h.Kind != "Sonstiges")
                        .GroupBy(h => h.Kind).Select(g => (Kind: g.Key, W: g.Sum(h => h.Score * h.Score))).OrderByDescending(x => x.W).ToList();
        if (votes.Count == 0) return c;
        var top = votes[0]; double best = hits.Max(h => h.Score);
        if (top.Kind == c.Kind) return c with { Confidence = Math.Round(Math.Max(c.Confidence, Math.Min(0.95, best)), 2) };
        bool weak = c.Confidence < 0.60 || c.Kind == "Sonstiges";
        if (!weak && best < 0.80) return c;
        var art = hits.FirstOrDefault(h => h.Kind == top.Kind)?.Art;
        return c with { Kind = top.Kind, Confidence = Math.Round(Math.Min(0.9, best), 2), SuggestedArt = string.IsNullOrEmpty(art) ? c.SuggestedArt : art!,
            IsBescheid = top.Kind == "Gebührenbescheid", Signals = c.Signals.Append($"Typ aus ähnlichen Formularen: {top.Kind}").ToList() };
    }

    public static int LoadExtraKinds(string path)
    {
        if (!File.Exists(path)) return 0;
        int n = 0;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        foreach (var k in doc.RootElement.EnumerateArray())
        {
            var kind = k.GetProperty("kind").GetString(); if (string.IsNullOrWhiteSpace(kind)) continue;
            var art = k.TryGetProperty("art", out var a) ? a.GetString() ?? "Formular" : "Formular";
            var terms = new List<(string, double)>();
            if (k.TryGetProperty("terms", out var ts)) foreach (var t in ts.EnumerateArray()) terms.Add((t.GetProperty("t").GetString()!.ToLowerInvariant(), t.GetProperty("w").GetDouble()));
            int ix = Rules.FindIndex(r => r.Kind == kind);
            if (ix >= 0) { Rules[ix] = Rules[ix] with { Terms = Rules[ix].Terms.Concat(terms).ToArray() }; }
            else Rules.Add(new Rule(kind!, art, terms.ToArray()));
            if (k.TryGetProperty("nameKeys", out var nk)) foreach (var t in nk.EnumerateArray()) NameKeys.Add((kind!, t.GetProperty("key").GetString()!.ToLowerInvariant(), t.GetProperty("w").GetDouble()));
            n++;
        }
        return n;
    }

    public static Classification Classify(FormFeatures f)
    {
        string text = (f.RawText + " " + f.Name).ToLowerInvariant();
        var scores = new Dictionary<string, double>(); var signals = new List<string>(); var art = new Dictionary<string, string>();
        foreach (var r in Rules)
        {
            double s = 0; art[r.Kind] = r.Art; var nameLow = f.Name.ToLowerInvariant();
            foreach (var (pat, w) in r.Terms)
            {
                int n = Regex.Matches(text, Regex.Escape(pat)).Count;
                if (n > 0) { s += w * (1 + Math.Log(n)); signals.Add($"{r.Kind}: \"{pat}\" ×{n}"); }
                if (nameLow.Contains(pat)) { s += w * 1.5; signals.Add($"{r.Kind}: Name enthält \"{pat}\""); }   // Dateiname ist ein starkes Signal
            }
            scores[r.Kind] = s;
        }
        { var nl = f.Name.ToLowerInvariant(); foreach (var (k, key, w) in NameKeys) if (nl.Contains(key) && scores.ContainsKey(k)) { scores[k] += w; signals.Add($"{k}: Formularname nennt \"{key}\""); } }
        // Hinweis aus Kommentar im Word-Dokument (z. B. "wird als Gebührenbescheid gedruckt") hat Vorrang
        if (f.HintKind != null && scores.ContainsKey(f.HintKind)) { scores[f.HintKind] += 30; signals.Add($"{f.HintKind}: Kommentar im Dokument"); }
        // Struktur-/Platzhaltersignale
        bool kassenForm = scores["Anordnung"] >= 6 || scores["Kostenanforderung"] >= 6;      // Kassen-Anordnungen/Forderungen sind KEINE Gebührenbescheide
        if (!kassenForm && f.Placeholders.Any(p => p.StartsWith("B_") || p.StartsWith("POS_") || p.StartsWith("UVOR_") || p.StartsWith("BK_"))) { scores["Gebührenbescheid"] += 3; signals.Add("Gebührenbescheid: Platzhalter B_/POS_/UVOR_/BK_"); }
        if (!kassenForm && f.Tables > 0) { scores["Gebührenbescheid"] += 2; signals.Add($"Gebührenbescheid: {f.Tables} variable Tabelle(n)"); }
        if (f.Checkboxes >= 5) { scores["Protokoll/Checkliste"] += 3; signals.Add($"Protokoll/Checkliste: {f.Checkboxes} Checkboxen"); }
        if (f.Placeholders.Contains("F_AnredeN") || f.Placeholders.Contains("F_AnredeS") || f.Placeholders.Contains("SB_Name")) { scores["Brief/Anschreiben"] += 2; signals.Add("Brief/Anschreiben: Adress-/Sachbearbeiter-Platzhalter"); }
        if (f.TextBoxes > 0 && f.TextBoxes <= 8 && f.Pages == 1) { scores["Schild/Aushang"] += 1.5; signals.Add("Schild/Aushang: sehr wenig Text auf einer Seite"); }
        if (f.Lines > 25 && f.Checkboxes < 5) { scores["Liste/Übersicht"] += 1.5; signals.Add($"Liste/Übersicht: {f.Lines} Linien"); }

        var rank = scores.OrderByDescending(kv => kv.Value).Select(kv => new KindScore(kv.Key, Math.Round(kv.Value, 2))).ToList();
        var top = rank[0]; var second = rank.Count > 1 ? rank[1].Score : 0;
        if (top.Score < 2.0) return new Classification("Sonstiges", 0.2, f.Art ?? "Formular", false, rank.Take(3).ToList(), signals.Take(8).ToList());
        double conf = Math.Round(top.Score / (top.Score + second + 1.5), 2);
        return new Classification(top.Kind, conf, f.Art is { } a && a != "Formular" ? a : art[top.Kind], top.Kind == "Gebührenbescheid", rank.Take(3).ToList(),
            signals.Where(s => s.StartsWith(top.Kind)).Take(8).ToList());
    }
}

public sealed record SimilarHit(string Name, string? Path, double Score, double TextSim, double StructSim, string Kind, string? Art, int Pages, int PlaceholderCount, string? AliasOf);

/// <summary>Ähnlichkeitssuche über ein Korpus von .stf-Dateien (TF-IDF auf Wörtern+Bigrammen, plus Strukturähnlichkeit).</summary>
public sealed class FormIndex
{
    public List<FormFeatures> Forms { get; } = new();
    readonly Dictionary<string, double> idf = new();
    readonly List<Dictionary<string, double>> vecs = new();
    public int Duplicates { get; private set; }
    public int Unreadable { get; private set; }

    public static FormIndex Build(IEnumerable<string> dirs) =>
        BuildFromFiles(dirs.Where(Directory.Exists).SelectMany(dir => Directory.EnumerateFiles(dir, "*.stf", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal)));

    /// <summary>Index aus einer festen Dateiliste (z. B. den STF-Einträgen des Katalogs). bzip2-komprimierte Dateien werden entpackt.</summary>
    public static FormIndex BuildFromFiles(IEnumerable<string> files)
    {
        var ix = new FormIndex(); var seen = new HashSet<string>();
        foreach (var f in files)
            {
                try
                {
                    var raw = Bzip2.MaybeDecompress(File.ReadAllBytes(f)); var h = Convert.ToHexString(SHA1.HashData(raw));
                    if (!seen.Add(h)) { ix.Duplicates++; continue; }
                    var feat = FormFeatures.FromStf(StfDocument.Parse(raw), f);
                    if (feat.TextBoxes == 0 || feat.IsAliasStub) continue;                    // leere Hüllen und reine Alias-Verweise überspringen (Alias-Option an vollständigen Formularen ist erlaubt)
                    ix.Forms.Add(feat);
                }
                catch { ix.Unreadable++; }
            }
        ix.Finish(); return ix;
    }

    void Finish()
    {
        var df = new Dictionary<string, int>();
        foreach (var f in Forms) foreach (var t in f.Words.Distinct()) df[t] = df.GetValueOrDefault(t) + 1;
        int n = Math.Max(Forms.Count, 1);
        foreach (var (t, c) in df) idf[t] = Math.Log((n + 1.0) / (c + 1.0)) + 1;
        foreach (var f in Forms) vecs.Add(Vec(f.Words));
    }

    Dictionary<string, double> Vec(List<string> words)
    {
        var tf = words.GroupBy(w => w).ToDictionary(g => g.Key, g => 1 + Math.Log(g.Count()));
        var v = tf.ToDictionary(kv => kv.Key, kv => kv.Value * idf.GetValueOrDefault(kv.Key, Math.Log(Forms.Count + 1.0) + 1));
        double norm = Math.Sqrt(v.Values.Sum(x => x * x)); if (norm > 0) foreach (var k in v.Keys.ToList()) v[k] /= norm; return v;
    }

    static double Struct(FormFeatures a, FormFeatures b)
    {
        double page = a.Pages == b.Pages ? 1 : 1.0 / (1 + Math.Abs(a.Pages - b.Pages));
        double chk = (a.Checkboxes >= 5) == (b.Checkboxes >= 5) ? 1 : 0;
        double ln = 1.0 / (1 + Math.Abs(Math.Log((a.Lines + 1.0) / (b.Lines + 1.0))));
        double tx = 1.0 / (1 + Math.Abs(Math.Log((a.TextBoxes + 1.0) / (b.TextBoxes + 1.0))));
        return (page + chk + ln + tx) / 4;
    }

    public List<SimilarHit> Query(FormFeatures q, int k = 5, string? excludePath = null, bool includeAliases = false)
    {
        var qv = Vec(q.Words); var hits = new List<SimilarHit>();
        for (int i = 0; i < Forms.Count; i++)
        {
            var f = Forms[i];
            if (excludePath != null && f.Path != null && string.Equals(System.IO.Path.GetFullPath(f.Path), System.IO.Path.GetFullPath(excludePath), StringComparison.OrdinalIgnoreCase)) continue;
            if (f.IsAliasStub && !includeAliases) continue;
            double cos = 0; foreach (var (t, w) in qv) if (vecs[i].TryGetValue(t, out var w2)) cos += w * w2;
            double st = Struct(q, f);
            hits.Add(new SimilarHit(f.Name, f.Path, Math.Round(0.85 * cos + 0.15 * st, 3), Math.Round(cos, 3), Math.Round(st, 3),
                FormClassifier.Classify(f).Kind, f.Art, f.Pages, f.Placeholders.Count, f.AliasOf));
        }
        return hits.OrderByDescending(h => h.Score).Take(k).ToList();
    }
}
}