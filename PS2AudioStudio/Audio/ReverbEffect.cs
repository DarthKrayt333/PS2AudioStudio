using System;

namespace PS2AudioStudio.Audio
{
    public sealed class ReverbPreset
    {
        public string Name;
        public bool Echo;               // true = feedback delay, false = room reverb
        public double Size = 1.0;       // scales the delay lengths (room size)
        public double Decay = 1.0;      // seconds until -60 dB
        public double Damp = 0.3;       // 0 = bright tail, 0.9 = dull tail
        public double PreMs = 0;        // pre-delay
        public double DelayMs = 250;    // echo time
        public double Feedback = 0.4;   // echo repeats
        public int Mix = 30;            // default amount in %

        public double TailSeconds
        {
            get
            {
                double t;
                if (Echo)
                {
                    double fb = Math.Max(0.01, Math.Min(0.95, Feedback));
                    t = DelayMs / 1000.0 * Math.Ceiling(3.0 / -Math.Log10(fb));
                }
                else t = Decay + PreMs / 1000.0;
                return Math.Min(8.0, t);
            }
        }
    }

    public static class ReverbEffect
    {
        public static readonly ReverbPreset[] Presets =
        {
            new ReverbPreset { Name = "Room",             Size = 0.6, Decay = 0.6, Damp = 0.35, PreMs = 0,  Mix = 25 },
            new ReverbPreset { Name = "Studio (small)",   Size = 0.8, Decay = 0.9, Damp = 0.25, PreMs = 5,  Mix = 25 },
            new ReverbPreset { Name = "Studio (large)",   Size = 1.1, Decay = 1.6, Damp = 0.30, PreMs = 10, Mix = 28 },
            new ReverbPreset { Name = "Hall",             Size = 1.6, Decay = 2.8, Damp = 0.45, PreMs = 20, Mix = 30 },
            new ReverbPreset { Name = "Space",            Size = 2.2, Decay = 5.0, Damp = 0.55, PreMs = 30, Mix = 35 },
            new ReverbPreset { Name = "Outdoor",          Size = 2.2, Decay = 1.8, Damp = 0.70, PreMs = 45, Mix = 22 },
            new ReverbPreset { Name = "City",             Size = 2.4, Decay = 2.5, Damp = 0.65, PreMs = 60, Mix = 25 },
            new ReverbPreset { Name = "Echo",             Echo = true, DelayMs = 240, Feedback = 0.50, Damp = 0.35, Mix = 35 },
            new ReverbPreset { Name = "Delay",            Echo = true, DelayMs = 450, Feedback = 0.35, Damp = 0.10, Mix = 35 },
            new ReverbPreset { Name = "Pipe",             Echo = true, DelayMs = 40,  Feedback = 0.85, Damp = 0.15, Mix = 30 },
        };

        private static readonly int[] CombTuning = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        private static readonly int[] AllpassTuning = { 556, 441, 341, 225 };

        private sealed class Engine
        {
            private readonly bool echo;
            private readonly float damp;
            private readonly float[][] cb, ab;
            private readonly int[] ci, ai;
            private readonly float[] cfb, cin, cstore;
            private readonly float[] pre; private int pi;
            private readonly float[] dl; private int di; private readonly float efb; private float estore;

            public Engine(ReverbPreset p, int rate, int index)
            {
                damp = (float)Math.Max(0, Math.Min(0.95, p.Damp));
                if (p.Echo)
                {
                    echo = true;
                    int n = Math.Max(2, (int)(p.DelayMs * (1 + 0.07 * index) * rate / 1000.0));   // right channel slightly later = width
                    dl = new float[n];
                    efb = (float)Math.Max(0, Math.Min(0.95, p.Feedback));
                    return;
                }
                int preN = (int)(p.PreMs * rate / 1000.0);
                if (preN > 0) pre = new float[preN];

                double k = rate / 44100.0;
                int spread = (int)Math.Round(23 * index * k);
                int nc = CombTuning.Length;
                cb = new float[nc][]; ci = new int[nc]; cfb = new float[nc]; cin = new float[nc]; cstore = new float[nc];
                for (int i = 0; i < nc; i++)
                {
                    int len = Math.Max(8, (int)Math.Round(CombTuning[i] * k * p.Size) + spread);
                    cb[i] = new float[len];
                    double g = Math.Pow(10, -3.0 * len / (Math.Max(0.05, p.Decay) * rate));
                    g = Math.Min(0.995, g);
                    cfb[i] = (float)g;
                    cin[i] = (float)(0.094 * (1 - g));
                }
                int na = AllpassTuning.Length;
                ab = new float[na][]; ai = new int[na];
                for (int i = 0; i < na; i++)
                    ab[i] = new float[Math.Max(4, (int)Math.Round(AllpassTuning[i] * k) + spread)];
            }

            public float Process(float x)
            {
                if (echo)
                {
                    float d = dl[di];
                    estore = d * (1 - damp) + estore * damp;
                    dl[di] = x + estore * efb;
                    if (++di >= dl.Length) di = 0;
                    return d;
                }

                if (pre != null)
                {
                    float y = pre[pi]; pre[pi] = x;
                    if (++pi >= pre.Length) pi = 0;
                    x = y;
                }

                float sum = 0;
                for (int i = 0; i < cb.Length; i++)
                {
                    var buf = cb[i]; int k = ci[i];
                    float o = buf[k];
                    cstore[i] = o * (1 - damp) + cstore[i] * damp;
                    buf[k] = x * cin[i] + cstore[i] * cfb[i];
                    if (++k >= buf.Length) k = 0;
                    ci[i] = k;
                    sum += o;
                }
                for (int i = 0; i < ab.Length; i++)
                {
                    var buf = ab[i]; int k = ai[i];
                    float bo = buf[k];
                    float o = -sum + bo;
                    buf[k] = sum + bo * 0.5f;
                    if (++k >= buf.Length) k = 0;
                    ai[i] = k;
                    sum = o;
                }
                return sum;
            }
        }

        /// <summary>
        /// Applies the preset to frames [start, end). The effect keeps ringing until outEnd (the tail); after 'end'
        /// the original audio (if any) is left as it is and only the tail is added to it.
        /// Returns the peak level of the processed range (1.0 = full scale).
        /// </summary>
        public static float Process(AudioBuffer b, int start, int end, int outEnd, ReverbPreset p, double mixPercent)
        {
            int ch = b.Channels;
            float[] data = b.Samples;
            int rate = b.SampleRate;
            double mix = Math.Max(0, Math.Min(100, mixPercent)) / 100.0;
            float dry = (float)(1.0 - mix);
            float wetG = (float)(mix * 1.8);

            int nEng = ch == 1 ? 1 : 2;
            var eng = new Engine[nEng];
            for (int i = 0; i < nEng; i++) eng[i] = new Engine(p, rate, i);

            outEnd = Math.Min(outEnd, b.TotalFrames);
            float peak = 0;
            for (int f = start; f < outEnd; f++)
            {
                int bi = f * ch;
                float x = 0;
                bool inside = f < end;
                if (inside)
                {
                    for (int c = 0; c < ch; c++) x += data[bi + c];
                    x /= ch;
                }
                float w0 = eng[0].Process(x);
                float w1 = nEng > 1 ? eng[1].Process(x) : 0f;
                for (int c = 0; c < ch; c++)
                {
                    float w = (c & 1) == 0 ? w0 : w1;
                    float d = inside ? data[bi + c] * dry : data[bi + c];
                    float v = d + w * wetG;
                    data[bi + c] = v;
                    float a = Math.Abs(v);
                    if (a > peak) peak = a;
                }
            }
            return peak;
        }
    }
}
