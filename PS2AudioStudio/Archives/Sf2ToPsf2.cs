using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PS2AudioStudio.Audio;

namespace PS2AudioStudio.Archives
{
    /// <summary>Converts a SoundFont 2 model into a PSF2 bank (HD + BD bytes).</summary>
    public static class Sf2ToPsf2
    {
        // templates taken from the working mini hack (same bytes as the PSF2 Studio uses)
        private static readonly byte[] TplParam = { 0, 0, 1, 0x40, 0x7F, 0, 1, 0, 0, 1, 0, 0x3C, 0, 0x40, 0, 0x0A, 0xFF, 0xFF, 0xFF, 0xAF, 0xCB, 0x5F, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3 };
        private static readonly byte[] TplProgHdr = { 0x24, 0, 0, 0, 1, 0x14, 0xFF, 0x40, 0, 0, 0, 0x3C, 0, 0xFF, 0, 0, 0, 0, 0, 0, 0x64, 0, 0x64, 0, 0x80, 0, 0x80, 0, 0x80, 0, 0x80, 0, 0, 0, 0, 0 };
        private static readonly byte[] TplSplit = { 0, 0, 0x0C, 0x3C, 0x77, 0, 0, 6, 0, 6, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0xFF, 0x40, 0, 0 };

        private const int MaxProgram = 2047;

        // ---------------------------------------------------------------- SF2 time/level -> SPU2 ADSR
        private static double CycleSec(int shift)
        {
            return Math.Pow(2, Math.Max(0, shift - 11)) / 44100.0;
        }

        private static double AttackLinSec(int shift, int step)
        {
            double inc = (7 - step) * Math.Pow(2, Math.Max(0, 11 - shift));
            return 32767.0 / inc * CycleSec(shift);
        }

        private static double DecaySec(int shift)
        {
            double d = 8 * Math.Pow(2, Math.Max(0, 11 - shift));
            double ratio = 1.0 - d / 32768.0;
            double dbPerCycle = -20.0 * Math.Log10(ratio);
            return 100.0 / dbPerCycle * CycleSec(shift);
        }

        private static double ReleaseExpSec(int shift)
        {
            return DecaySec(shift);   // same exponential curve, 5-bit shift
        }

        private static double Sec(int tc)
        {
            double s = Math.Pow(2.0, tc / 1200.0);
            return s < 0.0005 ? 0.0005 : s;
        }

        private static int BestAttack(double sec)
        {
            double best = double.MaxValue; int bs = 0, bp = 0;
            for (int shift = 0; shift < 32; shift++)
                for (int step = 0; step < 4; step++)
                {
                    double e = Math.Abs(Math.Log(AttackLinSec(shift, step) / sec));
                    if (e < best) { best = e; bs = shift; bp = step; }
                }
            return (bs << 2) | bp;
        }

        private static int BestDecay(double sec)
        {
            double best = double.MaxValue; int bs = 0;
            for (int shift = 0; shift < 16; shift++)
            {
                double e = Math.Abs(Math.Log(DecaySec(shift) / sec));
                if (e < best) { best = e; bs = shift; }
            }
            return bs;
        }

        private static int BestRelease(double sec)
        {
            double best = double.MaxValue; int bs = 0;
            for (int shift = 0; shift < 32; shift++)
            {
                double e = Math.Abs(Math.Log(ReleaseExpSec(shift) / sec));
                if (e < best) { best = e; bs = shift; }
            }
            return bs;
        }

        private static void MakeAdsr(int atkTc, int decTc, int susCb, int relTc, out int a1, out int a2)
        {
            int at = BestAttack(Sec(atkTc));
            int ds = BestDecay(Sec(decTc));
            double cb = Math.Max(0, Math.Min(1440, susCb));
            double frac = Math.Pow(10.0, -cb / 200.0);
            int sl = (int)Math.Round(frac * 16.0 - 1.0);
            if (sl < 0) sl = 0;
            if (sl > 15) sl = 15;
            int rs = BestRelease(Sec(relTc));

            a1 = ((at >> 2) << 10) | ((at & 3) << 8) | (ds << 4) | sl;   // linear attack, decay shift, sustain level
            a2 = 0x5FC0 | 0x20 | rs;                                    // sustain holds, exponential release
        }

        // ---------------------------------------------------------------- helpers
        private static int Gv(Sf2Zone z, Sf2Zone g, int op, int def)
        {
            if (z.Gens.ContainsKey(op)) return z.Gens[op];
            if (g != null && g.Gens.ContainsKey(op)) return g.Gens[op];
            return def;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private sealed class VagSet
        {
            public readonly Dictionary<long, int> Map = new Dictionary<long, int>();
            public readonly List<byte[]> Raw = new List<byte[]>();
            public readonly List<int> Rate = new List<int>();
            public readonly List<bool> Loop = new List<bool>();
            public int Resampled, BadRate;
        }

        /// <summary>One SF2 sample (+ loop or not) becomes one VAG. The same pair is only encoded once.</summary>
        private static int GetVag(Sf2File sf, VagSet vs, int sIdx, bool loop)
        {
            long key = sIdx * 2L + (loop ? 1 : 0);
            int found;
            if (vs.Map.TryGetValue(key, out found)) return found;

            Sf2Sample s = sf.Samples[sIdx];
            int n = s.Data.Length;
            int len = Math.Max(n, 56);                 // a VAG needs at least 2 blocks
            var pcm = new float[len];
            for (int i = 0; i < n; i++) pcm[i] = s.Data[i] / 32768f;

            int rate = s.SampleRate;
            if (rate < 1000) { rate = 22050; vs.BadRate++; }

            int ls = 0, le = len;
            if (loop && s.LoopEnd > s.LoopStart + 1 && s.LoopStart >= 0 && s.LoopEnd <= n)
            {
                ls = s.LoopStart; le = s.LoopEnd;
            }

            if (rate > 48000)
            {
                var b = new AudioBuffer(pcm, rate, 1);
                b.LoopEnabled = loop; b.LoopStart = ls; b.LoopEnd = le;
                b = AudioIO.Resample(b, 48000);
                pcm = b.Samples;
                if (loop) { ls = b.LoopStart; le = b.LoopEnd; } else { ls = 0; le = pcm.Length; }
                rate = 48000;
                vs.Resampled++;
            }

            byte[] adpcm = VagCodec.EncodeAdpcm(pcm, loop, ls, le);
            if (adpcm == null || adpcm.Length < 16) throw new InvalidDataException("Sample " + sIdx + " could not be encoded.");
            int padded = (adpcm.Length + 15) / 16 * 16;
            if (padded != adpcm.Length) { var t = new byte[padded]; Array.Copy(adpcm, t, adpcm.Length); adpcm = t; }

            int idx = vs.Raw.Count;
            vs.Raw.Add(adpcm);
            vs.Rate.Add(rate);
            vs.Loop.Add(loop);
            vs.Map[key] = idx;
            return idx;
        }

        // ---------------------------------------------------------------- main conversion
        public static void Build(Sf2File sf, out byte[] hd, out byte[] bd, out string report)
        {
            if (sf == null || sf.Samples.Count == 0 || sf.Presets.Count == 0)
                throw new InvalidDataException("The SF2 has no samples or no presets.");

            var sb = new StringBuilder();
            var vs = new VagSet();

            var hf = new HdFile();
            hf.VersionBytes = new byte[] { 0x49, 0x45, 0x43, 0x53, 0x73, 0x72, 0x65, 0x56, 0x10, 0, 0, 0, 0, 0, 2, 0 };
            var head = new byte[0x40];
            for (int i = 0; i < head.Length; i++) head[i] = 0xFF;
            Encoding.ASCII.GetBytes("IECSdaeH").CopyTo(head, 0);
            BitConverter.GetBytes(0x40u).CopyTo(head, 8);
            for (int i = 12; i < 36; i++) head[i] = 0;
            hf.HeadBytes = head;

            var paramMap = new Dictionary<string, int>();
            var setMap = new Dictionary<string, int>();

            // ---- program numbers: bank * 128 + preset (the SQ selects them like that)
            var sorted = sf.Presets.OrderBy(p => (int)p.Bank).ThenBy(p => (int)p.Preset).ToList();
            var used = new HashSet<int>();
            var assign = new List<KeyValuePair<Sf2Preset, int>>();
            var later = new List<Sf2Preset>();
            foreach (var p in sorted)
            {
                int idx = (int)p.Bank * 128 + (int)p.Preset;
                if (idx <= MaxProgram && used.Add(idx)) assign.Add(new KeyValuePair<Sf2Preset, int>(p, idx));
                else later.Add(p);
            }
            int next = used.Count > 0 ? used.Max() + 1 : 0;
            int skippedPresets = 0;
            var moved = new List<string>();
            foreach (var p in later)
            {
                while (next <= MaxProgram && used.Contains(next)) next++;
                if (next > MaxProgram) { skippedPresets++; continue; }
                used.Add(next);
                assign.Add(new KeyValuePair<Sf2Preset, int>(p, next));
                moved.Add("bank " + p.Bank + " preset " + p.Preset + " -> program " + next);
                next++;
            }

            int zonesDone = 0, zonesSkipped = 0, programsMade = 0;

            foreach (var kv in assign.OrderBy(k => k.Value))
            {
                Sf2Preset pre = kv.Key;
                var prog = new HdProgram { Header = (byte[])TplProgHdr.Clone(), SplitSize = 20 };

                // key range -> params of that range
                var groups = new Dictionary<int, List<ushort>>();
                var groupOrder = new List<int>();

                foreach (Sf2Zone pz in pre.Zones)
                {
                    if (pz.Target < 0 || pz.Target >= sf.Instruments.Count) continue;
                    Sf2Instrument inst = sf.Instruments[pz.Target];
                    Sf2Zone g = null;
                    foreach (Sf2Zone z0 in inst.Zones) if (z0.Target < 0) { g = z0; break; }

                    foreach (Sf2Zone iz in inst.Zones)
                    {
                        if (iz.Target < 0) continue;
                        if (iz.Target >= sf.Samples.Count || sf.Samples[iz.Target].Data == null || sf.Samples[iz.Target].Data.Length < 2)
                        { zonesSkipped++; continue; }

                        int klo = 0, khi = 127;
                        if (iz.HasKeyRange) { klo = iz.KeyLo; khi = iz.KeyHi; }
                        else if (g != null && g.HasKeyRange) { klo = g.KeyLo; khi = g.KeyHi; }
                        if (pz.HasKeyRange) { klo = Math.Max(klo, (int)pz.KeyLo); khi = Math.Min(khi, (int)pz.KeyHi); }
                        klo = Clamp(klo, 0, 127); khi = Clamp(khi, 0, 127);
                        if (klo > khi) { zonesSkipped++; continue; }

                        int vlo = 1, vhi = 127;
                        if (iz.HasVelRange) { vlo = iz.VelLo; vhi = iz.VelHi; }
                        else if (g != null && g.HasVelRange) { vlo = g.VelLo; vhi = g.VelHi; }
                        if (pz.HasVelRange) { vlo = Math.Max(vlo, (int)pz.VelLo); vhi = Math.Min(vhi, (int)pz.VelHi); }
                        vlo = Clamp(Math.Max(vlo, 1), 1, 127); vhi = Clamp(vhi, 1, 127);
                        if (vlo > vhi) { zonesSkipped++; continue; }

                        Sf2Sample smp = sf.Samples[iz.Target];

                        int modes = Gv(iz, g, Sf2Gen.SampleModes, 0) & 3;
                        bool loop = modes == 1 || modes == 3;

                        int root = Gv(iz, g, Sf2Gen.OverridingRootKey, -1);
                        if (root < 0 || root > 127)
                        {
                            int rk = smp.RootKey;
                            root = (rk >= 0 && rk <= 127) ? rk : 60;
                        }
                        int cents = Gv(iz, g, Sf2Gen.FineTune, 0) + (int)smp.PitchCorrection;
                        int shift = Gv(iz, g, Sf2Gen.CoarseTune, 0) + (int)Math.Round(cents / 100.0);
                        int baseNote = Clamp(root - shift, 0, 127);

                        int a1, a2;
                        MakeAdsr(Gv(iz, g, Sf2Gen.AttackVolEnv, -12000),
                                 Gv(iz, g, Sf2Gen.DecayVolEnv, -12000),
                                 Gv(iz, g, Sf2Gen.SustainVolEnv, 0),
                                 Gv(iz, g, Sf2Gen.ReleaseVolEnv, -12000), out a1, out a2);

                        int vagIdx = GetVag(sf, vs, iz.Target, loop);

                        var raw = (byte[])TplParam.Clone();
                        raw[0] = (byte)(vagIdx & 0xFF); raw[1] = (byte)((vagIdx >> 8) & 0xFF);
                        raw[2] = (byte)vlo;
                        raw[4] = (byte)vhi;
                        raw[11] = (byte)baseNote;
                        raw[18] = (byte)(a1 & 0xFF); raw[19] = (byte)((a1 >> 8) & 0xFF);
                        raw[20] = (byte)(a2 & 0xFF); raw[21] = (byte)((a2 >> 8) & 0xFF);

                        string pkey = BitConverter.ToString(raw);
                        int pidx;
                        if (!paramMap.TryGetValue(pkey, out pidx))
                        {
                            pidx = hf.Params.Count;
                            hf.Params.Add(new HdParam { Raw = raw });
                            paramMap[pkey] = pidx;
                        }

                        int gk = klo * 256 + khi;
                        List<ushort> list;
                        if (!groups.TryGetValue(gk, out list)) { list = new List<ushort>(); groups[gk] = list; groupOrder.Add(gk); }
                        if (!list.Contains((ushort)pidx)) list.Add((ushort)pidx);
                        zonesDone++;
                    }
                }

                if (groupOrder.Count == 0) continue;

                foreach (int gk in groupOrder.OrderBy(x => x))
                {
                    var plist = groups[gk];
                    string skey = string.Join(",", plist);
                    int sidx;
                    if (!setMap.TryGetValue(skey, out sidx))
                    {
                        var st = new HdSet();
                        foreach (ushort pi in plist) st.Params.Add(pi);
                        sidx = hf.Sets.Count;
                        hf.Sets.Add(st);
                        setMap[skey] = sidx;
                    }
                    var sp = new HdSplit { Raw = (byte[])TplSplit.Clone() };
                    sp.SetIndex = sidx;
                    sp.KeyLow = gk / 256;
                    sp.KeyHigh = gk % 256;
                    prog.Splits.Add(sp);
                }

                while (hf.Programs.Count <= kv.Value) hf.Programs.Add(null);
                hf.Programs[kv.Value] = prog;
                programsMade++;
            }

            if (vs.Raw.Count == 0 || programsMade == 0)
                throw new InvalidDataException("No usable sample/zone was found in the SF2 (nothing could be converted).");

            // ---- BD: a silent 16-byte lead block, then every VAG
            var ms = new MemoryStream();
            ms.Write(new byte[16], 0, 16);
            for (int i = 0; i < vs.Raw.Count; i++)
            {
                while (ms.Position % 16 != 0) ms.WriteByte(0);
                uint off = (uint)ms.Position;
                ms.Write(vs.Raw[i], 0, vs.Raw[i].Length);
                hf.Vags.Add(new HdVag { BdOffset = off, Rate = (ushort)vs.Rate[i], Attribute = (byte)(vs.Loop[i] ? 1 : 0) });
            }
            bd = ms.ToArray();
            hd = hf.Build((uint)bd.Length);
            HdFile.Parse(hd);                       // sanity: the HD must read back

            int loops = vs.Loop.Count(x => x);
            sb.AppendLine("SF2 samples in the file: " + sf.Samples.Count + "  |  VAGs made: " + vs.Raw.Count + " (" + loops + " looping)");
            sb.AppendLine("Programs: " + programsMade + "  |  sample sets: " + hf.Sets.Count + "  |  sample params: " + hf.Params.Count + "  |  zones converted: " + zonesDone);
            sb.AppendLine("BD size: " + (bd.Length / 1024) + " KB" + (bd.Length > 2 * 1024 * 1024 ? "  (!) more than the 2 MB of SPU2 sound RAM - fine for the tools, too big for a real game" : ""));
            if (zonesSkipped > 0) sb.AppendLine("Zones skipped (empty sample or impossible key/velocity range): " + zonesSkipped);
            if (vs.Resampled > 0) sb.AppendLine("Samples above 48000 Hz were resampled to 48000: " + vs.Resampled);
            if (vs.BadRate > 0) sb.AppendLine("Samples with a rate below 1000 Hz were set to 22050 Hz: " + vs.BadRate);
            if (moved.Count > 0)
            {
                sb.AppendLine("Program numbers above " + MaxProgram + " (or used twice) were moved:");
                foreach (string m in moved.Take(12)) sb.AppendLine("  " + m);
                if (moved.Count > 12) sb.AppendLine("  ...");
            }
            if (skippedPresets > 0) sb.AppendLine("Presets skipped (no free program number): " + skippedPresets);
            sb.AppendLine();
            sb.AppendLine("Program number = bank * 128 + preset. The SQ / MIDI must select it.");
            sb.AppendLine("Carried: samples, loops (only where the zone's sample mode loops), key and velocity ranges, root note + tuning (rounded to semitones), ADSR (approximate).");
            sb.AppendLine("Not carried: volume, pan, filters, LFO, modulators, stereo links, loop fine offsets.");
            sb.AppendLine("Sustain below -24 dB holds at -24 dB (the SPU2 level steps stop there).");
            sb.AppendLine("Press 'Save HD + BD...' to write the files.");
            report = sb.ToString();
        }
    }
}
