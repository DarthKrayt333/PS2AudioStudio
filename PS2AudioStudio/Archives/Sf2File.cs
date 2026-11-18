using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PS2AudioStudio.Archives
{
    /// <summary>SoundFont 2 generator numbers used by the converters.</summary>
    public static class Sf2Gen
    {
        public const int StartAddrsOffset = 0, EndAddrsOffset = 1, StartLoopOffset = 2, EndLoopOffset = 3;
        public const int StartAddrsCoarse = 4, EndAddrsCoarse = 12;
        public const int Pan = 17;
        public const int DelayVolEnv = 33, AttackVolEnv = 34, HoldVolEnv = 35, DecayVolEnv = 36, SustainVolEnv = 37, ReleaseVolEnv = 38;
        public const int Instrument = 41, KeyRange = 43, VelRange = 44;
        public const int StartLoopCoarse = 45, EndLoopCoarse = 50;
        public const int InitialAttenuation = 48;
        public const int CoarseTune = 51, FineTune = 52;
        public const int SampleID = 53, SampleModes = 54;
        public const int ScaleTuning = 56, OverridingRootKey = 58;
    }

    /// <summary>One mono 16-bit sample. LoopStart/LoopEnd are relative to Data[0]; LoopEnd is exclusive.</summary>
    public sealed class Sf2Sample
    {
        public string Name = "";
        public short[] Data = new short[0];
        public int SampleRate = 22050;
        public int RootKey = 60;
        public int PitchCorrection;      // cents
        public int LoopStart;
        public int LoopEnd;
        public bool Rom;                 // ROM sample (no data available)

        public bool HasLoopPoints { get { return LoopEnd > LoopStart && LoopStart >= 0 && LoopEnd <= Data.Length; } }
    }

    /// <summary>A zone of an instrument (Target = sample index) or of a preset (Target = instrument index).</summary>
    public sealed class Sf2Zone
    {
        public int KeyLo = 0, KeyHi = 127, VelLo = 0, VelHi = 127;
        public bool HasKeyRange, HasVelRange;
        public int Target = -1;          // -1 = global zone
        public readonly Dictionary<int, short> Gens = new Dictionary<int, short>();

        public bool IsGlobal { get { return Target < 0; } }

        public int Gen(int op, int def)
        {
            short v;
            return Gens.TryGetValue(op, out v) ? v : def;
        }
    }

    public sealed class Sf2Instrument
    {
        public string Name = "";
        public List<Sf2Zone> Zones = new List<Sf2Zone>();
    }

    public sealed class Sf2Preset
    {
        public string Name = "";
        public int Preset;               // MIDI program 0-127
        public int Bank;
        public List<Sf2Zone> Zones = new List<Sf2Zone>();
    }

    /// <summary>Reads and writes SoundFont 2 (.sf2) files. 16-bit samples only (sm24 is ignored); modulators are dropped.</summary>
    public sealed class Sf2File
    {
        public string Name = "";
        public List<Sf2Sample> Samples = new List<Sf2Sample>();
        public List<Sf2Instrument> Instruments = new List<Sf2Instrument>();
        public List<Sf2Preset> Presets = new List<Sf2Preset>();

        // ------------------------------------------------------------------ helpers
        private static int U16(byte[] d, int o) { return d[o] | (d[o + 1] << 8); }
        private static uint U32(byte[] d, int o) { return (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)); }
        private static string Tag(byte[] d, int o) { return Encoding.ASCII.GetString(d, o, 4); }

        private static string Str(byte[] d, int o, int max)
        {
            int n = 0;
            while (n < max && o + n < d.Length && d[o + n] != 0) n++;
            return Encoding.ASCII.GetString(d, o, n).Trim();
        }

        // ------------------------------------------------------------------ read
        public static Sf2File Parse(byte[] d)
        {
            if (d == null || d.Length < 12 || Tag(d, 0) != "RIFF" || Tag(d, 8) != "sfbk")
                throw new InvalidDataException("This is not a SoundFont 2 file (the RIFF 'sfbk' header is missing).");

            var f = new Sf2File();
            int smplOff = -1, smplLen = 0;
            var pd = new Dictionary<string, int[]>();

            int pos = 12;
            int end = (int)Math.Min((long)d.Length, 8L + U32(d, 4));
            while (pos + 8 <= end)
            {
                string id = Tag(d, pos);
                int len = (int)Math.Min(U32(d, pos + 4), (uint)(d.Length - pos - 8));
                int body = pos + 8;
                if (id == "LIST" && len >= 4)
                {
                    string type = Tag(d, body);
                    int p = body + 4, e = body + len;
                    while (p + 8 <= e)
                    {
                        string cid = Tag(d, p);
                        int cl = (int)Math.Min(U32(d, p + 4), (uint)(e - p - 8));
                        int cb = p + 8;
                        if (type == "INFO" && cid == "INAM") f.Name = Str(d, cb, cl);
                        else if (type == "sdta" && cid == "smpl") { smplOff = cb; smplLen = cl; }
                        else if (type == "pdta") pd[cid] = new int[] { cb, cl };
                        p = cb + cl + (cl & 1);
                    }
                }
                pos = body + len + (len & 1);
            }

            if (smplOff < 0) throw new InvalidDataException("The SoundFont has no sample data (smpl chunk missing).");
            foreach (string need in new[] { "phdr", "pbag", "pgen", "inst", "ibag", "igen", "shdr" })
                if (!pd.ContainsKey(need)) throw new InvalidDataException("The SoundFont is damaged: '" + need + "' chunk is missing.");

            int totalPoints = smplLen / 2;

            // ---- samples
            int[] shdr = pd["shdr"];
            int nSamp = shdr[1] / 46 - 1;
            for (int i = 0; i < nSamp; i++)
            {
                int o = shdr[0] + i * 46;
                var s = new Sf2Sample();
                s.Name = Str(d, o, 20);
                uint st = U32(d, o + 20), en = U32(d, o + 24), sl = U32(d, o + 28), el = U32(d, o + 32);
                s.SampleRate = (int)U32(d, o + 36);
                s.RootKey = d[o + 40];
                s.PitchCorrection = (sbyte)d[o + 41];
                int type = U16(d, o + 44);
                s.Rom = (type & 0x8000) != 0;

                if (!s.Rom && st < en && en <= (uint)totalPoints)
                {
                    int n = (int)(en - st);
                    s.Data = new short[n];
                    int src = smplOff + (int)st * 2;
                    for (int k = 0; k < n; k++) s.Data[k] = (short)U16(d, src + k * 2);
                    if (sl >= st && el > sl && el <= en) { s.LoopStart = (int)(sl - st); s.LoopEnd = (int)(el - st); }
                }
                if (s.RootKey > 127) s.RootKey = 60;
                if (s.SampleRate < 1) s.SampleRate = 22050;
                f.Samples.Add(s);
            }

            // ---- instruments
            int[] inst = pd["inst"], ibag = pd["ibag"], igen = pd["igen"];
            int nInst = inst[1] / 22 - 1;
            for (int i = 0; i < nInst; i++)
            {
                int o = inst[0] + i * 22;
                var it = new Sf2Instrument { Name = Str(d, o, 20) };
                int b0 = U16(d, o + 20), b1 = U16(d, o + 22 + 20);
                it.Zones = ReadZones(d, ibag, igen, b0, b1, false);
                f.Instruments.Add(it);
            }

            // ---- presets
            int[] phdr = pd["phdr"], pbag = pd["pbag"], pgen = pd["pgen"];
            int nPre = phdr[1] / 38 - 1;
            for (int i = 0; i < nPre; i++)
            {
                int o = phdr[0] + i * 38;
                var p = new Sf2Preset { Name = Str(d, o, 20), Preset = U16(d, o + 20), Bank = U16(d, o + 22) };
                int b0 = U16(d, o + 24), b1 = U16(d, o + 38 + 24);
                p.Zones = ReadZones(d, pbag, pgen, b0, b1, true);
                f.Presets.Add(p);
            }
            return f;
        }

        private static List<Sf2Zone> ReadZones(byte[] d, int[] bag, int[] gen, int b0, int b1, bool preset)
        {
            var list = new List<Sf2Zone>();
            int nBag = bag[1] / 4;
            int nGen = gen[1] / 4;
            for (int b = b0; b < b1 && b < nBag; b++)
            {
                int g0 = U16(d, bag[0] + b * 4);
                int g1 = (b + 1 < nBag) ? U16(d, bag[0] + (b + 1) * 4) : nGen;
                var z = new Sf2Zone();
                for (int g = g0; g < g1 && g < nGen; g++)
                {
                    int o = gen[0] + g * 4;
                    int op = U16(d, o);
                    if (op == Sf2Gen.KeyRange) { z.KeyLo = d[o + 2]; z.KeyHi = d[o + 3]; z.HasKeyRange = true; }
                    else if (op == Sf2Gen.VelRange) { z.VelLo = d[o + 2]; z.VelHi = d[o + 3]; z.HasVelRange = true; }
                    else if (op == (preset ? Sf2Gen.Instrument : Sf2Gen.SampleID)) z.Target = U16(d, o + 2);
                    else z.Gens[op] = (short)U16(d, o + 2);
                }
                list.Add(z);
            }
            return list;
        }

        // ------------------------------------------------------------------ write
        private static byte[] Chunk(string id, byte[] body)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes(id));
            w.Write((uint)body.Length);
            w.Write(body);
            if ((body.Length & 1) != 0) w.Write((byte)0);
            return ms.ToArray();
        }

        private static byte[] List(string type, params byte[][] chunks)
        {
            var inner = new MemoryStream();
            inner.Write(Encoding.ASCII.GetBytes(type), 0, 4);
            foreach (var c in chunks) inner.Write(c, 0, c.Length);
            return Chunk("LIST", inner.ToArray());
        }

        private static byte[] ZStr(string s)
        {
            byte[] raw = Encoding.ASCII.GetBytes(s ?? "");
            int n = raw.Length + 1;
            if ((n & 1) != 0) n++;
            var r = new byte[n];
            Array.Copy(raw, r, raw.Length);
            return r;
        }

        private static void Name20(BinaryWriter w, string s)
        {
            var b = new byte[20];
            byte[] raw = Encoding.ASCII.GetBytes(s ?? "");
            Array.Copy(raw, b, Math.Min(19, raw.Length));
            w.Write(b);
        }

        private static void WriteGens(BinaryWriter w, Sf2Zone z, bool preset, ref int genCount)
        {
            if (z.HasKeyRange) { w.Write((ushort)Sf2Gen.KeyRange); w.Write((byte)z.KeyLo); w.Write((byte)z.KeyHi); genCount++; }
            if (z.HasVelRange) { w.Write((ushort)Sf2Gen.VelRange); w.Write((byte)z.VelLo); w.Write((byte)z.VelHi); genCount++; }
            var keys = new List<int>(z.Gens.Keys);
            keys.Sort();
            foreach (int op in keys)
            {
                if (op == Sf2Gen.KeyRange || op == Sf2Gen.VelRange || op == Sf2Gen.Instrument || op == Sf2Gen.SampleID) continue;
                w.Write((ushort)op); w.Write(z.Gens[op]); genCount++;
            }
            if (z.Target >= 0)
            {
                w.Write((ushort)(preset ? Sf2Gen.Instrument : Sf2Gen.SampleID));
                w.Write((ushort)z.Target);
                genCount++;
            }
        }

        public byte[] Build()
        {
            if (Samples.Count == 0) throw new InvalidOperationException("The SoundFont needs at least one sample.");
            if (Presets.Count == 0) throw new InvalidOperationException("The SoundFont needs at least one preset.");

            // INFO
            var ifil = new byte[] { 2, 0, 1, 0 };
            byte[] info = List("INFO",
                Chunk("ifil", ifil),
                Chunk("isng", ZStr("EMU8000")),
                Chunk("INAM", ZStr(string.IsNullOrEmpty(Name) ? "PS2AudioStudio" : Name)),
                Chunk("ISFT", ZStr("PS2 Audio Studio")));

            // sdta: every sample followed by 46 zero points
            var smpl = new MemoryStream();
            var sw = new BinaryWriter(smpl);
            var starts = new int[Samples.Count];
            for (int i = 0; i < Samples.Count; i++)
            {
                starts[i] = (int)(smpl.Length / 2);
                foreach (short v in Samples[i].Data) sw.Write(v);
                for (int k = 0; k < 46; k++) sw.Write((short)0);
            }
            byte[] sdta = List("sdta", Chunk("smpl", smpl.ToArray()));

            // pdta
            var phdr = new MemoryStream(); var pbag = new MemoryStream(); var pmod = new MemoryStream(); var pgen = new MemoryStream();
            var inst = new MemoryStream(); var ibag = new MemoryStream(); var imod = new MemoryStream(); var igen = new MemoryStream();
            var shdr = new MemoryStream();
            var wph = new BinaryWriter(phdr); var wpb = new BinaryWriter(pbag); var wpm = new BinaryWriter(pmod); var wpg = new BinaryWriter(pgen);
            var win = new BinaryWriter(inst); var wib = new BinaryWriter(ibag); var wim = new BinaryWriter(imod); var wig = new BinaryWriter(igen);
            var wsh = new BinaryWriter(shdr);

            int pBags = 0, pGens = 0, iBags = 0, iGens = 0;

            foreach (var p in Presets)
            {
                Name20(wph, p.Name);
                wph.Write((ushort)p.Preset); wph.Write((ushort)p.Bank); wph.Write((ushort)pBags);
                wph.Write(0u); wph.Write(0u); wph.Write(0u);
                foreach (var z in p.Zones)
                {
                    wpb.Write((ushort)pGens); wpb.Write((ushort)0); pBags++;
                    WriteGens(wpg, z, true, ref pGens);
                }
            }
            Name20(wph, "EOP"); wph.Write((ushort)0); wph.Write((ushort)0); wph.Write((ushort)pBags); wph.Write(0u); wph.Write(0u); wph.Write(0u);
            wpb.Write((ushort)pGens); wpb.Write((ushort)0);
            wpm.Write(new byte[10]);
            wpg.Write(new byte[4]);

            foreach (var it in Instruments)
            {
                Name20(win, it.Name);
                win.Write((ushort)iBags);
                foreach (var z in it.Zones)
                {
                    wib.Write((ushort)iGens); wib.Write((ushort)0); iBags++;
                    WriteGens(wig, z, false, ref iGens);
                }
            }
            Name20(win, "EOI"); win.Write((ushort)iBags);
            wib.Write((ushort)iGens); wib.Write((ushort)0);
            wim.Write(new byte[10]);
            wig.Write(new byte[4]);

            for (int i = 0; i < Samples.Count; i++)
            {
                var s = Samples[i];
                int st = starts[i];
                int en = st + s.Data.Length;
                int ls = st, le = st;
                if (s.HasLoopPoints) { ls = st + s.LoopStart; le = st + s.LoopEnd; }
                Name20(wsh, s.Name);
                wsh.Write((uint)st); wsh.Write((uint)en); wsh.Write((uint)ls); wsh.Write((uint)le);
                wsh.Write((uint)s.SampleRate);
                wsh.Write((byte)Math.Max(0, Math.Min(127, s.RootKey)));
                wsh.Write((sbyte)Math.Max(-99, Math.Min(99, s.PitchCorrection)));
                wsh.Write((ushort)0);          // sample link
                wsh.Write((ushort)1);          // mono
            }
            Name20(wsh, "EOS");
            wsh.Write(new byte[26]);

            byte[] pdta = List("pdta",
                Chunk("phdr", phdr.ToArray()), Chunk("pbag", pbag.ToArray()), Chunk("pmod", pmod.ToArray()), Chunk("pgen", pgen.ToArray()),
                Chunk("inst", inst.ToArray()), Chunk("ibag", ibag.ToArray()), Chunk("imod", imod.ToArray()), Chunk("igen", igen.ToArray()),
                Chunk("shdr", shdr.ToArray()));

            var body = new MemoryStream();
            body.Write(Encoding.ASCII.GetBytes("sfbk"), 0, 4);
            body.Write(info, 0, info.Length);
            body.Write(sdta, 0, sdta.Length);
            body.Write(pdta, 0, pdta.Length);
            return Chunk("RIFF", body.ToArray());
        }

        public string Summary()
        {
            int pts = 0;
            foreach (var s in Samples) pts += s.Data.Length;
            return Name + ": " + Presets.Count + " preset(s), " + Instruments.Count + " instrument(s), " + Samples.Count + " sample(s), " + pts.ToString("N0") + " sample points";
        }
    }
}
