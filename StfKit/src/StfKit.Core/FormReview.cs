using System;
using System.Collections.Generic;
using System.Linq;

namespace StfKit.Core
{

/// <summary>Bewertung für den Bericht: ist die gefundene Vorlage brauchbar, muss ein Mensch das Ergebnis prüfen?</summary>
public static class FormReview
{
    public const string NoTemplate = "keine passende Vorlage, wird von Grund auf gebaut";
    /// <summary>Unterhalb dieser Typ-Sicherheit (oder bei Typ "Sonstiges") muss ein Mensch prüfen.</summary>
    public static double MinConfidence = 0.5;

    /// <summary>Vorlage brauchbar = Ähnlichkeit >= Schwelle. Der Formulartyp entscheidet nicht (wie bei Lusine: das ähnlichste vorhandene Formular
    /// wird bearbeitet; z. B. enthält ein Gebührenbescheid oft Anordnungs-Seiten). Abweichender Typ ist nur ein Hinweis (TypeNote).</summary>
    public static bool TemplateUsable(SimilarHit? hit, Classification cls, double minSimilarity) => hit != null && hit.Score >= minSimilarity;

    /// <summary>Hinweis, wenn der Typ der Vorlage vom erkannten Typ abweicht (leer = kein Hinweis).</summary>
    public static string TypeNote(SimilarHit? hit, Classification cls) =>
        hit == null || string.Equals(hit.Kind, cls.Kind, StringComparison.Ordinal) || cls.Signals.Contains("Kommentar im Dokument") ? "" : $"Typ der Vorlage \"{hit.Name}\" weicht ab ({hit.Kind} statt {cls.Kind}) - bitte prüfen";

    /// <summary>Text für Spalte/Mail: leer, wenn die Vorlage brauchbar ist.</summary>
    public static string TemplateRemark(SimilarHit? hit, Classification cls, double minSimilarity)
    {
        if (hit == null) return NoTemplate;
        if (TemplateUsable(hit, cls, minSimilarity)) return "";
        return $"{NoTemplate} (nächster Treffer \"{hit.Name}\": Ähnlichkeit {hit.Score:0.00} < {minSimilarity:0.00})";
    }

    /// <summary>Gründe, warum ein Mensch das Formular prüfen muss (leer = unauffällig).</summary>
    /// <summary>Hinweise zu Scans: ohne OCR gibt es keinen Text, mit OCR sind die Positionen künstlich.</summary>
    public static List<string> ScanReasons(IrDocument ir)
    {
        var r = new List<string>();
        int scans = ir.Pages.Count(p => p.Images.Any(m => m.Background));
        if (scans == 0) return r;
        if (ir.Ocr) r.Add($"Scan ({scans} Seite(n)): Text per OCR gelesen, Positionen künstlich - Wortlaut und Layout prüfen");
        else if (ir.Pages.All(p => p.Lines.Count == 0)) r.Add($"Scan ohne Text ({scans} Seite(n)): OCR nicht möglich oder fehlgeschlagen - kein Formular erzeugbar, bitte Word-Vorlage anfordern");
        return r;
    }

    public static List<string> ReviewReasons(Classification cls, int imagesOpen, IEnumerable<string>? unassignedHighlights = null)
    {
        var r = new List<string>();
        if (cls.Kind == "Sonstiges") r.Add($"Typ nicht erkannt (Sonstiges, Sicherheit {cls.Confidence:0.00})");
        else if (cls.Confidence < MinConfidence) r.Add($"niedrige Typ-Sicherheit {cls.Confidence:0.00} ({cls.Kind})");
        if (imagesOpen > 0) r.Add($"{imagesOpen} Bild(er) nicht übernommen: von Hand einfügen");
        var u = unassignedHighlights?.ToList() ?? new List<string>();
        if (u.Count > 0) r.Add($"{u.Count} gelbe/rote Markierung(en) ohne Platzhalter: {string.Join("; ", u.Take(5))}");
        return r;
    }
}
}
