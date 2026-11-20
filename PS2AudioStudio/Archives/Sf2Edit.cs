using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PS2AudioStudio.Audio;

namespace PS2AudioStudio.Archives
{
    public static class Sf2Edit
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);

        public static AudioBuffer ToBuffer(Sf2Sample s)
        {
            var f = new float[s.Data.Length];
            for (int i = 0; i < f.Length; i++) f[i] = s.Data[i] / 32768f;
            var b = new AudioBuffer(f, s.SampleRate, 1);
            if (s.HasLoopPoints) { b.LoopEnabled = true; b.LoopStart = s.LoopStart; b.LoopEnd = s.LoopEnd; }
            return b;
        }

        /// <summary>Replaces the audio of one SF2 sample. The file's own sample rate is used.
        /// Root key, tuning and every zone that uses the sample stay untouched.</summary>
        public static void Replace(Sf2Sample s, AudioBuffer src)
        {
            if (src.SampleRate > 50000) src = AudioIO.Resample(src, 48000);   // SF2 allows 400-50000 Hz
            int ch = Math.Max(1, src.Channels);
            int frames = src.Samples.Length / ch;
            int n = Math.Max(frames, 48);                                      // SF2 minimum
            var d = new short[n];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++) sum += src.Samples[i * ch + c];
                int v = (int)Math.Round(sum / ch * 32767f);
                d[i] = (short)Math.Max(-32768, Math.Min(32767, v));
            }
            s.Data = d;
            s.SampleRate = Math.Max(400, src.SampleRate);
            s.Rom = false;
            if (src.LoopEnabled && src.LoopEnd > src.LoopStart + 1 && src.LoopEnd <= frames)
            { s.LoopStart = src.LoopStart; s.LoopEnd = src.LoopEnd; }
            else { s.LoopStart = 0; s.LoopEnd = 0; }
        }

        /// <summary>Replace the selected samples by file-name order. Fewer files: only that many are replaced.
        /// More files: the extra ones are appended as new samples. Zones and presets are not touched.</summary>
        public static void ReplaceByNameOrder(Sf2File sf, IList<int> selected, IList<string> files,
                                              out int replaced, out int appended)
        {
            var sorted = files.OrderBy(x => Path.GetFileName(x), Comparer<string>.Create((a, b) => StrCmpLogicalW(a, b))).ToList();
            var idx = selected.OrderBy(x => x).ToList();
            int n = Math.Min(idx.Count, sorted.Count);
            replaced = 0; appended = 0;
            for (int k = 0; k < n; k++)
            {
                Replace(sf.Samples[idx[k]], AudioIO.Load(sorted[k]));
                replaced++;
            }
            for (int k = n; k < sorted.Count; k++)
            {
                string nm = Path.GetFileNameWithoutExtension(sorted[k]);
                var s = new Sf2Sample { Name = nm.Length > 19 ? nm.Substring(0, 19) : nm };
                Replace(s, AudioIO.Load(sorted[k]));
                sf.Samples.Add(s);
                appended++;
            }
        }
    }
}
