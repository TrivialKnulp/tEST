using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StfKit.Core
{

public sealed class IrPos { public double X { get; set; } public double Y { get; set; } public double W { get; set; } public double H { get; set; } }

/// <summary>Ein erkanntes Element des Kundendokuments im Format des Lastenhefts (12.2): document_id, page, element_type, text, position, confidence, source_region, field_type, placeholder_mapping, validation_errors.</summary>
public sealed class IrElement
{
    public string document_id { get; set; } = "";
    public int page { get; set; }
    public string element_type { get; set; } = "";        // text | line | rect | image | widget
    public string text { get; set; } = "";
    public IrPos position { get; set; } = new();           // mm, Seitenursprung links oben
    public double confidence { get; set; }
    public string source_region { get; set; } = "";        // pdf-text | ocr | pdf-vector | pdf-image | acroform | word-highlight
    public string field_type { get; set; } = "";           // "" | placeholder | checkbox | input | highlighted-input
    public Dictionary<string, string> placeholder_mapping { get; set; } = new();   // Text im Dokument -> Platzhalter
    public List<string> validation_errors { get; set; } = new();
}

public static class IrElements
{
    static readonly Regex PhRx = new("<([^<>\\r\\n]+)>", RegexOptions.Compiled);
    static readonly UTF8Encoding Utf8 = new(false);

    /// <param name="textMap">Zuordnung Markierungstext -> Platzhalter (Kommentare + automatische Regeln), wie sie die Erzeugung benutzt.</param>
    public static List<IrElement> Build(IrDocument ir, string documentId, IDictionary<string, string>? textMap = null)
    {
        var res = new List<IrElement>(); var hl = ir.Notes.Highlights.Select(h => h.Text.Trim()).Where(t => t.Length > 0).ToList();
        for (int p = 0; p < ir.Pages.Count; p++)
        {
            var pg = ir.Pages[p];
            foreach (var l in IrToStf.JoinSplitPlaceholders(pg.Lines, out _))      // abgerissene Platzhalter ("<DEBITORKEN" + "NUNG>") wie bei der Erzeugung zusammenführen
            {
                var e = New(documentId, p + 1, "text", l.Text, l.X, l.Y, l.W, l.H, ir.Ocr ? 0.6 : 0.99, ir.Ocr ? "ocr" : "pdf-text");
                foreach (Match m in PhRx.Matches(l.Text)) e.placeholder_mapping[m.Value] = m.Value;
                if (e.placeholder_mapping.Count > 0) e.field_type = "placeholder";
                if (textMap != null)
                    foreach (var kv in textMap)
                        if (kv.Key.Length > 0 && l.Text.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)) { e.placeholder_mapping[kv.Key] = "<" + kv.Value + ">"; e.field_type = "highlighted-input"; e.confidence = Math.Min(e.confidence, 0.8); }
                if (e.field_type == "" && hl.Any(t => t.Length > 2 && l.Text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                { e.field_type = "highlighted-input"; e.confidence = Math.Min(e.confidence, 0.5); e.validation_errors.Add("Markierung ohne Platzhalter: Mensch entscheidet"); }
                res.Add(e);
            }
            foreach (var r in pg.Rules) res.Add(New(documentId, p + 1, "line", "", r.X, r.Y, r.W, r.H, 0.95, "pdf-vector"));
            foreach (var r in pg.Rects) res.Add(New(documentId, p + 1, "rect", "", r.X, r.Y, r.W, r.H, 0.95, "pdf-vector"));
            foreach (var i in pg.Images) res.Add(New(documentId, p + 1, "image", "", i.X, i.Y, i.W, i.H, i.Background ? 0.9 : 0.95, "pdf-image"));
            foreach (var w in pg.Widgets)
            {
                var e = New(documentId, p + 1, "widget", w.Name, w.X, w.Y, w.W, w.H, 0.95, "acroform");
                e.field_type = w.Kind.Contains("check", StringComparison.OrdinalIgnoreCase) ? "checkbox" : "input"; res.Add(e);
            }
        }
        return res;
    }

    static IrElement New(string id, int page, string type, string text, double x, double y, double w, double h, double conf, string src) =>
        new() { document_id = id, page = page, element_type = type, text = text, position = new IrPos { X = Math.Round(x, 2), Y = Math.Round(y, 2), W = Math.Round(w, 2), H = Math.Round(h, 2) }, confidence = conf, source_region = src };

    /// <summary>Prüft jedes Element gegen die Regeln des Schemas (Pflichtfelder, Wertebereiche, Position auf der Seite, Platzhalter-Syntax, Dubletten) und trägt Fehler in validation_errors ein.
    /// Gibt alle Fehler als Liste zurück (leer = in Ordnung).</summary>
    public static List<string> Validate(List<IrElement> els, IrDocument ir)
    {
        var errs = new List<string>(); var seen = new HashSet<string>();
        foreach (var e in els)
        {
            void Err(string m) { e.validation_errors.Add(m); errs.Add($"S{e.page} {e.element_type} '{Trunc(e.text)}': {m}"); }
            if (string.IsNullOrEmpty(e.document_id)) Err("document_id fehlt");
            if (e.page < 1 || e.page > Math.Max(1, ir.Pages.Count)) Err("Seite ungültig");
            if (e.element_type is not ("text" or "line" or "rect" or "image" or "widget")) Err("element_type unbekannt");
            if (e.element_type == "text" && string.IsNullOrWhiteSpace(e.text)) Err("Text leer");
            if (e.confidence < 0 || e.confidence > 1) Err("confidence außerhalb 0..1");
            if (string.IsNullOrEmpty(e.source_region)) Err("source_region fehlt");
            if (e.page >= 1 && e.page <= ir.Pages.Count)
            {
                var pg = ir.Pages[e.page - 1]; const double tol = 1.0;
                if (e.position.W < 0 || e.position.H < 0) Err("negative Größe");
                else if (pg.W > 0 && (e.position.X < -tol || e.position.Y < -tol || e.position.X + e.position.W > pg.W + tol || e.position.Y + e.position.H > pg.H + tol)) Err("Position außerhalb der Seite");
            }
            if (e.element_type == "text")
            {
                int open = e.text.Count(c => c == '<'), close = e.text.Count(c => c == '>');
                if (open != close) Err("Platzhalter nicht geschlossen ('<' und '>' ungleich)");
                foreach (var k in e.placeholder_mapping.Values) if (!Regex.IsMatch(k, @"^[^<>]*(?:<[^<>\s][^<>]*>[^<>]*)+$")) Err("Platzhalter fehlerhaft formatiert: " + k);
                var key = $"{e.page}|{e.text}|{e.position.X:0.0}|{e.position.Y:0.0}";
                if (!seen.Add(key)) Err("doppeltes Element an gleicher Stelle");
            }
        }
        return errs;
    }

    public static void Save(List<IrElement> els, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(els, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Utf8);

    static string Trunc(string s) => s.Length <= 30 ? s : s[..29] + "…";

    /// <summary>JSON Schema (Draft 2020-12) der Elemente - zur Prüfung in n8n oder mit einem beliebigen Validator. Die Regeln aus <see cref="Validate"/> sind darin (soweit ausdrückbar) gleichwertig.</summary>
    public const string Schema = @"{
  ""$schema"": ""https://json-schema.org/draft/2020-12/schema"",
  ""title"": ""StfKit IR-Elemente"",
  ""type"": ""array"",
  ""items"": {
    ""type"": ""object"",
    ""required"": [""document_id"", ""page"", ""element_type"", ""text"", ""position"", ""confidence"", ""source_region"", ""field_type"", ""placeholder_mapping"", ""validation_errors""],
    ""properties"": {
      ""document_id"": { ""type"": ""string"", ""minLength"": 1 },
      ""page"": { ""type"": ""integer"", ""minimum"": 1 },
      ""element_type"": { ""enum"": [""text"", ""line"", ""rect"", ""image"", ""widget""] },
      ""text"": { ""type"": ""string"" },
      ""position"": { ""type"": ""object"", ""required"": [""X"", ""Y"", ""W"", ""H""], ""properties"": { ""X"": { ""type"": ""number"" }, ""Y"": { ""type"": ""number"" }, ""W"": { ""type"": ""number"", ""minimum"": 0 }, ""H"": { ""type"": ""number"", ""minimum"": 0 } } },
      ""confidence"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 1 },
      ""source_region"": { ""type"": ""string"", ""minLength"": 1 },
      ""field_type"": { ""enum"": ["""", ""placeholder"", ""checkbox"", ""input"", ""highlighted-input""] },
      ""placeholder_mapping"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""string"", ""pattern"": ""^[^<>]*(<[^<>\\s][^<>]*>[^<>]*)+$"" } },
      ""validation_errors"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
    }
  }
}";
}
}
