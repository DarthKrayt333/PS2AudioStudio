using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PS2AudioStudio.Archives
{
    /// <summary>VAGI table entry: where a VAG sits in the BD.</summary>
    public class HdVag
    {
        public uint BdOffset;
        public ushort Rate;
        public byte Attribute;          // 0 = one-shot, 1 = loop
        public byte Pad = 0xFF;

        internal byte[] ToBytes()
        {
            var b = new byte[8];
            Array.Copy(BitConverter.GetBytes(BdOffset), 0, b, 0, 4);
            Array.Copy(BitConverter.GetBytes(Rate), 0, b, 4, 2);
            b[6] = Attribute; b[7] = Pad;
            return b;
        }
    }

    /// <summary>SMPL entry (42 bytes). Unknown bytes are kept exactly as read.</summary>
    public class HdParam
    {
        public byte[] Raw = new byte[HdFile.ParamSize];
        public int VagIndex
        {
            get { return Raw[0] | (Raw[1] << 8); }
            set { Raw[0] = (byte)value; Raw[1] = (byte)(value >> 8); }
        }
    }

    /// <summary>SSET entry: a velocity range and a list of SMPL indexes.</summary>
    public class HdSet
    {
        public byte VelCurve, VelLow = 1, VelHigh = 127;
        public List<ushort> Params = new List<ushort>();

        internal byte[] ToBytes()
        {
            var b = new byte[4 + 2 * Params.Count];
            b[0] = VelCurve; b[1] = VelLow; b[2] = VelHigh; b[3] = (byte)Params.Count;
            for (int i = 0; i < Params.Count; i++)
            {
                b[4 + i * 2] = (byte)Params[i];
                b[5 + i * 2] = (byte)(Params[i] >> 8);
            }
            return b;
        }
    }

    /// <summary>One split of a program (20 bytes). Unknown bytes are kept.</summary>
    public class HdSplit
    {
        public byte[] Raw;
        public int SetIndex
        {
            get { return Raw[0] | (Raw[1] << 8); }
            set { Raw[0] = (byte)value; Raw[1] = (byte)(value >> 8); }
        }
        public int KeyLow { get { return Raw[2]; } set { Raw[2] = (byte)value; } }
        public int KeyHigh { get { return Raw[4]; } set { Raw[4] = (byte)value; } }
    }

    public class HdProgram
    {
        public byte[] Header;                       // 36 bytes in the known files
        public int SplitSize = 20;
        public List<HdSplit> Splits = new List<HdSplit>();

        internal byte[] ToBytes()
        {
            var hdr = (byte[])Header.Clone();
            Array.Copy(BitConverter.GetBytes((uint)hdr.Length), 0, hdr, 0, 4);
            hdr[4] = (byte)Splits.Count;
            hdr[5] = (byte)SplitSize;
            var b = new byte[hdr.Length + Splits.Count * SplitSize];
            Array.Copy(hdr, b, hdr.Length);
            for (int i = 0; i < Splits.Count; i++)
                Array.Copy(Splits[i].Raw, 0, b, hdr.Length + i * SplitSize, Math.Min(SplitSize, Splits[i].Raw.Length));
            return b;
        }
    }

    /// <summary>
    /// The whole HD file as an editable structure. Parse() reads it, Build() writes it again with every
    /// offset and size recomputed. Null list entries are empty slots (FF FF FF FF in the offset table).
    /// </summary>
    public class HdFile
    {
        public const int ParamSize = 42;

        public byte[] VersionBytes, HeadBytes, Gap, Tail;
        public uint BdSize;
        public List<HdVag> Vags = new List<HdVag>();
        public List<HdParam> Params = new List<HdParam>();
        public List<HdSet> Sets = new List<HdSet>();
        public List<HdProgram> Programs = new List<HdProgram>();
        private int[] order = { 0, 1, 2, 3 };       // chunk order: 0 VAGI, 1 SMPL, 2 SSET, 3 PROG

        private static uint U32(byte[] d, long o) { return BitConverter.ToUInt32(d, (int)o); }
        private static byte[] Slice(byte[] d, long off, long len)
        {
            var r = new byte[len]; Array.Copy(d, off, r, 0, len); return r;
        }
        private static void Need(byte[] h, long p, long len)
        {
            if (p < 0 || p + len > h.Length) throw new InvalidDataException("An HD entry points outside the file.");
        }

        private static int ChunkSlots(byte[] h, long addr, string sig)
        {
            if (addr < 0 || addr + 16 > h.Length) throw new InvalidDataException(sig + " chunk address is outside the file.");
            if (Encoding.ASCII.GetString(h, (int)addr, 8) != sig)
                throw new InvalidDataException("Chunk '" + sig + "' not found at 0x" + addr.ToString("X") + ".");
            long n = (long)U32(h, addr + 12) + 1;
            if (n < 0 || n > 20000 || addr + 16 + 4 * n > h.Length) throw new InvalidDataException(sig + " table is invalid.");
            return (int)n;
        }

        private static long EntryPos(byte[] h, long addr, int i)
        {
            uint rel = U32(h, addr + 16 + 4L * i);
            return rel == 0xFFFFFFFF ? -1 : addr + rel;
        }

        // ================================================================= parse
        public static HdFile Parse(byte[] h)
        {
            if (h == null || h.Length < 0x50) throw new InvalidDataException("HD file too small.");
            if (Encoding.ASCII.GetString(h, 0, 8) != "IECSsreV") throw new InvalidDataException("Not a SCEI HD file.");

            var f = new HdFile();
            int vers = (int)U32(h, 8);
            if (vers < 0x10 || vers > 0x100) vers = 0x10;
            if (vers + 16 > h.Length || Encoding.ASCII.GetString(h, vers, 8) != "IECSdaeH")
                throw new InvalidDataException("HD header chunk not found.");
            int hsz = (int)U32(h, vers + 8);
            if (hsz < 0x24 || hsz > 0x400 || vers + hsz > h.Length) throw new InvalidDataException("HD header chunk is invalid.");

            f.VersionBytes = Slice(h, 0, vers);
            f.HeadBytes = Slice(h, vers, hsz);
            f.BdSize = U32(h, vers + 16);
            long aProg = U32(h, vers + 20), aSet = U32(h, vers + 24), aSmp = U32(h, vers + 28), aVagi = U32(h, vers + 32);

            // ---- VAGI
            int n = ChunkSlots(h, aVagi, "IECSigaV");
            for (int i = 0; i < n; i++)
            {
                long p = EntryPos(h, aVagi, i);
                if (p < 0) { f.Vags.Add(null); continue; }
                Need(h, p, 8);
                f.Vags.Add(new HdVag { BdOffset = U32(h, p), Rate = BitConverter.ToUInt16(h, (int)p + 4), Attribute = h[p + 6], Pad = h[p + 7] });
            }

            // ---- SMPL
            n = ChunkSlots(h, aSmp, "IECSlpmS");
            for (int i = 0; i < n; i++)
            {
                long p = EntryPos(h, aSmp, i);
                if (p < 0) { f.Params.Add(null); continue; }
                Need(h, p, ParamSize);
                f.Params.Add(new HdParam { Raw = Slice(h, p, ParamSize) });
            }

            // ---- SSET
            n = ChunkSlots(h, aSet, "IECStesS");
            for (int i = 0; i < n; i++)
            {
                long p = EntryPos(h, aSet, i);
                if (p < 0) { f.Sets.Add(null); continue; }
                Need(h, p, 4);
                int cnt = h[p + 3];
                Need(h, p, 4 + 2 * cnt);
                var s = new HdSet { VelCurve = h[p], VelLow = h[p + 1], VelHigh = h[p + 2] };
                for (int j = 0; j < cnt; j++) s.Params.Add(BitConverter.ToUInt16(h, (int)p + 4 + j * 2));
                f.Sets.Add(s);
            }

            // ---- PROG
            n = ChunkSlots(h, aProg, "IECSgorP");
            for (int i = 0; i < n; i++)
            {
                long p = EntryPos(h, aProg, i);
                if (p < 0) { f.Programs.Add(null); continue; }
                Need(h, p, 8);
                int hdrLen = (int)U32(h, p);
                if (hdrLen < 8 || hdrLen > 0x400) hdrLen = 0x24;
                int cnt = h[p + 4];
                int sz = h[p + 5];
                if (sz < 20 || sz > 0x80) sz = 20;
                Need(h, p, hdrLen + (long)cnt * sz);
                var pr = new HdProgram { Header = Slice(h, p, hdrLen), SplitSize = sz };
                for (int j = 0; j < cnt; j++) pr.Splits.Add(new HdSplit { Raw = Slice(h, p + hdrLen + (long)j * sz, sz) });
                f.Programs.Add(pr);
            }

            // ---- chunk order, gap before the first chunk, tail after the last
            var spans = new List<KeyValuePair<long, int>>
            {
                new KeyValuePair<long, int>(aVagi, 0), new KeyValuePair<long, int>(aSmp, 1),
                new KeyValuePair<long, int>(aSet, 2), new KeyValuePair<long, int>(aProg, 3)
            };
            spans.Sort((a, b) => a.Key.CompareTo(b.Key));
            f.order = new int[4];
            for (int i = 0; i < 4; i++) f.order[i] = spans[i].Value;

            long[] addrs = { aVagi, aSmp, aSet, aProg };
            long first = long.MaxValue, lastEnd = 0;
            foreach (long a in addrs)
            {
                long end = a + U32(h, a + 8);
                if (a < first) first = a;
                if (end > lastEnd) lastEnd = end;
            }
            if (lastEnd > h.Length) throw new InvalidDataException("The HD is truncated (a chunk runs past the end of the file).");
            if (first > vers + hsz) f.Gap = Slice(h, vers + hsz, first - (vers + hsz));
            if (lastEnd < h.Length) f.Tail = Slice(h, lastEnd, h.Length - lastEnd);
            return f;
        }

        // ================================================================= build
        private static byte[] BuildChunk(string sig, List<byte[]> slots)
        {
            int n = slots.Count;
            int tbl = 16 + 4 * n;
            int dataLen = 0;
            foreach (var s in slots) if (s != null) dataLen += s.Length;
            int size = (tbl + dataLen + 15) / 16 * 16;

            var o = new byte[size];
            for (int i = 0; i < size; i++) o[i] = 0xFF;                       // table gaps and padding are FF
            Array.Copy(Encoding.ASCII.GetBytes(sig), 0, o, 0, 8);
            Array.Copy(BitConverter.GetBytes((uint)size), 0, o, 8, 4);
            Array.Copy(BitConverter.GetBytes((uint)(n - 1)), 0, o, 12, 4);    // highest index, not the count
            int pos = tbl;
            for (int i = 0; i < n; i++)
            {
                if (slots[i] == null) continue;
                Array.Copy(BitConverter.GetBytes((uint)pos), 0, o, 16 + 4 * i, 4);
                Array.Copy(slots[i], 0, o, pos, slots[i].Length);
                pos += slots[i].Length;
            }
            return o;
        }

        private byte[] BuildKind(int kind)
        {
            var slots = new List<byte[]>();
            switch (kind)
            {
                case 0: foreach (var v in Vags) slots.Add(v == null ? null : v.ToBytes()); return BuildChunk("IECSigaV", slots);
                case 1: foreach (var p in Params) slots.Add(p == null ? null : p.Raw); return BuildChunk("IECSlpmS", slots);
                case 2: foreach (var s in Sets) slots.Add(s == null ? null : s.ToBytes()); return BuildChunk("IECStesS", slots);
                default: foreach (var p in Programs) slots.Add(p == null ? null : p.ToBytes()); return BuildChunk("IECSgorP", slots);
            }
        }

        public byte[] Build(uint bdSize)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(VersionBytes, 0, VersionBytes.Length);
                ms.Write(HeadBytes, 0, HeadBytes.Length);
                if (Gap != null) ms.Write(Gap, 0, Gap.Length);
                var addr = new long[4];
                foreach (int kind in order)
                {
                    addr[kind] = ms.Position;
                    byte[] c = BuildKind(kind);
                    ms.Write(c, 0, c.Length);
                }
                if (Tail != null) ms.Write(Tail, 0, Tail.Length);

                byte[] o = ms.ToArray();
                int ho = VersionBytes.Length;
                Array.Copy(BitConverter.GetBytes((uint)o.Length), 0, o, ho + 12, 4);   // total HD size
                Array.Copy(BitConverter.GetBytes(bdSize), 0, o, ho + 16, 4);           // BD size
                Array.Copy(BitConverter.GetBytes((uint)addr[3]), 0, o, ho + 20, 4);    // PROG
                Array.Copy(BitConverter.GetBytes((uint)addr[2]), 0, o, ho + 24, 4);    // SSET
                Array.Copy(BitConverter.GetBytes((uint)addr[1]), 0, o, ho + 28, 4);    // SMPL
                Array.Copy(BitConverter.GetBytes((uint)addr[0]), 0, o, ho + 32, 4);    // VAGI
                return o;
            }
        }

        // ================================================================= tests
        private static int Used<T>(List<T> l) where T : class
        {
            int n = 0; foreach (var x in l) if (x != null) n++; return n;
        }

        public string Summary()
        {
            return $"VAG slots {Used(Vags)}/{Vags.Count}, sample params {Used(Params)}/{Params.Count}, sample sets {Used(Sets)}/{Sets.Count}, programs {Used(Programs)}/{Programs.Count}, BD size 0x{BdSize:X}";
        }

        public static string Compare(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length), diff = 0, firstAt = -1;
            for (int i = 0; i < n; i++)
                if (a[i] != b[i]) { diff++; if (firstAt < 0) firstAt = i; }
            if (diff == 0 && a.Length == b.Length) return $"IDENTICAL ({a.Length:N0} bytes)";
            string s = $"DIFFERENT: original {a.Length:N0} bytes, rebuilt {b.Length:N0} bytes, {diff} differing byte(s)";
            if (firstAt >= 0) s += $", first at 0x{firstAt:X} (original {a[firstAt]:X2}, rebuilt {b[firstAt]:X2})";
            return s;
        }
    }
}
