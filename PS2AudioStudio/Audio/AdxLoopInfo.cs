using System;

namespace PS2AudioStudio.Audio
{
    /// <summary>Reads the loop block of a CRI ADX header (version 3 and 4).</summary>
    public struct AdxLoopData
    {
        public bool IsAdx;
        public int Version;
        public bool HasLoop;
        public long StartSample, StartByte, EndSample, EndByte;
    }

    public static class AdxLoopInfo
    {
        /// <summary>Pass the ADX file (or at least its first 0x40 bytes).</summary>
        public static AdxLoopData Read(byte[] a)
        {
            AdxLoopData d = new AdxLoopData();
            if (a == null || a.Length < 0x20) return d;
            if (a[0] != 0x80 || a[1] != 0x00) return d;
            d.IsAdx = true;
            d.Version = a[0x12];

            int dataStart = ((a[2] << 8) | a[3]) + 4;
            int flagAt, sAt;
            if (d.Version == 3) { flagAt = 0x18; sAt = 0x1C; if (dataStart - 6 < 0x2C) return d; }
            else if (d.Version == 4) { flagAt = 0x28; sAt = 0x2C; if (dataStart - 6 < 0x38) return d; }
            else return d;                                   // v5 etc.: no loop block

            if (a.Length < sAt + 12) return d;
            if (Be32(a, flagAt) == 0) return d;

            d.StartSample = Be32(a, sAt);
            d.StartByte = Be32(a, sAt + 4);
            d.EndSample = Be32(a, sAt + 8);
            d.EndByte = a.Length >= sAt + 16 ? Be32(a, sAt + 12) : 0;
            d.HasLoop = d.EndSample > d.StartSample;
            return d;
        }

        public static bool HasLoop(byte[] a) { return Read(a).HasLoop; }

        private static long Be32(byte[] b, int o)
        {
            return ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];
        }
    }
}
