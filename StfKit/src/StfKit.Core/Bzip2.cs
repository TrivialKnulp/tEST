using System;
using System.IO;

namespace StfKit.Core
{

/// <summary>Minimaler bzip2-Decoder ohne Fremdpakete. In der Firebird-Tabelle FORMULARE liegen die Formulare als bzip2-komprimierter .stf-Text
/// (Beginn "BZh9"). Unkomprimierte Daten werden von <see cref="MaybeDecompress"/> unverändert zurückgegeben.</summary>
public static class Bzip2
{
    public static bool IsBzip2(byte[] d) => d.Length > 10 && d[0] == 'B' && d[1] == 'Z' && d[2] == 'h' && d[3] >= '1' && d[3] <= '9';

    public static byte[] MaybeDecompress(byte[] d) => IsBzip2(d) ? Decompress(d) : d;

    sealed class Bits
    {
        readonly byte[] b; int pos; uint buf; int cnt;
        public Bits(byte[] data, int start) { b = data; pos = start; }
        public int Read(int n)
        {
            while (cnt < n)
            {
                if (pos >= b.Length) throw new InvalidDataException("bzip2: unerwartetes Ende");
                buf = (buf << 8) | b[pos++]; cnt += 8;
            }
            int v = (int)((buf >> (cnt - n)) & ((1u << n) - 1)); cnt -= n; return v;
        }
        public int Bit() => Read(1);
    }

    public static byte[] Decompress(byte[] data)
    {
        if (!IsBzip2(data)) throw new InvalidDataException("kein bzip2");
        int blockMax = (data[3] - '0') * 100000;
        var br = new Bits(data, 4);
        var output = new MemoryStream(Math.Max(1024, data.Length * 4));
        var tt = new int[blockMax];
        while (true)
        {
            long magic = ((long)br.Read(24) << 24) | (uint)br.Read(24);
            if (magic == 0x177245385090) break;                       // Ende des Datenstroms
            if (magic != 0x314159265359) throw new InvalidDataException("bzip2: ungültiger Blockkopf");
            br.Read(32);                                               // Block-CRC (nicht geprüft)
            if (br.Bit() != 0) throw new InvalidDataException("bzip2: randomisierte Blöcke werden nicht unterstützt");
            int origPtr = br.Read(24);

            // Symboltabelle
            int used16 = br.Read(16); var seqToUnseq = new byte[256]; int nInUse = 0;
            for (int i = 0; i < 16; i++)
                if ((used16 & (0x8000 >> i)) != 0)
                {
                    int m = br.Read(16);
                    for (int j = 0; j < 16; j++) if ((m & (0x8000 >> j)) != 0) seqToUnseq[nInUse++] = (byte)(i * 16 + j);
                }
            if (nInUse == 0) throw new InvalidDataException("bzip2: leere Symboltabelle");
            int alphaSize = nInUse + 2;
            int nGroups = br.Read(3); if (nGroups < 2 || nGroups > 6) throw new InvalidDataException("bzip2: Gruppen");
            int nSelectors = br.Read(15); if (nSelectors < 1) throw new InvalidDataException("bzip2: Selektoren");
            var selectors = new byte[nSelectors]; var mtfG = new byte[nGroups]; for (int i = 0; i < nGroups; i++) mtfG[i] = (byte)i;
            for (int i = 0; i < nSelectors; i++)
            {
                int j = 0; while (br.Bit() == 1) { j++; if (j >= nGroups) throw new InvalidDataException("bzip2: Selektor"); }
                byte v = mtfG[j]; for (; j > 0; j--) mtfG[j] = mtfG[j - 1]; mtfG[0] = v; selectors[i] = v;
            }
            // Huffman-Tabellen
            var limit = new int[nGroups][]; var @base = new int[nGroups][]; var perm = new int[nGroups][]; var minLens = new int[nGroups];
            for (int t = 0; t < nGroups; t++)
            {
                var len = new int[alphaSize]; int curr = br.Read(5);
                for (int i = 0; i < alphaSize; i++)
                {
                    while (true)
                    {
                        if (curr < 1 || curr > 20) throw new InvalidDataException("bzip2: Codelänge");
                        if (br.Bit() == 0) break;
                        curr += br.Bit() == 0 ? 1 : -1;
                    }
                    len[i] = curr;
                }
                int minLen = 32, maxLen = 0; foreach (var l in len) { if (l > maxLen) maxLen = l; if (l < minLen) minLen = l; }
                limit[t] = new int[22]; @base[t] = new int[22]; perm[t] = new int[alphaSize]; minLens[t] = minLen;
                int pp = 0; for (int l = minLen; l <= maxLen; l++) for (int s = 0; s < alphaSize; s++) if (len[s] == l) perm[t][pp++] = s;
                var count = new int[23]; foreach (var l in len) count[l]++;
                int code = 0, idx = 0;
                for (int l = minLen; l <= maxLen; l++)
                {
                    @base[t][l] = idx - code;                          // perm-Index = code + base
                    code += count[l]; idx += count[l];
                    limit[t][l] = code - 1; code <<= 1;
                }
                for (int l = maxLen + 1; l < 22; l++) limit[t][l] = int.MaxValue;
            }
            // MTF + RLE2
            var mtf = new byte[256]; for (int i = 0; i < 256; i++) mtf[i] = (byte)i;
            var unzftab = new int[256]; int nblock = 0, eob = nInUse + 1;
            int groupNo = -1, groupPos = 0, gSel = 0; int runLen = 0, runBit = 1;
            while (true)
            {
                if (groupPos == 0) { groupNo++; if (groupNo >= nSelectors) throw new InvalidDataException("bzip2: Selektorende"); gSel = selectors[groupNo]; groupPos = 50; }
                groupPos--;
                int l = minLens[gSel], c = br.Read(l);
                while (l < 22 && c > limit[gSel][l]) { l++; c = (c << 1) | br.Bit(); }
                if (l >= 22) throw new InvalidDataException("bzip2: Huffman");
                int sym = perm[gSel][c + @base[gSel][l]];
                if (sym <= 1)
                {
                    runLen += runBit << sym; runBit <<= 1; continue;
                }
                if (runLen > 0)
                {
                    byte ch = seqToUnseq[mtf[0]];
                    if (nblock + runLen > blockMax) throw new InvalidDataException("bzip2: Blocküberlauf");
                    unzftab[ch] += runLen; while (runLen-- > 0) tt[nblock++] = ch;
                    runLen = 0; runBit = 1;
                }
                if (sym == eob) break;
                {
                    int p = sym - 1; byte v = mtf[p]; Array.Copy(mtf, 0, mtf, 1, p); mtf[0] = v;
                    byte ch = seqToUnseq[v];
                    if (nblock >= blockMax) throw new InvalidDataException("bzip2: Blocküberlauf");
                    unzftab[ch]++; tt[nblock++] = ch;
                }
            }
            if (origPtr >= nblock && nblock > 0) throw new InvalidDataException("bzip2: origPtr");
            // inverse BWT
            var cftab = new int[257]; for (int i = 0; i < 256; i++) cftab[i + 1] = cftab[i] + unzftab[i];
            for (int i = 0; i < nblock; i++) { int ch = tt[i] & 0xff; tt[cftab[ch]++] |= i << 8; }
            // Ausgabe mit RLE1-Dekodierung
            int tPos = nblock > 0 ? tt[origPtr] >> 8 : 0; int prev = -1, same = 0;
            for (int i = 0; i < nblock; i++)
            {
                tPos = tt[tPos]; int ch = tPos & 0xff; tPos >>= 8;
                if (same == 4) { for (int k = 0; k < ch; k++) output.WriteByte((byte)prev); same = 0; prev = -1; continue; }
                if (ch == prev) same++; else { same = 1; prev = ch; }
                output.WriteByte((byte)ch);
            }
        }
        return output.ToArray();
    }
}
}
