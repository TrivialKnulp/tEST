using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace StfKit.Core
{

/// <summary>Eine Formularquelle. Kind "Firebird-Export" = Ordner, den StfKit.DbInspect aus FORMULARE erzeugt hat (mit export.csv); "Verzeichnis" = normaler Ordner.
/// Priority: kleiner = vertrauenswürdiger bei gleichem Inhalt/als Aktualitätshinweis. Die Rolle der Ordner ist Konfiguration, kein Programmwissen.</summary>
public sealed class CatalogSource
{
    public string Name { get; set; } = "";            // z. B. Firebird / Echtbetrieb / AENEAS
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "Verzeichnis"; // Firebird-Export | Verzeichnis
    public int Priority { get; set; } = 10;
    public List<string>? Extensions { get; set; }     // nur für diese Quelle (überschreibt die globale Liste), z. B. [".stf"] für den großen Ordner mit Kundenunterlagen
    public bool CurrentHint { get; set; }             // Quelle gilt als Hinweis auf den aktuellen Stand (z. B. "A - im Echtbetrieb")
}

public sealed class CatalogConfig
{
    public double ContentLinkThreshold { get; set; } = 0.95;     // 0 = aus
    public List<CatalogSource> Sources { get; set; } = new();
    /// <summary>Nur diese Formate werden erfasst (Formulare). Alles andere (PDF, MSG, Bilder ... = meist Kundenunterlagen mit personenbezogenen Daten) wird nur gezählt, nie gelesen.</summary>
    public List<string> Extensions { get; set; } = new() { ".stf", ".docx" };
    /// <summary>Ordnernamen (ein Pfadteil genügt), die nie betreten werden, z. B. Kundenunterlagen.</summary>
    public List<string> ExcludeDirs { get; set; } = new();
    public static CatalogConfig Load(string path) => JsonSerializer.Deserialize<CatalogConfig>(File.ReadAllText(path), Catalog.Json)!;
}

public sealed class CatalogEntry
{
    public string DocumentId { get; set; } = "";
    public string FormularId { get; set; } = "";       // gleiche Id = dasselbe Formular (Name + Mandant) in verschiedenen Formaten/Quellen/Versionen
    public string Name { get; set; } = "";
    public string BaseName { get; set; } = "";
    public string Mandant { get; set; } = "";
    public string Folder { get; set; } = "";           // erster Ordner unter der Quelle (bei Verzeichnissen oft der Kunde)
    public int? Version { get; set; }
    public string Source { get; set; } = "";
    public string Format { get; set; } = "";           // STF | DOCX
    public string Root { get; set; } = "";
    public string RelPath { get; set; } = "";
    public string FullPath => System.IO.Path.Combine(Root, RelPath);
    public long? FirebirdId { get; set; }
    public string? Art { get; set; }
    public DateTime? Modified { get; set; }            // bei Firebird-Quelle bewusst leer (die Tabelle hat kein Änderungsdatum)
    public long Size { get; set; }
    public string Hash { get; set; } = "";
    public string Status { get; set; } = "Ungeprüft"; // Aktuell | Historisch | Ersetzt | Ungeprüft | Unklar
    public string StatusNote { get; set; } = "";
    public string? DuplicateOf { get; set; }           // DocumentId der bevorzugten Kopie mit gleichem Inhalt
    public string? Error { get; set; }
}

public sealed class CatalogFile
{
    public DateTime Created { get; set; }
    public CatalogConfig Config { get; set; } = new();
    public List<CatalogEntry> Entries { get; set; } = new();
    public Dictionary<string, int> Skipped { get; set; } = new();   // Endung -> Anzahl (nicht gelesene Dateien)
    public static CatalogFile Load(string path) => JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(path), Catalog.Json)!;
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Catalog.Json), new UTF8Encoding(false));

    Dictionary<string, CatalogEntry>? byPath;
    public CatalogEntry? FindByPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        byPath ??= Entries.GroupBy(e => System.IO.Path.GetFullPath(e.FullPath), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return byPath.TryGetValue(System.IO.Path.GetFullPath(path), out var e) ? e : null;
    }
    /// <summary>Die als aktuell eingestufte Fassung desselben Formulars (gleiche FormularId), falls vorhanden.</summary>
    public CatalogEntry? CurrentOf(CatalogEntry e) => Entries.Where(x => x.FormularId == e.FormularId && x.Status == "Aktuell" && x.DuplicateOf == null).OrderBy(x => x.Format == "STF" ? 0 : 1).FirstOrDefault();
    /// <summary>Dateien für den Ähnlichkeitsindex: je Inhalt eine Kopie, nur STF; Historisches bleibt drin (ein alter Stand kann das beste Vorbild sein).</summary>
    public List<string> IndexFiles() => Entries.Where(e => e.Format == "STF" && e.DuplicateOf == null && e.Error == null).Select(e => e.FullPath).Where(File.Exists).ToList();
}

public sealed class CatalogReport
{
    public int Found, New, Changed, Unchanged, Removed, Unreadable, Duplicates, Conflicts, Unclear, ContentLinks;
    public Dictionary<string, int> BySource = new(), ByStatus = new();
    public List<string> Notes = new();
}

public static class Catalog
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    static readonly Regex NameVerMand = new(@"^(?<base>.+?)_(?<ver>\d{1,2})_(?<mand>.+)$", RegexOptions.Compiled);
    static readonly Regex NameMand = new(@"^(?<base>.+)_(?<mand>[^_]+)$", RegexOptions.Compiled);

    static string Sha(byte[] b) => Convert.ToHexString(SHA1.HashData(b));
    static string Short(string s, int n) => Sha(Encoding.UTF8.GetBytes(s))[..n];

    /// <summary>Inhalts-Hash: STF wird entpackt (bzip2), ohne BOM und mit einheitlichem Zeilenende gehasht - damit dieselbe Datei in DB und Ordner denselben Hash bekommt.</summary>
    public static string ContentHash(string path, string format)
    {
        var raw = File.ReadAllBytes(path);
        if (format != "STF") return Sha(raw);
        var d = Bzip2.MaybeDecompress(raw);
        int s = d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF ? 3 : 0;
        var txt = Encoding.UTF8.GetString(d, s, d.Length - s).Replace("\r\n", "\n").Replace("\r", "\n");
        return Sha(Encoding.UTF8.GetBytes(txt));
    }

    public static CatalogFile Build(CatalogConfig cfg, CatalogFile? previous, out CatalogReport rep)
    {
        rep = new CatalogReport();
        var prev = previous?.Entries.ToDictionary(e => e.Source + "|" + e.RelPath, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        var res = new CatalogFile { Created = DateTime.Now, Config = cfg };
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var src in cfg.Sources)
        {
            if (!Directory.Exists(src.Path)) { rep.Notes.Add($"Quelle '{src.Name}' nicht erreichbar: {src.Path}"); continue; }
            var exts = new HashSet<string>((src.Extensions ?? cfg.Extensions).Select(x => x.ToLowerInvariant()));
            var meta = src.Kind == "Firebird-Export" ? ReadExportCsv(src.Path) : null;
            var otherRoots = cfg.Sources.Where(o => o != src && o.Kind == "Verzeichnis").Select(o => Path.GetFullPath(o.Path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar).ToList();
            foreach (var f in SafeEnumerate(src.Path, cfg.ExcludeDirs))
            {
                if (otherRoots.Any(r => Path.GetFullPath(f).StartsWith(r, StringComparison.OrdinalIgnoreCase) && Path.GetFullPath(src.Path).Length < r.Length)) continue;   // liegt in einer eigenen, genauer beschriebenen Quelle (Unterordner)
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (!exts.Contains(ext)) { var k = string.IsNullOrEmpty(ext) ? "(ohne)" : ext; res.Skipped[k] = res.Skipped.GetValueOrDefault(k) + 1; continue; }
                if (Path.GetFileName(f).StartsWith("~$")) continue;
                var rel = Path.GetRelativePath(src.Path, f); var key = src.Name + "|" + rel; seenKeys.Add(key);
                var fi = new FileInfo(f); var fmt = ext == ".stf" ? "STF" : ext.TrimStart('.').ToUpperInvariant();
                CatalogEntry e;
                if (prev.TryGetValue(key, out var old) && old.Size == fi.Length && old.Error == null && (src.Kind == "Firebird-Export" || (old.Modified is { } m && Math.Abs((m - fi.LastWriteTime).TotalSeconds) < 2)))
                { e = old; e.Root = src.Path; rep.Unchanged++; }
                else
                {
                    e = new CatalogEntry { Source = src.Name, Format = fmt, Root = src.Path, RelPath = rel, Size = fi.Length, Name = Path.GetFileNameWithoutExtension(f) };
                    e.Modified = src.Kind == "Firebird-Export" ? null : fi.LastWriteTime;
                    try { e.Hash = ContentHash(f, fmt); } catch (Exception ex) { e.Error = ex.Message; rep.Unreadable++; }
                    if (prev.ContainsKey(key)) rep.Changed++; else rep.New++;
                }
                e.Folder = rel.Contains(Path.DirectorySeparatorChar) || rel.Contains('/') ? rel.Split('\\', '/')[0] : "";
                if (meta != null && meta.TryGetValue(rel.Replace('/', '\\'), out var mm)) { e.FirebirdId = mm.Id; e.Art = mm.Art; e.Name = string.IsNullOrEmpty(mm.Name) ? e.Name : mm.Name; }
                e.Status = "Ungeprüft"; e.StatusNote = ""; e.DuplicateOf = null;
                res.Entries.Add(e);
            }
        }
        rep.Removed = previous?.Entries.Count(e => !seenKeys.Contains(e.Source + "|" + e.RelPath)) ?? 0;
        rep.Found = res.Entries.Count;

        ParseNames(res.Entries);
        if (cfg.ContentLinkThreshold > 0) ContentLink(res, cfg, rep);
        LinkAndRate(res, cfg, rep);
        foreach (var g in res.Entries.GroupBy(e => e.Source)) rep.BySource[g.Key] = g.Count();
        foreach (var g in res.Entries.GroupBy(e => e.Status)) rep.ByStatus[g.Key] = g.Count();
        return res;
    }

    static IEnumerable<string> SafeEnumerate(string root, List<string> exclude)
    {
        var stack = new Stack<string>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop(); string[] files = Array.Empty<string>(), dirs = Array.Empty<string>();
            try { files = Directory.GetFiles(d); dirs = Directory.GetDirectories(d); } catch { }
            foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal)) yield return f;
            foreach (var s in dirs.OrderByDescending(x => x, StringComparer.Ordinal))
                if (!exclude.Any(x => string.Equals(Path.GetFileName(s), x, StringComparison.OrdinalIgnoreCase))) stack.Push(s);
        }
    }

    sealed record Meta(long Id, string Name, string Art);
    static Dictionary<string, Meta>? ReadExportCsv(string dir)
    {
        var p = Path.Combine(dir, "export.csv"); if (!File.Exists(p)) return null;
        var d = new Dictionary<string, Meta>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(p, Encoding.UTF8).Skip(1))
        {
            var c = SplitCsv(line); if (c.Count < 5) continue;
            if (long.TryParse(c[1], out var id)) d[c[0].Replace('/', '\\')] = new Meta(id, c[2], c[4]);
        }
        return d;
    }
    static List<string> SplitCsv(string line)
    {
        var r = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q) { if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else if (c == '"') q = false; else sb.Append(c); }
            else if (c == '"') q = true; else if (c == ';') { r.Add(sb.ToString()); sb.Clear(); } else sb.Append(c);
        }
        r.Add(sb.ToString()); return r;
    }

    /// <summary>Name_Nr_Mandant (so heißen die Formulare in der zentralen Datenbank): Basisname, Version und Mandant. Mandanten werden aus den eindeutigen Namen gelernt.</summary>
    static void ParseNames(List<CatalogEntry> all)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in all) { var m = NameVerMand.Match(e.Name); if (m.Success) known.Add(m.Groups["mand"].Value); }
        foreach (var e in all)
        {
            var m = NameVerMand.Match(e.Name);
            if (m.Success) { e.BaseName = m.Groups["base"].Value.Trim(); e.Version = int.Parse(m.Groups["ver"].Value); e.Mandant = m.Groups["mand"].Value; continue; }
            m = NameMand.Match(e.Name);
            if (m.Success && known.Contains(m.Groups["mand"].Value)) { e.BaseName = m.Groups["base"].Value.Trim(); e.Mandant = m.Groups["mand"].Value; continue; }
            e.BaseName = e.Name.Trim();
            if (known.Contains(e.Folder)) e.Mandant = e.Folder;               // Ordner heißt wie ein bekannter Mandant
        }
        foreach (var e in all)
        {
            e.FormularId = "F" + Short(e.BaseName.ToLowerInvariant() + "|" + e.Mandant.ToLowerInvariant(), 10);
            e.DocumentId = "D" + Short(e.Source + "|" + e.RelPath, 12);
        }
    }

    /// <summary>Inhaltsähnlichkeit über Namen hinweg: dasselbe Formular unter anderem Namen in anderer Quelle (z. B. Ordner "Aeneas" vs. Firebird). Ab ContentLinkThreshold (Wort-Jaccard der .stf-Texte)
    /// bekommen beide dieselbe FormularId und werden danach wie Fassungen desselben Formulars bewertet. Nur STF, nur verschiedene Quellen, nur verschiedener Hash.</summary>
    static void ContentLink(CatalogFile cat, CatalogConfig cfg, CatalogReport rep)
    {
        var prio = cfg.Sources.ToDictionary(s => s.Name, s => s.Priority);
        var items = new List<(CatalogEntry E, HashSet<string> T)>();
        foreach (var e in cat.Entries.Where(e => e.Format == "STF" && e.Error == null))
        {
            try { var txt = StfDocument.Parse(Bzip2.MaybeDecompress(File.ReadAllBytes(e.FullPath))).Items.Where(i => i.Type is "Text" or "String").SelectMany(i => i.Lines).Select(l => Compare.Norm(l)).Where(l => l.Length > 0);
                  var t = new HashSet<string>(string.Join(" ", txt).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2)); if (t.Count >= 12) items.Add((e, t)); } catch { }
        }
        for (int i = 0; i < items.Count; i++)
            for (int j = i + 1; j < items.Count; j++)
            {
                var a = items[i]; var b = items[j];
                if (a.E.Source == b.E.Source || a.E.FormularId == b.E.FormularId || a.E.Hash == b.E.Hash) continue;
                if (Math.Min(a.T.Count, b.T.Count) < 0.7 * Math.Max(a.T.Count, b.T.Count)) continue;
                int inter = a.T.Count(b.T.Contains); double jac = (double)inter / (a.T.Count + b.T.Count - inter);
                if (jac < cfg.ContentLinkThreshold) continue;
                var keep = prio.GetValueOrDefault(a.E.Source, 99) <= prio.GetValueOrDefault(b.E.Source, 99) ? a.E : b.E; var other = keep == a.E ? b.E : a.E;
                var old = other.FormularId; foreach (var x in cat.Entries.Where(x => x.FormularId == old)) x.FormularId = keep.FormularId;
                rep.ContentLinks++; rep.Notes.Add($"Inhaltsgleich verknüpft ({jac:0.00}): {other.Source}:{other.RelPath} ~ {keep.Source}:{keep.RelPath}");
            }
    }

    /// <summary>Gleicher Inhalt in mehreren Quellen = eine Kopie bevorzugen, die anderen verknüpfen. Aktualität je Formular-Gruppe nach nachvollziehbaren Regeln (nie nur nach Dateidatum).</summary>
    static void LinkAndRate(CatalogFile cat, CatalogConfig cfg, CatalogReport rep)
    {
        var prio = cfg.Sources.ToDictionary(s => s.Name, s => s.Priority);
        var cur = cfg.Sources.ToDictionary(s => s.Name, s => s.CurrentHint);
        var central = new HashSet<string>(cfg.Sources.Where(s => s.Kind == "Firebird-Export").Select(s => s.Name));
        // 1. Duplikate (gleicher Hash, gleiches Format)
        foreach (var g in cat.Entries.Where(e => e.Error == null && e.Hash != "").GroupBy(e => e.Format + e.Hash).Where(g => g.Count() > 1))
        {
            var keep = g.OrderBy(e => prio.GetValueOrDefault(e.Source, 99)).ThenBy(e => e.RelPath, StringComparer.Ordinal).First();
            foreach (var o in g.Where(e => e != keep)) { o.DuplicateOf = keep.DocumentId; rep.Duplicates++; }
        }
        // 2. Aktualität je Gruppe (Name + Mandant); Duplikate erben den Status ihrer bevorzugten Kopie
        foreach (var grp in cat.Entries.Where(e => e.DuplicateOf == null && e.Error == null).GroupBy(e => e.FormularId))
        {
            var stf = grp.Where(e => e.Format == "STF").ToList(); var cand = stf.Count > 0 ? stf : grp.ToList();
            foreach (var w in grp.Where(e => !cand.Contains(e))) { w.Status = "Ungeprüft"; w.StatusNote = "Word-Fassung zu vorhandenem STF (andere Darstellung desselben Formulars)"; }
            if (cand.Count == 1)
            {
                var c = cand[0];
                if (cur.GetValueOrDefault(c.Source) || central.Contains(c.Source)) { c.Status = "Aktuell"; c.StatusNote = "einzige Fassung" + (cur.GetValueOrDefault(c.Source) ? " in einer Quelle für den aktuellen Stand" : " in der zentralen Datenbank"); }
                else { c.Status = "Ungeprüft"; c.StatusNote = "einzige Fassung, aus einer Quelle ohne Aktualitätshinweis"; }
                continue;
            }
            bool allNum = cand.All(c => c.Version != null);
            var top = allNum ? cand.OrderByDescending(c => c.Version).ThenBy(c => prio.GetValueOrDefault(c.Source, 99)).First()
                             : cand.OrderBy(c => prio.GetValueOrDefault(c.Source, 99)).ThenByDescending(c => c.Modified ?? DateTime.MinValue).First();
            var hintCands = cand.Where(c => cur.GetValueOrDefault(c.Source));
            // v11: mehrere Versionen im Hinweis-Ordner -> die höchste Versionsnummer gilt (früher: zuletzt geänderte Datei, das erzeugte Scheinkonflikte)
            var hint = allNum ? hintCands.OrderByDescending(c => c.Version).ThenByDescending(c => c.Modified ?? DateTime.MinValue).FirstOrDefault() : hintCands.OrderByDescending(c => c.Modified ?? DateTime.MinValue).FirstOrDefault();
            if (hint != null && hint != top)
            {
                // Aktualitätshinweis (Ordner "Echtbetrieb") und Versionsnummer/Priorität widersprechen sich: Mensch entscheidet
                foreach (var c in new[] { hint, top }) { c.Status = "Unklar"; c.StatusNote = $"Konflikt: '{hint.Source}' ({hint.RelPath}) vs. '{top.Source}' ({top.RelPath}) - Mensch entscheidet"; }
                rep.Conflicts++;
            }
            else { top.Status = "Aktuell"; top.StatusNote = allNum ? $"höchste Versionsnummer ({top.Version}) von {cand.Count} Fassungen" : $"von {cand.Count} Fassungen aus der Quelle mit höchster Priorität ('{top.Source}'), Versionsnummern fehlen - bitte bestätigen"; }
            foreach (var c in cand.Where(c => c != top && c != hint))
            {
                if (allNum && c.Version == top.Version)
                { c.Status = "Unklar"; top.Status = "Unklar"; c.StatusNote = top.StatusNote = $"gleiche Versionsnummer {top.Version}, aber unterschiedlicher Inhalt ({top.RelPath} / {c.RelPath}) - Mensch entscheidet"; rep.Conflicts++; continue; }
                c.Status = allNum ? "Ersetzt" : "Historisch"; c.StatusNote = allNum ? $"Version {c.Version} < {top.Version}" : "andere Fassung desselben Formulars; Reihenfolge nicht eindeutig";
            }
        }
        foreach (var d in cat.Entries.Where(e => e.DuplicateOf != null))
        { var k = cat.Entries.First(e => e.DocumentId == d.DuplicateOf); d.Status = k.Status; d.StatusNote = $"identischer Inhalt wie {k.Source}: {k.RelPath}"; }
        rep.Unclear = cat.Entries.Count(e => e.Status == "Unklar");
    }

    public static void WriteReports(CatalogFile cat, CatalogReport rep, string dir)
    {
        Directory.CreateDirectory(dir);
        cat.Save(Path.Combine(dir, "catalog.json"));
        var csv = new StringBuilder("DocumentId;FormularId;Name;Basisname;Mandant;Version;Quelle;Format;Pfad;FirebirdId;Art;Geaendert;Groesse;Hash;Status;Begruendung;DuplikatVon;Fehler\r\n");
        string Q(string? v) => "\"" + (v ?? "").Replace("\"", "'") + "\"";
        foreach (var e in cat.Entries.OrderBy(x => x.BaseName).ThenBy(x => x.Mandant).ThenBy(x => x.Version))
            csv.Append(string.Join(";", e.DocumentId, e.FormularId, Q(e.Name), Q(e.BaseName), Q(e.Mandant), e.Version?.ToString() ?? "", e.Source, e.Format, Q(e.FullPath), e.FirebirdId?.ToString() ?? "", e.Art ?? "", e.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "", e.Size, e.Hash, e.Status, Q(e.StatusNote), e.DuplicateOf ?? "", Q(e.Error))).Append("\r\n");
        File.WriteAllText(Path.Combine(dir, "katalog.csv"), csv.ToString(), new UTF8Encoding(true));

        var md = new StringBuilder();
        md.AppendLine($"# Katalog-Bericht ({cat.Created:yyyy-MM-dd HH:mm})\n");
        md.AppendLine($"- Erfasste Formulare: **{rep.Found}** (neu {rep.New}, geändert {rep.Changed}, unverändert {rep.Unchanged}, seit letztem Lauf entfernt {rep.Removed})");
        md.AppendLine($"- Nicht lesbar: {rep.Unreadable}; Duplikate (gleicher Inhalt in mehreren Quellen): {rep.Duplicates}; Konflikte bei der Aktualität: {rep.Conflicts}");
        md.AppendLine("\n## Nach Quelle"); foreach (var kv in rep.BySource) md.AppendLine($"- {kv.Key}: {kv.Value}");
        md.AppendLine("\n## Nach Status"); foreach (var kv in rep.ByStatus.OrderByDescending(k => k.Value)) md.AppendLine($"- {kv.Key}: {kv.Value}");
        md.AppendLine("\n## Nicht gelesene Dateien (nur gezählt, nie geöffnet)"); foreach (var kv in cat.Skipped.OrderByDescending(k => k.Value).Take(15)) md.AppendLine($"- {kv.Key}: {kv.Value}");
        if (rep.Notes.Count > 0) { md.AppendLine("\n## Hinweise"); foreach (var n in rep.Notes) md.AppendLine("- " + n); }
        var conf = cat.Entries.Where(e => e.Status == "Unklar").OrderBy(e => e.BaseName).Take(60).ToList();
        if (conf.Count > 0) { md.AppendLine("\n## Zu prüfen (Aktualität unklar)"); foreach (var e in conf) md.AppendLine($"- {e.Name} [{e.Source}] - {e.StatusNote}"); }
        md.AppendLine("\n## Hinweis\nOriginale werden nie verändert oder verschoben; der Katalog verweist nur auf Pfad und Hash. Status 'Aktuell' ist eine Einstufung nach Regeln (Versionsnummer, Quelle), keine Freigabe.");
        File.WriteAllText(Path.Combine(dir, "katalog-bericht.md"), md.ToString(), new UTF8Encoding(true));
    }
}
}
