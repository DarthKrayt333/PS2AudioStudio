using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PS2AudioStudio.Audio
{
    public sealed class RecordDevice
    {
        public RecordSourceKind Kind;
        public string Id = "";
        public string Name = "";
        public bool IsDefault;
        public override string ToString() =>
            (Kind == RecordSourceKind.Loopback ? "PC audio (loopback): " : "Input: ") + Name + (IsDefault ? "  [default]" : "");
    }

    public static class RecordDevices
    {
        public static List<RecordDevice> List()
        {
            var res = new List<RecordDevice>
            {
                new RecordDevice { Kind = RecordSourceKind.Capture,  Id = "", Name = "Windows default microphone" },
                new RecordDevice { Kind = RecordSourceKind.Loopback, Id = "", Name = "Windows default playback device" }
            };
            try
            {
                using var en = new MMDeviceEnumerator();
                string defCap = null, defRen = null;
                try { defCap = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID; } catch { }
                try { defRen = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID; } catch { }

                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    res.Add(new RecordDevice { Kind = RecordSourceKind.Capture, Id = d.ID, Name = d.FriendlyName, IsDefault = d.ID == defCap });
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    res.Add(new RecordDevice { Kind = RecordSourceKind.Loopback, Id = d.ID, Name = d.FriendlyName, IsDefault = d.ID == defRen });
            }
            catch { }
            return res;
        }

        public static IWaveIn Create(RecordSourceKind kind, string id)
        {
            var en = new MMDeviceEnumerator();
            MMDevice dev = null;
            if (!string.IsNullOrEmpty(id)) { try { dev = en.GetDevice(id); } catch { dev = null; } }

            if (kind == RecordSourceKind.Loopback)
            {
                if (dev == null) dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return new WasapiLoopbackCapture(dev);
            }
            if (dev == null) dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return new WasapiCapture(dev);
        }
    }

    /// <summary>Records from any device chosen in Settings; returns an AudioBuffer when stopped.</summary>
    public sealed class DeviceRecorder : IDisposable
    {
        private IWaveIn _in;
        private WaveFormat _fmt;
        private readonly List<float[]> _chunks = new List<float[]>();
        private readonly object _lock = new object();

        public bool IsRecording { get; private set; }
        public int SampleRate => _fmt == null ? 0 : _fmt.SampleRate;
        /// <summary>Peak level 0..1 of each captured block (raised on the capture thread).</summary>
        public event Action<float> Level;

        public void Start(RecordSourceKind kind, string deviceId)
        {
            if (IsRecording) return;
            lock (_lock) _chunks.Clear();
            _in = RecordDevices.Create(kind, deviceId);
            _fmt = _in.WaveFormat is WaveFormatExtensible x ? x.ToStandardWaveFormat() : _in.WaveFormat;
            _in.DataAvailable += OnData;
            _in.StartRecording();
            IsRecording = true;
        }

        public void Start() => Start(AppSettings.Current.RecordKind, AppSettings.Current.RecordDeviceId);

        private void OnData(object sender, WaveInEventArgs e)
        {
            int bytes = e.BytesRecorded;
            if (bytes <= 0 || _fmt == null) return;
            float[] f;
            int bits = _fmt.BitsPerSample;
            byte[] b = e.Buffer;

            if (_fmt.Encoding == WaveFormatEncoding.IeeeFloat && bits == 32)
            {
                f = new float[bytes / 4];
                Buffer.BlockCopy(b, 0, f, 0, f.Length * 4);
            }
            else if (bits == 16)
            {
                f = new float[bytes / 2];
                for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(b, i * 2) / 32768f;
            }
            else if (bits == 24)
            {
                f = new float[bytes / 3];
                for (int i = 0; i < f.Length; i++)
                {
                    int v = (b[i * 3] << 8) | (b[i * 3 + 1] << 16) | (b[i * 3 + 2] << 24);
                    f[i] = (v >> 8) / 8388608f;
                }
            }
            else if (bits == 32)
            {
                f = new float[bytes / 4];
                for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt32(b, i * 4) / 2147483648f;
            }
            else return;

            float peak = 0;
            for (int i = 0; i < f.Length; i++) { float a = Math.Abs(f[i]); if (a > peak) peak = a; }
            lock (_lock) _chunks.Add(f);
            Level?.Invoke(peak);
        }

        public AudioBuffer Stop()
        {
            if (_in == null) return null;
            try { _in.StopRecording(); } catch { }
            try { _in.Dispose(); } catch { }      // Dispose joins the capture thread
            _in = null;
            IsRecording = false;

            float[] all;
            lock (_lock)
            {
                int n = 0;
                foreach (var c in _chunks) n += c.Length;
                all = new float[n];
                int o = 0;
                foreach (var c in _chunks) { Array.Copy(c, 0, all, o, c.Length); o += c.Length; }
                _chunks.Clear();
            }
            int ch = Math.Max(1, _fmt.Channels);
            var buf = new AudioBuffer(all, _fmt.SampleRate, ch);
            if (ch > 2) buf = buf.ToChannels(2);
            return buf;
        }

        public void Dispose() { try { Stop(); } catch { } }
    }
}