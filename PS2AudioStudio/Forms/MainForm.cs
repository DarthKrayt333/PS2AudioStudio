using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PS2AudioStudio.Archives;
using PS2AudioStudio.Audio;
using PS2AudioStudio.Controls;
using DragFiles = System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, System.Func<byte[]>>>;

namespace PS2AudioStudio
{
    /// <summary>
    /// Shell: menu, transport bar (works on whichever tab is active), global drag &amp; drop, file routing.
    /// Tabs live in MainForm.Editor.cs / MainForm.Psf2.cs / MainForm.Afs.cs.
    /// </summary>
    public partial class MainForm : Form
    {
        private readonly AudioPlayer player = new AudioPlayer();
        private readonly DeviceRecorder recorder = new DeviceRecorder();
        private readonly System.Diagnostics.Stopwatch recWatch = new System.Diagnostics.Stopwatch();
        private readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer { Interval = 30 };
        private readonly System.Windows.Forms.Timer idleTimer = new System.Windows.Forms.Timer { Interval = 150 };
        private readonly ToolTip tip = new ToolTip();

        private MidiPlayer midi;            // plays SQ / MIDI songs through the Windows MIDI synth
        private bool _midiShown;

        private TabControl tabs;
        private TabPage tabEditor, tabPsf2, tabAfs;
        private Label lblStatus, lblTime, lblRate;
        private TrackBar trkVol;
        private Button bRec, bPlay, bPause, bStop, bStart, bEnd;
        private WaveformControl playingWave;
        private bool _boxSync;

        /// <summary>True while Ctrl+A selects every row of a list, so selection-changed handlers can ignore it.</summary>
        private bool _listBulk;

        [DllImport("user32.dll")] private static extern IntPtr GetFocus();

        public MainForm()
        {
            Text = "PS2 Audio Studio";
            Size = new Size(1360, 860);
            MinimumSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Ui.Bg;
            ForeColor = Color.White;
            KeyPreview = false;
            try { AppIcon.Apply(this); } catch { }

            BuildUI();
            HookDrop(this);

            uiTimer.Tick += (s, e) => OnUiTick();
            idleTimer.Tick += (s, e) => UpdateIdleClock();
            idleTimer.Start();
            Shown += (s, e) => InitSplitters();
            FormClosing += OnFormClosing;

            _wheelFilter = new WheelFilter(this);
            Application.AddMessageFilter(_wheelFilter);

            tabs.SelectedIndexChanged += (s, e) => UpdateTitle();
            SetStatus("Ready. Drag & drop VAG / ADX / WAV / MP3 / M4A / AFS / HD / BD / SQ / MID files anywhere in this window.");
        }

        // ================================================================= UI

        private void BuildUI()
        {
            // ---- transport (bottom, always visible, acts on the active tab) ----
            var transport = new Panel { Dock = DockStyle.Bottom, Height = 74, BackColor = Ui.Panel };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6, 6, 6, 2), BackColor = Ui.Panel };

            bStart = Ui.Btn("⏮", 44); bStart.Click += (s, e) => TransportGoStart();
            bPlay = Ui.Btn("▶ Play", 80); bPlay.Click += (s, e) => TransportPlay();
            bPause = Ui.Btn("⏸ Pause", 80); bPause.Click += (s, e) => TransportPause();
            bStop = Ui.Btn("⏹ Stop", 80); bStop.Click += (s, e) => TransportStop();
            bEnd = Ui.Btn("⏭", 44); bEnd.Click += (s, e) => TransportGoEnd();
            bRec = Ui.Btn("⏺ Rec", 80); bRec.Click += (s, e) => ToggleRec();
            tip.SetToolTip(bStart, "Go to the very start (Home)");
            tip.SetToolTip(bEnd, "Go to the very end (End)");

            trkVol = new TrackBar { Width = 130, Minimum = 0, Maximum = 100, Value = 80, TickFrequency = 25, AutoSize = false, Height = 28, Margin = new Padding(14, 2, 0, 0) };
            var lblVol = new Label { Text = "Vol 80%", AutoSize = true, ForeColor = Color.LightGreen, Margin = new Padding(2, 8, 10, 0) };
            trkVol.ValueChanged += (s, e) => { player.Volume = trkVol.Value / 100f; lblVol.Text = $"Vol {trkVol.Value}%"; };
            player.Volume = 0.8f;

            lblTime = new Label { Text = "00:00.000", AutoSize = true, Font = new Font("Consolas", 14, FontStyle.Bold), ForeColor = Color.Lime, Margin = new Padding(12, 3, 0, 0) };

            flow.Controls.AddRange(new Control[] { bStart, bPlay, bPause, bStop, bEnd, bRec, trkVol, lblVol, lblTime });

            lblStatus = new Label { Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, Padding = new Padding(8, 4, 4, 2), AutoEllipsis = true };

            // always visible: sample rate of the audio on screen and of the recording
            lblRate = new Label
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                ForeColor = Color.Khaki,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Padding = new Padding(8, 5, 12, 0),
                Text = "Audio: -   |   Rec: " + AppSettings.Current.RecordRate + " Hz"
            };

            transport.Controls.Add(lblStatus);
            transport.Controls.Add(lblRate);
            transport.Controls.Add(flow);

            // ---- tabs ----
            tabs = new TabControl { Dock = DockStyle.Fill };
            tabEditor = BuildEditorTab();
            tabPsf2 = BuildPsf2Tab();
            tabAfs = BuildAfsTab();
            tabs.TabPages.AddRange(new[] { tabEditor, tabPsf2, tabAfs });
            foreach (TabPage t in tabs.TabPages) { t.BackColor = Ui.Bg; t.ForeColor = Color.White; }

            var menu = BuildMenu();

            Controls.Add(tabs);
            Controls.Add(transport);
            Controls.Add(menu);
            MainMenuStrip = menu;
        }

        private MenuStrip BuildMenu()
        {
            var m = new MenuStrip();
            Ui.StyleMenu(m);

            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(Mi("New (what is cleared: see Settings)", (s, e) => NewProject(), Keys.Control | Keys.N));
            file.DropDownItems.Add(Mi("Open...  (audio / AFS / HD / BD / SQ / MIDI)", (s, e) => OpenDlg(), Keys.Control | Keys.O));
            file.DropDownItems.Add(Mi("Open raw PS-ADPCM (headerless / interleaved VAG)...", (s, e) => OpenRawDlg()));
            file.DropDownItems.Add(Mi("New SQ from MIDI file(s)...", (s, e) => NewSqFromMidi()));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Mi("Save current tab", (s, e) => SaveCurrent(), Keys.Control | Keys.S));
            file.DropDownItems.Add(Mi("Save audio as... (VAG / ADX / WAV / MP3 / M4A / raw)", (s, e) => SaveAudioAs(), Keys.Control | Keys.Shift | Keys.S));
            file.DropDownItems.Add(Mi("Save HD + BD as...", (s, e) => SaveBank(true)));
            file.DropDownItems.Add(Mi("Save SQ as...", (s, e) => SaveSq()));
            file.DropDownItems.Add(Mi("Save AFS", (s, e) => SaveAfs(false)));
            file.DropDownItems.Add(Mi("Save AFS as...", (s, e) => SaveAfs(true)));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Mi("Exit", (s, e) => Close()));

            var edit = new ToolStripMenuItem("&Edit");
            edit.DropDownItems.Add(Mi("Undo", (s, e) => Undo(), Keys.None, "Ctrl+Z"));
            edit.DropDownItems.Add(Mi("Redo", (s, e) => Redo(), Keys.None, "Ctrl+Y"));
            edit.DropDownItems.Add(new ToolStripSeparator());
            edit.DropDownItems.Add(Mi("Cut", (s, e) => Cut(), Keys.None, "Ctrl+X"));
            edit.DropDownItems.Add(Mi("Copy", (s, e) => Copy(), Keys.None, "Ctrl+C"));
            edit.DropDownItems.Add(Mi("Paste", (s, e) => Paste(), Keys.None, "Ctrl+V"));
            edit.DropDownItems.Add(Mi("Delete", (s, e) => DeleteSel(), Keys.None, "Del"));
            edit.DropDownItems.Add(new ToolStripSeparator());
            edit.DropDownItems.Add(Mi("Select all", (s, e) => SelectAllEditor(), Keys.None, "Ctrl+A"));
            edit.DropDownItems.Add(Mi("Trim to selection (crop)", (s, e) => FxCrop()));
            edit.DropDownItems.Add(Mi("Silence selection", (s, e) => FxSilence()));

            var fx = new ToolStripMenuItem("E&ffects");
            fx.DropDownItems.Add(Mi("Fade in", (s, e) => FxFadeIn()));
            fx.DropDownItems.Add(Mi("Fade out", (s, e) => FxFadeOut()));
            fx.DropDownItems.Add(Mi("Amplify (dB)...", (s, e) => FxAmplify()));
            fx.DropDownItems.Add(Mi("Normalize", (s, e) => FxNormalize()));
            fx.DropDownItems.Add(Mi("Reverse", (s, e) => FxReverse()));
            fx.DropDownItems.Add(Mi("Invert", (s, e) => FxInvert()));
            fx.DropDownItems.Add(new ToolStripSeparator());
            fx.DropDownItems.Add(Mi("Resample to... (e.g. 11025 / 22050 / 32000 / 44100 Hz)", (s, e) => FxResample()));
            fx.DropDownItems.Add(Mi("Mix down to mono", (s, e) => FxMono()));
            fx.DropDownItems.Add(Mi("Generate silence...", (s, e) => FxGenSilence()));

            fx.DropDownItems.Add(new ToolStripSeparator());
            var reverbMenu = BuildReverbMenu();
            fx.DropDownItems.Add(reverbMenu);
            foreach (ToolStripItem it in fx.DropDownItems)
            {
                if (it is ToolStripMenuItem first && first != reverbMenu)
                {
                    ApplyMenuForeColor(reverbMenu, first.ForeColor);
                    break;
                }
            }

            var view = new ToolStripMenuItem("&View");
            view.DropDownItems.Add(Mi("Zoom in", (s, e) => ActiveWave?.ZoomIn(), Keys.None, "Ctrl+Wheel / +"));
            view.DropDownItems.Add(Mi("Zoom out", (s, e) => ActiveWave?.ZoomOut(), Keys.None, "Ctrl+Wheel / -"));
            view.DropDownItems.Add(Mi("Zoom to fit (whole file)", (s, e) => ActiveWave?.ZoomFull()));
            view.DropDownItems.Add(Mi("Zoom to selection", (s, e) => ActiveWave?.ZoomToSelection()));

            var tr = new ToolStripMenuItem("&Transport");
            tr.DropDownItems.Add(Mi("Play", (s, e) => TransportPlay()));
            tr.DropDownItems.Add(Mi("Pause / resume", (s, e) => TransportPause()));
            tr.DropDownItems.Add(Mi("Stop", (s, e) => TransportStop()));
            tr.DropDownItems.Add(new ToolStripSeparator());
            tr.DropDownItems.Add(Mi("Go to very start", (s, e) => TransportGoStart(), Keys.None, "Home"));
            tr.DropDownItems.Add(Mi("Go to very end", (s, e) => TransportGoEnd(), Keys.None, "End"));
            tr.DropDownItems.Add(new ToolStripSeparator());
            tr.DropDownItems.Add(Mi("Record", (s, e) => ToggleRec()));

            var settings = new ToolStripMenuItem("&Settings");
            settings.DropDownItems.Add(Mi("⚙ Settings...  (recording device, File > New behaviour, looping)", (s, e) => ShowSettings()));

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(Mi("Loop points & formats - quick notes", (s, e) => ShowNotes()));
            help.DropDownItems.Add(Mi("About", (s, e) => MessageBox.Show(this,
                "PS2 Audio Studio\nVAG / ADX / WAV / MP3 / M4A editor, AFS archive manager and PSF2 (HD/BD/SQ) bank player.\n\nMade by DarthKrayt333",
                "About")));

            m.Items.AddRange(new ToolStripItem[] { file, edit, fx, view, tr, settings, help });
            MenuArrows.Hook(m);
            HookRecordStop(m);
            return m;
        }

        /// <summary>Clicking any item in File / Edit / Effects stops a running recording first.</summary>
        private void HookRecordStop(MenuStrip m)
        {
            foreach (ToolStripItem top in m.Items)
            {
                if (!(top is ToolStripMenuItem t)) continue;
                string name = (t.Text ?? "").Replace("&", "");
                if (name == "File" || name == "Edit" || name == "Effects") HookStopItem(t);
            }
        }

        private void HookStopItem(ToolStripMenuItem t)
        {
            foreach (ToolStripItem it in t.DropDownItems)
            {
                if (!(it is ToolStripMenuItem mi)) continue;
                if (mi.HasDropDownItems) HookStopItem(mi);
                else mi.MouseDown += (s, e) => StopRecordingQuietly();
            }
        }

        private WheelFilter _wheelFilter;

        /// <summary>While recording: the wheel over the waveform zooms the live view; clicking any other button stops the recording first.</summary>
        private sealed class WheelFilter : IMessageFilter
        {
            private readonly MainForm f;
            public WheelFilter(MainForm form) { f = form; }
            public bool PreFilterMessage(ref Message m)
            {
                if (!f.recorder.IsRecording) return false;

                if (m.Msg == 0x0201)   // WM_LBUTTONDOWN
                {
                    var c = Control.FromHandle(m.HWnd);
                    if (c is Button b && b != f.bRec) f.StopRecordingQuietly();
                    return false;
                }

                if (m.Msg != 0x020A) return false;   // WM_MOUSEWHEEL
                var r = f.wave.RectangleToScreen(f.wave.ClientRectangle);
                if (!r.Contains(Control.MousePosition)) return false;
                short delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                f.LiveZoom(delta > 0);
                return true;
            }
        }

        private static ToolStripMenuItem Mi(string text, EventHandler click, Keys sc = Keys.None, string disp = null)
        {
            var mi = new ToolStripMenuItem(text, null, click) { ForeColor = Color.White };
            if (sc != Keys.None) mi.ShortcutKeys = sc;
            if (disp != null) mi.ShortcutKeyDisplayString = disp;
            return mi;
        }

        private void ShowSettings()
        {
            StopRecordingQuietly();
            using (var f = new SettingsForm()) f.ShowDialog(this);
            SetStatus("Settings saved.");
        }

        private void InitSplitters()
        {
            try { InitEditorSplitters(); InitPsf2Splitters(); InitAfsSplitters(); } catch { }
        }

        // ================================================================= helpers

        private WaveformControl ActiveWave =>
            tabs.SelectedTab == tabPsf2 ? psf2Wave : tabs.SelectedTab == tabAfs ? afsWave : wave;

        private void SetStatus(string s) { if (lblStatus != null) lblStatus.Text = s; }

        private void UpdateTitle()
        {
            string t = "PS2 Audio Studio";
            if (tabs.SelectedTab == tabEditor && !string.IsNullOrEmpty(editorName)) t += " - " + Path.GetFileName(editorName);
            else if (tabs.SelectedTab == tabPsf2 && bank != null && bank.Loaded) t += " - " + Path.GetFileName(bank.HdPath) + (bank.Modified ? " *" : "");
            else if (tabs.SelectedTab == tabAfs && afs != null && afs.Path != null) t += " - " + Path.GetFileName(afs.Path) + (afs.Modified ? " *" : "");
            Text = t;
        }

        private bool TextInputFocused()
        {
            var c = Control.FromHandle(GetFocus());
            return c is TextBoxBase || c is UpDownBase || c is ComboBox;
        }

        private static byte[] ReadHead(string path, int n = 0x400)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] b = new byte[(int)Math.Min(n, fs.Length)];
            int off = 0;
            while (off < b.Length) { int r = fs.Read(b, off, b.Length - off); if (r <= 0) break; off += r; }
            return b;
        }

        private static string SafeFile(string s, string fallback = "file")
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim();
            return s.Length == 0 ? fallback : s;
        }

        private static byte[] WavBytes(AudioBuffer b)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "ps2as_" + Guid.NewGuid().ToString("N") + ".wav");
            try { StandardCodec.EncodeWav(b, tmp); return File.ReadAllBytes(tmp); }
            finally { try { File.Delete(tmp); } catch { } }
        }

        /// <summary>Ctrl+A in a list: select every row (ListView has no built-in Ctrl+A).</summary>
        private void SelectAllInList(ListView lv)
        {
            _listBulk = true;
            try
            {
                lv.BeginUpdate();
                foreach (ListViewItem it in lv.Items) it.Selected = true;
            }
            finally { lv.EndUpdate(); _listBulk = false; }
            SetStatus($"{lv.SelectedItems.Count} item(s) selected.");
        }

        // ================================================================= loop checkbox for the PSF2 / AFS tabs

        private CheckBox MakeLoopBox(WaveformControl w)
        {
            var chk = new CheckBox
            {
                Text = "🔁 Loop",
                ForeColor = Color.Lime,
                AutoSize = true,
                Dock = DockStyle.Right,
                Enabled = false,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Padding = new Padding(0, 2, 8, 0)
            };
            chk.CheckedChanged += (s, e) =>
            {
                if (_boxSync) return;
                var b = w.Buffer;
                if (b == null || b.TotalFrames == 0) { SyncLoopBox(chk, w); return; }
                b.LoopEnabled = chk.Checked;
                if (b.LoopEnabled && (b.LoopStart < 0 || b.LoopEnd <= b.LoopStart || b.LoopEnd > b.TotalFrames))
                {
                    if (w.HasSelection) { b.LoopStart = w.SelStart; b.LoopEnd = w.SelEnd; }
                    else { b.LoopStart = 0; b.LoopEnd = b.TotalFrames; }
                }
                b.ClampLoopPoints();
                w.Invalidate(); w.UpdateInfo();
                SetStatus(b.LoopEnabled ? "Loop ON - Play repeats the loop region." : "Loop OFF - Play plays once.");
            };
            return chk;
        }

        private void SyncLoopBox(CheckBox chk, WaveformControl w)
        {
            if (chk == null || w == null) return;
            _boxSync = true;
            try
            {
                var b = w.Buffer;
                bool has = b != null && b.TotalFrames > 0;
                chk.Enabled = has;
                chk.Checked = has && b.LoopEnabled;
            }
            finally { _boxSync = false; }
        }

        // ================================================================= drag out (to Explorer)

        private static void Drag(DragFiles l, string name, Func<byte[]> data)
        {
            l.Add(new KeyValuePair<string, Func<byte[]>>(name, data));
        }

        private void StartDragOut(Control src, DragFiles files)
        {
            if (files == null || files.Count == 0) { SetStatus("Nothing to drag out yet."); return; }
            try { DragOut.Start(src, files); }
            catch (Exception ex) { SetStatus("Drag-out failed: " + ex.Message); }
        }

        /// <summary>A button that starts a drag-out to Explorer when you press and drag it.</summary>
        private Button MakeDragHandle(string text, Func<DragFiles> files, int width = 90)
        {
            var b = Ui.Btn(text, width);
            b.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                DragFiles f = null;
                try { f = files(); }
                catch (Exception ex) { SetStatus("Drag-out failed: " + ex.Message); return; }
                StartDragOut(b, f);
            };
            return b;
        }

        // ================================================================= drag & drop (hooked on EVERY control)

        private void HookDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter -= OnDragEnter; c.DragEnter += OnDragEnter;
            c.DragOver -= OnDragEnter; c.DragOver += OnDragEnter;
            c.DragDrop -= OnDragDrop; c.DragDrop += OnDragDrop;
            foreach (Control ch in c.Controls) HookDrop(ch);
        }

        private void OnDragEnter(object s, DragEventArgs e)
        {
            if (DragOut.Active) { e.Effect = DragDropEffects.None; return; }   // our own drag-out: never re-import it
            e.Effect = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object s, DragEventArgs e)
        {
            if (DragOut.Active) return;
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            // let Explorer get its mouse back before we show dialogs
            BeginInvoke(new Action(() => OpenPaths(files)));
        }

        // ================================================================= file routing

        /// <summary>Path of a file without its extension.</summary>
        private static string StemPath(string file)
        {
            return Path.Combine(Path.GetDirectoryName(file) ?? "", Path.GetFileNameWithoutExtension(file));
        }

        /// <summary>Finds stem + ext next to a dropped file. If 'need' is not Unknown the content must be of that kind.</summary>
        private static string FindSibling(string stemPath, string ext, FileKind need)
        {
            foreach (string e in new[] { ext, ext.ToUpperInvariant() })
            {
                string p = stemPath + e;
                if (!File.Exists(p)) continue;
                if (need != FileKind.Unknown)
                {
                    try { if (AudioIO.Detect(ReadHead(p), p) != need) continue; }
                    catch { continue; }
                }
                return p;
            }
            return null;
        }

        public void OpenPaths(string[] paths)
        {
            StopRecordingQuietly();

            if (HandleSpecialDrops(paths)) return;

            var files = paths.Where(File.Exists).ToList();
            if (files.Count == 0) return;

            string hd = null, bd = null, sqPath = null, afsPath = null;
            var mids = new List<string>();
            var audio = new List<string>();
            var unknown = new List<string>();

            foreach (var f in files)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                FileKind k;
                try { k = AudioIO.Detect(ReadHead(f), f); } catch { k = FileKind.Unknown; }

                if (k == FileKind.PsfHd) { if (hd == null) hd = f; }
                else if (k == FileKind.PsfSq) { if (sqPath == null) sqPath = f; }
                else if (ext == ".bd") { if (bd == null) bd = f; }
                else if (k == FileKind.Midi) mids.Add(f);
                else if (k == FileKind.Afs) { if (afsPath == null) afsPath = f; }
                else if (AudioIO.IsAudio(k) || ext == ".wma" || ext == ".aac") audio.Add(f);
                else unknown.Add(f);
            }

            // Opening ANY ONE of .hd / .bd / .sq pulls in the same-name files from the same folder:
            // BGM = BD + HD + SQ, sound effects = BD + HD only.
            bool autoSq = false;
            string anchor = hd ?? bd ?? sqPath;
            if (anchor != null)
            {
                string stem = StemPath(anchor);
                if (hd == null) hd = FindSibling(stem, ".hd", FileKind.PsfHd);
                if (hd != null && bd == null) bd = FindSibling(stem, ".bd", FileKind.Unknown);
                if (hd != null && sqPath == null)
                {
                    sqPath = FindSibling(stem, ".sq", FileKind.PsfSq);
                    autoSq = sqPath != null;
                }
            }

            try
            {
                if (hd != null) LoadHdFile(hd, bd);
                else if (bd != null && bank != null && bank.Loaded) LoadHdFile(bank.HdPath, bd);
                else if (bd != null) MessageBox.Show(this, "Drop the matching .HD file together with the .BD (or load the HD first).", "BD file");

                if (sqPath != null) LoadSqFile(sqPath);
                foreach (var m in mids) LoadMidiFile(m);
                if (afsPath != null) LoadAfsFile(afsPath);
                if (audio.Count > 0) HandleAudioFiles(audio);
                if (unknown.Count > 0 && hd == null && sqPath == null && afsPath == null && audio.Count == 0 && mids.Count == 0 && bd == null)
                    MessageBox.Show(this, "Unknown file type:\n" + Path.GetFileName(unknown[0]) +
                        "\n\nIf this is headerless PS-ADPCM use File > Open raw PS-ADPCM...", "Cannot open");

                if (autoSq && hd != null)
                    SetStatus($"Opened HD + BD + SQ together: {Path.GetFileNameWithoutExtension(hd)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Open error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void HandleAudioFiles(List<string> audio)
        {
            var tab = tabs.SelectedTab;

            if (tab == tabPsf2 && bank != null && bank.Loaded && SelectedSampleIndex() >= 0 && audio.Count == 1)
            {
                int i = SelectedSampleIndex();
                var r = MessageBox.Show(this,
                    $"Replace PSF2 sample #{i} with\n{Path.GetFileName(audio[0])} ?\n\nYes = replace the sample (keeps the sample's original rate)\nNo = just open it in the editor",
                    "Drop audio on bank", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Yes) { ReplaceSampleFromFile(i, audio[0], true); return; }
                if (r == DialogResult.Cancel) return;
            }
            else if (tab == tabAfs && afs != null && (afs.Entries.Count > 0 || afs.Path != null))
            {
                if (audio.Count == 1 && lvAfs.SelectedIndices.Count == 1)
                {
                    int i = lvAfs.SelectedIndices[0];
                    var r = MessageBox.Show(this,
                        $"Replace AFS entry #{i} ({afs.Entries[i].FileName}) with\n{Path.GetFileName(audio[0])} ?\n\nYes = replace   No = add as NEW entry   Cancel = open in editor instead",
                        "Drop audio on archive", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Yes) { ReplaceAfsEntryWithFile(i, audio[0]); return; }
                    if (r == DialogResult.No) { AddAfsFiles(audio.ToArray()); return; }
                }
                else
                {
                    var r = MessageBox.Show(this, $"Add {audio.Count} file(s) to the AFS archive?\n\nNo = open the first one in the editor.", "Drop on archive", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Yes) { AddAfsFiles(audio.ToArray()); return; }
                    if (r == DialogResult.Cancel) return;
                }
            }
            OpenAudioIntoEditor(audio[0]);
        }

        private void OpenDlg()
        {
            using var d = new OpenFileDialog { Filter = AudioIO.OpenFilter, Multiselect = true, Title = "Open" };
            if (d.ShowDialog(this) == DialogResult.OK) OpenPaths(d.FileNames);
        }

        private void OpenRawDlg()
        {
            using var d = new OpenFileDialog { Filter = "Any file|*.*", Title = "Open raw PS-ADPCM data" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            var b = ShowRawImport(File.ReadAllBytes(d.FileName), d.FileName);
            if (b != null) SetEditorBuffer(b, d.FileName, true);
        }

        private AudioBuffer ShowRawImport(byte[] data, string name)
        {
            int rate = 44100, ch = 1, inter = 0x2000, off = 0;
            if (VagCodec.IsInterleavedHeader(data)) VagCodec.GuessVagiParams(data, out rate, out inter, out off, out ch);
            else if (VagCodec.HasHeader(data)) { off = 0x30; rate = (data[0x10] << 24) | (data[0x11] << 16) | (data[0x12] << 8) | data[0x13]; if (rate < 1000 || rate > 192000) rate = 44100; }
            using var dlg = new RawImportDialog(name, rate, ch, inter, off);
            if (dlg.ShowDialog(this) != DialogResult.OK) return null;
            try
            {
                return VagCodec.DecodeInterleaved(data, dlg.DataOffset, dlg.Channels, dlg.Interleave, dlg.Rate);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Raw import error");
                return null;
            }
        }

        private void SaveCurrent()
        {
            if (tabs.SelectedTab == tabPsf2) SaveBank(false);
            else if (tabs.SelectedTab == tabAfs) SaveAfs(false);
            else SaveAudioAs();
        }

        // ================================================================= transport

        private void TransportPlay()
        {
            StopRecordingQuietly();
            if (midi != null && midi.IsPaused) { midi.Resume(); return; }
            var w = ActiveWave;
            if (player.IsPaused && playingWave == w) { player.Resume(); return; }
            PlayWave(w);
        }

        private void TransportPause()
        {
            if (midi != null && midi.IsActive) { midi.TogglePause(); return; }
            player.TogglePause();
        }

        /// <summary>⏮ - stop playback and jump to the very start.</summary>
        private void TransportGoStart()
        {
            if (recorder.IsRecording) return;
            var w = ActiveWave;
            TransportStop();
            if (w == null) return;
            w.GoToStart();
            UpdateIdleClock();
        }

        /// <summary>⏭ - stop playback and jump to the very end.</summary>
        private void TransportGoEnd()
        {
            if (recorder.IsRecording) return;
            var w = ActiveWave;
            TransportStop();
            if (w == null) return;
            w.GoToEnd();
            UpdateIdleClock();
        }

        /// <summary>
        /// Loop rules (all tabs): when the buffer's loop is ON and "play looped" is enabled in Settings, playback follows
        /// the loop points forever. A selection then only chooses the start position - hold SHIFT on Play to hear the
        /// selection once instead. With loop OFF a selection plays once, as before.
        /// </summary>
        private void PlayWave(WaveformControl w)
        {
            var b = w?.Buffer;
            if (b == null || b.TotalFrames == 0) { SetStatus("Nothing to play."); return; }
            if (recorder.IsRecording) return;
            StopMidi();

            // The Loop checkmark is the only master switch (no Settings flag any more).
            bool loopOn = b.LoopEnabled && b.LoopEnd > b.LoopStart && b.LoopStart >= 0;
            bool shift = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;

            int start, end; bool ignoreLoop;
            bool selectionOnce = w.HasSelection && (!loopOn || shift);
            if (selectionOnce)
            {
                // a selection with Loop OFF (or Shift held): play just the selection once
                start = w.SelStart; end = w.SelEnd; ignoreLoop = true;
            }
            else
            {
                // Loop ON: play from the selection start / cursor, jump back at the loop end, repeat forever
                start = w.HasSelection ? w.SelStart : w.EditCursor;
                end = b.TotalFrames;
                ignoreLoop = false;
                if (start >= end - 1) start = 0;
            }

            if (playingWave != null && playingWave != w) playingWave.PlayPos = -1;
            playingWave = w;
            try { player.Play(b, start, end, ignoreLoop); }
            catch (Exception ex) { MessageBox.Show(this, "Playback error:\n" + ex.Message); return; }
            w.PlayPos = start;
            uiTimer.Start();

            if (loopOn && !ignoreLoop)
                SetStatus($"Playing looped ({b.LoopStart:N0} → {b.LoopEnd:N0}). It jumps back at the loop end. Stop to end.");
            else if (loopOn && selectionOnce)
                SetStatus("Playing the selection once (Shift held). Loop is ignored for this play.");
            else if (b.LoopEnabled)
                SetStatus($"Loop is ticked but the loop points are not valid ({b.LoopStart:N0} → {b.LoopEnd:N0}). Playing once.");
            else
                SetStatus("Playing once (Loop is off).");
        }

        /// <summary>
        /// Plays an SQ / MIDI song through the Windows MIDI synth (a sequence check - NOT the PS2 sound).
        /// Pause, Stop, ⏮ and the clock work on it like on audio.
        /// </summary>
        public void PlayMidiSong(SqSong song, bool loop, double startSeconds = 0)
        {
            if (song == null) return;
            if (recorder.IsRecording) return;
            TransportStop();
            try
            {
                if (midi == null) midi = new MidiPlayer();
                midi.Loop = loop;
                midi.Play(song, startSeconds);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "MIDI playback error:\n" + ex.Message, "MIDI player");
                return;
            }
            _midiShown = true;
            uiTimer.Start();
            SetStatus("Playing through the Windows MIDI synth (instruments are approximate - this checks the sequence only)." + (loop ? " Looping." : ""));
        }

        private void StopMidi()
        {
            if (midi == null) return;
            try { midi.Stop(); } catch { }
        }

        private void TransportStop()
        {
            player.Stop();
            StopMidi();
            if (playingWave != null) { playingWave.PlayPos = -1; playingWave = null; }
            uiTimer.Stop();
            _midiShown = false;
            UpdateIdleClock();
        }

        private void OnUiTick()
        {
            if (recorder.IsRecording)
            {
                lblTime.Text = "REC " + FormatClock(RecClockOffset() + recWatch.Elapsed.TotalSeconds);
                UpdateLiveRecording();
                return;
            }
            if (midi != null && midi.IsActive)
            {
                _midiShown = true;
                lblTime.Text = ClockText(midi.Position, midi.Duration);
                return;
            }
            if (_midiShown)
            {
                _midiShown = false;
                UpdateIdleClock();
            }
            if (playingWave == null) { uiTimer.Stop(); return; }
            if (player.IsActive)
            {
                int pos = player.PositionFrame;
                playingWave.PlayPos = pos;
                var b = playingWave.Buffer;
                if (b != null && b.SampleRate > 0)
                    lblTime.Text = ClockText((double)pos / b.SampleRate, (double)b.TotalFrames / b.SampleRate);
            }
            else
            {
                playingWave.PlayPos = -1;
                playingWave = null;
                uiTimer.Stop();
            }
        }

        /// <summary>When nothing plays: shows the cursor / selection start and the total length of the active tab's audio.</summary>
        /// <summary>When nothing plays: shows the cursor / selection start and the total length of the active tab's audio.</summary>
        private void UpdateIdleClock()
        {
            UpdateRateLabel();
            if (recorder.IsRecording) return;
            if (midi != null && midi.IsActive) return;
            if (playingWave != null && player.IsActive) return;
            var w = ActiveWave;
            var b = w?.Buffer;
            string t;
            if (b == null || b.TotalFrames == 0 || b.SampleRate <= 0) t = "00:00.000";
            else
            {
                int frame = w.HasSelection ? w.SelStart : w.EditCursor;
                frame = Math.Max(0, Math.Min(b.TotalFrames, frame));
                t = ClockText((double)frame / b.SampleRate, (double)b.TotalFrames / b.SampleRate);
            }
            if (lblTime.Text != t) lblTime.Text = t;
        }

        /// <summary>Bottom-right label: sample rate of the audio on screen and the recording rate. Always visible.</summary>
        private void UpdateRateLabel()
        {
            if (lblRate == null) return;
            var cfg = AppSettings.Current;
            string audio, rec = cfg.RecordRate + " Hz";
            if (recorder.IsRecording)
            {
                var st = _recStash;                         // the audio that was open before Rec
                bool has = st != null && st.TotalFrames > 0;
                audio = has ? st.SampleRate + " Hz" : "-";
                int dev = recorder.SampleRate;
                if (has && st.SampleRate != cfg.RecordRate) rec = st.SampleRate + " Hz (project rate)";
                else if (!has && dev > 0 && dev != cfg.RecordRate) rec += " (device " + dev + " Hz)";
            }
            else
            {
                var b = ActiveWave?.Buffer;
                audio = (b != null && b.TotalFrames > 0) ? b.SampleRate + " Hz" : "-";
            }
            string t = "Audio: " + audio + "   |   Rec: " + rec;
            if (lblRate.Text != t) lblRate.Text = t;
        }

        private static string ClockText(double pos, double total)
        {
            return total > 0 ? FormatClock(pos) + " / " + FormatClock(total) : FormatClock(pos);
        }

        private static string FormatClock(double t)
        {
            int m = (int)(t / 60);
            return $"{m:00}:{t - m * 60:00.000}";
        }

        // ================================================================= recording

        private void ToggleRec()
        {
            if (recorder.IsRecording) { FinishRecording(true); return; }

            try
            {
                var cfg = AppSettings.Current;
                bool loopback = cfg.RecordKind == RecordSourceKind.Loopback;
                if (!loopback && NAudio.Wave.WaveInEvent.DeviceCount == 0)
                {
                    MessageBox.Show(this, "No recording device found.\nPlug in a microphone, or choose a PC-audio (loopback) device in Settings.", "Record");
                    return;
                }
                TransportStop();
                recorder.Start();                       // uses the device chosen in Settings
                recWatch.Restart();
                bRec.Text = "⏹ Stop"; bRec.BackColor = Color.FromArgb(160, 40, 40);
                uiTimer.Start();
                string dev = string.IsNullOrEmpty(cfg.RecordDeviceName) ? "default device" : cfg.RecordDeviceName;
                SetStatus($"Recording from {dev}{(loopback ? " (PC audio)" : "")}... press Stop/Rec again to finish.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Recording error:\n" + ex.Message + "\n\nCheck Settings > Recording device.", "Record");
            }
        }

        /// <summary>Any other action (New, Open, drop, effects...) stops a running recording without asking anything.</summary>
        private void StopRecordingQuietly() { FinishRecording(false); }

        /// <summary>
        /// Stops the recording. Empty editor: the recording becomes the audio at the Rec rate from Settings.
        /// Audio already open: the recording is converted to that audio's rate and inserted at the line (or at the start
        /// of the selection). The view (zoom + position) stays exactly as it was while recording. No questions.
        /// </summary>
        private void FinishRecording(bool interactive)
        {
            if (!recorder.IsRecording) return;
            var rec = StopRecorderAndRestore();
            recWatch.Stop();
            bRec.Text = "⏺ Rec"; bRec.BackColor = Ui.Btn1;
            uiTimer.Stop();
            lblTime.Text = "00:00.000";
            if (rec == null || rec.TotalFrames == 0) { SetStatus("Nothing recorded."); return; }

            int wantRate = AppSettings.Current.RecordRate;
            int deviceRate = rec.SampleRate;
            double secs = rec.DurationSeconds;

            var target = wave.Buffer;
            bool empty = target == null || target.TotalFrames == 0;
            int outRate = empty ? wantRate : target.SampleRate;     // existing audio keeps its own rate so the insert stays in time

            if (rec.SampleRate != outRate)
            {
                Cursor = Cursors.WaitCursor;
                try { rec = rec.Resample(outRate); }
                finally { Cursor = Cursors.Default; }
            }

            if (empty)
            {
                SetEditorBuffer(rec, "recording.wav", false);
                wave.SetCursor(rec.TotalFrames);        // the next recording continues after this one
            }
            else
            {
                if (rec.Channels != target.Channels) rec = rec.ToChannels(target.Channels);

                int at = Math.Max(0, Math.Min(target.TotalFrames, _recInsertAt));
                PushUndo();
                target.InsertAt(at, rec);
                wave.BufferModified();
                wave.ClearSel();
                wave.SetCursor(at + rec.TotalFrames);   // the line moves to the end of the new part
                SyncLoopUI();
            }

            // keep the view exactly as it was while recording (no fit, no reset)
            if (_viewWinSec > 0 && _viewStartSec >= 0) wave.SetViewSeconds(_viewStartSec, _viewWinSec);
            _viewFor = wave.Buffer;

            string rateTxt = deviceRate == rec.SampleRate
                ? rec.SampleRate + " Hz"
                : deviceRate + " Hz (device) → " + rec.SampleRate + " Hz";
            if (!empty && rec.SampleRate != wantRate) rateTxt += " (rate of the open audio; Rec setting is " + wantRate + " Hz)";
            SetStatus($"Recorded {secs:F2}s at {rateTxt}.");
        }
        // ================================================================= keys
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            // while recording: these keys stop the recording first, then do their normal job
            if (recorder.IsRecording)
            {
                if (k == Keys.Space) { FinishRecording(true); return true; }
                if (k == Keys.Oemplus || k == (Keys.Shift | Keys.Oemplus) || k == Keys.Add) { LiveZoom(true); return true; }
                if (k == Keys.OemMinus || k == (Keys.Shift | Keys.OemMinus) || k == Keys.Subtract) { LiveZoom(false); return true; }
                switch (k)
                {
                    case Keys.Control | Keys.N:
                    case Keys.Control | Keys.O:
                    case Keys.Control | Keys.S:
                    case Keys.Control | Keys.Shift | Keys.S:
                    case Keys.Control | Keys.X:
                    case Keys.Control | Keys.C:
                    case Keys.Control | Keys.V:
                    case Keys.Control | Keys.Z:
                    case Keys.Control | Keys.Y:
                    case Keys.Control | Keys.A:
                    case Keys.Delete:
                        StopRecordingQuietly();
                        break;
                }
            }

            if (TextInputFocused()) return base.ProcessCmdKey(ref msg, k);

            var fc = Control.FromHandle(GetFocus());
            bool inList = fc is ListView || fc is TreeView;   // lists keep Home / End for themselves

            switch (k)
            {
                case Keys.Control | Keys.X: Cut(); return true;
                case Keys.Control | Keys.C: Copy(); return true;
                case Keys.Control | Keys.V: Paste(); return true;
                case Keys.Control | Keys.A:
                    if (fc is ListView lvFocus) { SelectAllInList(lvFocus); return true; }
                    if (tabs.SelectedTab == tabEditor) SelectAllEditor(); else ActiveWave?.SelectAll();
                    return true;
                case Keys.Control | Keys.Z: Undo(); return true;
                case Keys.Control | Keys.Y: Redo(); return true;
                case Keys.Delete:
                    if (tabs.SelectedTab == tabEditor) { DeleteSel(); return true; }
                    break;
                case Keys.Space:
                    if (midi != null && midi.IsActive)
                    {
                        if (midi.IsPaused) midi.Resume(); else TransportStop();
                        return true;
                    }
                    if (player.IsPlaying) { if (playingWave == ActiveWave) TransportStop(); else PlayWave(ActiveWave); }
                    else if (player.IsPaused) player.Resume();
                    else PlayWave(ActiveWave);
                    return true;
                case Keys.Oemplus:
                case Keys.Add: ActiveWave?.ZoomIn(); return true;
                case Keys.OemMinus:
                case Keys.Subtract: ActiveWave?.ZoomOut(); return true;
                case Keys.Home:
                    if (inList) break;
                    TransportGoStart(); return true;
                case Keys.End:
                    if (inList) break;
                    TransportGoEnd(); return true;
            }
            return base.ProcessCmdKey(ref msg, k);
        }

        private void OnFormClosing(object s, FormClosingEventArgs e)
        {
            bool dirty = (bank != null && bank.Modified) || (afs != null && afs.Modified) || sqModified;
            if (dirty)
            {
                var r = MessageBox.Show(this, "You have unsaved changes in the PSF2 bank / SQ / AFS tab.\nExit without saving?", "Unsaved changes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
            }

            if (_wheelFilter != null) Application.RemoveMessageFilter(_wheelFilter);

            try { if (recorder.IsRecording) recorder.Stop(); } catch { }
            try { StopMidi(); midi?.Dispose(); } catch { }
            try { player.Dispose(); renderCts?.Cancel(); } catch { }
        }

        private void ShowNotes()
        {
            MessageBox.Show(this,
                "LOOPS\n" +
                " • The Loop checkbox is the master switch for ALL formats. Unchecked = NO loop is written (VAG flags, ADX loop header, WAV smpl chunk).\n" +
                " • Drag the green/red flags on the waveform, or type frame numbers. Snap: 28 = VAG blocks, 32 = ADX frames.\n" +
                " • VAG/ADX need block-aligned loops; they are snapped automatically on save.\n" +
                " • Play repeats the loop in every tab when Loop is ticked. With a selection, Play starts at the selection; hold Shift on Play to hear only the selection.\n\n" +
                "PSF2 TAB\n" +
                " • Drop or open ANY of the .HD / .BD / .SQ - the other files with the same name in the same folder are loaded too. Then press Play Song.\n" +
                " • Songs are rendered through the HD programs / splits / ADSR using the BD samples. If the SQ has loop markers (CC#99) the render loops.\n" +
                " • Replace a sample, then File > Save HD + BD (a .bak of the originals is made once, if enabled in Settings).\n" +
                " • Drag samples / the 'Drag out' buttons to Explorer to export files.\n\n" +
                "AFS TAB\n" +
                " • Click, Shift+click and Ctrl+click select several entries; Ctrl+A selects all. Extract, Remove and drag-out act on every selected entry.\n" +
                " • Right-click an entry: extract, replace (auto-converts audio to the entry's format), rename, add, remove. Save AFS rebuilds the archive with 0x800 alignment.\n\n" +
                "TRANSPORT\n" +
                " • ⏮ / Home = very start, ⏭ / End = very end. Space = play / stop.\n\n" +
                "DRAG & DROP DOES NOT WORK? Windows blocks drops into an app that is not elevated if Visual Studio / the app runs as Administrator. Run both normally.",
                "Quick notes");
        }
    }
}
