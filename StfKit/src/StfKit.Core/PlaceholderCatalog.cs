using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace StfKit.Core
{

/// <summary>Häufigkeitskatalog aller &lt;Platzhalter&gt; in einem .stf-Korpus (mit Beispielkontext).</summary>
public static class PlaceholderCatalog
{
    public sealed record Entry(string Name, int Count, int Files, string Example);

    public static List<Entry> Build(string dir)
    {
        var count = new Dictionary<string, int>(); var files = new Dictionary<string, HashSet<string>>(); var ex = new Dictionary<string, string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.stf", SearchOption.AllDirectories))
        {
            StfDocument d; try { d = StfDocument.Load(f); } catch { continue; }
            foreach (var e in d.Items.Where(e => e.Type is "Text" or "String" or "Barcode"))
                foreach (var p in e.Placeholders)
                {
                    var name = p.Split('|')[0];                       // <F_Anrede|Frau|…> → F_Anrede
                    count[name] = count.GetValueOrDefault(name) + 1;
                    (files.TryGetValue(name, out var s) ? s : files[name] = new()).Add(f);
                    if (!ex.ContainsKey(name)) ex[name] = e.Text.Replace("\n", " / ")[..Math.Min(70, e.Text.Length)];
                }
        }
        return count.OrderByDescending(kv => kv.Value).Select(kv => new Entry(kv.Key, kv.Value, files[kv.Key].Count, ex[kv.Key])).ToList();
    }
}
}