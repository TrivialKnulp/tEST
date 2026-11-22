using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace StfKit.Core
{

/// <summary>Ein Formular aus dem Korpus: .stf (Ergebnis) und/oder Original (PDF/Word) mit gleichem Basisnamen im selben Ordner.</summary>
public sealed class CorpusEntry
{
    public string Id { get; set; } = "";                 // deterministische UUID aus dem relativen Pfad (erneutes Laden überschreibt, keine Duplikate)
    public string Root { get; set; } = "";
    public string RelPath { get; set; } = "";             // Ordner + Basisname relativ zum Korpus-Ordner
    public string Folder { get; set; } = "";              // erster Unterordner unter dem Korpus-Ordner (z. B. Stadt/Kunde)
    public string Name { get; set; } = "";
    public string? Stf { get; set; }
    public string? Pdf { get; set; }
    public string? Docx { get; set; }
    public string? Doc { get; set; }
    public List<string> Others { get; } = new List<string>();   // weitere Originale (rtf/odt) bzw. bei Einzeldateien die Datei selbst
    public string EmbedData { get; set; } = "";           // Text der Vorlage (PDF > Word > Text der .stf): danach wird gesucht
    public string EmbedSource { get; set; } = "";         // pdf | docx | stf
    public string Data { get; set; } = "";                // Inhalt der .stf (leer, wenn es nur ein Original gibt)
    public string? Sha1 { get; set; }
    public string Kind { get; set; } = "";               // Formulartyp laut Klassifikator (nur bei .stf), z. B. "Gebührenbescheid"
    public string Category { get; set; } = "Formular";   // "Dokument" = langer Text über maxChars (QM-Doku, Satzung ...), "Scan" = kein Text lesbar (nur Datei + Name), "Bild" = Bilddatei (z. B. Hintergrund-Screenshot): wird geladen, aber getrennt filterbar
    public string? PdfLayout { get; set; }               // JSON: Seiten mit Textzeilen (Position/Größe/Schrift), Linien, Rechtecken, Bildpositionen, Formularfeldern
    public string LayoutSource { get; set; } = "";       // "pdf" (Original-PDF) | "konvertiert" (aus Word/RTF/ODT per LibreOffice erzeugt)
    public string? DocxInfo { get; set; }                // JSON: Kommentare, Markierungen, Druckregeln, MERGEFIELD-Felder aus dem Word
    public List<string> Warnings { get; } = new List<string>();
}

public sealed class CorpusStats
{
    public Dictionary<string, int> FilesByExt { get; set; } = new Dictionary<string, int>();
    public int Groups { get; set; }
    public int WithStf { get; set; }
    public int OnlyOriginal { get; set; }                 // PDF/Word ohne .stf
    public int OnlyStf { get; set; }
    public int DuplicateStf { get; set; }
    public int DuplicateOriginal { get; set; }            // identische PDF/Word-Originale ohne .stf (z. B. bei mehreren Korpus-Ordnern): nur einmal geladen
    public int SkippedStub { get; set; }                  // Alias-Verweise / leere Hüllen
    public int Unreadable { get; set; }
    public int DocOldFormat { get; set; }                 // .doc (altes Word-Binärformat) ohne docx/pdf: Text nicht lesbar
    public int NoText { get; set; }
    public int Converted { get; set; }                    // Dokumente, die per LibreOffice konvertiert wurden (Layout/Kommentare/Text)
    public int OtherFiles { get; set; }                   // Bilder (png/jpg ...), z. B. Hintergrund-Screenshots: als category Bild geladen
    public Dictionary<string, int> IgnoredByExt { get; set; } = new Dictionary<string, int>();   // bewusst nicht gelesen (xlsx, ods, eml, txt, cfg, zgn, flbk ...)
    public int AsDocument { get; set; }                   // Texte über maxChars (z. B. QM-Dokumentation): als category "Dokument" geladen (auf MaxDocChars gekürzt)
    public List<string> AsDocumentNames { get; set; } = new List<string>();
    public Dictionary<string, int> ByFolder { get; set; } = new Dictionary<string, int>();
    public List<string> Problems { get; set; } = new List<string>();
}

/// <summary>Konvertierung über LibreOffice (headless): Word/.doc/RTF/ODT -> PDF (für die Zeilen-/Linien-Positionen) bzw. -> docx (für Kommentare, Markierungen, Felder).
/// Gesucht wird soffice über SofficePath, Umgebungsvariable SOFFICE, PATH und die üblichen Windows-Ordner.</summary>
public static class Office
{
    public static string? SofficePath;
    public static bool Enabled = true;
    static string? exe; static bool resolved;

    public static string? Find()
    {
        if (resolved) return exe; resolved = true;
        var cands = new List<string?> { SofficePath, Environment.GetEnvironmentVariable("SOFFICE") };
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(d)) { cands.Add(Path.Combine(d, "soffice.exe")); cands.Add(Path.Combine(d, "soffice")); }
        cands.Add(@"C:\Program Files\LibreOffice\program\soffice.exe"); cands.Add(@"C:\Program Files (x86)\LibreOffice\program\soffice.exe");
        exe = cands.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
        return exe;
    }
    public static bool Available => Enabled && Find() != null;

    /// <summary>fmt = "pdf" oder "docx". Ergebnis liegt in tmpDir; null bei Fehler/Zeitüberschreitung (120 s).</summary>
    public static string? Convert(string file, string fmt, string tmpDir)
    {
        var soffice = Find(); if (!Enabled || soffice == null) return null;
        try
        {
            var outDir = Path.Combine(tmpDir, fmt); Directory.CreateDirectory(outDir);
            var prof = new Uri(Path.Combine(tmpDir, "profile")).AbsoluteUri;
            var psi = new ProcessStartInfo(soffice) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-env:UserInstallation=" + prof, "--headless", "--convert-to", fmt, "--outdir", outDir, file }) psi.ArgumentList.Add(a);
            using var pr = Process.Start(psi)!;
            pr.StandardOutput.ReadToEndAsync(); pr.StandardError.ReadToEndAsync();
            if (!pr.WaitForExit(120000)) { try { pr.Kill(true); } catch { } return null; }
            var outFile = Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + "." + fmt);
            return File.Exists(outFile) ? outFile : null;
        }
        catch { return null; }
    }
}

public static class CorpusLoader
{
    static readonly string[] OrigExt = { ".pdf", ".docx", ".doc", ".rtf", ".odt" };       // Originale: Text wird gelesen, mit der .stf gepaart
    static readonly string[] ImgExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff" };
    static bool IsJunk(string f) { var n = Path.GetFileName(f).ToLowerInvariant(); return n.StartsWith("~$", StringComparison.Ordinal) || n == "thumbs.db" || n == "desktop.ini" || n == ".ds_store" || n.EndsWith(".tmp", StringComparison.Ordinal) || n.EndsWith(".lnk", StringComparison.Ordinal); }
    static readonly Regex PlaceholderRx = new Regex(@"<[A-Za-zÄÖÜäöüß_][A-Za-zÄÖÜäöüß0-9_.]{1,59}>", RegexOptions.Compiled);

    /// <summary>Platzhalter (z. B. &lt;BETRAG&gt;) aus Text und .stf.</summary>
    public static List<string> Placeholders(params string[] texts) => texts.Where(t => !string.IsNullOrEmpty(t)).SelectMany(t => PlaceholderRx.Matches(t).Cast<Match>().Select(m => m.Value)).Distinct().OrderBy(x => x).ToList();

    static readonly HashSet<string> RtfSkip = new HashSet<string> { "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "themedata", "datastore", "listtable", "listoverridetable", "rsidtbl", "generator", "latentstyles", "colorschememapping", "header", "footer", "headerl", "headerr", "footerl", "footerr", "pgdsc", "xmlnstbl", "mmathPr", "wgrffmtfilter", "fldinst", "bkmkstart", "bkmkend" };

    /// <summary>Klartext aus RTF: Schriften-/Farb-/Formattabellen, eingebettete Bilder und Feldanweisungen werden übersprungen.</summary>
    public static string RtfToText(string raw)
    {
        var sb = new StringBuilder(); var skip = new Stack<bool>(); bool skipping = false; int i = 0; int uc = 1;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '{')
            {
                skip.Push(skipping); i++;
                if (i < raw.Length && raw[i] == '\\')
                {
                    int j = i + 1;
                    if (j < raw.Length && raw[j] == '*') skipping = true;
                    else { int k = j; while (k < raw.Length && char.IsLetter(raw[k])) k++; if (RtfSkip.Contains(raw.Substring(j, k - j))) skipping = true; }
                }
                continue;
            }
            if (c == '}') { if (skip.Count > 0) skipping = skip.Pop(); i++; continue; }
            if (c == '\\')
            {
                i++; if (i >= raw.Length) break;
                char n = raw[i];
                if (n == '\'') { if (i + 2 < raw.Length && !skipping) sb.Append((char)Convert.ToInt32(raw.Substring(i + 1, 2), 16)); i += 3; continue; }
                if (n == '\\' || n == '{' || n == '}') { if (!skipping) sb.Append(n); i++; continue; }
                if (!char.IsLetter(n)) { i++; continue; }
                int st = i; while (i < raw.Length && char.IsLetter(raw[i])) i++;
                var word = raw.Substring(st, i - st);
                int numSt = i; if (i < raw.Length && (raw[i] == '-' || char.IsDigit(raw[i]))) { i++; while (i < raw.Length && char.IsDigit(raw[i])) i++; }
                var num = raw.Substring(numSt, i - numSt);
                if (i < raw.Length && raw[i] == ' ') i++;
                if (skipping) continue;
                if (word == "par" || word == "line" || word == "sect" || word == "page" || word == "row") sb.Append('\n');
                else if (word == "tab" || word == "cell") sb.Append('\t');
                else if (word == "uc" && int.TryParse(num, out var u)) uc = u;
                else if (word == "u" && int.TryParse(num, out var cp)) { sb.Append((char)(cp < 0 ? cp + 65536 : cp)); i += uc; }
                continue;
            }
            if (c == '\r' || c == '\n') { i++; continue; }
            if (!skipping) sb.Append(c);
            i++;
        }
        return Regex.Replace(sb.ToString(), @"[ \t]+\n", "\n").Trim();
    }

    /// <summary>Text aus rtf und odt/ods; null = kein Text lesbar.</summary>
    public static string? OtherText(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".rtf") return RtfToText(File.ReadAllText(path, Encoding.Latin1));
            if (ext == ".odt" || ext == ".ods" || ext == ".odp")
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                var e = zip.GetEntry("content.xml"); if (e == null) return null;
                using var st = e.Open(); var x = XDocument.Load(st);
                var sb = new StringBuilder();
                foreach (var n in x.Descendants().Where(n => (n.Name.LocalName == "p" || n.Name.LocalName == "h") && n.Name.NamespaceName.EndsWith("text:1.0", StringComparison.Ordinal))) sb.Append(n.Value).Append('\n');
                return sb.ToString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Wird von CLI/API gesetzt (StfKit.Pdf): liefert das Layout einer PDF als JSON, null = nicht verfügbar.</summary>
    public static Func<string, string?>? PdfLayout;

    /// <summary>IR einer PDF als kompaktes JSON (ohne Bilddaten, nur Positionen): Textzeilen, Linien, Rechtecke, Bilder, Formularfelder.</summary>
    public static string LayoutJson(IrDocument ir)
    {
        foreach (var pg in ir.Pages) foreach (var im in pg.Images) im.Png = null;
        return JsonSerializer.Serialize(new { pages = ir.Pages });
    }

    /// <summary>Name ohne Aeneas-Exportsuffix "_<Nr>_<Ort>" (z. B. "Gebührenbescheid_3_Buchholz" -> "gebührenbescheid"), klein, Leerzeichen vereinheitlicht.
    /// Varianten mit Zusatz ("Datenblatt_4_Bochum  10 Personen") behalten ihren vollen Namen und werden nicht mit dem Original gepaart.</summary>
    public static string NormName(string baseName)
    {
        var n = FormFeatures.NiceName(baseName + ".x");
        n = Regex.Replace(n, @"_\d+_[\p{L}\-]+$", "");
        return Regex.Replace(n.ToLowerInvariant(), @"\s+", " ").Trim();
    }

    /// <summary>Alle Unterordner rekursiv. Ein Eintrag = ein .stf (Ergebnis) samt Original (PDF/Word) mit gleichem Namen im selben Hauptordner (auch in anderen Unterordnern,
    /// Exportsuffix "_3_Ort" wird ignoriert) oder ein Original ohne .stf. pdfText liefert den Text einer PDF (StfKit.Pdf), null = nicht lesbar.
    /// Texte über maxChars sind keine Formulare (z. B. QM-Dokumentation): category "Dokument", gekürzt.</summary>
    public static List<CorpusEntry> Build(IEnumerable<string> roots, Func<string, string?>? pdfText, out CorpusStats stats, int limit = 0, int maxChars = 30000, int maxDocChars = 12000)
    {
        var st = new CorpusStats(); var res = new List<CorpusEntry>(); var seenSha = new HashSet<string>();
        var tmp = Path.Combine(Path.GetTempPath(), "stfkit_lo_" + Guid.NewGuid().ToString("N"));
        if (Office.Enabled && Office.Find() == null) st.Problems.Add("LibreOffice (soffice) nicht gefunden: Layout von Word/RTF/ODT, Kommentare in RTF/ODT und Text alter .doc entfallen. Pfad mit --soffice angeben.");
        try {
        int rootIdx = -1;
        var allRoots = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => Path.GetFullPath(r).TrimEnd('\\', '/')).ToList();
        foreach (var root0 in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            rootIdx++;
            var root = Path.GetFullPath(root0);
            // Ordner, der in einem anderen angegebenen Ordner liegt (z. B. "...\\Aeneas" und "...\\Aeneas\\A - im Echtbetrieb"): wird dort schon mitgelesen, nicht doppelt
            var rt = root.TrimEnd('\\', '/');
            var outer = allRoots.FirstOrDefault(o => !o.Equals(rt, StringComparison.OrdinalIgnoreCase) && (rt.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || rt.StartsWith(o + "/", StringComparison.OrdinalIgnoreCase)));
            if (outer != null) { st.Problems.Add($"Ordner {rt} liegt in {outer}: wird dort mitgelesen, nicht doppelt"); continue; }
            string IdFor(string rel) => Uuid(rootIdx == 0 ? "" : root, rel);     // erster Korpus-Ordner: wie bisher (bestehende Punkte bleiben gültig); weitere Ordner: Ordnername im Hash, damit gleiche Pfade nicht kollidieren
            if (!Directory.Exists(root)) { st.Problems.Add("Ordner fehlt: " + root); continue; }
            List<string> files;
            try { files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception ex) { st.Problems.Add($"Ordner nicht lesbar: {root} ({ex.Message})"); continue; }
            files = files.Where(f => !IsJunk(f)).ToList();
            foreach (var f in files) { var e = Path.GetExtension(f).ToLowerInvariant(); st.FilesByExt[e] = st.FilesByExt.TryGetValue(e, out var n) ? n + 1 : 1; }

            string Rel(string f) => Path.GetRelativePath(root, f).Replace('\\', '/');
            string Top(string f) { var r = Rel(f).Split('/'); return r.Length > 1 ? r[0] : ""; }

            // Originale (PDF/Word) nach Hauptordner + normalisiertem Namen
            var origs = files.Where(f => OrigExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .GroupBy(f => Top(f) + "|" + NormName(Path.GetFileNameWithoutExtension(f))).ToDictionary(g => g.Key, g => g.ToList());
            var used = new HashSet<string>();
            var others = files.Where(f => ImgExt.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
            foreach (var f in files) { var x = Path.GetExtension(f).ToLowerInvariant(); if (x != ".stf" && !OrigExt.Contains(x) && !ImgExt.Contains(x)) st.IgnoredByExt[x] = st.IgnoredByExt.TryGetValue(x, out var ic) ? ic + 1 : 1; }

            string? OrigText(CorpusEntry en, List<string> grp)
            {
                en.Pdf = grp.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
                en.Docx = grp.FirstOrDefault(f => f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));
                en.Doc = grp.FirstOrDefault(f => f.EndsWith(".doc", StringComparison.OrdinalIgnoreCase));
                foreach (var o in grp.Where(f => f.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".odt", StringComparison.OrdinalIgnoreCase))) en.Others.Add(o);
                string? text = null;
                if (en.Pdf != null) { try { en.PdfLayout = PdfLayout?.Invoke(en.Pdf); if (en.PdfLayout != null) en.LayoutSource = "pdf"; } catch (Exception ex) { en.Warnings.Add("PDF-Layout nicht lesbar: " + ex.Message); } }

                // Word-artige Originale (docx, doc, rtf, odt): per LibreOffice -> PDF (Layout, falls kein Original-PDF) und -> docx (Kommentare, Markierungen, Felder)
                var wordLike = en.Docx ?? en.Doc ?? en.Others.FirstOrDefault();
                string? convPdf = null, docxForInfo = en.Docx, convDocx = null;
                if (wordLike != null && Office.Available)
                {
                    convPdf = Office.Convert(wordLike, "pdf", tmp);
                    if (en.Pdf == null && convPdf != null) { try { en.PdfLayout = PdfLayout?.Invoke(convPdf); if (en.PdfLayout != null) en.LayoutSource = "konvertiert"; } catch (Exception ex) { en.Warnings.Add("Layout (konvertiert) nicht lesbar: " + ex.Message); } }
                    if (docxForInfo == null) { convDocx = Office.Convert(wordLike, "docx", tmp); docxForInfo = convDocx; }
                    if (convPdf != null || convDocx != null) st.Converted++; else en.Warnings.Add("LibreOffice-Konvertierung fehlgeschlagen");
                }
                if (docxForInfo != null) { try { en.DocxInfo = JsonSerializer.Serialize(new { notes = DocxNotes.Read(docxForInfo), fields = DocxFields.Read(docxForInfo), converted = convDocx != null }); } catch (Exception ex) { en.Warnings.Add("docx-Infos nicht lesbar: " + ex.Message); } }

                if (en.Pdf != null) { try { text = pdfText?.Invoke(en.Pdf); if (!string.IsNullOrWhiteSpace(text)) en.EmbedSource = "pdf"; } catch (Exception ex) { en.Warnings.Add("PDF nicht lesbar: " + ex.Message); } }
                if (string.IsNullOrWhiteSpace(text) && en.Docx != null) { try { text = DocxText(en.Docx); if (!string.IsNullOrWhiteSpace(text)) en.EmbedSource = "docx"; } catch (Exception ex) { en.Warnings.Add("docx nicht lesbar: " + ex.Message); } }
                if (string.IsNullOrWhiteSpace(text) && convDocx != null) { try { text = DocxText(convDocx); if (!string.IsNullOrWhiteSpace(text)) en.EmbedSource = "konvertiert"; } catch { } }
                if (string.IsNullOrWhiteSpace(text)) foreach (var o in en.Others) { text = OtherText(o); if (!string.IsNullOrWhiteSpace(text)) { en.EmbedSource = Path.GetExtension(o).TrimStart('.').ToLowerInvariant(); break; } }
                if (string.IsNullOrWhiteSpace(text) && convPdf != null) { try { text = pdfText?.Invoke(convPdf); if (!string.IsNullOrWhiteSpace(text)) en.EmbedSource = "konvertiert"; } catch { } }
                return text;
            }
            string Clean(string t) => Regex.Replace(t.Replace("\r", ""), @"[ \t]+\n", "\n").Trim();
            void Count(CorpusEntry en) { var k = en.Folder.Length == 0 ? "(Wurzel)" : en.Folder; st.ByFolder[k] = st.ByFolder.TryGetValue(k, out var c) ? c + 1 : 1; }

            // 1) alle .stf (jeweils ein Eintrag)
            foreach (var f in files.Where(f => f.EndsWith(".stf", StringComparison.OrdinalIgnoreCase)))
            {
                if (limit > 0 && res.Count >= limit) break;
                st.Groups++;
                var en = new CorpusEntry { Root = root, Stf = f, RelPath = Rel(f)[..^4], Folder = Top(f), Name = FormFeatures.NiceName(f) };
                en.Id = IdFor(en.RelPath);
                string stfText;
                try
                {
                    var raw = Bzip2.MaybeDecompress(File.ReadAllBytes(f)); var sha = ToHex(SHA1.HashData(raw));
                    var feat = FormFeatures.FromStf(StfDocument.Parse(raw), f);
                    if (feat.TextBoxes == 0 || feat.IsAliasStub) { st.SkippedStub++; continue; }
                    if (!seenSha.Add(sha)) { st.DuplicateStf++; continue; }
                    en.Sha1 = sha; stfText = feat.RawText; en.Data = Encoding.UTF8.GetString(raw);
                    try { en.Kind = FormClassifier.Classify(feat).Kind; } catch { }
                }
                catch (Exception ex) { st.Unreadable++; st.Problems.Add($"{f}: {ex.Message}"); continue; }

                string? text = null;
                if (origs.TryGetValue(Top(f) + "|" + NormName(Path.GetFileNameWithoutExtension(f)), out var grp))
                {
                    text = OrigText(en, grp); foreach (var g in grp) used.Add(g);
                    if (!string.IsNullOrWhiteSpace(text) && text!.Length > maxChars) { text = null; en.Warnings.Add("Original zu groß, Text der .stf verwendet"); }
                }
                if (string.IsNullOrWhiteSpace(text)) { text = stfText; en.EmbedSource = "stf"; }
                if (string.IsNullOrWhiteSpace(text)) { st.NoText++; st.Problems.Add("kein Text: " + f); continue; }
                en.EmbedData = Clean(text!);
                st.WithStf++; if (en.Pdf == null && en.Docx == null) st.OnlyStf++;
                Count(en); res.Add(en);
            }

            // 2) Originale ohne .stf (Vorlagen, für die es noch kein Ergebnis gibt)
            foreach (var kv in origs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (limit > 0 && res.Count >= limit) break;
                if (kv.Value.Any(used.Contains)) continue;
                st.Groups++;
                var first = kv.Value[0];
                try { if (new FileInfo(first).Length < 100000000 && !seenSha.Add("o:" + ToHex(SHA1.HashData(File.ReadAllBytes(first))))) { st.DuplicateOriginal++; continue; } } catch { }
                var en = new CorpusEntry { Root = root, RelPath = Rel(first)[..^Path.GetExtension(first).Length], Folder = Top(first), Name = FormFeatures.NiceName(first) };
                en.Id = IdFor(en.RelPath);
                var text = OrigText(en, kv.Value);
                if (string.IsNullOrWhiteSpace(text))
                {
                    // Scan / altes .doc / defekte Datei: Datei und Name werden trotzdem gespeichert (category "Scan"), nur ohne Text
                    if (en.Pdf == null && en.Docx == null && en.Others.Count == 0 && en.Doc != null) st.DocOldFormat++; else st.NoText++;
                    st.Problems.Add("kein Text lesbar (Datei wird trotzdem gespeichert, category Scan): " + first);
                    en.Category = "Scan"; en.EmbedSource = "name"; text = en.Name;
                }
                else if (text!.Length > maxChars)
                {
                    st.AsDocument++; st.AsDocumentNames.Add($"{Rel(first)} ({text.Length} Zeichen)");
                    en.Category = "Dokument"; en.Warnings.Add($"lang ({text.Length} Zeichen): als Dokument geladen, auf {maxDocChars} gekürzt");
                    text = text.Substring(0, maxDocChars);
                }
                en.EmbedData = Clean(text); st.OnlyOriginal++;
                Count(en); res.Add(en);
            }

            // 3) Bilder (Hintergrund-Screenshots u. ä.): nur die Datei; xlsx/ods/eml/txt/cfg/zgn/flbk usw. werden bewusst nicht gelesen
            foreach (var f in others)
            {
                if (limit > 0 && res.Count >= limit) break;
                st.Groups++; st.OtherFiles++;
                var en = new CorpusEntry { Root = root, RelPath = Rel(f), Folder = Top(f), Name = Path.GetFileName(f) };
                en.Id = IdFor(en.RelPath); en.Others.Add(f);
                en.Category = "Bild";
                en.EmbedSource = "name"; en.EmbedData = Clean(en.Name);
                Count(en); res.Add(en);
            }
        }
        }
        finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
        stats = st;
        return res;
    }

    static string ToHex(byte[] b) => BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
    /// <summary>Inhalts-Hash unabhängig von BOM und Zeilenende: dieselbe .stf als Datei (mit BOM/CRLF) und als Export aus Firebird (ohne BOM) ergibt denselben Wert.</summary>
    public static string NormHash(string data)
    {
        if (data.Length > 0 && data[0] == '\uFEFF') data = data[1..];
        return ToHex(SHA1.HashData(Encoding.UTF8.GetBytes(data.Replace("\r\n", "\n").Replace("\r", "\n"))));
    }

    public static string Uuid(string root, string rel) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(((string.IsNullOrEmpty(root) ? "" : Path.GetFileName(root.TrimEnd('\\', '/')).ToLowerInvariant() + "/") + rel).ToLowerInvariant()))).ToString();

    /// <summary>Klartext aus einem .docx (Absätze, Tabellenzellen, Tabs).</summary>
    public static string DocxText(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        var e = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("word/document.xml fehlt");
        using var s = e.Open(); var x = XDocument.Load(s);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var sb = new StringBuilder();
        XNamespace mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
        foreach (var p in x.Descendants(w + "p").Where(q => !q.Ancestors(mc + "Fallback").Any()))
        {
            foreach (var n in p.Descendants())
            {
                if (n.Name == w + "t") sb.Append(n.Value);
                else if (n.Name == w + "tab") sb.Append('\t');
                else if (n.Name == w + "br") sb.Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}

public sealed class QdrantOptions
{
    public string Url { get; set; } = "http://192.168.1.241:6333";
    public string Collection { get; set; } = "Formularerstellung";
    public string ApiKey { get; set; } = "";
    public string OllamaUrl { get; set; } = "http://kiserver:11434";
    public string EmbedModel { get; set; } = "bge-m3:latest";
    public int Batch { get; set; } = 8;
    public int MaxEmbedChars { get; set; } = 12000;       // Text für den dichten Vektor (der BM25-Vektor und das Payload bekommen immer den vollen Text); bei Fehler wird halbiert
    public int EmbedNumCtx { get; set; } = 8192;          // Kontextlänge für Ollama-Embeddings (Standard von Ollama ist oft nur 2048 Tokens); 0 = nicht mitschicken
    public bool StoreFiles { get; set; } = true;          // Original-PDF/-Word als base64 im Payload mitspeichern (auch ohne Zugriff auf den Ordner wieder herstellbar)
    public string Category { get; set; } = "Formular";   // Suche nur in dieser Kategorie (Dokument/Scan/Bild bleiben außen vor)
    public double MinCosine { get; set; } = 0.6;          // ab dieser Ähnlichkeit (0..1, bge-m3) gilt ein Treffer als brauchbare Vorlage
    public bool UseForTemplates { get; set; } = true;     // /generate sucht die Vorlage in Qdrant (sonst nur im Ordner-Index)
    public string OcrModel { get; set; } = "qwen3.8:27b";       // Bild-Modell für gescannte Seiten (leer = kein OCR)
    public int MaxFileBytes { get; set; } = 15000000;     // größere Dateien (z. B. .flbk) werden nicht eingebettet (nur Pfad + Text)
}

public sealed class LoadResult
{
    public int Uploaded { get; set; }
    public int Failed { get; set; }
    public double AvgLen { get; set; }
    public int FilesStored { get; set; }                  // Originaldateien, die im Payload eingebettet wurden
    public List<string> FilesTooBig { get; set; } = new List<string>();
    public bool CollectionCreated { get; set; }
    public List<string> Errors { get; set; } = new List<string>();
}

/// <summary>Schreibt Korpus-Einträge in Qdrant: dichter Vektor "bge-m3" (Ollama) + "bm25" (Qdrant-Inferenz), gleiche Punktstruktur wie der n8n-Workflow "Qdrant DB".</summary>
public static class QdrantLoader
{
    static HttpClient Client(QdrantOptions o, bool qdrant)
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (qdrant && !string.IsNullOrWhiteSpace(o.ApiKey)) c.DefaultRequestHeaders.Add("api-key", o.ApiKey);
        return c;
    }

    public sealed class ExistingPoints { public HashSet<string> Ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase); public HashSet<string> Hashes = new HashSet<string>(); public HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); public int Total; }

    /// <summary>Liest alle vorhandenen Punkte der Collection (nur document_id, name und den .stf-Inhalt; keine Vektoren) und bildet daraus ID-, Inhalts-Hash- und Namensliste. Ändert nichts in Qdrant.</summary>
    public static async Task<ExistingPoints> ReadExistingAsync(QdrantOptions o, Action<string>? log = null, CancellationToken ct = default)
    {
        var ex = new ExistingPoints(); using var qd = Client(o, true);
        var url = o.Url.TrimEnd('/') + "/collections/" + Uri.EscapeDataString(o.Collection) + "/points/scroll";
        object? offset = null;
        while (true)
        {
            var body = JsonSerializer.Serialize(new { limit = 64, offset, with_payload = new { include = new[] { "document_id", "name", "data", "sha1" } }, with_vector = false });
            var r = await qd.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct);
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Qdrant scroll {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(ct)}");
            using var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            var res = j.RootElement.GetProperty("result");
            foreach (var pt in res.GetProperty("points").EnumerateArray())
            {
                ex.Total++;
                if (pt.TryGetProperty("id", out var id)) ex.Ids.Add(id.ToString());
                if (pt.TryGetProperty("payload", out var pl) && pl.ValueKind == JsonValueKind.Object)
                {
                    if (pl.TryGetProperty("document_id", out var d) && d.ValueKind == JsonValueKind.String) ex.Ids.Add(d.GetString()!);
                    if (pl.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) ex.Names.Add(n.GetString()!);
                    if (pl.TryGetProperty("data", out var da) && da.ValueKind == JsonValueKind.String && da.GetString()!.Length > 0) ex.Hashes.Add(CorpusLoader.NormHash(da.GetString()!));
                }
            }
            log?.Invoke($"{ex.Total} Punkte gelesen");
            if (!res.TryGetProperty("next_page_offset", out var next) || next.ValueKind == JsonValueKind.Null) break;
            offset = next.ValueKind == JsonValueKind.Number ? (object)next.GetInt64() : next.GetString();
        }
        return ex;
    }

    /// <summary>Teilt Einträge in "fehlt in Qdrant" und "schon vorhanden". Vorhanden = gleiche ID (gleicher relativer Pfad) ODER gleicher .stf-Inhalt (BOM/Zeilenende egal). Gleicher Name mit anderem Inhalt gilt als neue Fassung und wird geladen.</summary>
    public static (List<CorpusEntry> Missing, List<(CorpusEntry E, string Why)> Present) SplitMissing(IList<CorpusEntry> entries, ExistingPoints ex)
    {
        var missing = new List<CorpusEntry>(); var present = new List<(CorpusEntry, string)>();
        foreach (var e in entries)
        {
            if (ex.Ids.Contains(e.Id)) { present.Add((e, "gleiche ID")); continue; }
            if (!string.IsNullOrEmpty(e.Data) && ex.Hashes.Contains(CorpusLoader.NormHash(e.Data))) { present.Add((e, "gleicher Inhalt")); continue; }
            missing.Add(e);
        }
        return (missing, present);
    }

    public static async Task<LoadResult> LoadAsync(IList<CorpusEntry> entries, QdrantOptions o, Action<string>? log = null, CancellationToken ct = default)
    {
        var res = new LoadResult();
        if (entries.Count == 0) return res;
        res.AvgLen = Math.Round(entries.Average(e => (double)Regex.Matches(e.EmbedData, @"\S+").Count), 1);   // mittlere Wortzahl für BM25 (einheitlich für alle Einträge)
        using var qd = Client(o, true); using var ol = Client(o, false);
        var qurl = o.Url.TrimEnd('/') + "/collections/" + Uri.EscapeDataString(o.Collection);
        var jo = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        var batch = new List<object>(); int done = 0; long batchBytes = 0;
        string? B64(string? path)
        {
            if (!o.StoreFiles || path == null) return null;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                if (fi.Length > o.MaxFileBytes) { res.FilesTooBig.Add($"{path} ({fi.Length / 1024} KB)"); return null; }
                res.FilesStored++; return Convert.ToBase64String(File.ReadAllBytes(path));
            }
            catch { return null; }
        }
        async Task Flush()
        {
            if (batch.Count == 0) return;
            var body = JsonSerializer.Serialize(new { points = batch }, jo);
            var r = await qd.PutAsync(qurl + "/points?wait=true", new StringContent(body, Encoding.UTF8, "application/json"), ct);
            if (!r.IsSuccessStatusCode) { var msg = await r.Content.ReadAsStringAsync(ct); res.Failed += batch.Count; res.Errors.Add($"Upsert {(int)r.StatusCode}: {(msg.Length > 300 ? msg.Substring(0, 300) : msg)}"); }
            else res.Uploaded += batch.Count;
            batch.Clear(); batchBytes = 0;
        }

        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var prompt = e.EmbedData.Length > o.MaxEmbedChars ? e.EmbedData[..o.MaxEmbedChars] : e.EmbedData;
                HttpResponseMessage er;
                while (true)
                {
                    er = await ol.PostAsync(o.OllamaUrl.TrimEnd('/') + "/api/embeddings",
                        new StringContent(JsonSerializer.Serialize(new { model = o.EmbedModel, prompt, options = o.EmbedNumCtx > 0 ? new { num_ctx = o.EmbedNumCtx } : null }, jo), Encoding.UTF8, "application/json"), ct);
                    if (er.IsSuccessStatusCode || prompt.Length <= 1000) break;
                    prompt = prompt.Substring(0, prompt.Length / 2);      // zu lang für das Modell: kürzen und nochmal
                }
                er.EnsureSuccessStatusCode();
                using var ej = JsonDocument.Parse(await er.Content.ReadAsStringAsync(ct));
                var emb = ej.RootElement.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray();
                if (done == 0 && !res.CollectionCreated) res.CollectionCreated = await EnsureCollection(qd, qurl, emb.Length, ct);

                // Stand der Dateien (neuester Änderungszeitpunkt) und normalisierter Name: damit bei mehreren Varianten desselben Formulars das aktuellste gewählt werden kann
                var members = new[] { e.Stf, e.Pdf, e.Docx, e.Doc }.Concat(e.Others).Where(x => x != null && File.Exists(x)).Cast<string>().ToList();
                var modified = members.Count > 0 ? members.Max(x => File.GetLastWriteTimeUtc(x)).ToString("o") : null;
                var normName = CorpusLoader.NormName(Path.GetFileNameWithoutExtension(e.Stf ?? members.FirstOrDefault() ?? e.Name));
                var fl = new List<object>(); long fb = 0;
                foreach (var fp in new[] { e.Pdf, e.Docx, e.Doc }.Concat(e.Others).Where(x => x != null).Cast<string>())
                {
                    var b64 = B64(fp); fb += b64?.Length ?? 0;
                    long size = 0; try { size = new FileInfo(fp).Length; } catch { }
                    fl.Add(new { name = Path.GetFileName(fp), ext = Path.GetExtension(fp).TrimStart('.').ToLowerInvariant(), path = fp, size, base64 = b64 });
                }
                batchBytes += fb + e.Data.Length + 2L * e.EmbedData.Length + (e.PdfLayout?.Length ?? 0);
                batch.Add(new
                {
                    id = e.Id,
                    payload = new
                    {
                        document_id = e.Id, category = e.Category, kind = e.Kind, name = e.Name, norm_name = normName, modified, folder = e.Folder, rel_path = e.RelPath,
                        stf_path = e.Stf, embed_source = e.EmbedSource, has_stf = e.Stf != null, sha1 = e.Sha1,
                        embed_data = e.EmbedData, embed_chars = prompt.Length, embed_chars_total = e.EmbedData.Length, data = e.Data,                 // Text der Vorlage / Inhalt der .stf
                        placeholders = CorpusLoader.Placeholders(e.Data, e.EmbedData),
                        pdf_layout = e.PdfLayout, layout_source = e.LayoutSource, docx_info = e.DocxInfo,        // Position jeder Zeile/Linie/Bild bzw. Kommentare, Markierungen, Felder
                        files = fl                                               // Originaldateien (base64, bis MaxFileBytes) mit Name, Typ, Pfad, Größe
                    },
                    vector = new Dictionary<string, object>
                    {
                        ["bge-m3"] = emb,
                        ["bm25"] = new { text = e.EmbedData, model = "qdrant/bm25", options = new { avg_len = res.AvgLen } }
                    }
                });
                if (batch.Count >= Math.Max(1, o.Batch) || batchBytes > 8000000) await Flush();
                done++; if (done % 25 == 0) log?.Invoke($"{done}/{entries.Count}");
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { res.Failed++; res.Errors.Add($"{e.RelPath}: {ex.Message}"); }
        }
        await Flush();
        return res;
    }

    /// <summary>Punkte löschen: nach IDs (document_id) oder nach Filter (category / folder / rel_path). dryRun = nur zählen. Ohne jede Angabe passiert nichts.</summary>
    public static async Task<(long Matched, bool Deleted)> DeleteAsync(QdrantOptions o, string[]? ids, string? category, string? folder, string? relPath, bool dryRun, CancellationToken ct = default)
    {
        var must = new List<object>();
        if (!string.IsNullOrEmpty(category)) must.Add(new { key = "category", match = new { value = category } });
        if (!string.IsNullOrEmpty(folder)) must.Add(new { key = "folder", match = new { value = folder } });
        if (!string.IsNullOrEmpty(relPath)) must.Add(new { key = "rel_path", match = new { value = relPath } });
        bool byIds = ids is { Length: > 0 };
        if (!byIds && must.Count == 0) throw new ArgumentException("Keine Auswahl angegeben (ids, category, folder oder relPath).");
        using var qd = Client(o, true);
        var baseUrl = o.Url.TrimEnd('/') + "/collections/" + Uri.EscapeDataString(o.Collection) + "/points";
        var filter = byIds ? (object)new { must = new object[] { new { key = "document_id", match = new { any = ids } } } } : new { must };
        var cr = await qd.PostAsync(baseUrl + "/count", new StringContent(JsonSerializer.Serialize(new { filter, exact = true }), Encoding.UTF8, "application/json"), ct);
        cr.EnsureSuccessStatusCode();
        using var cj = JsonDocument.Parse(await cr.Content.ReadAsStringAsync(ct));
        long matched = cj.RootElement.GetProperty("result").GetProperty("count").GetInt64();
        if (dryRun || matched == 0) return (matched, false);
        var dr = await qd.PostAsync(baseUrl + "/delete?wait=true", new StringContent(JsonSerializer.Serialize(new { filter }), Encoding.UTF8, "application/json"), ct);
        dr.EnsureSuccessStatusCode();
        return (matched, true);
    }

    /// <summary>Sammlung anlegen, falls sie fehlt (Vektoren wie im Workflow: bge-m3 dicht + bm25 sparse). Vorhandene Sammlungen bleiben unverändert.</summary>
    static async Task<bool> EnsureCollection(HttpClient qd, string qurl, int dim, CancellationToken ct)
    {
        var g = await qd.GetAsync(qurl, ct);
        if (g.IsSuccessStatusCode) return false;
        if ((int)g.StatusCode != 404) g.EnsureSuccessStatusCode();
        var body = JsonSerializer.Serialize(new
        {
            vectors = new Dictionary<string, object> { ["bge-m3"] = new { size = dim, distance = "Cosine" } },
            sparse_vectors = new Dictionary<string, object> { ["bm25"] = new { modifier = "idf" } }
        });
        var r = await qd.PutAsync(qurl, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        r.EnsureSuccessStatusCode(); return true;
    }
}
}
