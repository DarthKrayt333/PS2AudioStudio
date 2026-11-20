using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PS2AudioStudio.Audio;
namespace PS2AudioStudio.Archives
{
    /// <summary>Converts a loaded PSF2 bank (HD + BD) into a SoundFont 2 model.</summary>
    public static class Psf2ToSf2
    {
        /// <summary>Also writes every preset into bank 128 so GM players (MIDI channel 10 = drums) find it.</summary>
        public static bool DrumBankCopies = true;

        private static int I(object o) { return System.Convert.ToInt32(o); }
        private static T At<T>(IList<T> list, int i) where T : class
        {
            if (list == null || i < 0 || i >= list.Count) return null;
            return list[i];
        }
        // ---------- SPU ADSR -> seconds (SPU envelope runs at 44100 ticks/s) ----------
        private static double CycleSec(int shift)
        {
            return Math.Pow(2, Math.Max(0, shift - 11)) / 44100.0;
        }
        private static double AttackSec(int a1)
        {
            bool exp = (a1 & 0x8000) != 0;
            int shift = (a1 >> 10) & 31;
            int step = 7 - ((a1 >> 8) & 3);
            double inc = step * Math.Pow(2, Math.Max(0, 11 - shift));
            double t = 32767.0 / inc * CycleSec(shift);
            if (exp) t *= 1.75;                       // last quarter of the ramp is 4x slower
            return t;
        }
        private static double Decay100dBSec(int a1)
        {
            int shift = (a1 >> 4) & 15;
            double d = 8 * Math.Pow(2, Math.Max(0, 11 - shift));
            double ratio = 1.0 - d / 32768.0;
            double dbPerCycle = -20.0 * Math.Log10(ratio);
            return 100.0 / dbPerCycle * CycleSec(shift);
        }
        private static double SustainCb(int a1)
        {
            int sl = a1 & 15;
            double frac = (sl + 1) / 16.0;
            double cb = -200.0 * Math.Log10(frac);
            return Math.Max(0, Math.Min(1440, cb));
        }
        private static double Release100dBSec(int a2)
        {
            int shift = a2 & 31;
            bool exp = (a2 & 0x20) != 0;
            double d = 8 * Math.Pow(2, Math.Max(0, 11 - shift));
            if (exp)
            {
                double ratio = 1.0 - d / 32768.0;
                double dbPerCycle = -20.0 * Math.Log10(ratio);
                return 100.0 / dbPerCycle * CycleSec(shift);
            }
            return 32767.0 / d * CycleSec(shift);
        }
        private static short Tc(double sec)
        {
            if (sec < 0.001) sec = 0.001;
            double tc = 1200.0 * Math.Log(sec, 2.0);
            if (tc < -12000) tc = -12000;
            if (tc > 8000) tc = 8000;
            return (short)Math.Round(tc);
        }
        private static int Sw(int v) { return ((v & 0xFF) << 8) | ((v >> 8) & 0xFF); }
        private static double Score(int a1, int a2)
        {
            return Math.Min(60.0, AttackSec(a1)) + Math.Min(60.0, Release100dBSec(a2));
        }
        // The HD stores the ADSR halfwords in an order I could not verify, so the bank is
        // checked once: whichever byte order gives believable (short) attack/release times wins.
        private static bool DecideSwap(Psf2Bank bank)
        {
            double asIs = 0, swapped = 0;
            foreach (var p in bank.SampleParams)
            {
                if (p == null) continue;
                int a1 = I(p.Adsr1), a2 = I(p.Adsr2);
                asIs += Score(a1, a2);
                swapped += Score(Sw(a1), Sw(a2));
            }
            return swapped < asIs;
        }
        private static void Envelope(Sf2Zone z, int a1, int a2)
        {
            z.Gens[Sf2Gen.AttackVolEnv] = Tc(AttackSec(a1));
            z.Gens[Sf2Gen.DecayVolEnv] = Tc(Decay100dBSec(a1));
            z.Gens[Sf2Gen.SustainVolEnv] = (short)Math.Round(SustainCb(a1));
            z.Gens[Sf2Gen.ReleaseVolEnv] = Tc(Release100dBSec(a2));
        }
        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        /// <summary>Same as Convert, plus the reverb send: reverbSend > 0 writes it on every preset zone, 0 writes none.</summary>
        public static Sf2File Convert(Psf2Bank bank, string name, int reverbSend, out string report)
        {
            var sf = Convert(bank, name, out report);
            sf.SetReverbSend(reverbSend);
            report += reverbSend > 0
                ? "Effects ON: reverbEffectsSend = " + reverbSend + " on every preset zone (same value and place as the VGMTrans SF2). The reverb sound itself comes from the program that plays the SF2.\r\n"
                : "Effects OFF: no reverb send written.\r\n";
            return sf;
        }
        public static Sf2File Convert(Psf2Bank bank, string name, out string report)
        {
            var sb = new StringBuilder();
            var sf = new Sf2File();
            sf.Name = string.IsNullOrEmpty(name) ? "PSF2 bank" : name;
            bool swap = DecideSwap(bank);
            // ---- samples ----
            var map = new Dictionary<int, int>();        // bank sample index -> SF2 sample index
            var loopsOf = new Dictionary<int, bool>();   // SF2 sample index -> loops
            foreach (var s in bank.Samples)
            {
                if (s == null || !s.Valid || s.Decoded == null) continue;
                AudioBuffer d = s.Decoded;
                int ch = Math.Max(1, d.Channels);
                int n = d.Samples.Length / ch;
                if (n < 2) continue;
                var data = new short[n];
                for (int i = 0; i < n; i++)
                {
                    float v = d.Samples[i * ch];
                    if (v > 1f) v = 1f;
                    if (v < -1f) v = -1f;
                    data[i] = (short)Math.Round(v * 32767.0);
                }
                var smp = new Sf2Sample();
                smp.Name = "VAG " + s.Index.ToString("000");
                smp.Data = data;
                smp.SampleRate = (int)d.SampleRate;
                smp.RootKey = (byte)60;
                bool loops = s.Loops;
                if (loops)
                {
                    int ls = 0, le = n;
                    if (d.LoopEnabled && d.LoopEnd > d.LoopStart) { ls = d.LoopStart; le = d.LoopEnd; }
                    le = Math.Min(le, n);
                    ls = Math.Max(0, Math.Min(ls, le - 1));
                    smp.LoopStart = ls;
                    smp.LoopEnd = le;
                }
                int idx = sf.Samples.Count;
                sf.Samples.Add(smp);
                map[s.Index] = idx;
                loopsOf[idx] = loops;
            }
            sb.AppendLine("Samples exported: " + sf.Samples.Count);
            // ---- programs -> presets ----
            int zones = 0;
            foreach (var kv in bank.Programs.OrderBy(k => k.Key))
            {
                int progIdx = kv.Key;
                var prog = kv.Value;
                if (prog == null) continue;
                var inst = new Sf2Instrument();
                inst.Name = "Prog " + progIdx;
                foreach (var split in prog.Splits)
                {
                    if (split == null) continue;
                    var set = At(bank.SampleSets, I(split.SampleSetIndex));
                    if (set == null) continue;
                    int lo = Clamp(I(split.RangeLow), 0, 127);
                    int hi = Clamp(I(split.RangeHigh), 0, 127);
                    if (lo > hi) { int t = lo; lo = hi; hi = t; }
                    foreach (object po in set.SampleParams)
                    {
                        if (!(po is IConvertible)) continue;
                        var p = At(bank.SampleParams, I(po));
                        if (p == null) continue;
                        int sIdx;
                        if (!map.TryGetValue(I(p.VagIndex), out sIdx)) continue;
                        var z = new Sf2Zone();
                        z.HasKeyRange = true;
                        z.KeyLo = (byte)lo;
                        z.KeyHi = (byte)hi;
                        int vlo = Math.Max(I(p.VelLow), I(set.VelLow));
                        int vhi = Math.Min(I(p.VelHigh), I(set.VelHigh));
                        vlo = Clamp(vlo, 0, 127); vhi = Clamp(vhi, 0, 127);
                        // a range that ends at 0 can never be played (velocity 0 = note off): treat as "no range"
                        if (vhi >= 1 && vhi >= vlo && !(vlo <= 1 && vhi >= 127))
                        {
                            z.HasVelRange = true;
                            z.VelLo = (byte)vlo;
                            z.VelHi = (byte)vhi;
                        }
                        z.Target = sIdx;
                        if (loopsOf[sIdx]) z.Gens[Sf2Gen.SampleModes] = 1;
                        z.Gens[Sf2Gen.OverridingRootKey] = (short)Clamp(I(p.BaseNote), 0, 127);
                        int a1 = I(p.Adsr1), a2 = I(p.Adsr2);
                        if (swap) { a1 = Sw(a1); a2 = Sw(a2); }
                        Envelope(z, a1, a2);
                        inst.Zones.Add(z);
                        zones++;
                    }
                }
                if (inst.Zones.Count == 0) continue;
                int instIdx = sf.Instruments.Count;
                sf.Instruments.Add(inst);
                var pre = new Sf2Preset();
                pre.Name = "Program " + progIdx;
                pre.Bank = (ushort)Math.Min(progIdx / 128, 127);
                pre.Preset = (ushort)(progIdx % 128);
                var pz = new Sf2Zone();
                pz.Target = instIdx;
                pre.Zones.Add(pz);
                sf.Presets.Add(pre);
            }
            // ---- fallback: no usable programs -> every sample on its own key ----
            if (sf.Presets.Count == 0 && sf.Samples.Count > 0)
            {
                var inst = new Sf2Instrument();
                inst.Name = "All samples";
                for (int i = 0; i < sf.Samples.Count && i < 128; i++)
                {
                    var z = new Sf2Zone();
                    z.HasKeyRange = true;
                    z.KeyLo = (byte)i;
                    z.KeyHi = (byte)i;
                    z.Target = i;
                    if (loopsOf[i]) z.Gens[Sf2Gen.SampleModes] = 1;
                    z.Gens[Sf2Gen.OverridingRootKey] = (short)i;
                    inst.Zones.Add(z);
                    zones++;
                }
                sf.Instruments.Add(inst);
                var pre = new Sf2Preset();
                pre.Name = "All samples";
                pre.Bank = (ushort)0;
                pre.Preset = (ushort)0;
                var pz = new Sf2Zone();
                pz.Target = 0;
                pre.Zones.Add(pz);
                sf.Presets.Add(pre);
                sb.AppendLine("No programs found: every sample is on its own key (sample N on key N).");
            }
            // ---- bank 128 copies: GM players send MIDI channel 10 to bank 128 (drums) ----
            int drumCopies = 0;
            if (DrumBankCopies && sf.Presets.Count > 0)
            {
                var firstOf = new Dictionary<int, Sf2Preset>();
                foreach (var p0 in sf.Presets)
                {
                    if (p0.Bank >= 128) continue;
                    if (!firstOf.ContainsKey(p0.Preset)) firstOf[p0.Preset] = p0;
                }
                foreach (var kv2 in firstOf.OrderBy(k => k.Key))
                {
                    var p1 = kv2.Value;
                    var cp = new Sf2Preset();
                    cp.Name = "Drums " + p1.Name;
                    cp.Bank = (ushort)128;
                    cp.Preset = p1.Preset;
                    foreach (var z0 in p1.Zones)
                    {
                        var cz = new Sf2Zone();
                        cz.Target = z0.Target;
                        cp.Zones.Add(cz);
                    }
                    sf.Presets.Add(cp);
                    drumCopies++;
                }
            }
            sb.AppendLine("Presets: " + sf.Presets.Count + ", instruments: " + sf.Instruments.Count + ", zones: " + zones);
            if (drumCopies > 0) sb.AppendLine("Bank 128 copies: " + drumCopies + " (MIDI channel 10 plays the same programs in GM players such as Awave).");
            if (swap) sb.AppendLine("ADSR: bytes were swapped to get believable times.");
            sb.AppendLine("ADSR is approximate (SPU2 rates converted to SF2 time/level).");
            report = sb.ToString();
            return sf;
        }
    }
}
