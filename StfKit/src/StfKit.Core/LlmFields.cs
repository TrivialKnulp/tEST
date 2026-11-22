using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StfKit.Core
{

public sealed class LlmField
{
    public string field_name { get; set; } = ""; public string field_type { get; set; } = "text"; public string placeholder { get; set; } = ""; public double confidence { get; set; }
    public string label { get; set; } = "";
}

/// <summary>Feldliste per Sprachmodell (Hinweis Fraunhofer): qwen bekommt den extrahierten Text (+optional Seitenbild) und liefert Feldname, Feldtyp, Platzhalter als JSON.
/// Die Antwort wird gegen das Schema geprüft (Pflichtfelder, erlaubte Typen, Platzhaltersyntax), bei Fehlern gibt es EINEN Wiederholungsversuch mit Fehlerhinweis.
/// Das Ergebnis sind nur VORSCHLÄGE (llm-felder.json); in die .stf gelangt nichts ohne bestehende Markierung/Kommentar bzw. menschliche Prüfung.</summary>
public static class LlmFields
{
    public static readonly string[] Types = { "text", "date", "select", "checkbox", "number", "amount", "address", "signature" };
    static readonly System.Text.RegularExpressions.Regex PhRx = new(@"^<[^<>\s][^<>]*>$");

    public const string Schema = @"{""type"":""object"",""required"":[""fields""],""properties"":{""fields"":{""type"":""array"",""items"":{""type"":""object"",""required"":[""field_name"",""field_type"",""placeholder"",""confidence""],""properties"":{""field_name"":{""type"":""string"",""minLength"":1},""field_type"":{""enum"":[""text"",""date"",""select"",""checkbox"",""number"",""amount"",""address"",""signature""]},""placeholder"":{""type"":""string"",""pattern"":""^<[^<>\\s][^<>]*>$""},""confidence"":{""type"":""number"",""minimum"":0,""maximum"":1},""label"":{""type"":""string""}}}}}}";

    /// <summary>Prüft/liest die Modellantwort. Gibt Fehlerliste zurück (leer = gültig).</summary>
    public static List<LlmField> Parse(string json, out List<string> errors)
    {
        errors = new(); var res = new List<LlmField>();
        json = json.Trim(); int a = json.IndexOf('{'), b = json.LastIndexOf('}'); if (a >= 0 && b > a) json = json[a..(b + 1)];
        try
        {
            using var d = JsonDocument.Parse(json);
            JsonElement arr = d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement : d.RootElement.TryGetProperty("fields", out var f) ? f : default;
            if (arr.ValueKind != JsonValueKind.Array) { errors.Add("Feld 'fields' (Array) fehlt"); return res; }
            int i = 0;
            foreach (var o in arr.EnumerateArray())
            {
                i++; var x = new LlmField();
                string S(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                x.field_name = S("field_name"); x.field_type = S("field_type").ToLowerInvariant(); x.placeholder = S("placeholder"); x.label = S("label");
                x.confidence = o.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : -1;
                if (x.field_name == "") errors.Add($"#{i}: field_name fehlt");
                if (!Types.Contains(x.field_type)) errors.Add($"#{i}: field_type '{x.field_type}' unzulässig (erlaubt: {string.Join(", ", Types)})");
                if (!PhRx.IsMatch(x.placeholder)) errors.Add($"#{i}: placeholder '{x.placeholder}' muss die Form <NAME> haben");
                if (x.confidence < 0 || x.confidence > 1) errors.Add($"#{i}: confidence außerhalb 0..1");
                res.Add(x);
            }
        }
        catch (JsonException ex) { errors.Add("kein gültiges JSON: " + ex.Message); }
        return res;
    }

    public static string Prompt(IrDocument ir, IEnumerable<string>? knownPlaceholders)
    {
        var sb = new StringBuilder("Du analysierst eine Formularvorlage für die Software AENEAS. Nenne alle Stellen, an denen später Daten eingesetzt werden (Eingabefelder, markierte Stellen, Lücken).\n");
        sb.Append("Antworte NUR mit JSON: {\"fields\":[{\"field_name\":\"...\",\"label\":\"Beschriftung im Dokument\",\"field_type\":\"text|date|select|checkbox|number|amount|address|signature\",\"placeholder\":\"<NAME>\",\"confidence\":0.0-1.0}]}\n");
        if (knownPlaceholders != null) { var k = knownPlaceholders.Take(120).ToList(); if (k.Count > 0) sb.Append("Bevorzuge diese vorhandenen AENEAS-Platzhalter, wenn sie passen: " + string.Join(", ", k) + "\n"); }
        if (ir.Notes.Highlights.Count > 0) sb.Append("Im Dokument markierte Stellen: " + string.Join(" | ", ir.Notes.Highlights.Select(h => h.Text).Take(60)) + "\n");
        sb.Append("--- Text des Dokuments ---\n");
        for (int p = 0; p < ir.Pages.Count; p++) { sb.Append($"[Seite {p + 1}]\n"); foreach (var l in ir.Pages[p].Lines.OrderBy(x => x.Y).ThenBy(x => x.X)) sb.AppendLine(l.Text); }
        if (ir.Paragraphs.Count > 0) foreach (var p in ir.Paragraphs) sb.AppendLine(p);
        return sb.Length > 24000 ? sb.ToString(0, 24000) : sb.ToString();
    }

    public static async Task<(List<LlmField> Fields, List<string> Errors)> ExtractAsync(IrDocument ir, string ollamaUrl, string model, IEnumerable<string>? known, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var msgs = new List<object> { new { role = "system", content = "Du antwortest ausschließlich mit gültigem JSON." }, new { role = "user", content = Prompt(ir, known) } };
        List<LlmField> f = new(); List<string> errs = new(); int tries = 0;
        for (; tries < 2; tries++)
        {
            var body = JsonSerializer.Serialize(new { model, messages = msgs, stream = false, format = "json", options = new { temperature = 0 } });
            using var resp = await http.PostAsync(ollamaUrl.TrimEnd('/') + "/api/chat", new StringContent(body, Encoding.UTF8, "application/json"), ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var text = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
            f = Parse(text, out errs); if (errs.Count == 0) return (f, errs);
            msgs.Add(new { role = "assistant", content = text });
            msgs.Add(new { role = "user", content = "Die Antwort ist ungültig: " + string.Join("; ", errs.Take(8)) + ". Antworte erneut NUR mit korrektem JSON nach dem Schema." });
        }
        return (f.Where(x => x.field_name != "" && Types.Contains(x.field_type) && PhRx.IsMatch(x.placeholder) && x.confidence is >= 0 and <= 1).ToList(), errs);
    }

    public static string ToJson(List<LlmField> f, List<string> errs) => JsonSerializer.Serialize(new { quelle = "LLM-Vorschlag (ungeprüft)", fields = f, validierungsfehler = errs },
        new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
}
