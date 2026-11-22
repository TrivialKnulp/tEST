using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using StfKit.Core;

// StfKit.Examples - Testlauf über einen Ordner mit Kunden-Formularen (alles C#, kein Python).
//   StfKit.Examples <ordner> <arbeitsordner> [--corpus <ordner>[;<ordner>]] [--catalog <catalog.json>] [--high 0.75] [--min 0.45] [--ir]
// --catalog: Vorlagen aus dem Katalog (StfKit.Cli catalog ...): Firebird-Export + Echtbetrieb + ältere Ordner, mit Quelle/Version/Status je Treffer.
// Je Dokument (PDF, optional gleichnamiges docx daneben; rekursiv): PDF lesen -> Word-Kommentare/Markierungen lesen -> Typ erkennen
// -> ähnlichstes vorhandenes Formular suchen -> .stf (von Grund auf) + Vorschau-PDF erzeugen. Ergebnis: Tabelle + bericht.csv im Arbeitsordner.
// --mandant <name> --ziel Dateien|Importdatei|ZentraleDB: im Lauf (status.json/verlauf.jsonl) festgehalten. --resume: fertige Formulare überspringen, fehlgeschlagene wiederholen.
// --llm-felder <ollama-url> [--llm-model qwen3.8:27b]: Feldliste per Sprachmodell als Vorschlag (<name>.llmfelder.json). --erwartet <csv Dokument;Erwartet>: Qualität/Schwellen auswerten (auswertung.txt).
// Word/ODT/RTF ohne PDF werden mit LibreOffice in PDF gewandelt.
// --ir: statt PDFs fertige *.ir.json lesen (für Tests ohne PDF-Modul).
Console.OutputEncoding = new UTF8Encoding(false);
string? Opt(string n) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
if (args.Length < 2 || args[0].StartsWith("--") || args[1].StartsWith("--")) { Console.Error.WriteLine("Aufruf: StfKit.Examples <ordner> <arbeitsordner> [--corpus <ordner>] [--ir]"); return 2; }
var src = args[0]; var work = args[1]; Directory.CreateDirectory(work);
bool fromIr = args.Contains("--ir");
CatalogFile? cat = Opt("catalog") is { } cp ? CatalogFile.Load(cp) : null;
FormIndex? ix = cat != null ? FormIndex.BuildFromFiles(cat.IndexFiles()) : Opt("corpus") is { } c ? FormIndex.Build(c.Split(';')) : null;
double HighT = double.TryParse(Opt("high"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hv) ? hv : 0.75;
double MinT = double.TryParse(Opt("min"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mv) ? mv : 0.45;   // Schwellen sind NICHT kalibriert (Lastenheft 7.1): an 50-100 echten Dokumenten anpassen
File.WriteAllText(Path.Combine(work, "ir.schema.json"), IrElements.Schema, new UTF8Encoding(false));
if (ix != null) Console.WriteLine($"Korpus: {ix.Forms.Count} Formulare ({ix.Duplicates} Duplikate übersprungen)");

var run = Workflow.Start(work, src, Opt("mandant"), Opt("ziel")); bool resume = args.Contains("--resume");
var evalCases = new List<Eval.Case>(); var expected = Opt("erwartet") is { } ec ? Eval.ReadExpected(ec).ToDictionary(x => Operations.SafeFileName(x.Doc), x => x.Expected, StringComparer.OrdinalIgnoreCase) : null;
var items = new List<(string Name, string? Pdf, string? Docx, string? Ir, string? Doc)>();
if (fromIr) items.AddRange(Directory.EnumerateFiles(src, "*.ir.json", SearchOption.AllDirectories).OrderBy(x => x).Select(f => (Path.GetFileName(f)[..^8], (string?)null, (string?)null, (string?)f, (string?)null)));
else items.AddRange(Operations.Scan(src, true).Select(g => (g.BaseName, g.Pdf, g.Docx, (string?)null, g.Doc)));

var csv = new StringBuilder("Name;Typ;Sicherheit;Kommentare;Markierungen;Texte;Linien;Bilder;BilderOffen;Aehnlichstes;Aehnlichkeit;Vorlage;Pruefen;AutoPlatzhalter;ZusammengesetztePlatzhalter;Sicherheitsstufe;Status;Kandidaten;Vergleich;IRFehler;Fehler\r\n");
Console.WriteLine($"{"Datei",-46} {"Typ",-22} {"Sich.",5} {"Komm",4} {"Mark",4} {"Text",5} {"Lin.",5} {"Bild",4} {"Offen",5}  Ähnlichstes Formular");
int ok = 0, fail = 0, noPdf = 0;
foreach (var it in items)
{
    var wi = Workflow.Upsert(run, it.Name);
    if (resume && !Workflow.NeedsWork(run, it.Name)) { Console.WriteLine($"{Trunc(it.Name, 46),-46} (übersprungen: {wi.Status})"); continue; }
    Workflow.Step(work, run, wi, "analyse", Workflow.Processing);
    try
    {
        IrDocument ir;
        var pdfFile = it.Pdf; var docxFile = it.Docx;
        if (pdfFile == null && it.Ir == null && (it.Docx ?? it.Doc) is { } wf && Office.Available) pdfFile = Office.Convert(wf, "pdf", Path.Combine(work, "_office") );   // Word/ODT/RTF ohne PDF
        if (docxFile == null && it.Doc != null && Office.Available) docxFile = Office.Convert(it.Doc, "docx", Path.Combine(work, "_office"));
        if (it.Ir != null) ir = IrDocument.Load(it.Ir);
        else if (pdfFile == null) { noPdf++; Console.WriteLine($"{Trunc(it.Name, 46),-46} (ohne PDF übersprungen)"); continue; }
        else
        {
#if PDF
            ir = StfKit.Pdf.PdfExtractor.Extract(pdfFile);
            if (Opt("ollama") is { } olUrl && ScanOcr.ScanPages(ir).Count > 0)           // Scan: Text per Bild-Modell lesen (--ollama http://kiserver:11434 [--ocr-model qwen3.8:27b])
                try { ScanOcr.ApplyAsync(ir, olUrl, Opt("ocr-model") ?? "qwen3.8:27b").GetAwaiter().GetResult(); } catch (Exception ex) { Console.WriteLine("   OCR fehlgeschlagen: " + ex.Message); }
            if (docxFile != null) { ir.Fields = DocxFields.Read(docxFile).Where(f => f.Kind == "MERGEFIELD").Select(f => f.Name).Distinct().ToList(); ir.Notes = DocxNotes.Read(docxFile); }
#else
            throw new InvalidOperationException("ohne PDF-Modul gebaut (-p:WithPdf=false): bitte --ir verwenden");
#endif
        }
        var feat = FormFeatures.FromIr(ir, it.Name); var cls = FormClassifier.Classify(feat);
        var safe = Operations.SafeFileName(it.Name);
        var stf = Operations.UniquePath(work, safe, ".stf");
        var r = Operations.Generate(ir, stf, "DB2026", new Dictionary<string, string>());
        var hits5 = ix?.Query(feat, 5) ?? new List<SimilarHit>(); var best = hits5.FirstOrDefault();
        cls = FormClassifier.Refine(cls, hits5, feat.HintKind);
        double MinSim = MinT;                                              // wie StfKit:MinSimilarity
        var tplRemark = ix == null ? "" : FormReview.TemplateRemark(best, cls, MinSim);
        var typeNote = ix == null ? "" : FormReview.TypeNote(best, cls);
        var imgPos = ir.Pages.SelectMany((p, i) => p.Images.Where(m => string.IsNullOrEmpty(m.Png)).Select(m => $"S{i + 1} {m.X:0}/{m.Y:0} mm {m.W:0}x{m.H:0}")).ToList();
        var review = FormReview.ReviewReasons(cls, r.ImagesTodo, r.UnassignedHighlights);
        // Quelle/Version/Status der Treffer aus dem Katalog; Sicherheitsstufe (Lastenheft 7.3)
        string Cand(SimilarHit h) { var ce = cat?.FindByPath(h.Path); return $"{h.Name} [{(ce == null ? "?" : ce.Source)}{(ce?.Version != null ? " v" + ce.Version : "")}{(ce == null ? "" : ", " + ce.Status)}] {h.Score:0.00}"; }
        var bestEntry = cat?.FindByPath(best?.Path);
        string level = best == null ? "niedrig" : best.Score >= HighT && (bestEntry == null || bestEntry.Status is "Aktuell" or "Ungeprüft") ? "hoch" : best.Score >= MinT ? "mittel" : "niedrig";
        if (bestEntry != null && bestEntry.Status is "Historisch" or "Ersetzt" && cat!.CurrentOf(bestEntry) is { } curE) review.Add($"Beste Vorlage '{best!.Name}' ist {bestEntry.Status} ({bestEntry.StatusNote}); aktuelle Fassung: {curE.Name} [{curE.Source}]");
        if (bestEntry != null && bestEntry.Status == "Unklar") review.Add($"Aktualität der Vorlage unklar: {bestEntry.StatusNote}");
        if (level == "mittel") review.Add("mittlere Sicherheit: bitte Kandidaten prüfen (Spalte Kandidaten)");
        else if (level == "niedrig" && ix != null) review.Add("niedrige Sicherheit: Suche erweitern bzw. Neuerstellung ohne Vorlage prüfen");
        // IR-Elemente + Schemaprüfung, Vergleich mit der Vorlage
        var docId = "K" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(it.Name)))[..10];
        var tmap = new Dictionary<string, string>(ir.Notes.CommentPlaceholders);
        foreach (var a in r.AutoPlaceholders ?? new List<string>()) { var k = a.IndexOf(" → ", StringComparison.Ordinal); if (k > 0) { var ph = a[(k + 3)..]; var p2 = ph.IndexOf(" (", StringComparison.Ordinal); if (p2 > 0) ph = ph[..p2]; tmap[a[..k]] = ph.Trim('<', '>'); } }
        var els = IrElements.Build(ir, docId, tmap); var irErrs = IrElements.Validate(els, ir);
        IrElements.Save(els, Path.Combine(work, safe + ".elemente.json"));
        var irReal = irErrs.Where(x => !x.Contains("Markierung ohne Platzhalter")).ToList();
        if (irReal.Count > 0) review.Add($"IR-Prüfung: {irReal.Count} Fehler (z. B. {irReal[0]})");
        string vgl = "";
        if (best != null && best.Path != null && best.Score >= MinT && File.Exists(best.Path))
            try { var rows = Vergleich.Run(ir, StfDocument.Parse(Bzip2.MaybeDecompress(File.ReadAllBytes(best.Path)))); Vergleich.Save(rows, Path.Combine(work, safe + ".vergleich.csv")); vgl = Vergleich.Summary(rows); } catch (Exception vex) { vgl = "Vergleich fehlgeschlagen: " + vex.Message; }
        review.InsertRange(0, FormReview.ScanReasons(ir));
        if (imgPos.Count > 0) review.Add("Bildposition: " + string.Join(", ", imgPos));
        var stfDoc = StfDocument.Load(stf); var lay = LayoutCheck.Run(ir, stfDoc);
        if (!lay.Ok) review.Add("Layout: " + lay.Summary);
        var testPdf = Path.Combine(work, Path.GetFileNameWithoutExtension(stf) + ".testdruck.pdf");
        File.WriteAllBytes(testPdf, LayoutCheck.Testdruck(stfDoc, safe));
        string llmNote = "";
        if (Opt("llm-felder") is { } llmUrl && !ir.TextOnly)
            try
            {
                var (lf, lprob) = LlmFields.ExtractAsync(ir, llmUrl, Opt("llm-model") ?? "qwen3.8:27b", System.Linq.Enumerable.Empty<string>()).GetAwaiter().GetResult();
                System.IO.File.WriteAllText(Path.Combine(work, safe + ".llmfelder.json"), System.Text.Json.JsonSerializer.Serialize(new { felder = lf, probleme = lprob }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
                var fresh = lf.Where(f => !r.AutoPlaceholders!.Any(a => a.Contains(f.placeholder)) && !System.IO.File.ReadAllText(stf).Contains(f.placeholder)).ToList();
                llmNote = $"LLM: {lf.Count} Felder, davon {fresh.Count} neu";
                if (fresh.Count > 0) review.Add($"LLM schlägt {fresh.Count} weitere Felder vor ({string.Join(", ", fresh.Take(5).Select(f => f.placeholder))}) - siehe {safe}.llmfelder.json");
                if (lf.Count == 0 && lprob.Count > 0) review.Add("LLM-Felder ungültig: " + lprob[^1]);
            }
            catch (Exception lex) { review.Add("LLM-Felder nicht verfügbar: " + lex.Message); }
        wi.Stf = stf; wi.Vorschau = Path.Combine(work, Path.GetFileNameWithoutExtension(stf) + ".vorschau.pdf"); wi.Testdruck = testPdf; wi.Typ = cls.Kind; wi.Sicherheitsstufe = level; wi.Layout = lay.Summary;
        wi.Vorlage = best == null ? "" : Cand(best); wi.Vergleich = File.Exists(Path.Combine(work, safe + ".vergleich.csv")) ? Path.Combine(work, safe + ".vergleich.csv") : ""; wi.Pruefen = string.Join(" | ", review);
        if (expected != null) evalCases.Add(new Eval.Case(it.Name, expected.GetValueOrDefault(safe, ""), hits5.Select(h => (h.Name, h.Score)).ToList()));
        File.WriteAllBytes(Path.Combine(work, Path.GetFileNameWithoutExtension(stf) + ".vorschau.pdf"), StfPreview.Render(StfDocument.Load(stf), new StfPreview.Options(ir, null, safe)));
        Console.WriteLine($"{Trunc(it.Name, 46),-46} {cls.Kind,-22} {cls.Confidence,5:0.00} {ir.Notes.Comments.Count,4} {ir.Notes.Highlights.Count,4} {r.Texts,5} {r.Lines,5} {r.PicturesMade,4} {r.ImagesTodo,5}  {(best == null ? "" : $"{Trunc(best.Name, 40)} ({best.Score:0.00})")}");
        if (ix != null) Console.WriteLine($"{"",-46}   Sicherheit: {level}{(bestEntry != null ? $"; Quelle {bestEntry.Source}, Status {bestEntry.Status}" : "")}{(vgl.Length > 0 ? "; Vergleich: " + vgl : "")}");
        if (ix != null) Console.WriteLine($"{"",-46}   Vorlage: {(tplRemark.Length == 0 ? "ok" : tplRemark)}{(tplRemark.Length == 0 && typeNote.Length > 0 ? " - " + typeNote : "")}");
        foreach (var x in review) Console.WriteLine($"{"",-46}   PRÜFEN: {x}");
        if (r.AutoPlaceholders is { Count: > 0 }) Console.WriteLine($"{"",-46}   Auto-Platzhalter: {string.Join("; ", r.AutoPlaceholders)}");
        if (r.JoinedPlaceholders is { Count: > 0 }) Console.WriteLine($"{"",-46}   zusammengesetzt: {string.Join("; ", r.JoinedPlaceholders)}");
        csv.Append($"{Qs(it.Name)};{cls.Kind};{cls.Confidence:0.00};{ir.Notes.Comments.Count};{ir.Notes.Highlights.Count};{r.Texts};{r.Lines};{r.PicturesMade};{r.ImagesTodo};{Qs(best?.Name ?? "")};{best?.Score:0.00};{Qs(ix == null ? "" : tplRemark.Length == 0 ? ("ok" + (typeNote.Length > 0 ? " - " + typeNote : "")) : tplRemark)};{Qs(string.Join(" | ", review))};{Qs(string.Join("; ", r.AutoPlaceholders ?? new List<string>()))};{Qs(string.Join("; ", r.JoinedPlaceholders ?? new List<string>()))};{level};Review Required;{Qs(string.Join(" | ", hits5.Select(Cand)))};{Qs(vgl)};{irErrs.Count};\r\n");
        Workflow.Step(work, run, wi, "erzeugt", Workflow.ReviewRequired, review.Count == 0 ? "zur Prüfung bereit" : review.Count + " Hinweise");
        ok++;
    }
    catch (Exception ex) { fail++; Workflow.Step(work, run, wi, wi.LetzterSchritt == "" ? "analyse" : wi.LetzterSchritt, Workflow.Failed, ex.Message); Console.WriteLine($"{Trunc(it.Name, 46),-46} FEHLER: {ex.Message}"); csv.Append($"{Qs(it.Name)};;;;;;;;;;;;;;;;;;;;{Qs(ex.Message)}\r\n"); }
}
File.WriteAllText(Path.Combine(work, "bericht.csv"), csv.ToString(), new UTF8Encoding(true));
Workflow.WriteReviewHtml(work); File.WriteAllText(Path.Combine(work, "mail.txt"), Workflow.MailText(work), new UTF8Encoding(false));
if (expected != null) { var rep2 = Eval.Report(evalCases, HighT, MinT); File.WriteAllText(Path.Combine(work, "auswertung.txt"), rep2, new UTF8Encoding(true)); Console.WriteLine("\n" + rep2); }
Workflow.Log(work, run.ProcessId, "ende", "", "", $"{ok} erzeugt, {fail} Fehler");
Console.WriteLine($"Prozess {run.ProcessId}; Prüfseite: {Path.Combine(work, "review.html")}; Mail-Text: mail.txt");
Console.WriteLine($"\n{ok} erzeugt, {fail} Fehler, {noPdf} ohne PDF. Ergebnisse und bericht.csv: {work}");
return fail == 0 ? 0 : 1;

static string Qs(string v) => "\"" + (v ?? "").Replace("\"", "'") + "\"";
static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
