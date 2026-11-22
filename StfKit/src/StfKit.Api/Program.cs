using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StfKit.Core;

// .NET 5: kein WebApplication/minimal API -> Generic Host + Endpoint-Routing.
var cfg = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables().AddCommandLine(args).Build();
var opt = cfg.GetSection("StfKit").Get<ApiOptions>() ?? new ApiOptions();
bool useKey = !string.IsNullOrWhiteSpace(opt.ApiKey);
if (useKey && opt.ApiKey.Length < 16)
{ Console.Error.WriteLine("StfKit:ApiKey ist zu kurz (mind. 16 Zeichen; oder leer lassen = ohne Schlüssel, nur lokal). Dienst startet nicht."); return 1; }
if (opt.AllowedRoots.Length == 0)
{ Console.Error.WriteLine("StfKit:AllowedRoots ist leer. Dienst startet nicht."); return 1; }
ILogger? log = null;
var jsonOpt = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
Directory.CreateDirectory(opt.WorkDir);
{
    var kf = !string.IsNullOrWhiteSpace(opt.KindsFile) ? opt.KindsFile : (string.IsNullOrWhiteSpace(opt.KnowledgeDir) ? "" : Path.Combine(opt.KnowledgeDir, "kinds.json"));
    if (kf.Length > 0) try { var n = FormClassifier.LoadExtraKinds(kf); if (n > 0) Console.WriteLine($"Formulartypen aus {kf}: {n}"); } catch (Exception ex) { Console.Error.WriteLine("kinds.json: " + ex.Message); }
}
foreach (var f in Directory.EnumerateFiles(opt.WorkDir, "*.ir.json").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30))) { try { File.Delete(f); } catch { } }
var guard = new PathGuard(opt.AllowedRoots, opt.CorpusDirs);      // Korpus-Ordner (alle Unterordner) dürfen gelesen werden, geschrieben wird nur unter AllowedRoots
var irIdRx = new Regex(@"^[A-Za-z0-9_.\-]{1,200}\.ir\.json$", RegexOptions.Compiled);
var catalogCache = new Dictionary<string, (DateTime At, List<PlaceholderCatalog.Entry> Data)>();
var indexCache = new Dictionary<string, (DateTime At, FormIndex Ix)>();
var genLock = new object();                                                   // Kalibrierung ist global (statisch): Erzeugung/Lernen nicht parallel
KnowledgeBase? kb = string.IsNullOrWhiteSpace(opt.KnowledgeDir) ? null : KnowledgeBase.Load(opt.KnowledgeDir);
FormIndex? GetIndex(string[]? dirs)
{
    var list = (dirs is { Length: > 0 } ? dirs : opt.CorpusDirs).Select(d => guard.Dir(d)).ToArray();
    if (list.Length == 0) return null;
    var key = string.Join("|", list);
    lock (indexCache)
    {
        if (!indexCache.TryGetValue(key, out var c) || c.At < DateTime.UtcNow.AddMinutes(-10)) indexCache[key] = c = (DateTime.UtcNow, FormIndex.Build(list));
        return c.Ix;
    }
}
IrDocument LoadIr(string? irId)
{
    if (string.IsNullOrWhiteSpace(irId) || !irIdRx.IsMatch(irId)) throw new GuardException("Ungültige irId");
    var path = Path.Combine(opt.WorkDir, irId); if (!File.Exists(path)) throw new GuardException("irId unbekannt (abgelaufen?)");
    return IrDocument.Load(path);
}

// Erwartbare Fehler -> HTTP 200 mit ok=false (n8n-Auswertung bleibt einfach); Auth-Fehler -> 401.
async Task Write(HttpContext ctx, object o, int status = 200)
{
    ctx.Response.StatusCode = status; ctx.Response.ContentType = "application/json; charset=utf-8";
    await ctx.Response.WriteAsync(JsonSerializer.Serialize(o, jsonOpt));
}
async Task<object> Guarded(Func<Task<object>> f)
{
    try { return await f(); }
    catch (GuardException ex) { return new { ok = false, error = ex.Message }; }
    catch (Exception ex) { log?.LogError(ex, "Fehler"); return new { ok = false, error = ex.Message }; }
}
void Post<T>(IEndpointRouteBuilder e, string path, Func<T, Task<object>> handler) where T : class =>
    e.MapPost(path, async ctx =>
    {
        T? req = null;
        try { req = await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, jsonOpt); }
        catch (JsonException ex) { await Write(ctx, new { ok = false, error = "Ungültiges JSON: " + ex.Message }); return; }
        if (req == null) { await Write(ctx, new { ok = false, error = "Anfrage-Body fehlt" }); return; }
        await Write(ctx, await Guarded(() => handler(req)));
    });

void MapRoutes(IEndpointRouteBuilder e)
{
e.MapGet("/health", ctx => Write(ctx, new
{
    ok = true, version = "0.1", libreOffice = Office.Available, libreOfficePath = Office.Find(),
#if PDF
    pdf = true,
#else
    pdf = false,
#endif
}));

Post<ScanReq>(e, "/scan", r => Guarded(() =>
{
    var dir = guard.Dir(r.Dir);
    // Verzeichnis-Modus: die Dateien bleiben im Quellordner; fehlt zu einem Word-Dokument die PDF, wird sie (bei .doc/.rtf/.odt auch die .docx)
    // per LibreOffice in einem Arbeitsordner erzeugt - das Original wird nie verändert.
    var inbox = string.IsNullOrWhiteSpace(opt.InboxDir) ? Path.Combine(opt.AllowedRoots[0], "_inbox") : opt.InboxDir;
    string? convDir = null;
    var items = Operations.Scan(dir, r.Recursive ?? true).Select(g =>
    {
        string? pdf = g.Pdf, docx = g.Docx; var wordLike = g.Docx ?? g.Doc; bool pdfFromWord = false;
        if ((r.Convert ?? true) && wordLike != null && Office.Available && (pdf == null || docx == null))
        {
            convDir ??= guard.DirForWrite(Path.Combine(inbox, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_scan_" + Guid.NewGuid().ToString("N")[..6])); Directory.CreateDirectory(convDir);
            var tmp = Path.Combine(Path.GetTempPath(), "stfkit_scan_" + Guid.NewGuid().ToString("N"));
            try
            {
                var safe = Operations.SafeFileName(g.BaseName);
                if (pdf == null) { var c = Office.Convert(wordLike, "pdf", tmp); if (c != null) { pdf = Path.Combine(convDir, safe + ".pdf"); File.Copy(c, pdf, true); pdfFromWord = true; } }
                if (docx == null) { var c = Office.Convert(wordLike, "docx", tmp); if (c != null) { docx = Path.Combine(convDir, safe + ".docx"); File.Copy(c, docx, true); } }
            }
            finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
        }
        var warning = pdf == null && wordLike != null ? "Keine PDF und keine Umwandlung möglich (LibreOffice auf dem Server fehlt?): Layout/Analyse nicht möglich" : null;
        return new { baseName = g.BaseName, dir = g.Dir, pdf, docx, doc = g.Doc, pdfFromWord, warning };
    }).ToList();
    return Task.FromResult<object>(new { ok = true, count = items.Count, dir, items });
}));

// Korpus (alle Unterordner: .stf/.pdf/.docx) prüfen bzw. in Qdrant laden. Einmalig/bei Änderungen von Hand anstoßen, NICHT bei jedem Formular-Upload.
Func<string[]?, int?, (List<CorpusEntry>, CorpusStats)> BuildCorpus = (dirs, limit) =>
{
    var list = (dirs is { Length: > 0 } ? dirs : opt.CorpusDirs).Select(d => guard.Dir(d)).ToArray();
    Func<string, string?>? pdfText = null;
#if PDF
    pdfText = p => string.Join("\n", StfKit.Pdf.PdfExtractor.Extract(p).Pages.Select(pg => string.Join("\n", pg.Lines.Select(l => l.Text))));
    CorpusLoader.PdfLayout = p => CorpusLoader.LayoutJson(StfKit.Pdf.PdfExtractor.Extract(p));
#endif
    var en = CorpusLoader.Build(list, pdfText, out var st, limit ?? 0);
    return (en, st);
};
Post<CorpusReq>(e, "/corpus/scan", r => Guarded(() =>
{
    var (en, st) = BuildCorpus(r.CorpusDirs, r.Limit);
    return Task.FromResult<object>(new { ok = true, entries = en.Count, stats = st, items = (r.List ?? false) ? en.Select(x => new { x.Folder, x.Name, x.EmbedSource, stf = x.Stf != null, pdf = x.Pdf != null, docx = x.Docx != null, chars = x.EmbedData.Length, x.Warnings }) : null });
}));
Post<CorpusReq>(e, "/corpus/load", r => Guarded(async () =>
{
    var (en, st) = BuildCorpus(r.CorpusDirs, r.Limit);
    var lr = await QdrantLoader.LoadAsync(en, opt.Qdrant, m => log?.LogInformation("Qdrant-Load {Msg}", m));
    return new { ok = lr.Failed == 0, entries = en.Count, loaded = lr, stats = st };
}));

string IrText(IrDocument ir) => string.Join("\n", ir.Pages.Select(pg => string.Join("\n", pg.Lines.Select(l => l.Text))));

// Hochgeladene Dateien (Base64) ablegen. Liefert dieselbe Struktur wie /scan; fehlt die PDF, wird sie aus Word/RTF/ODT per LibreOffice erzeugt.
Post<UploadReq>(e, "/upload", r => Guarded(() =>
{
    if (r.Files == null || r.Files.Length == 0) throw new GuardException("Keine Dateien übergeben");
    var inbox = string.IsNullOrWhiteSpace(opt.InboxDir) ? Path.Combine(opt.AllowedRoots[0], "_inbox") : opt.InboxDir;
    var batch = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
    var dir = guard.DirForWrite(Path.Combine(inbox, batch)); Directory.CreateDirectory(dir);
    var okExt = new[] { ".pdf", ".docx", ".doc", ".rtf", ".odt", ".stf" };
    var saved = new List<string>(); var skipped = new List<string>(); var fileMap = new List<object>();
    foreach (var f in r.Files)
    {
        var ext = Path.GetExtension(f.Name ?? "").ToLowerInvariant();
        if (!okExt.Contains(ext) || string.IsNullOrEmpty(f.Base64)) { skipped.Add(f.Name ?? "?"); continue; }
        var path = Operations.UniquePath(dir, Operations.SafeFileName(Path.GetFileNameWithoutExtension(f.Name!)), ext);
        File.WriteAllBytes(path, Convert.FromBase64String(f.Base64));
        saved.Add(path); fileMap.Add(new { name = f.Name, path });
    }
    var items = saved.GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase).Select(g =>
    {
        string? Pick(params string[] ex) => g.FirstOrDefault(p => ex.Contains(Path.GetExtension(p).ToLowerInvariant()));
        var pdf = Pick(".pdf"); var docx = Pick(".docx"); var other = Pick(".doc", ".rtf", ".odt"); var stf = Pick(".stf");
        bool pdfFromWord = false;
        var wordLike = docx ?? other;
        if (wordLike != null && Office.Available)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "stfkit_up_" + Guid.NewGuid().ToString("N"));
            try
            {
                if (pdf == null) { var c = Office.Convert(wordLike, "pdf", tmp); if (c != null) { pdf = Path.Combine(dir, g.Key + ".pdf"); File.Copy(c, pdf, true); pdfFromWord = true; } }
                if (docx == null) { var c = Office.Convert(wordLike, "docx", tmp); if (c != null) { docx = Path.Combine(dir, g.Key + ".docx"); File.Copy(c, docx, true); } }
            }
            finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
        }
        var warning = pdf == null && wordLike != null ? "Keine PDF und keine Umwandlung möglich (LibreOffice auf dem Server fehlt?): Layout/Analyse nicht möglich" : null;
        return new { baseName = g.Key, dir, pdf, docx, doc = other, stf, pdfFromWord, warning };
    }).ToList();
    return Task.FromResult<object>(new { ok = true, batch, dir, count = items.Count, skipped, files = fileMap, items });
}));

// Ähnliche Formulare in Qdrant suchen (Text des Kundenformulars -> hybride Suche); bei mehreren Varianten desselben Formulars wird die neueste gewählt.
Post<CorpusSearchReq>(e, "/corpus/search", r => Guarded(async () =>
{
    var text = !string.IsNullOrWhiteSpace(r.Text) ? r.Text! : IrText(LoadIr(r.IrId));
    var hits = await QdrantSearch.SearchAsync(text, opt.Qdrant, r.Top ?? 8, r.OnlyStf ?? true, r.Kind);
    var best = QdrantSearch.PickBest(hits, opt.Qdrant);
    return new { ok = true, minCosine = opt.Qdrant.MinCosine, best = best?.Id, usable = best != null,
        hits = hits.Select(h => new { h.Id, h.Name, h.Folder, h.RelPath, h.StfPath, h.Cosine, h.Score, h.Kind, h.NormName, h.Modified, h.Category, h.Placeholders }) };
}));

// Neue/korrigierte Formulare (z. B. hochgeladener Ordner) in Qdrant aufnehmen, damit sie beim nächsten Mal als Vorlage zur Verfügung stehen.
Post<CorpusAddReq>(e, "/corpus/add", r => Guarded(async () =>
{
    var dir = guard.Dir(r.Dir);
    var (en, st) = BuildCorpus(new[] { dir }, null);
    var salt = "neu/" + Path.GetFileName(dir.TrimEnd('\\', '/'));
    foreach (var x in en) { x.Id = CorpusLoader.Uuid("", salt + "/" + x.RelPath); x.Folder = "_neu"; }
    var lr = await QdrantLoader.LoadAsync(en, opt.Qdrant);
    return new { ok = lr.Failed == 0, added = lr.Uploaded, loaded = lr, stats = st };
}));

// Punkte aus Qdrant löschen (ohne confirm=true wird nur gezählt)
Post<CorpusDeleteReq>(e, "/corpus/delete", r => Guarded(async () =>
{
    var (matched, deleted) = await QdrantLoader.DeleteAsync(opt.Qdrant, r.Ids, r.Category, r.Folder, r.RelPath, !(r.Confirm ?? false));
    return new { ok = true, matched, deleted };
}));

// Platzhalter-Namen des Korpus (Katalog) für die Zuordnung von Markierungen in Worten; gecacht wie /placeholders
IEnumerable<string> CatalogNames()
{
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var d in opt.CorpusDirs.Where(Directory.Exists))
        lock (catalogCache)
        {
            if (!catalogCache.TryGetValue(d, out var c) || c.At < DateTime.UtcNow.AddMinutes(-10)) catalogCache[d] = c = (DateTime.UtcNow, PlaceholderCatalog.Build(d));
            foreach (var en in c.Data) names.Add(en.Name);
        }
    return names;
}

Post<PlaceholderReq>(e, "/placeholders", r => Guarded(() =>
{
    var dir = guard.Dir(r.Dir);
    lock (catalogCache)
    {
        if (!catalogCache.TryGetValue(dir, out var c) || c.At < DateTime.UtcNow.AddMinutes(-10))
            catalogCache[dir] = c = (DateTime.UtcNow, PlaceholderCatalog.Build(dir));
        var entries = c.Data.Take(Math.Clamp(r.Top ?? 150, 1, 1000)).ToList();
        return Task.FromResult<object>(new { ok = true, count = entries.Count, entries });
    }
}));

Post<AnalyzeReq>(e, "/analyze", r => Guarded(() =>
{
    // PDF vorhanden: Text, Linien, Bilder, Felder mit Positionen. Kein PDF (Word ohne LibreOffice): nur Text der Absätze (textOnly) - die Positionen kommen später aus einer ähnlichen Vorlage.
    var docx = string.IsNullOrWhiteSpace(r.Docx) ? null : guard.File(r.Docx, ".docx");
    string? pdf = null, other = null, ocrError = null; IrDocument ir; bool textOnly = false;
    if (!string.IsNullOrWhiteSpace(r.Pdf))
    {
#if PDF
        pdf = guard.File(r.Pdf, ".pdf");
        ir = StfKit.Pdf.PdfExtractor.Extract(pdf);
        ir.Source = pdf;
        // Scan (Seite = nur ein Bild): Text per Bild-Modell lesen; schlägt das fehl, bleibt die Seite leer und wird im Bericht als Scan gemeldet
        if (!string.IsNullOrWhiteSpace(opt.Qdrant.OcrModel) && ScanOcr.ScanPages(ir).Count > 0)
            try { ScanOcr.ApplyAsync(ir, opt.Qdrant.OllamaUrl, opt.Qdrant.OcrModel).GetAwaiter().GetResult(); } catch (Exception ex) { ocrError = ex.Message; }
#else
        throw new GuardException("Ohne PDF-Unterstützung gebaut (-p:WithPdf=false)");
#endif
    }
    else
    {
        List<DocxLayout.Para> paras; string src;
        if (docx != null) { paras = DocxLayout.ReadParagraphs(docx); src = docx; }
        else if (!string.IsNullOrWhiteSpace(r.Other))
        {
            other = guard.FileAny(r.Other, ".rtf", ".odt", ".doc");
            if (other.EndsWith(".doc", StringComparison.OrdinalIgnoreCase))
                throw new GuardException("Altes Word-Format (.doc) kann ohne PDF und ohne LibreOffice nicht gelesen werden: bitte als .docx oder PDF speichern bzw. LibreOffice auf dem Server installieren");
            paras = DocxLayout.FromPlainText(CorpusLoader.OtherText(other) ?? ""); src = other;
        }
        else throw new GuardException("Weder PDF noch Word übergeben");
        if (!paras.Any(q => q.Text.Length > 0)) throw new GuardException("Im Dokument wurde kein Text gefunden");
        ir = DocxLayout.ToIr(paras, src); textOnly = true;
    }
    if (docx != null) { ir.Fields = DocxFields.Read(docx).Where(f => f.Kind == "MERGEFIELD").Select(f => f.Name).Distinct().ToList(); ir.Notes = DocxNotes.Read(docx); PlaceholderRepair.ApplyKnown(ir, PlaceholderRepair.NamesFromDocx(docx).Concat(ir.Notes.CommentPlaceholders.Values)); }
    var srcName = pdf ?? docx ?? other ?? "dokument";
    var irId = Operations.SafeFileName(Path.GetFileNameWithoutExtension(srcName)) + "_" + Guid.NewGuid().ToString("N")[..8] + ".ir.json";
    if (!irIdRx.IsMatch(irId)) irId = "doc_" + Guid.NewGuid().ToString("N")[..8] + ".ir.json";
    ir.Save(Path.Combine(opt.WorkDir, irId));
    return Task.FromResult<object>(new
    {
        ok = true, irId, source = srcName, docx, textOnly, pages = ir.Pages.Count, textLines = ir.Pages.Sum(p => p.Lines.Count), fields = ir.Fields,
        linesTodo = ir.Pages.Sum(p => p.Rules.Count + p.Rects.Count), scanPages = ir.Pages.Count(p => p.Images.Any(m => m.Background)), ocr = ir.Ocr, ocrError, imagesTodo = ir.Pages.Sum(p => p.Images.Count(i => string.IsNullOrEmpty(i.Png) && !i.Background)), picturesFound = ir.Pages.Sum(p => p.Images.Count(i => !string.IsNullOrEmpty(i.Png))),
        notes = new { comments = ir.Notes.Comments.Select(c => new { c.Author, c.Text, c.Anchor }), highlights = ir.Notes.Highlights.Select(h => new { h.Color, h.Text, h.Context }), kindHint = ir.Notes.KindHint, printRules = ir.Notes.PrintRules, commentPlaceholders = ir.Notes.CommentPlaceholders },
        promptContext = textOnly ? ir.Paragraphs.Take(80).ToList() : ir.Pages.SelectMany(p => p.Lines).Select(l => l.Text).Take(80).ToList(),
        inputFields = ir.Pages.SelectMany((p, i) => FieldSlots.Find(p).Select(sl => new { page = i + 1, key = sl.Key, label = sl.Label })).ToList()
    });
}));

Post<ClassifyReq>(e, "/classify", r => Guarded(() =>
{
    var ir = LoadIr(r.IrId); var feat = FormFeatures.FromIr(ir, r.Name ?? "");
    var c = FormClassifier.Classify(feat);
    try { var ixc = GetIndex(null); if (ixc != null) c = FormClassifier.Refine(c, ixc.Query(feat, 5), feat.HintKind); } catch { }
    return Task.FromResult<object>(new { ok = true, kind = c.Kind, confidence = c.Confidence, suggestedArt = c.SuggestedArt, isBescheid = c.IsBescheid, ranking = c.Ranking, signals = c.Signals, kindHint = ir.Notes.KindHint });
}));

Post<SimilarReq>(e, "/similar", r => Guarded(() =>
{
    var ir = LoadIr(r.IrId); var feat = FormFeatures.FromIr(ir, r.Name ?? "");
    var ix = GetIndex(r.CorpusDirs) ?? throw new GuardException("Kein Korpus konfiguriert (StfKit:CorpusDirs oder corpusDirs im Aufruf)");
    var hits = ix.Query(feat, Math.Clamp(r.Top ?? 5, 1, 20));
    return Task.FromResult<object>(new { ok = true, corpusForms = ix.Forms.Count, duplicates = ix.Duplicates, minSimilarity = opt.MinSimilarity, hits = hits.Select(h => new { h.Name, h.Score, h.TextSim, h.StructSim, h.Kind, h.Art, h.Pages, h.PlaceholderCount }) });
}));

Post<GenerateReq>(e, "/generate", r => Guarded(() =>
{
    var ir = LoadIr(r.IrId);
    var outDir = guard.DirForWrite(r.OutDir);
    var eol = r.Eol switch { "lf" => "\n", "crcrlf" => "\r\r\n", _ => "\r\n" };
    var map = r.Mapping is { } m ? Operations.ParseMap(m) : new Dictionary<string, string>();
    var slots = r.Slots is { } sj ? JsonSerializer.Deserialize<Dictionary<string, SlotDef>>(sj.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
    var textMap = r.TextMapping is { } tmj ? Operations.ParseMap(tmj) : null;
    var baseName = Operations.SafeFileName(string.IsNullOrWhiteSpace(r.Name) ? Path.GetFileNameWithoutExtension(r.IrId!) : r.Name);
    var version = r.Version ?? "DB2026";
    string mode = (r.Mode ?? "auto").ToLowerInvariant();
    var feat = FormFeatures.FromIr(ir, baseName); var cls = FormClassifier.Classify(feat);

    // Vorlage bestimmen
    SimilarHit? best = null; StfDocument? tpl = null; string tplName = ""; string? tplPath = null; var topHits = new List<SimilarHit>();
    if (!string.IsNullOrWhiteSpace(r.TemplatePath)) { tplPath = guard.File(r.TemplatePath, ".stf"); }
    StfDocument? qTplDoc = null; QdrantHit? qBest = null; List<QdrantHit>? qHits = null; string? qError = null;
    if (string.IsNullOrWhiteSpace(r.TemplatePath) && mode != "scratch" && (r.UseQdrant ?? opt.Qdrant.UseForTemplates) && !string.IsNullOrWhiteSpace(opt.Qdrant.Url))
    {
        try
        {
            bool wantB = mode == "bescheid" || (mode == "auto" && cls.IsBescheid);
            qHits = QdrantSearch.SearchAsync(IrText(ir), opt.Qdrant, 10, true, wantB ? "Gebührenbescheid" : null, true).GetAwaiter().GetResult();
            qBest = QdrantSearch.PickBest(qHits, opt.Qdrant);
            if (qBest != null)
            {
                if (qBest.StfPath != null && File.Exists(qBest.StfPath)) { try { tplPath = guard.File(qBest.StfPath, ".stf"); } catch (GuardException) { tplPath = null; } }
                if (tplPath == null && !string.IsNullOrEmpty(qBest.Data)) { qTplDoc = StfDocument.Parse(Encoding.UTF8.GetBytes(qBest.Data)); }     // Datei nicht erreichbar: Inhalt aus Qdrant
                if (tplPath == null && qTplDoc == null) qBest = null;
            }
        }
        catch (Exception ex) { qError = ex.Message; log?.LogWarning("Qdrant-Suche fehlgeschlagen: {Msg}", ex.Message); }
    }
    if (!string.IsNullOrWhiteSpace(r.TemplatePath)) { }
    else if (mode != "scratch" && tplPath == null && qTplDoc == null)
    {
        var ix = GetIndex(r.CorpusDirs);
        if (ix != null)
        {
            topHits = ix.Query(feat, 8);
            cls = FormClassifier.Refine(cls, topHits, feat.HintKind);
            bool wantBescheid = mode == "bescheid" || (mode == "auto" && cls.IsBescheid);
            best = wantBescheid ? topHits.FirstOrDefault(h => h.Kind == "Gebührenbescheid") : topHits.FirstOrDefault();
            // ähnlichstes Formular, brauchbar ab MinSimilarity (sonst von Grund auf)
            if (best?.Path != null && (wantBescheid || FormReview.TemplateUsable(best, cls, r.MinSimilarity ?? opt.MinSimilarity) || mode == "inherit")) tplPath = best.Path;
        }
    }
    string effective = mode;
    bool haveTpl = tplPath != null || qTplDoc != null;
    bool textOnly = ir.TextOnly;                 // Word ohne PDF: es gibt keine Positionen, Geometrie kann nur aus der Vorlage kommen
    if (mode == "auto") 
        effective = !haveTpl ? "scratch" : (textOnly ? "textonly" : (cls.IsBescheid ? "bescheid" : "inherit"));
    else if 
        (textOnly && haveTpl && (mode == "inherit" || mode == "bescheid" || mode == "reuse")) effective = "textonly";
    // Das gleiche Formular ist praktisch schon vorhanden (Ähnlichkeit >= ReuseSimilarity): Vorlage vollständig behalten, nur geänderte Texte anpassen (kein Neubau mit verlorenen Platzhaltern)
    double? tplSim = qBest?.Cosine ?? best?.Score;
    if (effective == "inherit" && mode == "auto" && haveTpl && tplSim >= opt.ReuseSimilarity) effective = "reuse";
    if (effective == "reuse" && !haveTpl) throw new GuardException("Modus reuse: keine Vorlage gefunden");
    if ((effective == "bescheid" || (textOnly && mode == "bescheid")) && !haveTpl)
        throw new GuardException("Gebührenbescheid erkannt, aber keine vorhandene Bescheid-Vorlage im Korpus gefunden. Ein Gebührenbescheid lässt sich in Aeneas nicht neu anlegen: bitte einen vorhandenen Bescheid exportieren (FormulareExport) und den Korpus-Ordner angeben.");
    if (tplPath != null) { tpl = StfDocument.Load(tplPath); tplName = qBest != null ? qBest.Name : FormFeatures.NiceName(tplPath); }
    else if (qTplDoc != null) { tpl = qTplDoc; tplName = qBest!.Name; }
    if (effective == "scratch") tpl = null;

    bool replaceMode = effective == "bescheid" || (effective == "textonly" && cls.IsBescheid);      // Gebührenbescheid: vorhandenen Bescheid ersetzen
    var formName = replaceMode ? tplName : baseName;
    var outPath = Operations.UniquePath(outDir, Operations.SafeFileName(formName), ".stf");      // überschreibt nie vorhandene Dateien
    Operations.GenResult res;
    lock (genLock)
        res = Operations.Generate(ir, outPath, version, map, eol, (r.UseKnowledge ?? true) ? kb : null, slots, tpl, effective, tplName, textMap, CatalogNames());

    // Vorschau-PDF (Näherung) mit Original-Overlay
    string? previewPath = null;
    if (r.Preview ?? true)
    {
        var pdir = Path.Combine(outDir, "_vorschau"); Directory.CreateDirectory(pdir);
        previewPath = Path.Combine(pdir, Path.GetFileNameWithoutExtension(outPath) + ".vorschau.pdf");
        File.WriteAllBytes(previewPath, StfPreview.Render(StfDocument.Load(outPath), new StfPreview.Options(textOnly ? null : ir, null, Path.GetFileNameWithoutExtension(outPath))));       // ohne PDF kein Original-Overlay (die Positionen im IR sind künstlich)
    }

    var notes = (res.Adapt?.Notes ?? new List<string>()).Take(25).ToList();
    double minSim = r.MinSimilarity ?? opt.MinSimilarity;
    string templateRemark = effective == "scratch" && mode != "scratch" ? FormReview.TemplateRemark(best, cls, minSim) : "";
    if (templateRemark.Length > 0 && qHits is { Count: > 0 } && qBest == null) templateRemark = $"{FormReview.NoTemplate} (nächster Qdrant-Treffer \"{qHits[0].Name}\": Ähnlichkeit {qHits[0].Cosine:0.00} < {opt.Qdrant.MinCosine:0.00})";
    if (templateRemark.Length > 0) notes.Insert(0, templateRemark);
    if (qBest != null) notes.Insert(0, $"Vorlage aus Qdrant: \"{qBest.Name}\" ({qBest.Folder}), Ähnlichkeit {qBest.Cosine:0.00}, Stand {qBest.Modified:yyyy-MM-dd}" + (qBest.StfPath != null && File.Exists(qBest.StfPath) ? "" : " (Inhalt aus Qdrant, Datei nicht erreichbar)"));
    if (qError != null) notes.Add("Qdrant-Suche nicht möglich (" + qError + "), Ordner-Index verwendet");
    if (tpl != null && (effective == "inherit" || effective == "reuse") && FormReview.TypeNote(best, cls) is { Length: > 0 } typeNote) notes.Insert(0, typeNote);
    // Layout-Prüfung: trifft das erzeugte Formular die Positionen des gesendeten PDFs? (bei Word ohne PDF gibt es keine Positionen)
    LayoutResult? layout = null, layoutBefore = null; SnapResult? snap = null;
    try
    {
        if (!textOnly)
        {
            var gdoc = StfDocument.Load(outPath); layout = LayoutCheck.Measure(ir, StfToIr.Convert(gdoc));
            // Vorlage-Geometrie weicht vom PDF ab: Texte/Platzhalter der Vorlage behalten, Positionen am PDF ausrichten (abschaltbar mit align=false)
            if (layout.Poor && (r.Align ?? true) && effective != "scratch")
            {
                layoutBefore = layout; snap = LayoutSnap.Apply(gdoc, ir);
                if (snap.RemovedShadowed is { Count: > 0 }) notes.Add($"{snap.RemovedShadowed.Count} überlagerte Text(e) der Vorlage entfernt (stehen nicht im PDF und lagen über neu erzeugtem Text): " + string.Join(" | ", snap.RemovedShadowed.Take(6)));
                if (snap.PagesAligned > 0) { gdoc.Save(outPath); layout = LayoutCheck.Measure(ir, StfToIr.Convert(StfDocument.Load(outPath))); notes.Add($"Layout an das PDF ausgerichtet (Vorlage-Geometrie verschoben, {snap.Anchors} Anker auf {snap.PagesAligned} Seite(n)): vorher {layoutBefore.Within15} von {layoutBefore.Matched}, jetzt {layout.Within15} von {layout.Matched} Zeilen innerhalb 1,5 mm."); }
                else snap = null;
            }
        }
    }
    catch { }
    foreach (var pn in ir.Notes.RemovedPersonNames.Take(4)) notes.Add("Personenname aus dem Muster entfernt: " + pn);
    if (layout is { Poor: true }) notes.Add("PRÜFEN: Layout weicht vom PDF ab - " + layout.Describe() + ". Das Ergebnis übernimmt die Geometrie der Vorlage; mode=scratch (Layout aus dem PDF) zum Vergleich versuchen.");
    var reviewReasons = FormReview.ReviewReasons(cls, res.ImagesTodo, res.UnassignedHighlights);
    reviewReasons.InsertRange(0, FormReview.ScanReasons(ir));
    if (textOnly)
    {
        reviewReasons.Insert(0, effective == "textonly"
            ? $"Word ohne PDF: nur der Text wurde übernommen, Positionen/Linien/Bilder stammen aus der Vorlage \"{tplName}\" - Layout in Aeneas prüfen"
            : "Word ohne PDF und keine passende Vorlage: nur ein einfacher Fließtext (A4, linker Rand) - Positionen, Tabellen und Linien von Hand gestalten");
        if (res.Adapt is { Conflicts: > 0 } ad0) reviewReasons.Add($"{ad0.Conflicts} Absatz/Absätze aus dem Word ohne Position in der Vorlage (nicht eingefügt, siehe Hinweise)");
        if (res.Adapt != null && res.Adapt.Notes.Any(n => n.Contains("PRÜFEN"))) reviewReasons.Add("abweichender Wortlaut oder zu langer Text in Boxen (siehe Hinweise)");
    }
    if (reviewReasons.Count > 0) notes.Insert(0, "PRÜFEN (Mensch): " + string.Join("; ", reviewReasons));
    if (res.JoinedPlaceholders is { Count: > 0 }) notes.Add("abgerissene Platzhalter zusammengesetzt: " + string.Join("; ", res.JoinedPlaceholders));
    if (res.AutoPlaceholders is { Count: > 0 }) notes.Add("Markierung ohne Kommentar automatisch zugeordnet: " + string.Join("; ", res.AutoPlaceholders.Take(12)));
    if (replaceMode && best != null && best.Score < (r.MinSimilarity ?? opt.MinSimilarity))
        notes.Insert(0, $"ACHTUNG: gewählte Bescheid-Vorlage \"{tplName}\" ist nur wenig ähnlich ({best.Score:0.00}) - bitte prüfen, ob es der Bescheid DIESES Kunden ist (Import-Modus Ersetzen!)");
    var docComments = ir.Notes.Comments.Select(c => (c.Anchor.Length > 0 ? "[" + (c.Anchor.Length > 60 ? c.Anchor[..60] + "…" : c.Anchor) + "] " : "") + c.Text).ToList();
    if (ir.Notes.KindHint != null && cls.Kind != ir.Notes.KindHint) notes.Add($"Kommentar im Dokument nennt Typ {ir.Notes.KindHint}, erkannt wurde {cls.Kind}");
    var modus = replaceMode ? "Ersetzen" : "Neu";
    var manifest = Operations.AppendManifest(outDir, Path.GetFileName(outPath), formName, replaceMode ? (best?.Art ?? "Formular") : cls.SuggestedArt, null, modus,
        replaceMode ? tplName : null, tpl != null ? tplName : null, qBest?.Cosine ?? best?.Score, string.Join(" | ", notes.Take(7).Concat(ir.Notes.PrintRules.Select(x => "Druckregel: " + x))));
    var metaDir = Path.Combine(outDir, "_meta"); Directory.CreateDirectory(metaDir);
    File.WriteAllText(Path.Combine(metaDir, Path.GetFileName(outPath) + ".json"), JsonSerializer.Serialize(new { irId = r.IrId, mode = effective, kind = cls.Kind, template = tpl != null ? tplName : null, created = DateTime.Now.ToString("s") }));
    return Task.FromResult<object>(new
    {
        ok = true, stf = res.Stf, renamed = !Path.GetFileName(outPath).Equals(Operations.SafeFileName(formName) + ".stf", StringComparison.OrdinalIgnoreCase), version = res.Version,
        pages = res.Pages, texts = res.Texts, placeholders = res.Placeholders, unmappedFields = res.UnmappedFields, slotsFilled = res.SlotsFilled,
        linesMade = res.Lines, linesTodo = res.LinesTodo, imagesTodo = res.ImagesTodo, picturesMade = res.PicturesMade, textMapped = res.TextMapped, docComments, printRules = ir.Notes.PrintRules,
        kind = cls.Kind, kindConfidence = cls.Confidence, isBescheid = cls.IsBescheid, suggestedArt = cls.SuggestedArt,
        mode = effective, template = tpl != null ? tplName : null, similarity = qBest?.Cosine ?? best?.Score, templateSource = qBest != null ? "qdrant" : (tpl != null ? "index" : null), templateFolder = qBest?.Folder, templateModified = qBest?.Modified, qdrantError = qError, replaceTarget = replaceMode ? tplName : null, textOnly,
        importMode = modus, manifest, layout, layoutBefore, layoutAligned = snap != null, previewPdf = previewPath, usedKnowledge = (r.UseKnowledge ?? true) && kb != null,
        adapt = res.Adapt == null ? null : new { res.Adapt.Mode, res.Adapt.Inherited, res.Adapt.Replaced, res.Adapt.Unchanged, res.Adapt.Untouched, res.Adapt.Added, res.Adapt.Conflicts },
        notes, similarHits = topHits.Take(3).Select(h => new { h.Name, h.Score, h.Kind }),
        templateUsable = tpl != null, templateRemark, needsReview = reviewReasons.Count > 0, reviewReasons,
        autoPlaceholders = res.AutoPlaceholders, unassignedHighlights = res.UnassignedHighlights, joinedPlaceholders = res.JoinedPlaceholders
    });
}));

Post<PreviewReq>(e, "/preview", r => Guarded(() =>
{
    var stf = guard.File(r.Stf, ".stf"); var ir = string.IsNullOrWhiteSpace(r.IrId) ? null : LoadIr(r.IrId);
    var pdir = Path.Combine(Path.GetDirectoryName(stf)!, "_vorschau"); Directory.CreateDirectory(pdir);
    var pdf = Path.Combine(pdir, Path.GetFileNameWithoutExtension(stf) + ".vorschau.pdf");
    var doc = StfDocument.Load(stf);
    File.WriteAllBytes(pdf, StfPreview.Render(doc, new StfPreview.Options(ir, null, Path.GetFileNameWithoutExtension(stf))));
    return Task.FromResult<object>(new { ok = true, pdf, pages = doc.Pages().Count, overlay = ir != null });
}));

// Lernen aus einer korrigierten .stf (gemeinsam für /learn und /stf/ingest)
LearnReport DoLearn(string genPath, string corPath, string? irId, string? name)
{
    if (kb == null) throw new GuardException("Lernen ist nicht aktiviert (StfKit:KnowledgeDir nicht gesetzt)");
    var gen = StfDocument.Load(genPath); var cor = StfDocument.Load(corPath);
    var irRef = irId;
    if (string.IsNullOrWhiteSpace(irRef))
    {
        var meta = Path.Combine(Path.GetDirectoryName(genPath)!, "_meta", Path.GetFileName(genPath) + ".json");
        if (File.Exists(meta)) try { irRef = JsonDocument.Parse(File.ReadAllText(meta)).RootElement.GetProperty("irId").GetString(); } catch { }
    }
    IrDocument? ir = null; if (!string.IsNullOrWhiteSpace(irRef) && irIdRx.IsMatch(irRef) && File.Exists(Path.Combine(opt.WorkDir, irRef))) ir = LoadIr(irRef);
    LearnReport rep;
    lock (genLock) { rep = kb.Learn(gen, cor, string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(corPath) : name, ir); kb.Apply(); }
    return rep;
}

Post<LearnReq>(e, "/learn", r => Guarded(() =>
{
    var rep = DoLearn(guard.File(r.GeneratedStf, ".stf"), guard.File(r.CorrectedStf, ".stf"), r.IrId, r.Name);
    return Task.FromResult<object>(new { ok = true, report = rep });
}));

// Hochgeladene .stf verarbeiten: (1) als neue Vorlage in Qdrant aufnehmen, (2) wenn sie die korrigierte Fassung eines von der KI erzeugten Formulars ist, daraus lernen.
// mode: auto (Vorlage aufnehmen + lernen, falls ein passendes erzeugtes Formular gefunden wird) | template (nur aufnehmen) | learn (nur lernen).
// searchDir: Ordner (mit Unterordnern), in dem die erzeugten Formulare liegen (Ausgabeordner; erkannt an _meta\<datei>.json).
Post<StfIngestReq>(e, "/stf/ingest", r => Guarded(async () =>
{
    if (r.Files == null || r.Files.Length == 0) throw new GuardException("Keine .stf übergeben");
    var mode = (r.Mode ?? "auto").Trim().ToLowerInvariant(); if (mode is not ("auto" or "template" or "learn")) mode = "auto";
    string? searchDir = null;
    if (!string.IsNullOrWhiteSpace(r.SearchDir) && mode != "template") { var sd = guard.DirForWrite(r.SearchDir); if (Directory.Exists(sd)) searchDir = sd; }
    var tplRoot = string.IsNullOrWhiteSpace(opt.TemplateDir) ? Path.Combine(opt.AllowedRoots[0], "_vorlagen") : opt.TemplateDir;
    static string Key(string path) { var b = Path.GetFileNameWithoutExtension(path).ToLowerInvariant().Trim(); return Regex.Replace(b, @"(_ki\d*)+$", ""); }
    var results = new List<object>();
    foreach (var up in r.Files)
    {
        var res = new Dictionary<string, object?>(); var actions = new List<string>(); var warnings = new List<string>();
        string path = "";
        try
        {
            path = guard.File(up, ".stf");
            res["name"] = Path.GetFileNameWithoutExtension(path); res["file"] = path;
            try { StfDocument.Load(path); } catch (Exception ex) { throw new GuardException("Keine gültige .stf: " + ex.Message); }

            // (2) passendes erzeugtes Formular suchen (gleicher Name ohne _KI-Zusatz, mit _meta-Eintrag; das neueste)
            string? generated = null;
            if (searchDir != null)
            {
                var key = Key(path);
                generated = Directory.EnumerateFiles(searchDir, "*.stf", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "_vorschau" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "_meta" + Path.DirectorySeparatorChar))
                    .Where(f => !f.Equals(path, StringComparison.OrdinalIgnoreCase) && Key(f) == key && File.Exists(Path.Combine(Path.GetDirectoryName(f)!, "_meta", Path.GetFileName(f) + ".json")))
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
            }
            res["generated"] = generated;
            if (mode == "learn" && generated == null) throw new GuardException("Zum Lernen wurde kein erzeugtes Formular mit gleichem Namen im Ausgabeordner gefunden (Name der korrigierten .stf = Name des erzeugten Formulars, ohne _KI)");
            if (generated != null && mode != "template")
            {
                if (kb == null) warnings.Add("Lernen nicht aktiviert (StfKit:KnowledgeDir leer): nur als Vorlage aufgenommen");
                else
                {
                    var rep = DoLearn(guard.File(generated, ".stf"), path, null, null);
                    actions.Add("gelernt"); res["learned"] = rep;
                }
            }
            else if (mode == "auto" && searchDir != null) warnings.Add("Kein erzeugtes Formular mit diesem Namen gefunden: nicht gelernt, nur als Vorlage aufgenommen");

            // (1) als neue Vorlage aufnehmen: Datei (und gleichnamige Originale PDF/Word aus demselben Upload) dauerhaft ablegen, dann nach Qdrant
            if (mode != "learn")
            {
                var dest = guard.DirForWrite(Path.Combine(tplRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6]));
                Directory.CreateDirectory(dest);
                var baseName = Path.GetFileNameWithoutExtension(path); var srcDir = Path.GetDirectoryName(path)!;
                foreach (var ext in new[] { ".stf", ".pdf", ".docx", ".doc", ".rtf", ".odt" })
                { var f = Path.Combine(srcDir, baseName + ext); if (File.Exists(f)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true); }
                var (en, st) = BuildCorpus(new[] { dest }, null);
                var salt = "neu/" + Path.GetFileName(dest);
                foreach (var x in en) { x.Id = CorpusLoader.Uuid("", salt + "/" + x.RelPath); x.Folder = "_neu"; }
                if (en.Count == 0) throw new GuardException("Aus der Datei konnte kein Eintrag erzeugt werden: " + string.Join("; ", st.Problems.Take(3)));
                var lr = await QdrantLoader.LoadAsync(en, opt.Qdrant);
                if (lr.Failed > 0) throw new GuardException("Qdrant: " + string.Join("; ", lr.Errors.Take(2)));
                actions.Add("als Vorlage in Qdrant aufgenommen");
                res["templatePath"] = Path.Combine(dest, baseName + ".stf"); res["qdrantIds"] = en.Select(x => x.Id).ToList(); res["embedSource"] = en[0].EmbedSource;
            }
            res["ok"] = true;
        }
        catch (GuardException ex) { res["ok"] = false; res["error"] = ex.Message; }
        catch (Exception ex) { log?.LogError(ex, "stf/ingest"); res["ok"] = false; res["error"] = ex.Message; }
        res["actions"] = actions; res["warnings"] = warnings;
        results.Add(res);
    }
    return new { ok = results.All(x => (bool)((Dictionary<string, object?>)x)["ok"]!), mode, count = results.Count, results };
}));

e.MapGet("/knowledge", ctx => Write(ctx, kb == null ? new { ok = false, error = "Lernen nicht aktiviert" } : (object)new
{
    ok = true, corrections = kb.Cal.Corrections, textSamples = kb.Cal.TextSamples, lineSamples = kb.Cal.LineSamples, calibration = kb.Cal, rewrites = kb.Rewrites.Count, slots = kb.Slots.Count,
    slotLabels = kb.Slots.Select(x => new { label = x.Key, text = x.Value.Text, count = x.Value.Count }).Take(100)
}));
}   // MapRoutes

var host = Host.CreateDefaultBuilder(args)
#if WINSVC
    .UseWindowsService()
#endif
    .ConfigureWebHostDefaults(web => web.UseConfiguration(cfg).Configure(app =>
    {
        log = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("StfKit");
        if (!useKey) log.LogWarning("StfKit:ApiKey ist leer: keine Authentifizierung. Nur mit Urls=http://127.0.0.1:... betreiben (nur dieser Rechner).");
        // --- Authentifizierung (alles außer /health) ---
        app.Use(async (ctx, next) =>
        {
            if (!useKey || ctx.Request.Path.StartsWithSegments("/health")) { await next(); return; }
            var got = ctx.Request.Headers["X-Api-Key"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(opt.ApiKey)))
            { await Write(ctx, new { ok = false, error = "Unauthorized" }, 401); return; }
            await next();
        });
        app.UseRouting();
        app.UseEndpoints(MapRoutes);
    })).Build();
await host.RunAsync();
return 0;

// ---------------------------------------------------------------------------
sealed class ApiOptions
{
    public string ApiKey { get; set; } = "";
    public string WorkDir { get; set; } = Path.Combine(Path.GetTempPath(), "StfKit");
    public string[] AllowedRoots { get; set; } = Array.Empty<string>();
    public string[] CorpusDirs { get; set; } = Array.Empty<string>();      // Ordner mit vorhandenen .stf (Export aus Aeneas), unterhalb AllowedRoots
    public string KnowledgeDir { get; set; } = "";                           // Gedächtnis aus Korrekturen (leer = Lernen aus)
    public string TemplateDir { get; set; } = "";                              // dauerhafte Ablage hochgeladener .stf-Vorlagen (leer = <erster AllowedRoot>\\_vorlagen)
    public string InboxDir { get; set; } = "";                                  // Ablage hochgeladener Dateien (leer = <erster AllowedRoot>\_inbox)
    public QdrantOptions Qdrant { get; set; } = new QdrantOptions();          // Ziel für /corpus/load (Sammlung Formularerstellung)
    public double MinSimilarity { get; set; } = 0.45;                        // ab hier wird ein ähnliches Formular als Basis verwendet
    public double ReuseSimilarity { get; set; } = 0.95;                      // ab hier gilt das Formular als schon vorhanden: Vorlage vollständig behalten (Modus reuse), nur Texte anpassen
    public string KindsFile { get; set; } = "";                                // zusätzliche Formulartypen (JSON, siehe FormClassifier.LoadExtraKinds); leer = <KnowledgeDir>\kinds.json
}
record CorpusReq(string[]? CorpusDirs, int? Limit, bool? List);
record UploadFile(string? Name, string? Base64);
record UploadReq(UploadFile[]? Files, string? Kunde);
record CorpusSearchReq(string? IrId, string? Text, int? Top, bool? OnlyStf, string? Kind);
record CorpusAddReq(string? Dir);
record CorpusDeleteReq(string[]? Ids, string? Category, string? Folder, string? RelPath, bool? Confirm);
record ScanReq(string? Dir, bool? Recursive, bool? Convert = null);
record PlaceholderReq(string? Dir, int? Top);
record AnalyzeReq(string? Pdf, string? Docx, string? Other = null);
record GenerateReq(string? IrId, string? Name, string? OutDir, string? Version, string? Eol, JsonElement? Mapping, JsonElement? Slots = null, string? Mode = null,
    string? TemplatePath = null, string[]? CorpusDirs = null, double? MinSimilarity = null, bool? UseKnowledge = null, bool? Preview = null, JsonElement? TextMapping = null, bool? UseQdrant = null, bool? Align = null);
record ClassifyReq(string? IrId, string? Name);
record SimilarReq(string? IrId, string? Name, string[]? CorpusDirs, int? Top);
record PreviewReq(string? Stf, string? IrId);
record StfIngestReq(string[]? Files, string? SearchDir, string? Mode);
record LearnReq(string? GeneratedStf, string? CorrectedStf, string? IrId, string? Name);

sealed class GuardException : Exception { public GuardException(string m) : base(m) { } }

/// <summary>Lässt nur Pfade unterhalb der konfigurierten Wurzeln zu (kein Shell-Zugriff, keine .. -Tricks).</summary>
sealed class PathGuard
{
    readonly string[] roots;
    readonly StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public PathGuard(IEnumerable<string> r, IEnumerable<string>? readOnly = null)
    {
        roots = r.Select(x => Path.GetFullPath(x).TrimEnd('\\', '/')).ToArray();
        readRoots = (readOnly ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Path.GetFullPath(x).TrimEnd('\\', '/')).ToArray();
    }
    readonly string[] readRoots;

    string Resolve(string? p, bool write = false)
    {
        if (string.IsNullOrWhiteSpace(p)) throw new GuardException("Pfad fehlt");
        if (p.IndexOf('\0') >= 0) throw new GuardException("Ungültiger Pfad");
        var full = Path.GetFullPath(p).TrimEnd('\\', '/');
        if (!(write ? roots : roots.Concat(readRoots)).Any(r => full.Equals(r, cmp) || full.StartsWith(r + Path.DirectorySeparatorChar, cmp) || full.StartsWith(r + "/", cmp)))
            throw new GuardException("Pfad liegt außerhalb der erlaubten Verzeichnisse");
        return full;
    }
    public string Dir(string? p) { var f = Resolve(p); if (!Directory.Exists(f)) throw new GuardException("Verzeichnis nicht gefunden"); return f; }
    public string DirForWrite(string? p) => Resolve(p, true);
    public string FileAny(string? p, params string[] exts)
    {
        var f = Resolve(p);
        if (!exts.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase))) throw new GuardException("Erwartet " + string.Join(" / ", exts));
        if (!System.IO.File.Exists(f)) throw new GuardException("Datei nicht gefunden");
        return f;
    }
    public string File(string? p, string ext)
    {
        var f = Resolve(p);
        if (!f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) throw new GuardException("Erwartet " + ext);
        if (!System.IO.File.Exists(f)) throw new GuardException("Datei nicht gefunden");
        return f;
    }
}