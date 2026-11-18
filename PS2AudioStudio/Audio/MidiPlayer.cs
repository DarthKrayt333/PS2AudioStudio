using System;
using System.Diagnostics;
using System.Threading;
using NAudio.Midi;
using PS2AudioStudio.Archives;

namespace PS2AudioStudio.Audio
{
    /// <summary>Plays an SqSong (from an SQ or a .mid) through a Windows MIDI output device (default: Microsoft GS Wavetable Synth).</summary>
    public sealed class MidiPlayer : IDisposable
    {
        private MidiOut _out;
        private Thread _th;
        private volatile bool _stop, _paused, _seeked;
        private readonly object _lk = new object();
        private readonly Stopwatch _sw = new Stopwatch();
        private double _base;
        private double _dur;

        public int DeviceNumber = 0;
        public volatile bool Loop;

        public static int DeviceCount { get { return MidiOut.NumberOfDevices; } }
        public bool IsActive { get { var t = _th; return t != null && t.IsAlive; } }
        public bool IsPaused { get { return IsActive && _paused; } }
        public bool IsPlaying { get { return IsActive && !_paused; } }
        public double Duration { get { return _dur; } }

        public double Position
        {
            get { lock (_lk) return _base + (_sw.IsRunning ? _sw.Elapsed.TotalSeconds : 0); }
        }

        public void Play(SqSong song, double startSeconds = 0)
        {
            Stop();
            if (song == null || song.Events.Count == 0) return;
            if (MidiOut.NumberOfDevices == 0)
                throw new InvalidOperationException("No MIDI output device found (Windows normally provides 'Microsoft GS Wavetable Synth').");

            _dur = song.DurationSeconds;
            var mo = new MidiOut(DeviceNumber);
            _out = mo;
            _stop = false; _paused = false; _seeked = true;     // _seeked makes the thread chase the state up to the start position
            lock (_lk) { _base = Math.Max(0, startSeconds); _sw.Restart(); }
            _th = new Thread(() => Run(song, mo)) { IsBackground = true, Name = "MidiPlayer" };
            _th.Start();
        }

        public void Pause()
        {
            if (!IsPlaying) return;
            lock (_lk) { _base += _sw.Elapsed.TotalSeconds; _sw.Reset(); }
            _paused = true;
            AllOff(_out);
        }

        public void Resume()
        {
            if (!IsPaused) return;
            lock (_lk) { _sw.Restart(); }
            _paused = false;
        }

        public void TogglePause() { if (IsPaused) Resume(); else Pause(); }

        public void Seek(double seconds)
        {
            if (!IsActive) return;
            lock (_lk) { _base = Math.Max(0, Math.Min(_dur, seconds)); if (_sw.IsRunning) _sw.Restart(); }
            _seeked = true;
        }

        public void Stop()
        {
            _stop = true;
            var t = _th;
            if (t != null && t.IsAlive && Thread.CurrentThread != t) t.Join(500);
            _th = null;
            var mo = _out; _out = null;
            if (mo != null) { try { AllOff(mo); mo.Dispose(); } catch { } }
            lock (_lk) { _base = 0; _sw.Reset(); }
            _paused = false;
        }

        public void Dispose() { Stop(); }

        // ------------------------------------------------------------------ playback thread
        private void Run(SqSong song, MidiOut mo)
        {
            try
            {
                double[] secs = song.EventSeconds();
                var ev = song.Events;
                int n = ev.Count;
                double endAll = secs[n - 1] + 1.0;
                int i = 0;

                while (!_stop)
                {
                    if (_paused) { Thread.Sleep(10); continue; }

                    if (_seeked)
                    {
                        _seeked = false;
                        AllOff(mo);
                        i = IndexAt(secs, Position);
                        Chase(mo, ev, i);
                    }

                    double now = Position;

                    // loop range: the song's CC#99 markers, else the whole song
                    bool looping = Loop;
                    double ls = 0, le = secs[n - 1];
                    int startIdx = -1;
                    if (looping && song.TryGetLoopRange(out int sIdx, out int eIdx))
                    {
                        startIdx = sIdx;
                        ls = sIdx < 0 ? 0 : secs[sIdx];
                        le = eIdx >= n ? secs[n - 1] : secs[eIdx];
                    }
                    if (le <= ls + 0.05) looping = false;

                    double limit = looping ? Math.Min(now, le) : now;
                    while (i < n && secs[i] <= limit) { Send(mo, ev[i]); i++; }

                    if (looping && now >= le)
                    {
                        AllOff(mo);
                        lock (_lk) { _base = ls; _sw.Restart(); }
                        i = IndexAt(secs, ls);
                        if (startIdx >= 0 && i <= startIdx) i = startIdx + 1;
                        Chase(mo, ev, i);
                        continue;
                    }

                    if (i >= n && now >= endAll) break;
                    Thread.Sleep(1);
                }
            }
            catch { }
            finally { try { AllOff(mo); } catch { } }
        }

        private static int IndexAt(double[] t, double s)
        {
            int lo = 0, hi = t.Length;
            while (lo < hi) { int m = (lo + hi) / 2; if (t[m] < s) lo = m + 1; else hi = m; }
            return lo;
        }

        /// <summary>Re-sends program / controller / pitch-bend state of everything before event index 'upTo' (after a seek or loop jump).</summary>
        private static void Chase(MidiOut mo, System.Collections.Generic.List<SqEvent> ev, int upTo)
        {
            for (int k = 0; k < upTo && k < ev.Count; k++)
            {
                var e = ev[k];
                if (e.Type == SqEventType.Program || e.Type == SqEventType.Cc || e.Type == SqEventType.PitchBend) Send(mo, e);
            }
        }

        private static void Send(MidiOut mo, SqEvent e)
        {
            int ch = e.Channel & 15;
            int msg;
            switch (e.Type)
            {
                case SqEventType.NoteOn:
                    msg = (0x90 | ch) | ((e.A & 0x7F) << 8) | (Math.Max(1, e.B & 0x7F) << 16); break;
                case SqEventType.NoteOff:
                    msg = (0x80 | ch) | ((e.A & 0x7F) << 8); break;
                case SqEventType.Cc:
                    if (e.A == 99 || e.A == 98 || e.A == 0 || e.A == 32) return;     // loop markers / PS2 bank select mean nothing to a GM synth
                    msg = (0xB0 | ch) | ((e.A & 0x7F) << 8) | ((e.B & 0x7F) << 16); break;
                case SqEventType.Program:
                    msg = (0xC0 | ch) | ((e.A & 0x7F) << 8); break;
                case SqEventType.PitchBend:
                    msg = (0xE0 | ch) | ((e.A & 0x7F) << 8) | ((e.B & 0x7F) << 16); break;
                default: return;
            }
            try { mo.Send(msg); } catch { }
        }

        private static void AllOff(MidiOut mo)
        {
            if (mo == null) return;
            for (int ch = 0; ch < 16; ch++)
            {
                try
                {
                    mo.Send((0xB0 | ch) | (64 << 8));            // sustain off
                    mo.Send((0xB0 | ch) | (123 << 8));           // all notes off
                    mo.Send((0xB0 | ch) | (120 << 8));           // all sound off
                }
                catch { }
            }
        }
    }
}
