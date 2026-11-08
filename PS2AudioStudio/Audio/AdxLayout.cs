using System;

namespace PS2AudioStudio.Audio
{
    /// <summary>
    /// Re-lays a looping ADX (version 3) so the LOOP START BYTE sits on a 0x800 sector boundary
    /// (the game streams from disc and seeks by byte offset). The header is made as large as needed
    /// for that; the audio frames are untouched. Non-looping files are returned unchanged.
    /// </summary>
    public static class AdxLayout
    {
        public const int Sector = 0x800;
        private const int MinDataStart = 0x3C;      // 0x2C header + "(c)CRI" + 4

        public static byte[] ToSectorLayout(byte[] adx)
        {
            try { return Build(adx); }
            catch { return adx; }
        }

        private static byte[] Build(byte[] a)
        {
            if (a == null || a.Length < 0x30) return a;
            if (a[0] != 0x80 || a[1] != 0x00) return a;
            if (a[0x12] != 3) return a;                       // only version 3 carries the loop block we write
            if (Be32(a, 0x18) == 0) return a;                 // not looping -> leave as is

            int oldData = ((a[2] << 8) | a[3]) + 4;
            if (oldData < 0x2C || oldData > a.Length) return a;

            int block = a[5];
            int ch = a[7];
            if (block <= 0 || ch <= 0) return a;
            int fs = block * ch;                              // bytes per frame, all channels

            long total = Be32(a, 0x0C);
            long frames = (total + 31) / 32;
            long dataLen = frames * fs;
            if (oldData + dataLen > a.Length) dataLen = a.Length - oldData;

            long lsSample = Be32(a, 0x1C);
            long leSample = Be32(a, 0x24);
            long lsFrame = lsSample / 32;
            long leFrame = (leSample + 31) / 32;

            // data start so that (dataStart + lsFrame*fs) is a multiple of 0x800
            long r = (lsFrame * fs) % Sector;
            long newData = (Sector - r) % Sector;
            if (newData < MinDataStart) newData += Sector;

            long loopStartByte = newData + lsFrame * fs;      // multiple of 0x800
            long loopEndByte = newData + leFrame * fs;

            long pos = newData + dataLen;
            long target = ((pos + fs + 0x7FF) / 0x800) * 0x800;
            long pad = target - pos - 4;

            byte[] o = new byte[target];
            Array.Copy(a, 0, o, 0, 0x2C);                     // all header fields

            long cpyOff = newData - 4;
            o[2] = (byte)(cpyOff >> 8); o[3] = (byte)cpyOff;
            Put32(o, 0x1C, lsSample);
            Put32(o, 0x20, loopStartByte);
            Put32(o, 0x24, leSample);
            Put32(o, 0x28, loopEndByte);

            long c = newData - 6;                             // "(c)CRI" ends right before the data
            o[c] = (byte)'('; o[c + 1] = (byte)'c'; o[c + 2] = (byte)')';
            o[c + 3] = (byte)'C'; o[c + 4] = (byte)'R'; o[c + 5] = (byte)'I';

            Array.Copy(a, oldData, o, newData, dataLen);

            o[pos] = 0x80; o[pos + 1] = 0x01;
            o[pos + 2] = (byte)(pad >> 8); o[pos + 3] = (byte)pad;
            return o;
        }

        private static long Be32(byte[] b, int o)
        {
            return ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];
        }

        private static void Put32(byte[] b, int o, long v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
    }
}