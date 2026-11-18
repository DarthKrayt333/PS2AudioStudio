using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using PS2AudioStudio.Archives;

namespace PS2AudioStudio.Audio
{
    /// <summary>What happened to every note of a render: which ones made no sound and why.</summary>
    public sealed class Psf2RenderReport
    {
        public int NotesTotal, NotesPlayed;
        public int NotesNoProgram, NotesNoSplit, NotesNoSet, NotesNoVelocity, NotesNoSample, NotesNoGain;
        public int VoicesCreated, VoicesStolen, PeakVoices, VoiceLimit;
        public int FlagLoopOverrides;          // samples looped by their ADPCM flags although the HD says one-shot
        public int EventsDroppedAfterLoop;
        public bool Looped;
        public readonly Dictionary<string, int> Details = new Dictionary<string, int>();
        public readonly SortedDictionary<int, int> UnhandledCc = new SortedDictionary<int, int>();

        internal void AddDetail(string s) { Details.TryGetValue(s, out int n); Details[s] = n + 1; }
        internal void CountCc(int cc) { UnhandledCc.TryGetValue(cc, out int n); UnhandledCc[cc] = n + 1; }

        public int NotesSilent { get { return NotesTotal - NotesPlayed; } }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Notes: {NotesTotal:N0} total, {NotesPlayed:N0} played, {NotesSilent:N0} made NO sound.");
            if (NotesNoProgram > 0) sb.AppendLine($"  - program not in the HD: {NotesNoProgram:N0}");
            if (NotesNoSplit > 0) sb.AppendLine($"  - no split covers the key: {NotesNoSplit:N0}");
            if (NotesNoSet > 0) sb.AppendLine($"  - split points to a missing sample set: {NotesNoSet:N0}");
            if (NotesNoVelocity > 0) sb.AppendLine($"  - no sample for this velocity: {NotesNoVelocity:N0}");
            if (NotesNoSample > 0) sb.AppendLine($"  - sample empty / cannot decode: {NotesNoSample:N0}");
            if (NotesNoGain > 0) sb.AppendLine($"  - volume resolved to 0: {NotesNoGain:N0}");
            sb.AppendLine($"Voices: {VoicesCreated:N0} created, peak {PeakVoices} at once, {VoicesStolen:N0} stolen (limit {VoiceLimit}).");
            sb.AppendLine($"Samples looped by their ADPCM loop flags although the HD says one-shot: {FlagLoopOverrides}");
            if (Looped && EventsDroppedAfterLoop > 0)
                sb.AppendLine($"Events after the loop-end marker not rendered: {EventsDroppedAfterLoop:N0} (a looped render stops at the loop end).");
            if (Details.Count > 0)
            {
                sb.AppendLine("Details:");
                foreach (var kv in Details.OrderByDescending(x => x.Value).Take(20))
                    sb.AppendLine($"  {kv.Key}  x{kv.Value:N0}");
            }
            if (UnhandledCc.Count > 0)
                sb.AppendLine("Controllers in the song that the renderer ignores: " + string.Join(", ", UnhandledCc.Select(kv => $"CC{kv.Key} x{kv.Value}")));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Offline sampler: plays an SQ / MIDI song through the instruments of an HD+BD bank
    /// (program -> split (key range) -> sample set (velocity range) -> sample param -> VAG sample)
    /// and returns the finished stereo audio. SPU ADSR envelopes are emulated per output sample (48 kHz = SPU2 rate).
    /// </summary>
    public static class Psf2Renderer
    {
        public const int OutRate = 48000;

        /// <summary>The SPU2 loops by the loop flags inside the ADPCM data, not by the HD attribute byte. Set false to get the old behaviour.</summary>
        public static bool LoopFromAdpcmFlags = true;
        public static int MaxVoices = 160;

        // ------------------------------------------------------------------ SPU envelope
        private sealed class Env
        {
            private int level, phase, counter;       // phase 0 A, 1 D, 2 S, 3 R
            private bool atkExp, susExp, susDec, relExp;
            private int atkShift, atkStep, decShift, susLevel, susShift, susStep, relShift;
            public bool Dead;
            public int Level => level;

            public Env(ushort a1, ushort a2)
            {
                atkExp = (a1 & 0x8000) != 0;
                atkShift = (a1 >> 10) & 0x1F;
                atkStep = (a1 >> 8) & 3;
                decShift = (a1 >> 4) & 0xF;
                susLevel = ((a1 & 0xF) + 1) * 0x800;
                susExp = (a2 & 0x8000) != 0;
                susDec = (a2 & 0x4000) != 0;
                susShift = (a2 >> 8) & 0x1F;
                susStep = (a2 >> 6) & 3;
                relExp = (a2 & 0x20) != 0;
                relShift = a2 & 0x1F;
                level = 0; phase = 0; counter = 0;
            }

            public void Release() { if (phase != 3) { phase = 3; counter = 0; } }
            public bool Releasing => phase == 3;

            public void Tick()
            {
                int shift, step; bool exp, dec;
                switch (phase)
                {
                    case 0: shift = atkShift; step = 7 - atkStep; exp = atkExp; dec = false; break;
                    case 1: shift = decShift; step = -8; exp = true; dec = true; break;
                    case 2: shift = susShift; dec = susDec; step = dec ? -8 + susStep : 7 - susStep; exp = susExp; break;
                    default: shift = relShift; step = -8; exp = relExp; dec = true; break;
                }
                int cycles = 1 << Math.Max(0, shift - 11);
                int astep = step << Math.Max(0, 11 - shift);
                if (exp && !dec && level > 0x6000) cycles *= 4;
                if (exp && dec) astep = (astep * level) >> 15;

                if (++counter >= cycles)
                {
                    counter = 0;
                    level += astep;
                    if (level > 0x7FFF) level = 0x7FFF;
                    if (level < 0) level = 0;
                }

                if (phase == 0 && level >= 0x7FFF) phase = 1;
                else if (phase == 1 && level <= susLevel) phase = 2;
                else if (phase == 3 && level <= 0) Dead = true;
            }
        }

        // ------------------------------------------------------------------ voice / channel state
        private sealed class Voice
        {
            public float[] Data;
            public int LoopStart, LoopEnd;
            public bool Loop;
            public double Pos, BaseStep;
            public float Gain;
            public int PanOffset;          // sum of sample/program/split pan offsets (-63..63)
            public int Channel, Key;
            public Env Env;
            public bool Held;              // note-off received while sustain pedal down
            public bool KeyUp;
            public bool PlayOnce;          // sustained song end: play the sample to its loop end once, then stop
            public long ReleaseAge;
        }

        private sealed class Chan
        {
            public int Bank, Program, Vol = 127, Expr = 127, Pan = 64, Bend = 8192;
            public int BendRange = 2;                 // semitones (RPN 0,0)
            public int RpnMsb = 127, RpnLsb = 127;    // 127/127 = "null" RPN
            public bool Sustain;
            public float Gain => (Vol / 127f) * (Expr / 127f);
        }

        private sealed class RSample
        {
            public float[] Data;
            public bool Loop;
            public int LoopStart, LoopEnd;
            public double Rate;
        }

        private sealed class Ctx
        {
            public Psf2Bank Bank;
            public List<Voice> Voices;
            public Psf2RenderReport Report;
            public readonly Dictionary<int, RSample> Cache = new Dictionary<int, RSample>();
        }

        private static int PanConv(int v)
        {
            if (v > 0x7F) return 0x40 - (v - 0x7F);
            return v - 0x40;
        }

        /// <summary>Number of notes that are still on (no note-off) after the very last event of the song.</summary>
        private static int CountHeldAtEnd(SqSong song)
        {
            var held = new HashSet<int>();
            foreach (var e in song.Events)
            {
                int ch = e.Channel & 15;
                switch (e.Type)
                {
                    case SqEventType.NoteOn: held.Add(ch * 128 + (e.A & 127)); break;
                    case SqEventType.NoteOff: held.Remove(ch * 128 + (e.A & 127)); break;
                    case SqEventType.Cc:
                        if (e.A == 120 || e.A == 123) held.RemoveWhere(k => k / 128 == ch);
                        break;
                }
            }
            return held.Count;
        }

        // ------------------------------------------------------------------ render
        public static AudioBuffer Render(Psf2Bank bank, SqSong song, IProgress<int> progress = null,
                                         CancellationToken ct = default, bool honorLoop = true,
                                         Psf2RenderReport report = null)
        {
            if (bank == null || !bank.Loaded) throw new InvalidOperationException("Load an HD/BD bank first.");
            if (song == null || song.Events.Count == 0) throw new InvalidOperationException("The song has no events.");
            if (report == null) report = new Psf2RenderReport();
            report.VoiceLimit = MaxVoices;

            double[] secs = song.EventSeconds();
            int evCount = song.Events.Count;
            long maxFrames = (long)OutRate * 60 * 12;

            // ---- notes that are never switched off (one-VAG mini hacks): the game keeps them ringing, so do we
            int heldAtEnd = CountHeldAtEnd(song);
            bool sustained = heldAtEnd > 0;
            if (sustained)
                report.AddDetail(heldAtEnd + " note(s) have no note-off: they play out their whole sample once (like the game)");

            // ---- event timeline: normal, or "intro + loop body + one more lap of the body"
            var evIndex = new List<int>(evCount * 2);
            var evFrames = new List<long>(evCount * 2);
            bool looped = false;
            long loopStartF = 0, loopLenF = 0;

            if (!sustained && honorLoop && song.TryGetLoopRange(out int sIdx, out int eIdx))
            {
                int endI = Math.Min(eIdx, evCount);
                double sSec = sIdx < 0 ? 0.0 : secs[sIdx];
                double eSec = eIdx >= evCount ? secs[evCount - 1] : secs[eIdx];
                long sF = (long)(sSec * OutRate), eF = (long)(eSec * OutRate);
                if (eF - sF > OutRate / 4 && eF + (eF - sF) < maxFrames)
                {
                    looped = true;
                    loopStartF = sF;
                    loopLenF = eF - sF;
                    for (int i = 0; i < endI; i++)                       // lap 1: everything before the loop end
                    { evIndex.Add(i); evFrames.Add((long)(secs[i] * OutRate)); }
                    for (int i = sIdx + 1; i < endI; i++)                // lap 2: loop body again, starting at the loop-end time
                    { evIndex.Add(i); evFrames.Add((long)((secs[i] - sSec + eSec) * OutRate)); }
                    report.EventsDroppedAfterLoop = Math.Max(0, evCount - endI);
                }
            }
            if (!looped)
            {
                for (int i = 0; i < evCount; i++) { evIndex.Add(i); evFrames.Add((long)(secs[i] * OutRate)); }
            }
            report.Looped = looped;

            long endFrame, lastEventFrame;
            if (looped)
            {
                endFrame = loopStartF + 2 * loopLenF;
                lastEventFrame = endFrame;
            }
            else
            {
                double lastSec = secs[evCount - 1];
                lastEventFrame = Math.Min(maxFrames, (long)(lastSec * OutRate));
                endFrame = (long)(lastSec * OutRate) + OutRate * (sustained ? 10 : 6);   // sustained: grows while notes still play
                if (endFrame > maxFrames) endFrame = maxFrames;
            }

            float[] L = new float[endFrame], R = new float[endFrame];
            var chans = new Chan[16];
            for (int i = 0; i < 16; i++) chans[i] = new Chan();
            var voices = new List<Voice>(128);
            var ctx = new Ctx { Bank = bank, Voices = voices, Report = report };

            int ei = 0;
            bool endReleased = looped;          // looped renders are cut, not released
            int lastPct = -1;

            for (long f = 0; f < endFrame; f++)
            {
                if ((f & 0x3FFF) == 0)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException();
                    int pct = (int)(f * 100 / endFrame);
                    if (pct != lastPct) { lastPct = pct; progress?.Report(pct); }
                }

                // ---- sustained songs: keep the buffer growing while voices are still playing
                if (sustained && f >= endFrame - 1 && voices.Count > 0 && endFrame < maxFrames)
                {
                    endFrame = Math.Min(maxFrames, endFrame + OutRate * 10);
                    Array.Resize(ref L, (int)endFrame);
                    Array.Resize(ref R, (int)endFrame);
                }

                // ---- events
                while (ei < evIndex.Count && evFrames[ei] <= f)
                {
                    var e = song.Events[evIndex[ei++]];
                    int chIdx = e.Channel & 15;
                    var ch = chans[chIdx];
                    switch (e.Type)
                    {
                        case SqEventType.Program: ch.Program = e.A; break;
                        case SqEventType.Cc:
                            switch (e.A)
                            {
                                case 0: ch.Bank = e.B; break;
                                case 6: if (ch.RpnMsb == 0 && ch.RpnLsb == 0) ch.BendRange = Math.Max(1, (int)e.B); break;
                                case 7: ch.Vol = e.B; break;
                                case 10: ch.Pan = e.B; break;
                                case 11: ch.Expr = e.B; break;
                                case 64:
                                    {
                                        bool down = e.B >= 64;
                                        ch.Sustain = down;
                                        if (!down)
                                            foreach (var v in voices)
                                                if (v.Channel == chIdx && v.Held) { v.Held = false; v.Env.Release(); }
                                        break;
                                    }
                                case 100: ch.RpnLsb = e.B; break;
                                case 101: ch.RpnMsb = e.B; break;
                                case 120:
                                case 123:
                                    foreach (var v in voices) if (v.Channel == chIdx) v.Env.Release();
                                    break;
                                case 121: ch.Expr = 127; ch.Bend = 8192; ch.Sustain = false; break;
                                case 32: case 38: case 98: case 99: break;   // 99 = loop markers: handled through the timeline
                                default: report.CountCc(e.A); break;
                            }
                            break;
                        case SqEventType.PitchBend: ch.Bend = e.A | (e.B << 7); break;
                        case SqEventType.NoteOff:
                            foreach (var v in voices)
                                if (v.Channel == chIdx && v.Key == e.A && !v.KeyUp)
                                {
                                    v.KeyUp = true;
                                    if (ch.Sustain) v.Held = true; else v.Env.Release();
                                }
                            break;
                        case SqEventType.NoteOn:
                            NoteOn(ctx, chIdx, ch, e.A, e.B);
                            break;
                    }
                }

                // ---- after the last event (non-looped songs): release everything,
                //      except notes that were never switched off in a sustained song: they play their sample out once
                if (!endReleased && f >= lastEventFrame)
                {
                    endReleased = true;
                    foreach (var v in voices)
                    {
                        if (sustained && (!v.KeyUp || v.Held)) v.PlayOnce = true;
                        else v.Env.Release();
                    }
                }
                if (!looped && endReleased && voices.Count == 0 && f > lastEventFrame + 1000) { endFrame = f; break; }

                if (voices.Count > report.PeakVoices) report.PeakVoices = voices.Count;

                // ---- mix
                float l = 0, r = 0;
                for (int vi = voices.Count - 1; vi >= 0; vi--)
                {
                    var v = voices[vi];
                    var ch = chans[v.Channel];

                    v.Env.Tick();
                    if (v.Env.Releasing) { v.ReleaseAge++; if (v.ReleaseAge > OutRate * 12 || v.Env.Level < 8) v.Env.Dead = true; }
                    if (v.Env.Dead) { voices.RemoveAt(vi); continue; }

                    double bend = ((ch.Bend - 8192) / 8192.0) * ch.BendRange;
                    double step = bend == 0 ? v.BaseStep : v.BaseStep * Math.Pow(2.0, bend / 12.0);

                    int i0 = (int)v.Pos;
                    double fr = v.Pos - i0;
                    float[] d = v.Data;
                    float s0 = d[i0];
                    int i1 = i0 + 1;
                    if (v.Loop && i1 >= v.LoopEnd) i1 = v.LoopStart;
                    float s1 = i1 < d.Length ? d[i1] : 0f;
                    float smp = s0 + (float)fr * (s1 - s0);

                    float amp = smp * (v.Env.Level / 32767f) * v.Gain * ch.Gain;
                    int pan = 64 + v.PanOffset + (ch.Pan - 64);
                    if (pan < 0) pan = 0; else if (pan > 127) pan = 127;
                    double ang = pan / 127.0 * (Math.PI / 2);
                    l += amp * (float)Math.Cos(ang);
                    r += amp * (float)Math.Sin(ang);

                    v.Pos += step;
                    if (v.Loop && v.PlayOnce && v.Pos >= v.LoopEnd) v.Loop = false;   // sustained end: leave the loop, finish the data
                    if (v.Loop)
                    {
                        if (v.Pos >= v.LoopEnd) v.Pos -= (v.LoopEnd - v.LoopStart);
                    }
                    else if (v.Pos >= d.Length - 1) { voices.RemoveAt(vi); }
                }
                L[f] = l; R[f] = r;
            }

            progress?.Report(100);

            AudioBuffer outBuf;
            if (looped)
            {
                // output = intro (lap 1 up to loop start) + loop body taken from lap 2 (already contains the tails of lap 1)
                int total = (int)(loopStartF + loopLenF);
                outBuf = new AudioBuffer(OutRate, 2, total);
                for (int i = 0; i < loopStartF && i < L.Length; i++)
                {
                    outBuf.Samples[i * 2] = L[i];
                    outBuf.Samples[i * 2 + 1] = R[i];
                }
                for (int i = 0; i < loopLenF; i++)
                {
                    long src = loopStartF + loopLenF + i;
                    if (src >= L.Length) break;
                    long dst = loopStartF + i;
                    outBuf.Samples[dst * 2] = L[src];
                    outBuf.Samples[dst * 2 + 1] = R[src];
                }
                outBuf.LoopEnabled = true;
                outBuf.LoopStart = (int)loopStartF;
                outBuf.LoopEnd = total;
            }
            else
            {
                int frames = (int)Math.Min(endFrame, L.Length);
                int trimmed = frames;
                while (trimmed > 1 && Math.Abs(L[trimmed - 1]) < 1e-5f && Math.Abs(R[trimmed - 1]) < 1e-5f) trimmed--;
                frames = Math.Min(L.Length, trimmed + OutRate / 10);

                outBuf = new AudioBuffer(OutRate, 2, frames);
                for (int i = 0; i < frames; i++)
                {
                    outBuf.Samples[i * 2] = L[i];
                    outBuf.Samples[i * 2 + 1] = R[i];
                }
            }

            float peak = 0;
            for (int i = 0; i < outBuf.Samples.Length; i++)
            {
                float a = Math.Abs(outBuf.Samples[i]);
                if (a > peak) peak = a;
            }
            if (peak > 0.0005f)
            {
                float g = 0.9f / peak;          // always normalise the preview render to a comfortable level
                for (int i = 0; i < outBuf.Samples.Length; i++) outBuf.Samples[i] *= g;
            }
            return outBuf;
        }

        // ------------------------------------------------------------------ sample lookup (loop by ADPCM flags)
        private static RSample GetSample(Ctx c, int index)
        {
            if (c.Cache.TryGetValue(index, out RSample cached)) return cached;

            RSample rs = null;
            var s = c.Bank.Samples[index];
            var dec = s.Decoded;
            if (s.Valid && s.Raw != null && dec != null && dec.TotalFrames >= 2)
            {
                double rate = s.SampleRate > 0 ? s.SampleRate : dec.SampleRate;
                rs = new RSample
                {
                    Data = dec.Samples,
                    Rate = rate,
                    Loop = dec.LoopEnabled && dec.LoopEnd > dec.LoopStart,
                    LoopStart = dec.LoopStart,
                    LoopEnd = Math.Min(dec.LoopEnd, dec.Samples.Length)
                };

                if (LoopFromAdpcmFlags && s.Attribute == 0)
                {
                    AudioBuffer raw = null;
                    try { raw = VagCodec.DecodeAdpcm(s.Raw, 0, s.Raw.Length, (int)rate); } catch { }
                    if (raw != null && raw.LoopEnabled && raw.LoopEnd > raw.LoopStart && raw.TotalFrames >= 2)
                    {
                        rs.Data = raw.Samples;
                        rs.Loop = true;
                        rs.LoopStart = raw.LoopStart;
                        rs.LoopEnd = Math.Min(raw.LoopEnd, raw.Samples.Length);
                        c.Report.FlagLoopOverrides++;
                    }
                }
                if (!rs.Loop) { rs.LoopStart = 0; rs.LoopEnd = 0; }
            }
            c.Cache[index] = rs;
            return rs;
        }

        private static void StealVoice(Ctx c)
        {
            int idx = c.Voices.FindIndex(v => v.Env.Releasing);   // prefer a voice that is already fading out
            if (idx < 0) idx = 0;
            c.Voices.RemoveAt(idx);
            c.Report.VoicesStolen++;
        }

        private static void NoteOn(Ctx c, int chIdx, Chan ch, int key, int vel)
        {
            var bank = c.Bank; var rep = c.Report; var voices = c.Voices;
            rep.NotesTotal++;

            int progIdx = ch.Bank * 128 + ch.Program;
            Psf2Program prog;
            if (!bank.Programs.TryGetValue(progIdx, out prog))
                if (!bank.Programs.TryGetValue(ch.Program, out prog))
                {
                    rep.NotesNoProgram++;
                    rep.AddDetail($"program {ch.Program} (bank {ch.Bank}) is not in the HD (channel {chIdx + 1})");
                    return;
                }

            // retrigger: choke a still-held identical key on this channel
            foreach (var old in voices)
                if (old.Channel == chIdx && old.Key == key && !old.KeyUp) { old.KeyUp = true; old.Env.Release(); }

            bool anyKey = false, anySet = false, anyVel = false, anySmp = false;
            int made = 0;

            foreach (var split in prog.Splits)
            {
                if (key < split.RangeLow || key > split.RangeHigh) continue;
                anyKey = true;
                if (split.SampleSetIndex < 0 || split.SampleSetIndex >= bank.SampleSets.Length) continue;
                var set = bank.SampleSets[split.SampleSetIndex];
                if (set == null) continue;
                anySet = true;

                foreach (int spi in set.SampleParams)
                {
                    if (spi < 0 || spi >= bank.SampleParams.Length) continue;
                    var sp = bank.SampleParams[spi];
                    if (sp == null) continue;

                    int sl = set.VelLow, sh = set.VelHigh; if (sl == 0 && sh == 0) sh = 127;     // unset range = everything
                    int pl = sp.VelLow, ph = sp.VelHigh; if (pl == 0 && ph == 0) ph = 127;
                    int vLow = Math.Max(sl, pl);
                    int vHigh = Math.Min(sh, ph);
                    if (vLow <= vHigh && (vel < vLow || vel > vHigh)) continue;
                    anyVel = true;

                    if (sp.VagIndex < 0 || sp.VagIndex >= bank.Samples.Count) continue;
                    var rs = GetSample(c, sp.VagIndex);
                    if (rs == null) continue;
                    anySmp = true;

                    double semis = (key - sp.BaseNote) + split.Transpose + (split.Detune / 100.0);
                    double step = (rs.Rate / OutRate) * Math.Pow(2.0, semis / 12.0);

                    float gain = (prog.Volume / 127f) * (split.Volume / 127f) * (sp.Volume / 127f) * (vel / 127f) * 0.5f;
                    if (gain <= 0) continue;

                    var v = new Voice
                    {
                        Data = rs.Data,
                        Loop = rs.Loop,
                        LoopStart = rs.LoopStart,
                        LoopEnd = rs.LoopEnd,
                        BaseStep = step,
                        Gain = gain,
                        PanOffset = PanConv(sp.Panpot) + PanConv(prog.Panpot) + PanConv(split.Panpot),
                        Channel = chIdx,
                        Key = key,
                        Env = new Env(sp.Adsr1, sp.Adsr2)
                    };
                    voices.Add(v);
                    made++;
                    rep.VoicesCreated++;
                    if (voices.Count > MaxVoices) StealVoice(c);
                }
            }

            if (made > 0) { rep.NotesPlayed++; return; }

            if (!anyKey) { rep.NotesNoSplit++; rep.AddDetail($"program {progIdx}: no split covers key {key}"); }
            else if (!anySet) { rep.NotesNoSet++; rep.AddDetail($"program {progIdx}: split points to a missing sample set"); }
            else if (!anyVel) { rep.NotesNoVelocity++; rep.AddDetail($"program {progIdx}: nothing for velocity {vel} (key {key})"); }
            else if (!anySmp) { rep.NotesNoSample++; rep.AddDetail($"program {progIdx}: its sample is empty / undecodable"); }
            else rep.NotesNoGain++;
        }
    }
}
