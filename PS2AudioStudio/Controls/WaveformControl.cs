using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PS2AudioStudio.Audio;

namespace PS2AudioStudio.Controls
{
    /// <summary>
    /// Audacity-style waveform view.
    ///   * zoom is stored as "frames per pixel" (independent of control size -> zoom in/out always works)
    ///   * mouse wheel zooms around the play-start line (Shift+wheel scrolls); you can zoom out / scroll past the end of the audio
    ///   * horizontal scrollbar, one lane per channel, time ruler, min/max peak cache for long files
    ///   * click = cursor, drag = selection, drag selection edges = resize, drag the green/red flags = loop points
    /// All selection ranges are [SelStart, SelEnd) (end exclusive).
    /// </summary>
    public class WaveformControl : UserControl
    {
        private const int RulerH = 22;
        private const int PkBlock = 128;
        private const double MinFpp = 1.0 / 64.0;       // max zoom = 64 pixels per sample
        private const int SelGrab = 8;                  // pixels around a selection edge that grab it
        private const int LoopGrab = 7;                 // pixels around a loop flag that grab it

        private AudioBuffer _buf;
        private double _viewStart;          // first visible frame
        private double _fpp = 1;            // frames per pixel
        private bool _fit = true;

        private double _keepFpp;            // zoom restored after a recording; allowed even if it is wider than the normal limit

        private bool _fullFit;              // true = the fit view shows the audio at 100% of the width (set by MarkFullFit)

        private float[][] _pmin, _pmax;

        private int _cursor;
        private int _selA = -1, _selB = -1;
        private int _playPos = -1;
        private bool _selecting, _dragLS, _dragLE;

        // live recording view (see LiveBegin)
        private bool _live;
        private double _liveSec = 30.0;     // width of the visible window in seconds
        private int _liveRate = 48000;
        private int _liveEnd;               // frame where the newest recorded sample is
        private const double LiveMinSec = 2.0;
        private double _liveOffsetSec;      // real track time (seconds) of the left edge of the live window
        /// <summary>Widest live window in seconds (MainForm lowers it to 60 when the editor is empty).</summary>
        public double LiveMaxSeconds { get; set; } = 3600.0;

        private readonly HScrollBar _sb;
        private bool _sbUpdating;

        public Label InfoLabel { get; set; }
        /// <summary>0 = free, 28 = VAG blocks, 32 = ADX frames.</summary>
        public int LoopSnap { get; set; }
        public bool FollowPlayhead { get; set; } = true;
        /// <summary>true = plain mouse wheel zooms (Shift+wheel scrolls). false = plain wheel scrolls, Ctrl+wheel zooms.</summary>
        public bool WheelZooms { get; set; } = true;
        /// <summary>true = zoom keeps the play-start line (playhead / selection start / cursor) in place when it is visible.</summary>
        public bool ZoomAtPlayLine { get; set; } = true;
        public string EmptyText { get; set; } = "Drop an audio file here  (VAG, ADX, WAV, MP3, M4A ...)";
        /// <summary>Frame under the mouse when the right button was pressed (for context menus).</summary>
        public int ContextFrame { get; private set; }

        public bool IsLive => _live;
        public double LiveWindowSeconds => _liveSec;

        public event EventHandler LoopChanged;
        public event EventHandler SelectionChanged;
        public event EventHandler EditCursorChanged;

        public WaveformControl()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(18, 18, 26);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            TabStop = true;

            _sb = new HScrollBar { Dock = DockStyle.Bottom, Enabled = false };
            _sb.ValueChanged += (s, e) =>
            {
                if (_sbUpdating || _buf == null || _live) return;
                _fit = false;
                _viewStart = _sb.Value;
                ClampView();
                Invalidate();
            };
            Controls.Add(_sb);
        }

        // ================================================================ public API

        public AudioBuffer Buffer
        {
            get => _buf;
            set => SetBuffer(value, false);
        }

        public int Total => _buf == null ? 0 : _buf.TotalFrames;
        public int EditCursor => _cursor;
        public int SelStart => Math.Min(_selA, _selB);
        public int SelEnd => Math.Max(_selA, _selB);
        public bool HasSelection => _selA >= 0 && _selB >= 0 && _selA != _selB;

        public int PlayPos
        {
            get => _playPos;
            set
            {
                if (value == _playPos) return;
                _playPos = value;
                if (FollowPlayhead && value >= 0 && !_fit && _buf != null)
                {
                    double vis = CW * _fpp;
                    if (value < _viewStart || value > _viewStart + vis)
                    {
                        _viewStart = Math.Max(0, value - vis * 0.1);
                        ClampView();
                    }
                }
                Invalidate();
            }
        }

        public void SetBuffer(AudioBuffer b, bool keepView)
        {
            _buf = b;
            _selecting = _dragLS = _dragLE = false;
            if (!keepView)
            {
                _viewStart = 0; _cursor = 0; _selA = _selB = -1; _fit = true; _playPos = -1; _keepFpp = 0;
                _fullFit = false;       // every normally loaded audio starts at 40%
            }
            else
            {
                if (_cursor > Total) _cursor = Total;
                if (_selA > Total || _selB > Total) _selA = _selB = -1;
            }
            BuildPeaks();
            ClampView();
            Invalidate();
            UpdateInfo();
        }

        /// <summary>Show the current audio at 100% of the width (whole audio fills the window). Call it right after SetBuffer.</summary>
        public void MarkFullFit()
        {
            if (_buf == null || _live) return;
            _fullFit = true;
            _fit = true;
            _viewStart = 0;
            ClampView();
            Invalidate();
        }

        /// <summary>Sets the exact view: left edge in seconds and window width in seconds (no fit, no jump).</summary>
        public void SetViewSeconds(double startSec, double windowSec)
        {
            if (_buf == null || _live || _buf.SampleRate <= 0 || windowSec <= 0) return;
            double fpp = Math.Max(MinFpp, windowSec * _buf.SampleRate / CW);
            _keepFpp = fpp;
            _fit = false;
            _fpp = fpp;
            _viewStart = Math.Max(0, startSec) * _buf.SampleRate;
            ClampView();
            Invalidate();
        }

        /// <summary>Reads the view that is on screen right now: left edge in seconds and window width in seconds.</summary>
        public bool GetViewSeconds(out double startSec, out double windowSec)
        {
            startSec = 0; windowSec = 0;
            if (_buf == null || _live || Total <= 0 || _buf.SampleRate <= 0) return false;
            double fpp = _fit ? FitFpp : _fpp;
            windowSec = CW * fpp / _buf.SampleRate;
            startSec = (_fit ? 0 : _viewStart) / _buf.SampleRate;
            return windowSec > 0;
        }

        /// <summary>Call after the buffer's samples were edited in place (keeps the current view).</summary>
        public void BufferModified()
        {
            if (_buf == null) return;
            if (_cursor > Total) _cursor = Total;
            if (_selA > Total || _selB > Total) _selA = _selB = -1;
            BuildPeaks();
            ClampView();
            Invalidate();
            UpdateInfo();
        }

        public void SetSelection(int a, int b)
        {
            if (_buf == null) return;
            _selA = Math.Max(0, Math.Min(Total, a));
            _selB = Math.Max(0, Math.Min(Total, b));
            Invalidate(); UpdateInfo();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetCursor(int frame)
        {
            _cursor = Math.Max(0, Math.Min(Total, frame));
            Invalidate(); UpdateInfo();
            EditCursorChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SelectAll() { if (_buf == null) return; SetSelection(0, Total); }
        public void ClearSel() { _selA = _selB = -1; Invalidate(); UpdateInfo(); SelectionChanged?.Invoke(this, EventArgs.Empty); }
        public void ClearSelection() => ClearSel();

        /// <summary>Jump to the very start: clears the selection, puts the cursor at 0 and scrolls there.</summary>
        public void GoToStart()
        {
            if (_buf == null) return;
            _selA = _selB = -1;
            _cursor = 0;
            if (!_fit) { _viewStart = 0; ClampView(); }
            Invalidate(); UpdateInfo();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            EditCursorChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Jump to the very end: clears the selection, puts the cursor at the last frame and scrolls there.</summary>
        public void GoToEnd()
        {
            if (_buf == null) return;
            _selA = _selB = -1;
            _cursor = Total;
            if (!_fit)
            {
                double vis = CW * _fpp;
                if (Total < _viewStart || Total > _viewStart + vis)
                {
                    _viewStart = Total - vis * 0.9;
                    ClampView();
                }
            }
            Invalidate(); UpdateInfo();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            EditCursorChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---- live recording view ----

        /// <summary>Start the live view: the window is windowSeconds wide.</summary>
        public void LiveBegin(double windowSeconds, int sampleRate)
        {
            _live = true;
            _liveRate = Math.Max(1, sampleRate);
            _liveSec = Math.Max(LiveMinSec, Math.Min(Math.Max(LiveMinSec, LiveMaxSeconds), windowSeconds));
            _liveEnd = 0;
            _liveOffsetSec = 0;
            _cursor = 0;
            _selA = _selB = -1;
            _playPos = -1;
            _selecting = _dragLS = _dragLE = false;
            _fit = false;
            _viewStart = 0;
            _sbUpdating = true;
            try { _sb.Enabled = false; } finally { _sbUpdating = false; }
            LiveApplyFpp();
            Invalidate();
        }

        /// <summary>Show a new snapshot. lineFrame = position of the newest sample inside it, leftSec = real track time of its left edge.</summary>
        public void LiveUpdate(AudioBuffer snap, int lineFrame, double leftSec)
        {
            if (!_live || snap == null) return;
            _buf = snap;
            _liveRate = Math.Max(1, snap.SampleRate);
            _liveOffsetSec = Math.Max(0, leftSec);
            _liveEnd = Math.Max(0, Math.Min(snap.TotalFrames, lineFrame));
            _cursor = _liveEnd;
            _selA = _selB = -1;
            _fit = false;
            _viewStart = 0;
            LiveApplyFpp();
            BuildPeaks();
            Invalidate();
            UpdateInfo();
        }

        /// <summary>factor &gt; 1 zooms in, factor &lt; 1 zooms out.</summary>
        public void LiveZoom(double factor)
        {
            if (!_live || factor <= 0) return;
            _liveSec = Math.Max(LiveMinSec, Math.Min(Math.Max(LiveMinSec, LiveMaxSeconds), _liveSec / factor));
            LiveApplyFpp();
            Invalidate();
            UpdateInfo();
        }

        public void LiveEnd()
        {
            _live = false;
            _liveEnd = 0;
            _liveOffsetSec = 0;
        }

        private void LiveApplyFpp()
        {
            _fpp = Math.Max(MinFpp, _liveSec * _liveRate / CW);
        }

        // ---- zoom ----
        public void ZoomIn() { if (_live) LiveZoom(1.5); else ZoomButton(2.0); }
        public void ZoomOut() { if (_live) LiveZoom(1.0 / 1.5); else ZoomButton(0.5); }

        /// <summary>Fit the audio into the window (FitFraction of the width, the rest stays empty).</summary>
        public void ZoomFull()
        {
            if (_buf == null || _live) return;
            _fit = true; _viewStart = 0;
            ClampView(); Invalidate();
        }
        public void AutoZoom() => ZoomFull();

        public void ZoomToSelection()
        {
            if (_buf == null || !HasSelection || _live) return;
            _fit = false;
            double len = SelEnd - SelStart;
            _fpp = Math.Max(MinFpp, len * 1.06 / CW);
            _viewStart = SelStart - len * 0.03;
            ClampView(); Invalidate();
        }

        public void ScrollToFrame(int frame)
        {
            if (_buf == null || _live) return;
            _fit = false;
            _viewStart = frame - CW * _fpp * 0.1;
            ClampView(); Invalidate();
        }

        // ================================================================ geometry

        private int CW => Math.Max(1, ClientSize.Width);
        private int LaneTop => RulerH;
        private int LaneBottom => ClientSize.Height - _sb.Height;

        /// <summary>Scrollable length = the audio + empty room after it (for pasting more audio at the end).</summary>
        private double VirtualTotal
        {
            get
            {
                if (_buf == null || Total <= 0) return 0;
                long pad = Math.Max((long)Total, (long)_buf.SampleRate * 30);
                return (double)Total + pad;
            }
        }

        /// <summary>How much of the window the audio fills in the default view (0.4 = 40%, the rest stays empty).</summary>
        public double FitFraction { get; set; } = 0.6;

        private double FitFpp => Math.Max(Total / (CW * (_fullFit ? 1.0 : Math.Max(0.05, Math.Min(1.0, FitFraction)))), MinFpp);
        private double MaxFpp => Math.Max(Math.Max(Math.Max(VirtualTotal, (double)Total * 5.0) / CW, _keepFpp), MinFpp);

        /// <summary>The line the audio starts from when you press Play: the playhead while playing,
        /// otherwise the selection start, otherwise the edit cursor.</summary>
        private double ZoomLineFrame()
        {
            if (_playPos >= 0 && FollowPlayhead) return _playPos;
            return HasSelection ? SelStart : _cursor;
        }

        /// <summary>Zoom buttons / keys: keep the play-start line where it is; if it is off-screen, centre the view on it.</summary>
        private void ZoomButton(double factor)
        {
            if (_buf == null || Total <= 0) return;
            if (_fit) _fpp = FitFpp;
            double line = ZoomLineFrame();
            double x = XOf(line);
            int ax = (x >= 0 && x <= CW) ? (int)x : CW / 2;
            ZoomAtFrame(factor, line, ax);
        }

        /// <summary>Zoom so that 'frame' stays at screen position anchorX.</summary>
        private void ZoomAtFrame(double factor, double frame, int anchorX)
        {
            if (_buf == null || Total <= 0) return;
            if (_fit) _fpp = FitFpp;
            double newFpp = Math.Max(MinFpp, Math.Min(MaxFpp, _fpp / factor));
            _fit = false;
            _fpp = newFpp;
            _viewStart = frame - anchorX * _fpp;
            ClampView();
            Invalidate();
        }

        /// <summary>Zoom around a screen position (used by the wheel when the play-start line is off-screen).</summary>
        private void ZoomAt(double factor, int anchorX)
        {
            if (_buf == null || Total <= 0) return;
            if (_fit) _fpp = FitFpp;
            ZoomAtFrame(factor, _viewStart + anchorX * _fpp, anchorX);
        }

        private void ClampView()
        {
            if (_live)
            {
                _fit = false; _viewStart = 0;
                LiveApplyFpp();
                return;
            }
            if (_buf == null || Total <= 0) { _viewStart = 0; UpdateScroll(); return; }
            if (_fit) _fpp = FitFpp;
            else _fpp = Math.Max(MinFpp, Math.Min(MaxFpp, _fpp));
            double vis = CW * _fpp;
            double maxStart = Math.Max(0, VirtualTotal - vis);
            if (_viewStart > maxStart) _viewStart = maxStart;
            if (_viewStart < 0) _viewStart = 0;
            UpdateScroll();
        }

        private void UpdateScroll()
        {
            _sbUpdating = true;
            try
            {
                double virt = VirtualTotal;
                int vt = (int)Math.Min(int.MaxValue - 1, Math.Ceiling(virt));
                double visD = CW * _fpp;
                if (_buf == null || vt <= 1 || visD >= vt - 0.5)
                {
                    _sb.Enabled = false;
                    _sb.Minimum = 0; _sb.Maximum = 1; _sb.LargeChange = 1; _sb.Value = 0;
                }
                else
                {
                    int vis = (int)Math.Max(1, Math.Min(vt - 1, Math.Floor(visD)));
                    _sb.Enabled = true;
                    _sb.Minimum = 0;
                    _sb.Maximum = vt - 1;
                    _sb.LargeChange = vis;
                    _sb.SmallChange = Math.Max(1, vis / 10);
                    int v = (int)Math.Max(0, Math.Min(vt - vis, _viewStart));
                    _sb.Value = v;
                }
            }
            catch (ArgumentException) { }
            finally { _sbUpdating = false; }
        }

        private double XOf(double frame) => (frame - _viewStart) / _fpp;

        private int FrameAt(int x)
        {
            double f = _viewStart + x * _fpp;
            int fi = (int)Math.Round(f);
            return Math.Max(0, Math.Min(Total, fi));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_buf != null) ClampView();
            Invalidate();
        }

        // ================================================================ peaks

        private void BuildPeaks()
        {
            _pmin = _pmax = null;
            if (_buf == null || Total == 0) return;
            int ch = _buf.Channels, total = Total;
            int blocks = (total + PkBlock - 1) / PkBlock;
            _pmin = new float[ch][];
            _pmax = new float[ch][];
            float[] s = _buf.Samples;
            for (int c = 0; c < ch; c++)
            {
                var mn = new float[blocks];
                var mx = new float[blocks];
                for (int b = 0; b < blocks; b++)
                {
                    int a = b * PkBlock, e = Math.Min(total, a + PkBlock);
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int f = a; f < e; f++)
                    {
                        float v = s[f * ch + c];
                        if (v < lo) lo = v;
                        if (v > hi) hi = v;
                    }
                    mn[b] = lo; mx[b] = hi;
                }
                _pmin[c] = mn; _pmax[c] = mx;
            }
        }

        // ================================================================ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int W = ClientSize.Width;
            int top = LaneTop, bottom = LaneBottom;
            if (bottom <= top + 8) return;

            if (_buf == null || Total == 0)
            {
                using var f = new Font("Segoe UI", 12);
                var sz = g.MeasureString(EmptyText, f);
                g.DrawString(EmptyText, f, Brushes.DimGray, (W - sz.Width) / 2, top + (bottom - top - sz.Height) / 2);
                DrawRuler(g, W);
                return;
            }

            int ch = _buf.Channels;
            int laneH = (bottom - top) / ch;
            int total = Total;

            // lanes
            for (int c = 0; c < ch; c++)
            {
                int y0 = top + c * laneH;
                int mid = y0 + laneH / 2;
                using (var bg = new SolidBrush(c % 2 == 0 ? Color.FromArgb(20, 20, 30) : Color.FromArgb(24, 24, 36)))
                    g.FillRectangle(bg, 0, y0, W, laneH);
                using var gp = new Pen(Color.FromArgb(40, 40, 58));
                g.DrawLine(gp, 0, y0 + laneH / 4, W, y0 + laneH / 4);
                g.DrawLine(gp, 0, y0 + 3 * laneH / 4, W, y0 + 3 * laneH / 4);
                g.DrawLine(Pens.DimGray, 0, mid, W, mid);
                if (c > 0) g.DrawLine(Pens.Gray, 0, y0, W, y0);
            }

            // empty room after the end of the audio (paste / append area); while recording: after the newest sample
            float endX = (float)XOf(_live ? _liveEnd : total);
            if (endX < W)
            {
                float ex0 = Math.Max(0f, endX);
                using (var shade = new SolidBrush(Color.FromArgb(150, 6, 6, 10)))
                    g.FillRectangle(shade, ex0, top, W - ex0, bottom - top);
                if (endX >= 0 && !_live)
                    using (var endPen = new Pen(Color.FromArgb(160, 255, 255, 255)) { DashStyle = DashStyle.Dash })
                        g.DrawLine(endPen, endX, top, endX, bottom);
            }

            // loop region tint
            bool loopOn = !_live && _buf.LoopEnabled && _buf.LoopStart >= 0 && _buf.LoopEnd > _buf.LoopStart;
            if (loopOn)
            {
                float lx1 = (float)XOf(_buf.LoopStart), lx2 = (float)XOf(_buf.LoopEnd);
                float a = Math.Max(0, lx1), b = Math.Min(W, lx2);
                if (b > a)
                    using (var br = new SolidBrush(Color.FromArgb(28, 0, 255, 90)))
                        g.FillRectangle(br, a, top, b - a, bottom - top);
            }

            // selection
            if (HasSelection)
            {
                float x1 = (float)Math.Max(0, XOf(SelStart)), x2 = (float)Math.Min(W, XOf(SelEnd));
                if (x2 > x1)
                    using (var br = new SolidBrush(Color.FromArgb(70, 90, 140, 255)))
                        g.FillRectangle(br, x1, top, x2 - x1, bottom - top);
                // edge lines (these are the draggable handles)
                using (var ep = new Pen(Color.FromArgb(200, 140, 180, 255), 1))
                {
                    float sx = (float)XOf(SelStart), ex = (float)XOf(SelEnd);
                    if (sx >= 0 && sx <= W) g.DrawLine(ep, sx, top, sx, bottom);
                    if (ex >= 0 && ex <= W) g.DrawLine(ep, ex, top, ex, bottom);
                }
            }

            // waveform
            g.SmoothingMode = SmoothingMode.None;
            using (var wp = new Pen(Color.FromArgb(110, 185, 255)))
            using (var dot = new SolidBrush(Color.FromArgb(255, 220, 120)))
            {
                for (int c = 0; c < ch; c++)
                    DrawLane(g, c, top + c * laneH, laneH, W, wp, dot);
            }

            // loop markers
            if (loopOn)
            {
                DrawFlag(g, (float)XOf(_buf.LoopStart), top, bottom, Color.Lime, "L", true);
                DrawFlag(g, (float)XOf(_buf.LoopEnd), top, bottom, Color.Red, "R", false);
            }

            // edit cursor (while recording: the red recording line)
            if (!HasSelection)
            {
                float cx = (float)XOf(_cursor);
                if (cx >= 0 && cx <= W)
                    using (var cp = new Pen(_live ? Color.Red : Color.White, _live ? 2 : 1)) g.DrawLine(cp, cx, top, cx, bottom);
            }

            // play head
            if (_playPos >= 0)
            {
                float px = (float)XOf(_playPos);
                if (px >= 0 && px <= W)
                    using (var pp = new Pen(Color.Yellow, 2)) g.DrawLine(pp, px, top, px, bottom);
            }

            DrawRuler(g, W);
        }

        private void DrawLane(Graphics g, int c, int y0, int h, int W, Pen pen, Brush dot)
        {
            int total = Total, ch = _buf.Channels;
            float[] S = _buf.Samples;
            int mid = y0 + h / 2;
            float amp = h / 2f - 3;

            if (_fpp < 1.0)
            {
                double ppf = 1.0 / _fpp;
                int fa = Math.Max(0, (int)Math.Floor(_viewStart) - 1);
                int fb = Math.Min(total - 1, (int)Math.Ceiling(_viewStart + W * _fpp) + 1);
                int n = fb - fa + 1;
                if (n < 2) return;
                var pts = new PointF[n];
                for (int i = 0; i < n; i++)
                    pts[i] = new PointF((float)((fa + i - _viewStart) * ppf), mid - S[(fa + i) * ch + c] * amp);
                g.DrawLines(pen, pts);
                if (ppf >= 6)
                    foreach (var p in pts) g.FillRectangle(dot, p.X - 2, p.Y - 2, 4, 4);
                return;
            }

            float[] pmn = _pmin[c], pmx = _pmax[c];
            bool useCache = _fpp >= PkBlock * 2;
            for (int x = 0; x < W; x++)
            {
                double f0 = _viewStart + x * _fpp;
                int a = (int)f0;
                if (a >= total) break;
                int b = (int)Math.Ceiling(f0 + _fpp);
                if (b > total) b = total;
                if (b <= a) b = a + 1;

                float mn = float.MaxValue, mx = float.MinValue;
                if (useCache)
                {
                    int ba = a / PkBlock, bb = (b - 1) / PkBlock;
                    for (int k = ba; k <= bb && k < pmn.Length; k++)
                    {
                        if (pmn[k] < mn) mn = pmn[k];
                        if (pmx[k] > mx) mx = pmx[k];
                    }
                }
                else
                {
                    for (int f = a; f < b; f++)
                    {
                        float v = S[f * ch + c];
                        if (v < mn) mn = v;
                        if (v > mx) mx = v;
                    }
                }
                if (mn > mx) continue;
                int y1 = mid - (int)(mx * amp);
                int y2 = mid - (int)(mn * amp);
                if (y2 <= y1) y2 = y1 + 1;
                g.DrawLine(pen, x, y1, x, y2);
            }
        }

        private void DrawFlag(Graphics g, float x, int top, int bottom, Color c, string label, bool atTop)
        {
            if (x < -20 || x > ClientSize.Width + 20) return;
            using var p = new Pen(c, 2);
            g.DrawLine(p, x, top, x, bottom);
            int ty = atTop ? top + 1 : bottom - 15;
            using var br = new SolidBrush(c);
            g.FillRectangle(br, x - 8, ty, 16, 14);
            using var f = new Font("Arial", 8, FontStyle.Bold);
            g.DrawString(label, f, Brushes.Black, x - 6, ty);
        }

        private static readonly double[] Steps =
        {
            0.0001, 0.0002, 0.0005, 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5,
            1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600
        };

        private void DrawRuler(Graphics g, int W)
        {
            using (var bg = new SolidBrush(Color.FromArgb(34, 34, 46)))
                g.FillRectangle(bg, 0, 0, W, RulerH);
            g.DrawLine(Pens.Gray, 0, RulerH - 1, W, RulerH - 1);
            if (_buf == null || Total == 0) return;

            double sr = _buf.SampleRate;
            double off = _live ? _liveOffsetSec : 0;           // while recording: real track time of the left edge
            double pps = sr / _fpp;                             // pixels per second
            double step = Steps[Steps.Length - 1];
            foreach (double s in Steps) if (s * pps >= 90) { step = s; break; }

            double t0 = off + _viewStart / sr;
            double t1 = off + (_viewStart + W * _fpp) / sr;
            double t = Math.Floor(t0 / step) * step;
            using var f = new Font("Consolas", 7.5f);
            using var br = new SolidBrush(Color.FromArgb(180, 180, 195));
            using var tick = new Pen(Color.FromArgb(150, 150, 165));
            for (; t <= t1 + step; t += step)
            {
                float x = (float)(((t - off) * sr - _viewStart) / _fpp);
                if (x >= -40 && x <= W + 40)
                {
                    g.DrawLine(tick, x, RulerH - 9, x, RulerH - 1);
                    g.DrawString(FormatTime(t, step), f, br, x + 2, 2);
                }
                float xm = x + (float)(step / 2 * pps);
                g.DrawLine(tick, xm, RulerH - 5, xm, RulerH - 1);
            }
        }

        public static string FormatTime(double t, double step)
        {
            if (t < 0) t = 0;
            int m = (int)(t / 60);
            double s = t - m * 60;
            if (step >= 1) return $"{m}:{(int)Math.Round(s):00}";
            if (step >= 0.001) return $"{m}:{s:00.000}";
            return $"{m}:{s:00.0000}";
        }

        // ================================================================ info line

        public void UpdateInfo()
        {
            if (InfoLabel == null) return;
            if (_buf == null) { InfoLabel.Text = "No audio loaded"; return; }
            if (_live)
            {
                double pos = _liveOffsetSec + (double)_liveEnd / Math.Max(1, _liveRate);
                InfoLabel.Text = $"● RECORDING | line at {FormatTime(pos, 0.001)} | window {_liveSec:F0}s | mouse wheel or + / - to zoom";
                return;
            }
            double sr = _buf.SampleRate;
            string s = $"{_buf.SampleRate} Hz | {_buf.Channels} ch | {_buf.DurationSeconds:F4}s | {_buf.TotalFrames:N0} frames";
            if (HasSelection)
                s += $" | Sel: {SelStart / sr:F4}s → {SelEnd / sr:F4}s  ({SelStart:N0}–{SelEnd:N0}, {SelEnd - SelStart:N0} frames)";
            else
                s += $" | Cursor: {_cursor / sr:F4}s ({_cursor:N0})";
            if (_buf.LoopEnabled)
                s += $" | Loop: {_buf.LoopStart:N0} → {_buf.LoopEnd:N0}  ({_buf.LoopStart / sr:F4}s → {_buf.LoopEnd / sr:F4}s)";
            else
                s += " | Loop: OFF";
            InfoLabel.Text = s;
        }

        // ================================================================ mouse

        private int Snap(int frame)
        {
            if (LoopSnap > 1) frame = (int)Math.Round(frame / (double)LoopSnap) * LoopSnap;
            return Math.Max(0, Math.Min(Total, frame));
        }

        private bool LoopActive => _buf != null && _buf.LoopEnabled && _buf.LoopStart >= 0 && _buf.LoopEnd > _buf.LoopStart;

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            var f = FindForm();
            if (f != null && Form.ActiveForm == f && !(f.ActiveControl is UpDownBase) && !(f.ActiveControl is TextBoxBase) && !(f.ActiveControl is ComboBox))
                Focus();
        }

        /// <summary>Hit test for the loop flags and the selection edges at mouse position (x, y).</summary>
        private void HitTest(int x, int y, bool shift,
            out bool nearLoop, out double dLs, out double dLe,
            out bool nearSel, out double dSs, out double dSe)
        {
            nearLoop = nearSel = false;
            dLs = dLe = dSs = dSe = double.MaxValue;
            if (_buf == null || y <= LaneTop) return;

            if (LoopActive)
            {
                dLs = Math.Abs(x - XOf(_buf.LoopStart));
                dLe = Math.Abs(x - XOf(_buf.LoopEnd));
                nearLoop = dLs <= LoopGrab || dLe <= LoopGrab;
            }
            if (HasSelection && !shift)
            {
                dSs = Math.Abs(x - XOf(SelStart));
                dSe = Math.Abs(x - XOf(SelEnd));
                nearSel = dSs <= SelGrab || dSe <= SelGrab;
            }
            // loop flag and selection edge on the same spot: the top / bottom strip (where the L / R tabs are) grabs the loop flag
            if (nearLoop && nearSel)
            {
                bool flagZone = y < LaneTop + 16 || y > LaneBottom - 16;
                nearLoop = flagZone;
                nearSel = !flagZone;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (_buf == null || _live) return;
            int f = FrameAt(e.X);
            ContextFrame = f;
            if (e.Button != MouseButtons.Left) return;

            bool shift = (ModifierKeys & Keys.Shift) != 0;
            HitTest(e.X, e.Y, shift, out bool nearLoop, out double dLs, out double dLe,
                    out bool nearSel, out double dSs, out double dSe);

            if (nearLoop)
            {
                if (dLs <= dLe) _dragLS = true; else _dragLE = true;
                return;
            }

            if (nearSel)
            {
                // grab one edge of the existing selection: the other edge stays fixed, nothing changes until the mouse moves
                int s = SelStart, en = SelEnd;
                if (dSs <= dSe) { _selA = en; _selB = s; }
                else { _selA = s; _selB = en; }
                _selecting = true;
                return;
            }

            _selecting = true;
            if (shift)
            {
                int anchor = HasSelection ? _selA : _cursor;
                _selA = anchor; _selB = f;
            }
            else
            {
                _selA = _selB = f;
                _cursor = f;
            }
            EditCursorChanged?.Invoke(this, EventArgs.Empty);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate(); UpdateInfo();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_buf == null || _live) return;

            if (_selecting || _dragLS || _dragLE)
            {
                // auto-scroll when dragging past the edges
                if (e.X < 0) { _fit = false; _viewStart -= (-e.X) * _fpp * 0.4 + _fpp; ClampView(); }
                else if (e.X > CW) { _fit = false; _viewStart += (e.X - CW) * _fpp * 0.4 + _fpp; ClampView(); }
            }

            int f = FrameAt(e.X);
            if (_dragLS)
            {
                int v = Snap(f);
                _buf.LoopStart = Math.Max(0, Math.Min(v, _buf.LoopEnd - 1));
                LoopChanged?.Invoke(this, EventArgs.Empty);
                Invalidate(); UpdateInfo();
                return;
            }
            if (_dragLE)
            {
                int v = Snap(f);
                _buf.LoopEnd = Math.Min(Total, Math.Max(v, _buf.LoopStart + 1));
                LoopChanged?.Invoke(this, EventArgs.Empty);
                Invalidate(); UpdateInfo();
                return;
            }
            if (_selecting)
            {
                _selB = f;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                Invalidate(); UpdateInfo();
                return;
            }

            // hover feedback
            HitTest(e.X, e.Y, (ModifierKeys & Keys.Shift) != 0, out bool nearLoop, out _, out _, out bool nearSel, out _, out _);
            Cursor = (nearLoop || nearSel) ? Cursors.SizeWE : Cursors.IBeam;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_live) return;
            bool cursorMoved = false;
            if (_selecting && _selA == _selB)
            {
                // a plain click, or an edge dragged back onto the other edge: becomes the edit cursor
                if (_selA >= 0 && _cursor != _selA) { _cursor = _selA; cursorMoved = true; }
                _selA = _selB = -1;
            }
            _selecting = _dragLS = _dragLE = false;
            Invalidate(); UpdateInfo();
            if (cursorMoved) EditCursorChanged?.Invoke(this, EventArgs.Empty);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e is HandledMouseEventArgs h) h.Handled = true;

            // while recording: the wheel zooms the live window (2 s .. 60 s), no matter what
            if (_live) { LiveZoom(Math.Pow(1.5, e.Delta / 120.0)); return; }

            if (_buf == null || Total <= 0) return;

            bool shift = (ModifierKeys & Keys.Shift) != 0;
            bool ctrl = (ModifierKeys & Keys.Control) != 0;

            if (!shift && (WheelZooms || ctrl))
            {
                // smooth zoom (1.6x per wheel notch)
                double factor = Math.Pow(1.6, e.Delta / 120.0);
                int mx = Math.Max(0, Math.Min(CW, e.X));
                if (ZoomAtPlayLine)
                {
                    if (_fit) _fpp = FitFpp;
                    double line = ZoomLineFrame();
                    double x = XOf(line);
                    if (x >= 0 && x <= CW) ZoomAtFrame(factor, line, (int)x);   // play-start line visible: keep it in place
                    else ZoomAt(factor, mx);                                    // off-screen: zoom at the mouse
                }
                else ZoomAt(factor, mx);
            }
            else
            {
                if (_fit) _fpp = FitFpp;
                _fit = false;
                _viewStart -= (e.Delta / 120.0) * CW * _fpp * 0.2;
                ClampView();
                Invalidate();
            }
        }
    }
}
