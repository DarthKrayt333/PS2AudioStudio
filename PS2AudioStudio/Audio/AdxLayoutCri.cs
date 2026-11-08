using System;

namespace PS2AudioStudio.Audio
{
    /// <summary>
    /// Alternative loop layout: standard 0x800 header (copyright offset 0x7FC) and N silent frames
    /// inserted at the start so the loop start BYTE offset is a multiple of 0x800.
    /// </summary>
    public static class AdxLayoutCri
    {
        public const int Sector = 0x800;

        public static byte[] ToCriLayout(byte[] adx)
        {
            try { return Build(adx); }
            catch { return adx; }
        }

        private static byte[] Build(byte[] a)
        {
            if (a == null || a.Length < 0x30) return a;
            if (a[0] != 0x80 || a[1] != 0x00) return a;
            if (a[0x12] != 3) return a;
            if (Be32(a, 0x18) == 0) return a;                    // no loop -> unchanged

            int oldData = ((a[2] << 8) | a[3]) + 4;
            if (oldData < 0x2C || oldData > a.Length) return a;

            int block = a[5];
            int ch = a[7];
            if (block <= 0 || ch <= 0) return a;
            int fs = block * ch;                                  // bytes per frame set (18 mono, 36 stereo)

            long total = Be32(a, 0x0C);
            long frames = (total + 31) / 32;
            long dataLen = frames * fs;
            if (oldData + dataLen > a.Length) dataLen = a.Length - oldData;

            long lsSample = Be32(a, 0x1C);
            long leSample = Be32(a, 0x24);
            long lsFrame = lsSample / 32;
            long leFrame = (leSample + 31) / 32;

            // silent frames to insert so (n + lsFrame) * fs is a multiple of 0x800
            long period = Sector / Gcd(fs, Sector);
            long n = (period - (lsFrame % period)) % period;
            long shift = n * 32;

            long dataStart = Sector;                              // standard 0x800 header
            long loopStartByte = dataStart + (n + lsFrame) * fs;
            long loopEndByte = dataStart + (n + leFrame) * fs;

            long pos = dataStart + (n + frames) * fs;
            if (pos - dataStart - n * fs != dataLen) pos = dataStart + n * fs + dataLen;
            long target = ((pos + fs + 0x7FF) / 0x800) * 0x800;
            long pad = target - pos - 4;

            byte[] o = new byte[target];
            Array.Copy(a, 0, o, 0, 0x2C);

            o[2] = 0x07; o[3] = 0xFC;
            Put32(o, 0x0C, total + shift);
            Put32(o, 0x1C, lsSample + shift);
            Put32(o, 0x20, loopStartByte);
            Put32(o, 0x24, leSample + shift);
            Put32(o, 0x28, loopEndByte);

            o[0x7FA] = (byte)'('; o[0x7FB] = (byte)'c'; o[0x7FC] = (byte)')';
            o[0x7FD] = (byte)'C'; o[0x7FE] = (byte)'R'; o[0x7FF] = (byte)'I';

            // silent frames are all-zero bytes (scale 1, nibbles 0 = digital silence)
            Array.Copy(a, oldData, o, dataStart + n * fs, dataLen);

            o[pos] = 0x80; o[pos + 1] = 0x01;
            o[pos + 2] = (byte)(pad >> 8); o[pos + 3] = (byte)pad;
            return o;
        }

        private static long Gcd(long x, long y) { while (y != 0) { long t = x % y; x = y; y = t; } return x; }
        private static long Be32(byte[] b, int o) { return ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3]; }
        private static void Put32(byte[] b, int o, long v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    }
}
