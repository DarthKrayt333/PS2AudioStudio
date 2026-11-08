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

        private TabControl tabs;
        private TabPage tabEditor, tabPsf2, tabAfs;
        private Label lblStatus, lblTime;
        private TrackBar trkVol;
        private Button bRec, bPlay, bPause, bStop;
        private WaveformControl playingWave;
        private bool _boxSync;

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
            Shown += (s, e) => InitSplitters();
            FormClosing += OnFormClosing;
            tabs.SelectedIndexChanged += (s, e) => UpdateTitle();
            SetStatus("Ready. Drag & drop VAG / ADX / WAV / MP3 / M4A / AFS / HD / BD / SQ / MID files anywhere in this window.");
        }

        // ================================================================= UI

        private void BuildUI()
        {
            // ---- transport (bottom, always visible, acts on the active tab) ----
            var transport = new Panel { Dock = DockStyle.Bottom, Height = 74, BackColor = Ui.Panel };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6, 6, 6, 2), BackColor = Ui.Panel };

            bPlay = Ui.Btn("▶ Play", 80); bPlay.Click += (s, e) => TransportPlay();
            bPause = Ui.Btn("⏸ Pause", 80); bPause.Click += (s, e) => player.TogglePause();
            bStop = Ui.Btn("⏹ Stop", 80); bStop.Click += (s, e) => TransportStop();
            bRec = Ui.Btn("⏺ Rec", 80); bRec.Click += (s, e) => ToggleRec();

            trkVol = new TrackBar { Width = 130, Minimum = 0, Maximum = 100, Value = 80, TickFrequency = 25, AutoSize = false, Height = 28, Margin = new Padding(14, 2, 0, 0) };
            var lblVol = new Label { Text = "Vol 80%", AutoSize = true, ForeColor = Color.LightGreen, Margin = new Padding(2, 8, 10, 0) };
            trkVol.ValueChanged += (s, e) => { player.Volume = trkVol.Value / 100f; lblVol.Text = $"Vol {trkVol.Value}%"; };
            player.Volume = 0.8f;

            lblTime = new Label { Text = "00:00.000", AutoSize = true, Font = new Font("Consolas", 14, FontStyle.Bold), ForeColor = Color.Lime, Margin = new Padding(12, 3, 0, 0) };

            flow.Controls.AddRange(new Control[] { bPlay, bPause, bStop, bRec, trkVol, lblVol, lblTime });

            lblStatus = new Label { Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, Padding = new Padding(8, 4, 4, 2), AutoEllipsis = true };
            transport.Controls.Add(lblStatus);
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
            file.DropDownItems.Add(Mi("Open...  (audio / AFS / HD / SQ / MIDI)", (s, e) => OpenDlg(), Keys.Control | Keys.O));
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

            var view = new ToolStripMenuItem("&View");
            view.DropDownItems.Add(Mi("Zoom in", (s, e) => ActiveWave?.ZoomIn(), Keys.None, "Ctrl+Wheel / +"));
            view.DropDownItems.Add(Mi("Zoom out", (s, e) => ActiveWave?.ZoomOut(), Keys.None, "Ctrl+Wheel / -"));
            view.DropDownItems.Add(Mi("Zoom to fit (whole file)", (s, e) => ActiveWave?.ZoomFull()));
            view.DropDownItems.Add(Mi("Zoom to selection", (s, e) => ActiveWave?.ZoomToSelection()));

            var tr = new ToolStripMenuItem("&Transport");
            tr.DropDownItems.Add(Mi("Play", (s, e) => TransportPlay()));
            tr.DropDownItems.Add(Mi("Pause / resume", (s, e) => player.TogglePause()));
            tr.DropDownItems.Add(Mi("Stop", (s, e) => TransportStop()));
            tr.DropDownItems.Add(Mi("Record", (s, e) => ToggleRec()));

            var settings = new ToolStripMenuItem("&Settings");
            settings.DropDownItems.Add(Mi("⚙ Settings...  (recording device, File > New behaviour, looping)", (s, e) => ShowSettings()));

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(Mi("Loop points & formats - quick notes", (s, e) => ShowNotes()));
            help.DropDownItems.Add(Mi("About", (s, e) => MessageBox.Show(this,
                "PS2 Audio Studio\nVAG / ADX / WAV / MP3 / M4A editor, AFS archive manager and PSF2 (HD/BD/SQ) bank player.\n\nMade by DarthKrayt333",
                "About")));

            m.Items.AddRange(new ToolStripItem[] { file, edit, fx, view, tr, settings, help });
            return m;
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

        public void OpenPaths(string[] paths)
        {
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
            var w = ActiveWave;
            if (player.IsPaused && playingWave == w) { player.Resume(); return; }
            PlayWave(w);
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

            bool allowLoop = AppSettings.Current.PlayLoopedWhenChecked;
            bool loopOn = allowLoop && b.LoopEnabled && b.LoopEnd > b.LoopStart && b.LoopStart >= 0;
            bool shift = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;

            int start, end; bool ignoreLoop;
            if (w.HasSelection && (!loopOn || shift))
            {
                start = w.SelStart; end = w.SelEnd; ignoreLoop = true;
            }
            else
            {
                start = w.HasSelection ? w.SelStart : w.EditCursor;
                end = b.TotalFrames;
                ignoreLoop = !allowLoop;
                if (start >= end - 1) start = 0;
            }

            if (playingWave != null && playingWave != w) playingWave.PlayPos = -1;
            playingWave = w;
            try { player.Play(b, start, end, ignoreLoop); }
            catch (Exception ex) { MessageBox.Show(this, "Playback error:\n" + ex.Message); return; }
            w.PlayPos = start;
            uiTimer.Start();
            if (loopOn && !ignoreLoop) SetStatus($"Playing looped ({b.LoopStart:N0} → {b.LoopEnd:N0}). Stop to end.");
        }

        private void TransportStop()
        {
            player.Stop();
            if (playingWave != null) { playingWave.PlayPos = -1; playingWave = null; }
            uiTimer.Stop();
            lblTime.Text = "00:00.000";
        }

        private void OnUiTick()
        {
            if (recorder.IsRecording)
            {
                lblTime.Text = "REC " + FormatClock(recWatch.Elapsed.TotalSeconds);
                return;
            }
            if (playingWave == null) { uiTimer.Stop(); return; }
            if (player.IsActive)
            {
                int pos = player.PositionFrame;
                playingWave.PlayPos = pos;
                var b = playingWave.Buffer;
                if (b != null) lblTime.Text = FormatClock((double)pos / b.SampleRate);
            }
            else
            {
                playingWave.PlayPos = -1;
                playingWave = null;
                uiTimer.Stop();
            }
        }

        private static string FormatClock(double t)
        {
            int m = (int)(t / 60);
            return $"{m:00}:{t - m * 60:00.000}";
        }

        // ================================================================= recording

        private void ToggleRec()
        {
            if (recorder.IsRecording)
            {
                var rec = recorder.Stop();
                recWatch.Stop();
                bRec.Text = "⏺ Rec"; bRec.BackColor = Ui.Btn1;
                uiTimer.Stop();
                lblTime.Text = "00:00.000";
                if (rec == null) { SetStatus("Nothing recorded."); return; }

                tabs.SelectedTab = tabEditor;
                if (wave.Buffer == null || wave.Buffer.TotalFrames == 0)
                    SetEditorBuffer(rec, "recording.wav", false);
                else
                {
                    var r = MessageBox.Show(this, "Insert the recording at the cursor of the current audio?\n\nYes = insert   No = replace the current audio with the recording",
                        "Recording finished", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Yes) { PushUndo(); wave.Buffer.InsertAt(wave.EditCursor, rec); wave.BufferModified(); SyncLoopUI(); }
                    else if (r == DialogResult.No) SetEditorBuffer(rec, "recording.wav", false);
                }
                SetStatus($"Recorded {rec.DurationSeconds:F2}s.");
                return;
            }

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

        // ================================================================= keys

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (TextInputFocused()) return base.ProcessCmdKey(ref msg, k);

            switch (k)
            {
                case Keys.Control | Keys.X: Cut(); return true;
                case Keys.Control | Keys.C: Copy(); return true;
                case Keys.Control | Keys.V: Paste(); return true;
                case Keys.Control | Keys.A:
                    if (tabs.SelectedTab == tabEditor) SelectAllEditor(); else ActiveWave?.SelectAll();
                    return true;
                case Keys.Control | Keys.Z: Undo(); return true;
                case Keys.Control | Keys.Y: Redo(); return true;
                case Keys.Delete:
                    if (tabs.SelectedTab == tabEditor) { DeleteSel(); return true; }
                    break;
                case Keys.Space:
                    if (player.IsPlaying) { if (playingWave == ActiveWave) TransportStop(); else PlayWave(ActiveWave); }
                    else if (player.IsPaused) player.Resume();
                    else PlayWave(ActiveWave);
                    return true;
                case Keys.Oemplus:
                case Keys.Add: ActiveWave?.ZoomIn(); return true;
                case Keys.OemMinus:
                case Keys.Subtract: ActiveWave?.ZoomOut(); return true;
                case Keys.Home: ActiveWave?.SetCursor(0); ActiveWave?.ScrollToFrame(0); return true;
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
            try { if (recorder.IsRecording) recorder.Stop(); } catch { }
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
                " • Drop the .HD (the .BD with the same name is found automatically) - then drop an .SQ or .MID and press Play Song.\n" +
                " • Songs are rendered through the HD programs / splits / ADSR using the BD samples. If the SQ has loop markers (CC#99) the render loops.\n" +
                " • Replace a sample, then File > Save HD + BD (a .bak of the originals is made once, if enabled in Settings).\n" +
                " • Drag samples / the 'Drag out' buttons to Explorer to export files.\n\n" +
                "AFS TAB\n" +
                " • Right-click an entry: extract, replace (auto-converts audio to the entry's format), rename, add, remove. Save AFS rebuilds the archive with 0x800 alignment. Drag entries out to Explorer to extract them.\n\n" +
                "DRAG & DROP DOES NOT WORK? Windows blocks drops into an app that is not elevated if Visual Studio / the app runs as Administrator. Run both normally.",
                "Quick notes");
        }
    }
}
