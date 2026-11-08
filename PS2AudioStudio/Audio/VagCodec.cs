using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PS2AudioStudio.Audio
{
    /// <summary>
    /// Sony PS-ADPCM ("VAG"). 16-byte blocks = 1 byte shift/predict, 1 byte flag, 14 bytes = 28 samples.
    /// Flag bits: 1 = end of data, 2 = repeat (loop) , 4 = loop start.
    ///   non-looping : 0 0 0 ... 1
    ///   looping     : 2 2 6(start) 2 2 ... 3(end)
    ///   7           = "end + mute" marker block (00 07 77 77 ...) -> never decoded
    /// </summary>
    public class VagCodec : IAudioCodec
    {
        private static readonly int[,] F = new int[,]
        {
            {   0,   0 },
            {  60,   0 },
            { 115, -52 },
            {  98, -55 },
            { 122, -60 }
        };

        public const int BlockSize = 16;
        public const int SamplesPerBlock = 28;

        public string FormatName => "VAG";
        public string FileExtension => ".vag";

        public AudioBuffer Decode(byte[] data) => LoadFromBytes(data);
        public byte[] Encode(AudioBuffer buffer) => SaveToBytes(buffer);

        // ------------------------------------------------------------------ detection helpers

        public static bool HasHeader(byte[] f)
        {
            return f != null && f.Length >= 0x30 && f[0] == 'V' && f[1] == 'A' && f[2] == 'G';
        }

        public static bool IsInterleavedHeader(byte[] f)
        {
            return HasHeader(f) && f[3] == 'i';
        }

        private static int BE32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        private static int LE32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

        // ------------------------------------------------------------------ decode

        /// <summary>Decodes a standard "VAGp" file (mono). Throws if there is no header (use the raw importer instead).</summary>
        public AudioBuffer LoadFromBytes(byte[] file)
        {
            if (!HasHeader(file))
                throw new InvalidDataException("No VAG header found. Use File > Open Raw PS-ADPCM... for headerless / interleaved data.");

            if (file[3] == 'i')
                throw new InvalidDataException("This is an interleaved 'VAGi' file. Use File > Open Raw PS-ADPCM... (rate/interleave/offset pre-filled).");

            int rate = BE32(file, 0x10);
            if (rate < 1000 || rate > 192000) rate = 44100;

            int len = file.Length - 0x30;
            int declared = BE32(file, 0x0C);
            if (declared > 0 && declared <= len) len = declared;

            return DecodeAdpcm(file, 0x30, len, rate);
        }

        public static AudioBuffer DecodeAdpcm(byte[] data, int sampleRate) => DecodeAdpcm(data, 0, data.Length, sampleRate);

        /// <summary>
        /// Decode one mono PS-ADPCM stream. Reads loop info from the block flags:
        /// LoopStart = first block flagged 4, LoopEnd = end of block flagged 3.
        /// </summary>
        public static AudioBuffer DecodeAdpcm(byte[] data, int offset, int length, int sampleRate)
        {
            int hist1 = 0, hist2 = 0;
            int blocks = Math.Max(0, length / BlockSize);
            var samples = new float[blocks * SamplesPerBlock];
            int n = 0;
            int loopStart = -1, loopEnd = -1;

            for (int b = 0; b < blocks; b++)
            {
                int off = offset + b * BlockSize;
                if (off + BlockSize > data.Length) break;

                byte hdr = data[off];
                byte flag = data[off + 1];
                if (flag == 7) break;

                int predict = (hdr >> 4) & 0x0F;
                int shift = hdr & 0x0F;
                if (predict > 4) predict = 0;
                if (shift > 12) shift = 9;   // hardware treats >12 like 9

                if ((flag & 4) != 0 && loopStart < 0) loopStart = n;

                for (int i = 0; i < 14; i++)
                {
                    byte by = data[off + 2 + i];
                    for (int h = 0; h < 2; h++)
                    {
                        int nib = h == 0 ? (by & 0x0F) : ((by >> 4) & 0x0F);
                        if (nib >= 8) nib -= 16;
                        int raw = (nib << 12) >> shift;
                        int s = Clamp(raw + ((hist1 * F[predict, 0] + hist2 * F[predict, 1] + 32) >> 6));
                        hist2 = hist1; hist1 = s;
                        samples[n++] = s / 32768f;
                    }
                }

                if (flag == 3 || flag == 7) { loopEnd = n; }
                if ((flag & 1) != 0) break;
            }

            if (n != samples.Length) Array.Resize(ref samples, n);
            var buf = new AudioBuffer(samples, sampleRate, 1);

            if (loopEnd > 0)
            {
                buf.LoopEnabled = true;
                buf.LoopStart = loopStart >= 0 ? loopStart : 0;
                buf.LoopEnd = loopEnd;
                buf.ClampLoopPoints();
            }
            return buf;
        }

        /// <summary>
        /// Decode raw interleaved PS-ADPCM (no header): channel c owns every c-th chunk of 'interleave' bytes.
        /// </summary>
        public static AudioBuffer DecodeInterleaved(byte[] data, int offset, int channels, int interleave, int sampleRate)
        {
            if (channels < 1) channels = 1;
            if (channels == 1) return DecodeAdpcm(data, offset, data.Length - offset, sampleRate);
            if (interleave < BlockSize) interleave = BlockSize;
            interleave -= interleave % BlockSize;

            int total = data.Length - offset;
            var streams = new List<byte>[channels];
            for (int c = 0; c < channels; c++) streams[c] = new List<byte>(total / channels + 16);

            int pos = offset;
            int chunk = 0;
            while (pos < data.Length)
            {
                int c = chunk % channels;
                int take = Math.Min(interleave, data.Length - pos);
                for (int i = 0; i < take; i++) streams[c].Add(data[pos + i]);
                pos += interleave;
                chunk++;
            }

            var decoded = new AudioBuffer[channels];
            int min = int.MaxValue;
            for (int c = 0; c < channels; c++)
            {
                byte[] arr = streams[c].ToArray();
                decoded[c] = DecodeAdpcm(arr, 0, arr.Length, sampleRate);
                min = Math.Min(min, decoded[c].TotalFrames);
            }

            var res = new AudioBuffer(sampleRate, channels, min);
            for (int c = 0; c < channels; c++)
                for (int f = 0; f < min; f++)
                    res.Samples[f * channels + c] = decoded[c].Samples[f];

            if (decoded[0].LoopEnabled)
            {
                res.LoopEnabled = true;
                res.LoopStart = decoded[0].LoopStart;
                res.LoopEnd = Math.Min(decoded[0].LoopEnd, min);
                res.ClampLoopPoints();
            }
            return res;
        }

        /// <summary>Best guess for 'VAGi' headers: returns rate, interleave, offset to pre-fill the raw importer.</summary>
        public static void GuessVagiParams(byte[] f, out int rate, out int interleave, out int dataOffset, out int channels)
        {
            rate = BE32(f, 0x10);
            if (rate < 1000 || rate > 192000) rate = 44100;
            interleave = LE32(f, 0x08);
            if (interleave < 16 || interleave > 0x10000) interleave = 0x800;
            dataOffset = 0x800;
            if (f.Length <= 0x800) dataOffset = 0x30;
            channels = 2;
        }

        // ------------------------------------------------------------------ encode

        public byte[] SaveToBytes(AudioBuffer buffer) => SaveToBytes(buffer, "sample");

        public byte[] SaveToBytes(AudioBuffer buffer, string name)
        {
            float[] mono = buffer.MixToMono();
            bool loop = buffer.LoopEnabled && buffer.LoopEnd > buffer.LoopStart && buffer.LoopStart >= 0;
            byte[] adpcm = EncodeAdpcm(mono, loop, buffer.LoopStart, buffer.LoopEnd);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(new byte[] { (byte)'V', (byte)'A', (byte)'G', (byte)'p' });
            WriteBE32(bw, 0x20);                 // version
            WriteBE32(bw, 0);                    // reserved
            WriteBE32(bw, adpcm.Length);         // data size
            WriteBE32(bw, buffer.SampleRate);    // sample rate
            for (int i = 0; i < 12; i++) bw.Write((byte)0);
            byte[] nameBytes = new byte[16];
            var nm = Encoding.ASCII.GetBytes(name ?? "sample");
            Array.Copy(nm, nameBytes, Math.Min(nm.Length, 15));
            bw.Write(nameBytes);
            bw.Write(adpcm);
            return ms.ToArray();
        }

        /// <summary>Wrap already-encoded PS-ADPCM bytes in a VAGp header (lossless extraction from BD banks).</summary>
        public static byte[] WrapRaw(byte[] adpcm, int sampleRate, string name)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(new byte[] { (byte)'V', (byte)'A', (byte)'G', (byte)'p' });
            WriteBE32(bw, 0x20);
            WriteBE32(bw, 0);
            WriteBE32(bw, adpcm.Length);
            WriteBE32(bw, sampleRate);
            for (int i = 0; i < 12; i++) bw.Write((byte)0);
            byte[] nameBytes = new byte[16];
            var nm = Encoding.ASCII.GetBytes(name ?? "sample");
            Array.Copy(nm, nameBytes, Math.Min(nm.Length, 15));
            bw.Write(nameBytes);
            bw.Write(adpcm);
            return ms.ToArray();
        }

        /// <summary>Raw interleaved PS-ADPCM for N channels (no header). Loop flags are written on every channel.</summary>
        public static byte[] EncodeInterleaved(AudioBuffer buffer, int interleave)
        {
            int ch = buffer.Channels;
            interleave -= interleave % BlockSize;
            if (interleave < BlockSize) interleave = 0x10;
            bool loop = buffer.LoopEnabled && buffer.LoopEnd > buffer.LoopStart && buffer.LoopStart >= 0;

            var streams = new byte[ch][];
            int maxLen = 0;
            for (int c = 0; c < ch; c++)
            {
                streams[c] = EncodeAdpcm(buffer.GetChannel(c), loop, buffer.LoopStart, buffer.LoopEnd);
                maxLen = Math.Max(maxLen, streams[c].Length);
            }
            int chunks = (maxLen + interleave - 1) / interleave;

            using var ms = new MemoryStream();
            for (int k = 0; k < chunks; k++)
                for (int c = 0; c < ch; c++)
                {
                    byte[] chunk = new byte[interleave];
                    int src = k * interleave;
                    int n = Math.Max(0, Math.Min(interleave, streams[c].Length - src));
                    if (n > 0) Array.Copy(streams[c], src, chunk, 0, n);
                    ms.Write(chunk, 0, chunk.Length);
                }
            return ms.ToArray();
        }

        private static void WriteBE32(BinaryWriter bw, int v)
        {
            bw.Write((byte)((v >> 24) & 0xFF));
            bw.Write((byte)((v >> 16) & 0xFF));
            bw.Write((byte)((v >> 8) & 0xFF));
            bw.Write((byte)(v & 0xFF));
        }

        /// <summary>
        /// Encode mono float PCM into PS-ADPCM blocks.
        /// loop == false -> NO loop flags at all (last block = 1), regardless of start/end values.
        /// loop == true  -> start block flagged 6, body 2, end block 3. Loop points snap to 28 and audio after the loop end is dropped
        ///                  (the SPU never plays it).
        /// </summary>
        public static byte[] EncodeAdpcm(float[] pcm, bool loop, int loopStartSample, int loopEndSample)
        {
            int len = pcm.Length;
            if (len == 0) len = 1;

            int totalBlocks = (len + 27) / 28;
            int loopStartBlock = -1, loopEndBlock = -1;
            if (loop)
            {
                loopStartBlock = Math.Max(0, loopStartSample / 28);
                loopEndBlock = Math.Max(0, (loopEndSample - 1) / 28);
                if (loopEndBlock >= totalBlocks) loopEndBlock = totalBlocks - 1;
                if (loopStartBlock >= loopEndBlock)
                {
                    if (loopEndBlock + 1 < totalBlocks) loopEndBlock = loopStartBlock + 1;
                    else if (loopStartBlock > 0) loopStartBlock = loopEndBlock - 1;
                    else loop = false;
                }
                if (loop) totalBlocks = loopEndBlock + 1;
            }

            short[] s16 = new short[totalBlocks * 28];
            for (int i = 0; i < s16.Length && i < pcm.Length; i++)
            {
                float v = pcm[i];
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                s16[i] = (short)Math.Round(v * 32767f);
            }

            using var ms = new MemoryStream();
            int h1 = 0, h2 = 0;
            short[] seg = new short[28];

            for (int blk = 0; blk < totalBlocks; blk++)
            {
                Array.Copy(s16, blk * 28, seg, 0, 28);
                var (pred, shift, blockData, nh1, nh2) = EncodeBlock(seg, h1, h2);
                h1 = nh1; h2 = nh2;

                byte hdr = (byte)(((pred & 0x0F) << 4) | (shift & 0x0F));
                byte flag;
                if (loop)
                {
                    if (blk == loopEndBlock) flag = 3;
                    else if (blk == loopStartBlock) flag = 6;
                    else flag = 2;
                }
                else
                {
                    flag = (byte)(blk == totalBlocks - 1 ? 1 : 0);
                }

                ms.WriteByte(hdr);
                ms.WriteByte(flag);
                ms.Write(blockData, 0, 14);
            }
            return ms.ToArray();
        }

        private static (int predict, int shift, byte[] data, int newH1, int newH2) EncodeBlock(short[] seg, int h1In, int h2In)
        {
            int bestPredict = 0, bestShift = 12;
            byte[] bestData = new byte[14];
            long bestError = long.MaxValue;
            int bestH1 = h1In, bestH2 = h2In;

            for (int pred = 0; pred < 5; pred++)
            {
                int th1 = h1In, th2 = h2In;
                int maxAbs = 0;
                for (int i = 0; i < 28; i++)
                {
                    int predicted = (th1 * F[pred, 0] + th2 * F[pred, 1] + 32) >> 6;
                    int resid = seg[i] - predicted;
                    int ar = Math.Abs(resid);
                    if (ar > maxAbs) maxAbs = ar;
                    th2 = th1; th1 = seg[i];
                }

                int shiftTry = 12;
                while (shiftTry > 0 && (7 << (12 - shiftTry)) < maxAbs) shiftTry--;

                for (int sh = shiftTry; sh >= Math.Max(0, shiftTry - 1); sh--)
                {
                    byte[] data = new byte[14];
                    int dh1 = h1In, dh2 = h2In;
                    long err = 0;

                    for (int i = 0; i < 28; i += 2)
                    {
                        int nl = EncodeNibble(seg[i], dh1, dh2, pred, sh, out int sl, out long el);
                        dh2 = dh1; dh1 = sl;
                        int nh = EncodeNibble(seg[i + 1], dh1, dh2, pred, sh, out int shh, out long eh);
                        dh2 = dh1; dh1 = shh;
                        data[i / 2] = (byte)(((nh & 0x0F) << 4) | (nl & 0x0F));
                        err += el + eh;
                    }

                    if (err < bestError)
                    {
                        bestError = err; bestPredict = pred; bestShift = sh; bestData = data; bestH1 = dh1; bestH2 = dh2;
                    }
                }
            }
            return (bestPredict, bestShift, bestData, bestH1, bestH2);
        }

        private static int EncodeNibble(int sample, int h1, int h2, int predict, int shift, out int reconstructed, out long error)
        {
            int predicted = (h1 * F[predict, 0] + h2 * F[predict, 1] + 32) >> 6;
            int resid = sample - predicted;

            int sa = 12 - shift;
            int n = sa > 0 ? (resid + (1 << (sa - 1))) >> sa : resid;
            if (n > 7) n = 7;
            if (n < -8) n = -8;

            reconstructed = Clamp(((n << 12) >> shift) + predicted);
            long d = sample - reconstructed;
            error = d * d;
            return n & 0x0F;
        }

        private static int Clamp(int v) => v > 32767 ? 32767 : (v < -32768 ? -32768 : v);
    }
}
