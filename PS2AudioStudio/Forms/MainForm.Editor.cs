using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using PS2AudioStudio.Audio;
using PS2AudioStudio.Controls;
using DragFiles = System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, System.Func<byte[]>>>;

namespace PS2AudioStudio
{
    public partial class MainForm
    {
        private WaveformControl wave;
        private Label lblInfo;
        private CheckBox chkLoop;
        private NumericUpDown numLS, numLE;
        private ComboBox cmbSnap;
        private Button bLoopSel, bLS, bLE;
        private AudioBuffer clip;
        private string editorName;
        private bool _syncing;

        private readonly List<AudioBuffer> undo = new List<AudioBuffer>();
        private readonly List<AudioBuffer> redo = new List<AudioBuffer>();
        private const int MaxUndo = 12;

        // ================================================================= tab
        private TabPage BuildEditorTab()
        {
            var p = new TabPage("🎵 Audio Editor");

            var tb = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Ui.Bar,
                Padding = new Padding(3),
                WrapContents = true
            };

            chkLoop = new CheckBox { Text = "🔁 Loop", ForeColor = Color.Lime, AutoSize = true, Margin = new Padding(6, 7, 6, 0), Font = new Font("Segoe UI", 9, FontStyle.Bold) };
            chkLoop.CheckedChanged += (s, e) => OnLoopCheckChanged();

            numLS = new NumericUpDown { Maximum = 1, Width = 95, BackColor = Ui.Edit, ForeColor = Color.Lime, Margin = new Padding(2, 5, 2, 0) };
            numLE = new NumericUpDown { Maximum = 1, Width = 95, BackColor = Ui.Edit, ForeColor = Color.Tomato, Margin = new Padding(2, 5, 2, 0) };
            numLS.ValueChanged += (s, e) => OnLoopNumChanged(true);
            numLE.ValueChanged += (s, e) => OnLoopNumChanged(false);

            bLoopSel = Ui.Btn("Loop ← Selection"); bLoopSel.Click += (s, e) => { if (wave.HasSelection) SetLoop(wave.SelStart, wave.SelEnd); else SetStatus("Select a region first."); };
            bLS = Ui.Btn("Start ← Cursor"); bLS.Click += (s, e) => SetLoopStartAt(wave.EditCursor);
            bLE = Ui.Btn("End ← Cursor"); bLE.Click += (s, e) => SetLoopEndAt(wave.EditCursor);

            cmbSnap = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Margin = new Padding(2, 5, 2, 0), BackColor = Ui.Edit, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            cmbSnap.Items.AddRange(new object[] { "Free", "28  (VAG blocks)", "32  (ADX frames)" });
            cmbSnap.SelectedIndex = 0;
            cmbSnap.SelectedIndexChanged += (s, e) =>
            {
                wave.LoopSnap = cmbSnap.SelectedIndex == 1 ? 28 : cmbSnap.SelectedIndex == 2 ? 32 : 0;
                if (wave.Buffer != null && wave.Buffer.LoopEnabled && wave.LoopSnap > 1)
                {
                    wave.Buffer.SnapLoop(wave.LoopSnap); SyncLoopUI(); wave.Invalidate(); wave.UpdateInfo();
                }
            };

            var bZi = Ui.Btn("🔍+", 44); bZi.Click += (s, e) => wave.ZoomIn();
            var bZo = Ui.Btn("🔍−", 44); bZo.Click += (s, e) => wave.ZoomOut();
            var bZf = Ui.Btn("Fit", 44); bZf.Click += (s, e) => wave.ZoomFull();
            var bZs = Ui.Btn("Sel", 44); bZs.Click += (s, e) => wave.ZoomToSelection();

            tb.Controls.Add(chkLoop);
            tb.Controls.Add(Ui.Lbl("Start:")); tb.Controls.Add(numLS);
            tb.Controls.Add(Ui.Lbl("End:")); tb.Controls.Add(numLE);
            tb.Controls.Add(bLoopSel); tb.Controls.Add(bLS); tb.Controls.Add(bLE);
            tb.Controls.Add(Ui.Lbl("Snap:")); tb.Controls.Add(cmbSnap);
            tb.Controls.Add(Ui.Lbl("   Zoom:")); tb.Controls.AddRange(new Control[] { bZi, bZo, bZf, bZs });
            tb.Controls.Add(Ui.Lbl("   Drag out (press & drag to Explorer):"));

            lblInfo = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                BackColor = Ui.Panel,
                ForeColor = Color.LightGreen,
                Font = new Font("Consolas", 9),
                Padding = new Padding(5, 4, 0, 0)
            };

            wave = new WaveformControl { Dock = DockStyle.Fill, InfoLabel = lblInfo };
            wave.LoopChanged += (s, e) => SyncLoopUI();
            wave.ContextMenuStrip = BuildWaveMenu();

            p.Controls.Add(wave);
            p.Controls.Add(lblInfo);
            p.Controls.Add(tb);
            lblInfo.Text = "No audio loaded - File > New, File > Open, or drop a file here.";
            SyncLoopUI();
            return p;
        }

        private void InitEditorSplitters() { }

        private ContextMenuStrip BuildWaveMenu()
        {
            var m = Ui.Ctx();
            m.Items.Add(Mi("Set loop START here", (s, e) => SetLoopStartAt(wave.ContextFrame)));
            m.Items.Add(Mi("Set loop END here", (s, e) => SetLoopEndAt(wave.ContextFrame)));
            m.Items.Add(Mi("Loop = selection", (s, e) => { if (wave.HasSelection) SetLoop(wave.SelStart, wave.SelEnd); }));
            m.Items.Add(Mi("Disable loop", (s, e) => chkLoop.Checked = false));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Mi("Cut", (s, e) => Cut()));
            m.Items.Add(Mi("Copy", (s, e) => Copy()));
            m.Items.Add(Mi("Paste", (s, e) => Paste()));
            m.Items.Add(Mi("Delete", (s, e) => DeleteSel()));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Mi("Select all", (s, e) => wave.SelectAll()));
            m.Items.Add(Mi("Zoom to selection", (s, e) => wave.ZoomToSelection()));
            m.Items.Add(Mi("Zoom to fit", (s, e) => wave.ZoomFull()));
            return m;
        }

        // ================================================================= drag out

        private DragFiles EditorDragFiles(string ext)
        {
            var b = wave.Buffer;
            if (b == null || b.TotalFrames == 0) return null;

            var c = b.Clone();
            c.LoopEnabled = chkLoop.Checked && c.LoopEnd > c.LoopStart && c.LoopStart >= 0;
            string baseName = SafeFile(string.IsNullOrEmpty(editorName) ? "audio" : Path.GetFileNameWithoutExtension(editorName), "audio");
            string name = baseName + ext;

            Func<byte[]> make;
            if (ext == ".wav") make = () => WavBytes(c);
            else if (ext == ".adx")
                make = () =>
                {
                    if (c.LoopEnabled) c.SnapLoop(32);
                    return AudioIO.Encode(c, FileKind.Adx, name);
                };
            else
                make = () =>
                {
                    var m = c;
                    if (c.Channels > 1)
                    {
                        m = c.ToChannels(1);
                        m.LoopEnabled = c.LoopEnabled; m.LoopStart = c.LoopStart; m.LoopEnd = c.LoopEnd;
                    }
                    if (m.LoopEnabled) m.SnapLoop(VagCodec.SamplesPerBlock);
                    return AudioIO.Encode(m, FileKind.Vag, name);
                };

            var list = new DragFiles();
            Drag(list, name, make);
            return list;
        }

        // ================================================================= load / new

        private void SetEditorBuffer(AudioBuffer b, string name, bool switchTab = true)
        {
            undo.Clear(); redo.Clear();
            editorName = name;
            wave.Buffer = b;

            string ext = Path.GetExtension(name ?? "").ToLowerInvariant();
            cmbSnap.SelectedIndex = ext == ".vag" ? 1 : ext == ".adx" ? 2 : 0;

            SyncLoopUI();
            if (switchTab) tabs.SelectedTab = tabEditor;
            UpdateTitle();
            wave.UpdateInfo();
            if (b != null)
                SetStatus($"{Path.GetFileName(name)}: {b.SampleRate} Hz, {b.Channels} ch, {b.DurationSeconds:F3}s" +
                          (b.LoopEnabled ? $", loop {b.LoopStart}→{b.LoopEnd}" : ", no loop"));
        }

        private void ClearEditorTab()
        {
            undo.Clear(); redo.Clear();
            editorName = null;
            wave.Buffer = null;
            SyncLoopUI();
            lblInfo.Text = "No audio loaded - File > New, File > Open, or drop a file here.";
            UpdateTitle();
        }

        /// <summary>File > New. Which tabs are cleared depends on Settings (active tab only / all tabs / custom).</summary>
        private void NewProject()
        {
            var cfg = AppSettings.Current;
            int active = tabs.SelectedIndex;
            bool clrEd = cfg.ShouldClear(0, active);
            bool clrPs = cfg.ShouldClear(1, active);
            bool clrAf = cfg.ShouldClear(2, active);

            // the editor becomes a silent project only when it is the tab you are on; otherwise it is simply emptied
            bool silent = clrEd && active == 0;
            double secs = 5;
            if (silent)
            {
                if (!Prompt.Number(this, "New project", "Length of the new silent project (seconds):", 5, out secs)) return;
                secs = Math.Max(0.1, Math.Min(3600, secs));
            }

            bool dirty = (clrPs && ((bank != null && bank.Modified) || sqModified)) || (clrAf && afs != null && afs.Modified);
            if (dirty)
            {
                var r = MessageBox.Show(this, "The tab(s) you are about to clear have unsaved changes (PSF2 bank / SQ / AFS).\nClear them anyway?",
                    "New", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }

            TransportStop();
            if (clrPs) ClearPsf2Tab();
            if (clrAf) ClearAfsTab();
            if (clrEd)
            {
                if (silent) SetEditorBuffer(new AudioBuffer(44100, 2, (int)(44100 * secs)), "untitled.wav", false);
                else ClearEditorTab();
            }
            UpdateTitle();

            var parts = new List<string>();
            if (clrEd) parts.Add("Audio Editor");
            if (clrPs) parts.Add("PSF2");
            if (clrAf) parts.Add("AFS");
            SetStatus("New: cleared " + string.Join(" + ", parts) + ".");
        }

        private void OpenAudioIntoEditor(string path)
        {
            AudioBuffer b;
            try { b = AudioIO.Load(path); }
            catch (RawImportRequiredException rx)
            {
                b = ShowRawImport(rx.RawData, rx.FileName);
                if (b == null) return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Cannot open {Path.GetFileName(path)}:\n{ex.Message}", "Open error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            SetEditorBuffer(b, path);
        }

        /// <summary>Loads an audio file for replacing something (bank sample / AFS entry). Returns null if cancelled.</summary>
        private AudioBuffer LoadAudioForReplace(string path)
        {
            try { return AudioIO.Load(path); }
            catch (RawImportRequiredException rx) { return ShowRawImport(rx.RawData, rx.FileName); }
        }

        // ================================================================= loop UI

        private int SnapF(int f)
        {
            int s = wave.LoopSnap;
            if (s > 1) f = (int)Math.Round(f / (double)s) * s;
            return f;
        }

        private void SyncLoopUI()
        {
            if (numLS == null) return;
            _syncing = true;
            try
            {
                var b = wave.Buffer;
                bool has = b != null && b.TotalFrames > 0;
                chkLoop.Enabled = has;
                chkLoop.Checked = has && b.LoopEnabled;
                bool on = has && b.LoopEnabled;

                decimal max = Math.Max(1, has ? b.TotalFrames : 1);
                numLS.Maximum = max; numLE.Maximum = max;
                if (has)
                {
                    numLS.Value = Math.Max(0, Math.Min(max, b.LoopStart < 0 ? 0 : b.LoopStart));
                    numLE.Value = Math.Max(0, Math.Min(max, b.LoopEnd < 0 ? b.TotalFrames : b.LoopEnd));
                }
                else { numLS.Value = 0; numLE.Value = 0; }

                numLS.Enabled = numLE.Enabled = bLoopSel.Enabled = bLS.Enabled = bLE.Enabled = on;
            }
            finally { _syncing = false; }
        }

        private void OnLoopCheckChanged()
        {
            if (_syncing) return;
            var b = wave.Buffer;
            if (b == null || b.TotalFrames == 0) { _syncing = true; chkLoop.Checked = false; _syncing = false; return; }

            b.LoopEnabled = chkLoop.Checked;
            if (b.LoopEnabled && (b.LoopStart < 0 || b.LoopEnd <= b.LoopStart || b.LoopEnd > b.TotalFrames))
            {
                if (wave.HasSelection) { b.LoopStart = SnapF(wave.SelStart); b.LoopEnd = SnapF(wave.SelEnd); }
                else { b.LoopStart = 0; b.LoopEnd = b.TotalFrames; }
            }
            if (b.LoopEnabled && wave.LoopSnap > 1) b.SnapLoop(wave.LoopSnap);
            b.ClampLoopPoints();
            SyncLoopUI();
            wave.Invalidate(); wave.UpdateInfo();
            SetStatus(b.LoopEnabled ? "Loop ON - Play repeats it; it will be written to VAG / ADX / WAV on save." : "Loop OFF - no loop data will be written on save.");
        }

        private void OnLoopNumChanged(bool start)
        {
            if (_syncing) return;
            var b = wave.Buffer; if (b == null || !b.LoopEnabled) return;
            if (start)
            {
                b.LoopStart = Math.Max(0, Math.Min(SnapF((int)numLS.Value), b.TotalFrames - 1));
                if (b.LoopEnd <= b.LoopStart) b.LoopEnd = Math.Min(b.TotalFrames, b.LoopStart + 1);
            }
            else
            {
                b.LoopEnd = Math.Min(b.TotalFrames, Math.Max(SnapF((int)numLE.Value), b.LoopStart + 1));
            }
            SyncLoopUI(); wave.Invalidate(); wave.UpdateInfo();
        }

        private void SetLoop(int s, int e)
        {
            var b = wave.Buffer; if (b == null) return;
            s = Math.Max(0, Math.Min(b.TotalFrames - 1, SnapF(s)));
            e = Math.Max(s + 1, Math.Min(b.TotalFrames, SnapF(e)));
            b.LoopEnabled = true; b.LoopStart = s; b.LoopEnd = e;
            SyncLoopUI(); wave.Invalidate(); wave.UpdateInfo();
        }

        private void SetLoopStartAt(int frame)
        {
            var b = wave.Buffer; if (b == null) return;
            int e = b.LoopEnabled && b.LoopEnd > frame ? b.LoopEnd : b.TotalFrames;
            SetLoop(frame, e);
        }

        private void SetLoopEndAt(int frame)
        {
            var b = wave.Buffer; if (b == null) return;
            int s = b.LoopEnabled && b.LoopStart < frame ? b.LoopStart : 0;
            SetLoop(s, frame);
        }

        // ================================================================= undo / redo

        private void PushUndo()
        {
            var b = wave.Buffer; if (b == null) return;
            undo.Add(b.Clone());
            if (undo.Count > MaxUndo) undo.RemoveAt(0);
            redo.Clear();
        }

        private void Undo()
        {
            if (undo.Count == 0 || wave.Buffer == null) { SetStatus("Nothing to undo."); return; }
            redo.Add(wave.Buffer.Clone());
            var b = undo[undo.Count - 1]; undo.RemoveAt(undo.Count - 1);
            wave.SetBuffer(b, true); SyncLoopUI();
            SetStatus("Undo.");
        }

        private void Redo()
        {
            if (redo.Count == 0 || wave.Buffer == null) { SetStatus("Nothing to redo."); return; }
            undo.Add(wave.Buffer.Clone());
            var b = redo[redo.Count - 1]; redo.RemoveAt(redo.Count - 1);
            wave.SetBuffer(b, true); SyncLoopUI();
            SetStatus("Redo.");
        }

        // ================================================================= clipboard

        private void Copy()
        {
            var w = ActiveWave;
            if (w?.Buffer == null || !w.HasSelection) { SetStatus("Select a region to copy."); return; }
            clip = w.Buffer.GetRegion(w.SelStart, w.SelEnd);
            SetStatus($"Copied {clip.DurationSeconds:F3}s.");
        }

        private void Cut()
        {
            if (tabs.SelectedTab != tabEditor) { Copy(); return; }
            if (wave.Buffer == null || !wave.HasSelection) return;
            PushUndo();
            clip = wave.Buffer.CutRegion(wave.SelStart, wave.SelEnd);
            int at = wave.SelStart;
            wave.BufferModified(); wave.ClearSel(); wave.SetCursor(at); SyncLoopUI();
        }

        private void Paste()
        {
            if (clip == null) { SetStatus("Clipboard is empty."); return; }
            if (wave.Buffer == null) { SetEditorBuffer(clip.Clone(), "pasted.wav"); return; }
            PushUndo();
            int at = wave.EditCursor;
            if (wave.HasSelection) { at = wave.SelStart; wave.Buffer.DeleteRegion(wave.SelStart, wave.SelEnd); }
            wave.Buffer.InsertAt(at, clip);
            wave.BufferModified(); wave.ClearSel(); wave.SetCursor(at + clip.TotalFrames); SyncLoopUI();
        }

        private void DeleteSel()
        {
            if (wave.Buffer == null || !wave.HasSelection) return;
            PushUndo();
            int at = wave.SelStart;
            wave.Buffer.DeleteRegion(wave.SelStart, wave.SelEnd);
            wave.BufferModified(); wave.ClearSel(); wave.SetCursor(at); SyncLoopUI();
        }

        private void SelectAllEditor() { wave.SelectAll(); }

        // ================================================================= effects

        private bool Range(out int s, out int e)
        {
            s = e = 0;
            var b = wave.Buffer;
            if (b == null || b.TotalFrames == 0) { SetStatus("Open or create audio in the Audio Editor first."); return false; }
            if (wave.HasSelection) { s = wave.SelStart; e = wave.SelEnd; } else { s = 0; e = b.TotalFrames; }
            return e > s;
        }

        private void ApplyFx(Action<AudioBuffer, int, int> fx, string name)
        {
            if (!Range(out int s, out int e)) return;
            tabs.SelectedTab = tabEditor;
            PushUndo();
            fx(wave.Buffer, s, e);
            wave.BufferModified(); SyncLoopUI();
            SetStatus(name + (wave.HasSelection ? " (selection)" : " (whole file)"));
        }

        private void FxFadeIn() => ApplyFx((b, s, e) => b.FadeIn(s, e), "Fade in");
        private void FxFadeOut() => ApplyFx((b, s, e) => b.FadeOut(s, e), "Fade out");
        private void FxReverse() => ApplyFx((b, s, e) => b.Reverse(s, e), "Reverse");
        private void FxInvert() => ApplyFx((b, s, e) => b.Invert(s, e), "Invert");
        private void FxSilence() { if (wave.HasSelection) ApplyFx((b, s, e) => b.Silence(s, e), "Silence"); else SetStatus("Select a region first."); }
        private void FxNormalize() => ApplyFx((b, s, e) =>
        {
            float peak = 0;
            for (int i = s * b.Channels; i < e * b.Channels; i++) peak = Math.Max(peak, Math.Abs(b.Samples[i]));
            if (peak > 0.00001f) b.Amplify(1f / peak, s, e);
        }, "Normalize");

        private void FxAmplify()
        {
            if (!Range(out _, out _)) return;
            if (!Prompt.Number(this, "Amplify", "Gain in dB (negative = quieter):", 3, out double db)) return;
            float g = (float)Math.Pow(10, db / 20.0);
            ApplyFx((b, s, e) => b.Amplify(g, s, e), $"Amplify {db:+0.0;-0.0} dB");
        }

        private void FxCrop()
        {
            if (wave.Buffer == null || !wave.HasSelection) { SetStatus("Select the region to keep first."); return; }
            PushUndo();
            var keep = wave.Buffer.GetRegion(wave.SelStart, wave.SelEnd);
            keep.LoopEnabled = false;
            wave.SetBuffer(keep, false); SyncLoopUI();
        }

        private void FxResample()
        {
            var b = wave.Buffer;
            if (b == null || b.TotalFrames == 0) { SetStatus("Open audio in the editor first."); return; }
            if (!Prompt.Number(this, "Resample", $"New sample rate in Hz (current {b.SampleRate}).\nPS2 typical: 11025, 22050, 32000, 44100, 48000", 22050, out double hz)) return;
            int rate = (int)Math.Max(1000, Math.Min(192000, hz));
            tabs.SelectedTab = tabEditor;
            PushUndo();
            Cursor = Cursors.WaitCursor;
            try { wave.SetBuffer(b.Resample(rate), false); }
            finally { Cursor = Cursors.Default; }
            SyncLoopUI();
            SetStatus($"Resampled to {rate} Hz.");
        }

        private void FxMono()
        {
            var b = wave.Buffer;
            if (b == null || b.Channels == 1) { SetStatus("Already mono."); return; }
            PushUndo();
            wave.SetBuffer(b.ToChannels(1), true); SyncLoopUI();
        }

        private void FxGenSilence()
        {
            if (wave.Buffer == null) { NewProject(); return; }
            if (!Prompt.Number(this, "Generate silence", "Seconds of silence to insert at the cursor:", 1, out double secs)) return;
            PushUndo();
            var sil = new AudioBuffer(wave.Buffer.SampleRate, wave.Buffer.Channels, (int)(wave.Buffer.SampleRate * Math.Max(0.001, secs)));
            wave.Buffer.InsertAt(wave.EditCursor, sil);
            wave.BufferModified(); SyncLoopUI();
        }

        // ================================================================= save audio

        private void SaveAudioAs()
        {
            var b = wave.Buffer;
            if (b == null || b.TotalFrames == 0) { MessageBox.Show(this, "Nothing to save - open or record some audio first.", "Save"); return; }
            tabs.SelectedTab = tabEditor;

            string baseName = string.IsNullOrEmpty(editorName) ? "audio" : Path.GetFileNameWithoutExtension(editorName);
            using var d = new SaveFileDialog
            {
                Title = "Save audio as",
                FileName = baseName,
                AddExtension = true,
                OverwritePrompt = true,
                Filter = "VAG (PS1/PS2 ADPCM, mono)|*.vag|ADX (CRI ADPCM)|*.adx|WAV (PCM 16-bit)|*.wav|MP3|*.mp3|M4A (AAC)|*.m4a|Raw interleaved PS-ADPCM (no header)|*.bin"
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            SaveAudioTo(b, d.FileName);
        }

        private void SaveAudioTo(AudioBuffer b, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            // the checkbox is the single source of truth
            b.LoopEnabled = chkLoop.Checked && b.LoopEnd > b.LoopStart && b.LoopStart >= 0;

            // ---- choose the sample rate (every format except raw .bin) ----
            AudioBuffer src = b;
            if (ext == ".vag" || ext == ".adx" || ext == ".wav" || ext == ".mp3" || ext == ".m4a" || ext == ".aac")
            {
                src = SaveRateDialog.Ask(this, b, ext.TrimStart('.').ToUpperInvariant());
                if (src == null) return;      // cancelled
            }

            string msg;
            try
            {
                Cursor = Cursors.WaitCursor;
                switch (ext)
                {
                    case ".vag":
                        {
                            if (src.Channels > 1)
                            {
                                var r = MessageBox.Show(this,
                                    "VAG files are mono.\n\nYes = mix down to mono and save .vag\nNo = save RAW INTERLEAVED stereo PS-ADPCM (.bin, no header)\nCancel = abort",
                                    "Stereo → VAG", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                                if (r == DialogResult.Cancel) return;
                                if (r == DialogResult.No) { SaveRawInterleaved(src, Path.ChangeExtension(path, ".bin")); return; }
                            }
                            if (b.LoopEnabled) { b.SnapLoop(VagCodec.SamplesPerBlock); SyncLoopUI(); wave.Invalidate(); }
                            File.WriteAllBytes(path, AudioIO.Encode(src, FileKind.Vag, path));
                            msg = "VAG";
                            break;
                        }
                    case ".adx":
                        {
                            if (b.LoopEnabled) { b.SnapLoop(32); SyncLoopUI(); wave.Invalidate(); }
                            File.WriteAllBytes(path, AudioIO.Encode(src, FileKind.Adx, path));
                            msg = "ADX";
                            break;
                        }
                    case ".wav":
                        StandardCodec.EncodeWav(src, path);
                        msg = "WAV";
                        break;
                    case ".mp3":
                        StandardCodec.ExportCompressed(src, path, true);
                        msg = "MP3 (no loop info in MP3)";
                        break;
                    case ".m4a":
                    case ".aac":
                        StandardCodec.ExportCompressed(src, path, false);
                        msg = "M4A/AAC (no loop info in M4A)";
                        break;
                    case ".bin":
                    case ".raw":
                        SaveRawInterleaved(b, path);
                        return;
                    default:
                        MessageBox.Show(this, "Use .vag, .adx, .wav, .mp3, .m4a or .bin as the file extension.", "Save");
                        return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save error:\n" + ex.Message, "Save", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finally { Cursor = Cursors.Default; }

            editorName = path;
            UpdateTitle();
            string rateTxt = src.SampleRate != b.SampleRate ? $" @ {src.SampleRate} Hz" : "";
            string loopTxt = src.LoopEnabled ? $"loop ON {src.LoopStart:N0} → {src.LoopEnd:N0}" : "loop OFF (none written)";
            SetStatus($"Saved {msg}{rateTxt}: {path}  |  {loopTxt}");
            MessageBox.Show(this, $"Saved {msg}{rateTxt}\n{path}\n\n{loopTxt}", "Saved");
        }

        private void SaveRawInterleaved(AudioBuffer b, string path)
        {
            if (!Prompt.Number(this, "Raw interleaved PS-ADPCM", "Interleave size in bytes (multiple of 16, e.g. 4096 / 0x1000 / 0x4000):", 4096, out double il)) return;
            if (b.LoopEnabled) b.SnapLoop(VagCodec.SamplesPerBlock);
            File.WriteAllBytes(path, VagCodec.EncodeInterleaved(b, (int)il));
            SyncLoopUI(); wave.Invalidate();
            SetStatus($"Saved raw interleaved PS-ADPCM ({b.Channels} ch, interleave {(int)il}): {path}");
            MessageBox.Show(this, $"Saved raw PS-ADPCM\n{path}\n{b.Channels} channel(s), interleave {(int)il} bytes.", "Saved");
        }
    }
}