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

/// <summary>Treffer der Ähnlichkeitssuche in Qdrant (Sammlung Formularerstellung).</summary>
public sealed class QdrantHit
{
    public string Id { get; set; } = "";
    public double Score { get; set; }                     // Rang-Punktzahl der hybriden Suche (RRF), nur zum Sortieren
    public double? Cosine { get; set; }                   // Ähnlichkeit des dichten Vektors (bge-m3), 0..1: darauf beruht die Schwelle MinCosine
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public string RelPath { get; set; } = "";
    public string? StfPath { get; set; }
    public bool HasStf { get; set; }
    public string Category { get; set; } = "";
    public string Kind { get; set; } = "";
    public string NormName { get; set; } = "";
    public DateTime? Modified { get; set; }
    public List<string> Placeholders { get; set; } = new List<string>();
    public string? Data { get; set; }                     // Inhalt der .stf (nur wenn angefordert)
}

/// <summary>Sucht ähnliche Formulare: Text des Kundenformulars -> Embedding (Ollama bge-m3) + BM25 -> hybride Suche (RRF) in Qdrant.</summary>
public static class QdrantSearch
{
    static readonly JsonSerializerOptions Jo = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<float[]> EmbedAsync(HttpClient ol, QdrantOptions o, string text, CancellationToken ct = default)
    {
        var prompt = text.Length > o.MaxEmbedChars ? text.Substring(0, o.MaxEmbedChars) : text;
        HttpResponseMessage er;
        while (true)
        {
            er = await ol.PostAsync(o.OllamaUrl.TrimEnd('/') + "/api/embeddings", new StringContent(JsonSerializer.Serialize(new { model = o.EmbedModel, prompt, options = o.EmbedNumCtx > 0 ? new { num_ctx = o.EmbedNumCtx } : null }, Jo), Encoding.UTF8, "application/json"), ct);
            if (er.IsSuccessStatusCode || prompt.Length <= 1000) break;
            prompt = prompt.Substring(0, prompt.Length / 2);
        }
        er.EnsureSuccessStatusCode();
        using var ej = JsonDocument.Parse(await er.Content.ReadAsStringAsync(ct));
        return ej.RootElement.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray();
    }

    static object Filter(string? category, bool onlyStf, string? kind)
    {
        var must = new List<object>();
        if (!string.IsNullOrEmpty(category)) must.Add(new { key = "category", match = new { value = category } });
        if (onlyStf) must.Add(new { key = "has_stf", match = new { value = true } });
        if (!string.IsNullOrEmpty(kind)) must.Add(new { key = "kind", match = new { value = kind } });
        return new { must };
    }

    static readonly string[] Include = { "name", "folder", "rel_path", "stf_path", "has_stf", "category", "kind", "norm_name", "modified", "placeholders" };

    /// <summary>top Treffer; onlyStf = nur Formulare, zu denen es schon eine .stf gibt (nur die taugen als Vorlage); kind = Formulartyp (z. B. "Gebührenbescheid").</summary>
    public static async Task<List<QdrantHit>> SearchAsync(string text, QdrantOptions o, int top = 8, bool onlyStf = true, string? kind = null, bool withData = false, CancellationToken ct = default)
    {
        using var qd = new HttpClient { Timeout = TimeSpan.FromMinutes(2) }; if (!string.IsNullOrWhiteSpace(o.ApiKey)) qd.DefaultRequestHeaders.Add("api-key", o.ApiKey);
        using var ol = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var emb = await EmbedAsync(ol, o, text, ct);
        var url = o.Url.TrimEnd('/') + "/collections/" + Uri.EscapeDataString(o.Collection) + "/points/query";
        var filter = Filter(o.Category, onlyStf, kind);
        var payloadSel = withData ? (object)true : new { include = Include };

        async Task<JsonElement> Query(object body)
        {
            var r = await qd.PostAsync(url, new StringContent(JsonSerializer.Serialize(body, Jo), Encoding.UTF8, "application/json"), ct);
            var s = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Qdrant {(int)r.StatusCode}: {(s.Length > 300 ? s.Substring(0, 300) : s)}");
            return JsonDocument.Parse(s).RootElement.GetProperty("result").GetProperty("points").Clone();
        }

        // 1) hybrid (dicht + BM25, Rangfusion) für die Reihenfolge
        var hybrid = await Query(new
        {
            prefetch = new object[]
            {
                new { query = emb, @using = "bge-m3", limit = 30, filter },
                new { query = new { text, model = "qdrant/bm25" }, @using = "bm25", limit = 30, filter }
            },
            query = new { fusion = "rrf" }, limit = Math.Clamp(top, 1, 50), filter, with_payload = payloadSel
        });
        // 2) nur dicht: echte Kosinus-Ähnlichkeit (0..1) für die Schwelle
        var dense = await Query(new { query = emb, @using = "bge-m3", limit = 50, filter, with_payload = false });
        var cos = dense.EnumerateArray().ToDictionary(p => p.GetProperty("id").ToString() ?? "", p => p.GetProperty("score").GetDouble());

        var hits = new List<QdrantHit>();
        foreach (var p in hybrid.EnumerateArray())
        {
            var pl = p.TryGetProperty("payload", out var pe) ? pe : default;
            string S(string k) => pl.ValueKind == JsonValueKind.Object && pl.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var id = p.GetProperty("id").ToString() ?? "";
            var h = new QdrantHit { Id = id, Score = p.GetProperty("score").GetDouble(), Cosine = cos.TryGetValue(id, out var c) ? c : (double?)null,
                Name = S("name"), Folder = S("folder"), RelPath = S("rel_path"), StfPath = S("stf_path") is { Length: > 0 } sp ? sp : null, Category = S("category"), Kind = S("kind"), NormName = S("norm_name") };
            if (pl.ValueKind == JsonValueKind.Object)
            {
                h.HasStf = pl.TryGetProperty("has_stf", out var hs) && hs.ValueKind == JsonValueKind.True;
                if (DateTime.TryParse(S("modified"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)) h.Modified = dt;
                if (pl.TryGetProperty("placeholders", out var phs) && phs.ValueKind == JsonValueKind.Array) h.Placeholders = phs.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                if (withData) h.Data = S("data");
            }
            hits.Add(h);
        }
        return hits;
    }

    /// <summary>Bester Treffer; gibt es mehrere Varianten desselben Formulars (gleicher normalisierter Name, ähnlich nah), wird die NEUESTE genommen
    /// (in den Mandanten liegt nicht immer der aktuelle Stand). null, wenn nichts die Schwelle MinCosine erreicht.</summary>
    public static QdrantHit? PickBest(IList<QdrantHit> hits, QdrantOptions o, double tolerance = 0.05)
    {
        var best = hits.FirstOrDefault(); if (best == null) return null;
        var bc = best.Cosine ?? 0;
        var cands = hits.Where(h => h.NormName.Length > 0 && h.NormName == best.NormName && bc - (h.Cosine ?? 0) <= tolerance).ToList();
        if (cands.Count == 0) cands.Add(best);
        var pick = cands.OrderByDescending(h => h.Modified ?? DateTime.MinValue).ThenByDescending(h => h.Cosine ?? 0).First();
        return (pick.Cosine ?? 0) >= o.MinCosine ? pick : null;
    }
}
}
