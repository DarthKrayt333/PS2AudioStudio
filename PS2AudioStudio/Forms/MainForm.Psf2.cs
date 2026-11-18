using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
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
        private string sqLabel;                 // shown in the info line when the SQ has no file path (inside a .psf2 / new)
        private bool sqModified;
        private readonly List<SqSong> songs = new List<SqSong>();
        private readonly Dictionary<SqSong, AudioBuffer> renderCache = new Dictionary<SqSong, AudioBuffer>();
        private readonly Dictionary<SqSong, Psf2RenderReport> reportCache = new Dictionary<SqSong, Psf2RenderReport>();

        private Psf2Entry psf2Template;         // set when the bank came from a .psf2 (its IRX files are reused when saving)
        private string psf2Name;                // stem of the opened .psf2
        
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
        private readonly List<ToolStripItem> sampleSingleItems = new List<ToolStripItem>();

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
            cmbSong.SelectedIndexChanged += (s, e) => ShowSongInfo();

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

            // ---- row 4: song tools (MIDI player, full-song view, report, save as) ----
            var bar4 = Ui.Bar1(40);
            var bMidiPlay = Ui.Btn("🎹 Play with MIDI player"); bMidiPlay.Click += (s, e) => PlaySelectedMidi();
            var bMidiStop = Ui.Btn("⏹ Stop"); bMidiStop.Click += (s, e) => TransportStop();
            var bShowFull = Ui.Btn("🎼 Show full song"); bShowFull.Click += (s, e) => ShowFullSong();
            var bReport = Ui.Btn("📋 Render report..."); bReport.Click += (s, e) => ShowRenderReport();
            var bSaveSong = Ui.Btn("💾 Save song as MIDI / SQ..."); bSaveSong.Click += (s, e) => SaveSelectedSongAs();
            bar4.Controls.Add(Ui.Lbl("Song tools:"));
            bar4.Controls.AddRange(new Control[] { bMidiPlay, bMidiStop, bShowFull, bReport, bSaveSong });

            // ---- row 5: .psf2 packages ----
            var bar5 = Ui.Bar1(40);
            var bOpenPsf2 = Ui.Btn("📦 Open .psf2..."); bOpenPsf2.Click += (s, e) => OpenPsf2Dlg();
            var bUnpack = Ui.Btn("📤 Unpack .psf2 to folder..."); bUnpack.Click += (s, e) => UnpackPsf2();
            var bRepack = Ui.Btn("📥 Repack folder to .psf2..."); bRepack.Click += (s, e) => RepackPsf2();
            var bPsf2Cur = Ui.Btn("📥 Save as .psf2 (current bank + SQ)..."); bPsf2Cur.Click += (s, e) => SavePsf2FromCurrent();
            bar5.Controls.Add(Ui.Lbl(".psf2:"));
            bar5.Controls.AddRange(new Control[] { bOpenPsf2, bUnpack, bRepack, bPsf2Cur });
            bar5.Controls.Add(MakeHdTestButton());

            bar5.Controls.Add(MakeStudioButton());

            bar5.Controls.Add(MakeSf2Button());

            lblPsf2Info = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = Color.Yellow, BackColor = Ui.Panel, Padding = new Padding(6, 4, 0, 0), Font = new Font("Consolas", 9) };
            lblPsf2Info.Text = "Drop an .HD / .BD / .SQ (the others with the same name are found automatically), a .psf2 or a .MID, then press Play song.";

            // ---- body ----
            splitPsf2 = new SplitContainer { Dock = DockStyle.Fill };
            splitPsf2Left = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };

            lvSamples = new ListView { Dock = DockStyle.Fill };
            Ui.StyleList(lvSamples);
            lvSamples.Columns.Add("#", 45); lvSamples.Columns.Add("Rate", 60); lvSamples.Columns.Add("Loop", 45);
            lvSamples.Columns.Add("BD offset", 85); lvSamples.Columns.Add("Size", 75); lvSamples.Columns.Add("Length", 65); lvSamples.Columns.Add("Mod", 40);
            lvSamples.SelectedIndexChanged += (s, e) =>
            {
                if (_listBulk) return;
                int n = lvSamples.SelectedItems.Count;
                if (n == 1) ShowPsf2Sample(SelectedSampleIndex());
                else if (n > 1) SetStatus($"{n} samples selected - right-click to extract or revert them, or drag them out.");
            };
            lvSamples.DoubleClick += (s, e) => PlaySelectedSample();
            lvSamples.ItemDrag += (s, e) => DragOutSamples();
            lvSamples.ContextMenuStrip = BuildSampleMenu();

            tvProg = new TreeView { Dock = DockStyle.Fill, BackColor = Ui.Panel, ForeColor = Color.White, Font = new Font("Consolas", 9), BorderStyle = BorderStyle.None };
            tvProg.NodeMouseDoubleClick += (s, e) =>
            {
                if (e.Node.Tag is int vag)
                {
                    foreach (ListViewItem it in lvSamples.Items)
                        if ((int)it.Tag == vag)
                        {
                            lvSamples.SelectedItems.Cast<ListViewItem>().ToList().ForEach(x => x.Selected = false);
                            it.Selected = true; it.EnsureVisible(); PlaySelectedSample(); break;
                        }
                }
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
            splitPsf2.Panel2.Controls.Add(BuildPsf2LoopBar());
            splitPsf2.Panel2.Controls.Add(hdr);

            p.Controls.Add(splitPsf2);
            p.Controls.Add(lblPsf2Info);
            p.Controls.Add(bar3);
            p.Controls.Add(bar5);
            p.Controls.Add(bar4);
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
            ToolStripItem S(ToolStripItem it) { sampleSingleItems.Add(it); return it; }

            m.Items.Add(S(Mi("▶ Play", (s, e) => PlaySelectedSample())));
            m.Items.Add(S(Mi("Open in Audio Editor", (s, e) => { int i = SelectedSampleIndex(); if (i >= 0) SendSampleToEditor(i); })));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Mi("Extract as .vag (lossless)...", (s, e) => ExtractSample(false)));
            m.Items.Add(Mi("Extract as .wav...", (s, e) => ExtractSample(true)));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(S(Mi("Replace with file... (keep original sample rate)", (s, e) => ReplaceSelectedFromDialog(true))));
            m.Items.Add(S(Mi("Replace with file... (use the file's sample rate)", (s, e) => ReplaceSelectedFromDialog(false))));
            m.Items.Add(S(Mi("Replace with Audio Editor contents (keep rate)", (s, e) => { int i = SelectedSampleIndex(); if (i >= 0) ReplaceSampleFromEditor(i, true); })));
            m.Items.Add(S(Mi("Replace with Audio Editor contents (editor's rate)", (s, e) => { int i = SelectedSampleIndex(); if (i >= 0) ReplaceSampleFromEditor(i, false); })));
            m.Items.Add(Mi("Revert selected sample(s)", (s, e) => RevertSelected()));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Mi("Select all", (s, e) => SelectAllInList(lvSamples)));
            m.Items.Add(Mi("Save HD + BD...", (s, e) => SaveBank(true)));
            m.Opening += (s, e) =>
            {
                bool one = lvSamples.SelectedItems.Count == 1;
                foreach (var it in sampleSingleItems) it.Enabled = one;
            };
            return m;
        }

        private void InvalidateRenders()
        {
            renderCache.Clear();
            reportCache.Clear();
        }

        private void ClearPsf2Tab()
        {
            renderCts?.Cancel();
            TransportStop();
            bank = new Psf2Bank();
            sq = null; sqPath = null; sqLabel = null; sqModified = false;
            psf2Template = null; psf2Name = null;
            songs.Clear();
            InvalidateRenders();
            cmbSong.Items.Clear();
            lvSamples.Items.Clear();
            tvProg.Nodes.Clear();
            psf2Wave.Buffer = null;
            SyncLoopBox(chkLoopPsf2, psf2Wave);
            lblPsf2View.Text = "Select a sample, or press Play song to render a song.";
            lblPsf2Info.Text = "Drop an .HD / .BD / .SQ (the others with the same name are found automatically), a .psf2 or a .MID, then press Play song.";
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
            string b = sqPath != null ? Path.GetFileNameWithoutExtension(sqPath) : psf2Name;
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
                Drag(files, SafeFile(sqPath != null ? Path.GetFileName(sqPath) : (psf2Name ?? "song") + ".sq"), () => cur.Data);
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
            using var d = new OpenFileDialog { Filter = "HD files|*.hd;*.HD|All|*.*", Title = "Select HD (the BD and SQ with the same name are auto-loaded)" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string hd = d.FileName;
            LoadHdFile(hd, null);
            if (bank.Loaded && bank.HdPath == hd)
            {
                var set = Psf2Set.Find(hd);
                if (set.Sq != null)
                {
                    try { LoadSqFile(set.Sq); }
                    catch (Exception ex) { SetStatus("The SQ next to the HD could not be loaded: " + ex.Message); }
                }
            }
        }

        private void LoadSqDlg()
        {
            using var d = new OpenFileDialog { Filter = "SQ / MIDI|*.sq;*.SQ;*.mid;*.midi|All|*.*", Title = "Select SQ or MIDI file", Multiselect = true };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            OpenPaths(d.FileNames);
        }

        private void ApplyBank(Psf2Bank nb)
        {
            bank = nb;
            InvalidateRenders();
            PopulateSamples();
            PopulateProgramTree();
            psf2Wave.Buffer = null;
            SyncLoopBox(chkLoopPsf2, psf2Wave);
            tabs.SelectedTab = tabPsf2;
            UpdateBankInfo();
            UpdateTitle();
            if (lvSamples.Items.Count > 0) lvSamples.Items[0].Selected = true;
        }

        private void ApplySq(SqFile f, string path, string label)
        {
            sq = f;
            sqPath = path;
            sqLabel = path != null ? Path.GetFileName(path) : label;
            sqModified = false;
            songs.RemoveAll(s => s.Index >= 0);
            songs.InsertRange(0, sq.Songs);
            InvalidateRenders();
            RefreshSongList(sq.Songs[0]);
            tabs.SelectedTab = tabPsf2;
            UpdateBankInfo();
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

            psf2Template = null; psf2Name = null;
            ApplyBank(nb);
            SetStatus($"Loaded {Path.GetFileName(bank.HdPath)} + {Path.GetFileName(bank.BdPath)}.");
        }

        private void UpdateBankInfo()
        {
            if (!bank.Loaded) return;
            int valid = bank.Samples.Count(s => s.Valid);
            int rep = bank.Samples.Count(s => s.Replaced);
            lblPsf2Info.Text = $"HD: {Path.GetFileName(bank.HdPath)} | BD: {Path.GetFileName(bank.BdPath)} | {valid}/{bank.Samples.Count} samples | {bank.Programs.Count} programs | " +
                               (rep > 0 ? $"{rep} replaced (unsaved)" : "unmodified") +
                               (sq != null ? $" | SQ: {sqLabel ?? "(new)"} ({sq.Songs.Count} song(s))" : "");
        }

        private int SelectedSampleIndex()
        {
            if (lvSamples == null || lvSamples.SelectedItems.Count == 0) return -1;
            return (int)lvSamples.SelectedItems[0].Tag;
        }

        private List<int> SelectedSampleIndices()
        {
            return lvSamples.SelectedItems.Cast<ListViewItem>().Select(it => (int)it.Tag).OrderBy(x => x).ToList();
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
            var song = SelectedSong();
            bool haveSong = song != null && renderCache.ContainsKey(song);
            lblPsf2View.Text = $"Sample #{i}  |  {s.SampleRate} Hz  |  {s.Decoded.DurationSeconds:F3}s  |  {(s.Loops ? "LOOP" : "one-shot")}  |  BD@0x{s.BdOffset:X}  {s.BdSize:N0} bytes" +
                               (s.Replaced ? "  |  REPLACED" : "") +
                               (haveSong ? "  |  press 🎼 Show full song to go back" : "");
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
            var f = SqFile.Load(path);
            ApplySq(f, path, null);
            int loops = sq.Songs.Count(x => x.HasLoop);
            SetStatus($"Loaded SQ {Path.GetFileName(path)}: {sq.Songs.Count} song(s), {loops} with loop markers." + (bank.Loaded ? " Press Play song." : " Press Play song to hear it with the MIDI player (load an HD/BD to use the game's instruments)."));
        }

        private void LoadMidiFile(string path)
        {
            var song = SqSong.FromSmf(File.ReadAllBytes(path), "MIDI: " + Path.GetFileName(path));
            song.Index = -1;
            songs.Add(song);
            RefreshSongList(song);
            tabs.SelectedTab = tabPsf2;
            SetStatus($"Loaded MIDI {Path.GetFileName(path)} ({song.NoteCount} notes, {FormatClock(song.DurationSeconds)}{(song.HasLoop ? ", loop markers found" : "")})." + (bank.Loaded ? " Press Play song." : " Press Play song to hear it with the MIDI player."));
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


        /// <summary>Selecting a song shows its length (and its render if it already exists) - no need to play first.</summary>
        private void ShowSongInfo()
        {
            var song = SelectedSong();
            if (song == null) return;
            TransportStop();
            if (renderCache.TryGetValue(song, out var cb)) { ShowRendered(cb, song); return; }
            psf2Wave.Buffer = null;
            SyncLoopBox(chkLoopPsf2, psf2Wave);
            lblPsf2View.Text = $"Song: {song.Name}  |  length {FormatClock(song.DurationSeconds)}  |  {song.NoteCount:N0} notes  |  " +
                               (song.HasLoop ? "loop: " + song.LoopDescription : "no loop markers") + "  |  not rendered yet";
        }

        /// <summary>
        /// "Mini hack" = ONE valid looping VAG in the bank and a song of 1-8 notes that never get a note-off.
        /// In the game such a hack loops by the VAG's own loop flags, not by the SQ markers.
        /// </summary>
        private bool IsMiniHack(SqSong song, out Psf2Sample vag)
        {
            vag = null;
            if (song == null || !bank.Loaded) return false;

            Psf2Sample only = null;
            int valid = 0;
            foreach (var s in bank.Samples)
                if (s.Valid) { valid++; only = s; }
            if (valid != 1 || only == null || !only.Loops || only.Decoded == null) return false;

            // count note-on / note-off per channel
            var onPer = new int[16];
            var offPer = new int[16];
            int on = 0;
            foreach (var e in song.Events)
            {
                int c = e.Channel & 15;
                if (e.Type == SqEventType.NoteOn) { on++; onPer[c]++; }
                else if (e.Type == SqEventType.NoteOff) offPer[c]++;
            }
            if (on < 1 || on > 8) return false;

            // at least one channel has a note that is never released
            bool held = false;
            for (int c = 0; c < 16; c++)
                if (onPer[c] > offPer[c]) held = true;
            if (!held) return false;

            vag = only;
            return true;
        }

        /// <summary>Returns the rendered song (from the cache, or by rendering it now). Null when cancelled or failed.</summary>
        private async Task<AudioBuffer> GetRenderAsync(SqSong song)
        {
            if (renderCache.TryGetValue(song, out var cached)) return cached;

            TransportStop();
            renderCts?.Cancel();
            var cts = new CancellationTokenSource();
            renderCts = cts;
            pbRender.Value = 0; pbRender.Visible = true; bCancelRender.Visible = true; bPlaySong.Enabled = false;
            SetStatus("Rendering song through the HD/BD instruments...");
            var prog = new Progress<int>(pct => { if (!cts.IsCancellationRequested) pbRender.Value = Math.Max(0, Math.Min(100, pct)); });
            var report = new Psf2RenderReport();
            try
            {
                var b = bank; var token = cts.Token;
                // full song (honorLoop = false): the SQ loop is put on the buffer as editable loop points
                var result = await Task.Run(() => Psf2Renderer.Render(b, song, prog, token, false, report));
                ApplySongLoopToBuffer(result, song);

                // mini hack: the preview IS the VAG (own rate, own loop points) - no padding, exactly like the game
                if (IsMiniHack(song, out var mv))
                {
                    var vag = mv.Decoded.Clone();
                    vag.LoopStart = mv.Decoded.LoopStart;
                    vag.LoopEnd = mv.Decoded.LoopEnd;
                    vag.LoopEnabled = true;
                    result = vag;
                }

                renderCache[song] = result;
                reportCache[song] = report;
                return result;
            }
            catch (OperationCanceledException) { SetStatus("Render cancelled."); return null; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Render error", MessageBoxButtons.OK, MessageBoxIcon.Error); return null; }
            finally
            {
                if (renderCts == cts)
                {
                    pbRender.Visible = false; bCancelRender.Visible = false; bPlaySong.Enabled = true;
                }
            }
        }

        private string RenderSummary(AudioBuffer b, SqSong song)
        {
            bool mini = IsMiniHack(song, out _);
            string s = $"Rendered {b.DurationSeconds:F1}s." +
                       (b.LoopEnabled ? $" Loop {b.LoopStart:N0} → {b.LoopEnd:N0} ({(mini ? "mini hack: from the VAG's own loop, like the game" : song.HasLoop ? "from the SQ markers" : "from the sample")})." : "");
            if (reportCache.TryGetValue(song, out var rep) && rep.NotesSilent > 0)
                s += $"  {rep.NotesSilent:N0} of {rep.NotesTotal:N0} notes made no sound - see 📋 Render report.";
            return s;
        }

        private async void PlaySong()
        {
            var song = SelectedSong();
            if (song == null) { MessageBox.Show(this, "Load an .SQ or .MID file first.", "Play song"); return; }

            if (!bank.Loaded)
            {
                // no HD/BD needed to listen: use the Windows MIDI synth
                PlayMidiSong(song, chkLoopPsf2.Checked);
                SetStatus("Playing with the Windows MIDI synth (no HD/BD loaded). Load an HD/BD bank to render it with the game's instruments.");
                return;
            }

            bool wasCached = renderCache.ContainsKey(song);
            var b = await GetRenderAsync(song);
            if (b == null) return;
            ShowRendered(b, song);
            if (!wasCached) SetStatus(RenderSummary(b, song));
            PlayWave(psf2Wave);
        }

        private void ShowRendered(AudioBuffer b, SqSong song)
        {
            psf2Wave.Buffer = b;
            psf2Wave.MarkFullFit();                 // rendered full song = 100% view
            SyncLoopBox(chkLoopPsf2, psf2Wave);
            SyncPsf2LoopNums(true);

            bool mini = IsMiniHack(song, out _);
            string loopTxt = mini && b.LoopEnabled ? "MINI HACK: the VAG itself, loop taken from the VAG (like the game), the SQ markers are ignored"
                           : song.HasLoop ? "SQ loop: " + song.LoopDescription
                           : b.LoopEnabled ? "loop taken from the sample"
                           : "no loop markers in this song (" + song.Cc99Summary + ")";
            lblPsf2View.Text = $"Rendered song: {song.Name}  |  {b.DurationSeconds:F1}s  |  {b.SampleRate} Hz {(b.Channels == 1 ? "mono" : "stereo")}  |  " + loopTxt;
            UpdateBankInfo();
        }

        /// <summary>Item 13: back to the full-song view without re-rendering (and without restarting).</summary>
        private void ShowFullSong()
        {
            var song = SelectedSong();
            if (song == null || !renderCache.TryGetValue(song, out var b))
            {
                SetStatus("Nothing rendered for this song yet - press Play song first.");
                return;
            }
            ShowRendered(b, song);
            SetStatus("Showing the full rendered song again (kept in memory, no re-render).");
        }

        private void SendRenderedToEditor()
        {
            var song = SelectedSong();
            if (song != null && renderCache.TryGetValue(song, out var b))
                SetEditorBuffer(b.Clone(), (song.Name ?? "song").Replace("MIDI: ", "").Replace(" ", "_") + ".wav");
            else MessageBox.Show(this, "Press Play song first - then the rendered audio can be sent to the editor (and saved as WAV/MP3/ADX...).", "Render");
        }

        // ---------------------------------------------------------------- MIDI player (Windows synth)
        private void PlaySelectedMidi()
        {
            var song = SelectedSong();
            if (song == null) { MessageBox.Show(this, "Load an .SQ or .MID file first.", "MIDI player"); return; }
            PlayMidiSong(song, chkLoopPsf2.Checked);
        }

        // ---------------------------------------------------------------- render report
        private string BuildReportText(SqSong song, Psf2RenderReport rep)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Song: " + song.Name);
            sb.AppendLine($"Events: {song.Events.Count:N0}   Notes: {song.NoteCount:N0}   Duration: {song.DurationSeconds:F1}s   PPQN: {song.Ppqn}   Loop: {(song.HasLoop ? song.LoopDescription : "none")}");

            int nOn = song.Events.Count(e => e.Type == SqEventType.NoteOn);
            int nOff = song.Events.Count(e => e.Type == SqEventType.NoteOff);
            int nCc = song.Events.Count(e => e.Type == SqEventType.Cc);
            int nPr = song.Events.Count(e => e.Type == SqEventType.Program);
            int nPb = song.Events.Count(e => e.Type == SqEventType.PitchBend);
            int nTe = song.Events.Count(e => e.Type == SqEventType.Tempo);
            sb.AppendLine($"Parsed: {nOn:N0} note-on, {nOff:N0} note-off, {nCc:N0} controller, {nPr:N0} program, {nPb:N0} pitch-bend, {nTe:N0} tempo events.");

            if (song.RawBlock != null)
            {
                int re = 0;
                try { re = song.ToSqBlock().Length; } catch { }
                sb.AppendLine($"SQ block: {song.RawBlock.Length:N0} bytes in the file; {re:N0} bytes when re-encoded from the parsed events.");
                if (re > 0 && re < song.RawBlock.Length * 0.9)
                    sb.AppendLine("  (!) The re-encoded size is much SMALLER than the original - the SQ parser may have skipped part of the song.");
            }

            // notes per channel and the programs they used
            int[] prog = new int[16], bnk = new int[16];
            var per = new SortedDictionary<int, Dictionary<int, int>>();
            foreach (var e in song.Events)
            {
                int c = e.Channel & 15;
                if (e.Type == SqEventType.Cc && e.A == 0) bnk[c] = e.B;
                else if (e.Type == SqEventType.Program) prog[c] = e.A;
                else if (e.Type == SqEventType.NoteOn)
                {
                    int key = bnk[c] * 128 + prog[c];
                    if (!per.TryGetValue(c, out var d)) per[c] = d = new Dictionary<int, int>();
                    d.TryGetValue(key, out int n); d[key] = n + 1;
                }
            }
            sb.AppendLine();
            sb.AppendLine("Channels (notes, program used):");
            foreach (var kv in per)
            {
                int total = kv.Value.Values.Sum();
                var parts = kv.Value.OrderBy(x => x.Key).Select(x =>
                {
                    bool have = bank.Programs.ContainsKey(x.Key) || bank.Programs.ContainsKey(x.Key % 128);
                    return $"{x.Key} x{x.Value}" + (have ? "" : " (NOT IN HD)");
                });
                sb.AppendLine($"  ch {kv.Key + 1,2}: {total,6:N0} notes   programs: {string.Join(", ", parts)}");
            }

            sb.AppendLine();
            sb.AppendLine($"HD: {bank.Programs.Count} programs, {bank.Samples.Count(s => s.Valid)} samples.");
            var plist = bank.Programs.Keys.OrderBy(x => x).Take(64).ToList();
            if (plist.Count > 0) sb.AppendLine("Program numbers in the HD: " + string.Join(", ", plist) + (bank.Programs.Count > 64 ? ", ..." : ""));
            sb.AppendLine();
            sb.Append(rep.ToText());
            return sb.ToString();
        }

        private void ShowRenderReport()
        {
            var song = SelectedSong();
            if (song == null || !reportCache.TryGetValue(song, out var rep))
            {
                MessageBox.Show(this, "Press Play song first - the report is made while the song is rendered.", "Render report");
                return;
            }
            string text = BuildReportText(song, rep);

            using var f = new Form
            {
                Text = "Render report - " + song.Name,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(820, 560),
                BackColor = Ui.Panel,
                ForeColor = Color.White,
                ShowInTaskbar = false
            };
            var tb = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5f),
                BackColor = Ui.Edit,
                ForeColor = Color.White,
                Text = text
            };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = Ui.Panel };
            var copy = new Button { Text = "Copy to clipboard", Left = 8, Top = 6, Width = 150, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Ui.Edit, ForeColor = Color.White };
            copy.Click += (s, e) => { try { Clipboard.SetText(text); SetStatus("Render report copied to the clipboard."); } catch { } };
            var close = new Button { Text = "Close", Left = 168, Top = 6, Width = 90, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Ui.Edit, ForeColor = Color.White, DialogResult = DialogResult.OK };
            bar.Controls.Add(copy); bar.Controls.Add(close);
            f.Controls.Add(tb);
            f.Controls.Add(bar);
            f.AcceptButton = close;
            f.Shown += (s, e) => { tb.SelectionStart = 0; tb.SelectionLength = 0; };
            f.ShowDialog(this);
        }

        // ---------------------------------------------------------------- save song as MIDI / SQ
        private void SaveSelectedSongAs()
        {
            var song = SelectedSong();
            if (song == null) { MessageBox.Show(this, "No song loaded.", "Save song as"); return; }
            SaveSongAs(song, SongBaseName(song), 1);
        }

        /// <summary>filterIndex 1 = MIDI, 2 = SQ. The extension typed by the user decides the format.</summary>
        private void SaveSongAs(SqSong song, string baseName, int filterIndex)
        {
            using var d = new SaveFileDialog
            {
                Filter = "Standard MIDI (*.mid)|*.mid|SCEI SQ (*.sq)|*.sq",
                FilterIndex = filterIndex,
                FileName = baseName,
                AddExtension = true
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                bool asSq = d.FileName.EndsWith(".sq", StringComparison.OrdinalIgnoreCase);
                byte[] data = asSq ? SqFile.BuildNew(new List<SqSong> { song }, sq) : song.ToSmf();
                BackupOnce(d.FileName);
                File.WriteAllBytes(d.FileName, data);
                SetStatus((asSq ? "Saved SQ: " : "Saved MIDI: ") + d.FileName);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save song", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ExportMidi()
        {
            var song = SelectedSong();
            if (song == null) { MessageBox.Show(this, "No song loaded.", "Extract MIDI"); return; }
            using var d = new SaveFileDialog { Filter = "Standard MIDI|*.mid", FileName = SongBaseName(song), AddExtension = true };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllBytes(d.FileName, song.ToSmf());
            SetStatus("Saved MIDI: " + d.FileName);
        }

        // ---------------------------------------------------------------- drops: .psf2, .mid, .sq
        /// <summary>Called first thing in OpenPaths. Returns true when it handled the files itself.</summary>
        /// <summary>Called first thing in OpenPaths. Returns true when it handled the files itself.</summary>
        private bool HandleSpecialDrops(IEnumerable<string> pathsIn)
        {
            var paths = pathsIn.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).ToList();
            if (paths.Count == 0) return false;

            string psf2 = paths.FirstOrDefault(p => string.Equals(Path.GetExtension(p), ".psf2", StringComparison.OrdinalIgnoreCase));
            if (psf2 != null) { OpenPsf2File(psf2); return true; }

            string sf2 = paths.FirstOrDefault(p => string.Equals(Path.GetExtension(p), ".sf2", StringComparison.OrdinalIgnoreCase));
            if (sf2 != null) { ImportSf2File(sf2); return true; }

            bool IsMid(string p)
            {
                string x = Path.GetExtension(p).ToLowerInvariant();
                return x == ".mid" || x == ".midi";
            }

            try
            {
                // .mid / .midi: straight into the PSF2 tab, nothing is asked
                if (paths.All(IsMid))
                {
                    foreach (var m in paths) LoadMidiFile(m);
                    return true;
                }

                // a lone .sq: loaded directly; if an HD + BD with the same name sit next to it, OpenPaths combines all three
                if (paths.Count == 1 && string.Equals(Path.GetExtension(paths[0]), ".sq", StringComparison.OrdinalIgnoreCase))
                {
                    if (Psf2Set.Find(paths[0]).HasBank) return false;
                    LoadSqFile(paths[0]);
                    return true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Open", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return true;
            }
            return false;
        }

        private int Choose(string title, string text, params string[] options)
        {
            int result = -1;
            using var f = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = Ui.Panel,
                ForeColor = Color.White
            };
            f.Controls.Add(new Label { Left = 14, Top = 12, Width = 432, Height = 48, Text = text, ForeColor = Color.White });
            int y = 66;
            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                var b = new Button { Text = options[i], Left = 14, Top = y, Width = 432, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Ui.Edit, ForeColor = Color.White };
                b.Click += (s, e) => { result = idx; f.DialogResult = DialogResult.OK; f.Close(); };
                f.Controls.Add(b);
                y += 38;
            }
            var cancel = new Button { Text = "Cancel", Left = 14, Top = y + 4, Width = 100, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Ui.Edit, ForeColor = Color.White, DialogResult = DialogResult.Cancel };
            f.Controls.Add(cancel);
            f.CancelButton = cancel;
            f.ClientSize = new Size(460, y + 46);
            f.ShowDialog(this);
            return result;
        }

        private void MidiDropChoice(string path)
        {
            string name = Path.GetFileName(path);
            int c = Choose("MIDI file", name + "\n\nWhat do you want to do with it?",
                "Load it into the PSF2 tab",
                "Play it now (Windows MIDI synth)",
                "Convert and save as SQ...",
                "Save as MIDI...");
            if (c < 0) return;
            try
            {
                if (c == 0) LoadMidiFile(path);
                else if (c == 1)
                {
                    LoadMidiFile(path);
                    var song = songs[songs.Count - 1];
                    PlayMidiSong(song, chkLoopPsf2.Checked);
                }
                else
                {
                    var song = SqSong.FromSmf(File.ReadAllBytes(path), "MIDI: " + name);
                    SaveSongAs(song, Path.GetFileNameWithoutExtension(path), c == 2 ? 2 : 1);
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "MIDI", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void SqDropChoice(string path)
        {
            string name = Path.GetFileName(path);
            int c = Choose("SQ file", name + "\n\nWhat do you want to do with it?",
                "Load it into the PSF2 tab",
                "Play it now (Windows MIDI synth)",
                "Convert to MIDI (.mid)...");
            if (c < 0) return;
            try
            {
                if (c == 0) LoadSqFile(path);
                else if (c == 1) { LoadSqFile(path); PlayMidiSong(sq.Songs[0], chkLoopPsf2.Checked); }
                else ExportSqAsMidi(path);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "SQ", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ExportSqAsMidi(string sqFilePath)
        {
            var f = SqFile.Load(sqFilePath);
            string stem = Path.GetFileNameWithoutExtension(sqFilePath);
            string dir = Path.GetDirectoryName(sqFilePath);
            if (f.Songs.Count == 1)
            {
                using var d = new SaveFileDialog { Filter = "Standard MIDI|*.mid", FileName = stem + ".mid", AddExtension = true, InitialDirectory = dir };
                if (d.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllBytes(d.FileName, f.Songs[0].ToSmf());
                SetStatus("Saved MIDI: " + d.FileName);
            }
            else
            {
                using var fb = new FolderBrowserDialog { Description = $"The SQ has {f.Songs.Count} songs - one .mid per song is written into the folder you choose.", SelectedPath = dir };
                if (fb.ShowDialog(this) != DialogResult.OK) return;
                foreach (var s in f.Songs)
                    File.WriteAllBytes(Path.Combine(fb.SelectedPath, $"{stem}_{s.Index}.mid"), s.ToSmf());
                SetStatus($"Saved {f.Songs.Count} MIDI files to {fb.SelectedPath}");
            }
        }

        // ---------------------------------------------------------------- .psf2 (item 6 + 10)
        private void OpenPsf2Dlg()
        {
            using var d = new OpenFileDialog { Filter = "PSF2|*.psf2|All|*.*", Title = "Open a .psf2 (its bank and songs are loaded and the first song is rendered)" };
            if (d.ShowDialog(this) == DialogResult.OK) OpenPsf2File(d.FileName);
        }

        private async void OpenPsf2File(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (!Psf2Container.IsPsf2(data))
                {
                    MessageBox.Show(this, "This is not a PSF2 package (missing the 'PSF' + version 2 header).", "Open .psf2");
                    return;
                }
                var root = Psf2Container.Read(data);
                var hdE = Psf2Container.FindByExt(root, ".hd");
                var bdE = Psf2Container.FindByExt(root, ".bd");
                var sqE = Psf2Container.FindByExt(root, ".sq");
                if (hdE == null || bdE == null)
                {
                    var names = string.Join(", ", Psf2Container.Files(root).Select(x => x.Key));
                    MessageBox.Show(this, "No .hd + .bd pair was found inside this .psf2.\n\nFiles inside: " + names, "Open .psf2");
                    return;
                }

                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                var nb = new Psf2Bank();
                nb.LoadFromBytes(hdE.Data, bdE.Data, Path.Combine(dir, hdE.Name), Path.Combine(dir, bdE.Name));

                string stem = Path.GetFileNameWithoutExtension(path);
                ApplyBank(nb);
                psf2Template = root;
                psf2Name = stem;
                sq = null; sqPath = null; sqLabel = null;
                songs.RemoveAll(s => s.Index >= 0);

                if (sqE != null)
                {
                    try
                    {
                        var nsq = new SqFile();
                        nsq.Parse(sqE.Data);
                        ApplySq(nsq, null, sqE.Name + " (in .psf2)");
                    }
                    catch (Exception ex)
                    {
                        RefreshSongList(null);
                        MessageBox.Show(this, "The bank was loaded, but the sequence inside could not be read:\n" + ex.Message, "Open .psf2");
                    }
                }
                else RefreshSongList(null);

                UpdateBankInfo();
                UpdateTitle();

                if (sq == null)
                {
                    SetStatus($"Opened {Path.GetFileName(path)}: bank only (no .sq inside - a sound-effect bank).");
                    return;
                }

                var song = sq.Songs[0];
                var b = await GetRenderAsync(song);
                if (b == null) return;
                ShowRendered(b, song);
                SetEditorBuffer(b.Clone(), stem + ".wav");
                SetStatus($"Opened {Path.GetFileName(path)} as an audio track. " + RenderSummary(b, song));
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open .psf2", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void UnpackPsf2()
        {
            using var od = new OpenFileDialog { Filter = "PSF2|*.psf2|All|*.*", Title = "Select the .psf2 to unpack" };
            if (od.ShowDialog(this) != DialogResult.OK) return;
            string src = od.FileName;
            using var fb = new FolderBrowserDialog { Description = "Choose where to unpack. A folder named after the file is created inside it.", SelectedPath = Path.GetDirectoryName(src) };
            if (fb.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                byte[] data = File.ReadAllBytes(src);
                if (!Psf2Container.IsPsf2(data)) { MessageBox.Show(this, "This is not a PSF2 package.", "Unpack .psf2"); return; }
                var root = Psf2Container.Read(data);
                string target = Path.Combine(fb.SelectedPath, Path.GetFileNameWithoutExtension(src));
                int n = Psf2Container.ExtractTo(root, target);
                string list = string.Join("\n", Psf2Container.Files(root).Select(x => "  " + x.Key + "  (" + x.Value.Data.Length.ToString("N0") + " bytes)"));
                SetStatus($"Unpacked {n} file(s) to {target}");
                MessageBox.Show(this, $"Unpacked {n} file(s) to\n{target}\n\n{list}", "Unpack .psf2");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unpack .psf2", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void RepackPsf2()
        {
            using var fb = new FolderBrowserDialog { Description = "Select the folder to pack: the .hd, .bd, .sq and the .irx files (as unpacked)." };
            if (fb.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var root = Psf2Container.FromFolder(fb.SelectedPath);
                var files = Psf2Container.Files(root);
                bool Has(string ext) => files.Any(x => x.Key.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
                if (!Has(".hd") || !Has(".bd"))
                {
                    MessageBox.Show(this, "The folder needs at least a .hd and a .bd file.", "Repack .psf2");
                    return;
                }
                if (!Has(".irx") &&
                    MessageBox.Show(this, "There are no .irx files in this folder. A .psf2 without the IOP modules (libsd, modhsyn, modmidi, ...) will not play in a player.\n\nPack it anyway?",
                        "Repack .psf2", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                string folderName = new DirectoryInfo(fb.SelectedPath).Name;
                using var sd = new SaveFileDialog
                {
                    Filter = "PSF2|*.psf2",
                    FileName = folderName + ".psf2",
                    AddExtension = true,
                    InitialDirectory = Path.GetDirectoryName(fb.SelectedPath.TrimEnd('\\', '/'))
                };
                if (sd.ShowDialog(this) != DialogResult.OK) return;
                byte[] outb = Psf2Container.Write(root);
                BackupOnce(sd.FileName);
                File.WriteAllBytes(sd.FileName, outb);
                SetStatus($"Packed {files.Count} file(s) into {sd.FileName}");
                MessageBox.Show(this, $"Packed {files.Count} file(s) into\n{sd.FileName}\n({outb.Length:N0} bytes)\n\nTest it in your PSF2 player before relying on it.", "Repack .psf2");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Repack .psf2", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void SavePsf2FromCurrent()
        {
            if (!bank.Loaded) { MessageBox.Show(this, "Load a bank first.", "Save as .psf2"); return; }
            if (psf2Template == null)
            {
                MessageBox.Show(this, "This bank was not opened from a .psf2, so there are no IRX files to reuse.\n\nOpen a .psf2 first, or use 'Repack folder to .psf2' with a folder that contains the .irx files.", "Save as .psf2");
                return;
            }
            using var d = new SaveFileDialog { Filter = "PSF2|*.psf2", FileName = (psf2Name ?? "song") + ".psf2", AddExtension = true };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                bank.BuildFiles(out byte[] h, out byte[] b);
                bool doneHd = false, doneBd = false, doneSq = false;
                foreach (var kv in Psf2Container.Files(psf2Template))
                {
                    string k = kv.Key.ToLowerInvariant();
                    if (k.EndsWith(".hd") && !doneHd) { kv.Value.Data = h; doneHd = true; }
                    else if (k.EndsWith(".bd") && !doneBd) { kv.Value.Data = b; doneBd = true; }
                    else if (k.EndsWith(".sq") && !doneSq && sq != null) { kv.Value.Data = sq.Data; doneSq = true; }
                }
                byte[] outb = Psf2Container.Write(psf2Template);
                BackupOnce(d.FileName);
                File.WriteAllBytes(d.FileName, outb);
                SetStatus("Saved " + d.FileName + " (bank" + (doneSq ? " + SQ" : "") + " replaced, IRX files kept).");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save as .psf2", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        // ---------------------------------------------------------------- new SQ / replace song
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

            sqPath = null; sqLabel = null; sqModified = true;
            songs.RemoveAll(s => s.Index >= 0);
            songs.InsertRange(0, sq.Songs);
            InvalidateRenders();
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
            InvalidateRenders();
            RefreshSongList(sq.Songs[Math.Min(idx, sq.Songs.Count - 1)]);
            SetStatus($"SQ song #{idx} replaced with {Path.GetFileName(d.FileName)} ({ns.NoteCount} notes). Use Save SQ... to write the file.");
        }

        private void SaveSq()
        {
            if (sq == null) { MessageBox.Show(this, "No SQ loaded.", "Save SQ"); return; }
            using var d = new SaveFileDialog
            {
                Filter = "SQ|*.sq;*.SQ|All|*.*",
                FileName = sqPath != null ? Path.GetFileName(sqPath) : (psf2Name ?? "song") + ".sq",
                InitialDirectory = sqPath != null ? Path.GetDirectoryName(sqPath) : null,
                AddExtension = true
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            BackupOnce(d.FileName);
            File.WriteAllBytes(d.FileName, sq.Data);
            sqPath = d.FileName; sqLabel = Path.GetFileName(d.FileName); sqModified = false;
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
            var idxs = SelectedSampleIndices().Where(i => i >= 0 && i < bank.Samples.Count && bank.Samples[i].Valid).ToList();
            if (idxs.Count == 0) { SetStatus("Select a valid sample."); return; }

            if (idxs.Count > 1)
            {
                using var fb = new FolderBrowserDialog { Description = $"Extract the {idxs.Count} selected samples as {(wav ? ".wav" : ".vag")} into a folder" };
                if (fb.ShowDialog(this) != DialogResult.OK) return;
                int n = 0;
                try
                {
                    foreach (int i in idxs)
                    {
                        string p = Path.Combine(fb.SelectedPath, bank.SampleFileName(i) + (wav ? ".wav" : ".vag"));
                        if (wav) StandardCodec.EncodeWav(bank.Samples[i].Decoded, p);
                        else File.WriteAllBytes(p, bank.ExtractVag(i));
                        n++;
                    }
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Extract error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                SetStatus($"Extracted {n} sample(s) to {fb.SelectedPath}");
                return;
            }

            int one = idxs[0];
            using var d = new SaveFileDialog { Filter = wav ? "WAV|*.wav" : "VAG|*.vag", FileName = bank.SampleFileName(one), AddExtension = true };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            if (wav) StandardCodec.EncodeWav(bank.Samples[one].Decoded, d.FileName);
            else File.WriteAllBytes(d.FileName, bank.ExtractVag(one));
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
            InvalidateRenders();
            RefreshSampleRow(i);
            ShowPsf2Sample(i);
            UpdateBankInfo(); UpdateTitle();
            SetStatus($"Sample #{i} replaced from {Path.GetFileName(from)} ({how}). Use Save HD + BD to write the bank.");
        }

        private void RevertSample(int i)
        {
            bank.Revert(i);
            InvalidateRenders();
            RefreshSampleRow(i); ShowPsf2Sample(i); UpdateBankInfo(); UpdateTitle();
        }

        private void RevertSelected()
        {
            var idx = SelectedSampleIndices();
            if (idx.Count == 0) { SetStatus("Select a sample first."); return; }
            foreach (int i in idx) bank.Revert(i);
            InvalidateRenders();
            foreach (int i in idx) RefreshSampleRow(i);
            if (idx.Count == 1) ShowPsf2Sample(idx[0]);
            UpdateBankInfo(); UpdateTitle();
            SetStatus($"Reverted {idx.Count} sample(s).");
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
