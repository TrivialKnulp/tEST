using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace StfKit.Core
{

/// <summary>Kalibrierung der Erzeugung (wird aus Korrekturen nachgeführt).</summary>
public sealed class Calibration
{
    public double Dx { get; set; } = 0.2; public double Dy { get; set; } = 0.3; public double Pad { get; set; } = 1.5;
    public double RectDx { get; set; } = 0.1; public double RectDy { get; set; } = 0.7; public double RectDw { get; set; } = -0.7; public double RectDh { get; set; } = 0.3;
    public double HMid { get; set; } = 0.3; public double VMid { get; set; } = 0.2; public double BoxDx { get; set; } = 0.1; public double BoxDy { get; set; } = 0.7;
    public int TextSamples { get; set; } public int LineSamples { get; set; } public int Corrections { get; set; }
}
public sealed class RewriteEntry { public string To { get; set; } = ""; public int Count { get; set; } = 1; public string Form { get; set; } = ""; }

public sealed record LearnReport(string Form, int GeneratedBoxes, int CorrectedBoxes, int TextPairs, int LinePairs, double? DxShift, double? DyShift,
    int RewritesAdded, int RewritesUpdated, int TextEditsLogged, Calibration CalibrationAfter, List<string> Notes, int SlotsAdded = 0, int SlotsUpdated = 0);

/// <summary>Gedächtnis aus Korrekturen: (1) Kalibrierung der Positionen, (2) Zeilen-Ersetzungen mit Platzhaltern,
/// (3) Protokoll aller Korrekturen. Dateien: calibration.json, rewrites.json, corrections.jsonl.
/// Annahme beim Lernen: die zu vergleichende Erzeugung wurde mit der aktuell gespeicherten Kalibrierung erstellt.</summary>
public sealed record PhraseEntry(string Ph, int Count, string Source);

public sealed class KnowledgeBase
{
    public string Dir { get; }
    public Calibration Cal { get; private set; } = new();
    public Dictionary<string, RewriteEntry> Rewrites { get; private set; } = new();
    public Dictionary<string, SlotDef> Slots { get; private set; } = new();
    public Dictionary<string, PhraseEntry> Phrases { get; private set; } = new();     // Markierungstext in Worten -> Aeneas-Platzhalter (aus Word-Kommentaren, Korrekturen, phrase-add)
    static readonly JsonSerializerOptions J = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    KnowledgeBase(string dir) { Dir = dir; }

    public static KnowledgeBase Load(string dir)
    {
        Directory.CreateDirectory(dir);
        var kb = new KnowledgeBase(dir);
        var cp = Path.Combine(dir, "calibration.json"); var rp = Path.Combine(dir, "rewrites.json");
        if (File.Exists(cp)) kb.Cal = JsonSerializer.Deserialize<Calibration>(File.ReadAllText(cp)) ?? new();
        var pp = Path.Combine(dir, "phrases.json"); if (File.Exists(pp)) kb.Phrases = JsonSerializer.Deserialize<Dictionary<string, PhraseEntry>>(File.ReadAllText(pp)) ?? new();
        var sp = Path.Combine(dir, "slots.json"); if (File.Exists(sp)) kb.Slots = JsonSerializer.Deserialize<Dictionary<string, SlotDef>>(File.ReadAllText(sp)) ?? new();
        if (File.Exists(rp)) kb.Rewrites = JsonSerializer.Deserialize<Dictionary<string, RewriteEntry>>(File.ReadAllText(rp)) ?? new();
        return kb;
    }

    public string? PhraseLookup(string text) => Phrases.TryGetValue(PhraseMatcher.Norm(text), out var e) ? e.Ph : null;

    /// <summary>Zuordnung merken (Quelle z. B. "Word-Kommentar", "manuell"). Gleicher Text mit anderem Platzhalter überschreibt, der Zähler zeigt die Sicherheit.</summary>
    public void PhraseLearn(string text, string placeholder, string source)
    {
        var key = PhraseMatcher.Norm(text); var ph = placeholder.Trim().Trim('<', '>'); if (key.Length < 2 || ph.Length == 0) return;
        if (Phrases.TryGetValue(key, out var e) && e.Ph == ph) Phrases[key] = e with { Count = e.Count + 1 }; else Phrases[key] = new PhraseEntry(ph, 1, source);
    }

    public void Save()
    {
        File.WriteAllText(Path.Combine(Dir, "calibration.json"), JsonSerializer.Serialize(Cal, J));
        File.WriteAllText(Path.Combine(Dir, "rewrites.json"), JsonSerializer.Serialize(Rewrites, J));
        File.WriteAllText(Path.Combine(Dir, "slots.json"), JsonSerializer.Serialize(Slots, J));
        File.WriteAllText(Path.Combine(Dir, "phrases.json"), JsonSerializer.Serialize(Phrases, J));
    }

    /// <summary>Kalibrierung für die Erzeugung aktivieren.</summary>
    public void Apply()
    {
        IrToStf.Dx = Cal.Dx; IrToStf.Dy = Cal.Dy; IrToStf.Pad = Cal.Pad;
        LineBuilder.RectDx = Cal.RectDx; LineBuilder.RectDy = Cal.RectDy; LineBuilder.RectDw = Cal.RectDw; LineBuilder.RectDh = Cal.RectDh;
        LineBuilder.HMid = Cal.HMid; LineBuilder.VMid = Cal.VMid; LineBuilder.BoxDx = Cal.BoxDx; LineBuilder.BoxDy = Cal.BoxDy;
    }

    public string? Rewrite(string generatedLine)
    {
        var k = Compare.Norm(generatedLine);
        return k.Length >= 3 && Rewrites.TryGetValue(k, out var e) ? e.To : null;
    }

    static double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); int n = s.Count; return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2; }
    static double Merge(double old, int n0, double med, int m) => (old * Math.Min(n0, 300) + (old + med) * m) / (Math.Min(n0, 300) + m);
    static bool HasPh(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, "<[^<>\n]+>");

    public LearnReport Learn(StfDocument generated, StfDocument corrected, string formName, IrDocument? ir = null)
    {
        var notes = new List<string>(); var rp = corrected.Pages(); var gp = generated.Pages(); int pages = Math.Min(rp.Count, gp.Count);
        if (rp.Count != gp.Count) notes.Add($"Seitenzahl abweichend (erzeugt {gp.Count}, korrigiert {rp.Count}) - nur die ersten {pages} Seiten verglichen");
        var dxs = new List<double>(); var dys = new List<double>(); int pairs = 0, added = 0, updated = 0, edits = 0, slotsAdded = 0, slotsUpdated = 0;
        var lineOff = new Dictionary<string, List<(double dx, double dy, double dw, double dh)>>();
        var editLog = new List<object>();

        for (int p = 0; p < pages; p++)
        {
            // ---- Texte
            var G = gp[p].Where(e => e.Type is "Text" or "String" && e.Lines.Count > 0).ToList();
            foreach (var c in rp[p].Where(e => e.Type is "Text" or "String"))
            {
                var first = c.Lines.Select(Compare.Norm).FirstOrDefault(l => l.Length >= 3);
                var cb = c.Box!.Value;
                if (first == null && !c.Lines.Any(HasPh)) continue;
                // reine Platzhalter-Box (first == null): kein Textvergleich möglich -> Slot-Pfad. Vergleiche immer ordinal (Kultur-Vergleich ignoriert Steuerzeichen).
                var cand = first == null ? new List<Elem>() : G.Where(g => { var t = Compare.Norm(g.Lines[0]); return t.Length > 0 && (t == first || t.StartsWith(first, StringComparison.Ordinal) || (first.StartsWith(t, StringComparison.Ordinal) && t.Length >= 6)); }).ToList();
                if (cand.Count == 0)
                {
                    {
                        // Platzhalter-Box ohne Gegenstück im erzeugten Text -> liegt vermutlich in einem leeren Eingabefeld des PDFs: Slot lernen
                        if (ir != null && p < ir.Pages.Count && c.Lines.Any(HasPh))
                        {
                            var slot = FieldSlots.Find(ir.Pages[p]).Where(sl => cb.X + cb.W / 2 >= sl.Widget.X - 1.5 && cb.X + cb.W / 2 <= sl.Widget.X + sl.Widget.W + 1.5 &&
                                                                              cb.Y + cb.H / 2 >= sl.Widget.Y - 1.5 && cb.Y + cb.H / 2 <= sl.Widget.Y + sl.Widget.H + 1.5)
                                .OrderBy(sl => Math.Abs(sl.Widget.X - cb.X) + Math.Abs(sl.Widget.Y - cb.Y)).FirstOrDefault();
                            if (slot != null)
                            {
                                string txt = string.Join("\n", c.Lines); var f = c.Font;
                                if (Slots.TryGetValue(slot.Key, out var ex)) { if (ex.Text == txt) ex.Count++; else if (ex.Count <= 1) { ex.Text = txt; ex.Form = formName; slotsUpdated++; } else ex.Count--; }
                                else { Slots[slot.Key] = new SlotDef { Text = txt, Size = f?.Size ?? 10, Dx = Math.Round(cb.X - slot.Widget.X, 2), Dy = Math.Round(cb.Y - slot.Widget.Y, 2), W = Math.Round(cb.W, 1), H = Math.Round(cb.H, 1), Form = formName }; slotsAdded++; }
                            }
                        }
                        continue;
                    }
                }
                var gE = cand.OrderBy(g => Math.Pow(g.Box!.Value.X - cb.X, 2) + Math.Pow(g.Box!.Value.Y - cb.Y, 2)).First();
                var gb = gE.Box!.Value; pairs++; dxs.Add(cb.X - gb.X); dys.Add(cb.Y - gb.Y);
                // Zeilenweise Vergleich bei gleicher Zeilenzahl
                if (gE.Lines.Count == c.Lines.Count)
                    for (int i = 0; i < c.Lines.Count; i++)
                    {
                        string gl = gE.Lines[i], cl = c.Lines[i];
                        if (gl == cl || string.IsNullOrWhiteSpace(gl) || string.IsNullOrWhiteSpace(cl)) continue;
                        var key = Compare.Norm(gl); if (key.Length < 3) continue;
                        if (HasPh(cl) && !HasPh(gl))
                        {
                            if (Rewrites.TryGetValue(key, out var ex)) { if (ex.To == cl) ex.Count++; else if (ex.Count <= 1) { ex.To = cl; ex.Form = formName; updated++; } else ex.Count--; }
                            else { Rewrites[key] = new RewriteEntry { To = cl, Form = formName }; added++; }
                        }
                        else if (Compare.Norm(gl) != Compare.Norm(cl)) { edits++; editLog.Add(new { from = gl, to = cl }); }   // reine Textkorrektur: nur protokolliert
                    }
            }
            // ---- Linien/Rahmen
            var GL = gp[p].Where(e => e.Type == "Line").Select(e => (K: Compare.Kind(e), B: e.Box!.Value)).ToList(); var used = new HashSet<int>();
            foreach (var cE in rp[p].Where(e => e.Type == "Line"))
            {
                var k = Compare.Kind(cE); var b = cE.Box!.Value; int best = -1; double bd = 3;
                for (int i = 0; i < GL.Count; i++)
                {
                    if (used.Contains(i) || GL[i].K != k) continue; var g = GL[i].B;
                    double d = Math.Sqrt(Math.Pow(g.X + g.W / 2 - b.X - b.W / 2, 2) + Math.Pow(g.Y + g.H / 2 - b.Y - b.H / 2, 2));
                    if (d < bd && (k == "Vert" || Math.Abs(g.W - b.W) < 4) && (k == "Horiz" || Math.Abs(g.H - b.H) < 4)) { bd = d; best = i; }
                }
                if (best < 0) continue; used.Add(best); var gg = GL[best].B;
                string key = k == "Rect" ? (b.W <= 8 && b.H <= 8 ? "Box" : "Rect") : k;
                if (!lineOff.TryGetValue(key, out var l)) lineOff[key] = l = new();
                l.Add((b.X - gg.X, b.Y - gg.Y, b.W - gg.W, b.H - gg.H));
            }
        }

        double? mdx = null, mdy = null;
        if (dxs.Count >= 5)
        {
            mdx = Median(dxs); mdy = Median(dys);
            Cal.Dx = Math.Round(Merge(Cal.Dx, Cal.TextSamples, mdx.Value, dxs.Count), 3); Cal.Dy = Math.Round(Merge(Cal.Dy, Cal.TextSamples, mdy.Value, dys.Count), 3);
            Cal.TextSamples += dxs.Count;
        }
        else notes.Add($"Nur {dxs.Count} Textpaare gefunden - Kalibrierung der Texte nicht verändert (mind. 5 nötig)");
        int lp = 0;
        foreach (var (key, v) in lineOff)
        {
            if (v.Count < 3) { notes.Add($"{key}: nur {v.Count} Paare - übersprungen"); continue; }
            lp += v.Count; int n0 = Cal.LineSamples;
            double m(Func<(double dx, double dy, double dw, double dh), double> f) => Median(v.Select(f).ToList());
            switch (key)
            {
                case "Rect": Cal.RectDx = Math.Round(Merge(Cal.RectDx, n0, m(x => x.dx), v.Count), 3); Cal.RectDy = Math.Round(Merge(Cal.RectDy, n0, m(x => x.dy), v.Count), 3);
                             Cal.RectDw = Math.Round(Merge(Cal.RectDw, n0, m(x => x.dw), v.Count), 3); Cal.RectDh = Math.Round(Merge(Cal.RectDh, n0, m(x => x.dh), v.Count), 3); break;
                case "Box": Cal.BoxDx = Math.Round(Merge(Cal.BoxDx, n0, m(x => x.dx), v.Count), 3); Cal.BoxDy = Math.Round(Merge(Cal.BoxDy, n0, m(x => x.dy), v.Count), 3); break;
                case "Horiz": Cal.HMid = Math.Round(Merge(Cal.HMid, n0, m(x => x.dy + x.dh / 2), v.Count), 3); break;
                case "Vert": Cal.VMid = Math.Round(Merge(Cal.VMid, n0, m(x => x.dx + x.dw / 2), v.Count), 3); break;
            }
        }
        Cal.LineSamples += lp; Cal.Corrections++;
        Save();
        File.AppendAllText(Path.Combine(Dir, "corrections.jsonl"), JsonSerializer.Serialize(new
        {
            at = DateTime.Now.ToString("s"), form = formName, generatedBoxes = generated.Items.Count(e => e.Type == "Text"), correctedBoxes = corrected.Items.Count(e => e.Type == "Text"),
            textPairs = pairs, linePairs = lp, dx = mdx, dy = mdy, rewritesAdded = added, rewritesUpdated = updated, textEdits = editLog.Take(50), slotsAdded, slotsUpdated
        }) + "\n");
        return new LearnReport(formName, generated.Items.Count(e => e.Type == "Text"), corrected.Items.Count(e => e.Type == "Text"), pairs, lp, mdx, mdy, added, updated, edits, Cal, notes, slotsAdded, slotsUpdated);
    }
}
}