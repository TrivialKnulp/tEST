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

/// <summary>Minimaler PDF-Schreiber ohne Abhängigkeiten (Standardschriften Helvetica, WinAnsi). Koordinaten in mm, Ursprung links oben.</summary>
public sealed class PdfWriter
{
    static readonly Encoding Win1252;
    static PdfWriter()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Win1252 = Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
    }
    const double Pt = 72.0 / 25.4;
    readonly List<(StringBuilder C, double W, double H)> pages = new();
    StringBuilder? cur; double curH;

    static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", "").Replace("\n", " ");

    public void NewPage(double wMm = 210, double hMm = 297)
    {
        cur = new StringBuilder(); curH = hMm; pages.Add((cur, wMm, hMm));
    }

    /// <summary>style: 0 normal, 1 fett, 2 kursiv, 3 fett+kursiv</summary>
    public void Text(double xMm, double baselineMm, string text, double sizePt, int style = 0, (double R, double G, double B)? color = null)
    {
        if (cur == null || string.IsNullOrEmpty(text)) return;
        var c = color ?? (0, 0, 0);
        cur.Append($"BT {N(c.R)} {N(c.G)} {N(c.B)} rg /F{1 + style} {N(sizePt)} Tf {N(xMm * Pt)} {N((curH - baselineMm) * Pt)} Td ({Esc(text)}) Tj ET\n");
    }

    public void Line(double x1, double y1, double x2, double y2, double widthMm = 0.2, (double R, double G, double B)? color = null, bool dashed = false)
    {
        if (cur == null) return; var c = color ?? (0, 0, 0);
        cur.Append($"{N(c.R)} {N(c.G)} {N(c.B)} RG {N(Math.Max(widthMm, 0.05) * Pt)} w {(dashed ? "[2 2] 0 d" : "[] 0 d")} {N(x1 * Pt)} {N((curH - y1) * Pt)} m {N(x2 * Pt)} {N((curH - y2) * Pt)} l S\n");
    }

    public void Rect(double x, double y, double w, double h, double widthMm = 0.2, (double R, double G, double B)? color = null,
        (double R, double G, double B)? fill = null, bool dashed = false)
    {
        if (cur == null) return; var c = color ?? (0, 0, 0);
        if (fill is { } f) cur.Append($"{N(f.R)} {N(f.G)} {N(f.B)} rg {N(x * Pt)} {N((curH - y - h) * Pt)} {N(w * Pt)} {N(h * Pt)} re f\n");
        cur.Append($"{N(c.R)} {N(c.G)} {N(c.B)} RG {N(Math.Max(widthMm, 0.05) * Pt)} w {(dashed ? "[2 2] 0 d" : "[] 0 d")} {N(x * Pt)} {N((curH - y - h) * Pt)} {N(w * Pt)} {N(h * Pt)} re S\n");
    }

    readonly List<(int Page, string Name, byte[] Pdf, int W, int H, string Cs, int Bpc, byte[]? Palette, bool Predictor)> imgs = new();

    /// <summary>PNG (ohne Interlace) als Bild in die Seite setzen. true = gezeichnet, false = Format nicht unterstützt.</summary>
    public bool Image(double x, double y, double w, double h, byte[] png)
    {
        if (cur == null) return false;
        try
        {
            var im = PngInfo.Read(png); if (im == null) return false;
            string name = "Im" + (imgs.Count + 1);
            imgs.Add((pages.Count - 1, name, im.Data, im.W, im.H, im.ColorSpace, im.Bpc, im.Palette, im.Predictor));
            cur.Append($"q {N(w * Pt)} 0 0 {N(h * Pt)} {N(x * Pt)} {N((curH - y - h) * Pt)} cm /{name} Do Q\n");
            return true;
        }
        catch { return false; }
    }

    string XObj(int page, List<int> ids)
    {
        var l = new List<string>(); for (int k = 0; k < imgs.Count; k++) if (imgs[k].Page == page) l.Add($"/{imgs[k].Name} {ids[k]} 0 R");
        return l.Count == 0 ? "" : " /XObject << " + string.Join(" ", l) + " >>";
    }

    public byte[] ToBytes()
    {
        if (pages.Count == 0) NewPage();
        var ms = new MemoryStream(); var offs = new List<long>();
        void W(string s) { var b = Win1252.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(int id, string body) { offs.Add(ms.Position); W($"{id} 0 obj\n{body}\nendobj\n"); }
        W("%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n");
        int n = pages.Count; int firstPage = 8;                      // 1 Catalog, 2 Pages, 3-6 Fonts, 7 reserve
        int imgBase = firstPage + 2 * n;                              // Bildobjekte danach (je Bild 1, bei Palette 2)
        var imgIds = new List<int>(); { int id = imgBase; foreach (var im in imgs) { imgIds.Add(id); id += im.Palette != null ? 2 : 1; } }
        int objTotal = imgBase + imgs.Sum(i => i.Palette != null ? 2 : 1);
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, $"<< /Type /Pages /Count {n} /Kids [{string.Join(" ", Enumerable.Range(0, n).Select(i => $"{firstPage + 2 * i} 0 R"))}] >>");
        string[] fonts = { "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique" };
        for (int i = 0; i < 4; i++) Obj(3 + i, $"<< /Type /Font /Subtype /Type1 /BaseFont /{fonts[i]} /Encoding /WinAnsiEncoding >>");
        Obj(7, "<< /Producer (StfKit) >>");
        for (int i = 0; i < n; i++)
        {
            var (c, w, h) = pages[i]; int pid = firstPage + 2 * i, cid = pid + 1;
            Obj(pid, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(w * Pt)} {N(h * Pt)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R /F4 6 0 R >>{XObj(i, imgIds)} >> /Contents {cid} 0 R >>");
            var data = Win1252.GetBytes(c.ToString());
            offs.Add(ms.Position); W($"{cid} 0 obj\n<< /Length {data.Length} >>\nstream\n"); ms.Write(data, 0, data.Length); W("\nendstream\nendobj\n");
        }
        for (int k = 0; k < imgs.Count; k++)
        {
            var im = imgs[k]; int id = imgIds[k];
            string cs = im.Palette != null ? $"[/Indexed /DeviceRGB {im.Palette.Length / 3 - 1} {id + 1} 0 R]" : "/" + im.Cs;
            string parms = im.Predictor ? $" /DecodeParms << /Predictor 15 /Colors {(im.Cs == "DeviceRGB" ? 3 : 1)} /BitsPerComponent {im.Bpc} /Columns {im.W} >>" : "";
            offs.Add(ms.Position); W($"{id} 0 obj\n<< /Type /XObject /Subtype /Image /Width {im.W} /Height {im.H} /ColorSpace {cs} /BitsPerComponent {im.Bpc} /Filter /FlateDecode{parms} /Length {im.Pdf.Length} >>\nstream\n");
            ms.Write(im.Pdf, 0, im.Pdf.Length); W("\nendstream\nendobj\n");
            if (im.Palette != null) { offs.Add(ms.Position); W($"{id + 1} 0 obj\n<< /Length {im.Palette.Length} >>\nstream\n"); ms.Write(im.Palette, 0, im.Palette.Length); W("\nendstream\nendobj\n"); }
        }
        long xref = ms.Position; int total = objTotal;
        W($"xref\n0 {total}\n0000000000 65535 f \n");
        foreach (var o in offs) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {total} /Root 1 0 R /Info 7 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}

/// <summary>Liest das Nötigste aus einem PNG, damit es ohne Bibliothek in ein PDF eingebettet werden kann.
/// Direkt übernommen (PDF-Predictor 15): Graustufen/RGB/Palette ohne Interlace. Mit Alpha: Alpha wird auf Weiß gerechnet und neu verpackt.</summary>
sealed class PngInfo
{
    public int W, H, Bpc; public string ColorSpace = "DeviceRGB"; public byte[]? Palette; public byte[] Data = Array.Empty<byte>(); public bool Predictor = true;

    public static PngInfo? Read(byte[] b)
    {
        if (b.Length < 33 || b[0] != 0x89 || b[1] != 0x50) return null;
        int pos = 8, ct = 0, depth = 8, il = 0; var info = new PngInfo(); var idat = new MemoryStream(); byte[]? plte = null;
        int I(int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        while (pos + 8 <= b.Length)
        {
            int len = I(pos); string type = Encoding.ASCII.GetString(b, pos + 4, 4); int d = pos + 8;
            if (type == "IHDR") { info.W = I(d); info.H = I(d + 4); depth = b[d + 8]; ct = b[d + 9]; il = b[d + 12]; }
            else if (type == "PLTE") { plte = new byte[len]; Array.Copy(b, d, plte, 0, len); }
            else if (type == "IDAT") idat.Write(b, d, len);
            else if (type == "IEND") break;
            pos = d + len + 4;
        }
        if (il != 0 || info.W <= 0 || info.H <= 0) return null;
        var z = idat.ToArray(); info.Bpc = depth;
        if (ct == 0 && depth <= 8) { info.ColorSpace = "DeviceGray"; info.Data = z; return info; }
        if (ct == 2 && depth == 8) { info.ColorSpace = "DeviceRGB"; info.Data = z; return info; }
        if (ct == 3 && plte != null) { info.ColorSpace = "Indexed"; info.Palette = plte; info.Data = z; return info; }
        if ((ct == 6 || ct == 4) && depth == 8)
        {
            int ch = ct == 6 ? 4 : 2, stride = info.W * ch; var raw = new MemoryStream();
            using (var inf = new System.IO.Compression.DeflateStream(new MemoryStream(z, 2, z.Length - 2), System.IO.Compression.CompressionMode.Decompress)) inf.CopyTo(raw);
            var rw = raw.ToArray(); if (rw.Length < (stride + 1) * info.H) return null;
            var prev = new byte[stride]; var cur = new byte[stride]; int oc = ct == 6 ? 3 : 1; var outp = new byte[(info.W * oc + 1) * info.H];
            for (int y = 0; y < info.H; y++)
            {
                int ft = rw[y * (stride + 1)]; Buffer.BlockCopy(rw, y * (stride + 1) + 1, cur, 0, stride);
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= ch ? cur[i - ch] : 0, up = prev[i], c = i >= ch ? prev[i - ch] : 0, add = ft switch
                    { 1 => a, 2 => up, 3 => (a + up) / 2, 4 => Paeth(a, up, c), _ => 0 };
                    cur[i] = (byte)(cur[i] + add);
                }
                int o = y * (info.W * oc + 1); outp[o] = 0;
                for (int x = 0; x < info.W; x++)
                {
                    int al = cur[x * ch + ch - 1];
                    for (int k = 0; k < oc; k++) outp[o + 1 + x * oc + k] = (byte)((cur[x * ch + k] * al + 255 * (255 - al)) / 255);
                }
                var t = prev; prev = cur; cur = t;
            }
            using var ms = new MemoryStream(); ms.WriteByte(0x78); ms.WriteByte(0x9C);
            using (var df = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) df.Write(outp, 0, outp.Length);
            uint s1 = 1, s2 = 0; foreach (var v in outp) { s1 = (s1 + v) % 65521; s2 = (s2 + s1) % 65521; }
            uint ad = (s2 << 16) | s1; ms.WriteByte((byte)(ad >> 24)); ms.WriteByte((byte)(ad >> 16)); ms.WriteByte((byte)(ad >> 8)); ms.WriteByte((byte)ad);
            info.Data = ms.ToArray(); info.ColorSpace = ct == 6 ? "DeviceRGB" : "DeviceGray"; return info;
        }
        return null;
    }
    static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
}

/// <summary>Annäherung an die Druckausgabe einer .stf-Datei (NICHT der echte Designer-Ausdruck) zur Prüfung durch den Menschen:
/// Platzhalter werden mit Beispielwerten gefüllt (blau), optional wird das Original (aus der IR) in Rot darübergelegt.</summary>
public static class StfPreview
{
    const double Pt = 25.4 / 72.0;
    public sealed record Options(IrDocument? Overlay = null, IDictionary<string, string>? Samples = null, string Title = "");

    static readonly Dictionary<string, string> DefaultSamples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["F_Name"] = "Mustermann", ["F_Vorname"] = "Max", ["F_AnredeN"] = "Herrn", ["F_AnredeS"] = "Sehr geehrter Herr Mustermann",
        ["F_Anrede"] = "Herr", ["F_U_Name"] = "Gemeinschaftsunterkunft Nord", ["F_U_Straße"] = "Musterweg 12", ["F_U_Strasse"] = "Musterweg 12",
        ["F_U_Hausnummer"] = "12", ["F_U_PLZ"] = "12345", ["F_U_Ort"] = "Musterstadt", ["F_U_DatumVon"] = "01.11.2026", ["F_U_DatumBis"] = "30.11.2026",
        ["Datum"] = "02.11.2026", ["SB_Name"] = "Beispiel", ["SB_Vorname"] = "Berta", ["SB_NameVorname"] = "Beispiel, Berta", ["SB_Raum"] = "1.07",
        ["SB_Festnetz"] = "01234 56789", ["SB_eMail"] = "sb@beispiel.de", ["B_Datum"] = "02.11.2026", ["B_Monat"] = "123,45 €",
        ["B_MonatGesamt"] = "246,90 €", ["B_Anteilig"] = "98,76 €", ["B_Personen"] = "2", ["B_Tage"] = "24",
    };
    static readonly Regex PhRx = new(@"<([^<>\n|]+)(?:\|[^<>\n]*)?>", RegexOptions.Compiled);
    static readonly Regex MarkRx = new(@"\[[a-z#0-9A-F]*\]", RegexOptions.Compiled);
    static readonly (double, double, double) Blue = (0.05, 0.2, 0.8), Red = (0.85, 0.2, 0.2), Gray = (0.55, 0.55, 0.55);

    public static string Sub(string t, IDictionary<string, string> samples, out bool had)
    {
        samples = samples.Count == 0 ? DefaultSamples : samples;
        bool h = false;
        var r = PhRx.Replace(t, m => { h = true; return samples.TryGetValue(m.Groups[1].Value, out var v) ? v : "\u2039" + m.Groups[1].Value + "\u203a"; });
        had = h; return r;
    }

    public static byte[] Render(StfDocument doc, Options? opt = null)
    {
        opt ??= new Options();
        var samples = new Dictionary<string, string>(DefaultSamples, StringComparer.OrdinalIgnoreCase);
        if (opt.Samples != null) foreach (var kv in opt.Samples) samples[kv.Key] = kv.Value;
        var pdf = new PdfWriter(); var pages = doc.Pages();
        for (int p = 0; p < pages.Count; p++)
        {
            var ip = opt.Overlay != null && p < opt.Overlay.Pages.Count ? opt.Overlay.Pages[p] : null;
            pdf.NewPage(ip?.W ?? 210, ip?.H ?? 297);
            // Original (rot) zuerst, damit das Erzeugte darüber liegt
            if (ip != null)
            {
                foreach (var r in ip.Rules) pdf.Line(r.X, r.Y, r.X + r.W, r.Y + r.H, 0.15, Red);
                foreach (var w in ip.Widgets) pdf.Rect(w.X, w.Y, w.W, w.H, 0.15, Red);
                foreach (var l in ip.Lines) pdf.Text(l.X, l.Y + 0.79 * l.Size * Pt, l.Text, l.Size, l.Bold ? 1 : 0, Red);
            }
            foreach (var e in pages[p])
            {
                if (e.Type is "Seite" or "Group") continue;
                var (x, y, w, h) = e.Box!.Value;
                switch (e.Type)
                {
                    case "Line":
                    {
                        var b = e.Body; double th = double.TryParse(b[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0.2;
                        bool full = b[4] == "TRUE" && b[8] == "TRUE", horiz = b[4] == "TRUE" && !full, vert = b[4] == "FALSE" && b[8] == "FALSE";
                        if (full) pdf.Rect(x, y, w, h, th);
                        else if (horiz) pdf.Line(x, y + h / 2, x + w, y + h / 2, th);
                        else if (vert) pdf.Line(x + w / 2, y, x + w / 2, y + h, th);
                        else pdf.Rect(x, y, w, h, 0.2, Gray, (0.9, 0.9, 0.9));
                        break;
                    }
                    case "Text" or "String":
                    {
                        var f = e.Font!; var lines = e.Lines; double step = f.Size * 1.17 * Pt, y0 = y - 0.3 + 0.79 * f.Size * Pt;
                        for (int i = 0; i < lines.Count; i++)
                        {
                            string raw = lines[i];
                            bool bold = raw.Contains("[f]") || (f.Style & 1) != 0, ital = raw.Contains("[k]") || (f.Style & 2) != 0;
                            string txt = Sub(MarkRx.Replace(raw, ""), samples, out bool ph);
                            double wid = txt.Length * f.Size * 0.5 * Pt, xx = x;
                            int al = raw.Contains("[z]") ? 1 : raw.Contains("[r]") ? 2 : f.Align;
                            if (al == 1) xx = x + (w - wid) / 2; else if (al == 2) xx = x + w - wid;
                            pdf.Text(xx, y0 + i * step, txt, f.Size, (bold ? 1 : 0) + (ital ? 2 : 0), ph ? Blue : null);
                        }
                        break;
                    }
                    case "PPicture": pdf.Rect(x, y, w, h, 0.2, Gray, null, true); pdf.Text(x + 1, y + 4, "[Bild " + e.Body[0] + "]", 7, 2, Gray); break;
                    case "Picture":
                    {
                        var b64 = string.Concat(e.Body.Skip(8).TakeWhile(q => q != "" ));
                        byte[]? png = null; try { png = Convert.FromBase64String(b64); } catch { }
                        if (png == null || !pdf.Image(x, y, w, h, png)) { pdf.Rect(x, y, w, h, 0.2, Gray, null, true); pdf.Text(x + 1, y + 4, "[Abbildung]", 7, 2, Gray); }
                        break;
                    }
                    case "VarTabelle": pdf.Rect(x, y, w, h, 0.2, Gray, null, true); pdf.Text(x + 1, y + 4, "[Tabelle]", 7, 2, Gray); break;
                    case "Barcode": pdf.Rect(x, y, w, h, 0.2, Gray, null, true); pdf.Text(x + 1, y + 4, "[QR/Barcode]", 7, 2, Gray); break;
                }
            }
            pdf.Text(10, 6, $"{opt.Title} - Seite {p + 1}/{pages.Count} - Vorschau (Näherung, nicht der Designer-Ausdruck)" + (opt.Overlay != null ? " - rot = Original, schwarz/blau = erzeugt" : ""), 6.5, 2, Gray);
        }
        return pdf.ToBytes();
    }
}
}