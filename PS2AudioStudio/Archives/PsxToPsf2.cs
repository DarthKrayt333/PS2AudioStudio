using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PS2AudioStudio.Audio;

namespace PS2AudioStudio.Archives
{
    public sealed class PsxExport
    {
        public byte[] Hd, Bd;
        public SqSong Song;
        public string Report;
        public string Effects;          // one-line description of what was written for the effects
        public int ReverbMode = -1;     // PS2 reverb mode (libsd) the .psf's registers match, -1 = no known preset
        public int ReverbDepth;
        public int[] ReverbRegs;        // the 32 reverb registers the .psf programmed (measured)
        public int ReturnL, ReturnR;
        public bool ReverbOn;
    }

    /// <summary>Builds HD + BD + SQ from what the game's own sound driver did (PsxCapture): samples and envelopes from SPU RAM,
    /// one MIDI channel per sounding note, one program per sample/envelope, pitch as key + pitch bend, volume/pan from the voice volumes.</summary>
    public static class PsxToPsf2
    {
        private static readonly byte[] TplParam = { 0, 0, 1, 0x40, 0x7F, 0, 1, 0, 0, 1, 0, 0x3C, 0, 0x40, 0, 0x0A, 0xFF, 0xFF, 0xFF, 0xAF, 0xCB, 0x5F, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3 };
        private static readonly byte[] TplProgHdr = { 0x24, 0, 0, 0, 1, 0x14, 0x64, 0x40, 0, 0, 0, 0x3C, 0, 0xFF, 0, 0, 0, 0, 0, 0, 0x64, 0, 0x64, 0, 0x80, 0, 0x80, 0, 0x80, 0, 0x80, 0, 0, 0, 0, 0 };
        private static readonly byte[] TplSplit = { 0, 0, 0x0C, 0x3C, 0x77, 0, 0, 2, 0, 2, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0x64, 0x40, 0, 0 };

        // PS1 "Studio Large" reverb registers 1DC0..1DFE (as I remember the hardware docs; they equal the 32 values 120.psf writes)
        private static readonly int[] StudioLarge =
        {
            0x00E3, 0x00A9, 0x6F60, 0x4FA8, 0xBCE0, 0x4510, 0xBEF0, 0xA680, 0x5680, 0x52C0, 0x0DFB, 0x0B58, 0x0D09, 0x0A3C, 0x0BD9, 0x0973,
            0x0B59, 0x08DA, 0x08D9, 0x05E9, 0x07EC, 0x04B0, 0x06EF, 0x03D2, 0x05EA, 0x031D, 0x031C, 0x0238, 0x0154, 0x00AA, 0x8000, 0x8000
        };

        private const double TicksPerSecond = 960.0;     // 480 per quarter note at 120 bpm
        private const int Rate = 44100;                  // the PS1 SPU plays 0x1000 = 44100 Hz

        private sealed class Chg { public int Frame, Pitch, Vl, Vr; }

        private sealed class Nt
        {
            public int Voice, Addr, A1, A2, Rep, Start, End = -1, RelEnd = -1, StartPitch, StartVl, StartVr, Pitch, Vl, Vr, Prog, Ch;
            public bool Rev;

            public int Key;
            public string Sig;

            public List<Chg> Chgs = new List<Chg>();
        }

        private sealed class Ev { public int Tick, Order; public byte[] Msg; }

        private static double Sv(int x)
        {
            if ((x & 0x8000) != 0) return 0.0;
            x = (x & 0x7FFF) << 1;
            if (x >= 0x8000) x -= 0x10000;
            return x / 32768.0;
        }

        private static int Tick(int frame) { return (int)Math.Round(frame / (double)Rate * TicksPerSecond); }

        private static bool IsSilent(byte[] s)
        {
            for (int i = 0; i < s.Length; i++)
                if ((i & 15) >= 2 && s[i] != 0) return false;
            return true;
        }

        private static double MaxLevel(Nt n)
        {
            double m = Math.Max(Math.Abs(Sv(n.StartVl)), Math.Abs(Sv(n.StartVr)));
            foreach (var c in n.Chgs) m = Math.Max(m, Math.Max(Math.Abs(Sv(c.Vl)), Math.Abs(Sv(c.Vr))));
            return m;
        }

        private static int RefPitch(Nt n)
        {
            if (n.StartPitch > 0) return n.StartPitch;
            foreach (var c in n.Chgs) if (c.Pitch > 0) return c.Pitch;
            return 0;
        }

        private static int KeyOf(Nt n)
        {
            int key = 60 + (int)Math.Round(12.0 * Math.Log(RefPitch(n) / 4096.0, 2.0));
            if (key < 0) key = 0;
            if (key > 127) key = 127;
            return key;
        }
        private static string StateSig(int key, int pitch, int vl, int vr)
        {
            int bv = -1;
            if (pitch > 0)
            {
                double bs = 12.0 * Math.Log(pitch / 4096.0, 2.0) - (key - 60);
                if (bs < -2.0) bs = -2.0;
                if (bs > 2.0) bs = 2.0;
                bv = 8192 + (int)Math.Round(bs / 2.0 * 8192.0);
                if (bv < 0) bv = 0;
                if (bv > 16383) bv = 16383;
            }
            double lv = Math.Abs(Sv(vl)), rv = Math.Abs(Sv(vr));
            double g = Math.Sqrt(lv * lv + rv * rv);
            if (g > 1.0) g = 1.0;
            int vol = (int)Math.Round(127.0 * Math.Sqrt(g));
            int pan = -1;
            if (g > 1e-6)
            {
                pan = (int)Math.Round(Math.Atan2(rv, lv) / (Math.PI / 2.0) * 127.0);
                if (pan < 0) pan = 0;
                if (pan > 127) pan = 127;
            }
            return bv + "/" + vol + "/" + pan;
        }
        /// <summary>Signature of the note's channel state if it stays the same for the whole note, otherwise null.</summary>
        private static string StaticSig(Nt n)
        {
            int key = KeyOf(n);
            int lastPitch = n.StartPitch > 0 ? n.StartPitch : RefPitch(n);
            string s0 = StateSig(key, lastPitch, n.StartVl, n.StartVr);
            foreach (var c in n.Chgs)
            {
                if (c.Pitch > 0) lastPitch = c.Pitch;
                if (StateSig(key, lastPitch, c.Vl, c.Vr) != s0) return null;
            }
            return n.Prog + "|" + s0;
        }

        public static PsxExport Build(PsxCapture cap, double seconds, double loopStartSec, double loopEndSec, bool effects, string name)
        {
            if (cap == null) throw new InvalidDataException("The driver run produced no capture.");
            int total = (int)Math.Round(seconds * Rate);

            // ---- 1. notes from the log (End = key-off, RelEnd = the frame the driver's voice was really finished)
            var notes = new List<Nt>();
            var open = new Nt[24];
            var last = new Nt[24];
            int keyOns = 0;
            foreach (var e in cap.Events)
            {
                if (e.Voice < 0 || e.Voice >= 24) continue;
                int v = e.Voice;
                Nt cur = open[v];
                if (e.Kind == 1)
                {
                    keyOns++;
                    if (cur != null) { cur.End = e.Frame; cur.RelEnd = e.Frame; }
                    else if (last[v] != null && last[v].RelEnd < 0) last[v].RelEnd = e.Frame;
                    var nt = new Nt
                    {
                        Voice = v,
                        Addr = e.Addr,
                        A1 = e.A1,
                        A2 = e.A2,
                        Rep = e.Rep,
                        Rev = e.Rev,
                        Start = e.Frame,
                        StartPitch = e.Pitch,
                        StartVl = e.Vl,
                        StartVr = e.Vr,
                        Pitch = e.Pitch,
                        Vl = e.Vl,
                        Vr = e.Vr
                    };
                    notes.Add(nt);
                    open[v] = nt;
                    last[v] = nt;
                }
                else if (e.Kind == 5)
                {
                    if (cur != null) { cur.End = e.Frame; cur.RelEnd = e.Frame; open[v] = null; }
                    else if (last[v] != null && last[v].RelEnd < 0) last[v].RelEnd = e.Frame;
                }
                else if (cur == null) continue;
                else if (e.Kind == 2) { cur.End = e.Frame; open[v] = null; }
                else if (e.Kind == 3) { cur.Pitch = e.Pitch; cur.Chgs.Add(new Chg { Frame = e.Frame, Pitch = cur.Pitch, Vl = cur.Vl, Vr = cur.Vr }); }
                else if (e.Kind == 4) { cur.Vl = e.Vl; cur.Vr = e.Vr; cur.Chgs.Add(new Chg { Frame = e.Frame, Pitch = cur.Pitch, Vl = cur.Vl, Vr = cur.Vr }); }
            }
            foreach (var o in notes)
            {
                if (o.End < 0) o.End = total;
                if (o.RelEnd < 0) o.RelEnd = Math.Max(o.End, total);     // never reported as finished: busy to the end
                if (o.RelEnd < o.End) o.RelEnd = o.End;
            }

            // ---- 2. keep the audible ones; programs = (sample, ADSR1, ADSR2), VAGs = sample addresses
            var keep = new List<Nt>();
            var progOf = new Dictionary<string, int>();
            var progs = new List<int[]>();
            var vagOf = new Dictionary<int, int>();
            var vagAddr = new List<int>();
            var repOf = new Dictionary<int, int>();
            int dropNoSample = 0, dropSilent = 0, dropPitch = 0, dropLate = 0;
            bool wet = effects && cap.ReverbEnabled;

            foreach (var n in notes)
            {
                if (n.Start >= total) { dropLate++; continue; }
                byte[] smp;
                if (!cap.Samples.TryGetValue(n.Addr, out smp) || smp == null) { dropNoSample++; continue; }
                if (IsSilent(smp) || MaxLevel(n) <= 0.0) { dropSilent++; continue; }
                if (RefPitch(n) <= 0) { dropPitch++; continue; }

                int wetFlag = (wet && n.Rev) ? 1 : 0;
                string pk = n.Addr + "|" + n.A1 + "|" + n.A2 + "|" + wetFlag;
                int pi;
                if (!progOf.TryGetValue(pk, out pi)) { pi = progs.Count; progOf[pk] = pi; progs.Add(new[] { n.Addr, n.A1, n.A2, wetFlag }); }

                n.Prog = pi;
                if (!vagOf.ContainsKey(n.Addr)) { vagOf[n.Addr] = vagAddr.Count; vagAddr.Add(n.Addr); repOf[n.Addr] = n.Rep; }
                keep.Add(n);
            }
            if (keep.Count == 0) throw new InvalidDataException("The driver played no audible notes in this length.");

            var chFree = new int[16];
            var chAll = new List<Nt>[16];
            for (int c = 0; c < 16; c++) chAll[c] = new List<Nt>();
            int stolen = 0, shared = 0;
            double firstSteal = -1;
            foreach (var n in keep)
            {
                n.Key = KeyOf(n);
                n.Sig = StaticSig(n);
                int pick = -1;
                bool stoleNow = false;
                for (int c = 0; c < 16; c++)
                    if (chFree[c] <= n.Start && (pick < 0 || chFree[c] < chFree[pick])) pick = c;
                if (pick < 0 && n.Sig != null)
                {
                    for (int c = 0; c < 16 && pick < 0; c++)
                    {
                        bool shOk = true;
                        foreach (var cand in chAll[c])
                        {
                            if (cand.RelEnd <= n.Start) continue;
                            if (cand.Sig == null || cand.Sig != n.Sig || cand.Key == n.Key) { shOk = false; break; }
                        }
                        if (shOk) { pick = c; shared++; }
                    }
                }
                if (pick < 0)
                {
                    pick = 0;
                    for (int c = 1; c < 16; c++) if (chFree[c] < chFree[pick]) pick = c;
                    foreach (var cand in chAll[pick]) if (cand.End > n.Start) cand.End = n.Start;   // the older notes on it are cut here
                    stolen++;
                    stoleNow = true;
                    if (firstSteal < 0) firstSteal = n.Start / (double)Rate;
                }
                n.Ch = pick;
                chAll[pick].Add(n);
                int until = Math.Max(n.RelEnd, n.Start + 1);
                chFree[pick] = stoleNow ? until : Math.Max(chFree[pick], until);
            }

            int usedCh = 0;
            var seenCh = new bool[16];
            foreach (var n in keep) if (!seenCh[n.Ch]) { seenCh[n.Ch] = true; usedCh++; }

            // ---- 2c. the constant tuning offset of each sample (distance from the nearest key) goes into the VAG's rate,
            //          like the real set's mixed rates. Only the remainder is left for the pitch bend.
            var tuneOff = new Dictionary<int, double>();
            {
                var tuneLists = new Dictionary<int, List<double>>();
                foreach (var tn in keep)
                {
                    double tOffs = 12.0 * Math.Log(RefPitch(tn) / 4096.0, 2.0) - (KeyOf(tn) - 60);
                    List<double> tList;
                    if (!tuneLists.TryGetValue(tn.Addr, out tList)) { tList = new List<double>(); tuneLists[tn.Addr] = tList; }
                    tList.Add(tOffs);
                }
                foreach (var tkv in tuneLists) { tkv.Value.Sort(); tuneOff[tkv.Key] = tkv.Value[tkv.Value.Count / 2]; }
            }

            // ---- 3. HD + BD
            var hf = new HdFile();
            hf.VersionBytes = new byte[] { 0x49, 0x45, 0x43, 0x53, 0x73, 0x72, 0x65, 0x56, 0x10, 0, 0, 0, 0, 0, 2, 0 };
            var head = new byte[0x40];
            for (int i = 0; i < head.Length; i++) head[i] = 0xFF;
            Encoding.ASCII.GetBytes("IECSdaeH").CopyTo(head, 0);
            BitConverter.GetBytes(0x40u).CopyTo(head, 8);
            for (int i = 12; i < 36; i++) head[i] = 0;
            hf.HeadBytes = head;

            var ms = new MemoryStream();
            ms.Write(new byte[16], 0, 16);
            int loopsFixed = 0, loopSamples = 0;
            for (int vi = 0; vi < vagAddr.Count; vi++)
            {
                int addr = vagAddr[vi];
                byte[] raw = (byte[])cap.Samples[addr].Clone();
                int blocks = raw.Length / 16;
                bool loops = (raw[(blocks - 1) * 16 + 1] & 2) != 0;
                if (loops)
                {
                    loopSamples++;
                    bool hasStart = false;
                    for (int b = 0; b < blocks; b++) if ((raw[b * 16 + 1] & 4) != 0) { hasStart = true; break; }
                    if (!hasStart)
                    {
                        int lb = (repOf[addr] - addr) / 16;
                        if (lb < 0 || lb >= blocks) lb = 0;
                        raw[lb * 16 + 1] |= 4;
                        loopsFixed++;
                    }
                }
                while (ms.Position % 16 != 0) ms.WriteByte(0);
                uint off = (uint)ms.Position;
                ms.Write(raw, 0, raw.Length);

                int vagRate = (int)Math.Round(Rate * Math.Pow(2.0, tuneOff[addr] / 12.0));
                if (vagRate < 1000) vagRate = 1000;
                if (vagRate > 65535) vagRate = 65535;

                hf.Vags.Add(new HdVag { BdOffset = off, Rate = (ushort)vagRate, Attribute = (byte)(loops ? 1 : 0) });
            }

            var paramMap = new Dictionary<string, int>();
            var setMap = new Dictionary<int, int>();

            int Param(int vag, int a1, int a2, int wetFlag)
            {
                var raw = (byte[])TplParam.Clone();
                raw[0] = (byte)(vag & 0xFF); raw[1] = (byte)((vag >> 8) & 0xFF);
                raw[11] = 60; raw[13] = 0x40; raw[16] = 127;
                raw[18] = (byte)(a1 & 0xFF); raw[19] = (byte)((a1 >> 8) & 0xFF);
                raw[20] = (byte)(a2 & 0xFF); raw[21] = (byte)((a2 >> 8) & 0xFF);

                raw[16] = 0x64;
                raw[41] = (byte)(wetFlag != 0 ? (PsfWet.Byte41 & 0xFF) : 0x03);

                string key = BitConverter.ToString(raw);
                int idx;
                if (!paramMap.TryGetValue(key, out idx))
                {
                    idx = hf.Params.Count;
                    hf.Params.Add(new HdParam { Raw = raw });
                    paramMap[key] = idx;
                }
                return idx;
            }

            int SetOf(int param)
            {
                int idx;
                if (!setMap.TryGetValue(param, out idx))
                {
                    var st = new HdSet();
                    st.Params.Add((ushort)param);
                    idx = hf.Sets.Count;
                    hf.Sets.Add(st);
                    setMap[param] = idx;
                }
                return idx;
            }

            foreach (var p in progs)
            {
                var prog = new HdProgram { Header = (byte[])TplProgHdr.Clone(), SplitSize = 20 };
                var sp = new HdSplit { Raw = (byte[])TplSplit.Clone() };
                sp.SetIndex = SetOf(Param(vagOf[p[0]], p[1], p[2], p[3]));
                sp.KeyLow = 0;
                sp.KeyHigh = 127;
                sp.Raw[19] = 0;
                prog.Splits.Add(sp);
                hf.Programs.Add(prog);
            }
            byte[] bd = ms.ToArray();
            byte[] hd = hf.Build((uint)bd.Length);
            HdFile.Parse(hd);

            // ---- 4. the SQ (standard MIDI, 480 per quarter, 120 bpm -> 960 ticks per second)
            var ev = new List<Ev>();
            var cBank = new int[16]; var cProg = new int[16]; var cBend = new int[16];
            var cVol = new int[16]; var cPan = new int[16]; var cRev = new int[16];
            for (int i = 0; i < 16; i++) { cBank[i] = -1; cProg[i] = -1; cBend[i] = -1; cVol[i] = -1; cPan[i] = -1; cRev[i] = 0; }
            int bendClip = 0, levelClip = 0, revNotes = 0;
            bool sendRev = false;   // the real BGM_FRM.sq has no CC91; wet is switched only by byte 41 of the sample entry

            void Cc(int tick, int chn, int cc, int val)
            {
                ev.Add(new Ev { Tick = tick, Order = 1, Msg = new byte[] { (byte)(0xB0 | chn), (byte)cc, (byte)val } });
            }

            void State(int tick, Nt nn, int key, int pitch, int vl, int vr)
            {
                int chn = nn.Ch;
                if (pitch > 0)
                {
                    double bs = 12.0 * Math.Log(pitch / 4096.0, 2.0) - (key - 60) - tuneOff[nn.Addr];
                    if (Math.Abs(bs) > 2.0) { bendClip++; bs = bs < 0 ? -2.0 : 2.0; }
                    int bv = 8192 + (int)Math.Round(bs / 2.0 * 8192.0);
                    if (bv < 0) bv = 0;
                    if (bv > 16383) bv = 16383;
                    if (bv != cBend[chn])
                    {
                        cBend[chn] = bv;
                        ev.Add(new Ev { Tick = tick, Order = 1, Msg = new byte[] { (byte)(0xE0 | chn), (byte)(bv & 0x7F), (byte)((bv >> 7) & 0x7F) } });
                    }
                }
                double lv = Math.Abs(Sv(vl)), rv = Math.Abs(Sv(vr));
                double g = Math.Sqrt(lv * lv + rv * rv);
                if (g > 1.0) { levelClip++; g = 1.0; }
                int vol = (int)Math.Round(127.0 * Math.Sqrt(g));
                if (vol != cVol[chn]) { cVol[chn] = vol; Cc(tick, chn, 7, vol); }
                if (g > 1e-6)
                {
                    int pan = (int)Math.Round(Math.Atan2(rv, lv) / (Math.PI / 2.0) * 127.0);
                    if (pan < 0) pan = 0;
                    if (pan > 127) pan = 127;
                    if (pan != cPan[chn]) { cPan[chn] = pan; Cc(tick, chn, 10, pan); }
                }
                if (sendRev)
                {
                    int rs = nn.Rev ? 127 : 0;
                    if (rs != cRev[chn]) { cRev[chn] = rs; Cc(tick, chn, 91, rs); }
                }
            }

            foreach (var n in keep)
            {
                int chn = n.Ch;
                int t0 = Tick(n.Start);
                int t1 = Math.Max(t0 + 1, Tick(n.End));
                int rp = RefPitch(n);
                int key = 60 + (int)Math.Round(12.0 * Math.Log(rp / 4096.0, 2.0));
                if (key < 0) key = 0;
                if (key > 127) key = 127;
                if (n.Rev) revNotes++;

                int bank = n.Prog / 128, pr = n.Prog % 128;
                if (cBank[chn] != bank || cProg[chn] != pr)
                {
                    if (cBank[chn] != bank) Cc(t0, chn, 0, bank);
                    ev.Add(new Ev { Tick = t0, Order = 1, Msg = new byte[] { (byte)(0xC0 | chn), (byte)pr } });
                    cBank[chn] = bank; cProg[chn] = pr;
                }
                int lastPitch = n.StartPitch > 0 ? n.StartPitch : rp;
                State(t0, n, key, lastPitch, n.StartVl, n.StartVr);
                ev.Add(new Ev { Tick = t0, Order = 2, Msg = new byte[] { (byte)(0x90 | chn), (byte)key, 90 } });

                for (int j = 0; j < n.Chgs.Count; j++)
                {
                    var c = n.Chgs[j];
                    int tc = Tick(c.Frame);
                    if (tc >= t1) break;
                    if (j + 1 < n.Chgs.Count && Tick(n.Chgs[j + 1].Frame) == tc) continue;
                    if (c.Pitch > 0) lastPitch = c.Pitch;
                    State(Math.Max(tc, t0), n, key, lastPitch, c.Vl, c.Vr);
                }
                ev.Add(new Ev { Tick = t1, Order = 0, Msg = new byte[] { (byte)(0x80 | chn), (byte)key, 0 } });
            }

            bool hasLoop = loopStartSec >= 0 && loopEndSec > loopStartSec;
            if (hasLoop)
            {
                int lc = keep[0].Ch;
                ev.Add(new Ev { Tick = (int)Math.Round(loopStartSec * TicksPerSecond), Order = 1, Msg = new byte[] { (byte)(0xB0 | lc), 99, 0 } });
                ev.Add(new Ev { Tick = (int)Math.Round(loopEndSec * TicksPerSecond), Order = 1, Msg = new byte[] { (byte)(0xB0 | lc), 99, 1 } });
            }

            var tempo = new List<Ev> { new Ev { Tick = 0, Order = 0, Msg = new byte[] { 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20 } } };
            var smf = new List<byte>();
            smf.AddRange(Encoding.ASCII.GetBytes("MThd"));
            smf.AddRange(new byte[] { 0, 0, 0, 6, 0, 1, 0, 2, 0x01, 0xE0 });
            smf.AddRange(Mtrk(tempo));
            smf.AddRange(Mtrk(ev));
            SqSong song = SqSong.FromSmf(smf.ToArray(), name);
            song.AddSonyLoopData();

            // ---- 5. effects
            var px = new PsxExport { Hd = hd, Bd = bd, Song = song };
            px.ReverbRegs = (int[])cap.ReverbRegs.Clone();
            px.ReturnL = cap.ReturnL;
            px.ReturnR = cap.ReturnR;
            px.ReverbOn = cap.ReverbEnabled;
            bool match = true;
            for (int i = 0; i < 32; i++) if ((cap.ReverbRegs[i] & 0xFFFF) != StudioLarge[i]) { match = false; break; }
            if (!effects) px.Effects = "Effects OFF: no reverb information written (no CC91 in the SQ; a saved .psf2 gets -r=0).";
            else if (!cap.ReverbEnabled) px.Effects = "Effects ON, but this .psf never switched its reverb on: nothing to write.";
            else
            {
                if (match) { px.ReverbMode = 4; px.ReverbDepth = cap.ReturnL & 0x7FFF; }
                px.Effects = "Effects ON: " + revNotes + " of " + keep.Count + " notes go through the reverb (REVON measured) -> byte 41 of their sample entry = " + PsfWet.Byte41.ToString("X2") + " (wet), the others 03 (dry); no CC91 is written (the real BGM_FRM.sq has none). " +
                    (match ? "The .psf's reverb registers equal PS1 'Studio Large' -> .psf2 gets -r=4 and -d=" + px.ReverbDepth + " (my assumption, not verified on a PS2 player)."
                           : "The reverb registers match no preset I know -> the .psf2's reverb mode is left alone.");
            }

            // ---- 6. release check: tail the driver's voices really had after key-off vs. what the exported ADSR2 gives from full level
            var tailSb = new StringBuilder();
            {
                var tailByProg = new Dictionary<int, List<double>>();
                foreach (var tn in keep)
                {
                    if (tn.RelEnd >= total || tn.RelEnd <= tn.End) continue;
                    List<double> tl;
                    if (!tailByProg.TryGetValue(tn.Prog, out tl)) { tl = new List<double>(); tailByProg[tn.Prog] = tl; }
                    tl.Add((tn.RelEnd - tn.End) * 1000.0 / Rate);
                }
                tailSb.AppendLine("Release check (driver tail after key-off vs. what the exported ADSR2 gives from full level):");
                foreach (var tk in tailByProg.OrderBy(q => q.Key))
                {
                    int tA2 = progs[tk.Key][2];
                    int tShift = tA2 & 0x1F;
                    bool tExp = (tA2 & 0x20) != 0;
                    var tv = tk.Value;
                    tv.Sort();
                    double tMed = tv[tv.Count / 2];
                    string tPred;
                    string tMark = "";
                    if (tExp) tPred = "exponential release";
                    else
                    {
                        int tCycles = 1 << Math.Max(0, tShift - 11);
                        int tStep = 8 << Math.Max(0, 11 - tShift);
                        double tPredMs = 32767.0 / tStep * tCycles * 1000.0 / Rate;
                        tPred = tPredMs.ToString("F1") + " ms";
                        if (tMed > tPredMs) tMark = "   <-- driver tail LONGER than the exported envelope allows";
                    }
                    tailSb.AppendLine("  prog " + tk.Key + ": ADSR2 " + tA2.ToString("X4") + " (release shift " + tShift + ", " + (tExp ? "exp" : "linear") + "), " + tv.Count +
                                      " notes, driver tail median " + tMed.ToString("F1") + " ms (min " + tv[0].ToString("F1") + ", max " + tv[tv.Count - 1].ToString("F1") + "), exported: " + tPred + tMark);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("Key-ons logged: " + keyOns + ", notes written: " + keep.Count + ", dropped: no sample " + dropNoSample + ", silent " + dropSilent + ", pitch 0 " + dropPitch + ", after the end " + dropLate + ".");
            sb.AppendLine("Samples (VAGs): " + vagAddr.Count + " (" + loopSamples + " looping, " + loopsFixed + " got a loop-start flag from the driver's repeat address), programs: " + progs.Count + ", BD " + (bd.Length / 1024) + " KB.");
            sb.AppendLine("MIDI channels used: " + usedCh + " of 16 (a channel is reused after the driver's voice for the previous note had finished, or shared when the sounding notes have the same program, pitch, level and pan: " + shared + " notes). " +
                          "Notes that had to cut another note because all 16 were busy: " + stolen + (stolen > 0 ? " (first at " + firstSteal.ToString("F2") + " s)" : "") + ".");
            sb.AppendLine("Pitch-bend values clipped to +-2 semitones: " + bendClip + ". Levels above 1.0 clipped: " + levelClip + ".");

            {
                var tuneTxt = new StringBuilder();
                foreach (var tq in tuneOff.OrderBy(q => q.Key)) tuneTxt.Append(" " + Math.Round(tq.Value * 100.0).ToString("0"));
                sb.AppendLine("Tuning offset per sample (cents, now stored in the VAG rate instead of the pitch bend):" + tuneTxt);
            }

            sb.AppendLine("Loop: " + (hasLoop ? loopStartSec.ToString("F3") + " s -> " + loopEndSec.ToString("F3") + " s" : "none") + ".");
            sb.AppendLine("Reverb: " + (cap.ReverbEnabled ? "on" : "off") + ", return volume L " + cap.ReturnL.ToString("X4") + " R " + cap.ReturnR.ToString("X4") + ", work area byte 0x" + cap.WorkAreaByte.ToString("X") +
                          ", registers " + string.Join(" ", cap.ReverbRegs.Select(x => (x & 0xFFFF).ToString("X4"))));
            sb.AppendLine(px.Effects);
            sb.Append(tailSb.ToString());
            px.Report = sb.ToString();
            return px;
        }

        private static byte[] Mtrk(List<Ev> ev)
        {
            var sorted = ev.OrderBy(e => e.Tick).ThenBy(e => e.Order).ToList();
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
