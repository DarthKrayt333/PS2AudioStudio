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
        public int FlagOffset;          // where the loop flag was found (for diagnostics), 0 = none
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
            if (d.Version != 3 && d.Version != 4) return d;      // v5 etc.: no loop block

            int dataStart = ((a[2] << 8) | a[3]) + 4;
            int blockEnd = dataStart - 6;                         // "(c)CRI" sits at copyrightOffset - 2
            int blk = a[5], ch = a[7];
            long total = Be32(a, 0x0C);
            int spf = blk >= 3 ? (blk - 2) * 2 : 32;
            if (ch < 1) ch = 1;

            // v3: flag at 0x18.  v4 (CRI / HMAWL): flag at 0x24.  0x28 kept as a fallback for files written by older versions of this tool.
            int[] candidates = d.Version == 4 ? new[] { 0x24, 0x28, 0x18 } : new[] { 0x18, 0x24, 0x28 };

            int bestScore = -1;
            foreach (int flagAt in candidates)
            {
                int sAt = flagAt + 4;
                if (sAt + 16 > blockEnd || sAt + 16 > a.Length) continue;
                if (Be32(a, flagAt) == 0) continue;

                long ss = Be32(a, sAt), sb = Be32(a, sAt + 4), es = Be32(a, sAt + 8), eb = Be32(a, sAt + 12);
                if (es <= ss) continue;

                int score = 0;
                if (total <= 0 || es <= total + 64) score += 2;           // end inside the stream
                if (sb >= dataStart) score += 1;                          // start byte inside the data
                if (eb >= sb) score += 1;
                long expect = dataStart + (ss / spf) * blk * (long)ch;
                if (Math.Abs(sb - expect) <= 0x800) score += 2;           // byte offset agrees with the sample number

                if (score > bestScore)
                {
                    bestScore = score;
                    d.FlagOffset = flagAt;
                    d.StartSample = ss; d.StartByte = sb; d.EndSample = es; d.EndByte = eb;
                }
            }

            d.HasLoop = bestScore >= 3;
            if (!d.HasLoop)
            {
                d.FlagOffset = 0;
                d.StartSample = d.StartByte = d.EndSample = d.EndByte = 0;
            }
            return d;
        }

        public static bool HasLoop(byte[] a) { return Read(a).HasLoop; }

        private static long Be32(byte[] b, int o)
        {
            return ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];
        }
    }
}
