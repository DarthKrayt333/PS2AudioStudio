using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using PS2AudioStudio.Archives;

namespace PS2AudioStudio.Audio
{
    public enum FileKind
    {
        Unknown, Vag, Adx, Wav, Mp3, Flac, Ogg, M4a, Afs, PsfHd, PsfSq, Midi, Sfd, Bin
    }

    /// <summary>Thrown when a file needs the "raw PS-ADPCM" import dialog (headerless / interleaved VAG).</summary>
    public class RawImportRequiredException : Exception
    {
        public byte[] RawData { get; }
        public string FileName { get; }
        public RawImportRequiredException(byte[] data, string name) : base("Raw PS-ADPCM import required.") { RawData = data; FileName = name; }
    }

    /// <summary>One place that knows how to recognise, load and save every format (used by ALL tabs / drag&amp;drop).</summary>
    public static class AudioIO
    {
        public const string OpenFilter =
            "All supported|*.vag;*.adx;*.wav;*.mp3;*.m4a;*.aac;*.flac;*.wma;*.ogg;*.afs;*.hd;*.sq;*.mid;*.midi;*.bd|" +
            "Audio (VAG, ADX, WAV, MP3, M4A...)|*.vag;*.adx;*.wav;*.mp3;*.m4a;*.aac;*.flac;*.wma;*.ogg|" +
            "PSF2 bank / sequence (HD, SQ, BD)|*.hd;*.sq;*.bd|AFS archive|*.afs|MIDI|*.mid;*.midi|All files|*.*";

        public const string AudioOnlyFilter =
            "Audio|*.vag;*.adx;*.wav;*.mp3;*.m4a;*.aac;*.flac;*.wma;*.ogg|VAG|*.vag|ADX|*.adx|WAV|*.wav|MP3|*.mp3|All files|*.*";

        public static bool IsAudio(FileKind k) =>
            k == FileKind.Vag || k == FileKind.Adx || k == FileKind.Wav || k == FileKind.Mp3 ||
            k == FileKind.Flac || k == FileKind.Ogg || k == FileKind.M4a;

        public static FileKind Detect(byte[] d, string name = null)
        {
            string ext = string.IsNullOrEmpty(name) ? "" : Path.GetExtension(name).ToLowerInvariant();

            if (d != null && d.Length >= 4)
            {
                if (d[0] == 'V' && d[1] == 'A' && d[2] == 'G') return FileKind.Vag;
                if (d[0] == 'p' && d[1] == 'G' && d[2] == 'A' && d[3] == 'V') return FileKind.Vag;
                if (d[0] == 0x80 && d[1] == 0x00 && d.Length > 0x16)
                {
                    int cpy = (d[2] << 8) | d[3];
                    if (cpy >= 6 && cpy + 4 <= d.Length && cpy - 2 + 6 <= d.Length &&
                        d[cpy - 2] == '(' && d[cpy - 1] == 'c' && d[cpy] == ')') return FileKind.Adx;
                    if (d[4] >= 2 && d[4] <= 4) return FileKind.Adx;
                }
                if (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F') return FileKind.Wav;
                if (d[0] == 'O' && d[1] == 'g' && d[2] == 'g' && d[3] == 'S') return FileKind.Ogg;
                if (d[0] == 'f' && d[1] == 'L' && d[2] == 'a' && d[3] == 'C') return FileKind.Flac;
                if (d[0] == 'A' && d[1] == 'F' && d[2] == 'S' && d[3] == 0) return FileKind.Afs;
                if (d[0] == 'M' && d[1] == 'T' && d[2] == 'h' && d[3] == 'd') return FileKind.Midi;
                if (d[0] == 'C' && d[1] == 'R' && d[2] == 'I' && d[3] == 'D') return FileKind.Sfd;
                if (d[0] == 'I' && d[1] == 'D' && d[2] == '3') return FileKind.Mp3;
                if (d.Length > 8 && d[4] == 'f' && d[5] == 't' && d[6] == 'y' && d[7] == 'p') return FileKind.M4a;
                if (d[0] == 'I' && d[1] == 'E' && d[2] == 'C' && d[3] == 'S')
                    return Psf2Bank.IsSequenceFile(d) ? FileKind.PsfSq : FileKind.PsfHd;
                if (d[0] == 0xFF && (d[1] & 0xE0) == 0xE0 && ext == ".mp3") return FileKind.Mp3;
            }

            switch (ext)
            {
                case ".vag": return FileKind.Vag;
                case ".adx": return FileKind.Adx;
                case ".wav": return FileKind.Wav;
                case ".mp3": return FileKind.Mp3;
                case ".m4a": case ".aac": case ".mp4": return FileKind.M4a;
                case ".flac": return FileKind.Flac;
                case ".ogg": return FileKind.Ogg;
                case ".afs": return FileKind.Afs;
                case ".hd": return FileKind.PsfHd;
                case ".sq": return FileKind.PsfSq;
                case ".mid": case ".midi": return FileKind.Midi;
                case ".sfd": return FileKind.Sfd;
                case ".bd": return FileKind.Bin;
            }
            return FileKind.Unknown;
        }

        public static string ExtensionFor(FileKind k)
        {
            switch (k)
            {
                case FileKind.Vag: return ".vag";
                case FileKind.Adx: return ".adx";
                case FileKind.Wav: return ".wav";
                case FileKind.Mp3: return ".mp3";
                case FileKind.Flac: return ".flac";
                case FileKind.Ogg: return ".ogg";
                case FileKind.M4a: return ".m4a";
                case FileKind.Afs: return ".afs";
                case FileKind.PsfHd: return ".hd";
                case FileKind.PsfSq: return ".sq";
                case FileKind.Midi: return ".mid";
                case FileKind.Sfd: return ".sfd";
                default: return ".bin";
            }
        }

        public static string KindLabel(FileKind k)
        {
            switch (k)
            {
                case FileKind.Vag: return "VAG";
                case FileKind.Adx: return "ADX";
                case FileKind.Wav: return "WAV";
                case FileKind.Mp3: return "MP3";
                case FileKind.Flac: return "FLAC";
                case FileKind.Ogg: return "OGG";
                case FileKind.M4a: return "M4A";
                case FileKind.Afs: return "AFS";
                case FileKind.PsfHd: return "HD";
                case FileKind.PsfSq: return "SQ";
                case FileKind.Midi: return "MIDI";
                case FileKind.Sfd: return "SFD";
                default: return "BIN";
            }
        }

        // ------------------------------------------------------------------ decode

        public static AudioBuffer Decode(byte[] data, string nameHint)
        {
            FileKind k = Detect(data, nameHint);
            switch (k)
            {
                case FileKind.Vag:
                    if (VagCodec.IsInterleavedHeader(data) || !VagCodec.HasHeader(data))
                        throw new RawImportRequiredException(data, nameHint);
                    return new VagCodec().Decode(data);
                case FileKind.Adx:
                    return new AdxCodec().Decode(data);
                case FileKind.Wav:
                case FileKind.Mp3:
                case FileKind.Flac:
                case FileKind.Ogg:
                case FileKind.M4a:
                    string ext = Path.GetExtension(nameHint ?? "");
                    if (string.IsNullOrEmpty(ext)) ext = ExtensionFor(k);
                    return StandardCodec.DecodeBytes(data, ext);
            }
            throw new InvalidDataException("Not a supported audio format.");
        }

        public static AudioBuffer Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".vag" && !VagCodec.HasHeader(data))
                throw new RawImportRequiredException(data, path);

            FileKind k = Detect(data, path);
            if (k == FileKind.Vag || k == FileKind.Adx)
                return Decode(data, path);

            // WAV/MP3/M4A: let NAudio read straight from disk (faster, handles any container)
            return StandardCodec.DecodeFile(path);
        }

        // ------------------------------------------------------------------ resample

        /// <summary>
        /// High-quality (windowed-sinc, Blackman) sample-rate conversion. Loop points are scaled to the new rate.
        /// Returns the SAME buffer when the rate already matches.
        /// </summary>
        public static AudioBuffer Resample(AudioBuffer src, int newRate)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (newRate < 1000 || newRate > 192000)
                throw new ArgumentOutOfRangeException(nameof(newRate), "Sample rate must be between 1000 and 192000 Hz.");
            if (newRate == src.SampleRate || src.TotalFrames == 0) return src;

            int ch = src.Channels, inF = src.TotalFrames;
            double ratio = (double)src.SampleRate / newRate;               // input frames per output frame
            int outF = (int)Math.Max(1, Math.Round(inF / ratio));
            double cutoff = ratio > 1.0 ? 0.96 / ratio : 1.0;              // low-pass when going down
            int half = (int)Math.Ceiling(16.0 / cutoff);                   // kernel half width (input frames)

            // kernel table: 256 steps per input frame, linear interpolation
            const int RES = 256;
            int tabLen = 2 * half * RES + 2;
            var tab = new float[tabLen];
            for (int i = 0; i < tabLen; i++)
            {
                double d = (double)i / RES - half;
                double x = d / half;
                double win = Math.Abs(x) >= 1.0 ? 0.0 : 0.42 + 0.5 * Math.Cos(Math.PI * x) + 0.08 * Math.Cos(2.0 * Math.PI * x);
                double a = Math.PI * cutoff * d;
                double sinc = Math.Abs(a) < 1e-9 ? 1.0 : Math.Sin(a) / a;
                tab[i] = (float)(cutoff * sinc * win);
            }

            float[] S = src.Samples;
            var O = new float[outF * ch];
            const int chunk = 2048;
            int nChunks = (outF + chunk - 1) / chunk;

            Parallel.For(0, nChunks, ci =>
            {
                var wt = new float[2 * half];
                int oa = ci * chunk, ob = Math.Min(outF, oa + chunk);
                for (int o = oa; o < ob; o++)
                {
                    double t = o * ratio;
                    int c0 = (int)Math.Floor(t);
                    int lo = Math.Max(c0 - half + 1, 0);
                    int hi = Math.Min(c0 + half, inF - 1);
                    int n = 0;
                    for (int k = lo; k <= hi; k++, n++)
                    {
                        double p = (k - t + half) * RES;
                        int i0 = (int)p;
                        float fr = (float)(p - i0);
                        wt[n] = tab[i0] + (tab[i0 + 1] - tab[i0]) * fr;
                    }
                    for (int c = 0; c < ch; c++)
                    {
                        float acc = 0f;
                        for (int j = 0, k = lo; j < n; j++, k++) acc += wt[j] * S[k * ch + c];
                        if (acc > 1f) acc = 1f; else if (acc < -1f) acc = -1f;
                        O[o * ch + c] = acc;
                    }
                }
            });

            var res = new AudioBuffer(O, newRate, ch);
            if (src.LoopEnabled && src.LoopEnd > src.LoopStart)
            {
                int ls = (int)Math.Round(src.LoopStart / ratio);
                int le = (int)Math.Round(src.LoopEnd / ratio);
                res.LoopEnabled = true;
                res.LoopEnd = Math.Min(outF, Math.Max(1, le));
                res.LoopStart = Math.Max(0, Math.Min(ls, res.LoopEnd - 1));
                res.ClampLoopPoints();
            }
            return res;
        }

        // ------------------------------------------------------------------ encode

        /// <summary>
        /// Encode for the given container. Loop data is written ONLY when buf.LoopEnabled is true.
        /// targetRate &gt; 0 resamples first (loop points are scaled); 0 = keep the buffer's rate.
        /// </summary>
        public static byte[] Encode(AudioBuffer buf, FileKind kind, string name = null, int targetRate = 0)
        {
            if (targetRate > 0 && buf != null && targetRate != buf.SampleRate)
                buf = Resample(buf, targetRate);

            switch (kind)
            {
                case FileKind.Vag:
                    {
                        var b = buf.Clone();
                        b.SnapLoop(VagCodec.SamplesPerBlock);
                        return new VagCodec().SaveToBytes(b, name == null ? "sample" : Path.GetFileNameWithoutExtension(name));
                    }
                case FileKind.Adx:
                    {
                        var b = buf.Clone();
                        b.SnapLoop(32);
                        return new AdxCodec().Encode(b);
                    }
                case FileKind.Wav:
                    return StandardCodec.EncodeWavBytes(buf);
            }
            throw new NotSupportedException("Cannot encode to " + kind);
        }
    }
}