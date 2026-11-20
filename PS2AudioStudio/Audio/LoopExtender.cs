using System;

namespace PS2AudioStudio.Audio
{
    /// <summary>Turns a loopable render (intro + loop body) into a finished song: intro, loop body repeated, cut at lengthSeconds, then a linear fade of fadeSeconds (like the PSF tags length/fade).</summary>
    public static class LoopExtender
    {
        public static AudioBuffer Extend(AudioBuffer src, double lengthSeconds, double fadeSeconds)
        {
            if (src == null) throw new ArgumentNullException("src");
            if (!src.LoopEnabled || src.LoopEnd <= src.LoopStart) return src;
            if (lengthSeconds < 1) lengthSeconds = 1;
            if (fadeSeconds < 0) fadeSeconds = 0;
            if (lengthSeconds + fadeSeconds > 1200) throw new ArgumentException("Song length is limited to 20 minutes.");

            int rate = src.SampleRate;
            int frames = src.TotalFrames;
            int ch = frames > 0 ? src.Samples.Length / frames : 2;
            int ls = src.LoopStart;
            int le = Math.Min(src.LoopEnd, frames);
            int loopLen = le - ls;
            if (loopLen < 1) return src;

            long lenF = (long)(lengthSeconds * rate);
            long fadeF = (long)(fadeSeconds * rate);
            long total = lenF + fadeF;

            var outBuf = new AudioBuffer(rate, ch, (int)total);
            for (long i = 0; i < total; i++)
            {
                long s = i < le ? i : ls + (i - ls) % loopLen;
                float g = 1f;
                if (i >= lenF && fadeF > 0) g = 1f - (float)(i - lenF) / fadeF;
                if (g < 0f) g = 0f;
                for (int c = 0; c < ch; c++)
                    outBuf.Samples[i * ch + c] = src.Samples[s * ch + c] * g;
            }
            return outBuf;
        }
    }
}
