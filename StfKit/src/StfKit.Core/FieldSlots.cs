using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace StfKit.Core
{

/// <summary>Beschreibung eines Textes, der in ein (leeres) Eingabefeld des PDFs gesetzt wird.</summary>
public sealed class SlotDef
{
    public string Text { get; set; } = "";          // Zeilen mit Platzhaltern, getrennt durch \n
    public double Size { get; set; } = 10; public double Dx { get; set; } = 0.5; public double Dy { get; set; } = 0.0;
    public double W { get; set; } public double H { get; set; }   // 0 = Maße des Feldes
    public int Count { get; set; } = 1; public string Form { get; set; } = "";
}

/// <summary>Findet zu jedem Textfeld (AcroForm-Widget) seine Beschriftung (links oder darüber).
/// Die Beschriftung ist der Schlüssel, unter dem gelernt/zugeordnet wird, welcher Platzhalter ins Feld gehört.</summary>
public static class FieldSlots
{
    public sealed record Slot(IrWidget Widget, string Label, string Key);

    public static List<Slot> Find(IrPage pg)
    {
        var res = new List<Slot>();
        foreach (var w in pg.Widgets.Where(w => w.Kind == "text" && w.W > 3 && w.H > 2 && w.H < 20 && w.X >= 8 && !(w.Name ?? "").StartsWith("SYSTEM", StringComparison.OrdinalIgnoreCase)))
        {
            IrLine? best = null; double bd = double.MaxValue;
            foreach (var l in pg.Lines)
            {
                if (string.IsNullOrWhiteSpace(l.Text) || l.Text.Length > 70) continue;
                double cy = l.Y + l.H / 2, wy = w.Y + w.H / 2;
                // links auf gleicher Höhe
                if (Math.Abs(cy - wy) <= 3.5 && l.X + l.W <= w.X + 1 && w.X - (l.X + l.W) <= 60)
                { double d = w.X - (l.X + l.W); if (d < bd) { bd = d; best = l; } }
                // darüber, horizontal überlappend
                double gap = w.Y - (l.Y + l.H);
                if (gap >= -1.5 && gap <= 7 && l.X < w.X + w.W && l.X + l.W > w.X)
                { double d = gap + 2; if (d < bd) { bd = d; best = l; } }
            }
            if (best == null) continue;
            string key = Compare.Norm(best.Text).Trim(' ', ':', '.', ',', '-', '–');
            if (key.Length >= 2) res.Add(new Slot(w, best.Text, key));
        }
        return res;
    }
}
}