using System;

namespace PS2AudioStudio.Audio
{
    /// <summary>
    /// Interleaved float audio (-1..1) plus loop metadata.
    /// ALL frame ranges in this class are [start, end) -> END IS EXCLUSIVE.
    /// LoopEnd is exclusive too (the loop plays frames LoopStart .. LoopEnd-1, then jumps back).
    /// LoopStart/LoopEnd are only meaningful when LoopEnabled == true.
    /// </summary>
    public class AudioBuffer
    {
        public float[] Samples { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int BitsPerSample { get; set; } = 16;

        public int LoopStart { get; set; } = -1;
        public int LoopEnd { get; set; } = -1;
        public bool LoopEnabled { get; set; } = false;

        public int TotalFrames => Channels > 0 ? Samples.Length / Channels : 0;
        public double DurationSeconds => SampleRate > 0 ? (double)TotalFrames / SampleRate : 0;

        public AudioBuffer() { Samples = Array.Empty<float>(); SampleRate = 44100; Channels = 1; }

        public AudioBuffer(float[] samples, int sampleRate, int channels)
        {
            Samples = samples ?? Array.Empty<float>();
            SampleRate = sampleRate;
            Channels = Math.Max(1, channels);
        }

        public AudioBuffer(int sampleRate, int channels, int frames)
        {
            SampleRate = sampleRate;
            Channels = Math.Max(1, channels);
            Samples = new float[Math.Max(0, frames) * Channels];
        }

        public AudioBuffer Clone()
        {
            return new AudioBuffer
            {
                Samples = (float[])Samples.Clone(),
                SampleRate = SampleRate,
                Channels = Channels,
                BitsPerSample = BitsPerSample,
                LoopStart = LoopStart,
                LoopEnd = LoopEnd,
                LoopEnabled = LoopEnabled
            };
        }

        // ---------------------------------------------------------------- PCM helpers

        public static AudioBuffer FromPcm16(byte[] pcm, int sampleRate, int channels)
        {
            int frameCount = pcm.Length / (2 * channels);
            var buf = new AudioBuffer(sampleRate, channels, frameCount);
            for (int i = 0; i < frameCount * channels; i++)
                buf.Samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
            return buf;
        }

        public byte[] ToPcm16()
        {
            byte[] pcm = new byte[Samples.Length * 2];
            for (int i = 0; i < Samples.Length; i++)
            {
                float c = Samples[i];
                if (c > 1f) c = 1f; else if (c < -1f) c = -1f;
                short s = (short)Math.Round(c * 32767f);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return pcm;
        }

        // ---------------------------------------------------------------- range helpers

        private void ClampRange(ref int start, ref int end)
        {
            int total = TotalFrames;
            if (start < 0) start = 0;
            if (end > total) end = total;
            if (end < start) end = start;
        }

        public AudioBuffer GetRegion(int start, int end)
        {
            ClampRange(ref start, ref end);
            int count = end - start;
            var r = new AudioBuffer(SampleRate, Channels, count);
            Array.Copy(Samples, start * Channels, r.Samples, 0, count * Channels);
            return r;
        }

        public void DeleteRegion(int start, int end)
        {
            ClampRange(ref start, ref end);
            int count = end - start;
            if (count <= 0) return;

            float[] ns = new float[Samples.Length - count * Channels];
            Array.Copy(Samples, 0, ns, 0, start * Channels);
            Array.Copy(Samples, end * Channels, ns, start * Channels, Samples.Length - end * Channels);
            Samples = ns;

            if (LoopEnabled)
            {
                LoopStart = ShiftAfterDelete(LoopStart, start, end);
                LoopEnd = ShiftAfterDelete(LoopEnd, start, end);
            }
            ClampLoopPoints();
        }

        private static int ShiftAfterDelete(int p, int s, int e)
        {
            if (p >= e) return p - (e - s);
            if (p > s) return s;
            return p;
        }

        public AudioBuffer CutRegion(int start, int end)
        {
            var copy = GetRegion(start, end);
            DeleteRegion(start, end);
            return copy;
        }

        /// <summary>Insert other at frame. Converts channel count / sample rate of 'other' if needed.</summary>
        public void InsertAt(int frame, AudioBuffer other)
        {
            if (other == null || other.Samples.Length == 0) return;
            if (other.SampleRate != SampleRate) other = other.Resample(SampleRate);
            if (other.Channels != Channels) other = other.ToChannels(Channels);
            if (frame < 0) frame = 0;
            if (frame > TotalFrames) frame = TotalFrames;

            int ins = frame * Channels;
            float[] ns = new float[Samples.Length + other.Samples.Length];
            Array.Copy(Samples, 0, ns, 0, ins);
            Array.Copy(other.Samples, 0, ns, ins, other.Samples.Length);
            Array.Copy(Samples, ins, ns, ins + other.Samples.Length, Samples.Length - ins);
            Samples = ns;

            int n = other.TotalFrames;
            if (LoopEnabled)
            {
                if (LoopStart >= frame) LoopStart += n;
                if (LoopEnd > frame) LoopEnd += n;
            }
            ClampLoopPoints();
        }

        // ---------------------------------------------------------------- effects

        public void FadeIn(int start, int end)
        {
            ClampRange(ref start, ref end);
            int len = end - start;
            if (len <= 1) return;
            for (int f = 0; f < len; f++)
            {
                float g = (float)f / (len - 1);
                for (int c = 0; c < Channels; c++) Samples[(start + f) * Channels + c] *= g;
            }
        }

        public void FadeOut(int start, int end)
        {
            ClampRange(ref start, ref end);
            int len = end - start;
            if (len <= 1) return;
            for (int f = 0; f < len; f++)
            {
                float g = 1f - (float)f / (len - 1);
                for (int c = 0; c < Channels; c++) Samples[(start + f) * Channels + c] *= g;
            }
        }

        public void Amplify(float gain, int start, int end)
        {
            ClampRange(ref start, ref end);
            for (int i = start * Channels; i < end * Channels; i++) Samples[i] *= gain;
        }

        public void Normalize(float targetPeak = 1f)
        {
            float peak = 0;
            foreach (float s in Samples) { float a = Math.Abs(s); if (a > peak) peak = a; }
            if (peak < 0.00001f) return;
            float g = targetPeak / peak;
            for (int i = 0; i < Samples.Length; i++) Samples[i] *= g;
        }

        public void Silence(int start, int end)
        {
            ClampRange(ref start, ref end);
            Array.Clear(Samples, start * Channels, (end - start) * Channels);
        }

        public void Invert(int start, int end)
        {
            ClampRange(ref start, ref end);
            for (int i = start * Channels; i < end * Channels; i++) Samples[i] = -Samples[i];
        }

        public void Reverse(int start, int end)
        {
            ClampRange(ref start, ref end);
            int len = end - start;
            for (int f = 0; f < len / 2; f++)
            {
                int a = start + f, b = end - 1 - f;
                for (int c = 0; c < Channels; c++)
                {
                    float t = Samples[a * Channels + c];
                    Samples[a * Channels + c] = Samples[b * Channels + c];
                    Samples[b * Channels + c] = t;
                }
            }
        }

        public void Reverse() => Reverse(0, TotalFrames);

        // ---------------------------------------------------------------- conversion

        public AudioBuffer ToChannels(int target)
        {
            if (target == Channels) return Clone();
            int frames = TotalFrames;
            var r = new AudioBuffer(SampleRate, target, frames);
            for (int f = 0; f < frames; f++)
            {
                if (target == 1)
                {
                    float sum = 0;
                    for (int c = 0; c < Channels; c++) sum += Samples[f * Channels + c];
                    r.Samples[f] = sum / Channels;
                }
                else
                {
                    for (int c = 0; c < target; c++)
                        r.Samples[f * target + c] = Samples[f * Channels + Math.Min(c, Channels - 1)];
                }
            }
            r.CopyLoopFrom(this, 1.0);
            return r;
        }

        public float[] MixToMono()
        {
            if (Channels == 1) return Samples;
            int frames = TotalFrames;
            var m = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < Channels; c++) sum += Samples[f * Channels + c];
                m[f] = sum / Channels;
            }
            return m;
        }

        public float[] GetChannel(int ch)
        {
            int frames = TotalFrames;
            var m = new float[frames];
            for (int f = 0; f < frames; f++) m[f] = Samples[f * Channels + ch];
            return m;
        }

        private void CopyLoopFrom(AudioBuffer src, double scale)
        {
            LoopEnabled = src.LoopEnabled;
            if (src.LoopEnabled)
            {
                LoopStart = (int)Math.Round(src.LoopStart * scale);
                LoopEnd = (int)Math.Round(src.LoopEnd * scale);
                ClampLoopPoints();
            }
            else { LoopStart = src.LoopStart < 0 ? -1 : (int)Math.Round(src.LoopStart * scale); LoopEnd = src.LoopEnd < 0 ? -1 : (int)Math.Round(src.LoopEnd * scale); }
        }

        /// <summary>Band-limited (windowed sinc) resampler. Loop points are scaled.</summary>
        public AudioBuffer Resample(int targetRate)
        {
            int tf = TotalFrames;
            if (targetRate == SampleRate || tf == 0) return Clone();

            double ratio = (double)SampleRate / targetRate;           // input frames per output frame
            int nOut = (int)Math.Max(1, Math.Round(tf / ratio));
            var r = new AudioBuffer(targetRate, Channels, nOut);

            double cutoff = Math.Min(1.0, 1.0 / ratio) * 0.95;        // fraction of input Nyquist
            int half = (int)Math.Ceiling(8.0 / cutoff);
            double[] w = new double[half * 2 + 2];

            for (int f = 0; f < nOut; f++)
            {
                double center = f * ratio;
                int i0 = (int)Math.Floor(center) - half + 1;
                int i1 = (int)Math.Floor(center) + half;
                double wsum = 0;
                int wc = 0;
                for (int i = i0; i <= i1; i++, wc++)
                {
                    double t = i - center;
                    double sinc = Math.Abs(t) < 1e-9 ? 1.0 : Math.Sin(Math.PI * cutoff * t) / (Math.PI * cutoff * t);
                    double win = 0.5 + 0.5 * Math.Cos(Math.PI * t / half);
                    double v = (Math.Abs(t) >= half ? 0 : sinc * win) * cutoff;
                    w[wc] = v;
                    wsum += v;
                }
                if (Math.Abs(wsum) < 1e-12) wsum = 1;
                for (int c = 0; c < Channels; c++)
                {
                    double acc = 0;
                    wc = 0;
                    for (int i = i0; i <= i1; i++, wc++)
                    {
                        int ci = i < 0 ? 0 : (i >= tf ? tf - 1 : i);
                        acc += Samples[ci * Channels + c] * w[wc];
                    }
                    r.Samples[f * Channels + c] = (float)(acc / wsum);
                }
            }
            r.CopyLoopFrom(this, 1.0 / ratio);
            return r;
        }

        // ---------------------------------------------------------------- loop helpers

        public void ClampLoopPoints()
        {
            int total = TotalFrames;
            if (!LoopEnabled) return;
            if (total <= 1) { LoopEnabled = false; return; }
            if (LoopStart < 0) LoopStart = 0;
            if (LoopStart > total - 1) LoopStart = total - 1;
            if (LoopEnd <= LoopStart) LoopEnd = Math.Min(total, LoopStart + 1);
            if (LoopEnd > total) LoopEnd = total;
        }

        /// <summary>Snap loop points to a block size (28 for VAG, 32 for ADX). Start rounds down, end rounds up.</summary>
        public void SnapLoop(int block)
        {
            if (!LoopEnabled || block <= 1) return;
            int total = TotalFrames;
            LoopStart = (LoopStart / block) * block;
            int e = ((LoopEnd + block - 1) / block) * block;
            if (e > total) e = (total / block) * block;     // never go beyond real data
            if (e <= LoopStart) e = Math.Min(total, LoopStart + block);
            LoopEnd = e;
            ClampLoopPoints();
        }
    }
}
