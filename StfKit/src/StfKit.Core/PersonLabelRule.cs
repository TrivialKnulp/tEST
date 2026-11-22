using System;
using System.Text.RegularExpressions;

namespace StfKit.Core
{

    /// <summary>Im Kundenmuster steht hinter Zuständigkeits-Beschriftungen oft der Name einer echten Person ("Sachbearbeiter/in Cindy Radermacher").
    /// Der Name gehört nicht in die Formularvorlage (er kommt über &lt;SB_Vorname&gt; &lt;SB_Name&gt;) und verlängert die Beschriftung so, dass sie umbricht und die
    /// folgenden Beschriftungen gegenüber den Werten verrutschen. Diese Regel lässt nur die Beschriftung stehen und meldet die Entfernung.
    /// Nur Namen aus genau zwei großgeschriebenen Wörtern (optional Herr/Frau davor) direkt hinter der Beschriftung; alles andere bleibt unverändert.</summary>
    public static class PersonLabelRule
    {
        static readonly Regex Rx = new(@"^(?<label>(?:Sachbearbeiter(?:/in|in|\(in\))?|Bearbeiter(?:/in|in)?|Ansprechpartner(?:/in|in)?|Auskunft erteilt(?: durch)?|Zuständig(?:/e|e|er)?)\s*:?)\s+(?:(?:Herr|Frau|Herrn)\s+)?(?<name>\p{Lu}[\p{L}\-']+\s+\p{Lu}[\p{L}\-']+)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static int Apply(IrDocument ir)
        {
            int n = 0;
            foreach (var pg in ir.Pages)
                foreach (var l in pg.Lines)
                {
                    var m = Rx.Match(l.Text.Trim()); if (!m.Success) continue;
                    var label = m.Groups["label"].Value.Trim(); var name = m.Groups["name"].Value;
                    ir.Notes.RemovedPersonNames.Add($"{l.Text.Trim()} → {label} (Name '{name}' entfernt; kommt über <SB_Vorname> <SB_Name>)");
                    if (l.Text.Length > 0) l.W = Math.Max(1.0, l.W * label.Length / Math.Max(1, l.Text.Trim().Length));
                    l.Text = label; n++;
                }
            return n;
        }
    }
}