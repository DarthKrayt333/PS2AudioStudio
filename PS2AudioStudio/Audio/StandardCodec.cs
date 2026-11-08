using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace PS2AudioStudio.Audio
{
    /// <summary>WAV / MP3 / M4A / FLAC / AAC / WMA ... via NAudio (+ Windows Media Foundation).</summary>
    public static class StandardCodec
    {
        public static AudioBuffer DecodeFile(string filePath)
        {
            float[] all;
            int rate, ch;
            using (var reader = new AudioFileReader(filePath))
            {
                rate = reader.WaveFormat.SampleRate;
                ch = reader.WaveFormat.Channels;
                var list = new List<float[]>();
                int totalRead = 0;
                float[] chunk = new float[rate * ch];              // ~1 second per read
                int n;
                while ((n = reader.Read(chunk, 0, chunk.Length)) > 0)
                {
                    var part = new float[n];
                    Array.Copy(chunk, part, n);
                    list.Add(part);
                    totalRead += n;
                }
                all = new float[totalRead];
                int o = 0;
                foreach (var p in list) { Array.Copy(p, 0, all, o, p.Length); o += p.Length; }
            }

            var buf = new AudioBuffer(all, rate, ch) { BitsPerSample = 32 };
            if (ch > 2) buf = buf.ToChannels(2);

            if (string.Equals(Path.GetExtension(filePath), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                try { ReadWavLoop(File.ReadAllBytes(filePath), buf); } catch { /* no loop info */ }
            }
            return buf;
        }

        /// <summary>Decode from memory (used for AFS entries etc.) by round-tripping through a temp file.</summary>
        public static AudioBuffer DecodeBytes(byte[] data, string extension)
        {
            if (string.IsNullOrEmpty(extension)) extension = ".wav";
            string tmp = Path.Combine(Path.GetTempPath(), "ps2as_" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                File.WriteAllBytes(tmp, data);
                return DecodeFile(tmp);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private static void ReadWavLoop(byte[] d, AudioBuffer buf)
        {
            if (d.Length < 12 || d[0] != 'R' || d[1] != 'I') return;
            int p = 12;
            while (p + 8 <= d.Length)
            {
                string id = Encoding.ASCII.GetString(d, p, 4);
                int size = BitConverter.ToInt32(d, p + 4);
                int body = p + 8;
                if (id == "smpl" && size >= 36 && body + 36 <= d.Length)
                {
                    int numLoops = BitConverter.ToInt32(d, body + 28);
                    if (numLoops > 0 && body + 36 + 24 <= d.Length)
                    {
                        int start = BitConverter.ToInt32(d, body + 36 + 8);
                        int end = BitConverter.ToInt32(d, body + 36 + 12);      // inclusive in WAV
                        if (end >= start && start >= 0)
                        {
                            buf.LoopEnabled = true;
                            buf.LoopStart = start;
                            buf.LoopEnd = end + 1;
                            buf.ClampLoopPoints();
                        }
                    }
                    return;
                }
                if (size < 0) return;
                p = body + size + (size & 1);
            }
        }

        // ------------------------------------------------------------------ WAV writer

        public static byte[] EncodeWavBytes(AudioBuffer buffer)
        {
            using var ms = new MemoryStream();
            WriteWav(buffer, ms);
            return ms.ToArray();
        }

        public static void EncodeWav(AudioBuffer buffer, string filePath)
        {
            using var fs = File.Create(filePath);
            WriteWav(buffer, fs);
        }

        private static void WriteWav(AudioBuffer buffer, Stream output)
        {
            byte[] pcm = buffer.ToPcm16();
            int ch = buffer.Channels, sr = buffer.SampleRate;
            const int bps = 16;
            int blockAlign = ch * (bps / 8);
            int byteRate = sr * blockAlign;

            // smpl chunk ONLY if the loop is enabled
            bool hasLoop = buffer.LoopEnabled && buffer.LoopStart >= 0 && buffer.LoopEnd > buffer.LoopStart;
            int smplSize = hasLoop ? 36 + 24 : 0;
            int dataSize = pcm.Length;
            int riffSize = 4 + (8 + 16) + (hasLoop ? 8 + smplSize : 0) + (8 + dataSize);

            using var bw = new BinaryWriter(output, Encoding.ASCII, true);
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(riffSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));

            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)ch);
            bw.Write(sr);
            bw.Write(byteRate);
            bw.Write((short)blockAlign);
            bw.Write((short)bps);

            if (hasLoop)
            {
                bw.Write(Encoding.ASCII.GetBytes("smpl"));
                bw.Write(smplSize);
                bw.Write(0);                    // manufacturer
                bw.Write(0);                    // product
                bw.Write(1000000000 / sr);      // sample period (ns)
                bw.Write(60);                   // MIDI unity note
                bw.Write(0);                    // pitch fraction
                bw.Write(0);                    // SMPTE format
                bw.Write(0);                    // SMPTE offset
                bw.Write(1);                    // loops
                bw.Write(0);                    // sampler data
                bw.Write(0);                    // cue id
                bw.Write(0);                    // forward loop
                bw.Write(buffer.LoopStart);
                bw.Write(buffer.LoopEnd - 1);   // WAV stores inclusive end
                bw.Write(0);                    // fraction
                bw.Write(0);                    // play count (infinite)
            }

            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);
            bw.Write(pcm);
        }

        // ------------------------------------------------------------------ MP3 / M4A export (Windows Media Foundation)

        public static void ExportCompressed(AudioBuffer buffer, string path, bool mp3, int kbps = 192)
        {
            AudioBuffer b = buffer;
            int[] okRates = mp3 ? new[] { 32000, 44100, 48000 } : new[] { 44100, 48000 };
            if (Array.IndexOf(okRates, b.SampleRate) < 0) b = b.Resample(44100);
            if (b.Channels > 2) b = b.ToChannels(2);

            MediaFoundationApi.Startup();
            byte[] pcm = b.ToPcm16();
            using var ms = new MemoryStream(pcm);
            var raw = new RawSourceWaveStream(ms, new WaveFormat(b.SampleRate, 16, b.Channels));
            if (mp3) MediaFoundationEncoder.EncodeToMp3(raw, path, kbps * 1000);
            else MediaFoundationEncoder.EncodeToAac(raw, path, kbps * 1000);
        }
    }
}
