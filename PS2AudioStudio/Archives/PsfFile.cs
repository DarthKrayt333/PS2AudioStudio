using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PS2AudioStudio.Archives
{
    public class PsfVabInfo
    {
        public string Source;
        public int Offset;
        public bool Valid;
        public int Version, Programs, UsedPrograms, Tones, Vags;
        public int[] VagSizes = new int[0];
        public long DataBytes;
        public int DataOffset;
        public string Note = "";
    }

    public class PsfSeqInfo
    {
        public string Source;
        public int Offset;
        public bool Valid;
        public int Version, Resolution, TempoMicros, Length;
        public string Note = "";
    }

    public class PsfScan
    {
        public string FilePath;
        public List<string> Lines = new List<string>();
        public List<PsfVabInfo> Vabs = new List<PsfVabInfo>();
        public List<PsfSeqInfo> Seqs = new List<PsfSeqInfo>();
        public int AkaoHits;

        public int ValidVabs { get { int n = 0; foreach (var v in Vabs) if (v.Valid) n++; return n; } }
        public int ValidSeqs { get { int n = 0; foreach (var s in Seqs) if (s.Valid) n++; return n; } }

        public string Report()
        {
            var sb = new StringBuilder();
            foreach (string l in Lines) sb.AppendLine(l);
            sb.AppendLine();

            sb.AppendLine("VAB sound banks found: " + Vabs.Count);
            int shown = 0;
            foreach (var v in Vabs)
            {
                if (shown++ >= 12) { sb.AppendLine("  ..."); break; }
                sb.Append("  at 0x" + v.Offset.ToString("X") + " in " + v.Source + ": ");
                if (!v.Valid) { sb.AppendLine("not a real VAB (" + v.Note + ")"); continue; }
                sb.Append("version " + v.Version + ", " + v.Programs + " programs, " + v.Tones + " tones, "
                          + v.Vags + " VAGs, " + v.DataBytes.ToString("N0") + " bytes of sample data");
                if (v.Note.Length > 0) sb.Append(" [" + v.Note + "]");
                sb.AppendLine();
            }

            sb.AppendLine("SEQ songs found: " + Seqs.Count);
            shown = 0;
            foreach (var s in Seqs)
            {
                if (shown++ >= 12) { sb.AppendLine("  ..."); break; }
                sb.Append("  at 0x" + s.Offset.ToString("X") + " in " + s.Source + ": ");
                if (!s.Valid) { sb.AppendLine("not a real SEQ (" + s.Note + ")"); continue; }
                double bpm = s.TempoMicros > 0 ? 60000000.0 / s.TempoMicros : 0;
                sb.AppendLine("resolution " + s.Resolution + " ticks, tempo " + bpm.ToString("0.#")
                              + " BPM, about " + s.Length.ToString("N0") + " bytes");
            }

            if (AkaoHits > 0) sb.AppendLine("AKAO markers: " + AkaoHits);
            sb.AppendLine();

            if (ValidVabs > 0 && ValidSeqs > 0)
                sb.AppendLine("RESULT: standard Sony VAB + SEQ data found. Conversion is possible.");
            else if (ValidVabs > 0)
                sb.AppendLine("RESULT: a VAB bank was found but no SEQ song. The songs may be in another file.");
            else if (ValidSeqs > 0)
                sb.AppendLine("RESULT: SEQ songs were found but no VAB bank.");
            else if (AkaoHits > 0)
                sb.AppendLine("RESULT: no standard VAB/SEQ, but AKAO markers (Square's own driver). Samples are found by scanning (see the list). Song conversion needs the AKAO reader.");
            else
                sb.AppendLine("RESULT: no VAB/SEQ data recognised. This game uses a custom driver or the data is packed.");
            return sb.ToString();
        }
    }

    public static class PsfFile
    {
        static readonly byte[] SigVab = { 0x70, 0x42, 0x41, 0x56 };   // pBAV
        static readonly byte[] SigSeq = { 0x70, 0x51, 0x45, 0x53 };   // pQES
        static readonly byte[] SigAkao = { 0x41, 0x4B, 0x41, 0x4F };  // AKAO

        // ---- little helpers ----
        static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
        static int U16(byte[] b, int o) { return b[o] | (b[o + 1] << 8); }
        static uint U32BE(byte[] b, int o) { return (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]); }
        static int U16BE(byte[] b, int o) { return (b[o] << 8) | b[o + 1]; }

        static int Find(byte[] buf, int pos, byte[] sig)
        {
            if (pos >= buf.Length) return -1;
            return new ReadOnlySpan<byte>(buf, pos, buf.Length - pos).IndexOf(new ReadOnlySpan<byte>(sig));
        }

        static byte[] Inflate(byte[] src, int start, int len)
        {
            var ms = new MemoryStream();
            try
            {
                using (var input = new MemoryStream(src, start, len))
                using (var z = new ZLibStream(input, CompressionMode.Decompress))
                {
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = z.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                }
            }
            catch (InvalidDataException) { }
            catch (IOException) { }
            return ms.ToArray();
        }

        /// <summary>Reads a PSF container: returns the decompressed program and the [TAG] values.</summary>
        public static byte[] ReadProgram(byte[] f, out Dictionary<string, string> tags, out int version)
        {
            tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            version = -1;
            if (f.Length < 16 || f[0] != 'P' || f[1] != 'S' || f[2] != 'F')
                throw new InvalidDataException("Not a PSF file (no 'PSF' header).");
            version = f[3];
            long reserved = U32(f, 4), comp = U32(f, 8);
            long start = 16 + reserved;
            if (start > f.Length) throw new InvalidDataException("PSF header is damaged (reserved area is bigger than the file).");
            long end = start + comp;
            if (end > f.Length) end = f.Length;
            byte[] prog = Inflate(f, (int)start, (int)(end - start));

            int t = (int)end;
            if (t + 5 <= f.Length && f[t] == '[' && f[t + 1] == 'T' && f[t + 2] == 'A' && f[t + 3] == 'G' && f[t + 4] == ']')
            {
                string text = Encoding.UTF8.GetString(f, t + 5, f.Length - t - 5);
                foreach (string line in text.Split('\n'))
                {
                    string l = line.Trim('\r', ' ', '\t');
                    int eq = l.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = l.Substring(0, eq).Trim();
                    string v = l.Substring(eq + 1).Trim();
                    if (!tags.ContainsKey(k)) tags[k] = v;
                }
            }
            return prog;
        }

        public static PsfScan Scan(string path)
        {
            var r = new PsfScan { FilePath = path };
            byte[] f = File.ReadAllBytes(path);
            if (f.Length >= 4 && f[0] == 'P' && f[1] == 'S' && f[2] == 'F')
                ScanPsf(r, path, f, 0);
            else
            {
                r.Lines.Add(System.IO.Path.GetFileName(path) + ": plain file, " + f.Length.ToString("N0") + " bytes. Scanning the bytes directly.");
                ScanBuffer(r, System.IO.Path.GetFileName(path), f);
            }
            return r;
        }

        static void ScanPsf(PsfScan r, string path, byte[] f, int depth)
        {
            string name = System.IO.Path.GetFileName(path);
            Dictionary<string, string> tags;
            int ver;
            byte[] prog = ReadProgram(f, out tags, out ver);

            string kind = ver == 1 ? "PlayStation 1" : ver == 2 ? "PlayStation 2 (PSF2)" : "other";
            r.Lines.Add(name + ": PSF version " + ver + " (" + kind + "), program " + prog.Length.ToString("N0") + " bytes");
            string title, game, artist;
            if (tags.TryGetValue("game", out game)) r.Lines.Add("  game: " + game);
            if (tags.TryGetValue("title", out title)) r.Lines.Add("  title: " + title);
            if (tags.TryGetValue("artist", out artist)) r.Lines.Add("  artist: " + artist);

            if (ver == 2)
            {
                r.Lines.Add("  This is a PSF2 file. Use the PSF2 tab's Open .psf2 instead.");
                return;
            }

            if (prog.Length == 0)
            {
                r.Lines.Add("  The program is empty (a minipsf that only points to its library, or the data is damaged).");
            }
            else
            {
                if (prog.Length >= 0x800 && Encoding.ASCII.GetString(prog, 0, 8) == "PS-X EXE")
                    r.Lines.Add("  PS-X EXE: start address 0x" + U32(prog, 0x10).ToString("X8") +
                                ", load address 0x" + U32(prog, 0x18).ToString("X8") +
                                ", size 0x" + U32(prog, 0x1C).ToString("X"));
                ScanBuffer(r, name, prog);
            }

            if (depth >= 4) return;
            string dir = System.IO.Path.GetDirectoryName(path);
            foreach (var kv in tags)
            {
                if (!kv.Key.StartsWith("_lib", StringComparison.OrdinalIgnoreCase)) continue;
                string libPath = System.IO.Path.Combine(dir ?? "", kv.Value);
                if (!File.Exists(libPath))
                {
                    r.Lines.Add("  Library not found: " + kv.Value + " (put it in the same folder as the minipsf)");
                    continue;
                }
                r.Lines.Add("  Library: " + kv.Value);
                try { ScanPsf(r, libPath, File.ReadAllBytes(libPath), depth + 1); }
                catch (Exception ex) { r.Lines.Add("  Could not read " + kv.Value + ": " + ex.Message); }
            }
        }

        public static void ScanBuffer(PsfScan r, string src, byte[] buf)
        {
            int pos = 0;
            while (true)
            {
                int i = Find(buf, pos, SigVab);
                if (i < 0) break;
                int off = pos + i;
                r.Vabs.Add(ParseVab(src, buf, off));
                pos = off + 4;
            }

            pos = 0;
            while (true)
            {
                int i = Find(buf, pos, SigSeq);
                if (i < 0) break;
                int off = pos + i;
                r.Seqs.Add(ParseSeq(src, buf, off));
                pos = off + 4;
            }

            pos = 0;
            while (true)
            {
                int i = Find(buf, pos, SigAkao);
                if (i < 0) break;
                r.AkaoHits++;
                pos += i + 4;
            }
        }

        static PsfVabInfo ParseVab(string src, byte[] buf, int off)
        {
            var v = new PsfVabInfo { Source = src, Offset = off };
            if (buf.Length - off < 0x820) { v.Note = "header cut off"; return v; }

            v.Version = (int)U32(buf, off + 4);
            v.Programs = U16(buf, off + 18);
            v.Tones = U16(buf, off + 20);
            v.Vags = U16(buf, off + 22);
            if (v.Programs < 1 || v.Programs > 128 || v.Vags < 1 || v.Vags > 255 || v.Tones > 2048)
            {
                v.Note = "header values out of range: false match";
                return v;
            }

            int used = 0;
            for (int i = 0; i < 128; i++) if (buf[off + 0x20 + i * 16] > 0) used++;
            v.UsedPrograms = used;

            int tablePos = off + 0x820 + v.Programs * 512;
            if (tablePos + 512 > buf.Length) { v.Note = "tone table runs past the end"; return v; }

            int[] table = new int[256];
            for (int i = 0; i < 256; i++) table[i] = U16(buf, tablePos + i * 2);
            int shift = table[0] == 0 ? 1 : 0;
            v.VagSizes = new int[v.Vags];
            long sum = 0;
            for (int i = 0; i < v.Vags; i++)
            {
                int idx = i + shift;
                int size = idx < 256 ? table[idx] * 8 : 0;
                v.VagSizes[i] = size;
                sum += size;
            }
            v.DataBytes = sum;
            v.DataOffset = tablePos + 512;
            v.Valid = true;

            var notes = new List<string>();
            if (used != v.Programs) notes.Add(used + " of the programs have tones");
            long over = v.DataOffset + sum - buf.Length;
            if (over > 0) notes.Add("sample data runs " + over.ToString("N0") + " bytes past the end: the bank may be streamed from disc");
            if (sum == 0) notes.Add("all VAG sizes are 0");
            v.Note = string.Join("; ", notes);
            return v;
        }

        static PsfSeqInfo ParseSeq(string src, byte[] buf, int off)
        {
            var s = new PsfSeqInfo { Source = src, Offset = off };
            if (buf.Length - off < 16) { s.Note = "header cut off"; return s; }

            s.Version = (int)U32BE(buf, off + 4);
            s.Resolution = U16BE(buf, off + 8);
            s.TempoMicros = (buf[off + 10] << 16) | (buf[off + 11] << 8) | buf[off + 12];
            if (s.Version != 1 || s.Resolution < 1 || s.Resolution > 4000)
            {
                s.Note = "header values out of range: false match";
                return s;
            }

            int limit = buf.Length;
            int next = Find(buf, off + 4, SigSeq);
            if (next >= 0) limit = off + 4 + next;
            int end = -1;
            for (int j = off + 15; j + 2 < limit; j++)
            {
                if (buf[j] == 0xFF && buf[j + 1] == 0x2F && buf[j + 2] == 0x00) { end = j + 3; break; }
            }
            s.Length = (end > 0 ? end : limit) - off;
            s.Valid = true;
            if (end < 0) s.Note = "no end-of-track marker found";
            return s;
        }
    }
}
