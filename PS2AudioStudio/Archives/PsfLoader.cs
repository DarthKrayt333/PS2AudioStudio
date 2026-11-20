using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PS2AudioStudio.Archives
{
    public class PsfBuffer
    {
        public string Name;
        public byte[] Data;
        public bool HasExeHeader;
        public uint LoadAddress, Pc, Gp, Sp, TextSize;
        public PsfScan Scan;

        /// <summary>PlayStation memory address of a byte of this buffer.</summary>
        public uint Mem(int fileOffset)
        {
            if (!HasExeHeader) return (uint)fileOffset;
            return (uint)((long)LoadAddress + fileOffset - 0x800);
        }
    }

    public class PsfPackage
    {
        public string FilePath;
        public List<string> Notes = new List<string>();
        public Dictionary<string, string> Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<PsfBuffer> Buffers = new List<PsfBuffer>();
    }

    public class PsfFoundSample
    {
        public int Offset, Size;
        public bool Loops;
    }

    public static class PsfLoader
    {
        static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }

        public static PsfPackage Load(string path)
        {
            var pkg = new PsfPackage { FilePath = path };

            PS2AudioStudio.Audio.PsxDriver.LastPsfPath = path;

            byte[] f = File.ReadAllBytes(path);
            if (f.Length >= 4 && f[0] == 'P' && f[1] == 'S' && f[2] == 'F')
                AddPsf(pkg, path, f, 0);
            else
            {
                pkg.Notes.Add(Path.GetFileName(path) + ": plain file, " + f.Length.ToString("N0") + " bytes.");
                AddBuffer(pkg, Path.GetFileName(path), f);
            }
            return pkg;
        }

        static void AddPsf(PsfPackage pkg, string path, byte[] f, int depth)
        {
            string name = Path.GetFileName(path);
            Dictionary<string, string> tags;
            int ver;
            byte[] prog = PsfFile.ReadProgram(f, out tags, out ver);

            if (ver == 2)
            {
                pkg.Notes.Add(name + ": this is a PSF2 file - use the PSF2 tab.");
                return;
            }

            pkg.Notes.Add(name + ": PSF version " + ver + ", program " + prog.Length.ToString("N0") + " bytes");
            if (depth == 0)
                foreach (var kv in tags) if (!pkg.Tags.ContainsKey(kv.Key)) pkg.Tags[kv.Key] = kv.Value;

            if (prog.Length > 0) AddBuffer(pkg, name, prog);
            else pkg.Notes.Add("  " + name + ": empty program (a minipsf that only points to its library).");

            if (depth >= 4) return;
            string dir = Path.GetDirectoryName(path) ?? "";
            foreach (var kv in tags)
            {
                if (!kv.Key.StartsWith("_lib", StringComparison.OrdinalIgnoreCase)) continue;
                string lib = Path.Combine(dir, kv.Value);
                if (!File.Exists(lib)) { pkg.Notes.Add("  Library not found: " + kv.Value + " (put it in the same folder)"); continue; }
                try { AddPsf(pkg, lib, File.ReadAllBytes(lib), depth + 1); }
                catch (Exception ex) { pkg.Notes.Add("  Could not read " + kv.Value + ": " + ex.Message); }
            }
        }

        static void AddBuffer(PsfPackage pkg, string name, byte[] data)
        {
            var b = new PsfBuffer { Name = name, Data = data };
            if (data.Length >= 0x800 && data[0] == 'P' && data[1] == 'S' && data[2] == '-' && data[3] == 'X')
            {
                b.HasExeHeader = true;
                b.Pc = U32(data, 0x10);
                b.Gp = U32(data, 0x14);
                b.LoadAddress = U32(data, 0x18);
                b.TextSize = U32(data, 0x1C);
                b.Sp = U32(data, 0x30);
            }
            b.Scan = new PsfScan { FilePath = name };
            PsfFile.ScanBuffer(b.Scan, name, data);
            pkg.Buffers.Add(b);
        }
    }

    /// <summary>Finds PS-ADPCM samples in raw memory by their block structure (works for any driver, AKAO included).</summary>
    public static class PsfSampleFinder
    {
        static bool Valid(byte[] d, int p)
        {
            return (d[p] & 0x0F) <= 12 && (d[p] >> 4) <= 4 && d[p + 1] <= 7;
        }

        static bool IsZero(byte[] d, int p)
        {
            for (int i = 0; i < 16; i++) if (d[p + i] != 0) return false;
            return true;
        }

        public static List<PsfFoundSample> Find(byte[] d, int minBlocks = 8)
        {
            var list = new List<PsfFoundSample>();
            int n = d.Length / 16;
            int blk = 0;
            while (blk < n)
            {
                int p0 = blk * 16;
                if (!Valid(d, p0)) { blk++; continue; }
                // a run of zero blocks is padding: start at the last zero block
                if (IsZero(d, p0) && blk + 1 < n && IsZero(d, p0 + 16)) { blk++; continue; }
                // the first block of a sample is never an end block
                if ((d[p0 + 1] & 1) != 0) { blk++; continue; }

                int j = blk;
                bool ended = false, loop = false;
                while (j < n && j - blk < 65536)
                {
                    int p = j * 16;
                    if (!Valid(d, p)) break;
                    int f = d[p + 1];
                    if ((f & 4) != 0) loop = true;
                    if ((f & 1) != 0)
                    {
                        ended = true;
                        if ((f & 2) != 0) loop = true;
                        break;
                    }
                    j++;
                }

                if (ended && j - blk + 1 >= minBlocks)
                {
                    list.Add(new PsfFoundSample { Offset = p0, Size = (j - blk + 1) * 16, Loops = loop });
                    blk = j + 1;
                }
                else blk = ended ? j + 1 : Math.Max(blk + 1, j);
            }
            return list;
        }

        public static bool RawLoops(byte[] raw)
        {
            for (int p = 0; p + 1 < raw.Length; p += 16)
                if ((raw[p + 1] & 4) != 0) return true;
            if (raw.Length >= 16 && (raw[raw.Length - 15] & 2) != 0) return true;
            return false;
        }
    }

    /// <summary>Sony PS1 SEQ ('pQES') to a Standard MIDI File. The event data of a SEQ is MIDI track data.</summary>
    public static class PsfSeq
    {
        static int ReadVlq(byte[] b, int pos, out int value)
        {
            value = 0;
            for (int i = 0; i < 4 && pos < b.Length; i++)
            {
                int x = b[pos++];
                value = (value << 7) | (x & 0x7F);
                if ((x & 0x80) == 0) break;
            }
            return pos;
        }

        /// <summary>Position just after the end-of-track event, or -1.</summary>
        static int WalkEnd(byte[] b, int pos)
        {
            int status = 0;
            while (pos < b.Length)
            {
                int delta;
                pos = ReadVlq(b, pos, out delta);
                if (pos >= b.Length) return -1;
                int c = b[pos];
                if (c == 0xFF)
                {
                    if (pos + 1 >= b.Length) return -1;
                    int type = b[pos + 1];
                    int mlen;
                    int p2 = ReadVlq(b, pos + 2, out mlen);
                    if (type == 0x2F) return p2;
                    pos = p2 + mlen;
                    continue;
                }
                if (c == 0xF0 || c == 0xF7)
                {
                    int slen;
                    pos = ReadVlq(b, pos + 1, out slen);
                    pos += slen;
                    continue;
                }
                if (c >= 0x80) { status = c; pos++; }
                if (status == 0) return -1;
                int hi = status & 0xF0;
                pos += (hi == 0xC0 || hi == 0xD0) ? 1 : 2;
            }
            return -1;
        }

        public static byte[] ToSmf(byte[] b, int off, out string note)
        {
            note = "";
            if (off < 0 || off + 16 > b.Length) { note = "header cut off"; return null; }
            int res = (b[off + 8] << 8) | b[off + 9];
            int tempo = (b[off + 10] << 16) | (b[off + 11] << 8) | b[off + 12];
            if (tempo <= 0) tempo = 500000;
            int pos = off + 15;

            int end = WalkEnd(b, pos);
            bool addEnd = false;
            if (end < 0)
            {
                end = b.Length;
                int nx = new ReadOnlySpan<byte>(b, off + 4, b.Length - off - 4).IndexOf(new ReadOnlySpan<byte>(new byte[] { 0x70, 0x51, 0x45, 0x53 }));
                if (nx >= 0) end = off + 4 + nx;
                addEnd = true;
                note = "no end-of-track marker: song cut at the next SEQ / end of data";
            }
            if (end <= pos) { note = "empty song"; return null; }

            bool hasTempo = end - pos >= 6 && b[pos] == 0 && b[pos + 1] == 0xFF && b[pos + 2] == 0x51;
            var trk = new MemoryStream();
            if (!hasTempo)
            {
                trk.WriteByte(0); trk.WriteByte(0xFF); trk.WriteByte(0x51); trk.WriteByte(3);
                trk.WriteByte((byte)(tempo >> 16)); trk.WriteByte((byte)(tempo >> 8)); trk.WriteByte((byte)tempo);
            }
            trk.Write(b, pos, end - pos);
            if (addEnd) { trk.WriteByte(0); trk.WriteByte(0xFF); trk.WriteByte(0x2F); trk.WriteByte(0); }
            byte[] td = trk.ToArray();

            var ms = new MemoryStream();
            void W32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            ms.Write(Encoding.ASCII.GetBytes("MThd"), 0, 4);
            W32(6); W16(0); W16(1); W16(res);
            ms.Write(Encoding.ASCII.GetBytes("MTrk"), 0, 4);
            W32(td.Length);
            ms.Write(td, 0, td.Length);
            return ms.ToArray();
        }
    }

    /// <summary>Text dump of a PSF's contents: used to build the AKAO converter against real data.</summary>
    public static class PsfDump
    {
        static void Hex(StringBuilder sb, byte[] d, int off, int len)
        {
            int end = Math.Min(d.Length, off + len);
            for (int p = Math.Max(0, off); p < end; p += 16)
            {
                sb.Append("  " + p.ToString("X6") + ": ");
                var asc = new StringBuilder();
                for (int i = 0; i < 16; i++)
                {
                    if (p + i < end)
                    {
                        byte v = d[p + i];
                        sb.Append(v.ToString("X2") + " ");
                        asc.Append(v >= 32 && v < 127 ? (char)v : '.');
                    }
                    else sb.Append("   ");
                }
                sb.AppendLine(" " + asc);
            }
        }

        public static string Build(PsfPackage pkg)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PS2 Audio Studio - PSF data dump");
            sb.AppendLine("File: " + pkg.FilePath);
            foreach (string n in pkg.Notes) sb.AppendLine(n);
            foreach (var kv in pkg.Tags) sb.AppendLine("tag " + kv.Key + " = " + kv.Value);

            foreach (var b in pkg.Buffers)
            {
                byte[] d = b.Data;
                sb.AppendLine();
                sb.AppendLine("=== " + b.Name + ": " + d.Length.ToString("N0") + " bytes ===");
                if (b.HasExeHeader)
                    sb.AppendLine("PS-X EXE: PC 0x" + b.Pc.ToString("X8") + "  GP 0x" + b.Gp.ToString("X8") + "  load 0x" + b.LoadAddress.ToString("X8")
                                  + "  size 0x" + b.TextSize.ToString("X") + "  SP 0x" + b.Sp.ToString("X8") + "   (file offset = memory - load + 0x800)");

                // AKAO markers
                var hits = new List<int>();
                int pos = 0;
                byte[] sig = { 0x41, 0x4B, 0x41, 0x4F };
                while (pos < d.Length - 4)
                {
                    int i = new ReadOnlySpan<byte>(d, pos, d.Length - pos).IndexOf(new ReadOnlySpan<byte>(sig));
                    if (i < 0) break;
                    hits.Add(pos + i);
                    pos += i + 4;
                    if (hits.Count >= 60) break;
                }
                sb.AppendLine();
                sb.AppendLine("AKAO markers: " + hits.Count);
                for (int h = 0; h < hits.Count; h++)
                {
                    int next = h + 1 < hits.Count ? hits[h + 1] : d.Length;
                    sb.AppendLine("  #" + h + " at file 0x" + hits[h].ToString("X6") + "  mem 0x" + b.Mem(hits[h]).ToString("X8")
                                  + "  distance to next marker/end: " + (next - hits[h]).ToString("N0") + " bytes");
                }
                for (int h = 0; h < hits.Count && h < 8; h++)
                {
                    sb.AppendLine();
                    sb.AppendLine("AKAO #" + h + " first 0x200 bytes:");
                    Hex(sb, d, hits[h], 0x200);
                }

                // PS-ADPCM samples
                var found = PsfSampleFinder.Find(d);
                long total = 0;
                foreach (var f in found) total += f.Size;
                sb.AppendLine();
                sb.AppendLine("PS-ADPCM samples found by scanning: " + found.Count + ", " + total.ToString("N0") + " bytes in total");
                for (int i = 0; i < found.Count && i < 200; i++)
                    sb.AppendLine("  #" + i + "  file 0x" + found[i].Offset.ToString("X6") + "  mem 0x" + b.Mem(found[i].Offset).ToString("X8")
                                  + "  " + found[i].Size.ToString("N0") + " bytes  " + (found[i].Loops ? "LOOP" : "once"));
                if (found.Count > 200) sb.AppendLine("  ... " + (found.Count - 200) + " more");

                if (found.Count > 0)
                {
                    int first = found[0].Offset;
                    sb.AppendLine();
                    sb.AppendLine("0x300 bytes BEFORE the first sample (an instrument table is often here):");
                    Hex(sb, d, Math.Max(0, first - 0x300), Math.Min(0x300, first));
                    sb.AppendLine();
                    sb.AppendLine("first 0x40 bytes of the first sample:");
                    Hex(sb, d, first, 0x40);
                }
            }
            return sb.ToString();
        }
    }
}
