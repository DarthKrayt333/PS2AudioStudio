using System;

namespace PS2AudioStudio.Archives
{
    /// <summary>
    /// Songs without drums have a drum-table offset of 0. The reader rejects them.
    /// This makes a patched copy that points the drum table at 768 empty bytes.
    /// </summary>
    public static class AkaoFix
    {
        public static byte[] PatchNoDrum(byte[] x)
        {
            if (x == null || x.Length < 0x60) return x;
            byte[] y = null;
            int origLen = x.Length;
            for (int p = 0; p + 0x44 <= x.Length; p++)
            {
                if (x[p] != 0x41 || x[p + 1] != 0x4B || x[p + 2] != 0x41 || x[p + 3] != 0x4F) continue;
                uint bankLen = U32(x, p + 0x14);
                uint bankCnt = U32(x, p + 0x1C);
                if (bankCnt >= 1 && bankCnt <= 512 && bankLen >= 32) continue;   // sample bank
                if ((x[p + 0x20] | (x[p + 0x21] << 8)) == 0) continue;           // end marker
                uint progOff = U32(x, p + 0x30);
                uint drumOff = U32(x, p + 0x34);
                if (drumOff != 0 || progOff == 0) continue;
                long pt = (long)p + 0x30 + progOff;
                if (pt <= p || pt >= origLen) continue;
                if (y == null)
                {
                    y = new byte[origLen + 0x400];
                    Buffer.BlockCopy(x, 0, y, 0, origLen);
                }
                uint off = (uint)(origLen - (p + 0x34));
                y[p + 0x34] = (byte)off;
                y[p + 0x35] = (byte)(off >> 8);
                y[p + 0x36] = (byte)(off >> 16);
                y[p + 0x37] = (byte)(off >> 24);
            }
            return y ?? x;
        }

        private static uint U32(byte[] b, int i)
        {
            return (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
        }
    }
}
