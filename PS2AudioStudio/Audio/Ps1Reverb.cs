using System;
namespace PS2AudioStudio.Audio
{
    /// <summary>The PS1 SPU reverb unit (same algorithm as PsxDriver, standalone): 22050 Hz reverb with the 39-tap
    /// down/up filter, own 512 KB work area. Input and output are float, +-1.0 = 16-bit full scale.</summary>
    public sealed class Ps1Reverb
    {
        private static readonly int[] Fir =
        {
            -1, 0, 2, 0, -10, 0, 35, 0, -103, 0, 266, 0, -616, 0, 1332, 0, -2960, 0, 10246, 16384,
            10246, 0, -2960, 0, 1332, 0, -616, 0, 266, 0, -103, 0, 35, 0, -10, 0, 2, 0, -1
        };
        /// <summary>The 32 registers 1DC0..1DFE that 120.psf writes (PS1 "Studio Large").</summary>
        public static readonly int[] StudioLarge =
        {
            0x00E3, 0x00A9, 0x6F60, 0x4FA8, 0xBCE0, 0x4510, 0xBEF0, 0xA680, 0x5680, 0x52C0, 0x0DFB, 0x0B58, 0x0D09, 0x0A3C, 0x0BD9, 0x0973,
            0x0B59, 0x08DA, 0x08D9, 0x05E9, 0x07EC, 0x04B0, 0x06EF, 0x03D2, 0x05EA, 0x031D, 0x031C, 0x0238, 0x0154, 0x00AA, 0x8000, 0x8000
        };
        private readonly short[] ram = new short[0x40000];
        private readonly int[] reg = new int[32];
        private readonly int vLOUT, vROUT;
        private readonly double[] rIL = new double[39], rIR = new double[39], rOL = new double[39], rOR = new double[39];
        private int rPos, rPhase, rStart, rCur;

        /// <summary>Studio Large, work area at mBase 0xF204 (byte 0x79020), return volume 0x3700.</summary>
        public Ps1Reverb() : this(StudioLarge, 0xF204, 0x3700, 0x3700) { }

        public Ps1Reverb(int[] regs, int mBase, int returnL, int returnR)
        {
            if (regs == null || regs.Length < 32) throw new ArgumentException("32 reverb registers needed.");
            for (int i = 0; i < 32; i++) reg[i] = regs[i] & 0xFFFF;
            rStart = (mBase & 0xFFFF) * 4;
            if (rStart >= 0x40000) throw new ArgumentException("Reverb work area base is outside SPU RAM.");
            rCur = rStart;
            vLOUT = (short)returnL;
            vROUT = (short)returnR;
        }

        private static int Cl(int x) { return x > 32767 ? 32767 : (x < -32768 ? -32768 : x); }
        private int RG(int i) { return reg[i]; }
        private int RS(int i) { return (short)reg[i]; }
        private int RvA(int off)
        {
            int a = rCur + off;
            if (a > 0x3FFFF) a = rStart + (a - 0x40000);
            else if (a < rStart) a += 0x40000 - rStart;
            return a;
        }
        private int RvR(int rg, int extra) { return ram[RvA(rg * 4 + extra)]; }
        private void RvW(int rg, int extra, int v) { ram[RvA(rg * 4 + extra)] = (short)Cl(v); }

        private void Rev22(int inL, int inR, out int outL, out int outR)
        {
            int dAPF1 = RG(0), dAPF2 = RG(1);
            int vIIR = RS(2), vC1 = RS(3), vC2 = RS(4), vC3 = RS(5), vC4 = RS(6), vWALL = RS(7), vA1 = RS(8), vA2 = RS(9);
            int mLSAME = RG(10), mRSAME = RG(11), mLC1 = RG(12), mRC1 = RG(13), mLC2 = RG(14), mRC2 = RG(15);
            int dLSAME = RG(16), dRSAME = RG(17), mLDIFF = RG(18), mRDIFF = RG(19), mLC3 = RG(20), mRC3 = RG(21);
            int mLC4 = RG(22), mRC4 = RG(23), dLDIFF = RG(24), dRDIFF = RG(25), mLA1 = RG(26), mRA1 = RG(27), mLA2 = RG(28), mRA2 = RG(29);
            int vLIN = RS(30), vRIN = RS(31);
            int lin = Cl((vLIN * inL) >> 15), rin = Cl((vRIN * inR) >> 15);
            int p, t;
            p = RvR(mLSAME, -1); t = Cl(lin + ((RvR(dLSAME, 0) * vWALL) >> 15) - p); RvW(mLSAME, 0, ((t * vIIR) >> 15) + p);
            p = RvR(mRSAME, -1); t = Cl(rin + ((RvR(dRSAME, 0) * vWALL) >> 15) - p); RvW(mRSAME, 0, ((t * vIIR) >> 15) + p);
            p = RvR(mLDIFF, -1); t = Cl(lin + ((RvR(dRDIFF, 0) * vWALL) >> 15) - p); RvW(mLDIFF, 0, ((t * vIIR) >> 15) + p);
            p = RvR(mRDIFF, -1); t = Cl(rin + ((RvR(dLDIFF, 0) * vWALL) >> 15) - p); RvW(mRDIFF, 0, ((t * vIIR) >> 15) + p);
            int lo = Cl(((vC1 * RvR(mLC1, 0)) >> 15) + ((vC2 * RvR(mLC2, 0)) >> 15) + ((vC3 * RvR(mLC3, 0)) >> 15) + ((vC4 * RvR(mLC4, 0)) >> 15));
            int ro = Cl(((vC1 * RvR(mRC1, 0)) >> 15) + ((vC2 * RvR(mRC2, 0)) >> 15) + ((vC3 * RvR(mRC3, 0)) >> 15) + ((vC4 * RvR(mRC4, 0)) >> 15));
            int q = RvR(mLA1 - dAPF1, 0);
            lo = Cl(lo - ((vA1 * q) >> 15)); RvW(mLA1, 0, lo); lo = Cl(((lo * vA1) >> 15) + q);
            q = RvR(mRA1 - dAPF1, 0);
            ro = Cl(ro - ((vA1 * q) >> 15)); RvW(mRA1, 0, ro); ro = Cl(((ro * vA1) >> 15) + q);
            q = RvR(mLA2 - dAPF2, 0);
            lo = Cl(lo - ((vA2 * q) >> 15)); RvW(mLA2, 0, lo); lo = Cl(((lo * vA2) >> 15) + q);
            q = RvR(mRA2 - dAPF2, 0);
            ro = Cl(ro - ((vA2 * q) >> 15)); RvW(mRA2, 0, ro); ro = Cl(((ro * vA2) >> 15) + q);
            outL = Cl((lo * vLOUT) >> 15);
            outR = Cl((ro * vROUT) >> 15);
            rCur++;
            if (rCur > 0x3FFFF) rCur = rStart;
        }

        /// <summary>One 44100 Hz sample in, one reverb sample out (16-bit scale).</summary>
        public void Step(double inL, double inR, out double oL, out double oR)
        {
            rPos++; if (rPos == 39) rPos = 0;
            rIL[rPos] = inL; rIR[rPos] = inR;
            if (rPhase == 0)
            {
                double sl = 0, sr = 0;
                for (int i = 0; i < 39; i++)
                {
                    int c = Fir[i]; if (c == 0) continue;
                    int k = rPos - i; if (k < 0) k += 39;
                    sl += c * rIL[k]; sr += c * rIR[k];
                }
                int ol, orr;
                Rev22(Cl((int)(sl / 32768.0)), Cl((int)(sr / 32768.0)), out ol, out orr);
                rOL[rPos] = ol; rOR[rPos] = orr;
            }
            else { rOL[rPos] = 0; rOR[rPos] = 0; }
            rPhase ^= 1;
            double al = 0, ar = 0;
            for (int i = 0; i < 39; i++)
            {
                int c = Fir[i]; if (c == 0) continue;
                int k = rPos - i; if (k < 0) k += 39;
                al += c * rOL[k]; ar += c * rOR[k];
            }
            oL = al / 16384.0; oR = ar / 16384.0;
        }

        /// <summary>Replaces the wet bus l/r (count samples at 'rate' Hz, +-1.0 scale) by the reverb output of the same length and rate.
        /// The tail after the last sample is not produced.</summary>
        public void Process(float[] l, float[] r, int count, int rate)
        {
            if (count <= 0 || rate <= 0) return;
            int n44 = (int)((long)count * 44100 / rate) + 1;
            var oL = new float[n44];
            var oR = new float[n44];
            double ratio = rate / 44100.0;
            for (int k = 0; k < n44; k++)
            {
                double pos = k * ratio;
                int i = (int)pos; if (i >= count) i = count - 1;
                int j = i + 1 < count ? i + 1 : i;
                double f = pos - i; if (f < 0) f = 0; if (f > 1) f = 1;
                double a = (l[i] * (1 - f) + l[j] * f) * 32768.0;
                double b = (r[i] * (1 - f) + r[j] * f) * 32768.0;
                double wl, wr;
                Step(a, b, out wl, out wr);
                oL[k] = (float)(wl / 32768.0);
                oR[k] = (float)(wr / 32768.0);
            }
            double inv = 44100.0 / rate;
            for (int x = 0; x < count; x++)
            {
                double pos = x * inv;
                int i = (int)pos; if (i >= n44) i = n44 - 1;
                int j = i + 1 < n44 ? i + 1 : i;
                double f = pos - i; if (f < 0) f = 0; if (f > 1) f = 1;
                l[x] = (float)(oL[i] * (1 - f) + oL[j] * f);
                r[x] = (float)(oR[i] * (1 - f) + oR[j] * f);
            }
        }
    }
}
