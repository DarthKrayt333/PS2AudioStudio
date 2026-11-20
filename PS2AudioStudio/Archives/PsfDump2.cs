using System;
using System.Collections.Generic;
using System.Text;

namespace PS2AudioStudio.Archives
{
    public static class PsfDump2
    {
        public static string Build(PsfPackage pkg)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PS2 Audio Studio - AKAO dump 2");
            foreach (var b in pkg.Buffers)
            {
                if (b == null || b.Data == null) continue;
                sb.AppendLine();
                sb.AppendLine("=== " + b.Name + " (" + b.Data.Length.ToString("N0") + " bytes) ===");
                Dump(sb, b.Data);
            }
            return sb.ToString();
        }

        private static long U32(byte[] d, long p)
        {
            if (p < 0 || p + 4 > d.Length) return 0;
            return BitConverter.ToUInt32(d, (int)p);
        }

        private static long U16(byte[] d, long p)
        {
            if (p < 0 || p + 2 > d.Length) return 0;
            return BitConverter.ToUInt16(d, (int)p);
        }

        private static void Hex(StringBuilder sb, byte[] d, long off, long len, string indent)
        {
            if (off < 0) return;
            long end = Math.Min(d.Length, off + len);
            for (long p = off; p < end; p += 16)
            {
                sb.Append(indent).Append(p.ToString("X6")).Append(": ");
                long n = Math.Min(16, end - p);
                for (long i = 0; i < n; i++) sb.Append(d[p + i].ToString("X2")).Append(' ');
                sb.AppendLine();
            }
        }

        private static string Blk(byte[] d, long p)
        {
            if (p < 0 || p + 16 > d.Length) return "(outside file)";
            var sb = new StringBuilder();
            for (int i = 0; i < 16; i++) sb.Append(d[p + i].ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }

        private static bool Valid(byte[] d, long p)
        {
            if (p < 0 || p + 16 > d.Length) return false;
            int shift = d[p] & 0x0F, filt = d[p] >> 4, flags = d[p + 1];
            return shift <= 12 && filt <= 4 && flags <= 7;
        }

        private static bool Zero(byte[] d, long p)
        {
            for (int i = 0; i < 16; i++) if (d[p + i] != 0) return false;
            return true;
        }

        private static void Map(StringBuilder sb, byte[] d)
        {
            for (long c = 0; c < d.Length; c += 0x10000)
            {
                long end = Math.Min(d.Length, c + 0x10000);
                int blocks = 0, zero = 0, nonzero = 0, like = 0;
                for (long p = c; p + 16 <= end; p += 16)
                {
                    blocks++;
                    if (Zero(d, p)) { zero++; continue; }
                    nonzero++;
                    if (Valid(d, p)) like++;
                }
                sb.AppendLine("  0x" + c.ToString("X6") + ": blocks " + blocks + ", all-zero " + zero + ", non-zero " + nonzero + ", non-zero that pass the ADPCM header test " + like);
            }
        }

        private static void Dump(StringBuilder sb, byte[] d)
        {
            var marks = new List<int>();
            for (int i = 0; i + 4 <= d.Length; i++)
                if (d[i] == 0x41 && d[i + 1] == 0x4B && d[i + 2] == 0x41 && d[i + 3] == 0x4F) marks.Add(i);
            sb.AppendLine("AKAO markers: " + marks.Count);

            sb.AppendLine();
            sb.AppendLine("--- map, 0x10000 bytes per line ---");
            Map(sb, d);

            for (int k = 0; k < marks.Count && k < 20; k++)
            {
                int m = marks[k];
                long next = k + 1 < marks.Count ? marks[k + 1] : d.Length;
                long dist = next - m;
                sb.AppendLine();
                sb.AppendLine("##### AKAO #" + k + " at file 0x" + m.ToString("X") + ", distance to next marker/end 0x" + dist.ToString("X"));
                sb.AppendLine("  u16@4=0x" + U16(d, m + 4).ToString("X") + " u16@6=0x" + U16(d, m + 6).ToString("X") +
                    " u16@8=0x" + U16(d, m + 8).ToString("X") + " u32@0x14=0x" + U32(d, m + 0x14).ToString("X") +
                    " u32@0x18=0x" + U32(d, m + 0x18).ToString("X") + " u32@0x1C=0x" + U32(d, m + 0x1C).ToString("X"));

                long size = U32(d, m + 0x14), count = U32(d, m + 0x1C);
                bool bank = count > 0 && count <= 512 && 0x40 + count * 16 + size == dist;
                bool song = U16(d, m + 6) == dist;

                if (bank) Bank(sb, d, m, (int)count, size);
                else if (song)
                {
                    sb.AppendLine("SONG: length field equals the distance. Whole blob:");
                    Hex(sb, d, m, Math.Min(dist, 16384), "  ");
                }
                else
                {
                    sb.AppendLine("Unknown kind, first 0x100 bytes:");
                    Hex(sb, d, m, 0x100, "  ");
                }
            }
        }

        private static void Bank(StringBuilder sb, byte[] d, int m, int count, long size)
        {
            long tbl = m + 0x40;
            long data = tbl + count * 16L;
            sb.AppendLine("BANK: " + count + " entries, table at 0x" + tbl.ToString("X") + ", data at 0x" + data.ToString("X") + ", data size 0x" + size.ToString("X"));
            sb.AppendLine("header 0x40 bytes:");
            Hex(sb, d, m, 0x40, "  ");

            var starts = new long[count];
            for (int i = 0; i < count; i++) starts[i] = U32(d, tbl + i * 16L);
            var sorted = (long[])starts.Clone();
            Array.Sort(sorted);

            sb.AppendLine("entries (raw 16 bytes | start loop len | first block | last block):");
            for (int i = 0; i < count; i++)
            {
                long e = tbl + i * 16L;
                long start = starts[i], loop = U32(d, e + 4);
                long end = size;
                foreach (long s in sorted) if (s > start) { end = s; break; }
                sb.AppendLine("  #" + i.ToString("D2") + " " + Blk(d, e) + " | 0x" + start.ToString("X") + " 0x" + loop.ToString("X") +
                    " 0x" + (end - start).ToString("X") + " | " + Blk(d, data + start) + " | " + Blk(d, data + end - 16));
            }

            sb.AppendLine("first 0x200 bytes of the sample data:");
            Hex(sb, d, data, 0x200, "  ");

            long dataEnd = Math.Min(d.Length, data + size);
            int total = 0, zero = 0, valid = 0, other = 0;
            var flags = new int[8];
            for (long p = data; p + 16 <= dataEnd; p += 16)
            {
                total++;
                if (Zero(d, p)) { zero++; continue; }
                if (Valid(d, p)) { valid++; flags[d[p + 1]]++; } else other++;
            }
            sb.AppendLine("data blocks: " + total + ", all-zero " + zero + ", valid ADPCM header " + valid + ", invalid " + other);
            var fl = new StringBuilder("flag counts 0..7:");
            for (int i = 0; i < 8; i++) fl.Append(' ').Append(flags[i]);
            sb.AppendLine(fl.ToString());
        }
    }
}
