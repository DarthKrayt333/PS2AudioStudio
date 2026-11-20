using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PS2AudioStudio.Archives
{
    /// <summary>Standard MIDI file -> Sony PlayStation SEQ ("pQES", version 1). All tracks are merged into one.
    /// Kept: note on/off, controllers, program, pitch bend, tempo, end of track. Dropped: aftertouch, sysex, text.</summary>
    public static class PsxSeqWriter
    {
        private sealed class E { public long Tick; public int Order; public byte[] Data; }

        private static int ReadVlq(byte[] b, ref int p, int end)
        {
            int v = 0;
            for (int i = 0; i < 4 && p < end; i++)
            {
                int c = b[p++];
                v = (v << 7) | (c & 0x7F);
                if ((c & 0x80) == 0) return v;
            }
            return v;
        }

        private static void PutVlq(List<byte> o, long n)
        {
            if (n < 0) n = 0;
            var st = new Stack<byte>();
            st.Push((byte)(n & 0x7F));
            n >>= 7;
            while (n > 0) { st.Push((byte)((n & 0x7F) | 0x80)); n >>= 7; }
            o.AddRange(st);
        }

        public static byte[] FromSmf(byte[] smf)
        {
            if (smf == null || smf.Length < 14 || smf[0] != 'M' || smf[1] != 'T' || smf[2] != 'h' || smf[3] != 'd')
                throw new InvalidDataException("Not a standard MIDI file.");
            int hdrLen = (smf[4] << 24) | (smf[5] << 16) | (smf[6] << 8) | smf[7];
            int ntrk = (smf[10] << 8) | smf[11];
            int div = (smf[12] << 8) | smf[13];
            if ((div & 0x8000) != 0 || div == 0) throw new InvalidDataException("This MIDI file uses SMPTE timing, which a SEQ cannot store.");
            int p = 8 + hdrLen;
            var evs = new List<E>();
            int order = 0, tempo = 500000, tsN = 4, tsD = 2;
            bool haveTempo = false, haveTs = false;
            for (int t = 0; t < ntrk && p + 8 <= smf.Length; t++)
            {
                bool isTrk = smf[p] == 'M' && smf[p + 1] == 'T' && smf[p + 2] == 'r' && smf[p + 3] == 'k';
                int len = (smf[p + 4] << 24) | (smf[p + 5] << 16) | (smf[p + 6] << 8) | smf[p + 7];
                int end = (int)Math.Min((long)smf.Length, (long)p + 8 + len);
                int q = p + 8;
                p = (int)Math.Min((long)smf.Length, (long)p + 8 + len);
                if (!isTrk) continue;
                long tick = 0;
                int status = 0;
                while (q < end)
                {
                    tick += ReadVlq(smf, ref q, end);
                    if (q >= end) break;
                    int b = smf[q];
                    if (b == 0xFF)
                    {
                        q++;
                        if (q >= end) break;
                        int type = smf[q++];
                        int ml = ReadVlq(smf, ref q, end);
                        if (q + ml > end) break;
                        if (type == 0x2F) break;
                        if (type == 0x51 && ml == 3)
                        {
                            int tv = (smf[q] << 16) | (smf[q + 1] << 8) | smf[q + 2];
                            if (!haveTempo && tick == 0) { tempo = tv; haveTempo = true; }
                            else evs.Add(new E { Tick = tick, Order = order++, Data = new byte[] { 0xFF, 0x51, 3, smf[q], smf[q + 1], smf[q + 2] } });
                        }
                        else if (type == 0x58 && ml >= 2 && !haveTs) { tsN = smf[q]; tsD = smf[q + 1]; haveTs = true; }
                        q += ml;
                        status = 0;
                        continue;
                    }
                    if (b == 0xF0 || b == 0xF7)
                    {
                        q++;
                        int sl = ReadVlq(smf, ref q, end);
                        q += sl;
                        status = 0;
                        continue;
                    }
                    if (b >= 0xF1 && b <= 0xFE) { q++; continue; }
                    if (b >= 0x80) { status = b; q++; }
                    else if (status == 0) break;
                    int hi = status & 0xF0;
                    int need = (hi == 0xC0 || hi == 0xD0) ? 1 : 2;
                    if (q + need > end) break;
                    int d1 = smf[q] & 0x7F;
                    int d2 = need == 2 ? (smf[q + 1] & 0x7F) : 0;
                    q += need;
                    if (hi == 0x80 || hi == 0x90 || hi == 0xB0 || hi == 0xE0)
                        evs.Add(new E { Tick = tick, Order = order++, Data = new byte[] { (byte)status, (byte)d1, (byte)d2 } });
                    else if (hi == 0xC0)
                        evs.Add(new E { Tick = tick, Order = order++, Data = new byte[] { (byte)status, (byte)d1 } });
                }
            }
            var o = new List<byte>();
            o.AddRange(new byte[]
            {
                (byte)'p', (byte)'Q', (byte)'E', (byte)'S',
                0, 0, 0, 1,
                (byte)(div >> 8), (byte)div,
                (byte)(tempo >> 16), (byte)(tempo >> 8), (byte)tempo,
                (byte)tsN, (byte)tsD
            });
            long last = 0;
            foreach (var e in evs.OrderBy(x => x.Tick).ThenBy(x => x.Order))
            {
                PutVlq(o, e.Tick - last);
                last = e.Tick;
                o.AddRange(e.Data);
            }
            o.Add(0); o.Add(0xFF); o.Add(0x2F); o.Add(0);
            return o.ToArray();
        }
    }
}
