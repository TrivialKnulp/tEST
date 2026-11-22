using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StfKit.Core
{

/// <summary>Font-Angaben eines Text-/String-Elements.</summary>
public sealed record Fnt(string Name, double Size, int Style, int Align);

/// <summary>Ein Element der .stf-Datei (Text, Line, PPicture, Picture, Group, String, Seite, VarTabelle, Barcode).
/// Der Rumpf bleibt roh (Zeilen); typisierte Zugriffe werden vom Ende bzw. Anfang her berechnet.</summary>
public sealed class Elem
{
    public string Type { get; }
    public List<string> Body { get; }
    public Elem(string type, IEnumerable<string>? body = null) { Type = type; Body = body?.ToList() ?? new(); }

    static double Num(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);
    static string Fmt(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    (int Start, int End) Split()
    {
        int end = Body.Count;
        if (Type != "Picture" && end > 1 && Body[^1] == "" && Body[^2] != "") end--;
        int start = 0;
        if (Type == "String") start = 1;
        else if (Type is "Text" or "Barcode") start = Body.IndexOf("@") + 1;
        return (start, end);
    }

    int[] Gi()
    {
        if (Type is "PPicture" or "Picture") return new[] { 2, 3, 4, 5 };
        if (Type == "Group") return new[] { 3, 4, 5, 6 };
        var (_, e) = Split();
        return new[] { e - 6, e - 5, e - 4, e - 3 };
    }

    /// <summary>x, y, Breite, Höhe in mm (Ursprung links oben); null bei @Seite.</summary>
    public (double X, double Y, double W, double H)? Box
    {
        get
        {
            if (Type == "Seite") return null;
            var g = Gi();
            return (Num(Body[g[0]]), Num(Body[g[1]]), Num(Body[g[2]]), Num(Body[g[3]]));
        }
    }

    public void SetBox(double x, double y, double w, double h)
    {
        var g = Gi();
        Body[g[0]] = Fmt(x); Body[g[1]] = Fmt(y); Body[g[2]] = Fmt(w); Body[g[3]] = Fmt(h);
    }

    public IReadOnlyList<string> Lines
    {
        get
        {
            if (Type == "String") return new[] { Body[0] };
            if (Type is "Text" or "Barcode") return Body.Take(Body.IndexOf("@")).ToList();
            throw new InvalidOperationException(Type);
        }
    }

    public void SetLines(IEnumerable<string> lines)
    {
        var l = lines.ToList();
        if (Type == "String") { if (l.Count != 1) throw new ArgumentException("String hat genau 1 Zeile"); Body[0] = l[0]; return; }
        int k = Body.IndexOf("@");
        Body.RemoveRange(0, k);
        Body.InsertRange(0, l);
    }

    public string Text => string.Join("\n", Lines);

    public Fnt? Font
    {
        get
        {
            if (Type is not ("Text" or "String" or "VarTabelle")) return null;
            var (_, e) = Split();
            return new Fnt(Body[e - 10], Num(Body[e - 9]), int.Parse(Body[e - 8]), int.Parse(Body[e - 7]));
        }
    }

    /// <summary>Sichtbarkeits-Bedingung (z. B. TextÜberlauf &lt;&gt; ""), leer = immer.</summary>
    public string? Cond
    {
        get
        {
            if (Type == "Seite") return null;
            if (Type is "PPicture" or "Picture" or "Group") return Body[7];
            var (_, e) = Split();
            return Body[e - 2];
        }
    }

    static readonly Regex Ph = new(@"<([^<>\n]+)>", RegexOptions.Compiled);
    public IEnumerable<string> Placeholders =>
        Type is "Text" or "String" or "Barcode" ? Ph.Matches(Text).Select(m => m.Groups[1].Value) : Enumerable.Empty<string>();

    public byte[]? Png =>
        Type == "Picture" ? Convert.FromBase64String(string.Concat(Body.Skip(8))) : null;
}

/// <summary>Eine komplette .stf-Datei. Parse(b).DumpBytes() == b (byte-genau).</summary>
public sealed class StfDocument
{
    public static readonly string[] ElemTypes = { "Text", "Line", "PPicture", "Picture", "Group", "String", "Seite", "VarTabelle", "Barcode" };
    static readonly HashSet<string> Markers = ElemTypes.Select(t => "@" + t).ToHashSet();
    static readonly UTF8Encoding Utf8 = new(false);

    public string Version => Header[0];
    public List<string> Header { get; set; } = new();
    public List<Elem> Items { get; set; } = new();
    public string Eol { get; set; } = "\r\n";

    public static StfDocument Parse(byte[] raw)
    {
        string t = Utf8.GetString(raw);
        string eol = t.Contains("\r\r\n") ? "\r\r\n" : t.Contains("\r\n") ? "\r\n" : "\n";
        var L = t.Split(eol);
        var idx = Enumerable.Range(0, L.Length).Where(i => Markers.Contains(L[i])).ToList();
        var doc = new StfDocument { Eol = eol };
        if (idx.Count == 0) { doc.Header = L.ToList(); return doc; }
        doc.Header = L.Take(idx[0]).ToList();
        for (int k = 0; k < idx.Count; k++)
        {
            int a = idx[k], b = k + 1 < idx.Count ? idx[k + 1] : L.Length;
            doc.Items.Add(new Elem(L[a][1..], L.Skip(a + 1).Take(b - a - 1)));
        }
        return doc;
    }

    public static StfDocument Load(string path) => Parse(File.ReadAllBytes(path));

    public string Dump()
    {
        var o = new List<string>(Header);
        foreach (var e in Items) { o.Add("@" + e.Type); o.AddRange(e.Body); }
        return string.Join(Eol, o);
    }
    public byte[] DumpBytes() => Utf8.GetBytes(Dump());
    public void Save(string path) => File.WriteAllBytes(path, DumpBytes());

    /// <summary>Prüft die Group-Zähler (Kinder = die nächsten n Elemente, rekursiv).</summary>
    public void ValidateTree()
    {
        int i = 0;
        void Take(int n)
        {
            for (int k = 0; k < n; k++)
            {
                if (i >= Items.Count) throw new InvalidDataException("Group-Zähler überschreitet Dateiende");
                var e = Items[i++];
                if (e.Type == "Group") Take(int.Parse(e.Body[0]));
            }
        }
        while (i < Items.Count) { var e = Items[i++]; if (e.Type == "Group") Take(int.Parse(e.Body[0])); }
    }

    /// <summary>Elemente je Seite (getrennt durch @Seite).</summary>
    public List<List<Elem>> Pages()
    {
        var pages = new List<List<Elem>>(); var cur = new List<Elem>();
        foreach (var e in Items) { if (e.Type == "Seite") { pages.Add(cur); cur = new(); } else cur.Add(e); }
        pages.Add(cur); return pages;
    }

    // ---------------- Builder ----------------
    static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    public static List<string> DefaultHeader(string version) => new()
        { version, "", "300", "300", "0.0", "0.0", "1", "0", "1.0", "1.0", "FALSE", "FALSE", "FALSE", "", "0", "0", "@", "" };

    public static StfDocument New(string version = "DB2026", string eol = "\r\n") =>
        new() { Header = DefaultHeader(version), Eol = eol };

    public StfDocument Add(Elem e) { Items.Add(e); return this; }

    static List<string> TextPrefix(string version, string name)
    {
        var p = new List<string> { "0.2", "-1.0", "FALSE", "-1.0", "FALSE" };
        if (string.CompareOrdinal(version, "DB2025") >= 0) p.Add("TRUE");
        if (string.CompareOrdinal(version, "DB2026") >= 0) p.Add(name);
        return p;
    }

    public static Elem MakeText(string version, double x, double y, double w, double h, IEnumerable<string> lines,
        string font = "Arial", double size = 12.0, int style = 0, int align = 0, string cond = "", string name = "")
    {
        var b = lines.ToList(); b.Add("@"); b.AddRange(TextPrefix(version, name));
        b.AddRange(new[] { font, F(size), style.ToString(), align.ToString(), F(x), F(y), F(w), F(h), cond, "0" });
        return new Elem("Text", b);
    }

    /// <summary>Art eines @Line-Elements (aus Korpus-Statistik und Vergleich mit PDF-Vorlagen abgeleitet):
    /// Rect = Rahmen (Flags 4=TRUE,8=TRUE) | Horizontal = Linie durch die vertikale Mitte der Box (4=TRUE,8=FALSE)
    /// | Vertical = Linie durch die horizontale Mitte (4=FALSE,8=FALSE). Rundung: [6]=TRUE, [7]=Radius.
    /// Unbekannt: (4=FALSE,8=TRUE) – 48 Vorkommen im Korpus, nur flächige Boxen (vermutlich gefüllt).</summary>
    public enum LineKind { Rect, Horizontal, Vertical }

    public static Elem MakeLine(double x, double y, double w, double h, double thickness = 0.2, string cond = "",
        LineKind kind = LineKind.Horizontal, double radius = 0.0)
    {
        string f4 = kind == LineKind.Vertical ? "FALSE" : "TRUE";
        string f8 = kind == LineKind.Rect ? "TRUE" : "FALSE";
        return new("Line", new[] { "", "FALSE", "FALSE", F(thickness), f4, "FALSE", radius > 0 ? "TRUE" : "FALSE", F(radius), f8, F(x), F(y), F(w), F(h), cond, "0" });
    }

    public static Elem MakePPicture(string name, double x, double y, double w, double h, string cond = "") =>
        new("PPicture", new[] { name, "TRUE", F(x), F(y), F(w), F(h), cond, "0" });

    public static Elem MakePicture(byte[] png, double x, double y, double w, double h, bool keepAspect = true, string cond = "")
    {
        string b64 = Convert.ToBase64String(png);
        var b = new List<string> { "$PNG", keepAspect ? "TRUE" : "FALSE", F(x), F(y), F(w), F(h), cond, "0" };
        for (int i = 0; i < b64.Length; i += 80) b.Add(b64.Substring(i, Math.Min(80, b64.Length - i)));
        b.Add(""); return new Elem("Picture", b);
    }

    public static Elem MakeSeite() => new("Seite");
}
}