using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace StfKit.Core
{

public sealed record AdaptReport(string Mode, string Template, string? ReplaceTarget, int Inherited, int Replaced, int Unchanged, int Untouched, int Added, int Conflicts, List<string> Notes);

/// <summary>Erzeugt ein Formular auf Basis eines ähnlichen, bereits vorhandenen Formulars.
/// Inherit: Geometrie/Text aus dem neuen PDF, Platzhalter-Blöcke (Adressfeld, Sachbearbeiter, Datum …) aus der Vorlage.
/// EditTemplate (Gebührenbescheid): Vorlage bleibt vollständig erhalten (Tabellen, Gruppen, Bedingungen, Platzhalter); nur statische Texte werden aktualisiert.</summary>
public static class FormAdapter
{
    static readonly Regex Fld = new("«[^»]*»", RegexOptions.Compiled);
    static readonly Regex Wrd = new(@"[a-zäöüß0-9]{2,}", RegexOptions.Compiled);

    static double Overlap((double X, double Y, double W, double H) a, (double X, double Y, double W, double H) b)
    {
        double ix = Math.Max(0, Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X)), iy = Math.Max(0, Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y));
        double min = Math.Min(a.W * a.H, b.W * b.H); return min <= 0 ? 0 : ix * iy / min;
    }
    static double Dist((double X, double Y, double W, double H) a, (double X, double Y, double W, double H) b) =>
        Math.Sqrt(Math.Pow(a.X + a.W / 2 - b.X - b.W / 2, 2) + Math.Pow(a.Y + a.H / 2 - b.Y - b.H / 2, 2));
    static double Dice(string a, string b)
    {
        var x = Wrd.Matches(a).Select(m => m.Value).ToHashSet(); var y = Wrd.Matches(b).Select(m => m.Value).ToHashSet();
        return x.Count + y.Count == 0 ? 0 : 2.0 * x.Intersect(y).Count() / (x.Count + y.Count);
    }
    static string Static(Elem e) => Compare.Norm(e.Text);
    static bool Has(Elem e) => e.Placeholders.Any();
    static bool IsMarker(Elem e) => Fld.IsMatch(e.Text) || Compare.Norm(e.Text).Length < 3;

    /// <summary>Text/String-Element in das Zielformat (Version) übernehmen; Layoutfelder der Versionen unterscheiden sich.</summary>
    public static Elem Rebuild(Elem e, string version)
    {
        var f = e.Font!; var b = e.Box!.Value;
        return StfDocument.MakeText(version, b.X, b.Y, b.W, b.H, e.Lines, f.Name, f.Size, f.Style, f.Align, e.Cond ?? "");
    }
    static Elem Clone(Elem e) => new(e.Type, e.Body.ToList());

    static StfDocument Join(StfDocument basis, List<List<Elem>> pages)
    {
        var d = new StfDocument { Header = basis.Header.ToList(), Eol = basis.Eol };
        for (int i = 0; i < pages.Count; i++) { if (i > 0) d.Items.Add(StfDocument.MakeSeite()); d.Items.AddRange(pages[i]); }
        return d;
    }

    // ---------------------------------------------------------------- Modus 1
    /// <summary>Nur 'Slot'-Blöcke werden vererbt (Adressfeld, Sachbearbeiter, Datum …): Blöcke, deren statischer Text kurz ist.
    /// Platzhalter innerhalb langer Fließtext-Absätze gehören zum konkreten Dokument und werden nicht übernommen.</summary>
    public const int MaxStaticChars = 70;

    public static (StfDocument Doc, AdaptReport Report) Inherit(StfDocument generated, StfDocument template, string templateName)
    {
        var notes = new List<string>(); int inherited = 0, conflicts = 0;
        var gp = generated.Pages(); var tp = template.Pages(); string ver = generated.Version;
        if (template.Version != ver) notes.Add($"Vorlage hat Version {template.Version}, Ergebnis {ver}: übernommene Textblöcke wurden in das Zielformat umgebaut (Sonderflags wie 'Überlauf' gehen dabei verloren)");
        for (int p = 0; p < gp.Count && p < tp.Count; p++)
        {
            var anchors = Anchors(tp[p], gp[p]);
            foreach (var t in tp[p].Where(e => e.Type is "Text" or "String" && Has(e)))
            {
                if (Compare.Norm(t.Text).Length > MaxStaticChars) continue;       // Platzhalter in langem Fließtext: dokumentspezifisch
                var tb = t.Box!.Value;
                var sh = LocalShift(tb, anchors);
                if (sh is { } s)
                {
                    tb = (tb.X + s.Dx, tb.Y + s.Dy, tb.W, tb.H);
                    notes.Add($"Seite {p + 1}: Block {string.Join(", ", t.Placeholders.Distinct())} um ({s.Dx:0.0}; {s.Dy:0.0}) mm verschoben (Lage der Beschriftungen im neuen PDF weicht von der Vorlage ab)");
                }
                var ov = gp[p].Where(g => g.Type is "Text" or "String" && Overlap(g.Box!.Value, tb) > 0.3).ToList();
                if (ov.Any(g => !IsMarker(g)))
                {
                    var real = ov.Where(g => !IsMarker(g)).ToList();
                    if (real.Any(g => Dice(Static(g), Compare.Norm(t.Text)) >= 0.5)) { foreach (var g in ov) gp[p].Remove(g); }     // gleicher Inhalt: Vorlage mit Platzhaltern gewinnt
                    else { conflicts++; notes.Add($"Seite {p + 1}: Platzhalterblock der Vorlage ({string.Join(",", t.Placeholders.Distinct())}) kollidiert mit vorhandenem Text '{Trunc(real[0].Text)}' - nicht übernommen"); continue; }
                }
                else foreach (var g in ov) gp[p].Remove(g);                       // nur Feld-Marker «…»/leer: ersetzen
                var nb = Rebuild(t, ver);
                if (sh is { } s2 && nb.Box is { } ob) nb.SetBox(ob.X + s2.Dx, ob.Y + s2.Dy, ob.W, ob.H);
                gp[p].Add(nb); inherited++;
                notes.Add($"Seite {p + 1}: übernommen aus '{templateName}': {string.Join(", ", t.Placeholders.Distinct())}");
            }
        }
        return (Join(generated, gp), new AdaptReport("inherit", templateName, null, inherited, 0, 0, 0, 0, conflicts, notes));
    }

    /// <summary>Beschriftungen (Text ohne Platzhalter), die auf der Seite in Vorlage UND neuem PDF genau einmal mit gleichem Wortlaut vorkommen: Anker für die Lage.</summary>
    static List<(double X, double Y, double Dx, double Dy)> Anchors(List<Elem> tpl, List<Elem> gen)
    {
        List<Elem> St(List<Elem> l) => l.Where(e => e.Type is "Text" or "String" && !Has(e) && !Fld.IsMatch(e.Text) && Static(e).Length >= 4 && e.Box != null).ToList();
        var T = St(tpl); var G = St(gen);
        var res = new List<(double, double, double, double)>();
        foreach (var t in T)
        {
            var key = Static(t);
            if (T.Count(x => Static(x) == key) != 1) continue;
            var gs = G.Where(g => Static(g) == key).ToList(); if (gs.Count != 1) continue;
            var a = t.Box!.Value; var b = gs[0].Box!.Value;
            res.Add((a.X, a.Y, b.X - a.X, b.Y - a.Y));
        }
        return res;
    }

    /// <summary>Lokale Verschiebung eines Vorlagenblocks: der nächste Anker (&lt;= 45 mm) gibt die Richtung vor; er gilt, wenn er sehr nah ist (&lt;= 15 mm) oder mindestens ein weiterer Anker
    /// (innerhalb 2 mm gleiche Abweichung) ihn bestätigt. Abweichungen &lt;= 1 mm werden ignoriert.</summary>
    static (double Dx, double Dy)? LocalShift((double X, double Y, double W, double H) b, List<(double X, double Y, double Dx, double Dy)> anchors)
    {
        var all = anchors.Select(a => (a, d: Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)))).Where(x => x.d <= 45).OrderBy(x => x.d).ToList();
        if (all.Count == 0) return null;
        var first = all[0];
        int support = all.Count(x => Math.Abs(x.a.Dx - first.a.Dx) <= 2.0 && Math.Abs(x.a.Dy - first.a.Dy) <= 2.0);
        if (first.d > 15 && support < 2) return null;
        var agree = all.Where(x => Math.Abs(x.a.Dx - first.a.Dx) <= 2.0 && Math.Abs(x.a.Dy - first.a.Dy) <= 2.0).Take(3).ToList();
        double Med(IEnumerable<double> v) { var l = v.OrderBy(x => x).ToList(); return l[l.Count / 2]; }
        double mx = Med(agree.Select(x => x.a.Dx)), my = Med(agree.Select(x => x.a.Dy));
        if (Math.Abs(mx) <= 1.0 && Math.Abs(my) <= 1.0) return null;
        return (Math.Abs(mx) > 1.0 ? Math.Round(mx, 1) : 0, Math.Abs(my) > 1.0 ? Math.Round(my, 1) : 0);
    }

    // ---------------------------------------------------------------- Modus 2 (Gebührenbescheid etc.)
    /// <summary>modeName: "bescheid" (Gebührenbescheid, ersetzt die Vorlage) oder "reuse" (gleiches Formular schon vorhanden: Vorlage bleibt vollständig, nur geänderte Texte werden angepasst; neues Formular).</summary>
    public static (StfDocument Doc, AdaptReport Report) EditTemplate(StfDocument generated, StfDocument template, string templateName, bool addNew = true, string modeName = "bescheid")
    {
        var notes = new List<string>(); int replaced = 0, unchanged = 0, untouched = 0, added = 0;
        var result = new StfDocument { Header = template.Header.ToList(), Eol = template.Eol, Items = template.Items.Select(Clone).ToList() };
        if (generated.Version != result.Version) notes.Add($"Ergebnis behält die Version der Vorlage ({result.Version}); angefordert war {generated.Version}");
        var gp = generated.Pages(); var tp = result.Pages();
        for (int p = 0; p < tp.Count && p < gp.Count; p++)
        {
            var G = gp[p].Where(e => e.Type is "Text" or "String" && !Fld.IsMatch(e.Text) && Compare.Norm(e.Text).Length >= 3).ToList();
            var T = tp[p].Where(e => e.Type is "Text" or "String" && e.Cond != "0").ToList();      // bedingte Blöcke dürfen aktualisiert werden (Bedingung bleibt); Cond "0" = nie gedruckt
            var usedG = new HashSet<Elem>(); var usedT = new HashSet<Elem>();
            var pairs = (from t in T from g in G where !Has(t)
                         let d = Dist(t.Box!.Value, g.Box!.Value) where d < 40
                         let sc = Dice(Static(t), Static(g)) - d / 400.0 where sc >= 0.45
                         orderby sc descending select (t, g, sc)).ToList();
            foreach (var (t, g, _) in pairs)
            {
                if (usedT.Contains(t) || usedG.Contains(g)) continue; usedT.Add(t); usedG.Add(g);
                if (Static(t) == Static(g)) { unchanged++; continue; }
                notes.Add($"Seite {p + 1}: Text geändert: '{Trunc(t.Text)}' -> '{Trunc(g.Text)}'");
                t.SetLines(g.Lines); replaced++;
            }
            // Boxen mit Platzhaltern werden NIE überschrieben (Platzhalter-Position im Satz ist nicht sicher übertragbar).
            // Es wird nur gemeldet, wenn im Bereich der Box im neuen PDF Wörter des statischen Vorlagentextes fehlen.
            foreach (var t in T.Where(Has))
            {
                var ts = Static(t); if (ts.Length < 6) { untouched++; continue; }
                var tb = t.Box!.Value;
                var region = G.Where(g => { var gb = g.Box!.Value; double cx = gb.X + gb.W / 2, cy = gb.Y + gb.H / 2; return cx >= tb.X - 5 && cx <= tb.X + tb.W + 5 && cy >= tb.Y - 5 && cy <= tb.Y + tb.H + 5; }).ToList();
                untouched++;
                if (region.Count == 0) continue;
                foreach (var g in region) usedG.Add(g);
                var gw = Wrd.Matches(string.Join(" ", region.Select(Static))).Select(m => m.Value).ToHashSet();
                var tw = Wrd.Matches(ts).Select(m => m.Value).Distinct().ToList();
                var missing = tw.Where(w => !gw.Contains(w)).ToList();
                if (missing.Count > 0 && missing.Count * 25 >= tw.Count)           // ab ca. 4 % abweichender Wörter
                {
                    var fresh = gw.Where(w => !tw.Contains(w) && !w.All(char.IsDigit)).Take(6).ToList();
                    notes.Add($"Seite {p + 1}: PRÜFEN - Wortlaut weicht ab bei '{Trunc(t.Text)}' (fehlt im neuen PDF: {string.Join(", ", missing.Take(6))}{(fresh.Count > 0 ? "; neu: " + string.Join(", ", fresh) : "")})");
                }
            }
            foreach (var t in T.Where(t => !Has(t) && !usedT.Contains(t))) { untouched++; if (Static(t).Length >= 12) notes.Add($"Seite {p + 1}: Vorlagentext im neuen PDF nicht gefunden (bleibt): '{Trunc(t.Text)}'"); }
            if (addNew)
                foreach (var g in G.Where(g => !usedG.Contains(g)))
                {
                    if (tp[p].Any(e => e.Type is not "Seite" && e.Box is { } b && Overlap(g.Box!.Value, b) > 0.3)) continue;
                    tp[p].Add(Rebuild(g, result.Version)); added++; notes.Add($"Seite {p + 1}: neuer Text aus dem PDF ergänzt: '{Trunc(g.Text)}'");
                }
        }
        if (gp.Count != tp.Count) notes.Add($"Seitenzahl: Vorlage {tp.Count}, neues PDF {gp.Count} - nur gemeinsame Seiten abgeglichen");
        var doc = Join(result, tp);
        return (doc, new AdaptReport(modeName, templateName, modeName == "bescheid" ? templateName : null, 0, replaced, unchanged, untouched, added, 0, notes));
    }

    // ---------------------------------------------------------------- Modus 3: nur Text (Word ohne PDF)
    const double PtMm = 25.4 / 72.0;
    static HashSet<string> Words(string norm) => Wrd.Matches(norm).Select(m => m.Value).ToHashSet();

    /// <summary>Word/RTF/ODT ohne PDF: es gibt nur den Text der Absätze, keine Positionen. Die Vorlage bleibt vollständig erhalten (Positionen, Linien, Bilder, Gruppen, Platzhalter);
    /// nur statische Textboxen werden mit dem Wortlaut aus dem Word aktualisiert. Boxen mit Platzhaltern werden nie überschrieben (nur Hinweis bei abweichendem Wortlaut).
    /// Absätze, für die in der Vorlage keine Box existiert, werden NICHT eingefügt (keine Position bekannt), sondern gemeldet (AdaptReport.Conflicts).
    /// paragraphs: bereits mit Platzhaltern versehen (IrToStf.TransformText).</summary>
    public static (StfDocument Doc, AdaptReport Report) TextOnly(StfDocument template, IList<string> paragraphs, string templateName)
    {
        var notes = new List<string>(); int replaced = 0, unchanged = 0, untouched = 0;
        var result = new StfDocument { Header = template.Header.ToList(), Eol = template.Eol, Items = template.Items.Select(Clone).ToList() };
        var tp = result.Pages();
        var P = paragraphs.Select(t => (Text: t, Norm: Compare.Norm(t))).Where(x => x.Norm.Length >= 3 && !(Fld.IsMatch(x.Text) && Compare.Norm(Fld.Replace(x.Text, "")).Length < 3)).ToList();
        var pw = P.Select(x => Words(x.Norm)).ToList();
        var allWords = new HashSet<string>(pw.SelectMany(x => x));
        var usedP = new HashSet<int>();

        var T = new List<(int Page, Elem E, string Stat, HashSet<string> W)>();
        for (int p = 0; p < tp.Count; p++)
            foreach (var e in tp[p].Where(e => e.Type is "Text" or "String" && e.Cond != "0"))
            {
                var st = Static(e);
                if (Has(e)) { T.Add((p, e, st, Words(st))); continue; }
                if (st.Length >= 3) T.Add((p, e, st, Words(st)));
            }

        // 1) unverändert: Text der Box steht (ganz oder als Teil eines Absatzes) im Word
        var open = new List<(int Page, Elem E, string Stat, HashSet<string> W)>();
        foreach (var t in T.Where(t => !Has(t.E)))
        {
            int hit = -1;
            for (int i = 0; i < P.Count && hit < 0; i++)
                if (P[i].Norm == t.Stat || (t.W.Count >= 3 && t.W.IsSubsetOf(pw[i]))) hit = i;
            if (hit >= 0) { usedP.Add(hit); unchanged++; } else open.Add(t);
        }

        // 2) geänderter Wortlaut: ähnlichste noch freie Absätze (eins zu eins)
        var pairs = (from t in open from i in Enumerable.Range(0, P.Count) where !usedP.Contains(i)
                     let sc = Dice(t.Stat, P[i].Norm) where sc >= 0.5 orderby sc descending select (t, i, sc)).ToList();
        var doneT = new HashSet<Elem>();
        foreach (var (t, i, sc) in pairs)
        {
            if (doneT.Contains(t.E) || usedP.Contains(i)) continue; doneT.Add(t.E); usedP.Add(i);
            var f = t.E.Font!; var b = t.E.Box!.Value;
            int cpl = (int)Math.Floor(Math.Max(b.W - 1.5, 5) / (f.Size * 0.5 * PtMm));
            var wrapped = DocxLayout.Wrap(P[i].Text, cpl);
            var first = t.E.Lines.Count > 0 ? t.E.Lines[0] : "";
            var prefix = Regex.Match(first, @"^(\[[a-z#0-9A-F]*\])+").Value;
            var lines = wrapped.Select(l => prefix.Length > 0 ? prefix + l + "[]" : l).ToList();
            if (t.E.Type == "String" && lines.Count > 1) lines = new List<string> { string.Join(" ", lines) };
            notes.Add($"Seite {t.Page + 1}: Text geändert (Ähnlichkeit {sc:0.00}): '{Trunc(t.E.Text)}' -> '{Trunc(P[i].Text)}'");
            t.E.SetLines(lines); replaced++;
            if (lines.Count * f.Size * 1.17 * PtMm > b.H + 1.5)
                notes.Add($"Seite {t.Page + 1}: PRÜFEN - neuer Text ist länger als die Box ({lines.Count} Zeilen, Höhe {b.H:0.#} mm): '{Trunc(P[i].Text)}'");
        }

        // 3) Rest der Vorlage bleibt; Boxen mit Platzhaltern: nur Hinweis bei abweichendem Wortlaut
        foreach (var t in T)
        {
            if (Has(t.E))
            {
                untouched++;
                if (t.Stat.Length < 6) continue;
                var missing = t.W.Where(w => !allWords.Contains(w)).ToList();
                if (missing.Count > 0 && missing.Count * 25 >= t.W.Count)
                    notes.Add($"Seite {t.Page + 1}: PRÜFEN - Wortlaut weicht ab bei '{Trunc(t.E.Text)}' (fehlt im Word: {string.Join(", ", missing.Take(6))})");
                continue;
            }
            if (doneT.Contains(t.E) || !open.Any(o => o.E == t.E)) continue;
            untouched++;
            if (t.Stat.Length >= 12) notes.Add($"Seite {t.Page + 1}: Vorlagentext im Word nicht gefunden (bleibt): '{Trunc(t.E.Text)}'");
        }

        // 4) Absätze aus dem Word ohne Box in der Vorlage: keine Position bekannt -> nicht eingefügt, nur gemeldet
        var unplaced = Enumerable.Range(0, P.Count).Where(i => !usedP.Contains(i) && P[i].Norm.Length >= 12 && !InPlaceholderBox(P[i].Text, tp)).ToList();
        foreach (var i in unplaced.Take(15)) notes.Add($"Absatz aus dem Word ohne Position in der Vorlage (NICHT eingefügt): '{Trunc(P[i].Text)}'");
        if (unplaced.Count > 15) notes.Add($"... und {unplaced.Count - 15} weitere Absätze ohne Position");
        notes.Insert(0, $"Nur-Text-Modus (Word ohne PDF): Positionen, Linien und Bilder stammen aus '{templateName}'; Seitenzahl und Umbrüche des Word sind unbekannt");
        return (Join(result, tp), new AdaptReport("textonly", templateName, null, 0, replaced, unchanged, untouched, 0, unplaced.Count, notes));
    }

    // Absatz, der schon als Platzhalter-Text in der Vorlage vorkommt (z. B. Anschriftenblock), gilt nicht als fehlend
    static bool InPlaceholderBox(string text, List<List<Elem>> pages)
    {
        var n = Compare.Norm(text);
        return pages.SelectMany(p => p).Any(e => e.Type is "Text" or "String" && Has(e) && Compare.Norm(e.Text).Length >= 3 && (n.Contains(Compare.Norm(e.Text)) || Compare.Norm(e.Text).Contains(n)));
    }

    static string Trunc(string s) { s = s.Replace("\n", " / "); return s.Length > 60 ? s[..60] + "…" : s; }
}

/// <summary>Rekonstruiert eine IR aus einer .stf (für Selbsttests und Vorschauen: "wie würde das gedruckte Formular als PDF aussehen").</summary>
public static class StfToIr
{
    const double Pt = 25.4 / 72.0;
    public static IrDocument Convert(StfDocument d)
    {
        var ir = new IrDocument { Source = "stf" };
        foreach (var page in d.Pages())
        {
            var pg = new IrPage { W = 210, H = 297 };
            foreach (var e in page.Where(e => e.Type is "Text" or "String"))
            {
                var f = e.Font!; var b = e.Box!.Value; double step = f.Size * 1.17 * Pt;
                for (int i = 0; i < e.Lines.Count; i++)
                {
                    string raw = e.Lines[i]; bool bold = raw.Contains("[f]");
                    string t = StfPreview.Sub(System.Text.RegularExpressions.Regex.Replace(raw, @"\[[a-z#0-9A-F]*\]", ""), new Dictionary<string, string>(), out _);
                    pg.Lines.Add(new IrLine { Text = t, X = b.X - IrToStf.Dx, Y = b.Y - IrToStf.Dy + i * step, W = Math.Max(t.Length * f.Size * 0.5 * Pt, 1), H = f.Size * Pt, Size = f.Size, Font = "Arial", Bold = bold });
                }
            }
            ir.Pages.Add(pg);
        }
        return ir;
    }
}
}