using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace PS2AudioStudio.Audio
{
    public sealed class PsxUnsupportedException : Exception
    {
        public PsxUnsupportedException(string message) : base(message) { }
    }

    public sealed class PsxLogEvent
    {
        public int Frame;        // output sample (44100 Hz) at which it happened
        public int Kind;         // 1 key-on, 2 key-off, 3 pitch change, 4 volume change, 5 voice ended by itself
        public int Voice, Addr, Pitch, Vl, Vr, A1, A2, Rep;
        public bool Rev;         // the voice's REVON bit
    }

    public sealed class PsxCapture
    {
        public readonly List<PsxLogEvent> Events = new List<PsxLogEvent>();
        public readonly Dictionary<int, byte[]> Samples = new Dictionary<int, byte[]>();   // SPU byte address -> ADPCM blocks up to the end flag (null = none)
        public int[] ReverbRegs = new int[32];
        public int ReturnL, ReturnR, WorkAreaByte, Frames;
        public bool ReverbEnabled;
    }

    public sealed class PsxResult
    {
        public short[] Pcm;          // interleaved L/R, 16-bit
        public int Frames;
        public int Rate = 44100;
        public double TickHz;
        public int Ticks;
        public int KeyOns, KeyOffs, Steals, MaxVoices, Peak;
        public string Report;
        public PsxCapture Capture;   // only when requested
    }

    /// <summary>Runs the PS-X EXE of a PSF1 file (R3000 interpreter), delivers the game's timer tick to its sound driver
    /// and renders the SPU output of that driver (dry, without the SPU reverb).</summary>
    public sealed class PsxDriver
    {
        private const uint Magic = 0xBFC00200;

        private static readonly int[] F0 = { 0, 60, 115, 98, 122 };
        private static readonly int[] F1 = { 0, 0, -52, -55, -60 };

        private sealed class Voice
        {
            public int I, Vl, Vr, Pitch, Start, A1, A2, Rep;
            public bool On;
            public int Addr, Idx, Cnt, Flags, S1, S2, Cur, Prev;
            public int[] Smp = new int[28];
            public int Ph, Level, Ec, Cyc = 1, Stp, Exp, Dec;
            public double Fl, Fr;
            public Voice(int i) { I = i; }
        }

        private sealed class Ev
        {
            public uint Handle, Cls, Spec, Mode, Handler;
            public bool Enabled;
        }

        // ---- machine state
        private readonly byte[] ram = new byte[0x200008];
        private readonly byte[] scr = new byte[0x410];
        private readonly byte[] spu = new byte[0x200];
        private readonly byte[] spuram = new byte[0x80000];
        private readonly uint[] r = new uint[32];
        private readonly uint[] c0 = new uint[32];
        private uint hi, lo, pc, npc;
        private long n;
        private uint gtog, istat, imask;
        private uint heap = 0x801C0000, evh = 0xF1000000;
        private readonly Dictionary<uint, uint> iov = new Dictionary<uint, uint>();
        private readonly List<Ev> events = new List<Ev>();

        // ---- SPU state
        private readonly Voice[] voices = new Voice[24];
        private readonly Voice[] act = new Voice[24];
        private int endx;
        private int maxOn, steals, konN, koffN, peak;
        private short[] pcm = new short[1 << 20];
        private int pcmPos;
    
        private PsxCapture cap;

        private bool irqMode, inIrq, crit, pendDma;
        private uint hookAddr;

        private static uint U32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }

        private PsxDriver(byte[] exe)
        {
            for (int i = 0; i < 24; i++) voices[i] = new Voice(i);
            pc = U32(exe, 0x10); npc = pc + 4;
            r[28] = U32(exe, 0x14);
            uint dest = U32(exe, 0x18), size = U32(exe, 0x1C), sp = U32(exe, 0x30);
            sp = sp == 0 ? 0x801FFFF0u : sp + U32(exe, 0x34);
            r[29] = sp; r[30] = sp;
            int b = (int)(dest & 0x1FFFFF);
            int len = (int)Math.Min((long)size, (long)Math.Max(0, exe.Length - 0x800));
            len = Math.Min(len, 0x200000 - b);
            if (len > 0) Buffer.BlockCopy(exe, 0x800, ram, b, len);
        }

        // ================= memory =================
        private static void WriteBytes(byte[] a, int q, uint v, int size)
        {
            for (int i = 0; i < size; i++) a[q + i] = (byte)(v >> (8 * i));
        }

        private uint Iov(uint p) { uint t; return iov.TryGetValue(p, out t) ? t : 0; }

        private uint Load(uint a, int size)
        {
            uint p = a & 0x1FFFFFFF;
            if (p < 0x800000)
            {
                int q = (int)(p & 0x1FFFFF);
                if (size == 4) return BitConverter.ToUInt32(ram, q);
                if (size == 2) return BitConverter.ToUInt16(ram, q);
                return ram[q];
            }
            if (p >= 0x1F800000 && p < 0x1F800400)
            {
                int q = (int)(p - 0x1F800000);
                if (size == 4) return BitConverter.ToUInt32(scr, q);
                if (size == 2) return BitConverter.ToUInt16(scr, q);
                return scr[q];
            }
            if (p >= 0x1F801000 && p < 0x1F803000) return IoR(p, size);
            return 0;
        }

        private void Store(uint a, uint v, int size)
        {
            uint p = a & 0x1FFFFFFF;
            if (size == 1) v &= 0xFF; else if (size == 2) v &= 0xFFFF;
            if (p < 0x800000)
            {
                if ((c0[12] & 0x10000) != 0) return;
                WriteBytes(ram, (int)(p & 0x1FFFFF), v, size);
                return;
            }
            if (p >= 0x1F800000 && p < 0x1F800400) { WriteBytes(scr, (int)(p - 0x1F800000), v, size); return; }
            if (p >= 0x1F801000 && p < 0x1F803000) IoW(p, v, size);
        }

        private uint IoR(uint p, int size)
        {
            uint mask = size == 4 ? 0xFFFFFFFFu : (1u << (8 * size)) - 1;
            if (p >= 0x1F801C00 && p < 0x1F801D80 && ((p - 0x1F801C00) & 15) == 12)
                return (uint)voices[(int)((p - 0x1F801C00) >> 4)].Level & mask;
            if (p == 0x1F801D9C) return (uint)endx & mask;
            if (p == 0x1F801D9E) return (uint)(endx >> 16) & mask;
            uint v;
            if (p >= 0x1F801C00 && p < 0x1F801E00)
            {
                int q = (int)(p - 0x1F801C00);
                if (q == 0x1AE) v = (uint)(spu[0x1AA] | (spu[0x1AB] << 8)) & 0x3F;
                else
                {
                    v = 0;
                    for (int i = 0; i < size && q + i < spu.Length; i++) v |= (uint)spu[q + i] << (8 * i);
                }
            }
            else if (p == 0x1F801070) v = istat;
            else if (p == 0x1F801074) v = imask;
            else if (p >= 0x1F801100 && p < 0x1F801130 && (p & 0xF) == 0) v = (uint)((n >> 3) & 0xFFFF);
            else if (p == 0x1F801814) { gtog ^= 0x80000000u; v = 0x1C000000u | gtog; }
            else v = Iov(p);
            return v & mask;
        }

        private void IoW(uint p, uint v, int size)
        {
            if (p >= 0x1F801C00 && p < 0x1F801E00)
            {
                int q = (int)(p - 0x1F801C00);
                WriteBytes(spu, q, v, size);
                if (q == 0x1A8)
                {
                    int ta = (spu[0x1A6] | (spu[0x1A7] << 8)) * 8;
                    if (ta + size <= 0x80000) WriteBytes(spuram, ta, v, size);
                    int na = ((ta + size) >> 3) & 0xFFFF;
                    spu[0x1A6] = (byte)na; spu[0x1A7] = (byte)(na >> 8);
                }
                for (int qq = q & ~1; qq < q + size; qq += 2)
                    if (qq + 1 < spu.Length) SpuReg(qq);
                return;
            }
            if (p == 0x1F801070) { istat &= v; return; }
            if (p == 0x1F801074) { imask = v; return; }

            if (irqMode && p == 0x1F8010F4)
            {
                uint old = Iov(p);
                uint flags = (old & 0x7F000000u) & ~(v & 0x7F000000u);
                uint nv = (v & 0x00FF803Fu) | flags;
                if ((nv & 0x8000u) != 0 || ((nv & 0x800000u) != 0 && (((nv >> 24) & (nv >> 16)) & 0x7Fu) != 0)) nv |= 0x80000000u;
                iov[p] = nv;
                return;
            }

            if (p >= 0x1F801080 && p < 0x1F8010F0 && (p & 0xF) == 8 && size == 4)
            {
                int ch = (int)((p - 0x1F801080) >> 4);
                uint bas = 0x1F801080 + (uint)ch * 16;
                if (ch == 4 && (v & (1u << 24)) != 0)
                {
                    int madr = (int)(Iov(bas) & 0x1FFFFF);
                    uint bcr = Iov(bas + 4);
                    long words = (long)(bcr & 0xFFFF) * Math.Max(bcr >> 16, 1u);
                    int nb = (int)(words * 4);
                    int ta = (spu[0x1A6] | (spu[0x1A7] << 8)) * 8;
                    int len = nb;
                    if (ta + len > 0x80000) len = 0x80000 - ta;
                    if (madr + len > ram.Length) len = ram.Length - madr;
                    if (len > 0)
                    {
                        if ((v & 1) != 0) Buffer.BlockCopy(ram, madr, spuram, ta, len);
                        else Buffer.BlockCopy(spuram, ta, ram, madr, len);
                    }

                    if (irqMode) pendDma = true;

                    int na = ((ta + nb) >> 3) & 0xFFFF;
                    spu[0x1A6] = (byte)na; spu[0x1A7] = (byte)(na >> 8);
                }
                iov[p] = v & ~((1u << 24) | (1u << 28));
                return;
            }
            iov[p] = v;
        }

        // ================= BIOS stubs =================
        private Ev FindEv(uint h)
        {
            for (int i = 0; i < events.Count; i++) if (events[i].Handle == h) return events[i];
            return null;
        }

        private void Bios(char tbl)
        {
            int fn = (int)(r[9] & 0xFF);
            uint a0 = r[4], a1 = r[5], a2 = r[6], a3 = r[7];

            r[2] = 0;
            if (tbl == 'A')
            {
                if (fn == 0x2A || fn == 0x2C)
                {
                    if (a2 != 0 && a2 < 0x200000)
                    {
                        int d = (int)(a0 & 0x1FFFFF), sr = (int)(a1 & 0x1FFFFF);
                        int len = (int)Math.Min(a2, (uint)(0x200000 - Math.Max(d, sr)));
                        if (len > 0)
                        {
                            byte[] tmp = new byte[len];
                            Buffer.BlockCopy(ram, sr, tmp, 0, len);
                            Buffer.BlockCopy(tmp, 0, ram, d, len);
                        }
                    }
                    r[2] = a0;
                }
                else if (fn == 0x2B)
                {
                    int d = (int)(a0 & 0x1FFFFF);
                    if (a2 > 0 && a2 < 0x200000)
                    {
                        int len = (int)Math.Min(a2, (uint)(0x200000 - d));
                        for (int i = 0; i < len; i++) ram[d + i] = (byte)(a1 & 0xFF);
                    }
                    r[2] = a0;
                }
                else if (fn == 0x28)
                {
                    int d = (int)(a0 & 0x1FFFFF);
                    if (a1 > 0 && a1 < 0x200000)
                    {
                        int len = (int)Math.Min(a1, (uint)(0x200000 - d));
                        for (int i = 0; i < len; i++) ram[d + i] = 0;
                    }
                }
                else if (fn == 0x1B)
                {
                    int p = (int)(a0 & 0x1FFFFF), q = p;
                    while (q < 0x200000 && ram[q] != 0) q++;
                    r[2] = (uint)(q - p);
                }
                else if (fn == 0x39) heap = a0 < 0x80000000u ? (a0 | 0x80000000u) : a0;
                else if (fn == 0x33)
                {
                    r[2] = heap;
                    heap += (a0 + 11) & ~7u;
                }
            }
            else if (tbl == 'B')
            {
                if (fn == 0x08)
                {
                    evh++;
                    events.Add(new Ev { Handle = evh, Cls = a0, Spec = a1, Mode = a2, Handler = a3, Enabled = false });
                    r[2] = evh;
                }
                else if (fn == 0x0C) { Ev e = FindEv(a0); if (e != null) e.Enabled = true; r[2] = 1; }
                else if (fn == 0x0D) { Ev e = FindEv(a0); if (e != null) e.Enabled = false; r[2] = 1; }
                else if (fn == 0x09) { Ev e = FindEv(a0); if (e != null) events.Remove(e); r[2] = 1; }
                else if (fn == 0x0A || fn == 0x0B) r[2] = 1;
                else if (fn == 0x19) { hookAddr = a0; }
                else if (fn == 0x17 && inIrq) { r[31] = Magic; }
                else if (fn == 0x56) r[2] = 0x200;
                else if (fn == 0x57) r[2] = 0x874;
            }
        }

        private uint FindTick()
        {
            for (int i = 0; i < events.Count; i++)
            {
                Ev e = events[i];
                if (e.Cls == 0xF2000002 && e.Mode == 0x1000 && e.Handler != 0 && e.Enabled) return e.Handler;
            }
            return 0;
        }

        // ================= R3000 =================
        private string Run(int maxi, long stop)
        {
            uint[] R = r;
            uint pcl = pc, npcl = npc;
            int cnt = 0;
            string why = "slice done";
            while (cnt < maxi)
            {
                if (stop >= 0 && pcl == (uint)stop) { why = "returned"; break; }
                cnt++;
                uint pp = pcl & 0x1FFFFFFF;
                if (pp == 0xA0 || pp == 0xB0 || pp == 0xC0)
                {
                    Bios(pp == 0xA0 ? 'A' : (pp == 0xB0 ? 'B' : 'C'));
                    pcl = R[31]; npcl = pcl + 4;
                    continue;
                }
                if (pp >= 0x800000) { why = "executing outside RAM at " + pcl.ToString("X8"); break; }
                uint w = BitConverter.ToUInt32(ram, (int)(pp & 0x1FFFFF));
                uint cur = pcl; pcl = npcl; npcl = pcl + 4;
                int op = (int)(w >> 26);
                int rs = (int)((w >> 21) & 31), rt = (int)((w >> 16) & 31), rd = (int)((w >> 11) & 31);
                uint imm = w & 0xFFFF;
                int simm = (short)imm;
                switch (op)
                {
                    case 0:
                        {
                            int f = (int)(w & 63);
                            int sa = (int)((w >> 6) & 31);
                            switch (f)
                            {
                                case 0: R[rd] = R[rt] << sa; break;
                                case 2: R[rd] = R[rt] >> sa; break;
                                case 3: R[rd] = (uint)((int)R[rt] >> sa); break;
                                case 4: R[rd] = R[rt] << (int)(R[rs] & 31); break;
                                case 6: R[rd] = R[rt] >> (int)(R[rs] & 31); break;
                                case 7: R[rd] = (uint)((int)R[rt] >> (int)(R[rs] & 31)); break;
                                case 8: npcl = R[rs]; break;
                                case 9: { uint t = R[rs]; R[rd] = cur + 8; npcl = t; } break;
                                case 12: if (R[4] == 1) { R[2] = 1; crit = true; } else if (R[4] == 2) crit = false; break;
                                case 13: why = "BREAK at " + cur.ToString("X8"); goto done;
                                case 16: R[rd] = hi; break;
                                case 17: hi = R[rs]; break;
                                case 18: R[rd] = lo; break;
                                case 19: lo = R[rs]; break;
                                case 24: { long pr = (long)(int)R[rs] * (long)(int)R[rt]; lo = (uint)pr; hi = (uint)(pr >> 32); } break;
                                case 25: { ulong pr = (ulong)R[rs] * (ulong)R[rt]; lo = (uint)pr; hi = (uint)(pr >> 32); } break;
                                case 26:
                                    {
                                        long a = (int)R[rs], b = (int)R[rt];
                                        if (b == 0) { lo = a < 0 ? 1u : 0xFFFFFFFFu; hi = (uint)a; }
                                        else
                                        {
                                            long q = Math.Abs(a) / Math.Abs(b);
                                            if ((a < 0) != (b < 0)) q = -q;
                                            lo = (uint)q; hi = (uint)(a - q * b);
                                        }
                                    }
                                    break;
                                case 27:
                                    {
                                        uint a = R[rs], b = R[rt];
                                        if (b == 0) { lo = 0xFFFFFFFFu; hi = a; }
                                        else { lo = a / b; hi = a % b; }
                                    }
                                    break;
                                case 32: case 33: R[rd] = R[rs] + R[rt]; break;
                                case 34: case 35: R[rd] = R[rs] - R[rt]; break;
                                case 36: R[rd] = R[rs] & R[rt]; break;
                                case 37: R[rd] = R[rs] | R[rt]; break;
                                case 38: R[rd] = R[rs] ^ R[rt]; break;
                                case 39: R[rd] = ~(R[rs] | R[rt]); break;
                                case 42: R[rd] = (int)R[rs] < (int)R[rt] ? 1u : 0u; break;
                                case 43: R[rd] = R[rs] < R[rt] ? 1u : 0u; break;
                                default: why = "unknown SPECIAL " + f + " at " + cur.ToString("X8"); goto done;
                            }
                        }
                        break;
                    case 1:
                        {
                            uint t = cur + 4 + (uint)(simm << 2);
                            int sv = (int)R[rs];
                            if (rt == 16 || rt == 17) R[31] = cur + 8;
                            if (rt == 0 || rt == 16) { if (sv < 0) npcl = t; }
                            else { if (sv >= 0) npcl = t; }
                        }
                        break;
                    case 2: npcl = ((cur + 4) & 0xF0000000u) | ((w & 0x3FFFFFF) << 2); break;
                    case 3: R[31] = cur + 8; npcl = ((cur + 4) & 0xF0000000u) | ((w & 0x3FFFFFF) << 2); break;
                    case 4: if (R[rs] == R[rt]) npcl = cur + 4 + (uint)(simm << 2); break;
                    case 5: if (R[rs] != R[rt]) npcl = cur + 4 + (uint)(simm << 2); break;
                    case 6: if ((int)R[rs] <= 0) npcl = cur + 4 + (uint)(simm << 2); break;
                    case 7: if ((int)R[rs] > 0) npcl = cur + 4 + (uint)(simm << 2); break;
                    case 8: case 9: R[rt] = R[rs] + (uint)simm; break;
                    case 10: R[rt] = (int)R[rs] < simm ? 1u : 0u; break;
                    case 11: R[rt] = R[rs] < (uint)simm ? 1u : 0u; break;
                    case 12: R[rt] = R[rs] & imm; break;
                    case 13: R[rt] = R[rs] | imm; break;
                    case 14: R[rt] = R[rs] ^ imm; break;
                    case 15: R[rt] = imm << 16; break;
                    case 16:
                        if (rs == 0) R[rt] = c0[rd];
                        else if (rs == 4) c0[rd] = R[rt];
                        break;
                    case 18: break;
                    case 32: { uint v = Load(R[rs] + (uint)simm, 1); R[rt] = (v & 0x80) != 0 ? (v | 0xFFFFFF00u) : v; } break;
                    case 33: { uint v = Load(R[rs] + (uint)simm, 2); R[rt] = (v & 0x8000) != 0 ? (v | 0xFFFF0000u) : v; } break;
                    case 34:
                        {
                            uint a = R[rs] + (uint)simm; uint wd = Load(a & ~3u, 4); int sh = (int)(a & 3);
                            R[rt] = (R[rt] & (0x00FFFFFFu >> (8 * sh))) | (wd << (24 - 8 * sh));
                        }
                        break;
                    case 35: R[rt] = Load(R[rs] + (uint)simm, 4); break;
                    case 36: R[rt] = Load(R[rs] + (uint)simm, 1); break;
                    case 37: R[rt] = Load(R[rs] + (uint)simm, 2); break;
                    case 38:
                        {
                            uint a = R[rs] + (uint)simm; uint wd = Load(a & ~3u, 4); int sh = (int)(a & 3);
                            R[rt] = (R[rt] & (0xFFFFFF00u << (8 * (3 - sh)))) | (wd >> (8 * sh));
                        }
                        break;
                    case 40: Store(R[rs] + (uint)simm, R[rt], 1); break;
                    case 41: Store(R[rs] + (uint)simm, R[rt], 2); break;
                    case 42:
                        {
                            uint a = R[rs] + (uint)simm; uint wd = Load(a & ~3u, 4); int sh = (int)(a & 3);
                            Store(a & ~3u, (wd & (0xFFFFFF00u << (8 * sh))) | (R[rt] >> (24 - 8 * sh)), 4);
                        }
                        break;
                    case 43: Store(R[rs] + (uint)simm, R[rt], 4); break;
                    case 46:
                        {
                            uint a = R[rs] + (uint)simm; uint wd = Load(a & ~3u, 4); int sh = (int)(a & 3);
                            Store(a & ~3u, (wd & (0x00FFFFFFu >> (8 * (3 - sh)))) | (R[rt] << (8 * sh)), 4);
                        }
                        break;
                    case 50: case 58: break;
                    default:
                        why = "unknown opcode " + op + " at " + cur.ToString("X8") + " (word " + w.ToString("X8") + ")";
                        goto done;
                }
                R[0] = 0;
            }
        done:
            pc = pcl; npc = npcl; n += cnt;
            return why;
        }

        private string CallHandler(uint func, int budget)
        {
            uint[] sv = (uint[])r.Clone();
            uint spc = pc, snpc = npc, shi = hi, slo = lo;
            r[29] = r[29] - 0x200; r[31] = Magic; r[4] = 0;
            pc = func; npc = func + 4;
            string why = Run(budget, Magic);
            Array.Copy(sv, r, 32);
            pc = spc; npc = snpc; hi = shi; lo = slo;
            return why;
        }

        /// <summary>Delivers a hardware interrupt the way the Sony libapi expects: the BIOS jumps to the entry the program gave to HookEntryInt (a jmp_buf), and that handler ends with ReturnFromException.</summary>
        private string DeliverIrq(uint bits, int budget)
        {
            if (!irqMode || hookAddr == 0 || crit) return "skipped";
            if ((imask & bits) == 0) return "masked";
            if ((bits & 8) != 0)
            {
                uint dc = Iov(0x1F8010F4);
                if ((dc & 0x800000u) == 0 || (dc & (1u << 20)) == 0) return "dma irq not enabled";
                iov[0x1F8010F4] = dc | (1u << 28) | 0x80000000u;
            }
            istat |= bits;
            uint h = hookAddr;
            uint[] sv = (uint[])r.Clone();
            uint spc = pc, snpc = npc, shi = hi, slo = lo;
            bool scrit = crit;
            pc = Load(h, 4); npc = pc + 4;
            r[31] = pc;
            r[29] = Load(h + 4, 4); r[30] = Load(h + 8, 4);
            if (r[29] == 0) r[29] = sv[29] - 0x400;
            for (int i = 0; i < 8; i++) r[16 + i] = Load(h + 12 + (uint)i * 4, 4);
            r[28] = Load(h + 44, 4);
            r[2] = 1;
            inIrq = true; crit = true;
            string why = Run(budget, Magic);
            inIrq = false; crit = scrit;
            Array.Copy(sv, r, 32);
            pc = spc; npc = snpc; hi = shi; lo = slo;
            istat &= ~bits;
            return why;
        }
        private string RunPumped(int total)
        {
            string w = "slice done";
            for (int d = 0; d < total && w == "slice done"; d += 20000)
            {
                w = Run(20000, -1);
                if (w != "slice done") break;
                if (irqMode)
                {
                    if (pendDma) { pendDma = false; DeliverIrq(8, 300000); }
                    DeliverIrq(1, 300000);
                }
            }
            return w;
        }

        // ================= SPU voices =================
        private static double Sv(int x)
        {
            if ((x & 0x8000) != 0) return 0.0;
            x = (x & 0x7FFF) << 1;
            if (x >= 0x8000) x -= 0x10000;
            return x / 32768.0;
        }

        private void Dec(Voice v)
        {
            int a = v.Addr & 0x7FFF0;
            byte[] m = spuram;
            int h = m[a], fl = m[a + 1];
            int sh = h & 15, fi = (h >> 4) & 7;
            if (fi > 4) fi = 4;
            int f0 = F0[fi], f1 = F1[fi];
            int s1 = v.S1, s2 = v.S2, k = 0;
            int[] sm = v.Smp;
            for (int bi = 2; bi < 16; bi++)
            {
                int b = m[a + bi];
                for (int half = 0; half < 2; half++)
                {
                    int nb = half == 0 ? (b & 15) : (b >> 4);
                    if ((nb & 8) != 0) nb -= 16;
                    int t = ((nb << 12) >> sh) + ((s1 * f0 + s2 * f1 + 32) >> 6);
                    if (t > 32767) t = 32767; else if (t < -32768) t = -32768;
                    sm[k++] = t; s2 = s1; s1 = t;
                }
            }
            v.S1 = s1; v.S2 = s2; v.Flags = fl;
            if ((fl & 4) != 0) v.Rep = a;
        }

        private void SetPh(Voice v, int ph)
        {
            v.Ph = ph; v.Ec = 0;
            int a, sh, bs;
            if (ph == 0)
            {
                a = v.A1; v.Exp = a >> 15; sh = (a >> 10) & 31; bs = 7 - ((a >> 8) & 3); v.Dec = 0;
            }
            else if (ph == 1)
            {
                a = v.A1; v.Exp = 1; sh = (a >> 4) & 15; bs = -8; v.Dec = 1;
            }
            else if (ph == 2)
            {
                a = v.A2; v.Exp = a >> 15; sh = (a >> 8) & 31; int st = (a >> 6) & 3;
                if (((a >> 14) & 1) != 0) { bs = -8 + st; v.Dec = 1; }
                else { bs = 7 - st; v.Dec = 0; }
            }
            else
            {
                a = v.A2; v.Exp = (a >> 5) & 1; sh = a & 31; bs = -8; v.Dec = 1;
            }
            v.Cyc = 1 << Math.Max(0, sh - 11);
            v.Stp = bs << Math.Max(0, 11 - sh);
        }

        private void LogVoice(int kind, Voice v)
        {
            if (cap == null) return;
            int rv = spu[0x198] | (spu[0x199] << 8) | (spu[0x19A] << 16);
            cap.Events.Add(new PsxLogEvent
            {
                Frame = pcmPos / 2,
                Kind = kind,
                Voice = v.I,
                Addr = (v.Start * 8) & 0x7FFF0,
                Pitch = v.Pitch & 0x3FFF,
                Vl = v.Vl,
                Vr = v.Vr,
                A1 = v.A1,
                A2 = v.A2,
                Rep = v.Rep,
                Rev = ((rv >> v.I) & 1) != 0
            });
        }

        private byte[] ExtractSample(int addr)
        {
            int a = addr & 0x7FFF0;
            int max = Math.Min(0x40000, 0x80000 - a);
            for (int o = 0; o + 16 <= max; o += 16)
            {
                if ((spuram[a + o + 1] & 1) != 0)
                {
                    byte[] s = new byte[o + 16];
                    Buffer.BlockCopy(spuram, a, s, 0, o + 16);
                    return s;
                }
            }
            return null;
        }

        private void KeyOn(int i)
        {
            Voice v = voices[i];
            if (v.On) steals++;
            // v.Rep is NOT reset: the driver writes the repeat address (reg 0xE) before the key-on.
            v.On = true; v.Addr = (v.Start * 8) & 0x7FFF0; v.Idx = 0; v.Cnt = 0;
            v.S1 = 0; v.S2 = 0; Dec(v); v.Cur = v.Smp[0]; v.Prev = 0;
            v.Level = 0; SetPh(v, 0); endx &= ~(1 << i);
            konN++;
            if (cap != null)
            {
                if (!cap.Samples.ContainsKey(v.Addr)) cap.Samples[v.Addr] = ExtractSample(v.Addr);
                LogVoice(1, v);
            }
        }

        private void KeyOff(int i)
        {
            Voice v = voices[i];
            if (v.On && v.Ph != 3) { LogVoice(2, v); SetPh(v, 3); koffN++; }
        }

        private void Adv(Voice v)
        {
            v.Idx++;
            if (v.Idx >= 28)
            {
                int fl = v.Flags;
                if ((fl & 1) != 0)
                {
                    endx |= 1 << v.I;
                    if ((fl & 2) != 0) v.Addr = v.Rep;
                    else { LogVoice(5, v); v.On = false; v.Level = 0; return; }
                }
                else v.Addr = (v.Addr + 16) & 0x7FFF0;
                Dec(v); v.Idx = 0;
            }
            v.Prev = v.Cur; v.Cur = v.Smp[v.Idx];
        }

        private void SpuReg(int q)
        {
            int val = spu[q] | (spu[q + 1] << 8);
            if (q < 0x180)
            {
                Voice v = voices[q >> 4];
                switch (q & 15)
                {
                    case 0: { bool chg = val != v.Vl; v.Vl = val; if (chg && cap != null && v.On && v.Ph != 3) LogVoice(4, v); } break;
                    case 2: { bool chg = val != v.Vr; v.Vr = val; if (chg && cap != null && v.On && v.Ph != 3) LogVoice(4, v); } break;
                    case 4: { bool chg = val != v.Pitch; v.Pitch = val; if (chg && cap != null && v.On && v.Ph != 3) LogVoice(3, v); } break;
                    case 6: v.Start = val; break;
                    case 8: v.A1 = val; break;
                    case 10: v.A2 = val; break;
                    case 14: v.Rep = (val * 8) & 0x7FFF0; break;
                }
            }
            else if (q == 0x188 || q == 0x18A)
            {
                int b0 = q == 0x188 ? 0 : 16;
                for (int b = 0; b < 16; b++) if (((val >> b) & 1) != 0 && b0 + b < 24) KeyOn(b0 + b);
            }
            else if (q == 0x18C || q == 0x18E)
            {
                int b0 = q == 0x18C ? 0 : 16;
                for (int b = 0; b < 16; b++) if (((val >> b) & 1) != 0 && b0 + b < 24) KeyOff(b0 + b);
            }
        }

        // ================= SPU reverb (driven only by the registers the program wrote) =================
        private bool useRev;
        private static readonly int[] Fir =
        {
            -1, 0, 2, 0, -10, 0, 35, 0, -103, 0, 266, 0, -616, 0, 1332, 0, -2960, 0, 10246, 16384,
            10246, 0, -2960, 0, 1332, 0, -616, 0, 266, 0, -103, 0, 35, 0, -10, 0, 2, 0, -1
        };
        private readonly double[] rIL = new double[39], rIR = new double[39], rOL = new double[39], rOR = new double[39];
        private int rPos, rPhase, rBase = -1, rStart, rCur;

        private static int Cl(int x) { return x > 32767 ? 32767 : (x < -32768 ? -32768 : x); }

        private int RvA(int off)
        {
            int a = rCur + off;
            if (a > 0x3FFFF) a = rStart + (a - 0x40000);
            else if (a < rStart) a += 0x40000 - rStart;
            return a;
        }

        private int RvR(int reg, int extra) { return BitConverter.ToInt16(spuram, RvA(reg * 4 + extra) * 2); }

        private void RvW(int reg, int extra, int v)
        {
            int a = RvA(reg * 4 + extra) * 2;
            v = Cl(v);
            spuram[a] = (byte)v; spuram[a + 1] = (byte)(v >> 8);
        }

        private int RG(int i) { return spu[0x1C0 + i * 2] | (spu[0x1C1 + i * 2] << 8); }
        private int RS(int i) { return (short)RG(i); }

        /// <summary>One step of the reverb unit at 22050 Hz.</summary>
        private void Rev22(int inL, int inR, out int outL, out int outR)
        {
            int dAPF1 = RG(0), dAPF2 = RG(1);
            int vIIR = RS(2), vC1 = RS(3), vC2 = RS(4), vC3 = RS(5), vC4 = RS(6), vWALL = RS(7), vA1 = RS(8), vA2 = RS(9);
            int mLSAME = RG(10), mRSAME = RG(11), mLC1 = RG(12), mRC1 = RG(13), mLC2 = RG(14), mRC2 = RG(15);
            int dLSAME = RG(16), dRSAME = RG(17), mLDIFF = RG(18), mRDIFF = RG(19), mLC3 = RG(20), mRC3 = RG(21);
            int mLC4 = RG(22), mRC4 = RG(23), dLDIFF = RG(24), dRDIFF = RG(25), mLA1 = RG(26), mRA1 = RG(27), mLA2 = RG(28), mRA2 = RG(29);
            int vLIN = RS(30), vRIN = RS(31);
            int vLOUT = (short)(spu[0x184] | (spu[0x185] << 8)), vROUT = (short)(spu[0x186] | (spu[0x187] << 8));

            int lin = Cl((vLIN * inL) >> 15), rin = Cl((vRIN * inR) >> 15);
            int p, t;

            // same side and different side reflections
            p = RvR(mLSAME, -1); t = Cl(lin + ((RvR(dLSAME, 0) * vWALL) >> 15) - p); RvW(mLSAME, 0, ((t * vIIR) >> 15) + p);
            p = RvR(mRSAME, -1); t = Cl(rin + ((RvR(dRSAME, 0) * vWALL) >> 15) - p); RvW(mRSAME, 0, ((t * vIIR) >> 15) + p);
            p = RvR(mLDIFF, -1); t = Cl(lin + ((RvR(dRDIFF, 0) * vWALL) >> 15) - p); RvW(mLDIFF, 0, ((t * vIIR) >> 15) + p);
            p = RvR(mRDIFF, -1); t = Cl(rin + ((RvR(dLDIFF, 0) * vWALL) >> 15) - p); RvW(mRDIFF, 0, ((t * vIIR) >> 15) + p);

            // early echo (comb)
            int lo = Cl(((vC1 * RvR(mLC1, 0)) >> 15) + ((vC2 * RvR(mLC2, 0)) >> 15) + ((vC3 * RvR(mLC3, 0)) >> 15) + ((vC4 * RvR(mLC4, 0)) >> 15));
            int ro = Cl(((vC1 * RvR(mRC1, 0)) >> 15) + ((vC2 * RvR(mRC2, 0)) >> 15) + ((vC3 * RvR(mRC3, 0)) >> 15) + ((vC4 * RvR(mRC4, 0)) >> 15));

            // all-pass 1 and 2
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

        /// <summary>One 44100 Hz sample in, one reverb sample out (down-filter, 22050 Hz reverb, zero-stuff + up-filter).</summary>
        private void RevSample(double inL, double inR, out double oL, out double oR)
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

        private void RenderSamples(int count)
        {
            int na = 0;
            for (int i = 0; i < 24; i++)
            {
                Voice v = voices[i];
                if (v.On) { act[na++] = v; v.Fl = Sv(v.Vl); v.Fr = Sv(v.Vr); }
            }
            if (na > maxOn) maxOn = na;
            double mvl = Sv(spu[0x180] | (spu[0x181] << 8)), mvr = Sv(spu[0x182] | (spu[0x183] << 8));

            int revon = spu[0x198] | (spu[0x199] << 8) | (spu[0x19A] << 16) | (spu[0x19B] << 24);
            bool rev = useRev && (spu[0x1AA] & 0x80) != 0;
            int mb = spu[0x1A2] | (spu[0x1A3] << 8);
            if (rev && mb != 0 && mb != rBase) { rBase = mb; rStart = mb * 4; rCur = rStart; }
            if (rev && rBase <= 0) rev = false;

            if (pcmPos + count * 2 > pcm.Length) Array.Resize(ref pcm, Math.Max(pcm.Length * 2, pcmPos + count * 2));
            for (int s = 0; s < count; s++)
            {
                double L = 0.0, R2 = 0.0, revL = 0.0, revR = 0.0;
                for (int k = 0; k < na; k++)
                {
                    Voice v = act[k];
                    if (!v.On) continue;
                    int cyc = v.Cyc;
                    if (v.Exp != 0 && v.Dec == 0 && v.Level > 0x6000) cyc *= 4;
                    v.Ec++;
                    if (v.Ec >= cyc)
                    {
                        v.Ec = 0;
                        int st = v.Stp;
                        if (v.Exp != 0 && v.Dec != 0) st = (st * v.Level) >> 15;
                        int lv = v.Level + st;
                        if (lv > 0x7FFF) lv = 0x7FFF; else if (lv < 0) lv = 0;
                        v.Level = lv;
                        int ph = v.Ph;
                        if (ph == 0) { if (lv >= 0x7FFF) SetPh(v, 1); }
                        else if (ph == 1) { if (lv <= ((v.A1 & 15) + 1) * 0x800) SetPh(v, 2); }
                        else if (ph == 3) { if (lv == 0) { v.On = false; LogVoice(5, v); continue; } }
                    }
                    double x = (v.Prev + (v.Cur - v.Prev) * (v.Cnt / 4096.0)) * v.Level / 32768.0;
                    double xl = x * v.Fl, xr = x * v.Fr;
                    L += xl; R2 += xr;
                    if (rev && ((revon >> v.I) & 1) != 0) { revL += xl; revR += xr; }
                    v.Cnt += v.Pitch & 0x3FFF;
                    while (v.Cnt >= 0x1000)
                    {
                        v.Cnt -= 0x1000;
                        Adv(v);
                        if (!v.On) break;
                    }
                }
                if (rev)
                {
                    double wl, wr;
                    RevSample(revL, revR, out wl, out wr);
                    L += wl; R2 += wr;
                }
                int l = (int)(L * mvl), rr = (int)(R2 * mvr);
                if (l > 32767) l = 32767; else if (l < -32768) l = -32768;
                if (rr > 32767) rr = 32767; else if (rr < -32768) rr = -32768;
                if (l > peak) peak = l;
                if (-l > peak) peak = -l;
                if (rr > peak) peak = rr;
                if (-rr > peak) peak = -rr;
                pcm[pcmPos++] = (short)l;
                pcm[pcmPos++] = (short)rr;
            }
        }

        // ================= public API =================
        private static byte[] Inflate(byte[] src, int off, int len)
        {
            using (var ms = new MemoryStream(src, off, len))
            using (var z = new ZLibStream(ms, CompressionMode.Decompress))
            using (var o = new MemoryStream())
            {
                z.CopyTo(o);
                return o.ToArray();
            }
        }

        /// <summary>Runs the PSF1 program and returns the dry SPU output for the given number of seconds.</summary>
        public static PsxResult Render(byte[] psf, double seconds, IProgress<int> progress, CancellationToken ct)
        {
            return Render(psf, seconds, progress, ct, false);
        }

        /// <summary>Same, with the SPU reverb added when reverb = true.</summary>
        /// <summary>Same, with the SPU reverb added when reverb = true.</summary>
        public static PsxResult Render(byte[] psf, double seconds, IProgress<int> progress, CancellationToken ct, bool reverb)
        {
            return Render(psf, seconds, progress, ct, reverb, false);
        }

        public static string DescribeLoad(byte[] psf)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                string dir = string.IsNullOrEmpty(LastPsfPath) ? "" : System.IO.Path.GetDirectoryName(LastPsfPath);
                var exes = new List<byte[]>();
                var missing = new List<string>();
                var main = new int[1];
                CollectExes(psf, dir, exes, 0, missing, main);
                sb.AppendLine("Programs loaded (load order), the file's own program is #" + main[0] + ":");
                for (int i = 0; i < exes.Count; i++)
                {
                    byte[] x = exes[i];
                    if (x.Length < 0x800) { sb.AppendLine("  #" + i + ": " + x.Length + " bytes, no header"); continue; }
                    sb.AppendLine("  #" + i + ": loaded at " + BitConverter.ToUInt32(x, 0x18).ToString("X8") + ", size " + BitConverter.ToUInt32(x, 0x1C).ToString("X") +
                                  ", start PC " + BitConverter.ToUInt32(x, 0x10).ToString("X8") + ", GP " + BitConverter.ToUInt32(x, 0x14).ToString("X8") +
                                  ", SP " + BitConverter.ToUInt32(x, 0x30).ToString("X8"));
                }
                if (missing.Count > 0) sb.AppendLine("Missing library files: " + string.Join(", ", missing));
                for (int pass = 0; pass < 2; pass++)
                {
                    int from = pass == 0 ? 0 : main[0];
                    if (pass == 1 && main[0] <= 0) break;
                    byte[] m = MergeExes(exes, from);
                    uint pc = BitConverter.ToUInt32(m, 0x10), dest = BitConverter.ToUInt32(m, 0x18);
                    long off = 0x800L + (pc - dest);
                    sb.Append("Merged image, registers of #" + from + ": PC " + pc.ToString("X8") + ", words there:");
                    if (pc >= dest && off + 32 <= m.Length)
                    {
                        for (int k = 0; k < 8; k++) sb.Append(" " + BitConverter.ToUInt32(m, (int)off + k * 4).ToString("X8"));
                        var asc = new System.Text.StringBuilder();
                        for (int k = 0; k < 16; k++) { byte c = m[off + k]; asc.Append(c >= 32 && c < 127 ? (char)c : '.'); }
                        sb.Append("  text: " + asc);
                    }
                    else sb.Append(" (PC is outside the program)");
                    sb.AppendLine();
                }
            }
            catch (Exception ex) { sb.AppendLine("(could not describe the load: " + ex.Message + ")"); }
            return sb.ToString();
        }

        /// <summary>Folder of the PSF that was opened last: a minipsf looks for its _lib files there.</summary>
        public static string LastPsfPath;

        private static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        private static void LoadLib(string name, string dir, List<byte[]> exes, int depth, List<string> missing, int[] mainIdx)
        {
            if (string.IsNullOrEmpty(name)) return;
            string p = Path.Combine(dir ?? "", name);
            if (!File.Exists(p) && !string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir))
                    if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) { p = f; break; }
            if (!File.Exists(p)) { missing.Add(name); return; }
            CollectExes(File.ReadAllBytes(p), Path.GetDirectoryName(p), exes, depth + 1, missing, mainIdx);
        }

        /// <summary>Load order of the PSF format: _lib first, then the file's own program, then _lib2, _lib3 ... on top.</summary>
        private static void CollectExes(byte[] psf, string dir, List<byte[]> exes, int depth, List<string> missing, int[] mainIdx)
        {
            if (depth > 6) return;
            Dictionary<string, string> tags;
            int ver;
            byte[] prog;
            try { prog = PS2AudioStudio.Archives.PsfFile.ReadProgram(psf, out tags, out ver); }
            catch (Exception) { return; }
            if (ver != 1) return;
            string lib;
            if (tags.TryGetValue("_lib", out lib)) LoadLib(lib, dir, exes, depth, missing, mainIdx);
            if (prog != null && prog.Length >= 0x800 && prog[0] == (byte)'P' && prog[1] == (byte)'S' && prog[2] == (byte)'-' && prog[3] == (byte)'X')
            {
                exes.Add(prog);
                if (depth == 0) mainIdx[0] = exes.Count - 1;
            }
            for (int i = 2; i < 32; i++)
                if (tags.TryGetValue("_lib" + i, out lib)) LoadLib(lib, dir, exes, depth, missing, mainIdx);
        }

        /// <summary>Lays all programs over each other in RAM order and returns one PS-X EXE. The start registers (PC/GP/SP) come from exes[regsFrom].</summary>
        private static byte[] MergeExes(List<byte[]> exes, int regsFrom)
        {
            if (exes.Count == 1) return exes[0];
            byte[] img = new byte[0x200000];
            int lo = int.MaxValue, hi = 0;
            foreach (byte[] e in exes)
            {
                uint dest = U32(e, 0x18), size = U32(e, 0x1C);
                int b = (int)(dest & 0x1FFFFF);
                int len = (int)Math.Min((long)size, (long)(e.Length - 0x800));
                len = Math.Min(len, 0x200000 - b);
                if (len <= 0) continue;
                Buffer.BlockCopy(e, 0x800, img, b, len);
                if (b < lo) lo = b;
                if (b + len > hi) hi = b + len;
            }
            if (hi <= lo) return exes[0];
            byte[] o = new byte[0x800 + (hi - lo)];
            Buffer.BlockCopy(exes[regsFrom], 0, o, 0, 0x800);
            Buffer.BlockCopy(img, lo, o, 0x800, hi - lo);
            WriteU32(o, 0x18, 0x80000000u | (uint)lo);
            WriteU32(o, 0x1C, (uint)(hi - lo));
            return o;
        }

        /// <summary>capture = true also logs every key-on / key-off / pitch / volume write and the samples the driver used (PsxResult.Capture).</summary>
        public static PsxResult Render(byte[] psf, double seconds, IProgress<int> progress, CancellationToken ct, bool reverb, bool capture)
        {
            if (psf == null || psf.Length < 16 || psf[0] != (byte)'P' || psf[1] != (byte)'S' || psf[2] != (byte)'F')
                throw new PsxUnsupportedException("Not a PSF file.");
            if (psf[3] != 1)
                throw new PsxUnsupportedException("Only PSF version 1 (PlayStation 1) can be run. This file is version " + psf[3] + ".");
            string dir = null;
            try { if (!string.IsNullOrEmpty(LastPsfPath)) dir = Path.GetDirectoryName(LastPsfPath); } catch (Exception) { }
            var exes = new List<byte[]>();
            var missing = new List<string>();
            var mainIdx = new int[] { -1 };
            CollectExes(psf, dir, exes, 0, missing, mainIdx);
            string missNote = missing.Count > 0 ? " Library file(s) not found: " + string.Join(", ", missing) + " (they must be in the same folder as the PSF)." : "";
            if (exes.Count == 0)
                throw new PsxUnsupportedException(missing.Count > 0
                    ? "This PSF needs its library file(s): " + string.Join(", ", missing) + ". Put them in the same folder as the PSF and open it again."
                    : "The PSF does not contain a PS-X EXE.");

            // candidates: start registers from the first loaded program (the library), then from the file's own program
            var cands = new List<byte[]>();
            cands.Add(MergeExes(exes, 0));
            if (exes.Count > 1 && mainIdx[0] > 0) cands.Add(MergeExes(exes, mainIdx[0]));

            PsxDriver m = null;
            string why = "";
            uint func = 0;
            for (int ci = 0; ci < cands.Count && func == 0; ci++)
            {
                for (int attempt = 0; attempt < 2 && func == 0; attempt++)
                {
                    m = new PsxDriver(cands[ci]);
                    m.useRev = reverb;
                    m.irqMode = attempt == 1;
                    if (capture) m.cap = new PsxCapture();
                    int n0 = 0;
                    while (n0 < 6000000)
                    {
                        why = m.Run(20000, -1); n0 += 20000;
                        if (why != "slice done") break;
                        func = m.FindTick();
                        if (func != 0) break;
                        if (m.irqMode)
                        {
                            if (m.pendDma) { m.pendDma = false; m.DeliverIrq(8, 300000); }
                            m.DeliverIrq(1, 300000);
                        }
                    }
                    if (func != 0) { why = m.irqMode ? m.RunPumped(200000) : m.Run(200000, -1); func = m.FindTick(); }
                }
            }
            if (func == 0)
            {
                var sb0 = new StringBuilder();
                foreach (Ev e in m.events)
                    sb0.Append(" [class " + e.Cls.ToString("X8") + " mode " + e.Mode.ToString("X") + " handler " + e.Handler.ToString("X8") + (e.Enabled ? " enabled" : " not enabled") + "]");
                throw new PsxUnsupportedException("No sound-driver tick found. This player only knows drivers that run from a root-counter-2 event callback (OpenEvent class F2000002, mode 1000). CPU stopped with: " + why + " (PC " + m.pc.ToString("X8") + "). Events seen:" + (m.events.Count == 0 ? " none" : sb0.ToString()) + "." + missNote);
            }
            uint target = m.Iov(0x1F801128) & 0xFFFF, mode = m.Iov(0x1F801124);
            if (target == 0) throw new PsxUnsupportedException("The timer of the sound driver (root counter 2) has no target value.");
            double hz = 33868800.0 / (((mode & 0x200) != 0 ? 8.0 : 1.0) * target);
            if (hz < 20 || hz > 2000) throw new PsxUnsupportedException("The driver timer runs at " + hz.ToString("0.0") + " Hz, which is not a believable song tick rate.");
            int totalTicks = (int)Math.Round(seconds * hz);
            double per = 44100.0 / hz, acc = 0.0;
            int done = 0;
            string stopNote = "";
            for (int tk = 0; tk < totalTicks; tk++)
            {
                ct.ThrowIfCancellationRequested();
                why = m.Run(400, -1);
                if (why != "slice done") { stopNote = "main program stopped: " + why; break; }
                if (m.irqMode && m.pendDma) { m.pendDma = false; m.DeliverIrq(8, 300000); }
                func = m.FindTick();
                if (func != 0)
                {
                    why = m.CallHandler(func, 3000000);
                    if (why != "returned") { stopNote = "tick handler did not return: " + why; break; }
                }
                acc += per;
                int nn = (int)acc; acc -= nn;
                m.RenderSamples(nn);
                done = tk + 1;
                if (progress != null && (tk % 24) == 23) progress.Report((int)(100L * (tk + 1) / totalTicks));
            }
            var result = new PsxResult();
            result.Frames = m.pcmPos / 2;
            result.Pcm = new short[m.pcmPos];
            Array.Copy(m.pcm, result.Pcm, m.pcmPos);
            result.TickHz = hz; result.Ticks = done;
            result.KeyOns = m.konN; result.KeyOffs = m.koffN; result.Steals = m.steals; result.MaxVoices = m.maxOn; result.Peak = m.peak;
            if (m.cap != null)
            {
                m.cap.Frames = m.pcmPos / 2;
                for (int i = 0; i < 32; i++) m.cap.ReverbRegs[i] = m.RG(i);
                m.cap.ReturnL = (short)(m.spu[0x184] | (m.spu[0x185] << 8));
                m.cap.ReturnR = (short)(m.spu[0x186] | (m.spu[0x187] << 8));
                m.cap.WorkAreaByte = (m.spu[0x1A2] | (m.spu[0x1A3] << 8)) * 8;
                m.cap.ReverbEnabled = (m.spu[0x1AA] & 0x80) != 0;
                result.Capture = m.cap;
            }
            var sb = new StringBuilder();
            sb.AppendLine("Driver tick " + hz.ToString("0.###") + " Hz, handler at " + func.ToString("X8") + ", " + done + " ticks = " + (done / hz).ToString("0.0") + " s.");
            if (reverb) sb.AppendLine("Reverb ON: work area from byte 0x" + (m.rBase * 8).ToString("X") + ", return volume L " + ((short)(m.spu[0x184] | (m.spu[0x185] << 8))).ToString("X4") + " R " + ((short)(m.spu[0x186] | (m.spu[0x187] << 8))).ToString("X4") + ".");
            if (stopNote.Length > 0) sb.AppendLine("Stopped early: " + stopNote);
            result.Report = sb.ToString();
            return result;
        }

        public static void WriteWav(string path, short[] pcm, int frames, int rate)
        {
            int bytes = frames * 4;
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + bytes);
                bw.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); bw.Write(16);
                bw.Write((short)1); bw.Write((short)2); bw.Write(rate); bw.Write(rate * 4); bw.Write((short)4); bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(bytes);
                byte[] buf = new byte[bytes];
                Buffer.BlockCopy(pcm, 0, buf, 0, bytes);
                bw.Write(buf);
            }
        }
    }
}
