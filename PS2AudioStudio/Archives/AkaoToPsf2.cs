using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PS2AudioStudio.Archives
{
    /// <summary>Builds a PSF2-style bank (HD + BD) from an AKAO sample bank and the program / drum tables of one AKAO song.</summary>
    public static class AkaoToPsf2
    {
        // same templates as Sf2ToPsf2 / the PSF2 Studio (tested layout)
        private static readonly byte[] TplParam = { 0, 0, 1, 0x40, 0x7F, 0, 1, 0, 0, 1, 0, 0x3C, 0, 0x40, 0, 0x0A, 0xFF, 0xFF, 0xFF, 0xAF, 0xCB, 0x5F, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3 };
        private static readonly byte[] TplProgHdr = { 0x24, 0, 0, 0, 1, 0x14, 0x64, 0x40, 0, 0, 0, 0x3C, 0, 0xFF, 0, 0, 0, 0, 0, 0, 0x64, 0, 0x64, 0, 0x80, 0, 0x80, 0, 0x80, 0, 0x80, 0, 0, 0, 0, 0 };
        private static readonly byte[] TplSplit = { 0, 0, 0x0C, 0x3C, 0x77, 0, 0, 2, 0, 2, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0x64, 0x40, 0, 0 };

        /// <summary>Program number of the drum kit: bank 1, program 0 (what AkaoMidi writes for the drum channel).</summary>
        public const int DrumProgram = 128;

        private static bool IsZero(byte[] a)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] != 0) return false;
            return true;
        }

        /// <summary>Semitone shift of the AKAO tuning word (bytes 8-9 of a bank record): root = UnityKey + this.
        /// x &lt; 0x8000: ratio = 1 + x/32768; x &gt;= 0x8000: ratio = x/65536; shift = -12*log2(ratio).</summary>
        public static double TuneShift(int word)
        {
            word &= 0xFFFF;
            double r = word < 0x8000 ? 1.0 + word / 32768.0 : word / 65536.0;
            return -12.0 * Math.Log(r, 2.0);
        }

        /// <summary>Splits an exact root pitch (in semitones) into an integer base note and 0..99 extra cents (base note is rounded up).</summary>
        private static void SplitRoot(double eff, out int baseNote, out int detune)
        {
            int cents = (int)Math.Round(eff * 100.0);
            if (cents <= 0) { baseNote = 0; detune = 0; return; }
            int bn = (cents + 99) / 100;
            if (bn > 127) { baseNote = 127; detune = 0; return; }
            baseNote = bn;
            detune = bn * 100 - cents;
            if (detune < 0) detune = 0;
            if (detune > 99) detune = 99;
        }

        /// <summary>Envelope of one zone / drum slot. The bank record gives the base ADSR; the song's program table can override:
        /// attackByte = high byte of ADSR1 (0 = keep the bank's attack), releaseByte = release shift (0 = keep the bank's release).
        /// The release mode bit is kept from the bank record; the game's driver writes a linear release.</summary>
        private static void Envelope(AkaoSample s, int attackByte, int releaseByte, out int a1, out int a2)
        {
            a1 = s.Adsr1 & 0xFFFF;
            a2 = s.Adsr2 & 0xFFFF;

            if (attackByte != 0)
                a1 = (a1 & 0x00FF) | ((attackByte & 0xFF) << 8);

            if (releaseByte != 0)
                a2 = (a2 & ~0x3F) | (releaseByte & 0x1F);
        }

        /// <summary>Copy of the sample with correct PS-ADPCM flags: loop start at the loop point, loop end + repeat on the last block (or plain end).</summary>
        private static byte[] Prepare(AkaoSample s)
        {
            var raw = (byte[])s.Adpcm.Clone();
            int blocks = raw.Length / 16;
            if (blocks < 1) return raw;
            if (s.Loops)
            {
                int lb = Math.Max(0, Math.Min(blocks - 1, s.LoopInSample / 16));
                raw[lb * 16 + 1] |= 4;
                raw[(blocks - 1) * 16 + 1] |= 3;
            }
            else
            {
                raw[(blocks - 1) * 16 + 1] |= 1;
            }
            return raw;
        }

        public static void Build(AkaoBank bank, AkaoSongData song, int rate, out byte[] hd, out byte[] bd, out string report)
        {
            if (bank == null || song == null) throw new InvalidDataException("No AKAO bank or song.");
            if (rate < 1000) rate = 1000;
            if (rate > 48000) rate = 48000;

            int limit = song.UsedBankEntries;
            if (limit <= 0 || limit > bank.Samples.Count) limit = bank.Samples.Count;

            var hf = new HdFile();
            hf.VersionBytes = new byte[] { 0x49, 0x45, 0x43, 0x53, 0x73, 0x72, 0x65, 0x56, 0x10, 0, 0, 0, 0, 0, 2, 0 };
            var head = new byte[0x40];
            for (int i = 0; i < head.Length; i++) head[i] = 0xFF;
            Encoding.ASCII.GetBytes("IECSdaeH").CopyTo(head, 0);
            BitConverter.GetBytes(0x40u).CopyTo(head, 8);
            for (int i = 12; i < 36; i++) head[i] = 0;
            hf.HeadBytes = head;

            // ---- BD: a silent 16-byte lead block, then every non-empty sample. VAG index = AKAO bank entry.
            var smp = new AkaoSample[limit];
            var offs = new uint[limit];
            var ms = new MemoryStream();
            ms.Write(new byte[16], 0, 16);
            int made = 0, loops = 0, empty = 0, lastUsed = -1;
            for (int i = 0; i < limit; i++)
            {
                var s = bank.Samples[i];
                if (s.Adpcm == null || s.Adpcm.Length < 32 || IsZero(s.Adpcm)) { empty++; continue; }
                byte[] raw = Prepare(s);
                while (ms.Position % 16 != 0) ms.WriteByte(0);
                offs[i] = (uint)ms.Position;
                ms.Write(raw, 0, raw.Length);
                smp[i] = s;
                lastUsed = i;
                made++;
                if (s.Loops) loops++;
            }
            if (made == 0) throw new InvalidDataException("The AKAO bank has no usable samples.");
            for (int i = 0; i <= lastUsed; i++)
                hf.Vags.Add(smp[i] == null ? null : new HdVag { BdOffset = offs[i], Rate = (ushort)rate, Attribute = (byte)(smp[i].Loops ? 1 : 0) });

            var paramMap = new Dictionary<string, int>();
            var setMap = new Dictionary<int, int>();

            int Param(int vag, int baseNote, int volume, int pan, int a1, int a2)
            {
                var raw = (byte[])TplParam.Clone();
                raw[0] = (byte)(vag & 0xFF); raw[1] = (byte)((vag >> 8) & 0xFF);
                raw[11] = (byte)Math.Max(0, Math.Min(127, baseNote));
                raw[13] = (byte)(pan & 0x7F);
                raw[16] = (byte)Math.Max(1, Math.Min(127, volume));
                raw[18] = (byte)(a1 & 0xFF); raw[19] = (byte)((a1 >> 8) & 0xFF);
                raw[20] = (byte)(a2 & 0xFF); raw[21] = (byte)((a2 >> 8) & 0xFF);
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

            HdSplit Split(int set, int lo, int hi, int detuneCents)
            {
                var sp = new HdSplit { Raw = (byte[])TplSplit.Clone() };
                sp.SetIndex = set;
                sp.KeyLow = lo;
                sp.KeyHigh = hi;
                sp.Raw[19] = (byte)Math.Max(0, Math.Min(99, detuneCents));   // extra cents (renderer: split detune / 100)
                return sp;
            }

            var relSeen = new SortedSet<int>();
            var atkSeen = new SortedSet<int>();

            // ---- melodic programs: one split per AKAO zone; root = unity key shifted by the AKAO tuning word
            //      zone record (8 bytes): [0] sample id, [1] key low, [2] key high, [3] attack, [6] release shift
            int zonesDone = 0, zonesSkipped = 0;
            var progNumbers = new List<int>();
            foreach (var kv in song.Zones.OrderBy(k => k.Key))
            {
                var prog = new HdProgram { Header = (byte[])TplProgHdr.Clone(), SplitSize = 20 };
                foreach (var z in kv.Value)
                {
                    int idx = z.SampleId - song.BaseId;
                    int lo = Math.Min(127, z.KeyLow), hi = Math.Min(127, z.KeyHigh);
                    if (idx < 0 || idx >= limit || smp[idx] == null || lo > hi) { zonesSkipped++; continue; }
                    double eff = smp[idx].UnityKey + TuneShift((int)smp[idx].FineTune);
                    int bn, det;
                    SplitRoot(eff, out bn, out det);

                    int atk = 0, rel = 0;
                    if (z.Raw != null && z.Raw.Length >= 7 && z.Raw[0] == z.SampleId) { atk = z.Raw[3]; rel = z.Raw[6]; }
                    int a1, a2;
                    Envelope(smp[idx], atk, rel, out a1, out a2);
                    if (rel != 0) relSeen.Add(rel & 0x1F);
                    if (atk != 0) atkSeen.Add(atk);

                    prog.Splits.Add(Split(SetOf(Param(idx, bn, 100, 0x40, a1, a2)), lo, hi, det));
                    zonesDone++;
                }
                if (prog.Splits.Count == 0) continue;
                while (hf.Programs.Count <= kv.Key) hf.Programs.Add(null);
                hf.Programs[kv.Key] = prog;
                progNumbers.Add(kv.Key);
            }

            // ---- drum kit: program 128, one split per drum slot (key = slot number)
            //      record (8 bytes): [0] sample id, [1] unity key, [2] attack, [5] release shift, [6] volume (squared law), [7] pan (low 7 bits, 64 = centre)
            int drumsDone = 0, drumsSilent = 0;
            var dp = new HdProgram { Header = (byte[])TplProgHdr.Clone(), SplitSize = 20 };
            for (int k = 0; k < 96; k++)
            {
                var rec = song.Drums[k];
                if (rec == null || IsZero(rec)) continue;
                int idx = rec[0] - song.BaseId;
                if (idx < 0 || idx >= limit || smp[idx] == null) { drumsSilent++; continue; }

                double eff = k + smp[idx].UnityKey + TuneShift((int)smp[idx].FineTune) - rec[1];
                int bn, det;
                SplitRoot(eff, out bn, out det);

                double vf = rec[6] / 127.0;
                int vol = (int)Math.Round(100.0 * vf * vf);
                int pan = rec[7] & 0x7F;

                int a1, a2;
                Envelope(smp[idx], rec[2], rec[5], out a1, out a2);
                if (rec[5] != 0) relSeen.Add(rec[5] & 0x1F);
                if (rec[2] != 0) atkSeen.Add(rec[2]);

                dp.Splits.Add(Split(SetOf(Param(idx, bn, vol, pan, a1, a2)), k, k, det));
                drumsDone++;
            }
            if (dp.Splits.Count > 0)
            {
                while (hf.Programs.Count <= DrumProgram) hf.Programs.Add(null);
                hf.Programs[DrumProgram] = dp;
            }

            if (progNumbers.Count == 0 && dp.Splits.Count == 0)
                throw new InvalidDataException("The AKAO song has no usable programs or drums.");

            bd = ms.ToArray();
            hd = hf.Build((uint)bd.Length);
            HdFile.Parse(hd);                       // sanity: the HD must read back

            var sb = new StringBuilder();
            sb.AppendLine("VAGs: " + made + " (" + loops + " looping), empty placeholder entries skipped: " + empty + ". VAG index = AKAO bank entry number.");
            sb.AppendLine("Programs: " + progNumbers.Count + " (numbers " + string.Join(", ", progNumbers) + "), zones converted: " + zonesDone + (zonesSkipped > 0 ? ", skipped: " + zonesSkipped : ""));
            sb.AppendLine("Drum kit = program " + DrumProgram + " (bank 1, program 0): " + drumsDone + " slots" + (drumsSilent > 0 ? ", " + drumsSilent + " silent (their sample is an empty placeholder)" : "") + ".");
            sb.AppendLine("Sample sets: " + hf.Sets.Count + ", sample params: " + hf.Params.Count + ", BD " + (bd.Length / 1024) + " KB.");
            sb.AppendLine("Pitch: root = AKAO unity key shifted by the tuning word (bytes 8-9); extra cents are stored as split detune. Rate = " + rate + " Hz.");
            if (limit > 0 && smp[0] != null)
                sb.AppendLine("Check: entry #0 tuning word = " + ((int)smp[0].FineTune & 0xFFFF) + " (expected 6284), root = " + (smp[0].UnityKey + TuneShift((int)smp[0].FineTune)).ToString("0.00") + " (expected 44.97).");
            sb.AppendLine("Envelope: release shifts from the song's program table = " + (relSeen.Count == 0 ? "none" : string.Join(", ", relSeen.Select(x => "0x" + x.ToString("X2")))) +
                          " (expected 0x07, 0x0A, 0x0D for this song); attack overrides = " + (atkSeen.Count == 0 ? "none" : string.Join(", ", atkSeen.Select(x => "0x" + x.ToString("X2")))) + " (expected 0x2D).");
            sb.AppendLine("Drums: root, volume, pan and envelope come from the 8-byte drum record.");
            report = sb.ToString();
        }
    }
}
