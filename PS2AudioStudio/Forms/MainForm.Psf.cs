using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using PS2AudioStudio.Archives;
using PS2AudioStudio.Audio;
using PS2AudioStudio.Controls;
using DragFiles = System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, System.Func<byte[]>>>;

namespace PS2AudioStudio
{
    public partial class MainForm
    {
        private sealed class PsfRow
        {
            public string Name, Source;
            public int Offset;
            public byte[] Raw;
            public bool Loops;
            public AudioBuffer Decoded;
            public int DecodedRate;
        }

        private PsfPackage psfPackage;
        private string psfPath;
        private int psfRate = 44100;
        private readonly List<PsfRow> psfRows = new List<PsfRow>();

        private readonly List<SongItem> psfSeqItems = new List<SongItem>();
        private readonly List<AkaoSongData> psfAkao = new List<AkaoSongData>();
        private readonly Dictionary<SqSong, SqSong> psfGm = new Dictionary<SqSong, SqSong>();
        private double psfBpm;      // 0 = automatic (from the song's tempo word)

        private ListView lvPsf;
        private TextBox tbPsfReport;
        private WaveformControl psfWave;
        private Label lblPsfInfo, lblPsfView;
        private CheckBox chkLoopPsf;
        private ComboBox cmbPsfSong;
        private SplitContainer splitPsf, splitPsfLeft;

        // ================================================================= tab
        private TabPage BuildPsfTab()
        {
            var p = new TabPage("🎮 PSF (PS1)");

            var bar1 = Ui.Bar1(40);
            var bOpen = Ui.Btn("📂 Open .psf / .vab / .seq..."); bOpen.Click += (s, e) => OpenPsfDlg();
            var bDump = Ui.Btn("🧾 Dump AKAO data..."); bDump.Click += (s, e) => DumpPsfData();
            var bRate = Ui.Btn("🎚 Sample rate..."); bRate.Click += (s, e) => AskPsfRate();
            var bTempo = Ui.Btn("⏱ Tempo (BPM)..."); bTempo.Click += (s, e) => AskPsfTempo();
            bar1.Controls.AddRange(new Control[] { bOpen, bDump, bRate, bTempo });
            InstallPsfSeqButtons(bar1);
            PsfWrapBar(bar1);

            var bar2 = Ui.Bar1(40);
            var bPlay = Ui.Btn("▶ Play sample"); bPlay.Click += (s, e) => PlayPsfSample();
            var bVag = Ui.Btn("💾 Extract .vag..."); bVag.Click += (s, e) => ExtractPsf(false, false);
            var bWav = Ui.Btn("💾 Extract .wav..."); bWav.Click += (s, e) => ExtractPsf(true, false);
            var bAll = Ui.Btn("📁 Extract all VAG..."); bAll.Click += (s, e) => ExtractPsf(false, true);
            var bEd = Ui.Btn("✏ Open in Audio Editor"); bEd.Click += (s, e) => PsfToEditor();
            bar2.Controls.Add(Ui.Lbl("Samples:"));
            bar2.Controls.AddRange(new Control[] { bPlay, bVag, bWav, bAll, bEd });

            var bar3 = Ui.Bar1(40);
            cmbPsfSong = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Margin = new Padding(2, 5, 2, 0), BackColor = Ui.Edit, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            var bMidiPlay = Ui.Btn("🎹 Play with MIDI player"); bMidiPlay.Click += (s, e) => PlayPsfSong();
            var bStop = Ui.Btn("⏹ Stop"); bStop.Click += (s, e) => TransportStop();
            var bMid = Ui.Btn("🎼 Save as MIDI..."); bMid.Click += (s, e) => SavePsfSong(false);
            var bSq = Ui.Btn("💾 Save as SQ..."); bSq.Click += (s, e) => SavePsfSong(true);
            var bSeq = Ui.Btn("💾 Save as SEQ..."); bSeq.Click += (s, e) => SavePsfSong(false, true);
            var bTo2 = Ui.Btn("➡ Send to PSF2 tab"); bTo2.Click += (s, e) => PsfSongToPsf2();
            bar3.Controls.Add(Ui.Lbl("Song:"));
            bar3.Controls.AddRange(new Control[] { cmbPsfSong, bMidiPlay, bStop, bSq, bSeq, bTo2 });

            InstallPsfExtras(bar3);

            InstallPsfTools(bar3);

            lblPsfInfo = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = Color.Yellow, BackColor = Ui.Panel, Padding = new Padding(6, 4, 0, 0), Font = new Font("Consolas", 9) };
            lblPsfInfo.Text = "Drop a .psf / .minipsf / .vab / .seq here. Sample rate for preview and extraction: " + psfRate + " Hz.";

            splitPsf = new SplitContainer { Dock = DockStyle.Fill };
            splitPsfLeft = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };

            lvPsf = new ListView { Dock = DockStyle.Fill };
            Ui.StyleList(lvPsf);
            lvPsf.Columns.Add("#", 45); lvPsf.Columns.Add("Source", 90); lvPsf.Columns.Add("Offset", 80);
            lvPsf.Columns.Add("Size", 75); lvPsf.Columns.Add("Length", 65); lvPsf.Columns.Add("Loop", 50);
            lvPsf.SelectedIndexChanged += (s, e) =>
            {
                if (_listBulk) return;
                int n = lvPsf.SelectedItems.Count;
                if (n == 1) ShowPsfSample((int)lvPsf.SelectedItems[0].Tag);
                else if (n > 1) SetStatus(n + " samples selected - extract them or drag them out.");
            };
            lvPsf.DoubleClick += (s, e) => PlayPsfSample();
            lvPsf.ItemDrag += (s, e) => DragOutPsf();

            tbPsfReport = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9),
                BackColor = Ui.Edit,
                ForeColor = Color.White
            };

            splitPsfLeft.Panel1.Controls.Add(lvPsf);
            splitPsfLeft.Panel2.Controls.Add(tbPsfReport);

            lblPsfView = new Label { Dock = DockStyle.Fill, ForeColor = Color.LightSkyBlue, Padding = new Padding(6, 4, 0, 0), AutoEllipsis = true, Text = "Select a sample to see its waveform." };
            var psfInfo = new Label { Dock = DockStyle.Bottom, Height = 22, BackColor = Ui.Panel, ForeColor = Color.LightGreen, Font = new Font("Consolas", 9), Padding = new Padding(5, 4, 0, 0) };
            psfWave = new WaveformControl { Dock = DockStyle.Fill, InfoLabel = psfInfo, EmptyText = "Open a PSF and select a sample" };
            chkLoopPsf = MakeLoopBox(psfWave);
            psfWave.LoopChanged += (s, e) => SyncLoopBox(chkLoopPsf, psfWave);

            var hdr = new Panel { Dock = DockStyle.Top, Height = 24 };
            hdr.Controls.Add(lblPsfView);
            hdr.Controls.Add(chkLoopPsf);

            splitPsf.Panel1.Controls.Add(splitPsfLeft);
            splitPsf.Panel2.Controls.Add(psfWave);
            splitPsf.Panel2.Controls.Add(psfInfo);
            splitPsf.Panel2.Controls.Add(BuildPfsLoopBar());
            splitPsf.Panel2.Controls.Add(hdr);

            p.Controls.Add(splitPsf);
            p.Controls.Add(lblPsfInfo);
            p.Controls.Add(MakePfsBankBar());
            p.Controls.Add(MakePfsSongBar());
            p.Controls.Add(bar3);
            p.Controls.Add(bar2);
            p.Controls.Add(bar1);
            PsfRehookButtons(p);
            return p;
        }

        private void InitPsfSplitters()
        {
            splitPsf.Panel1MinSize = 300; splitPsf.Panel2MinSize = 300;
            splitPsf.SplitterDistance = Math.Max(300, Math.Min(splitPsf.Width - 300, (int)(splitPsf.Width * 0.5)));
            splitPsfLeft.Panel1MinSize = 80; splitPsfLeft.Panel2MinSize = 60;
            splitPsfLeft.SplitterDistance = Math.Max(80, Math.Min(splitPsfLeft.Height - 60, (int)(splitPsfLeft.Height * 0.6)));
        }

        /// <summary>File > New: empties the PSF tab.</summary>
        private void ClearPsfTab()
        {
            TransportStop();
            psfPackage = null;
            psfPath = null;
            psfBpm = 0;
            psfRows.Clear();
            psfSeqItems.Clear();
            psfAkao.Clear();
            psfGm.Clear();
            ClearPfsState();
            cmbPsfSong.Items.Clear();
            lvPsf.Items.Clear();
            tbPsfReport.Text = "";
            psfWave.Buffer = null;
            SyncLoopBox(chkLoopPsf, psfWave);
            lblPsfView.Text = "Select a sample to see its waveform.";
            lblPsfInfo.Text = "Drop a .psf / .minipsf / .vab / .seq here. Sample rate for preview and extraction: " + psfRate + " Hz.";
            UpdateTitle();
        }

        // ================================================================= open

        private void OpenPsfDlg()
        {
            using var d = new OpenFileDialog { Filter = "PSF / VAB / SEQ|*.psf;*.minipsf;*.psflib;*.vab;*.seq|All|*.*", Title = "Open a PS1 sound file" };
            if (d.ShowDialog(this) == DialogResult.OK) OpenPsfFile(d.FileName);
        }

        private static bool AllZero(byte[] a)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] != 0) return false;
            return true;
        }

        private void OpenPsfFile(string path)
        {
            PsfPackage pkg;
            try { pkg = PsfLoader.Load(path); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Open PSF", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            TransportStop();
            psfPackage = pkg;
            psfPath = path;
            psfBpm = 0;
            psfRows.Clear();
            psfSeqItems.Clear();
            psfAkao.Clear();
            psfGm.Clear();
            cmbPsfSong.Items.Clear();
            psfWave.Buffer = null;
            SyncLoopBox(chkLoopPsf, psfWave);

            var extra = new StringBuilder();

            // samples: AKAO bank first, then exact VAB tables, otherwise a PS-ADPCM scan
            foreach (var b in pkg.Buffers)
            {
                var banks = AkaoBankReader.Find(b.Data);
                var vabs = b.Scan.Vabs.Where(v => v.Valid && v.DataBytes > 0).ToList();
                if (banks.Count > 0)
                {
                    var akSongs = AkaoSongReader.Find(b.Data);
                    psfAkao.AddRange(akSongs);
                    int limit = akSongs.Count > 0 ? akSongs.Max(s => s.UsedBankEntries) : 0;
                    if (limit <= 0) limit = int.MaxValue;

                    int bn = 0;
                    foreach (var akb in banks)
                    {
                        bn++;
                        int made = 0, loops = 0, empty = 0;
                        foreach (var smp in akb.Samples)
                        {
                            if (smp.Index >= limit) continue;
                            if (smp.Adpcm == null || smp.Adpcm.Length < 32) continue;
                            bool isEmpty = AllZero(smp.Adpcm);
                            psfRows.Add(new PsfRow
                            {
                                Name = "akao" + (banks.Count > 1 ? bn + "_" : "_") + smp.Index.ToString("00") + (isEmpty ? "_empty" : "_key" + smp.UnityKey),
                                Source = isEmpty ? "AKAO (empty)" : "AKAO",
                                Offset = (int)smp.FileOffset,
                                Raw = smp.Adpcm,
                                Loops = !isEmpty && smp.Loops
                            });
                            made++;
                            if (isEmpty) empty++;
                            else if (smp.Loops) loops++;
                        }
                        extra.AppendLine("AKAO sample bank " + bn + " at 0x" + akb.MarkerOffset.ToString("X") + ": " + akb.Entries +
                                         " table entries, " + made + " shown" + (limit != int.MaxValue ? " (the song uses entries 0-" + (limit - 1) + "; the rest are identical empty placeholders)" : "") +
                                         ", " + loops + " looping, " + empty + " empty, data " + akb.DataSize.ToString("N0") + " bytes.");
                    }
                    foreach (var sd in akSongs) extra.AppendLine(sd.Summary(sd.AutoBpm));
                }
                else if (vabs.Count > 0)
                {
                    int vn = 0;
                    foreach (var v in vabs)
                    {
                        vn++;
                        int pos = v.DataOffset;
                        for (int i = 0; i < v.VagSizes.Length; i++)
                        {
                            int size = v.VagSizes[i];
                            if (size <= 0) continue;
                            int take = Math.Min(size, b.Data.Length - pos) & ~15;
                            if (take < 16) break;
                            var raw = new byte[take];
                            Array.Copy(b.Data, pos, raw, 0, take);
                            psfRows.Add(new PsfRow
                            {
                                Name = "vab" + vn + "_vag" + (i + 1).ToString("000"),
                                Source = "VAB " + vn,
                                Offset = pos,
                                Raw = raw,
                                Loops = PsfSampleFinder.RawLoops(raw)
                            });
                            pos += size;
                        }
                    }
                }
                else
                {
                    int k = 0;
                    foreach (var f in PsfSampleFinder.Find(b.Data))
                    {
                        var raw = new byte[f.Size];
                        Array.Copy(b.Data, f.Offset, raw, 0, f.Size);
                        psfRows.Add(new PsfRow
                        {
                            Name = "sample_" + (k++).ToString("000") + "_" + f.Offset.ToString("X6"),
                            Source = "scan",
                            Offset = f.Offset,
                            Raw = raw,
                            Loops = f.Loops
                        });
                    }
                }
            }

            // AKAO songs are searched in EVERY buffer, even when no AKAO sample bank was recognised in it
            foreach (var pbuf in pkg.Buffers)
            {
                var foundSongs = AkaoSongReader.Find(pbuf.Data);
                foreach (var sg in foundSongs)
                    if (!psfAkao.Any(a => a.Offset == sg.Offset)) psfAkao.Add(sg);
                if (foundSongs.Count == 0)
                {
                    string probe = AkaoSongReader.Explain(pbuf.Data);
                    if (probe.Length > 0) extra.AppendLine(probe);
                }
            }

            // songs: Sony SEQ -> MIDI
            int sn = 0;
            foreach (var b in pkg.Buffers)
            {
                foreach (var si in b.Scan.Seqs.Where(x => x.Valid))
                {
                    string note;
                    byte[] smf = PsfSeq.ToSmf(b.Data, si.Offset, out note);
                    if (smf == null) { extra.AppendLine("SEQ at 0x" + si.Offset.ToString("X") + " skipped: " + note); continue; }
                    sn++;
                    try
                    {
                        var song = SqSong.FromSmf(smf, "SEQ " + sn + " @0x" + si.Offset.ToString("X"));
                        song.Index = -1;
                        psfSeqItems.Add(new SongItem
                        {
                            Song = song,
                            Label = $"{song.Name}   [{FormatClock(song.DurationSeconds)} | {song.NoteCount} notes | {song.Ppqn} ppqn{(song.HasLoop ? " | LOOP" : "")}]"
                        });
                        if (note.Length > 0) extra.AppendLine("SEQ " + sn + ": " + note);
                    }
                    catch (Exception ex) { extra.AppendLine("SEQ at 0x" + si.Offset.ToString("X") + " could not be converted: " + ex.Message); }
                }
            }
            string akaoProblem = RebuildPsfSongs();
            if (akaoProblem.Length > 0) extra.AppendLine(akaoProblem);
            if (cmbPsfSong.Items.Count > 0) cmbPsfSong.SelectedIndex = 0;

            // report
            var rep = new StringBuilder();
            foreach (string n in pkg.Notes) rep.AppendLine(n);
            foreach (var kv in pkg.Tags) rep.AppendLine("  " + kv.Key + ": " + kv.Value);
            foreach (var b in pkg.Buffers)
            {
                rep.AppendLine();
                rep.AppendLine("--- " + b.Name + " ---");
                rep.Append(b.Scan.Report());
            }
            if (extra.Length > 0) { rep.AppendLine(); rep.Append(extra.ToString()); }
            tbPsfReport.Text = rep.ToString();
            tbPsfReport.SelectionStart = 0; tbPsfReport.SelectionLength = 0;

            PopulatePsfRows();
            tabs.SelectedTab = tabPsf;
            UpdateTitle();
            lblPsfInfo.Text = Path.GetFileName(path) + " | " + psfRows.Count + " sample(s) | " + cmbPsfSong.Items.Count + " song(s) | preview rate " + psfRate + " Hz";
            SetStatus($"Opened {Path.GetFileName(path)}: {psfRows.Count} sample(s), {cmbPsfSong.Items.Count} song(s)." +
                      (psfRows.Count == 0 && cmbPsfSong.Items.Count == 0 ? " Nothing recognised - see the report below the list." : ""));
            if (psfRows.Count > 0) lvPsf.Items[0].Selected = true;
            OfferSequenceIfNone(true);
        }

        /// <summary>Fills the song box: Sony SEQ songs plus the decoded AKAO songs at the current tempo. Returns a problem text ("" = fine).</summary>
        private string RebuildPsfSongs()
        {
            var problems = new StringBuilder();
            int keep = cmbPsfSong.SelectedIndex;
            cmbPsfSong.Items.Clear();
            psfGm.Clear();
            foreach (var it in psfSeqItems) cmbPsfSong.Items.Add(it);

            int an = 0;
            foreach (var d in psfAkao)
            {
                an++;
                double bpm = psfBpm > 0 ? psfBpm : d.AutoBpm;
                try
                {
                    string name = "AKAO song " + an + " @0x" + d.Offset.ToString("X");
                    var raw = SqSong.FromSmf(AkaoMidi.Build(d, false, bpm), name);
                    raw.Index = -1;
                    var gm = SqSong.FromSmf(AkaoMidi.Build(d, true, bpm), name + " (GM preview)");
                    gm.Index = -1;
                    psfGm[raw] = gm;
                    cmbPsfSong.Items.Add(new SongItem
                    {
                        Song = raw,
                        Label = $"{name}   [{FormatClock(raw.DurationSeconds)} | {raw.NoteCount} notes | {bpm:F0} bpm{(raw.HasLoop ? " | LOOP" : "")}]"
                    });
                }
                catch (Exception ex) { problems.AppendLine("AKAO song at 0x" + d.Offset.ToString("X") + " could not be converted: " + ex.Message); }
            }
            if (keep >= 0 && keep < cmbPsfSong.Items.Count) cmbPsfSong.SelectedIndex = keep;
            return problems.ToString();
        }

        private void AskPsfTempo()
        {
            if (psfAkao.Count == 0) { SetStatus("Tempo applies to AKAO songs - open a Square .psf first."); return; }
            double auto = psfAkao[0].AutoBpm;
            double cur = psfBpm > 0 ? psfBpm : auto;
            double v;
            if (!Prompt.Number(this, "Tempo", "Tempo in BPM for the AKAO song.\nAutomatic value from the song's tempo word: " + auto.ToString("F1") + "\nIf it sounds too fast or too slow, change it here.", cur, out v)) return;
            psfBpm = Math.Max(20, Math.Min(400, v));
            TransportStop();
            string problem = RebuildPsfSongs();
            if (cmbPsfSong.Items.Count > 0 && cmbPsfSong.SelectedIndex < 0) cmbPsfSong.SelectedIndex = 0;
            SetStatus("Tempo set to " + psfBpm.ToString("F1") + " BPM." + (problem.Length > 0 ? " " + problem.Trim() : ""));
        }

        private void PopulatePsfRows()
        {
            lvPsf.BeginUpdate();
            lvPsf.Items.Clear();
            for (int i = 0; i < psfRows.Count; i++)
            {
                var r = psfRows[i];
                double secs = r.Raw.Length / 16 * 28.0 / Math.Max(1, psfRate);
                var it = new ListViewItem(new[]
                {
                    i.ToString(), r.Source, "0x" + r.Offset.ToString("X"), r.Raw.Length.ToString("N0"),
                    secs.ToString("F2") + "s", r.Loops ? "LOOP" : "-"
                })
                { Tag = i };
                if (r.Source.Contains("empty")) it.ForeColor = Color.Gray;
                lvPsf.Items.Add(it);
            }
            lvPsf.EndUpdate();
        }

        // ================================================================= samples

        private AudioBuffer DecodePsfRow(PsfRow r)
        {
            if (r.Decoded == null || r.DecodedRate != psfRate)
            {
                r.Decoded = VagCodec.DecodeAdpcm(r.Raw, 0, r.Raw.Length, psfRate);
                r.DecodedRate = psfRate;
            }
            return r.Decoded;
        }

        private int SelectedPsfIndex()
        {
            if (lvPsf == null || lvPsf.SelectedItems.Count == 0) return -1;
            return (int)lvPsf.SelectedItems[0].Tag;
        }

        private void ShowPsfSample(int i)
        {
            if (i < 0 || i >= psfRows.Count) return;
            var r = psfRows[i];
            AudioBuffer b;
            try { b = DecodePsfRow(r); }
            catch (Exception ex) { SetStatus("Cannot decode this sample: " + ex.Message); return; }
            psfWave.Buffer = b.Clone();
            SyncLoopBox(chkLoopPsf, psfWave);
            lblPsfView.Text = $"{r.Name}  |  {psfRate} Hz  |  {b.DurationSeconds:F3}s  |  {(r.Loops ? "LOOP" : "one-shot")}  |  at 0x{r.Offset:X}  {r.Raw.Length:N0} bytes";
        }

        private void PlayPsfSample()
        {
            int i = SelectedPsfIndex();
            if (i < 0) { SetStatus("Select a sample first."); return; }
            ShowPsfSample(i);
            PlayWave(psfWave);
        }

        private void PsfToEditor()
        {
            int i = SelectedPsfIndex();
            if (i < 0) { SetStatus("Select a sample first."); return; }
            try { SetEditorBuffer(DecodePsfRow(psfRows[i]).Clone(), psfRows[i].Name + ".vag"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open in editor"); }
        }

        private void AskPsfRate()
        {
            double v;
            if (!Prompt.Number(this, "Sample rate", "Sample rate in Hz for previewing and extracting these samples.\nPS1 banks usually play at 44100 Hz at the base note.", psfRate, out v)) return;
            psfRate = (int)Math.Max(1000, Math.Min(192000, v));
            PopulatePsfRows();
            int i = SelectedPsfIndex();
            if (i >= 0) ShowPsfSample(i);
            lblPsfInfo.Text = (psfPath != null ? Path.GetFileName(psfPath) + " | " : "") + psfRows.Count + " sample(s) | preview rate " + psfRate + " Hz";
        }

        /// <summary>A .vag file: 0x30-byte header (big-endian size and rate) followed by the raw ADPCM blocks.</summary>
        private static byte[] MakeVag(byte[] raw, int rate, string name)
        {
            var o = new byte[0x30 + raw.Length];
            o[0] = (byte)'V'; o[1] = (byte)'A'; o[2] = (byte)'G'; o[3] = (byte)'p';
            o[7] = 0x20;
            o[0x0C] = (byte)(raw.Length >> 24); o[0x0D] = (byte)(raw.Length >> 16); o[0x0E] = (byte)(raw.Length >> 8); o[0x0F] = (byte)raw.Length;
            o[0x10] = (byte)(rate >> 24); o[0x11] = (byte)(rate >> 16); o[0x12] = (byte)(rate >> 8); o[0x13] = (byte)rate;
            string nm = name ?? "";
            for (int i = 0; i < 16 && i < nm.Length; i++) o[0x20 + i] = (byte)(nm[i] < 128 ? nm[i] : '_');
            Array.Copy(raw, 0, o, 0x30, raw.Length);
            return o;
        }

        private void WritePsfRow(int i, string path, bool wav)
        {
            var r = psfRows[i];
            if (wav) StandardCodec.EncodeWav(DecodePsfRow(r), path);
            else File.WriteAllBytes(path, MakeVag(r.Raw, psfRate, r.Name));
        }

        private void ExtractPsf(bool wav, bool all)
        {
            var idx = all
                ? Enumerable.Range(0, psfRows.Count).ToList()
                : lvPsf.SelectedItems.Cast<ListViewItem>().Select(it => (int)it.Tag).OrderBy(x => x).ToList();
            if (idx.Count == 0) { SetStatus("Nothing to extract: open a PSF and select samples."); return; }

            try
            {
                if (idx.Count == 1)
                {
                    using var d = new SaveFileDialog { Filter = wav ? "WAV|*.wav" : "VAG|*.vag", FileName = psfRows[idx[0]].Name, AddExtension = true };
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    WritePsfRow(idx[0], d.FileName, wav);
                    SetStatus("Extracted: " + d.FileName);
                    return;
                }
                using var fb = new FolderBrowserDialog { Description = "Extract " + idx.Count + " samples as " + (wav ? ".wav" : ".vag") + " into a folder" };
                if (fb.ShowDialog(this) != DialogResult.OK) return;
                foreach (int i in idx)
                    WritePsfRow(i, Path.Combine(fb.SelectedPath, SafeFile(psfRows[i].Name) + (wav ? ".wav" : ".vag")), wav);
                SetStatus("Extracted " + idx.Count + " sample(s) to " + fb.SelectedPath);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Extract error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void DragOutPsf()
        {
            var files = new DragFiles();
            int rate = psfRate;
            foreach (ListViewItem it in lvPsf.SelectedItems)
            {
                var r = psfRows[(int)it.Tag];
                Drag(files, SafeFile(r.Name) + ".vag", () => MakeVag(r.Raw, rate, r.Name));
            }
            StartDragOut(lvPsf, files);
        }

        // ================================================================= songs

        private SqSong SelectedPsfSong() { return (cmbPsfSong.SelectedItem as SongItem)?.Song; }

        /// <summary>For AKAO songs the General MIDI copy (sounds right in any MIDI player); other songs as they are.</summary>
        private SqSong PsfListenSong(SqSong song)
        {
            SqSong g;
            return song != null && psfGm.TryGetValue(song, out g) ? g : song;
        }

        private void PlayPsfSong()
        {
            var song = SelectedPsfSong();
            if (song == null) { MessageBox.Show(this, "No song in this file.", "MIDI player"); return; }
            PlayMidiSong(PsfListenSong(song), chkLoopPsf.Checked);
        }

        private void SavePsfSong(bool asSq, bool asSeq = false)
        {
            var song = SelectedPsfSong();
            if (song == null) { MessageBox.Show(this, "No song in this file.", "Save song"); return; }
            string stem = psfPath != null ? Path.GetFileNameWithoutExtension(psfPath) : "song";
            using var d = new SaveFileDialog
            {
                Filter = "Standard MIDI (*.mid)|*.mid|SCEI SQ (*.sq)|*.sq|Sony PlayStation SEQ (*.seq)|*.seq",
                FilterIndex = asSeq ? 3 : (asSq ? 2 : 1),
                FileName = SafeFile(stem + "_" + Math.Max(0, cmbPsfSong.SelectedIndex)),
                AddExtension = true
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                string ext = Path.GetExtension(d.FileName).ToLowerInvariant();
                SqSong outSong = song;
                string note = "";
                var ak = PfsAkaoFor(song);
                if (ak != null)
                {
                    // AKAO song: save the SAME song the SF2 export uses (driver export), not the raw game song
                    bool fx = chkPfsEffects != null && chkPfsEffects.Checked;
                    string why;
                    PsxExport px = BuildPfsDriverExport(song, ak, fx, out why);
                    if (px != null)
                    {
                        outSong = px.Song;
                        note = " (same song as the SF2 export)";
                    }
                    else
                    {
                        if (MessageBox.Show(this, "The exact song for the SF2 could not be built:\n\n" + why +
                            "\n\nSave the game's raw song instead? It will NOT match an exported SF2.",
                            "Save song", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                        outSong = ext == ".mid" ? PsfListenSong(song) : song;
                    }
                }
                byte[] data;
                if (ext == ".sq") data = SqFile.BuildNew(new List<SqSong> { outSong }, null);
                else if (ext == ".seq")
                {
                    string rep;
                    data = SmfSeq.FromMidi(outSong.ToSmf(), out rep);
                    string last = rep.TrimEnd().Split('\n').Last().Trim();
                    note += " | " + last;
                }
                else data = outSong.ToSmf();
                File.WriteAllBytes(d.FileName, data);
                SetStatus("Saved: " + d.FileName + note);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save song", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void PsfSongToPsf2()
        {
            var song = SelectedPsfSong();
            if (song == null) { MessageBox.Show(this, "No song in this file.", "Send to PSF2 tab"); return; }

            bool withBank = false;
            string how = "";
            var d = PfsAkaoFor(song);
            if (d != null)
            {
                bool fx = chkPfsEffects != null && chkPfsEffects.Checked;
                string why;
                PsxExport px = BuildPfsDriverExport(song, d, fx, out why);
                if (px != null)
                {
                    try
                    {
                        string stem = SafeFile(song.Name ?? "psf");
                        var nb = new Psf2Bank();
                        nb.LoadFromBytes(px.Hd, px.Bd, stem + ".hd", stem + ".bd");
                        px.Song.Index = -1;
                        psf2Template = null; psf2Name = null;
                        ApplyBank(nb);
                        song = px.Song;
                        withBank = true;
                        how = "from the game's driver, effects " + (fx ? "ON" : "OFF");
                        if (chkPsf2Effects != null) chkPsf2Effects.Checked = fx;
                        tbPsfReport.AppendText(Environment.NewLine + "--- Sent to PSF2 tab (game's driver) ---" + Environment.NewLine + px.Report);
                    }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "Send to PSF2 tab", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                }
                else
                {
                    if (!pfsBankEdited && !AskPfsApproxExport(why)) return;
                    string err;
                    var bk = EnsurePfsBank(d, out err);
                    if (bk == null) { MessageBox.Show(this, err ?? "No bank.", "Send to PSF2 tab"); return; }
                    try
                    {
                        byte[] hd, bd;
                        bk.BuildFiles(out hd, out bd);
                        var nb = new Psf2Bank();
                        nb.LoadFromBytes(hd, bd, bk.HdPath, bk.BdPath);     // an independent copy for the PSF2 tab
                        psf2Template = null; psf2Name = null;
                        ApplyBank(nb);
                        withBank = true;
                        how = "APPROXIMATE (AKAO bank + song)";
                    }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "Send to PSF2 tab", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                }
            }

            if (!songs.Contains(song)) songs.Add(song);
            RefreshSongList(song);
            tabs.SelectedTab = tabPsf2;
            SetStatus(withBank
                ? "Bank + song sent to the PSF2 tab (" + how + "). Press Play song there to render it."
                : "Song sent to the PSF2 tab. Load an HD + BD there to render it, or use the MIDI player.");
        }

        // ================================================================= dump / scan

        private void DumpPsfData()
        {
            if (psfPackage == null) { MessageBox.Show(this, "Open a .psf first.", "Dump"); return; }
            string text;
            try { text = PsfDump.Build(psfPackage); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Dump", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

            using var d = new SaveFileDialog
            {
                Filter = "Text|*.txt",
                FileName = Path.GetFileNameWithoutExtension(psfPackage.FilePath) + "_dump.txt",
                AddExtension = true
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(d.FileName, text, Encoding.UTF8);
                try { Clipboard.SetText(text); } catch { }
                SetStatus("Dump saved (and copied to the clipboard): " + d.FileName);
                MessageBox.Show(this, "Saved " + text.Length.ToString("N0") + " characters to\n" + d.FileName + "\n\nThe text is also on your clipboard.", "Dump");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Dump", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ScanPsfFile(string path)
        {
            OpenPsfFile(path);
        }
    }
}
