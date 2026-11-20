using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PS2AudioStudio.Archives
{
    public static class SmfSeq
    {
        private sealed class Ev
        {
            public long Tick; public int Order; public int N; public int Kind; public byte[] Raw;
        }

        public static byte[] FromMidi(byte[] d, out string report)
        {
            int p = IndexOfMThd(d);
            if (p < 0) throw new InvalidDataException("This is not a MIDI file (no MThd header).");
            int hl = (int)BE32(d, p + 4);
            int div = BE16(d, p + 12);
            if ((div & 0x8000) != 0) throw new NotSupportedException("SMPTE time division cannot be stored in a SEQ.");
            if (div == 0) throw new InvalidDataException("MIDI resolution is 0.");
            p += 8 + hl;

            var evs = new List<Ev>();
            long endTick = 0; int counter = 0, dropped = 0;
            bool haveTempo0 = false; uint tempo0 = 500000;
            int tsNum = 4, tsDen = 2; bool haveTs = false;

            while (p + 8 <= d.Length)
            {
                int len = (int)BE32(d, p + 4);
                bool isTrk = d[p] == 'M' && d[p + 1] == 'T' && d[p + 2] == 'r' && d[p + 3] == 'k';
                int pos = p + 8;
                int end = Math.Min(d.Length, pos + len);
                p = pos + len;
                if (!isTrk) continue;

                long tick = 0; int running = 0;
                while (pos < end)
                {
                    tick += ReadVlq(d, ref pos, end);
                    if (pos >= end) break;
                    int st = d[pos];
                    if (st < 0x80)
                    {
                        if (running == 0) throw new InvalidDataException("Bad running status at byte " + pos + ".");
                        st = running;
                    }
                    else { pos++; running = st < 0xF0 ? st : 0; }

                    if (st == 0xFF)
                    {
                        int type = d[pos++];
                        int n = (int)ReadVlq(d, ref pos, end);
                        if (type == 0x2F) { if (tick > endTick) endTick = tick; break; }
                        if (type == 0x51 && n == 3)
                        {
                            uint t = (uint)((d[pos] << 16) | (d[pos + 1] << 8) | d[pos + 2]);
                            if (!haveTempo0 && tick == 0) { haveTempo0 = true; tempo0 = t; }
                            else evs.Add(new Ev
                            {
                                Tick = tick,
                                Order = 1,
                                N = counter++,
                                Kind = 4,
                                Raw = new byte[] { 0xFF, 0x51, 0x03, d[pos], d[pos + 1], d[pos + 2] }
                            });
                        }
                        else if (type == 0x58 && n >= 2 && !haveTs) { tsNum = d[pos]; tsDen = d[pos + 1]; haveTs = true; }
                        pos += n;
                        continue;
                    }
                    if (st == 0xF0 || st == 0xF7)
                    {
                        int n = (int)ReadVlq(d, ref pos, end);
                        pos += n; dropped++;
                        continue;
                    }

                    int hi = st & 0xF0, ch = st & 0x0F;
                    int need = (hi == 0xC0 || hi == 0xD0) ? 1 : 2;
                    if (pos + need > end) break;
                    int a = d[pos], b = need == 2 ? d[pos + 1] : 0;
                    pos += need;

                    switch (hi)
                    {
                        case 0x80:
                            evs.Add(new Ev { Tick = tick, Order = 0, N = counter++, Kind = 0, Raw = new byte[] { (byte)(0x90 | ch), (byte)a, 0 } });
                            break;
                        case 0x90:
                            evs.Add(new Ev { Tick = tick, Order = b == 0 ? 0 : 3, N = counter++, Kind = 0, Raw = new byte[] { (byte)st, (byte)a, (byte)b } });
                            break;
                        case 0xB0:
                            evs.Add(new Ev { Tick = tick, Order = 1, N = counter++, Kind = 1, Raw = new byte[] { (byte)st, (byte)a, (byte)b } });
                            break;
                        case 0xC0:
                            evs.Add(new Ev { Tick = tick, Order = 1, N = counter++, Kind = 2, Raw = new byte[] { (byte)st, (byte)a } });
                            break;
                        case 0xE0:
                            evs.Add(new Ev { Tick = tick, Order = 1, N = counter++, Kind = 3, Raw = new byte[] { (byte)st, (byte)a, (byte)b } });
                            break;
                        default: dropped++; break;   // aftertouch: not supported by the Sony SEQ player
                    }
                    if (tick > endTick) endTick = tick;
                }
                if (tick > endTick) endTick = tick;
            }

            var sorted = evs.OrderBy(e => e.Tick).ThenBy(e => e.Order).ThenBy(e => e.N).ToList();
            var o = new List<byte>(sorted.Count * 4 + 32);
            o.AddRange(Encoding.ASCII.GetBytes("pQES"));
            o.AddRange(new byte[] { 0, 0, 0, 1 });
            o.Add((byte)(div >> 8)); o.Add((byte)div);
            o.Add((byte)(tempo0 >> 16)); o.Add((byte)(tempo0 >> 8)); o.Add((byte)tempo0);
            o.Add((byte)tsNum); o.Add((byte)tsDen);

            long last = 0;
            foreach (var e in sorted)
            {
                WriteVlq(o, e.Tick - last); last = e.Tick;
                o.AddRange(e.Raw);
            }
            long finalTick = Math.Max(last, endTick);
            WriteVlq(o, finalTick - last);
            o.Add(0xFF); o.Add(0x2F); o.Add(0x00);
            byte[] seq = o.ToArray();

            // read the SEQ back and compare with what was meant to be written
            var want = sorted.Select(e => e.Tick + ":" + Hex(e.Raw)).ToList();
            want.Add(finalTick + ":END");
            var got = ReadBack(seq);
            string check;
            if (want.Count != got.Count) check = "MISMATCH: wrote " + want.Count + " events, read back " + got.Count;
            else
            {
                int bad = -1;
                for (int i = 0; i < want.Count; i++) if (want[i] != got[i]) { bad = i; break; }
                check = bad < 0 ? "read-back check: IDENTICAL (" + want.Count + " events)"
                                : "MISMATCH at event " + bad + ": wrote " + want[bad] + ", read " + got[bad];
            }

            var sb = new StringBuilder();
            sb.AppendLine("MIDI -> SEQ");
            sb.AppendLine("resolution " + div + " ticks per quarter, start tempo " + tempo0 + " us per quarter (" + (60000000.0 / tempo0).ToString("0.0") + " bpm), time signature " + tsNum + "/" + (1 << tsDen));
            sb.AppendLine("notes " + sorted.Count(e => e.Kind == 0) + ", controllers " + sorted.Count(e => e.Kind == 1) +
                ", programs " + sorted.Count(e => e.Kind == 2) + ", pitch bends " + sorted.Count(e => e.Kind == 3) +
                ", extra tempo changes " + sorted.Count(e => e.Kind == 4));
            sb.AppendLine("not stored (the SEQ format has no place for them): " + dropped + " sysex/aftertouch events");
            sb.AppendLine(check);
            report = sb.ToString();
            return seq;
        }

        private static List<string> ReadBack(byte[] s)
        {
            var r = new List<string>();
            int p = 15; long tick = 0; int running = 0;
            while (p < s.Length)
            {
                tick += ReadVlq(s, ref p, s.Length);
                if (p >= s.Length) break;
                int st = s[p];
                if (st < 0x80) { st = running; if (st == 0) break; }
                else { p++; running = st < 0xF0 ? st : 0; }
                if (st == 0xFF)
                {
                    int type = s[p++];
                    int n = (int)ReadVlq(s, ref p, s.Length);
                    if (type == 0x2F) { r.Add(tick + ":END"); break; }
                    var raw = new byte[3 + n];
                    raw[0] = 0xFF; raw[1] = (byte)type; raw[2] = (byte)n;
                    Array.Copy(s, p, raw, 3, n);
                    p += n;
                    r.Add(tick + ":" + Hex(raw));
                    continue;
                }
                int hi = st & 0xF0;
                int need = (hi == 0xC0 || hi == 0xD0) ? 1 : 2;
                var m = new byte[1 + need];
                m[0] = (byte)st;
                for (int i = 0; i < need; i++) m[1 + i] = s[p + i];
                p += need;
                r.Add(tick + ":" + Hex(m));
            }
            return r;
        }

        private static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", ""); }

        private static int IndexOfMThd(byte[] d)
        {
            for (int i = 0; i + 4 <= d.Length; i++)
                if (d[i] == 'M' && d[i + 1] == 'T' && d[i + 2] == 'h' && d[i + 3] == 'd') return i;
            return -1;
        }

        private static uint BE32(byte[] d, int i) { return (uint)((d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3]); }
        private static int BE16(byte[] d, int i) { return (d[i] << 8) | d[i + 1]; }

        private static long ReadVlq(byte[] d, ref int pos, int end)
        {
            long v = 0;
            for (int i = 0; i < 4 && pos < end; i++)
            {
                int b = d[pos++];
                v = (v << 7) | (long)(b & 0x7F);
                if ((b & 0x80) == 0) break;
            }
            return v;
        }

        private static void WriteVlq(List<byte> o, long v)
        {
            if (v < 0) v = 0;
            uint x = (uint)v;
            var buf = new byte[5]; int n = 0;
            buf[n++] = (byte)(x & 0x7F); x >>= 7;
            while (x > 0) { buf[n++] = (byte)((x & 0x7F) | 0x80); x >>= 7; }
            for (int i = n - 1; i >= 0; i--) o.Add(buf[i]);
        }
    }
}
