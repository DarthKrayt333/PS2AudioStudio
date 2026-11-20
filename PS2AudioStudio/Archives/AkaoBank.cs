using System;
using System.Collections.Generic;

namespace PS2AudioStudio.Archives
{
    public class AkaoSample
    {
        public int Index;
        public int Start;          // byte offset inside the bank's sample data
        public int LoopPoint;      // as stored in the table (offset inside the bank data)
        public int Length;         // bytes, multiple of 16
        public short FineTune;     // table bytes 8-9 (meaning not confirmed)
        public byte UnityKey;      // table byte 10 (meaning not confirmed)
        public byte Extra;         // table byte 11
        public ushort Adsr1, Adsr2;
        public bool Loops;         // the last block carries the loop flag
        public int LoopInSample;   // loop start in bytes from the start of this sample
        public byte[] Adpcm;       // raw PS-ADPCM blocks
        public long FileOffset;    // where the sample starts in the buffer
    }

    public class AkaoBank
    {
        public int MarkerOffset, TableOffset, DataOffset, DataSize, Entries;
        public List<AkaoSample> Samples = new List<AkaoSample>();
    }

    public static class AkaoBankReader
    {
        private static long U32(byte[] d, long p)
        {
            if (p < 0 || p + 4 > d.Length) return 0;
            return BitConverter.ToUInt32(d, (int)p);
        }

        public static List<AkaoBank> Find(byte[] d)
        {
            var result = new List<AkaoBank>();
            if (d == null) return result;

            var marks = new List<int>();
            for (int i = 0; i + 4 <= d.Length; i++)
                if (d[i] == 0x41 && d[i + 1] == 0x4B && d[i + 2] == 0x41 && d[i + 3] == 0x4F) marks.Add(i);

            for (int k = 0; k < marks.Count; k++)
            {
                int m = marks[k];
                long next = k + 1 < marks.Count ? marks[k + 1] : d.Length;
                long dist = next - m;
                long size = U32(d, m + 0x14);
                long count = U32(d, m + 0x1C);
                if (count < 1 || count > 512 || size < 32) continue;
                if (0x40 + count * 16 + size > dist) continue;

                var bank = new AkaoBank();
                bank.MarkerOffset = m;
                bank.TableOffset = m + 0x40;
                bank.DataOffset = (int)(m + 0x40 + count * 16);
                bank.DataSize = (int)size;
                bank.Entries = (int)count;

                var starts = new long[count];
                for (int i = 0; i < count; i++) starts[i] = U32(d, bank.TableOffset + i * 16L);
                var sorted = new List<long>(starts);
                sorted.Sort();

                for (int i = 0; i < count; i++)
                {
                    long e = bank.TableOffset + i * 16L;
                    long st = starts[i];
                    long end = size;
                    foreach (long s in sorted) if (s > st) { end = s; break; }
                    long len = (end - st) / 16 * 16;

                    var smp = new AkaoSample();
                    smp.Index = i;
                    smp.Start = (int)st;
                    smp.LoopPoint = (int)U32(d, e + 4);
                    smp.FineTune = (short)(d[e + 8] | (d[e + 9] << 8));
                    smp.UnityKey = d[e + 10];
                    smp.Extra = d[e + 11];
                    smp.Adsr1 = (ushort)(d[e + 12] | (d[e + 13] << 8));
                    smp.Adsr2 = (ushort)(d[e + 14] | (d[e + 15] << 8));
                    smp.FileOffset = bank.DataOffset + st;

                    if (st >= size || bank.DataOffset + st + len > d.Length)
                        len = Math.Max(0, (d.Length - bank.DataOffset - st) / 16 * 16);
                    if (st >= size || len < 32)
                    {
                        smp.Adpcm = new byte[0];
                        bank.Samples.Add(smp);
                        continue;
                    }

                    smp.Length = (int)len;
                    smp.Adpcm = new byte[len];
                    Array.Copy(d, (int)(bank.DataOffset + st), smp.Adpcm, 0, (int)len);

                    int flag = smp.Adpcm[len - 15];
                    smp.Loops = (flag & 2) != 0;
                    if (smp.Loops && smp.LoopPoint >= st && smp.LoopPoint < st + len)
                        smp.LoopInSample = (int)((smp.LoopPoint - st) / 16 * 16);
                    bank.Samples.Add(smp);
                }
                result.Add(bank);
            }
            return result;
        }
    }
}
