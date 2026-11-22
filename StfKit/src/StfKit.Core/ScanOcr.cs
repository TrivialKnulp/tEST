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

/// <summary>Gescannte Seiten (nur ein seitenfüllendes Bild, kein Text): Text per Bild-Modell in Ollama (z. B. qwen3.8) lesen.
/// Positionen gibt es dabei nicht: der Text wird wie bei Word ohne PDF als Fließtext angelegt (Layout kommt aus einer ähnlichen Vorlage bzw. von Hand).</summary>
public static class ScanOcr
{
    public const string Prompt = "Lies den gesamten Text dieser eingescannten Seite und gib ihn unverändert als reinen Text zurück: eine Zeile pro Textzeile, " +
        "Tabellen zeilenweise mit Leerzeichen zwischen den Spalten. Nichts erklären, nichts ergänzen, keine Markdown-Formatierung. Ist die Seite gedreht, lies sie trotzdem in Leserichtung.";

    /// <summary>Seiten ohne Text, aber mit seitenfüllendem Scan-Bild.</summary>
    public static List<int> ScanPages(IrDocument ir) =>
        ir.Pages.Select((p, i) => (p, i)).Where(x => x.p.Lines.Count == 0 && x.p.Images.Any(m => m.Background && !string.IsNullOrEmpty(m.Png))).Select(x => x.i).ToList();

    public static async Task<int> ApplyAsync(IrDocument ir, string ollamaUrl, string model, CancellationToken ct = default)
    {
        var pages = ScanPages(ir); if (pages.Count == 0) return 0;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        int done = 0;
        foreach (var i in pages)
        {
            var png = ir.Pages[i].Images.First(m => m.Background && !string.IsNullOrEmpty(m.Png)).Png!;
            var body = JsonSerializer.Serialize(new { model, stream = false, options = new { temperature = 0 },
                messages = new[] { new { role = "user", content = Prompt, images = new[] { png } } } });
            var r = await http.PostAsync(ollamaUrl.TrimEnd('/') + "/api/chat", new StringContent(body, Encoding.UTF8, "application/json"), ct);
            var s = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Ollama OCR {(int)r.StatusCode}: {(s.Length > 300 ? s.Substring(0, 300) : s)}");
            using var d = JsonDocument.Parse(s);
            var text = d.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
            text = System.Text.RegularExpressions.Regex.Replace(text, "<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline).Trim();
            if (text.Length == 0) continue;
            var flow = DocxLayout.ToIr(DocxLayout.FromPlainText(text), ir.Source);
            ir.Pages[i].Lines = flow.Pages.SelectMany(p => p.Lines).ToList();          // eine Scanseite = eine Seite (bei Überlänge wird gekürzt statt neue Seite)
            done++;
        }
        if (done > 0) ir.Ocr = true;
        return done;
    }
}
}
