using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PS2AudioStudio.Audio
{
    /// <summary>Streams an AudioBuffer to the sound card. Honours the buffer's loop (live) unless told to ignore it.</summary>
    public sealed class AudioPlayer : IDisposable
    {
        private sealed class LoopProvider : ISampleProvider
        {
            private readonly AudioBuffer _b;
            private readonly float[] _s;
            private readonly int _ch;
            private readonly int _end;
            private readonly bool _ignoreLoop;
            private volatile int _pos;
            public volatile float Volume = 1f;

            public WaveFormat WaveFormat { get; }
            public int Position => _pos;

            public LoopProvider(AudioBuffer b, int start, int end, bool ignoreLoop)
            {
                _b = b; _s = b.Samples; _ch = b.Channels;
                _pos = Math.Max(0, start);
                _end = Math.Min(end, b.TotalFrames);
                _ignoreLoop = ignoreLoop;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(b.SampleRate, b.Channels);
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int framesWanted = count / _ch;
                int written = 0;
                float vol = Volume;
                int maxFrames = _s.Length / _ch;

                while (written < framesWanted)
                {
                    // read the loop settings live, so dragging the flags while playing works
                    int ls = Math.Max(0, _b.LoopStart);
                    int le = Math.Min(_b.LoopEnd, maxFrames);
                    bool looping = !_ignoreLoop && _b.LoopEnabled && le > ls;

                    int limit;
                    if (looping)
                        limit = _pos < le ? le : maxFrames;     // started after the loop end: play to the end, then wrap
                    else
                        limit = Math.Min(_end, maxFrames);

                    if (_pos >= limit)
                    {
                        if (looping) { _pos = ls; continue; }   // instant jump back to the loop start
                        break;                                  // not looping: end of audio
                    }

                    int n = Math.Min(framesWanted - written, limit - _pos);
                    int src = _pos * _ch;
                    int dst = offset + written * _ch;
                    for (int i = 0; i < n * _ch; i++) buffer[dst + i] = _s[src + i] * vol;
                    _pos += n;
                    written += n;
                }
                return written * _ch;
            }
        }

        private WaveOutEvent _out;
        private LoopProvider _prov;
        private float _volume = 0.8f;

        public AudioBuffer Buffer { get; private set; }
        public bool IsPlaying => _out != null && _out.PlaybackState == PlaybackState.Playing;
        public bool IsPaused => _out != null && _out.PlaybackState == PlaybackState.Paused;
        public bool IsActive => IsPlaying || IsPaused;
        public int PositionFrame => _prov == null ? 0 : _prov.Position;

        public float Volume
        {
            get => _volume;
            set { _volume = Math.Max(0f, Math.Min(1.5f, value)); if (_prov != null) _prov.Volume = _volume; }
        }

        public void Play(AudioBuffer buf, int startFrame, int endFrame, bool ignoreLoop)
        {
            Stop();
            if (buf == null || buf.TotalFrames == 0) return;
            if (startFrame >= endFrame) startFrame = 0;

            Buffer = buf;
            _prov = new LoopProvider(buf, startFrame, endFrame, ignoreLoop) { Volume = _volume };
            _out = new WaveOutEvent { DesiredLatency = 120 };
            _out.Init(new SampleToWaveProvider16(_prov));
            _out.Play();
        }

        public void TogglePause()
        {
            if (_out == null) return;
            if (_out.PlaybackState == PlaybackState.Playing) _out.Pause();
            else if (_out.PlaybackState == PlaybackState.Paused) _out.Play();
        }

        public void Pause() { if (IsPlaying) _out.Pause(); }
        public void Resume() { if (IsPaused) _out.Play(); }

        public void Stop()
        {
            if (_out != null)
            {
                try { _out.Stop(); } catch { }
                _out.Dispose();
                _out = null;
            }
            _prov = null;
        }

        public void Dispose() => Stop();
    }

    /// <summary>Legacy microphone recorder (16-bit PCM, device number). The app now uses DeviceRecorder; this stays for compatibility.</summary>
    public sealed class AudioRecorder : IDisposable
    {
        private WaveInEvent _in;
        private MemoryStream _ms;
        private readonly object _lock = new object();
        private int _rate, _ch;

        public bool IsRecording => _in != null;
        public int DeviceCount => WaveInEvent.DeviceCount;

        public void Start(int deviceNumber, int rate = 44100, int channels = 2)
        {
            Stop();
            _rate = rate; _ch = channels;
            _ms = new MemoryStream();
            _in = new WaveInEvent { DeviceNumber = deviceNumber, WaveFormat = new WaveFormat(rate, 16, channels), BufferMilliseconds = 50 };
            _in.DataAvailable += (s, e) => { lock (_lock) { _ms?.Write(e.Buffer, 0, e.BytesRecorded); } };
            _in.StartRecording();
        }

        public double SecondsRecorded
        {
            get { lock (_lock) { return _ms == null ? 0 : _ms.Length / (double)(_rate * _ch * 2); } }
        }

        public AudioBuffer Stop()
        {
            if (_in == null) return null;
            try { _in.StopRecording(); } catch { }
            _in.Dispose();
            _in = null;
            byte[] pcm;
            lock (_lock) { pcm = _ms.ToArray(); _ms.Dispose(); _ms = null; }
            if (pcm.Length < _ch * 2) return null;
            return AudioBuffer.FromPcm16(pcm, _rate, _ch);
        }

        public void Dispose() { Stop(); }
    }
}
