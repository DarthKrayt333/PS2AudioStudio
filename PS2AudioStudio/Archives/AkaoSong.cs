using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PS2AudioStudio.Archives
{
    public class AkaoZone
    {
        public int SampleId;          // bank entry = SampleId - BaseId
        public int KeyLow, KeyHigh;
        public byte[] Raw;            // the 8 record bytes
    }

    public class AkaoNote { public int Start, End, Key; }
    public class AkaoCtrl { public int Tick, Cc, Value; }
    public class AkaoProg { public int Tick, Program; }

    public class AkaoTrack
    {
        public int Index, Channel, Ptr, End, ParsedEnd, OpCount;
        public bool Drum, JumpOk;
        public int Tempo = -1;
        public int LoopStart = -1;
        public int EndTick;
        public int Stack;
        public string Error, RepeatError;
        public List<AkaoNote> Notes = new List<AkaoNote>();
        public List<AkaoCtrl> Ctrls = new List<AkaoCtrl>();
        public List<AkaoProg> Progs = new List<AkaoProg>();
        public bool Ok { get { return Error == null && ParsedEnd == End; } }
    }

    public class AkaoSongData
    {
        public int Offset, Mask, ProgramTable, DrumTable, BaseId;
        public List<AkaoTrack> Tracks = new List<AkaoTrack>();
        public Dictionary<int, List<AkaoZone>> Zones = new Dictionary<int, List<AkaoZone>>();
        public byte[][] Drums = new byte[96][];
        public int TempoWord = -1;
        public int LoopStart = -1, LoopEnd;     // ticks, 48 per quarter note
        public int MaxSampleId = -1;

        public double AutoBpm { get { return TempoWord > 0 ? TempoWord * AkaoSongReader.BpmScale : 120.0; } }
        public int UsedBankEntries { get { return MaxSampleId >= BaseId ? MaxSampleId - BaseId + 1 : 0; } }
        public int NoteCount { get { return Tracks.Sum(t => t.Notes.Count); } }

        public double Seconds(int ticks, double bpm) { return ticks / 48.0 * 60.0 / bpm; }

        public string Summary(double bpm)
        {
            var sb = new StringBuilder();
            sb.AppendLine("AKAO song at 0x" + Offset.ToString("X") + ": " + Tracks.Count + " tracks, " + NoteCount + " notes, base sample id 0x" + BaseId.ToString("X"));
            int bad = Tracks.Count(t => !t.Ok);
            sb.AppendLine("  tracks parsed cleanly: " + (Tracks.Count - bad) + "/" + Tracks.Count);
            sb.AppendLine("  tempo word " + TempoWord + " -> " + bpm.ToString("F1") + " bpm (assumed scale, change it with the Tempo button)");
            if (LoopStart >= 0)
                sb.AppendLine("  loop " + LoopStart + " -> " + LoopEnd + " ticks  (intro " + Seconds(LoopStart, bpm).ToString("F1") + "s, loop " +
                              Seconds(LoopEnd - LoopStart, bpm).ToString("F1") + "s)");
            else sb.AppendLine("  no loop found, length " + Seconds(LoopEnd, bpm).ToString("F1") + "s");
            sb.AppendLine("  programs: " + Zones.Count + ", drum slots: " + Drums.Count(r => r != null && r.Any(v => v != 0)) +
                          ", highest sample id used 0x" + MaxSampleId.ToString("X") + " (" + UsedBankEntries + " bank entries)");
            return sb.ToString();
        }
    }

    public static class AkaoSongReader
    {
        public const double BpmScale = 300.0 / 65536.0;     // bpm = tempo word * this (assumption, see the Tempo button)

        private static readonly int[] Dur = { 192, 96, 48, 24, 12, 6, 3, 32, 16, 8, 4 };
        private static readonly Dictionary<int, int> Args = new Dictionary<int, int>
        {
            {0xA0,0},{0xA3,1},{0xA4,1},{0xA5,1},{0xA6,0},{0xA7,0},{0xA8,1},{0xA9,2},
            {0xAA,1},{0xAD,1},{0xB2,1},{0xB4,3},{0xB5,1},{0xC2,0},{0xC8,0},{0xC9,1}
        };
        private static readonly Dictionary<int, int> FeArgs = new Dictionary<int, int>
        {
            {0x00,2},{0x02,2},{0x04,0},{0x06,2},{0x14,1},{0x15,2},{0x16,2}
        };

        private sealed class Op
        {
            public int Addr, Kind, A, B, Code;      // Kind: 0 note (A = note in octave, B = ticks), 1 rest (A), 2 tie (A), 3 command
            public int[] Args;
        }

        private static int U16(byte[] d, int p) { return p < 0 || p + 2 > d.Length ? 0 : d[p] | (d[p + 1] << 8); }
        private static long U32(byte[] d, int p) { return p < 0 || p + 4 > d.Length ? 0 : BitConverter.ToUInt32(d, p); }

        public static List<AkaoSongData> Find(byte[] x)
        {
            x = AkaoFix.PatchNoDrum(x);
            var res = new List<AkaoSongData>();
            if (x == null) return res;
            var bankMarks = new List<int>();
            var songMarks = new List<int>();
            var maybeSongs = new List<int>();       // frames that look like a bank but also have a channel mask
            for (int p = 0; p + 4 <= x.Length; p++)
            {
                if (x[p] != 0x41 || x[p + 1] != 0x4B || x[p + 2] != 0x41 || x[p + 3] != 0x4F) continue;
                long cnt = U32(x, p + 0x1C), sz = U32(x, p + 0x14);
                bool hasMask = U16(x, p + 0x20) != 0;
                if (cnt >= 1 && cnt <= 512 && sz >= 32) { bankMarks.Add(p); if (hasMask) maybeSongs.Add(p); }
                else if (hasMask) songMarks.Add(p);
            }
            // a "bank-looking" frame that really parses as a song with notes IS a song
            var rescued = new List<AkaoSongData>();
            foreach (int s in maybeSongs)
            {
                try
                {
                    var d = Parse(x, s, 0);
                    if (d != null && d.NoteCount > 0) { rescued.Add(d); bankMarks.Remove(s); }
                }
                catch { }
            }
            int bank = bankMarks.Count > 0 ? bankMarks[0] : -1;
            int baseId = bank >= 0 ? (int)U32(x, bank + 0x18) : 0;
            foreach (var d in rescued) { d.BaseId = baseId; res.Add(d); }
            foreach (int s in songMarks)
            {
                try
                {
                    var d = Parse(x, s, baseId);
                    if (d != null) res.Add(d);
                }
                catch { }
            }
            res.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return res;
        }

        /// <summary>Text report: for every AKAO frame, why it is or is not accepted as a song.</summary>
        public static string Explain(byte[] x)
        {
            x = AkaoFix.PatchNoDrum(x);
            var sb = new StringBuilder();
            if (x == null) return "";
            int frames = 0;
            for (int p = 0; p + 4 <= x.Length; p++)
            {
                if (x[p] != 0x41 || x[p + 1] != 0x4B || x[p + 2] != 0x41 || x[p + 3] != 0x4F) continue;
                if (frames == 0) sb.AppendLine("--- AKAO song probe (why songs are or are not recognised) ---");
                if (++frames > 12) { sb.AppendLine("(more AKAO frames not listed)"); break; }
                long cnt = U32(x, p + 0x1C), sz = U32(x, p + 0x14);
                int mask = U16(x, p + 0x20);
                sb.AppendLine("Frame at 0x" + p.ToString("X") + ": id " + U16(x, p + 4) + ", length 0x" + U16(x, p + 6).ToString("X") +
                              ", [+0x14]=" + sz + ", [+0x1C]=" + cnt + ", channel mask [+0x20]=0x" + mask.ToString("X4"));
                var hx = new StringBuilder("  header: ");
                for (int i = 0; i < 0x50 && p + i < x.Length; i++) hx.Append(x[p + i].ToString("X2")).Append(' ');
                sb.AppendLine(hx.ToString());
                if (cnt >= 1 && cnt <= 512 && sz >= 32) sb.AppendLine("  this frame looks like a SAMPLE BANK to the scanner");
                if (mask == 0) { sb.AppendLine("  channel mask is 0: end marker, not a song."); continue; }
                int nch = 0;
                for (int i = 0; i < 16; i++) if (((mask >> i) & 1) != 0) nch++;
                long pt = p + 0x30 + U32(x, p + 0x30), dt = p + 0x34 + U32(x, p + 0x34);
                sb.AppendLine("  " + nch + " channel(s), program table at 0x" + pt.ToString("X") + ", drum table at 0x" + dt.ToString("X"));
                if (pt <= p || pt >= x.Length) { sb.AppendLine("  REJECTED: the program table is outside the file."); continue; }
                if (dt <= pt || dt + 96 * 8 > x.Length) { sb.AppendLine("  REJECTED: the drum table is not after the program table, or runs past the end."); continue; }
                bool bad = false;
                var pl = new List<int>();
                for (int n = 0; n < nch; n++)
                {
                    int q = p + 0x40 + 2 * n;
                    int ptr = q + U16(x, q);
                    sb.AppendLine("  track " + n + ": pointer 0x" + ptr.ToString("X"));
                    if (ptr < p + 0x40 || ptr >= pt) { sb.AppendLine("  REJECTED: track pointer outside the track area (0x" + (p + 0x40).ToString("X") + " - 0x" + pt.ToString("X") + ")."); bad = true; break; }
                    if (n > 0 && ptr < pl[n - 1]) { sb.AppendLine("  REJECTED: the track pointers are not in ascending order."); bad = true; break; }
                    pl.Add(ptr);
                }
                if (bad) continue;
                try
                {
                    var d = Parse(x, p, 0);
                    if (d == null) { sb.AppendLine("  Parse() returned nothing."); continue; }
                    sb.Append("  PARSED: " + d.Summary(d.AutoBpm));
                    foreach (var t in d.Tracks)
                        sb.AppendLine("    track " + t.Index + ": 0x" + t.Ptr.ToString("X") + "-0x" + t.End.ToString("X") + ", parsed to 0x" + t.ParsedEnd.ToString("X") +
                                      ", " + t.Notes.Count + " notes" + (t.Error != null ? ", ERROR: " + t.Error : ""));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            return sb.ToString();
        }

        private static AkaoSongData Parse(byte[] x, int S, int baseId)
        {
            int mask = U16(x, S + 0x20);
            var chans = new List<int>();
            for (int i = 0; i < 16; i++) if (((mask >> i) & 1) != 0) chans.Add(i);
            if (chans.Count == 0) return null;

            long pt = S + 0x30 + U32(x, S + 0x30);
            long dt = S + 0x34 + U32(x, S + 0x34);
            if (pt <= S || pt >= x.Length || dt <= pt || dt + 96 * 8 > x.Length) return null;
            int progTab = (int)pt, drumTab = (int)dt;

            var ptrs = new List<int>();
            for (int n = 0; n < chans.Count; n++)
            {
                int q = S + 0x40 + 2 * n;
                int ptr = q + U16(x, q);
                if (ptr < S + 0x40 || ptr >= progTab) return null;
                ptrs.Add(ptr);
            }
            var ends = new List<int>(ptrs.Skip(1)); ends.Add(progTab);

            var d = new AkaoSongData { Offset = S, Mask = mask, ProgramTable = progTab, DrumTable = drumTab, BaseId = baseId };
            for (int i = 0; i < ptrs.Count; i++)
            {
                if (ends[i] < ptrs[i]) return null;
                string err;
                int pe;
                var ops = ParseTrack(x, ptrs[i], ends[i], out pe, out err);
                var t = new AkaoTrack { Index = i, Ptr = ptrs[i], End = ends[i], ParsedEnd = pe, Error = err, OpCount = ops.Count };
                Play(ops, t);
                d.Tracks.Add(t);
            }

            var free = new List<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15 };
            foreach (var t in d.Tracks)
            {
                if (t.Drum) t.Channel = 9;
                else if (free.Count > 0) { t.Channel = free[0]; free.RemoveAt(0); }
                else t.Channel = 15;
            }

            for (int k = 0; k < 96; k++)
            {
                var rec = new byte[8];
                Array.Copy(x, drumTab + 8 * k, rec, 0, 8);
                d.Drums[k] = rec;
                if (rec.Any(v => v != 0)) d.MaxSampleId = Math.Max(d.MaxSampleId, rec[0]);
            }

            int nprog = 1;
            foreach (var t in d.Tracks) foreach (var pr in t.Progs) nprog = Math.Max(nprog, pr.Program + 1);
            nprog = Math.Min(nprog, 64);
            int zoneBase = progTab + 0x20;
            for (int pr = 0; pr < nprog; pr++)
            {
                int q = zoneBase + U16(x, progTab + 2 * pr);
                var lst = new List<AkaoZone>();
                while (q >= 0 && q + 8 <= drumTab && lst.Count < 32)
                {
                    var rec = new byte[8];
                    Array.Copy(x, q, rec, 0, 8);
                    if (rec.All(v => v == 0)) break;
                    lst.Add(new AkaoZone { SampleId = rec[0], KeyLow = rec[1], KeyHigh = rec[2], Raw = rec });
                    d.MaxSampleId = Math.Max(d.MaxSampleId, rec[0]);
                    q += 8;
                }
                d.Zones[pr] = lst;
            }

            var tw = d.Tracks.FirstOrDefault(t => t.Tempo >= 0);
            d.TempoWord = tw != null ? tw.Tempo : -1;

            var gs = d.Tracks.Where(t => t.LoopStart >= 0).GroupBy(t => t.LoopStart).OrderByDescending(g => g.Count()).FirstOrDefault();
            var ge = d.Tracks.GroupBy(t => t.EndTick).OrderByDescending(g => g.Count()).FirstOrDefault();
            d.LoopEnd = ge != null ? ge.Key : 0;
            d.LoopStart = gs != null && gs.Key < d.LoopEnd ? gs.Key : -1;
            return d;
        }

        private static List<Op> ParseTrack(byte[] x, int start, int end, out int pe, out string err)
        {
            var ops = new List<Op>();
            int p = start;
            err = null;
            if (end > x.Length) end = x.Length;
            while (p < end)
            {
                int b = x[p];
                if (b == 0xFF)
                {
                    bool allFf = true;
                    for (int i = p; i < end; i++) if (x[i] != 0xFF) { allFf = false; break; }
                    if (allFf) p = end; else err = "stray FF at 0x" + p.ToString("X");
                    break;
                }
                if (b <= 0x83) { ops.Add(new Op { Addr = p, Kind = 0, A = b / 11, B = Dur[b % 11] }); p++; continue; }
                if (b <= 0x8E) { ops.Add(new Op { Addr = p, Kind = 1, A = Dur[b - 0x84] }); p++; continue; }
                if (b <= 0x99) { ops.Add(new Op { Addr = p, Kind = 2, A = Dur[b - 0x8F] }); p++; continue; }
                if (b >= 0xF0 && b <= 0xFD)
                {
                    if (p + 2 > end) { err = "truncated at 0x" + p.ToString("X"); break; }
                    int a = x[p + 1];
                    if (b <= 0xFB) ops.Add(new Op { Addr = p, Kind = 0, A = b - 0xF0, B = a });
                    else if (b == 0xFC) ops.Add(new Op { Addr = p, Kind = 1, A = a });
                    else ops.Add(new Op { Addr = p, Kind = 2, A = a });
                    p += 2; continue;
                }
                if (b == 0xFE)
                {
                    if (p + 2 > end) { err = "truncated at 0x" + p.ToString("X"); break; }
                    int sub = x[p + 1], n;
                    if (!FeArgs.TryGetValue(sub, out n)) { err = "unknown opcode FE " + sub.ToString("X2") + " at 0x" + p.ToString("X"); break; }
                    if (p + 2 + n > end) { err = "truncated at 0x" + p.ToString("X"); break; }
                    var args = new int[n];
                    for (int i = 0; i < n; i++) args[i] = x[p + 2 + i];
                    ops.Add(new Op { Addr = p, Kind = 3, Code = 0xFE00 | sub, Args = args });
                    p += 2 + n; continue;
                }
                int na;
                if (!Args.TryGetValue(b, out na)) { err = "unknown opcode " + b.ToString("X2") + " at 0x" + p.ToString("X"); break; }
                if (p + 1 + na > end) { err = "truncated at 0x" + p.ToString("X"); break; }
                var ar = new int[na];
                for (int i = 0; i < na; i++) ar[i] = x[p + 1 + i];
                ops.Add(new Op { Addr = p, Kind = 3, Code = b, Args = ar });
                p += 1 + na;
            }
            pe = p;
            return ops;
        }

        private static void Play(List<Op> ops, AkaoTrack r)
        {
            var idx = new Dictionary<int, int>();
            for (int i = 0; i < ops.Count; i++) idx[ops[i].Addr] = i;
            int tick = 0, octv = 0;
            AkaoNote last = null;
            var stack = new List<int>();
            var counters = new Dictionary<int, int>();
            var visit = new Dictionary<int, int>();
            int ip = 0, steps = 0;
            while (ip < ops.Count)
            {
                if (++steps > 500000) { r.RepeatError = "runaway"; break; }
                var o = ops[ip];
                if (!visit.ContainsKey(ip)) visit[ip] = tick;
                if (o.Kind == 0)
                {
                    last = new AkaoNote { Start = tick, End = tick + o.B, Key = octv * 12 + o.A };
                    r.Notes.Add(last);
                    tick += o.B;
                }
                else if (o.Kind == 1) tick += o.A;
                else if (o.Kind == 2)
                {
                    if (last != null && last.End == tick) last.End += o.A;
                    tick += o.A;
                }
                else
                {
                    int code = o.Code;
                    var a = o.Args;
                    if (code == 0xA0) break;
                    else if (code == 0xA5) octv = a[0];
                    else if (code == 0xA6) octv++;
                    else if (code == 0xA7) octv--;
                    else if (code == 0xA3) r.Ctrls.Add(new AkaoCtrl { Tick = tick, Cc = 7, Value = a[0] });
                    else if (code == 0xA8) r.Ctrls.Add(new AkaoCtrl { Tick = tick, Cc = 11, Value = a[0] });
                    else if (code == 0xAA) r.Ctrls.Add(new AkaoCtrl { Tick = tick, Cc = 10, Value = a[0] });
                    else if (code == 0xFE14) r.Progs.Add(new AkaoProg { Tick = tick, Program = a[0] });
                    else if (code == 0xFE04) r.Drum = true;
                    else if (code == 0xFE00) r.Tempo = a[0] | (a[1] << 8);
                    else if (code == 0xC8) stack.Add(ip + 1);
                    else if (code == 0xC9)
                    {
                        int cnt;
                        counters.TryGetValue(ip, out cnt);
                        cnt++;
                        if (cnt < a[0] && stack.Count > 0)
                        {
                            counters[ip] = cnt;
                            ip = stack[stack.Count - 1];
                            continue;
                        }
                        counters.Remove(ip);
                        if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                        else r.RepeatError = "C9 without C8";
                    }
                    else if (code == 0xFE06)
                    {
                        int rel = a[0] | (a[1] << 8);
                        if (rel >= 32768) rel -= 65536;
                        int tgt = o.Addr + 2 + rel, ti;
                        if (idx.TryGetValue(tgt, out ti) && visit.ContainsKey(ti)) { r.LoopStart = visit[ti]; r.JumpOk = true; }
                        break;
                    }
                }
                ip++;
            }
            r.EndTick = tick;
            r.Stack = stack.Count;
        }
    }

    /// <summary>Writes the decoded AKAO song as a Standard MIDI File (format 1, 480 per quarter note).</summary>
    public static class AkaoMidi
    {
        private const int OutMul = 10;                 // AKAO has 48 ticks per quarter note -> 480
        private static readonly int[] GmProg = { 73, 40, 48, 32, 89, 88, 91, 49, 52, 50, 0, 0, 0, 0, 0, 0 };

        private sealed class Ev { public int Tick, Order; public byte[] Msg; }

        /// <summary>gm = true: General MIDI instruments (to listen / edit in a DAW). gm = false: raw AKAO program numbers,
        /// drums on channel 10 as bank 1 / program 0 with the drum SLOT number as the key (the form that goes with the HD).</summary>
        public static byte[] Build(AkaoSongData d, bool gm, double bpm)
        {
            if (bpm < 10) bpm = 10;
            if (bpm > 600) bpm = 600;
            int us = (int)Math.Round(60000000.0 / bpm);

            var chunks = new List<byte[]>();
            chunks.Add(Mtrk(new List<Ev> { new Ev { Tick = 0, Order = 0, Msg = new byte[] { 0xFF, 0x51, 0x03, (byte)(us >> 16), (byte)(us >> 8), (byte)us } } }));
            bool markerDone = false;
            foreach (var t in d.Tracks)
            {
                chunks.Add(Mtrk(TrackEvents(t, gm, d, !markerDone && !t.Drum)));
                if (!t.Drum) markerDone = true;
            }

            int division = OutMul * 48;     // 480 ticks per quarter note
            var o = new List<byte>();
            o.AddRange(Encoding.ASCII.GetBytes("MThd"));
            o.AddRange(new byte[] { 0, 0, 0, 6, 0, 1, (byte)(chunks.Count >> 8), (byte)chunks.Count, (byte)(division >> 8), (byte)(division & 0xFF) });
            foreach (var c in chunks) o.AddRange(c);
            return o.ToArray();
        }

        private static List<Ev> TrackEvents(AkaoTrack t, bool gm, AkaoSongData d, bool markers)
        {
            var ev = new List<Ev>();
            int ch = t.Channel;
            if (t.Drum)
            {
                if (!gm) ev.Add(new Ev { Tick = 0, Order = 1, Msg = new byte[] { (byte)(0xB0 | ch), 0, 1 } });
                ev.Add(new Ev { Tick = 0, Order = 1, Msg = new byte[] { (byte)(0xC0 | ch), 0 } });
            }
            else
            {
                foreach (var p in t.Progs)
                {
                    int prog = gm ? GmProg[p.Program % 16] : p.Program;
                    ev.Add(new Ev { Tick = p.Tick * OutMul, Order = 1, Msg = new byte[] { (byte)(0xC0 | ch), (byte)(prog & 0x7F) } });
                }
            }
            foreach (var c in t.Ctrls)
                ev.Add(new Ev { Tick = c.Tick * OutMul, Order = 1, Msg = new byte[] { (byte)(0xB0 | ch), (byte)c.Cc, (byte)Math.Max(0, Math.Min(127, c.Value)) } });

            if (markers && d.LoopStart >= 0)
            {
                ev.Add(new Ev { Tick = d.LoopStart * OutMul, Order = 1, Msg = new byte[] { (byte)(0xB0 | ch), 99, 0 } });
                ev.Add(new Ev { Tick = d.LoopEnd * OutMul, Order = 1, Msg = new byte[] { (byte)(0xB0 | ch), 99, 1 } });
            }

            foreach (var n in t.Notes)
            {
                int k = n.Key;
                if (t.Drum)
                {
                    if (k < 0 || k >= 96 || d.Drums[k] == null || d.Drums[k].All(v => v == 0)) continue;
                    if (gm) { k = (k >= 35 && k <= 81) ? k : k - 48; k = Math.Max(35, Math.Min(81, k)); }
                }
                k = Math.Max(0, Math.Min(127, k));
                ev.Add(new Ev { Tick = n.Start * OutMul, Order = 2, Msg = new byte[] { (byte)(0x90 | ch), (byte)k, 100 } });
                ev.Add(new Ev { Tick = n.End * OutMul, Order = 0, Msg = new byte[] { (byte)(0x80 | ch), (byte)k, 0 } });
            }
            return ev;
        }

        private static byte[] Mtrk(List<Ev> ev)
        {
            var sorted = ev.OrderBy(e => e.Tick).ThenBy(e => e.Order).ToList();   // stable
            var data = new List<byte>();
            int t = 0;
            foreach (var e in sorted)
            {
                Vlq(data, e.Tick - t);
                data.AddRange(e.Msg);
                t = e.Tick;
            }
            data.Add(0); data.Add(0xFF); data.Add(0x2F); data.Add(0);
            var o = new List<byte>();
            o.AddRange(Encoding.ASCII.GetBytes("MTrk"));
            int len = data.Count;
            o.Add((byte)(len >> 24)); o.Add((byte)(len >> 16)); o.Add((byte)(len >> 8)); o.Add((byte)len);
            o.AddRange(data);
            return o.ToArray();
        }

        private static void Vlq(List<byte> o, int n)
        {
            if (n < 0) n = 0;
            var st = new Stack<byte>();
            st.Push((byte)(n & 0x7F));
            n >>= 7;
            while (n > 0) { st.Push((byte)((n & 0x7F) | 0x80)); n >>= 7; }
            o.AddRange(st);
        }
    }
}
