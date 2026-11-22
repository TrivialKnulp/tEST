using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StfKit.Core
{

/// <summary>Liest Word-Felder (MERGEFIELD …) aus einem docx und macht sie sichtbar:
/// Run-Elemente mit w:fldChar / w:instrText werden entfernt, der Ergebnistext «Name» bleibt.</summary>
public static class DocxFields
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly Regex PartRx = new(@"^word/(document|header\d*|footer\d*)\.xml$", RegexOptions.Compiled);
    static readonly Regex InstrRx = new(@"^\s*(MERGEFIELD|DOCPROPERTY|REF)\s+""?([^\s""\\]+)", RegexOptions.Compiled);

    public sealed record Field(string Kind, string Name, string Part);

    /// <summary>Nur auslesen.</summary>
    public static List<Field> Read(string docx)
    {
        using var z = ZipFile.OpenRead(docx);
        var res = new List<Field>(); var seen = new HashSet<string>();
        foreach (var e in z.Entries.Where(e => PartRx.IsMatch(e.FullName)))
        {
            using var s = e.Open(); var x = XDocument.Load(s);
            CollectFields(x, e.FullName, res, seen);
        }
        return res;
    }

    static void CollectFields(XDocument x, string part, List<Field> res, HashSet<string> seen)
    {
        foreach (var ins in x.Descendants(W + "instrText"))
        {
            var m = InstrRx.Match(ins.Value);
            if (m.Success && seen.Add(part + "|" + m.Groups[2].Value))
                res.Add(new Field(m.Groups[1].Value, m.Groups[2].Value, part));
        }
    }

    /// <summary>Schreibt eine bereinigte Kopie (Felder → sichtbarer Text) und liefert die Feldliste.</summary>
    public static List<Field> Prepare(string src, string dst)
    {
        var res = new List<Field>(); var seen = new HashSet<string>();
        using var zin = ZipFile.OpenRead(src);
        if (File.Exists(dst)) File.Delete(dst);
        using var zout = ZipFile.Open(dst, ZipArchiveMode.Create);
        foreach (var e in zin.Entries)
        {
            var ne = zout.CreateEntry(e.FullName, CompressionLevel.Optimal);
            using var os = ne.Open(); using var inp = e.Open();
            if (!PartRx.IsMatch(e.FullName)) { inp.CopyTo(os); continue; }
            var x = XDocument.Load(inp);
            CollectFields(x, e.FullName, res, seen);
            foreach (var r in x.Descendants(W + "r").ToList())
                if (r.Element(W + "fldChar") != null || r.Element(W + "instrText") != null) r.Remove();
            foreach (var fs in x.Descendants(W + "fldSimple").ToList()) fs.ReplaceWith(fs.Nodes());
            x.Save(os);
        }
        return res;
    }
}
}