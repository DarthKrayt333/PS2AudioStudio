using System;
using System.IO;

namespace PS2AudioStudio.Audio
{
    /// <summary>
    /// CRI ADX (standard fixed-coefficient ADPCM, encoding type 3, 18-byte frames = 32 samples per channel).
    ///
    /// Header written by this encoder (version 3, same layout vgmstream / CRI tools use):
    ///   00  80 00                 magic
    ///   02  BE16 copyrightOffset  (dataStart - 4)
    ///   04  03                    encoding: standard ADX
    ///   05  12                    frame size (18)
    ///   06  04                    bits per sample
    ///   07  ch                    channels
    ///   08  BE32 sampleRate
    ///   0C  BE32 totalSamples     (per channel; padded to the loop end when looping)
    ///   10  BE16 highpass (500)
    ///   12  03 00                 version 3, flags 0
    ///   14  BE32 loop marker      (1 when looping, else 0)  - DKZ Studio needs this to show "repeat"
    ///   18  BE32 loopFlag         (1 = loop)           \
    ///   1C  BE32 loopStartSample                       |  only when loop is enabled
    ///   20  BE32 loopStartByteOffset (absolute)        |
    ///   24  BE32 loopEndSample                         |
    ///   28  BE32 loopEndByteOffset (absolute)         /
    ///   cpyOff-2  "(c)CRI"
    ///   cpyOff+4  audio frames, channels interleaved per frame, then a 0x8001 end frame
    ///             (the size field = number of padding bytes after it; file is padded to a multiple of 0x800).
    /// </summary>
    public class AdxCodec : IAudioCodec
    {
        public string FormatName => "ADX (CRI ADPCM)";
        public string FileExtension => ".adx";

        private static ushort BE16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
        private static int BE32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

        private static void Coeffs(int hpf, int sr, out int c1, out int c2)
        {
            double z = Math.Cos(2.0 * Math.PI * hpf / sr);
            double a = Math.Sqrt(2.0) - z;
            double b = Math.Sqrt(2.0) - 1.0;
            double c = (a - Math.Sqrt((a + b) * (a - b))) / b;
            c1 = (int)Math.Floor(c * 8192.0);
            c2 = (int)Math.Floor(c * c * -4096.0);
        }

        // ================================================================== DECODE
        public AudioBuffer Decode(byte[] d)
        {
            if (d == null || d.Length < 0x14 || d[0] != 0x80 || d[1] != 0x00)
                throw new InvalidDataException("Not an ADX file.");

            int cpy = BE16(d, 2);
            int dataOff = cpy + 4;
            int enc = d[4];
            int blk = d[5];
            int ch = d[7];
            int sr = BE32(d, 8);
            int total = BE32(d, 0x0C);
            int hpf = BE16(d, 0x10);
            int version = d[0x12];

            if (enc != 3 && enc != 4)
                throw new InvalidDataException($"Unsupported ADX encoding type {enc} (only 3 = standard and 4 = exponential are supported; AHX/fixed-coef are not).");
            if (blk < 3 || ch < 1 || ch > 8 || sr < 1000 || total < 0)
                throw new InvalidDataException("Corrupt ADX header.");
            if (dataOff > d.Length) throw new InvalidDataException("ADX data offset beyond end of file.");

            int loopFlag = 0, loopStart = 0, loopEnd = 0;
            if (version == 3 && dataOff >= 0x2C)
            {
                loopFlag = BE32(d, 0x18);
                loopStart = BE32(d, 0x1C);
                loopEnd = BE32(d, 0x24);
            }
            else if (version == 4 && dataOff >= 0x38)
            {
                loopFlag = BE32(d, 0x28);
                loopStart = BE32(d, 0x2C);
                loopEnd = BE32(d, 0x34);
            }

            Coeffs(hpf, sr, out int c1, out int c2);

            int spf = (blk - 2) * 2;                    // samples per frame (32)
            var outS = new float[total * ch];
            int[] h1 = new int[ch], h2 = new int[ch];
            int frames = (total + spf - 1) / spf;
            int pos = dataOff;

            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < ch; c++)
                {
                    if (pos + blk > d.Length) goto done;
                    int raw = BE16(d, pos);
                    if ((raw & 0x8000) != 0) goto done;       // end-of-stream frame

                    int scale = enc == 4 ? (1 << (12 - (raw & 0x1FFF))) : ((raw & 0x1FFF) + 1);
                    int count = Math.Min(spf, total - f * spf);
                    for (int i = 0; i < count; i++)
                    {
                        int by = d[pos + 2 + i / 2];
                        int n = (i & 1) == 0 ? (by >> 4) & 0xF : by & 0xF;
                        if (n >= 8) n -= 16;
                        int pred = (c1 * h1[c] + c2 * h2[c]) >> 12;
                        int s = n * scale + pred;
                        if (s > 32767) s = 32767; else if (s < -32768) s = -32768;
                        h2[c] = h1[c]; h1[c] = s;
                        outS[(f * spf + i) * ch + c] = s / 32768f;
                    }
                    pos += blk;
                }
            }
        done:
            var buf = new AudioBuffer(outS, sr, ch);
            if (loopFlag != 0 && loopEnd > loopStart && loopStart >= 0)
            {
                buf.LoopEnabled = true;
                buf.LoopStart = loopStart;
                buf.LoopEnd = Math.Min(loopEnd, buf.TotalFrames);
                buf.ClampLoopPoints();
            }
            return buf;
        }

        // ================================================================== ENCODE
        public byte[] Encode(AudioBuffer buffer)
        {
            if (buffer == null || buffer.Samples == null || buffer.Samples.Length == 0)
                throw new InvalidDataException("Nothing to encode.");

            int ch = Math.Min(buffer.Channels, 2);
            int sr = buffer.SampleRate;
            int total = buffer.TotalFrames;
            const int hpf = 500;
            const int blk = 18;
            const int spf = 32;

            Coeffs(hpf, sr, out int c1, out int c2);

            // ---- loop (only when the Loop checkbox / LoopEnabled is ON) ----
            // Both points sit on exact frame boundaries so sample numbers and byte offsets always agree.
            int paddedTotal = ((total + spf - 1) / spf) * spf;
            bool doLoop = buffer.LoopEnabled && buffer.LoopEnd > buffer.LoopStart && buffer.LoopStart >= 0;
            int ls = 0, le = 0;
            if (doLoop)
            {
                ls = (buffer.LoopStart / spf) * spf;
                le = ((buffer.LoopEnd + spf - 1) / spf) * spf;      // round UP to a whole frame
                if (le > paddedTotal) le = paddedTotal;
                if (le <= ls) doLoop = false;
            }

            int headerTotal = doLoop ? Math.Max(total, le) : total;
            int bytesPerFrameSet = blk * ch;

            int cpyOff = doLoop ? 0x38 : 0x20;
            int dataOff = cpyOff + 4;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // ---- header ----
            W16(bw, 0x8000);
            W16(bw, (ushort)cpyOff);
            bw.Write((byte)3);
            bw.Write((byte)blk);
            bw.Write((byte)4);
            bw.Write((byte)ch);
            W32(bw, sr);
            W32(bw, headerTotal);
            W16(bw, hpf);
            bw.Write((byte)3);           // version
            bw.Write((byte)0);           // flags
            W32(bw, doLoop ? 1 : 0);     // 0x14 loop marker (DKZ Studio shows "repeat" only when this is 1)

            if (doLoop)
            {
                W32(bw, 1);                                              // 0x18 loop flag
                W32(bw, ls);                                             // 0x1C loop start sample
                W32(bw, dataOff + (ls / spf) * bytesPerFrameSet);        // 0x20 loop start byte
                W32(bw, le);                                             // 0x24 loop end sample
                W32(bw, dataOff + (le / spf) * bytesPerFrameSet);        // 0x28 loop end byte
            }
            while (ms.Position < cpyOff - 2) bw.Write((byte)0);
            bw.Write(new byte[] { 0x28, 0x63, 0x29, 0x43, 0x52, 0x49 }); // "(c)CRI"

            // ---- sample fetch (mono -> duplicated, >2ch -> first two) ----
            int srcCh = buffer.Channels;
            float[] S = buffer.Samples;
            int Get(int frame, int c)
            {
                if (frame >= total) return 0;
                int sc = srcCh == 1 ? 0 : Math.Min(c, srcCh - 1);
                float v = S[frame * srcCh + sc];
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                return (int)Math.Round(v * 32767f);
            }

            // ---- frames ----
            int[] h1 = new int[ch], h2 = new int[ch];
            int[] orig = new int[spf];
            byte[] block = new byte[blk - 2];

            for (int frame = 0; frame < headerTotal; frame += spf)
            {
                for (int c = 0; c < ch; c++)
                {
                    for (int i = 0; i < spf; i++) orig[i] = Get(frame + i, c);

                    // choose scale from the largest prediction residual
                    int th1 = h1[c], th2 = h2[c], maxAbs = 0;
                    for (int i = 0; i < spf; i++)
                    {
                        int pred = (c1 * th1 + c2 * th2) >> 12;
                        int ar = Math.Abs(orig[i] - pred);
                        if (ar > maxAbs) maxAbs = ar;
                        th2 = th1; th1 = orig[i];
                    }
                    int scale = (maxAbs + 6) / 7;
                    if (scale < 1) scale = 1;
                    if (scale > 0x2000) scale = 0x2000;

                    // quantise with real (reconstructed) history
                    th1 = h1[c]; th2 = h2[c];
                    Array.Clear(block, 0, block.Length);
                    for (int i = 0; i < spf; i++)
                    {
                        int pred = (c1 * th1 + c2 * th2) >> 12;
                        int n = (int)Math.Round((double)(orig[i] - pred) / scale);
                        if (n > 7) n = 7; else if (n < -8) n = -8;
                        int rec = n * scale + pred;
                        if (rec > 32767) rec = 32767; else if (rec < -32768) rec = -32768;
                        th2 = th1; th1 = rec;
                        int nv = n & 0xF;
                        if ((i & 1) == 0) block[i / 2] = (byte)(nv << 4); else block[i / 2] |= (byte)nv;
                    }
                    h1[c] = th1; h2[c] = th2;

                    W16(bw, (ushort)(scale - 1));
                    bw.Write(block);
                }
            }

            // ---- end-of-stream frame: 0x8001, BE16 = number of padding bytes that follow, zero padding.
            // The file is padded to a multiple of 0x800 (same as CRI's own tools). ----
            int pos = (int)ms.Position;
            int target = ((pos + blk + 0x7FF) / 0x800) * 0x800;
            int pad = target - pos - 4;
            W16(bw, 0x8001);
            W16(bw, (ushort)pad);
            for (int i = 0; i < pad; i++) bw.Write((byte)0);

            byte[] raw = ms.ToArray();
            return AppSettings.Current.AdxLayoutMode == 1
                ? AdxLayout.ToSectorLayout(raw)
                : AdxLayoutCri.ToCriLayout(raw);
        }

        private static void W16(BinaryWriter bw, ushort v) { bw.Write((byte)(v >> 8)); bw.Write((byte)v); }
        private static void W32(BinaryWriter bw, int v) { bw.Write((byte)(v >> 24)); bw.Write((byte)(v >> 16)); bw.Write((byte)(v >> 8)); bw.Write((byte)v); }
    }
}
