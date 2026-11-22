using System.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using StfKit.Core;

// StfKit CLI - wird von n8n (Execute Command) aufgerufen. Ausgabe: JSON auf stdout, Fehler auf stderr + Exit-Code 1.
//   StfKit scan <dir> [--recursive]                         Dokumentgruppen (pdf/docx gleicher Basisname) auflisten
//   StfKit corpus-scan <dir>[;<dir>] [--limit N] [--max-chars 30000] [--soffice <Pfad zu soffice.exe>] [--no-office] [--list]   Korpus (alle Unterordner: .stf/.pdf/.docx/.doc) prüfen: Zahlen je Typ/Ordner, ohne Qdrant
//   StfKit qdrant-delete [--id U[,U]] [--category C] [--folder F] [--rel-path P] [--yes]   Punkte aus Qdrant löschen (ohne --yes nur zählen)
//   StfKit qdrant-load <dir>[;<dir>] [--only-missing] [--dry-run [--list]] [--limit N] [--url http://host:6333] [--collection Formularerstellung] [--ollama http://kiserver:11434] [--model bge-m3:latest] [--api-key K]   Korpus in Qdrant laden (idempotent)
//   StfKit docx-fields <file.docx>                          MERGEFIELD-Namen auslesen
//   StfKit docx-notes <file.docx> [--into <ir.json>]       Kommentare, Textmarker, Druckregeln, Typ-Hinweis aus Word lesen
//   StfKit generate ... [--text-map <map.json>]            markierten Text -> Platzhalter ersetzen
//   StfKit analyze <file.pdf> --work <dir> [--docx <f>]     PDF -> IR-JSON (+ Felder aus docx)
//   StfKit generate --ir <ir.json> --out <x.stf> [--map <map.json> | --map-b64 <base64(json)>] [--version DB2026] [--eol crlf|crcrlf|lf]
//   StfKit placeholders <dir> [--top 300]                   Platzhalter-Katalog aus .stf-Korpus
//   StfKit inspect <x.stf>                                  Elementtabelle (Debug)
//   StfKit preview <x.stf> --out <v.pdf> [--ir <ir.json>]     PDF-Vorschau zur Prüfung (mit --ir: Original in Rot darübergelegt)
//   StfKit learn --generated <erzeugt.stf> --corrected <korrigiert.stf> --kb <ordner> [--ir <ir.json>] [--name <n>]   aus einer Korrektur lernen (mit --ir auch Feld-Slots)
//   StfKit generate ... [--template <x.stf> --mode inherit|bescheid]   auf Basis eines ähnlichen Formulars erzeugen
//   StfKit generate ... [--kb <ordner>]                    mit gelernter Kalibrierung/Ersetzungen erzeugen
//   StfKit classify <ir.json|x.stf>                          Formulartyp bestimmen (mit Begründung)
//   StfKit similar <ir.json|x.stf> --corpus <ordner>[;<ordner>] [--top 5]   ähnlichste vorhandene Formulare
//   StfKit selftest-bescheid <vorhandener-bescheid.stf>      Selbsttest des Bescheid-Modus (Struktur bleibt, Texte werden aktualisiert)
//   StfKit stf2ir <x.stf> --out <ir.json>                    IR aus einer .stf rekonstruieren (für Tests)
//   StfKit compare <referenz.stf> <erzeugt.stf>             Qualitaet gegen Handarbeit messen (Texte, Linien/Rahmen)
//   StfKit review list|approve|reject|correct <arbeitsordner> [<formular>] [--by <name>] [--note <t>] [--file <korrigiert.stf>] [--kb <ordner>]   Prüfung, Freigabe, Ablehnung, Korrektur (lernt)
//   StfKit review html|mail <arbeitsordner>                  Prüfseite bzw. Mail-Text erzeugen
//   StfKit export <arbeitsordner> [--exec "<FormulareImport-Aufruf mit {datei} {modus}>"] [--dry-run]   freigegebene Formulare übergeben (Dateien/Importdatei/ZentraleDB laut Lauf)
//   StfKit llm-fields <ir.json> --ollama <url> [--model qwen3.8:27b]   Feldliste (JSON) per Sprachmodell vorschlagen
//   StfKit roundtrip <dir>                                  Selbsttest: parse -> dump byte-identisch?

var J = new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
void Out(object o) => Console.WriteLine(JsonSerializer.Serialize(o, J));
string? Opt(string name) { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Flag(string name) => args.Contains("--" + name);
string Arg(int i) => args.Length > i && !args[i].StartsWith("--") ? args[i] : throw new ArgumentException("Argument fehlt");

try
{
    if (args.Length == 0) { Console.Error.WriteLine("Befehl fehlt (catalog|scan|docx-fields|analyze|generate|placeholders|inspect|roundtrip)"); return 2; }
    switch (args[0])
    {
        case "catalog":
        {
            // StfKit catalog <catalog.config.json> <ausgabeordner>
            //   Baut/aktualisiert den gemeinsamen Formular-Katalog aus allen Quellen der Konfiguration (Firebird-Export, Echtbetrieb, ältere Ordner):
            //   Hash, Mandant/Version aus dem Namen, Duplikate, Aktualitätsstatus. Ein vorhandener catalog.json im Ausgabeordner wird inkrementell fortgeschrieben.
            //   Liest nur .stf/.docx (konfigurierbar), alles andere wird nur gezählt. Originale bleiben unverändert.
            var cfg = CatalogConfig.Load(Arg(1)); var outDir = Arg(2); var cj = System.IO.Path.Combine(outDir, "catalog.json");
            var old = System.IO.File.Exists(cj) ? CatalogFile.Load(cj) : null;
            var cat = Catalog.Build(cfg, old, out var rep); Catalog.WriteReports(cat, rep, outDir);
            Console.WriteLine($"{rep.Found} Formulare (neu {rep.New}, geändert {rep.Changed}, unverändert {rep.Unchanged}, entfernt {rep.Removed}); Duplikate {rep.Duplicates}; Konflikte {rep.Conflicts}; nicht lesbar {rep.Unreadable}");
            foreach (var kv in rep.ByStatus) Console.WriteLine($"  {kv.Key}: {kv.Value}");
            foreach (var n in rep.Notes) Console.WriteLine("  Hinweis: " + n);
            Console.WriteLine($"Bericht: {System.IO.Path.Combine(outDir, "katalog-bericht.md")}");
            return 0;
        }
        case "scan":
        {
            var dir = Arg(1);
            var groups = Operations.Scan(dir, Flag("recursive")).Select(g => new { baseName = g.BaseName, dir = g.Dir, pdf = g.Pdf, docx = g.Docx, doc = g.Doc }).ToList();
            Out(new { count = groups.Count, items = groups });
            return 0;
        }
        case "sync-plan":
        {
            // StfKit sync-plan <neu-ordner[;ordner2]> <db-export-ordner> <ausgabe-ordner>
            //   neu-ordner       : Ordner mit neuen/von Hand bearbeiteten/von der KI erzeugten .stf (z. B. "A - im Echtbetrieb", KI-Ergebnisse), rekursiv
            //   db-export-ordner : Ausgabe von FormulareExport aus der zentralen Aeneas-DB (index.csv + .stf) = Stand der Datenbank
            //   ausgabe-ordner   : Import-Paket. manifest.csv = nur NEUE Formulare (für FormulareImport, Modus Neu);
            //                      manifest_geaendert.csv = Formulare mit gleichem Namen, aber anderem Inhalt: nur zur Prüfung, wird NICHT automatisch importiert
            var src = Arg(1).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries); var db = Arg(2); var outDir = Arg(3);
            Directory.CreateDirectory(outDir);
            string Key(string f) => FormFeatures.NiceName(f).Trim().ToLowerInvariant();
            var dbFiles = Directory.EnumerateFiles(db, "*.stf", SearchOption.AllDirectories).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
            string Sha(string f) { using var h = System.Security.Cryptography.SHA1.Create(); return Convert.ToHexString(h.ComputeHash(File.ReadAllBytes(f))); }
            var cand = src.SelectMany(d => Directory.EnumerateFiles(d, "*.stf", SearchOption.AllDirectories))
                          .Where(f => !f.StartsWith(Path.GetFullPath(outDir), StringComparison.OrdinalIgnoreCase))
                          .GroupBy(Key).Select(g => g.OrderByDescending(File.GetLastWriteTimeUtc).First());      // je Name nur die neueste Fassung
            var neu = new List<string>(); var changed = new List<string>(); int same = 0;
            const string head = "Datei;Name;Art;Kategorie;Modus;Ziel;Vorlage;Aehnlichkeit;Hinweise\r\n";
            var mNeu = new StringBuilder(head); var mChg = new StringBuilder(head);
            foreach (var f in cand)
            {
                var nm = FormFeatures.NiceName(f); var note = $"Quelle: {f}; geändert {File.GetLastWriteTime(f):dd.MM.yyyy HH:mm}";
                string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
                if (!dbFiles.TryGetValue(Key(f), out var d)) { File.Copy(f, Path.Combine(outDir, Operations.SafeFileName(nm) + ".stf"), true); mNeu.Append($"{Q(Operations.SafeFileName(nm) + ".stf")};{Q(nm)};\"Formular\";\"\";\"Neu\";\"\";\"\";;{Q(note)}\r\n"); neu.Add(nm); }
                else if (Sha(f) != Sha(d)) { mChg.Append($"{Q(Path.GetFileName(f))};{Q(nm)};\"Formular\";\"\";\"Ersetzen\";{Q(nm)};\"\";;{Q(note + "; Fassung in der DB weicht ab - Mensch entscheidet, welche neuer ist")}\r\n"); changed.Add(nm); }
                else same++;
            }
            File.WriteAllText(Path.Combine(outDir, "manifest.csv"), mNeu.ToString(), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(outDir, "manifest_geaendert.csv"), mChg.ToString(), new UTF8Encoding(true));
            Out(new { neu = neu.Count, geaendert = changed.Count, gleich = same, neuNamen = neu.Take(50), geaendertNamen = changed.Take(50), ausgabe = outDir });
            return 0;
        }
        case "qdrant-delete":
        {
            // StfKit qdrant-delete [--id <uuid>[,<uuid>]] [--category Dokument] [--folder Dormagen] [--rel-path "Ordner/Name"] [--yes]   (ohne --yes nur zählen)
            var qo = new QdrantOptions();
            if (Opt("url") is { } u0) qo.Url = u0; if (Opt("collection") is { } c0) qo.Collection = c0; if (Opt("api-key") is { } k0) qo.ApiKey = k0;
            var ids = Opt("id")?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var (matched, deleted) = await QdrantLoader.DeleteAsync(qo, ids, Opt("category"), Opt("folder"), Opt("rel-path"), !Flag("yes"));
            Out(new { ok = true, matched, deleted, hint = deleted || matched == 0 ? null : "Nur gezählt. Mit --yes wirklich löschen." });
            return 0;
        }
        case "corpus-scan":
        case "qdrant-load":
        {
            var dirs = Arg(1).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (Opt("soffice") is { } sofp) Office.SofficePath = sofp;
            if (Flag("no-office")) Office.Enabled = false;
            int limit = int.TryParse(Opt("limit"), out var lim) ? lim : 0;
            Func<string, string?>? pdfText = null;
#if PDF
            pdfText = p => string.Join("\n", StfKit.Pdf.PdfExtractor.Extract(p).Pages.Select(pg => string.Join("\n", pg.Lines.Select(l => l.Text))));
            CorpusLoader.PdfLayout = p => CorpusLoader.LayoutJson(StfKit.Pdf.PdfExtractor.Extract(p));
#endif
            var entries = CorpusLoader.Build(dirs, pdfText, out var stats, limit, int.TryParse(Opt("max-chars"), out var mc) ? mc : 30000);
            if (args[0] == "corpus-scan")
            {
                Out(new { ok = true, entries = entries.Count, stats, items = Flag("list") ? entries.Select(e => new { e.Folder, e.Name, e.EmbedSource, stf = e.Stf != null, pdf = e.Pdf != null, docx = e.Docx != null, chars = e.EmbedData.Length, e.Warnings }) : null });
                return 0;
            }
            var qo = new QdrantOptions();
            if (Opt("url") is { } u) qo.Url = u; if (Opt("collection") is { } col) qo.Collection = col; if (Opt("ollama") is { } ol) qo.OllamaUrl = ol;
            if (Opt("model") is { } mo) qo.EmbedModel = mo; if (Opt("api-key") is { } ak) qo.ApiKey = ak;
            Console.Error.WriteLine($"{entries.Count} Formulare -> {qo.Url} / {qo.Collection}");
            if (Flag("only-missing") || Flag("dry-run"))
            {
                // nur laden, was in Qdrant noch fehlt (ID oder Inhalt); --dry-run zeigt das nur an und lädt nichts
                var ex = await QdrantLoader.ReadExistingAsync(qo, m => Console.Error.WriteLine(m));
                var (missing, present) = QdrantLoader.SplitMissing(entries, ex);
                Console.Error.WriteLine($"in Qdrant vorhanden: {ex.Total} Punkte; aus den {entries.Count} Formularen fehlen {missing.Count}, schon vorhanden {present.Count}");
                if (Flag("dry-run"))
                {
                    Out(new { ok = true, dryRun = true, inQdrant = ex.Total, checkedEntries = entries.Count, missing = missing.Count, present = present.Count,
                        missingList = Flag("list") ? missing.Select(e => e.RelPath) : null, presentList = Flag("list") ? present.Select(p => new { p.E.RelPath, p.Why }) : null, stats });
                    return 0;
                }
                entries = missing;
                if (entries.Count == 0) { Out(new { ok = true, loaded = new { uploaded = 0 }, hint = "nichts zu laden: alles schon in Qdrant", stats }); return 0; }
            }
            var lr = await QdrantLoader.LoadAsync(entries, qo, m => Console.Error.WriteLine(m));
            Out(new { ok = lr.Failed == 0, loaded = lr, stats });
            return lr.Failed == 0 ? 0 : 1;
        }
        case "docx-notes":
        {   // Kommentare, Markierungen, Druckregeln aus docx; mit --into <ir.json> wird das Ergebnis in die IR geschrieben
            var notes = DocxNotes.Read(Arg(1));
            if (Opt("into") is { } intoPath) { var ir0 = IrDocument.Load(intoPath); ir0.Notes = notes; ir0.Save(intoPath); }
            Out(notes);
            return 0;
        }
        case "docx-fields":
            Out(DocxFields.Read(Arg(1)).Select(f => new { f.Kind, f.Name, f.Part }));
            return 0;
        case "analyze":
        {
#if PDF
            var pdf = Arg(1); var work = Opt("work") ?? Path.GetDirectoryName(Path.GetFullPath(pdf))!;
            Directory.CreateDirectory(work);
            var docx = Opt("docx") ?? Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(pdf))!, Path.GetFileNameWithoutExtension(pdf) + ".docx").FirstOrDefault();
            var ir = StfKit.Pdf.PdfExtractor.Extract(pdf);
            if (docx != null) { ir.Fields = DocxFields.Read(docx).Where(f => f.Kind == "MERGEFIELD").Select(f => f.Name).Distinct().ToList(); ir.Notes = DocxNotes.Read(docx); PlaceholderRepair.ApplyKnown(ir, PlaceholderRepair.NamesFromDocx(docx).Concat(ir.Notes.CommentPlaceholders.Values)); }
            var irPath = Path.Combine(work, Path.GetFileNameWithoutExtension(pdf) + ".ir.json");
            ir.Save(irPath);
            var ctx = ir.Pages.SelectMany(p => p.Lines).Select(l => l.Text).Take(80).ToList();
            Out(new { ir = irPath, source = pdf, docx, pages = ir.Pages.Count, textLines = ir.Pages.Sum(p => p.Lines.Count), fields = ir.Fields,
                      linesTodo = ir.Pages.Sum(p => p.Rules.Count + p.Rects.Count), imagesTodo = ir.Pages.Sum(p => p.Images.Count), promptContext = ctx });
            return 0;
#else
            Console.Error.WriteLine("analyze: ohne PDF-Unterstützung gebaut (-p:WithPdf=false)"); return 3;
#endif
        }
        case "generate":
        {
            var ir = IrDocument.Load(Opt("ir") ?? throw new ArgumentException("--ir fehlt"));
            var outp = Opt("out") ?? throw new ArgumentException("--out fehlt");
            var b64 = Opt("map-b64");
            var map = b64 != null ? Operations.ParseMap(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64))) : LoadMap(Opt("map"));
            var eol = Opt("eol") switch { "lf" => "\n", "crcrlf" => "\r\r\n", _ => "\r\n" };
            var kb = Opt("kb") is { } kd ? KnowledgeBase.Load(kd) : null;
            var tpl = Opt("template") is { } tpath ? StfDocument.Load(tpath) : null;
            var tmap = Opt("text-map") is { } tmf ? Operations.ParseMap(File.ReadAllText(tmf)) : null;
            var r = Operations.Generate(ir, outp, Opt("version") ?? "DB2026", map, eol, kb, null, tpl, Opt("mode") ?? (tpl != null ? "inherit" : "scratch"), tpl != null ? FormFeatures.NiceName(Opt("template")!) : "", tmap, Opt("catalog") is { } cdir && Directory.Exists(cdir) ? PlaceholderCatalog.Build(cdir).Select(x => x.Name).ToList() : null);
            Out(new { stf = r.Stf, version = r.Version, pages = r.Pages, texts = r.Texts, placeholders = r.Placeholders, unmappedFields = r.UnmappedFields,
                      linesTodo = r.LinesTodo, linesMade = r.Lines, slotsFilled = r.SlotsFilled, imagesTodo = r.ImagesTodo, picturesMade = r.PicturesMade, textMapped = r.TextMapped, adapt = r.Adapt });
            return 0;
        }
        case "placeholders":
        {
            int top = int.Parse(Opt("top") ?? "300");
            Out(PlaceholderCatalog.Build(Arg(1)).Take(top));
            return 0;
        }
        case "inspect":
        {
            var d = StfDocument.Load(Arg(1)); int p = 1;
            foreach (var e in d.Items)
            {
                if (e.Type == "Seite") { Console.WriteLine($"--- Seite {++p}"); continue; }
                var b = e.Box!.Value; string t = e.Type is "Text" or "String" or "Barcode" ? e.Text.Replace("\n", " / ") : "";
                Console.WriteLine($"{e.Type,-10} x={b.X,6:F1} y={b.Y,6:F1} w={b.W,6:F1} h={b.H,6:F1} {(e.Font is { } f ? $"{f.Name} {f.Size}" : ""),-12} {(t.Length > 70 ? t[..70] : t)}");
            }
            return 0;
        }
        case "preview":
        {
            var doc = StfDocument.Load(Arg(1)); var outp = Opt("out") ?? Path.ChangeExtension(Arg(1), ".preview.pdf");
            var ir = Opt("ir") is { } ip ? IrDocument.Load(ip) : null;
            File.WriteAllBytes(outp, StfPreview.Render(doc, new StfPreview.Options(ir, null, Path.GetFileNameWithoutExtension(Arg(1)))));
            Out(new { pdf = outp, pages = doc.Pages().Count, overlay = ir != null });
            return 0;
        }
        case "review":
        {
            var w = Arg(2); var sub = Arg(1); var who = Opt("by") ?? Environment.UserName;
            switch (sub)
            {
                case "list": Out(Workflow.Load(w)); return 0;
                case "approve": Out(new { approved = Workflow.Approve(w, Arg(3), who, Opt("note")) }); Workflow.WriteReviewHtml(w); return 0;
                case "reject": Workflow.Reject(w, Arg(3), who, Opt("note") ?? throw new ArgumentException("--note (Grund) fehlt")); Workflow.WriteReviewHtml(w); Out(new { rejected = Arg(3) }); return 0;
                case "correct": { var d = Workflow.Correct(w, Arg(3), Opt("file") ?? throw new ArgumentException("--file fehlt"), who, Opt("kb"), Opt("ir") is { } cip ? IrDocument.Load(cip) : null); Workflow.WriteReviewHtml(w); Out(new { learnedTo = d }); return 0; }
                case "html": Out(new { html = Workflow.WriteReviewHtml(w) }); return 0;
                case "mail": Console.WriteLine(Workflow.MailText(w)); return 0;
                default: throw new ArgumentException("review list|approve|reject|correct|html|mail");
            }
        }
        case "export":
        {
            var msgs = Workflow.Export(Arg(1), Opt("exec"), Flag("dry-run")); Workflow.WriteReviewHtml(Arg(1)); Out(new { messages = msgs }); return 0;
        }
        case "llm-fields":
        {
            var ir = IrDocument.Load(Arg(1)); var (f, pr) = LlmFields.ExtractAsync(ir, Opt("ollama") ?? throw new ArgumentException("--ollama fehlt"), Opt("model") ?? "qwen3.8:27b", Array.Empty<string>()).GetAwaiter().GetResult();
            Out(new { fields = f, problems = pr }); return f.Count > 0 ? 0 : 1;
        }
        case "phrase-add":      // StfKit phrase-add <kb-ordner> "<Text in Worten>" <Platzhalter>   (Markierungstext -> Aeneas-Platzhalter merken)
        case "phrase-import":   // StfKit phrase-import <kb-ordner> <datei.csv>                      (Zeilen: Text;Platzhalter)
        case "phrase-list":     // StfKit phrase-list <kb-ordner>
        {
            var kbp = KnowledgeBase.Load(Arg(1));
            if (args[0] == "phrase-add") kbp.PhraseLearn(Arg(2), Arg(3), "manuell");
            else if (args[0] == "phrase-import")
                foreach (var ln in File.ReadAllLines(Arg(2), System.Text.Encoding.UTF8).Select(l => l.Split(';')).Where(c => c.Length >= 2 && c[0].Trim().Length > 0 && !c[0].StartsWith("#")))
                    kbp.PhraseLearn(ln[0].Trim('"', ' '), ln[1].Trim('"', ' ', '<', '>'), "Import");
            kbp.Save();
            Out(new { phrases = kbp.Phrases.Count, items = kbp.Phrases.Select(x => new { text = x.Key, platzhalter = x.Value.Ph, anzahl = x.Value.Count, quelle = x.Value.Source }) });
            return 0;
        }
        case "layout-snap":    // StfKit layout-snap <x.stf> <ir.json> --out <y.stf>   Vorlage-Geometrie am PDF (IR) ausrichten
        {
            var gd = StfDocument.Load(Arg(1)); var irs = IrDocument.Load(Arg(2));
            var before = LayoutCheck.Measure(irs, StfToIr.Convert(gd)); var sn = LayoutSnap.Apply(gd, irs);
            var outS = Opt("out") ?? Path.ChangeExtension(Arg(1), ".ausgerichtet.stf"); gd.Save(outS);
            var after = LayoutCheck.Measure(irs, StfToIr.Convert(StfDocument.Load(outS)));
            Out(new { ausgabe = outS, anker = sn.Anchors, verschoben = sn.MovedElements, entfernt = sn.RemovedShadowed, vorher = before.Describe(), nachher = after.Describe(), nachherZeilenImRaster = after.Within15 + " von " + after.Matched });
            return 0;
        }
        case "layout-check":   // StfKit layout-check <x.stf> <ir.json>   misst, wie genau die .stf das Layout des PDFs (IR) trifft
        {
            var lr0 = LayoutCheck.Measure(IrDocument.Load(Arg(2)), StfToIr.Convert(StfDocument.Load(Arg(1))));
            Out(new { lr0.OriginalLines, lr0.Matched, lr0.Within15, lr0.Within15Share, lr0.MedianDx, lr0.MedianDy, lr0.MaxAbsDy, lr0.SlopeDyPerMm, poor = lr0.Poor, text = lr0.Describe() });
            return 0;
        }
        case "learn":
        {
            var gen = StfDocument.Load(Opt("generated") ?? throw new ArgumentException("--generated fehlt"));
            var cor = StfDocument.Load(Opt("corrected") ?? throw new ArgumentException("--corrected fehlt"));
            var kb = KnowledgeBase.Load(Opt("kb") ?? throw new ArgumentException("--kb fehlt"));
            var lir = Opt("ir") is { } lip ? IrDocument.Load(lip) : null;
            Out(kb.Learn(gen, cor, Opt("name") ?? Path.GetFileNameWithoutExtension(Opt("corrected")!), lir));
            return 0;
        }
        case "classify":
        {
            var feat = Feat(Arg(1)); var c = FormClassifier.Classify(feat);
            Out(new { form = feat.Name, kind = c.Kind, confidence = c.Confidence, suggestedArt = c.SuggestedArt, isBescheid = c.IsBescheid, ranking = c.Ranking, signals = c.Signals });
            return 0;
        }
        case "similar":
        {
            var feat = Feat(Arg(1)); var ix = FormIndex.Build((Opt("corpus") ?? throw new ArgumentException("--corpus fehlt")).Split(';'));
            var hits = ix.Query(feat, int.Parse(Opt("top") ?? "5"), Arg(1).EndsWith(".stf", StringComparison.OrdinalIgnoreCase) ? Arg(1) : null);
            Out(new { form = feat.Name, corpusForms = ix.Forms.Count, duplicates = ix.Duplicates, hits });
            return 0;
        }
        case "selftest-bescheid":
        {
            var tpl = StfDocument.Load(Arg(1)); string name = FormFeatures.NiceName(Arg(1));
            var ir = StfToIr.Convert(tpl);
            // zwei statische (platzhalterfreie) Textboxen im "neuen PDF" ändern
            var staticBoxes = tpl.Items.Where(e => e.Type == "Text" && !e.Placeholders.Any() && Compare.Norm(e.Text).Length >= 25).Take(2).ToList();
            var changed = new List<string>();
            foreach (var b in staticBoxes)
            {
                var line = ir.Pages.SelectMany(pg => pg.Lines).FirstOrDefault(l => l.Text == System.Text.RegularExpressions.Regex.Replace(b.Lines[0], @"\[[a-z#0-9A-F]*\]", ""));
                if (line == null) continue; line.Text += " (NEU GEFASST)"; changed.Add(b.Lines[0]);
            }
            // dritte Änderung: Wortlaut eines Fließtext-Absatzes MIT Platzhaltern (darf nicht überschrieben, nur gemeldet werden)
            var mixed = tpl.Items.FirstOrDefault(e => e.Type == "Text" && e.Placeholders.Any() && e.Lines.Count >= 3 && e.Lines.Any(l => l.Contains("überweisen")));
            string? mixedBefore = mixed?.Text;
            if (mixed != null)
            {
                var ml = ir.Pages.SelectMany(pg => pg.Lines).FirstOrDefault(l => l.Text.Contains("überweisen"));
                if (ml != null) ml.Text = ml.Text.Replace("überweisen", "bezahlen");
            }
            var (gen, _) = IrToStf.Convert(ir, tpl.Version);
            var (res, rep) = FormAdapter.EditTemplate(gen, tpl, name);
            string Counts(StfDocument d) => string.Join(", ", d.Items.GroupBy(e => e.Type).OrderBy(g => g.Key).Select(g => g.Key + ":" + g.Count()));
            var phT = tpl.Items.SelectMany(e => e.Placeholders).OrderBy(x => x).ToList(); var phR = res.Items.SelectMany(e => e.Placeholders).OrderBy(x => x).ToList();
            int Cnt(StfDocument d, string t) => d.Items.Count(e => e.Type == t);
            var checks = new Dictionary<string, bool>
            {
                ["Tabellen (VarTabelle) unverändert"] = Cnt(res, "VarTabelle") == Cnt(tpl, "VarTabelle"),
                ["Gruppen unverändert"] = Cnt(res, "Group") == Cnt(tpl, "Group"),
                ["Linien/Rahmen unverändert"] = Cnt(res, "Line") == Cnt(tpl, "Line"),
                ["alle Platzhalter erhalten"] = phT.SequenceEqual(phR),
                ["geänderte Texte übernommen"] = rep.Replaced >= changed.Count && changed.Count > 0,
                ["nichts ergänzt (Selbsttest ohne neue Texte)"] = rep.Added == 0,
                ["Absatz mit Platzhaltern bleibt unverändert, Abweichung wird gemeldet"] = mixed == null || (res.Items.Any(e => e.Type == "Text" && e.Text == mixedBefore) && rep.Notes.Any(n => n.Contains("PRÜFEN") && n.Contains("überweisen"))),
                ["Kopfzeile/Version unverändert"] = res.Header.SequenceEqual(tpl.Header),
            };
            Out(new { template = name, vorlage = Counts(tpl), ergebnis = Counts(res), geaendertImNeuenPdf = changed.Count, report = new { rep.Replaced, rep.Unchanged, rep.Untouched, rep.Added }, notes = rep.Notes.Take(8), checks });
            return checks.Values.All(x => x) ? 0 : 1;
        }
        case "stf2ir":
        {
            var ir = StfToIr.Convert(StfDocument.Load(Arg(1))); var o = Opt("out") ?? Path.ChangeExtension(Arg(1), ".ir.json"); ir.Save(o);
            Out(new { ir = o, pages = ir.Pages.Count, lines = ir.Pages.Sum(p => p.Lines.Count) }); return 0;
        }
        case "compare":
        {
            var res = Compare.Run(StfDocument.Load(Arg(1)), StfDocument.Load(Arg(2)));
            Console.Write(Compare.Format(res));
            return 0;
        }
        case "roundtrip":
        {
            int ok = 0, bad = 0, elems = 0;
            foreach (var f in Directory.EnumerateFiles(Arg(1), "*.stf", SearchOption.AllDirectories))
            {
                var raw = File.ReadAllBytes(f); var d = StfDocument.Parse(raw);
                try { d.ValidateTree(); foreach (var e in d.Items) { elems++; if (e.Type != "Seite") { _ = e.Box; _ = e.Font; _ = e.Cond; } } }
                catch (Exception ex) { Console.Error.WriteLine($"SEM  {f}: {ex.Message}"); bad++; continue; }
                if (d.DumpBytes().AsSpan().SequenceEqual(raw)) ok++; else { bad++; Console.Error.WriteLine($"DIFF {f}"); }
            }
            Out(new { ok, bad, elements = elems });
            return bad == 0 ? 0 : 1;
        }
        default:
            Console.Error.WriteLine("Unbekannter Befehl: " + args[0]); return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message); return 1;
}

// Map: {"Anschrift1":"F_AnredeN"}  oder  {"mapping":[{"field":"..","placeholder":"..","confidence":0.9}]}  oder direkt ein Array davon
static Dictionary<string, string> LoadMap(string? path) => path == null ? new() : Operations.ParseMap(File.ReadAllText(path));

static FormFeatures Feat(string path) =>
    path.EndsWith(".stf", StringComparison.OrdinalIgnoreCase) ? FormFeatures.FromStf(StfDocument.Load(path), path)
                                                              : FormFeatures.FromIr(IrDocument.Load(path), FormFeatures.NiceName(path).Replace(".ir", ""));