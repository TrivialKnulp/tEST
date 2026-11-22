using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace StfKit.Core
{

/// <summary>Ablaufsteuerung (Lastenheft: Prozess-ID, Status, Protokoll, Prüfung, Freigabe, Korrektur, Import).
/// Alles liegt im Arbeitsordner: verlauf.jsonl (Protokoll, nur anhängen), status.json (Stand je Formular), review.html (Prüfseite), freigegeben\ (nur freigegebene .stf), lernen\ (Korrekturen).</summary>
public static class Workflow
{
    public const string Processing = "Processing", ReviewRequired = "Review Required", Approved = "Approved", Rejected = "Rejected", Imported = "Imported", ImportFailed = "Import Failed", Failed = "Failed";
    public static readonly string[] Targets = { "Dateien", "Importdatei", "ZentraleDB" };

    public sealed class Item
    {
        public string Dokument { get; set; } = ""; public string Status { get; set; } = Processing; public string Notiz { get; set; } = "";
        public string Stf { get; set; } = ""; public string Vorschau { get; set; } = ""; public string Testdruck { get; set; } = ""; public string Vergleich { get; set; } = "";
        public string Typ { get; set; } = ""; public string Sicherheitsstufe { get; set; } = ""; public string Vorlage { get; set; } = "";
        public string Pruefen { get; set; } = ""; public string Layout { get; set; } = ""; public string Zeit { get; set; } = ""; public string Geprueft { get; set; } = ""; public string LetzterSchritt { get; set; } = "";
    }
    public sealed class RunFile
    {
        public string ProcessId { get; set; } = ""; public string Quelle { get; set; } = ""; public string Mandant { get; set; } = ""; public string Ziel { get; set; } = "Dateien";
        public string Gestartet { get; set; } = ""; public string Benutzer { get; set; } = ""; public List<Item> Formulare { get; set; } = new();
    }

    static readonly JsonSerializerOptions J = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    static string StatusPath(string w) => Path.Combine(w, "status.json");

    public static string NewProcessId() => "P" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4];

    public static RunFile Load(string work) => File.Exists(StatusPath(work)) ? JsonSerializer.Deserialize<RunFile>(File.ReadAllText(StatusPath(work)), J)! : new RunFile();
    public static void Save(string work, RunFile r) { Directory.CreateDirectory(work); File.WriteAllText(StatusPath(work), JsonSerializer.Serialize(r, J), new UTF8Encoding(false)); }

    /// <summary>Beginn eines Laufs. Existiert schon ein status.json (Wiederaufnahme), bleibt die Prozess-ID.</summary>
    public static RunFile Start(string work, string quelle, string? mandant, string? ziel)
    {
        var r = Load(work);
        if (r.ProcessId == "") { r.ProcessId = NewProcessId(); r.Gestartet = Now(); }
        r.Quelle = quelle; r.Mandant = mandant ?? r.Mandant; r.Benutzer = Environment.UserName;
        if (ziel != null) { if (!Targets.Contains(ziel, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("--ziel: " + string.Join(" | ", Targets)); r.Ziel = Targets.First(t => t.Equals(ziel, StringComparison.OrdinalIgnoreCase)); }
        Save(work, r); Log(work, r.ProcessId, "start", "", "", $"Quelle={quelle}; Mandant={r.Mandant}; Ziel={r.Ziel}");
        return r;
    }

    public static void Log(string work, string pid, string step, string doc, string status, string msg)
    {
        Directory.CreateDirectory(work);
        var o = new Dictionary<string, string> { ["zeit"] = Now(), ["prozess"] = pid, ["schritt"] = step, ["dokument"] = doc, ["status"] = status, ["meldung"] = msg, ["benutzer"] = Environment.UserName };
        File.AppendAllText(Path.Combine(work, "verlauf.jsonl"), JsonSerializer.Serialize(o, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", new UTF8Encoding(false));
    }

    public static Item Upsert(RunFile r, string doc)
    {
        var it = r.Formulare.FirstOrDefault(f => f.Dokument == doc);
        if (it == null) { it = new Item { Dokument = doc }; r.Formulare.Add(it); }
        return it;
    }

    /// <summary>Schritt beendet: Zwischenstand sofort sichern (so kann --resume nach einem Abbruch dort weitermachen).</summary>
    public static void Step(string work, RunFile r, Item it, string step, string status, string msg = "")
    {
        it.Status = status; it.LetzterSchritt = step; it.Zeit = Now(); if (msg != "") it.Notiz = msg;
        Save(work, r); Log(work, r.ProcessId, step, it.Dokument, status, msg);
    }

    /// <summary>Wiederaufnahme: fertige Formulare (Review Required/Approved/Imported...) werden übersprungen; Failed/Processing/Import Failed werden erneut bearbeitet.</summary>
    public static bool NeedsWork(RunFile r, string doc) { var it = r.Formulare.FirstOrDefault(f => f.Dokument == doc); return it == null || it.Status is Processing or Failed or ImportFailed; }

    static Item Find(RunFile r, string doc) => r.Formulare.FirstOrDefault(f => f.Dokument.Equals(doc, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Formular nicht im Lauf: " + doc);

    public static string Approve(string work, string doc, string who, string? note)
    {
        var r = Load(work); var it = Find(r, doc);
        if (it.Status is Imported) throw new InvalidOperationException("bereits importiert");
        it.Geprueft = who; Step(work, r, it, "freigabe", Approved, note ?? "");
        Directory.CreateDirectory(Path.Combine(work, "freigegeben"));
        if (File.Exists(it.Stf)) File.Copy(it.Stf, Path.Combine(work, "freigegeben", Path.GetFileName(it.Stf)), true);
        return it.Stf;
    }

    public static void Reject(string work, string doc, string who, string reason)
    {
        var r = Load(work); var it = Find(r, doc); it.Geprueft = who; Step(work, r, it, "ablehnung", Rejected, reason);
        var f = Path.Combine(work, "freigegeben", Path.GetFileName(it.Stf)); if (File.Exists(f)) File.Delete(f);
    }

    /// <summary>Korrigierte Fassung (vom Menschen im Designer/Editor geändert): wird freigegeben UND für das Lernen abgelegt (lernen\&lt;name&gt;\erzeugt.stf, korrigiert.stf, optional ir.json).</summary>
    public static string Correct(string work, string doc, string correctedStf, string who, string? kbDir, IrDocument? ir)
    {
        var r = Load(work); var it = Find(r, doc);
        var gen = StfDocument.Load(it.Stf); var cor = StfDocument.Load(correctedStf);
        var dir = Path.Combine(work, "lernen", Operations.SafeFileName(doc)); Directory.CreateDirectory(dir);
        File.Copy(it.Stf, Path.Combine(dir, "erzeugt.stf"), true); File.Copy(correctedStf, Path.Combine(dir, "korrigiert.stf"), true);
        string learned = "";
        if (kbDir != null) { var kb = KnowledgeBase.Load(kbDir); var res = kb.Learn(gen, cor, doc, ir); learned = JsonSerializer.Serialize(res, J); File.WriteAllText(Path.Combine(dir, "gelernt.json"), learned, new UTF8Encoding(false)); }
        File.Copy(correctedStf, it.Stf.Length > 0 ? Path.Combine(work, "freigegeben", Path.GetFileName(it.Stf)) : correctedStf, true);
        it.Geprueft = who; Step(work, r, it, "korrektur", Approved, "korrigierte Fassung freigegeben" + (kbDir != null ? "; Korrektur gelernt" : "; (kein --kb: Korrektur nur abgelegt)"));
        return dir;
    }

    /// <summary>Übergabe der freigegebenen Formulare je Ziel. Dateien: Ordner freigegeben\. Importdatei: zusätzlich importliste.csv. ZentraleDB: wie Importdatei - der eigentliche Schreibvorgang geschieht ausschließlich durch FormulareImport
    /// (Aufruf per --exec "&lt;Befehl&gt; {datei} {modus}"), niemals durch direktes SQL. Ohne --exec wird nur die Importliste erzeugt.</summary>
    public static List<string> Export(string work, string? exec, bool dryRun)
    {
        var r = Load(work); var msgs = new List<string>(); var dir = Path.Combine(work, "freigegeben"); Directory.CreateDirectory(dir);
        var csv = new StringBuilder("Name;Datei;Modus;Mandant;Ziel;ProzessId;GeprueftVon\r\n");
        foreach (var it in r.Formulare.Where(f => f.Status is Approved or ImportFailed))
        {
            var file = Path.Combine(dir, Path.GetFileName(it.Stf)); if (!File.Exists(file)) { msgs.Add($"{it.Dokument}: freigegebene Datei fehlt"); continue; }
            csv.Append($"\"{it.Dokument}\";\"{file}\";Neu;\"{r.Mandant}\";{r.Ziel};{r.ProcessId};\"{it.Geprueft}\"\r\n");
            if (r.Ziel == "ZentraleDB" && exec != null && !dryRun)
            {
                try
                {
                    var cmd = exec.Replace("{datei}", "\"" + file + "\"").Replace("{modus}", "Neu");
                    var psi = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", OperatingSystem.IsWindows() ? "/c " + cmd : "-c \"" + cmd.Replace("\"", "\\\"") + "\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    using var p = System.Diagnostics.Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(300000);
                    if (p.ExitCode == 0) { Step(work, r, it, "import", Imported, o.Trim().Length > 200 ? o.Trim()[..200] : o.Trim()); msgs.Add($"{it.Dokument}: importiert"); }
                    else { Step(work, r, it, "import", ImportFailed, "Exit " + p.ExitCode + ": " + o.Trim()); msgs.Add($"{it.Dokument}: Import fehlgeschlagen (Exit {p.ExitCode})"); }
                }
                catch (Exception ex) { Step(work, r, it, "import", ImportFailed, ex.Message); msgs.Add($"{it.Dokument}: Import fehlgeschlagen: {ex.Message}"); }
            }
            else if (r.Ziel != "ZentraleDB") { Step(work, r, it, "uebergabe", Imported, r.Ziel == "Dateien" ? "Datei bereitgestellt" : "Datei + Importliste bereitgestellt"); msgs.Add($"{it.Dokument}: bereitgestellt ({r.Ziel})"); }
            else msgs.Add($"{it.Dokument}: Importliste erzeugt{(dryRun ? " (Probelauf)" : "; ohne --exec wird nichts in die Datenbank geschrieben")}");
        }
        File.WriteAllText(Path.Combine(work, "importliste.csv"), csv.ToString(), new UTF8Encoding(true));
        return msgs;
    }

    /// <summary>Text der Mail an den Menschen "KI ist fertig" (Anforderung Haneke). n8n verschickt sie; hier nur Inhalt.</summary>
    public static string MailText(string work)
    {
        var r = Load(work); var g = r.Formulare.GroupBy(f => f.Status).Select(x => $"{x.Count()} {x.Key}");
        var sb = new StringBuilder($"StfKit Lauf {r.ProcessId} ist fertig.\nQuelle: {r.Quelle}\nMandant: {r.Mandant}   Ziel: {r.Ziel}\nStand: {string.Join(", ", g)}\n\n");
        foreach (var f in r.Formulare) sb.AppendLine($"- {f.Dokument}: {f.Status}{(f.Pruefen.Length > 0 ? " - zu prüfen: " + f.Pruefen : "")}");
        sb.AppendLine($"\nPrüfseite: {Path.Combine(work, "review.html")}");
        return sb.ToString();
    }

    static string H(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    public static string WriteReviewHtml(string work)
    {
        var r = Load(work); var sb = new StringBuilder();
        string Link(string f) => f.Length == 0 || !File.Exists(Path.Combine(work, f)) && !File.Exists(f) ? "" : $"<a href=\"{H(Path.GetFileName(f))}\">{H(Path.GetFileName(f))}</a>";
        sb.Append("<!doctype html><html lang=de><meta charset=utf-8><title>StfKit Prüfung</title><style>body{font:14px system-ui;margin:20px;color:#222}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:6px;vertical-align:top;text-align:left}th{background:#f0f0f0}.s{font-weight:600}.Approved,.Imported{color:#0a7a2f}.Rejected,.Failed,.ImportFailed{color:#b00020}.RR{color:#b36b00}</style>");
        sb.Append($"<h1>Prüfung - Lauf {H(r.ProcessId)}</h1><p>Quelle: {H(r.Quelle)} | Mandant: {H(r.Mandant)} | Ziel: {H(r.Ziel)} | gestartet {H(r.Gestartet)}</p>");
        sb.Append("<table><tr><th>Formular</th><th>Status</th><th>Typ / Sicherheit</th><th>Vorlage</th><th>Dateien</th><th>zu prüfen</th><th>Befehl</th></tr>");
        foreach (var f in r.Formulare)
        {
            string cls = f.Status == ReviewRequired ? "RR" : f.Status.Replace(" ", "");
            var d = $"StfKit.Cli review approve \"{work}\" \"{f.Dokument}\"";
            sb.Append($"<tr><td>{H(f.Dokument)}</td><td class='s {cls}'>{H(f.Status)}<br><small>{H(f.Geprueft)} {H(f.Zeit)}</small></td><td>{H(f.Typ)}<br>{H(f.Sicherheitsstufe)}</td><td>{H(f.Vorlage)}</td>");
            sb.Append($"<td>{Link(f.Stf)}<br>{Link(f.Vorschau)}<br>{Link(f.Testdruck)}<br>{Link(f.Vergleich)}</td><td>{H(f.Pruefen).Replace(" | ", "<br>")}<br><small>{H(f.Layout)}</small></td>");
            sb.Append($"<td><code>{H(d)}</code><br><code>… reject … \"Grund\"</code><br><code>… correct … korrigiert.stf</code></td></tr>");
        }
        sb.Append("</table><p>Freigabe, Ablehnung und Korrektur laufen über <code>StfKit.Cli review</code> (oder n8n). Nur freigegebene Formulare werden übergeben.</p></html>");
        var p = Path.Combine(work, "review.html"); File.WriteAllText(p, sb.ToString(), new UTF8Encoding(false)); return p;
    }
}
}
