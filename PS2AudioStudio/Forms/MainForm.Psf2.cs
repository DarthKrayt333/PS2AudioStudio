using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PS2AudioStudio.Archives;
using PS2AudioStudio.Audio;
using PS2AudioStudio.Controls;
using DragFiles = System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, System.Func<byte[]>>>;

namespace PS2AudioStudio
{
    public partial class MainForm
    {
        private sealed class SongItem
        {
            public SqSong Song; public string Label;
            public override string ToString() => Label;
        }

        private Psf2Bank bank = new Psf2Bank();
        private SqFile sq;
        private string sqPath;
        private bool sqModified;
        private readonly List<SqSong> songs = new List<SqSong>();
        private readonly Dictionary<SqSong, AudioBuffer> renderCache = new Dictionary<SqSong, AudioBuffer>();

        private ListView lvSamples;
        private TreeView tvProg;
        private WaveformControl psf2Wave;
        private Label lblPsf2Info, lblPsf2View;
        private CheckBox chkLoopPsf2;
        private ComboBox cmbSong;
        private ProgressBar pbRender;
        private Button bPlaySong, bCancelRender;
        private SplitContainer splitPsf2, splitPsf2Left;
        private CancellationTokenSource renderCts;

        // ================================================================= tab
        private TabPage BuildPsf2Tab()
        {
            var p = new TabPage("🎹 PSF2 Bank (HD / BD / SQ)");

            // ---- row 1: bank ----
            var bar1 = Ui.Bar1(40);
            var bHd = Ui.Btn("📂 Load HD (+BD)..."); bHd.Click += (s, e) => LoadHdDlg();
            var bSq = Ui.Btn("📂 Load SQ / MIDI..."); bSq.Click += (s, e) => LoadSqDlg();
            var bNewSq = Ui.Btn("🆕 New SQ from MIDI..."); bNewSq.Click += (s, e) => NewSqFromMidi();
            var bSave = Ui.Btn("💾 Save HD + BD..."); bSave.Click += (s, e) => SaveBank(true);
            var bExtAll = Ui.Btn("📁 Extract all VAG..."); bExtAll.Click += (s, e) => ExtractAllVag();
            var bPlaySel = Ui.Btn("▶ Play sample"); bPlaySel.Click += (s, e) => PlaySelectedSample();
            bar1.Controls.AddRange(new Control[] { bHd, bSq, bNewSq, bSave, bExtAll, bPlaySel });

            // ---- row 2: song ----
            var bar2 = Ui.Bar1(40);
            cmbSong = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, Margin = new Padding(2, 5, 2, 0), BackColor = Ui.Edit, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            bPlaySong = Ui.Btn("▶ Play song (through HD/BD)"); bPlaySong.Click += (s, e) => PlaySong();
            var bStopSong = Ui.Btn("⏹ Stop"); bStopSong.Click += (s, e) => { renderCts?.Cancel(); TransportStop(); };
            var bExpMidi = Ui.Btn("🎼 Extract MIDI (.mid)..."); bExpMidi.Click += (s, e) => ExportMidi();
            var bRepMidi = Ui.Btn("♻ Replace song from .mid..."); bRepMidi.Click += (s, e) => ReplaceSongFromMidi();
            var bSaveSq = Ui.Btn("💾 Save SQ..."); bSaveSq.Click += (s, e) => SaveSq();
            var bRenderEd = Ui.Btn("→ Rendered song to Editor"); bRenderEd.Click += (s, e) => SendRenderedToEditor();
            pbRender = new ProgressBar { Width = 120, Height = 18, Margin = new Padding(8, 8, 2, 0), Visible = false };
            bCancelRender = Ui.Btn("Cancel", 60); bCancelRender.Visible = false; bCancelRender.Click += (s, e) => renderCts?.Cancel();
            bar2.Controls.Add(Ui.Lbl("Song:"));
            bar2.Controls.AddRange(new Control[] { cmbSong, bPlaySong, bStopSong, bExpMidi, bRepMidi, bSaveSq, bRenderEd, pbRender, bCancelRender });

            // ---- row 3: drag out to Explorer ----
            var bar3 = Ui.Bar1(40);
            bar3.Controls.Add(Ui.Lbl("Drag out to Explorer (press & drag):"));
            bar3.Controls.Add(MakeDragHandle("⠿ Song .mid", () => SongDragFiles(false), 110));
            bar3.Controls.Add(MakeDragHandle("⠿ SQ file", () => SongDragFiles(true), 100));
            bar3.Controls.Add(MakeDragHandle("⠿ HD + BD", () => BankDragFiles(), 100));
            bar3.Controls.Add(MakeDragHandle("⠿ Rendered .wav", () => RenderedDragFiles(), 130));
            bar3.Controls.Add(Ui.Lbl("   (samples: just drag them out of the list)"));

            lblPsf2Info = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = Color.Yellow, BackColor = Ui.Panel, Padding = new Padding(6, 4, 0, 0), Font = new Font("Consolas", 9) };
            lblPsf2Info.Text = "Drop an .HD (its .BD is found automatically), then an .SQ or .MID, then press Play song.";

            // ---- body ----
            splitPsf2 = new SplitContainer { Dock = DockStyle.Fill };
            splitPsf2Left = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };

            lvSamples = new ListView { Dock = DockStyle.Fill };
            Ui.StyleList(lvSamples);
            lvSamples.Columns.Add("#", 45); lvSamples.Columns.Add("Rate", 60); lvSamples.Columns.Add("Loop", 45);
            lvSamples.Columns.Add("BD offset", 85); lvSamples.Columns.Add("Size", 75); lvSamples.Columns.Add("Length", 65); lvSamples.Columns.Add("Mod", 40);
            lvSamples.SelectedIndexChanged += (s, e) => { int i = SelectedSampleIndex(); if (i >= 0) ShowPsf2Sample(i); };
            lvSamples.DoubleClick += (s, e) => PlaySelectedSample();
            lvSamples.ItemDrag += (s, e) => DragOutSamples();
            lvSamples.ContextMenuStrip = BuildSampleMenu();

            tvProg = new TreeView { Dock = DockStyle.Fill, BackColor = Ui.Panel, ForeColor = Color.White, Font = new Font("Consolas", 9), BorderStyle = BorderStyle.None };
            tvProg.NodeMouseDoubleClick += (s, e) =>
            {
                if (e.Node.Tag is int vag)
                    foreach (ListViewItem it in lvSamples.Items)
                        if ((int)it.Tag == vag) { it.Selected = true; it.EnsureVisible(); PlaySelectedSample(); break; }
            };

            splitPsf2Left.Panel1.Controls.Add(lvSamples);
            splitPsf2Left.Panel2.Controls.Add(tvProg);

            lblPsf2View = new Label { Dock = DockStyle.Fill, ForeColor = Color.LightSkyBlue, Padding = new Padding(6, 4, 0, 0), AutoEllipsis = true, Text = "Select a sample, or press Play song to render a song." };
            var psf2Info = new Label { Dock = DockStyle.Bottom, Height = 22, BackColor = Ui.Panel, ForeColor = Color.LightGreen, Font = new Font("Consolas", 9), Padding = new Padding(5, 4, 0, 0) };
            psf2Wave = new WaveformControl { Dock = DockStyle.Fill, InfoLabel = psf2Info, EmptyText = "Select a sample (or render a song) to see its waveform" };
            chkLoopPsf2 = MakeLoopBox(psf2Wave);
            psf2Wave.LoopChanged += (s, e) => SyncLoopBox(chkLoopPsf2, psf2Wave);

            var hdr = new Panel { Dock = DockStyle.Top, Height = 24 };
            hdr.Controls.Add(lblPsf2View);
            hdr.Controls.Add(chkLoopPsf2);

            splitPsf2.Panel1.Controls.Add(splitPsf2Left);
            splitPsf2.Panel2.Controls.Add(psf2Wave);
            splitPsf2.Panel2.Controls.Add(psf2Info);
            splitPsf2.Panel2.Controls.Add(hdr);

            p.Controls.Add(splitPsf2);
            p.Controls.Add(lblPsf2Info);
            p.Controls.Add(bar3);
            p.Controls.Add(bar2);
            p.Controls.Add(bar1);
            return p;
        }

        private void InitPsf2Splitters()
        {
            splitPsf2.Panel1MinSize = 300; splitPsf2.Panel2MinSize = 300;
            splitPsf2.SplitterDistance = Math.Max(300, Math.Min(splitPsf2.Width - 300, (int)(splitPsf2.Width * 0.45)));
            splitPsf2Left.Panel1MinSize = 80; splitPsf2Left.Panel2MinSize = 60;
            splitPsf2Left.SplitterDistance = Math.Max(80, Math.Min(splitPsf2Left.Height - 60, (int)(splitPsf2Left.Height * 0.62)));
        }

        private ContextMenuStrip BuildSampleMenu()
        {
            var m = Ui.Ctx();
            m.Items.Add(Mi("▶ Play", (s, e) => PlaySelectedSample()));
            m.Items.Add(Mi("Open in Audio Editor", (s, e) => { int i = SelectedSampleIndex(); if (i >= 0) SendSampleToEditor(i); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Mi("Extract as .vag (lossless)...", (s, e) => ExtractSample(false)));
            m.Items.Add(Mi("Extract as .wav...", (s, e) => ExtractSample(true)));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Mi("Replace with file... (keep original sample rate)", (s, e) => ReplaceSelectedFromDialog(true)));
            m.Items.Add(Mi("Replace with file... (use the file's sample rate)", (s, e) => ReplaceSelectedFromDialog(false)));
            m.Items.Add(Mi("Replace with Audio Editor contents (keep rate)", (s, e) => { int i = SelectedSampleIndex(); if (i >= 0) ReplaceSampleFromEditor(i, true); }));
            m.Items.Add(Mi("Replace with Audio Editor contents (editor's rate)", (s, e) => { int i = SelectedSampleIndex(); if (i >= 0) ReplaceSampleFromEditor(i, false); }));
            m.Items.Add(Mi("Revert this sample", (s, e) => { int i = SelectedSampleIndex(); if (i >= 0) RevertSample(i); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Mi("Save HD + BD...", (s, e) => SaveBank(true)));
            return m;
        }

        private void ClearPsf2Tab()
        {
            renderCts?.Cancel();
            TransportStop();
            bank = new Psf2Bank();
            sq = null; sqPath = null; sqModified = false;
            songs.Clear();
            renderCache.Clear();
            cmbSong.Items.Clear();
            lvSamples.Items.Clear();
            tvProg.Nodes.Clear();
            psf2Wave.Buffer = null;
            SyncLoopBox(chkLoopPsf2, psf2Wave);
            lblPsf2View.Text = "Select a sample, or press Play song to render a song.";
            lblPsf2Info.Text = "Drop an .HD (its .BD is found automatically), then an .SQ or .MID, then press Play song.";
            UpdateTitle();
        }

        // ================================================================= drag out

        private void DragOutSamples()
        {
            if (!bank.Loaded) return;
            var files = new DragFiles();
            foreach (ListViewItem it in lvSamples.SelectedItems)
            {
                int i = (int)it.Tag;
                if (i < 0 || i >= bank.Samples.Count || !bank.Samples[i].Valid) continue;
                Drag(files, SafeFile(bank.SampleFileName(i)) + ".vag", () => bank.ExtractVag(i));
            }
            StartDragOut(lvSamples, files);
        }

        private string SongBaseName(SqSong song)
        {
            string b = sqPath != null ? Path.GetFileNameWithoutExtension(sqPath) : null;
            if (song != null && song.Index < 0 && !string.IsNullOrEmpty(song.Name)) b = song.Name.Replace("MIDI: ", "");
            b = SafeFile(b, "song");
            if (song != null && song.Index >= 0) b += "_" + song.Index;
            return b;
        }

        private DragFiles SongDragFiles(bool asSq)
        {
            var files = new DragFiles();
            if (asSq)
            {
                if (sq == null) { SetStatus("No SQ loaded."); return null; }
                var cur = sq;
                Drag(files, SafeFile(Path.GetFileName(sqPath ?? "song.sq")), () => cur.Data);
            }
            else
            {
                var song = SelectedSong();
                if (song == null) { SetStatus("No song selected."); return null; }
                Drag(files, SongBaseName(song) + ".mid", () => song.ToSmf());
            }
            return files;
        }

        private DragFiles BankDragFiles()
        {
            if (!bank.Loaded) { SetStatus("Load an HD + BD bank first."); return null; }
            byte[] hdBytes = null, bdBytes = null;
            void Build() { if (hdBytes == null) bank.BuildFiles(out hdBytes, out bdBytes); }
            var files = new DragFiles();
            Drag(files, Path.GetFileName(bank.HdPath), () => { Build(); return hdBytes; });
            Drag(files, Path.GetFileName(bank.BdPath), () => { Build(); return bdBytes; });
            return files;
        }

        private DragFiles RenderedDragFiles()
        {
            var song = SelectedSong();
            if (song == null || !renderCache.TryGetValue(song, out var b)) { SetStatus("Press Play song first - then the rendered audio can be dragged out."); return null; }
            var files = new DragFiles();
            Drag(files, SongBaseName(song) + ".wav", () => WavBytes(b));
            return files;
        }

        // ================================================================= loading

        private void LoadHdDlg()
        {
            using var d = new OpenFileDialog { Filter = "HD files|*.hd;*.HD|All|*.*", Title = "Select HD (the BD with the same name is auto-loaded)" };
            if (d.ShowDialog(this) == DialogResult.OK) LoadHdFile(d.FileName, null);
        }

        private void LoadSqDlg()
        {
            using var d = new OpenFileDialog { Filter = "SQ / MIDI|*.sq;*.SQ;*.mid;*.midi|All|*.*", Title = "Select SQ or MIDI file", Multiselect = true };
            if (d.ShowDialog(this) == DialogResult.OK) OpenPaths(d.FileNames);
        }

        private void LoadHdFile(string hdPath, string bdPath)
        {
            Psf2Bank nb = new Psf2Bank();
            try { nb.LoadHD(hdPath, bdPath); }
            catch (BdNotFoundException)
            {
                using var d = new OpenFileDialog { Title = "Locate the BD file for " + Path.GetFileName(hdPath), Filter = "BD / BIN|*.bd;*.bin|All|*.*" };
                if (d.ShowDialog(this) != DialogResult.OK) return;
                nb = new Psf2Bank();
                nb.LoadHD(hdPath, d.FileName);
            }

            bank = nb;
            renderCache.Clear();
            PopulateSamples();
            PopulateProgramTree();
            psf2Wave.Buffer = null;
            SyncLoopBox(chkLoopPsf2, psf2Wave);
            tabs.SelectedTab = tabPsf2;
            UpdateBankInfo();
            UpdateTitle();
            if (lvSamples.Items.Count > 0) lvSamples.Items[0].Selected = true;
            SetStatus($"Loaded {Path.GetFileName(bank.HdPath)} + {Path.GetFileName(bank.BdPath)}.");
        }

        private void UpdateBankInfo()
        {
            if (!bank.Loaded) return;
            int valid = bank.Samples.Count(s => s.Valid);
            int rep = bank.Samples.Count(s => s.Replaced);
            lblPsf2Info.Text = $"HD: {Path.GetFileName(bank.HdPath)} | BD: {Path.GetFileName(bank.BdPath)} | {valid}/{bank.Samples.Count} samples | {bank.Programs.Count} programs | " +
                               (rep > 0 ? $"{rep} replaced (unsaved)" : "unmodified") +
                               (sq != null ? $" | SQ: {(sqPath != null ? Path.GetFileName(sqPath) : "(new)")} ({sq.Songs.Count} song(s))" : "");
        }

        private int SelectedSampleIndex()
        {
            if (lvSamples == null || lvSamples.SelectedItems.Count == 0) return -1;
            return (int)lvSamples.SelectedItems[0].Tag;
        }

        private void PopulateSamples()
        {
            lvSamples.BeginUpdate();
            lvSamples.Items.Clear();
            foreach (var s in bank.Samples)
            {
                var it = new ListViewItem(new string[7]) { Tag = s.Index };
                FillRow(it, s);
                lvSamples.Items.Add(it);
            }
            lvSamples.EndUpdate();
        }

        private void FillRow(ListViewItem it, Psf2Sample s)
        {
            it.SubItems[0].Text = s.Index.ToString();
            if (!s.Valid)
            {
                for (int i = 1; i < 7; i++) it.SubItems[i].Text = i == 1 ? "(empty)" : "";
                it.ForeColor = Color.Gray;
                return;
            }
            it.SubItems[1].Text = s.SampleRate.ToString();
            it.SubItems[2].Text = s.Loops ? "LOOP" : "-";
            it.SubItems[3].Text = "0x" + s.BdOffset.ToString("X");
            it.SubItems[4].Text = s.BdSize.ToString("N0");
            it.SubItems[5].Text = s.Decoded == null ? "?" : s.Decoded.DurationSeconds.ToString("F2") + "s";
            it.SubItems[6].Text = s.Replaced ? "✱" : "";
            it.ForeColor = s.Replaced ? Color.Orange : Color.White;
        }

        private void RefreshSampleRow(int index)
        {
            foreach (ListViewItem it in lvSamples.Items)
                if ((int)it.Tag == index) { FillRow(it, bank.Samples[index]); break; }
        }

        private static string NoteName(int n)
        {
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            return names[n % 12] + (n / 12 - 1);
        }

        private void PopulateProgramTree()
        {
            tvProg.BeginUpdate();
            tvProg.Nodes.Clear();
            foreach (var prog in bank.Programs.Values.OrderBy(x => x.Index))
            {
                var pn = new TreeNode($"Program {prog.Index} (bank {prog.Index / 128} / #{prog.Index % 128})  vol {prog.Volume}  {prog.Splits.Count} split(s)");
                foreach (var sp in prog.Splits)
                {
                    var sn = new TreeNode($"Keys {NoteName(sp.RangeLow)}–{NoteName(sp.RangeHigh)}  → set {sp.SampleSetIndex}");
                    if (sp.SampleSetIndex >= 0 && sp.SampleSetIndex < bank.SampleSets.Length && bank.SampleSets[sp.SampleSetIndex] != null)
                    {
                        var set = bank.SampleSets[sp.SampleSetIndex];
                        foreach (int spi in set.SampleParams)
                        {
                            if (spi < 0 || spi >= bank.SampleParams.Length || bank.SampleParams[spi] == null) continue;
                            var par = bank.SampleParams[spi];
                            sn.Nodes.Add(new TreeNode($"VAG #{par.VagIndex}  base {NoteName(par.BaseNote)}  vel {Math.Max(set.VelLow, par.VelLow)}–{Math.Min(set.VelHigh, par.VelHigh)}") { Tag = par.VagIndex });
                        }
                    }
                    pn.Nodes.Add(sn);
                }
                tvProg.Nodes.Add(pn);
            }
            tvProg.EndUpdate();
        }

        private void ShowPsf2Sample(int i)
        {
            if (!bank.Loaded || i < 0 || i >= bank.Samples.Count) return;
            var s = bank.Samples[i];
            if (s.Decoded == null)
            {
                psf2Wave.Buffer = null;
                SyncLoopBox(chkLoopPsf2, psf2Wave);
                lblPsf2View.Text = $"Sample #{i}: empty / cannot decode";
                return;
            }
            psf2Wave.Buffer = s.Decoded.Clone();           // preview copy: dragging loop flags here never corrupts the bank
            SyncLoopBox(chkLoopPsf2, psf2Wave);
            lblPsf2View.Text = $"Sample #{i}  |  {s.SampleRate} Hz  |  {(s.Loops ? "LOOP" : "one-shot")}  |  BD@0x{s.BdOffset:X}  {s.BdSize:N0} bytes" + (s.Replaced ? "  |  REPLACED" : "");
        }

        private void PlaySelectedSample()
        {
            int i = SelectedSampleIndex();
            if (i < 0) { SetStatus("Select a sample first."); return; }
            ShowPsf2Sample(i);
            PlayWave(psf2Wave);
        }

        // ================================================================= SQ / MIDI

        private void LoadSqFile(string path)
        {
            sq = SqFile.Load(path);
            sqPath = path; sqModified = false;
            songs.RemoveAll(s => s.Index >= 0);
            songs.InsertRange(0, sq.Songs);
            renderCache.Clear();
            RefreshSongList(sq.Songs[0]);
            tabs.SelectedTab = tabPsf2;
            UpdateBankInfo();
            int loops = sq.Songs.Count(x => x.HasLoop);
            SetStatus($"Loaded SQ {Path.GetFileName(path)}: {sq.Songs.Count} song(s), {loops} with loop markers." + (bank.Loaded ? " Press Play song." : " Load the HD/BD to hear it."));
        }

        private void LoadMidiFile(string path)
        {
            var song = SqSong.FromSmf(File.ReadAllBytes(path), "MIDI: " + Path.GetFileName(path));
            song.Index = -1;
            songs.Add(song);
            RefreshSongList(song);
            tabs.SelectedTab = tabPsf2;
            SetStatus($"Loaded MIDI {Path.GetFileName(path)} ({song.NoteCount} notes{(song.HasLoop ? ", loop markers found" : "")})." + (bank.Loaded ? " Press Play song." : " Load an HD/BD bank to hear it."));
        }

        private void RefreshSongList(SqSong select)
        {
            cmbSong.Items.Clear();
            foreach (var s in songs)
            {
                var it = new SongItem
                {
                    Song = s,
                    Label = $"{s.Name}   [{FormatClock(s.DurationSeconds)} | {s.NoteCount} notes | ch {string.Join(",", s.Channels.Select(c => c + 1))} | {s.Ppqn} ppqn{(s.HasLoop ? " | LOOP" : "")}]"
                };
                cmbSong.Items.Add(it);
                if (s == select) cmbSong.SelectedItem = it;
            }
            if (cmbSong.SelectedIndex < 0 && cmbSong.Items.Count > 0) cmbSong.SelectedIndex = 0;
        }

        private SqSong SelectedSong() => (cmbSong.SelectedItem as SongItem)?.Song;

        private async void PlaySong()
        {
            if (!bank.Loaded) { MessageBox.Show(this, "Load an HD (+BD) bank first - the song's instruments come from it.", "Play song"); return; }
            var song = SelectedSong();
            if (song == null) { MessageBox.Show(this, "Load an .SQ or .MID file first.", "Play song"); return; }

            if (renderCache.TryGetValue(song, out var cached)) { ShowRendered(cached, song); PlayWave(psf2Wave); return; }

            TransportStop();
            renderCts?.Cancel();
            var cts = new CancellationTokenSource();
            renderCts = cts;
            pbRender.Value = 0; pbRender.Visible = true; bCancelRender.Visible = true; bPlaySong.Enabled = false;
            SetStatus("Rendering song through the HD/BD instruments...");
            var prog = new Progress<int>(pct => { if (!cts.IsCancellationRequested) pbRender.Value = Math.Max(0, Math.Min(100, pct)); });
            try
            {
                var b = bank; var token = cts.Token;
                var result = await Task.Run(() => Psf2Renderer.Render(b, song, prog, token));
                renderCache[song] = result;
                ShowRendered(result, song);
                SetStatus($"Rendered {result.DurationSeconds:F1}s." + (result.LoopEnabled ? $" Loop {result.LoopStart:N0} → {result.LoopEnd:N0} (from the SQ markers)." : ""));
                PlayWave(psf2Wave);
            }
            catch (OperationCanceledException) { SetStatus("Render cancelled."); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Render error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally
            {
                pbRender.Visible = false; bCancelRender.Visible = false; bPlaySong.Enabled = true;
            }
        }

        private void ShowRendered(AudioBuffer b, SqSong song)
        {
            psf2Wave.Buffer = b;
            SyncLoopBox(chkLoopPsf2, psf2Wave);
            lblPsf2View.Text = $"Rendered song: {song.Name}  |  {b.DurationSeconds:F1}s  |  {b.SampleRate} Hz stereo  |  " +
                               (song.HasLoop ? "SQ loop: " + song.LoopDescription : "no loop markers in this song");
        }

        private void SendRenderedToEditor()
        {
            var song = SelectedSong();
            if (song != null && renderCache.TryGetValue(song, out var b))
                SetEditorBuffer(b.Clone(), (song.Name ?? "song").Replace("MIDI: ", "").Replace(" ", "_") + ".wav");
            else MessageBox.Show(this, "Press Play song first - then the rendered audio can be sent to the editor (and saved as WAV/MP3/ADX...).", "Render");
        }

        private void ExportMidi()
        {
            var song = SelectedSong();
            if (song == null) { MessageBox.Show(this, "No song loaded.", "Extract MIDI"); return; }
            using var d = new SaveFileDialog { Filter = "Standard MIDI|*.mid", FileName = (sqPath != null ? Path.GetFileNameWithoutExtension(sqPath) : "song") + (song.Index >= 0 ? "_" + song.Index : ""), AddExtension = true };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllBytes(d.FileName, song.ToSmf());
            SetStatus("Saved MIDI: " + d.FileName);
        }

        private void NewSqFromMidi()
        {
            using var d = new OpenFileDialog { Filter = "MIDI|*.mid;*.midi", Title = "MIDI file(s) for the new SQ (one song per file)", Multiselect = true };
            if (d.ShowDialog(this) != DialogResult.OK) return;

            var list = new List<SqSong>();
            foreach (var f in d.FileNames)
            {
                try { list.Add(SqSong.FromSmf(File.ReadAllBytes(f), "SQ song")); }
                catch (Exception ex) { MessageBox.Show(this, Path.GetFileName(f) + ":\n" + ex.Message, "MIDI import"); return; }
            }
            if (list.Count == 0) return;

            string tmp = Path.Combine(Path.GetTempPath(), "ps2as_" + Guid.NewGuid().ToString("N") + ".sq");
            try
            {
                byte[] data = SqFile.BuildNew(list, sq);           // current SQ (if any) is used as the header template
                File.WriteAllBytes(tmp, data);
                sq = SqFile.Load(tmp);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "New SQ", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            finally { try { File.Delete(tmp); } catch { } }

            sqPath = null; sqModified = true;
            songs.RemoveAll(s => s.Index >= 0);
            songs.InsertRange(0, sq.Songs);
            renderCache.Clear();
            RefreshSongList(sq.Songs[0]);
            tabs.SelectedTab = tabPsf2;
            UpdateBankInfo();
            SetStatus($"New SQ with {sq.Songs.Count} song(s) built from MIDI. Use Save SQ... to write it" + (bank.Loaded ? ", or Play song to hear it." : "."));
        }

        private void ReplaceSongFromMidi()
        {
            if (sq == null)
            {
                var r = MessageBox.Show(this, "No SQ is loaded.\n\nCreate a NEW SQ from MIDI file(s) instead?", "Replace song", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes) NewSqFromMidi();
                return;
            }
            using var d = new OpenFileDialog { Filter = "MIDI|*.mid;*.midi", Title = "MIDI file to put into the SQ" };
            if (d.ShowDialog(this) != DialogResult.OK) return;

            SqSong ns;
            try { ns = SqSong.FromSmf(File.ReadAllBytes(d.FileName), "SQ song"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "MIDI import"); return; }

            int idx = SelectedSong() != null && SelectedSong().Index >= 0 ? SelectedSong().Index : 0;
            if (sq.Songs.Count > 1)
            {
                if (!Prompt.Number(this, "Replace song", $"Which SQ song slot to replace (0 - {sq.Songs.Count - 1}), or {sq.Songs.Count} to append?", idx, out double v)) return;
                idx = (int)Math.Max(0, Math.Min(sq.Songs.Count, v));
            }
            byte[] data = sq.BuildWith(idx, ns);
            sq.Parse(data);
            sqModified = true;
            songs.RemoveAll(s => s.Index >= 0);
            songs.InsertRange(0, sq.Songs);
            renderCache.Clear();
            RefreshSongList(sq.Songs[Math.Min(idx, sq.Songs.Count - 1)]);
            SetStatus($"SQ song #{idx} replaced with {Path.GetFileName(d.FileName)} ({ns.NoteCount} notes). Use Save SQ... to write the file.");
        }

        private void SaveSq()
        {
            if (sq == null) { MessageBox.Show(this, "No SQ loaded.", "Save SQ"); return; }
            using var d = new SaveFileDialog { Filter = "SQ|*.sq;*.SQ|All|*.*", FileName = Path.GetFileName(sqPath ?? "song.sq"), InitialDirectory = sqPath != null ? Path.GetDirectoryName(sqPath) : null, AddExtension = true };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            BackupOnce(d.FileName);
            File.WriteAllBytes(d.FileName, sq.Data);
            sqPath = d.FileName; sqModified = false;
            UpdateBankInfo();
            SetStatus("Saved SQ: " + d.FileName);
        }

        // ================================================================= extract / replace / save bank

        private static void BackupOnce(string path)
        {
            if (!AppSettings.Current.MakeBackups) return;
            try { if (File.Exists(path) && !File.Exists(path + ".bak")) File.Copy(path, path + ".bak"); } catch { }
        }

        private void ExtractSample(bool wav)
        {
            int i = SelectedSampleIndex();
            if (i < 0 || !bank.Samples[i].Valid) { SetStatus("Select a valid sample."); return; }
            using var d = new SaveFileDialog { Filter = wav ? "WAV|*.wav" : "VAG|*.vag", FileName = bank.SampleFileName(i), AddExtension = true };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            if (wav) StandardCodec.EncodeWav(bank.Samples[i].Decoded, d.FileName);
            else File.WriteAllBytes(d.FileName, bank.ExtractVag(i));
            SetStatus("Extracted: " + d.FileName);
        }

        private void ExtractAllVag()
        {
            if (!bank.Loaded) { MessageBox.Show(this, "Load an HD first.", "Extract"); return; }
            using var d = new FolderBrowserDialog { Description = "Extract every VAG sample of the bank into a folder" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            int n = 0;
            foreach (var s in bank.Samples.Where(x => x.Valid))
            {
                File.WriteAllBytes(Path.Combine(d.SelectedPath, bank.SampleFileName(s.Index) + ".vag"), bank.ExtractVag(s.Index));
                n++;
            }
            SetStatus($"Extracted {n} VAG files to {d.SelectedPath}");
        }

        private void ReplaceSelectedFromDialog(bool keepRate)
        {
            int i = SelectedSampleIndex();
            if (i < 0) { SetStatus("Select a sample to replace first."); return; }
            using var d = new OpenFileDialog { Filter = AudioIO.AudioOnlyFilter, Title = $"Replace sample #{i} with..." };
            if (d.ShowDialog(this) == DialogResult.OK) ReplaceSampleFromFile(i, d.FileName, keepRate);
        }

        private void ReplaceSampleFromFile(int i, string path, bool keepRate)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                var s = bank.Samples[i];
                if (VagCodec.HasHeader(data) && !VagCodec.IsInterleavedHeader(data))
                {
                    int rate = (data[0x10] << 24) | (data[0x11] << 16) | (data[0x12] << 8) | data[0x13];
                    if (!(keepRate && rate != s.OrigRate))
                    {
                        int len = data.Length - 0x30;
                        int decl = (data[0x0C] << 24) | (data[0x0D] << 16) | (data[0x0E] << 8) | data[0x0F];
                        if (decl > 0 && decl <= len) len = decl;
                        byte[] raw = new byte[len];
                        Array.Copy(data, 0x30, raw, 0, len);
                        bool loop = VagCodec.DecodeAdpcm(raw, 0, raw.Length, rate).LoopEnabled;
                        bank.ReplaceSampleRaw(i, raw, rate, loop);
                        AfterReplace(i, path, "lossless VAG copy");
                        return;
                    }
                }
                var b = LoadAudioForReplace(path);
                if (b == null) return;
                bank.ReplaceSample(i, b, keepRate);
                AfterReplace(i, path, keepRate ? "re-encoded, original rate kept" : "re-encoded");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Replace error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ReplaceSampleFromEditor(int i, bool keepRate)
        {
            var b = wave.Buffer;
            if (b == null || b.TotalFrames == 0) { MessageBox.Show(this, "The Audio Editor is empty.", "Replace"); return; }
            try
            {
                b.LoopEnabled = chkLoop.Checked && b.LoopEnd > b.LoopStart;
                bank.ReplaceSample(i, b, keepRate);
                AfterReplace(i, "Audio Editor", "loop " + (bank.Samples[i].Loops ? "ON" : "OFF"));
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Replace error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void AfterReplace(int i, string from, string how)
        {
            renderCache.Clear();
            RefreshSampleRow(i);
            ShowPsf2Sample(i);
            UpdateBankInfo(); UpdateTitle();
            SetStatus($"Sample #{i} replaced from {Path.GetFileName(from)} ({how}). Use Save HD + BD to write the bank.");
        }

        private void RevertSample(int i)
        {
            bank.Revert(i);
            renderCache.Clear();
            RefreshSampleRow(i); ShowPsf2Sample(i); UpdateBankInfo(); UpdateTitle();
        }

        private void SendSampleToEditor(int i)
        {
            var s = bank.Samples[i];
            if (s.Decoded == null) return;
            SetEditorBuffer(s.Decoded.Clone(), bank.SampleFileName(i) + ".vag");
        }

        private void SaveBank(bool saveAs)
        {
            if (!bank.Loaded) { MessageBox.Show(this, "Load an HD + BD bank first.", "Save bank"); return; }
            tabs.SelectedTab = tabPsf2;

            string hd = bank.HdPath, bd = bank.BdPath;
            if (saveAs)
            {
                using var d = new SaveFileDialog { Filter = "HD|*.hd;*.HD", FileName = Path.GetFileName(bank.HdPath), InitialDirectory = Path.GetDirectoryName(bank.HdPath), Title = "Save HD (the BD is saved next to it with the same name)", AddExtension = true };
                if (d.ShowDialog(this) != DialogResult.OK) return;
                hd = d.FileName;
                bd = Path.ChangeExtension(hd, Path.GetExtension(bank.BdPath));
            }
            else if (MessageBox.Show(this, $"Overwrite\n{hd}\n{bd}" + (AppSettings.Current.MakeBackups ? "\n\n(.bak copies of the originals are made once)" : ""), "Save bank", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            try
            {
                BackupOnce(hd); BackupOnce(bd);
                bank.Save(hd, bd);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

            PopulateSamples(); UpdateBankInfo(); UpdateTitle();
            SetStatus($"Saved {Path.GetFileName(hd)} + {Path.GetFileName(bd)}.");
            MessageBox.Show(this, $"Saved\n{hd}\n{bd}", "Saved");
        }
    }
}