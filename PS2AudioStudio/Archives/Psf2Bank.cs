using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PS2AudioStudio.Audio;

namespace PS2AudioStudio.Archives
{
    public class BdNotFoundException : FileNotFoundException
    {
        public string HdPath { get; }
        public BdNotFoundException(string hdPath, string msg) : base(msg) { HdPath = hdPath; }
    }

    public class Psf2Sample
    {
        public int Index;
        public bool Valid;                 // has data inside the BD
        public int ParamOffset;            // absolute offset of the VAGInfo param inside the HD
        public uint BdOffset;              // original offset inside the BD
        public int SampleRate;
        public byte Attribute;             // 0 = one-shot, 1 = loop (from the HD)
        public byte[] Raw;                 // PS-ADPCM bytes of this slot
        public AudioBuffer Decoded;

        public byte[] OrigRaw;
        public int OrigRate;
        public byte OrigAttr;
        public bool Replaced;

        public int BdSize => Raw == null ? 0 : Raw.Length;
        public bool Loops => Attribute != 0;
    }

    public class Psf2Split
    {
        public int SampleSetIndex, RangeLow, RangeHigh, Volume, Panpot, Transpose, Detune;
    }

    public class Psf2Program
    {
        public int Index, Volume, Panpot, Transpose, Detune;
        public List<Psf2Split> Splits = new List<Psf2Split>();
    }

    public class Psf2SampleSet
    {
        public int Index, VelCurve, VelLow, VelHigh;
        public List<int> SampleParams = new List<int>();
    }

    public class Psf2SampleParam
    {
        public int Index, VagIndex, VelLow, VelHigh, BaseNote, Detune, Panpot, Volume;
        public ushort Adsr1, Adsr2;
    }

    /// <summary>
    /// PSF2 / SCEI sound bank: HD (header: programs, sample sets, sample params, VAG table) + BD (body: PS-ADPCM data).
    /// Layout follows the SCEI "Sony PS2 InstrSet" format (little endian).
    /// </summary>
    public class Psf2Bank
    {
        public List<Psf2Sample> Samples { get; } = new List<Psf2Sample>();
        public Dictionary<int, Psf2Program> Programs { get; } = new Dictionary<int, Psf2Program>();
        public Psf2SampleSet[] SampleSets { get; private set; } = new Psf2SampleSet[0];
        public Psf2SampleParam[] SampleParams { get; private set; } = new Psf2SampleParam[0];

        public string HdPath { get; private set; }
        public string BdPath { get; private set; }
        public bool Loaded => hd != null;
        public bool Modified { get; set; }

        private byte[] hd;
        private byte[] bdOrig;
        private int hdrOff = 0x10;

        private static uint U32(byte[] d, long o) => BitConverter.ToUInt32(d, (int)o);
        private static int U16(byte[] d, long o) => BitConverter.ToUInt16(d, (int)o);

        // =========================================================== identification

        public static bool IsSequenceFile(byte[] d)
        {
            try
            {
                if (d == null || d.Length < 0x30) return false;
                if (Encoding.ASCII.GetString(d, 0, 4) != "IECS") return false;
                int vers = (int)U32(d, 8);
                if (vers < 0x10 || vers > 0x100) vers = 0x10;
                if (vers + 16 > d.Length) return false;
                int hdrSize = (int)U32(d, vers + 8);
                if (hdrSize < 0x10 || hdrSize > 0x1000) return false;
                long mid = vers + hdrSize;
                if (mid + 8 > d.Length) return false;
                return Encoding.ASCII.GetString(d, (int)mid, 8) == "IECSidiM";
            }
            catch { return false; }
        }

        public static string FindBdFor(string hdPath)
        {
            string dir = Path.GetDirectoryName(hdPath);
            string baseName = Path.GetFileNameWithoutExtension(hdPath);
            foreach (string ext in new[] { ".bd", ".BD", ".Bd", ".bin", ".BIN" })
            {
                string c = Path.Combine(dir, baseName + ext);
                if (File.Exists(c)) return c;
            }
            return null;
        }

        // =========================================================== load

        public void LoadHD(string hdPath, string bdPath = null)
        {
            byte[] h = File.ReadAllBytes(hdPath);
            if (h.Length < 0x40) throw new InvalidDataException("HD file too small.");
            string magic = Encoding.ASCII.GetString(h, 0, 8);
            if (magic != "IECSsreV")
                throw new InvalidDataException($"Not a SCEI HD file (expected 'IECSsreV', got '{magic}').");

            if (bdPath == null) bdPath = FindBdFor(hdPath);
            if (bdPath == null || !File.Exists(bdPath))
                throw new BdNotFoundException(hdPath,
                    $"Cannot find the BD body file next to the HD.\nLooked for {Path.GetFileNameWithoutExtension(hdPath)}.bd / .BD / .bin");

            byte[] bd = File.ReadAllBytes(bdPath);

            int vers = (int)U32(h, 8);
            if (vers < 0x10 || vers > 0x100) vers = 0x10;
            int hOff = vers;                                    // header chunk
            if (hOff + 0x24 > h.Length) throw new InvalidDataException("HD header chunk is truncated.");

            uint progAddr = U32(h, hOff + 20);
            uint setAddr = U32(h, hOff + 24);
            uint smpAddr = U32(h, hOff + 28);
            uint vagiAddr = U32(h, hOff + 32);

            if (vagiAddr + 0x10 > h.Length) throw new InvalidDataException("VAGI chunk offset out of bounds.");
            if (Encoding.ASCII.GetString(h, (int)vagiAddr, 8) != "IECSigaV")
                throw new InvalidDataException("VAGI chunk magic mismatch.");

            uint numVags = U32(h, vagiAddr + 12) + 1;           // stored value is the MAX index
            if (numVags == 0 || numVags > 20000) throw new InvalidDataException($"Invalid VAG count {numVags}.");

            // ---- VAG info table
            var list = new List<Psf2Sample>();
            for (int i = 0; i < numVags; i++)
            {
                var s = new Psf2Sample { Index = i };
                long tbl = vagiAddr + 16 + i * 4L;
                if (tbl + 4 <= h.Length)
                {
                    uint rel = U32(h, tbl);
                    if (rel != 0xFFFFFFFF)
                    {
                        long p = vagiAddr + rel;
                        if (p + 8 <= h.Length)
                        {
                            s.ParamOffset = (int)p;
                            s.BdOffset = U32(h, p);
                            s.SampleRate = U16(h, p + 4);
                            s.Attribute = h[p + 6];
                            s.Valid = s.BdOffset < bd.Length;
                        }
                    }
                }
                list.Add(s);
            }

            // ---- slot sizes (data runs until the next sample's offset / end of BD)
            var offs = list.Where(s => s.Valid).Select(s => s.BdOffset).Distinct().OrderBy(x => x).ToList();
            foreach (var s in list.Where(x => x.Valid))
            {
                int idx = offs.IndexOf(s.BdOffset);
                long end = idx + 1 < offs.Count ? offs[idx + 1] : bd.Length;
                int size = (int)(end - s.BdOffset);
                s.Raw = new byte[size];
                Array.Copy(bd, s.BdOffset, s.Raw, 0, size);
                s.OrigRaw = s.Raw;
                s.OrigRate = s.SampleRate;
                s.OrigAttr = s.Attribute;
                s.Decoded = DecodeSample(s.Raw, s.SampleRate > 0 ? s.SampleRate : 44100, s.Attribute);
            }

            hd = h; bdOrig = bd; hdrOff = hOff;
            HdPath = hdPath; BdPath = bdPath;
            Samples.Clear(); Samples.AddRange(list);
            Modified = false;

            ParseInstruments(progAddr, setAddr, smpAddr);
        }

        public static AudioBuffer DecodeSample(byte[] raw, int rate, byte attr)
        {
            AudioBuffer b;
            try { b = VagCodec.DecodeAdpcm(raw, 0, raw.Length, rate); }
            catch { return null; }
            if (attr == 0) { b.LoopEnabled = false; }
            else if (!b.LoopEnabled && b.TotalFrames > 1)
            {
                b.LoopEnabled = true; b.LoopStart = 0; b.LoopEnd = b.TotalFrames;
            }
            return b;
        }

        // =========================================================== instruments

        private void ParseInstruments(uint progAddr, uint setAddr, uint smpAddr)
        {
            Programs.Clear();
            SampleSets = new Psf2SampleSet[0];
            SampleParams = new Psf2SampleParam[0];
            byte[] d = hd;

            try   // sample params
            {
                long c = smpAddr;
                int n = (int)Math.Min(U32(d, c + 12) + 1, 20000u);
                SampleParams = new Psf2SampleParam[n];
                for (int i = 0; i < n; i++)
                {
                    uint rel = U32(d, c + 16 + i * 4L);
                    if (rel == 0xFFFFFFFF) continue;
                    long p = c + rel;
                    if (p + 22 > d.Length) continue;
                    SampleParams[i] = new Psf2SampleParam
                    {
                        Index = i,
                        VagIndex = U16(d, p),
                        VelLow = d[p + 2],
                        VelHigh = d[p + 4],
                        BaseNote = d[p + 11],
                        Detune = (sbyte)d[p + 12],
                        Panpot = d[p + 13],
                        Volume = d[p + 16],
                        Adsr1 = (ushort)U16(d, p + 18),
                        Adsr2 = (ushort)U16(d, p + 20)
                    };
                }
            }
            catch { }

            try   // sample sets
            {
                long c = setAddr;
                int n = (int)Math.Min(U32(d, c + 12) + 1, 20000u);
                SampleSets = new Psf2SampleSet[n];
                for (int i = 0; i < n; i++)
                {
                    uint rel = U32(d, c + 16 + i * 4L);
                    if (rel == 0xFFFFFFFF) continue;
                    long p = c + rel;
                    if (p + 4 > d.Length) continue;
                    var set = new Psf2SampleSet { Index = i, VelCurve = d[p], VelLow = d[p + 1], VelHigh = d[p + 2] };
                    int ns = d[p + 3];
                    for (int j = 0; j < ns && p + 4 + j * 2 + 2 <= d.Length; j++) set.SampleParams.Add(U16(d, p + 4 + j * 2));
                    SampleSets[i] = set;
                }
            }
            catch { }

            try   // programs
            {
                long c = progAddr;
                int n = (int)Math.Min(U32(d, c + 12) + 1, 20000u);
                for (int i = 0; i < n; i++)
                {
                    uint rel = U32(d, c + 16 + i * 4L);
                    if (rel == 0xFFFFFFFF) continue;
                    long p = c + rel;
                    if (p + 10 > d.Length) continue;
                    var prog = new Psf2Program
                    {
                        Index = i,
                        Volume = d[p + 6],
                        Panpot = d[p + 7],
                        Transpose = (sbyte)d[p + 8],
                        Detune = (sbyte)d[p + 9]
                    };
                    long splitAddr = p + U32(d, p);
                    int nSplit = d[p + 4];
                    int sz = d[p + 5]; if (sz < 20) sz = 20;
                    for (int j = 0; j < nSplit; j++)
                    {
                        long sp = splitAddr + j * (long)sz;
                        if (sp + 20 > d.Length) break;
                        prog.Splits.Add(new Psf2Split
                        {
                            SampleSetIndex = U16(d, sp),
                            RangeLow = d[sp + 2],
                            RangeHigh = d[sp + 4],
                            Volume = d[sp + 16],
                            Panpot = d[sp + 17],
                            Transpose = (sbyte)d[sp + 18],
                            Detune = (sbyte)d[sp + 19]
                        });
                    }
                    Programs[i] = prog;
                }
            }
            catch { }
        }

        // =========================================================== editing

        /// <summary>Replace a sample by new audio. Looping is taken from the audio's LoopEnabled flag.</summary>
        public void ReplaceSample(int index, AudioBuffer src, bool keepOriginalRate)
        {
            var s = Samples[index];
            if (!s.Valid) throw new InvalidOperationException("This slot is empty (no data in BD) and cannot be replaced.");

            AudioBuffer b = src.Clone();
            if (b.Channels > 1)
            {
                var m = new AudioBuffer(b.MixToMono(), b.SampleRate, 1)
                { LoopEnabled = b.LoopEnabled, LoopStart = b.LoopStart, LoopEnd = b.LoopEnd };
                b = m;
            }

            int rate = keepOriginalRate && s.OrigRate >= 1000 ? s.OrigRate : b.SampleRate;
            if (rate > 65535) rate = 65535;
            if (b.SampleRate != rate) b = b.Resample(rate);

            b.SnapLoop(VagCodec.SamplesPerBlock);
            bool loop = b.LoopEnabled && b.LoopEnd > b.LoopStart;
            byte[] raw = VagCodec.EncodeAdpcm(b.Samples, loop, b.LoopStart, b.LoopEnd);

            s.Raw = raw;
            s.SampleRate = rate;
            s.Attribute = (byte)(loop ? 1 : 0);
            s.Replaced = true;
            s.Decoded = DecodeSample(raw, rate, s.Attribute);
            Modified = true;
        }

        /// <summary>Replace with already-encoded PS-ADPCM (e.g. from a .vag file) - no re-encoding, no quality loss.</summary>
        public void ReplaceSampleRaw(int index, byte[] adpcm, int rate, bool loop)
        {
            var s = Samples[index];
            if (!s.Valid) throw new InvalidOperationException("This slot is empty and cannot be replaced.");
            int len = adpcm.Length / 16 * 16;
            var raw = new byte[len];
            Array.Copy(adpcm, raw, len);
            s.Raw = raw;
            s.SampleRate = Math.Min(65535, rate);
            s.Attribute = (byte)(loop ? 1 : 0);
            s.Replaced = true;
            s.Decoded = DecodeSample(raw, s.SampleRate, s.Attribute);
            Modified = true;
        }

        public void Revert(int index)
        {
            var s = Samples[index];
            if (!s.Replaced) return;
            s.Raw = s.OrigRaw; s.SampleRate = s.OrigRate; s.Attribute = s.OrigAttr; s.Replaced = false;
            s.Decoded = DecodeSample(s.Raw, s.SampleRate > 0 ? s.SampleRate : 44100, s.Attribute);
            Modified = Samples.Any(x => x.Replaced);
        }

        // =========================================================== extract

        /// <summary>Lossless .vag extraction (VAGp header + the original PS-ADPCM bytes).</summary>
        public byte[] ExtractVag(int index)
        {
            var s = Samples[index];
            string baseName = HdPath == null ? "bank" : Path.GetFileNameWithoutExtension(HdPath);
            return VagCodec.WrapRaw(s.Raw, s.SampleRate > 0 ? s.SampleRate : 44100, $"{baseName}_{index:D3}");
        }

        public string SampleFileName(int index) =>
            $"{(HdPath == null ? "bank" : Path.GetFileNameWithoutExtension(HdPath))}_{index:D3}";

        // =========================================================== save

        public void BuildFiles(out byte[] newHd, out byte[] newBd)
        {
            if (hd == null) throw new InvalidOperationException("No bank loaded.");
            var valid = Samples.Where(s => s.Valid).ToList();
            var newOff = new Dictionary<int, uint>();

            using var ms = new MemoryStream();
            if (valid.Count > 0)
            {
                long minOrig = valid.Min(s => (long)s.BdOffset);
                if (minOrig > 0) ms.Write(bdOrig, 0, (int)Math.Min(minOrig, bdOrig.Length));   // keep any reserved lead-in

                var groups = valid
                    .GroupBy(s => s.Replaced ? -(long)(s.Index + 1) : (long)s.BdOffset)
                    .OrderBy(g => g.Min(s => (long)s.BdOffset)).ThenBy(g => g.Min(s => s.Index))
                    .ToList();

                foreach (var g in groups)
                {
                    while (ms.Position % 16 != 0) ms.WriteByte(0);
                    uint off = (uint)ms.Position;
                    byte[] data = g.First().Raw;
                    foreach (var s in g) newOff[s.Index] = off;
                    ms.Write(data, 0, data.Length);
                }
            }
            newBd = ms.ToArray();

            newHd = (byte[])hd.Clone();
            Array.Copy(BitConverter.GetBytes((uint)newBd.Length), 0, newHd, hdrOff + 16, 4);
            foreach (var s in valid)
            {
                int p = s.ParamOffset;
                Array.Copy(BitConverter.GetBytes(newOff[s.Index]), 0, newHd, p, 4);
                Array.Copy(BitConverter.GetBytes((ushort)s.SampleRate), 0, newHd, p + 4, 2);
                newHd[p + 6] = s.Attribute;
            }
        }

        public void Save(string hdPath, string bdPath)
        {
            BuildFiles(out byte[] h, out byte[] b);
            File.WriteAllBytes(hdPath, h);
            File.WriteAllBytes(bdPath, b);

            // the saved files become the new baseline
            hd = h; bdOrig = b; HdPath = hdPath; BdPath = bdPath;
            var offs = Samples.Where(s => s.Valid).ToList();
            foreach (var s in offs)
            {
                s.BdOffset = BitConverter.ToUInt32(h, s.ParamOffset);
                s.OrigRaw = s.Raw; s.OrigRate = s.SampleRate; s.OrigAttr = s.Attribute; s.Replaced = false;
            }
            Modified = false;
        }
    }
}
