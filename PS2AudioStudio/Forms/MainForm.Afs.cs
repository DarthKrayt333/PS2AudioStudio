using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PS2AudioStudio.Archives;
using PS2AudioStudio.Audio;
using PS2AudioStudio.Controls;
using DragFiles = System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, System.Func<byte[]>>>;

namespace PS2AudioStudio
{
    public partial class MainForm
    {
        private AfsArchive afs = new AfsArchive();
        private ListView lvAfs;
        private WaveformControl afsWave;
        private Label lblAfsInfo, lblAfsView;
        private CheckBox chkLoopAfs;
        private SplitContainer splitAfs;

        // ================================================================= tab
        private TabPage BuildAfsTab()
        {
            var p = new TabPage("📦 AFS Archive");

            var bar = Ui.Bar1(40);
            var bOpen = Ui.Btn("📂 Open AFS..."); bOpen.Click += (s, e) => OpenAfsDlg();
            var bNew = Ui.Btn("🆕 New AFS"); bNew.Click += (s, e) => { afs = new AfsArchive(); afs.New(); PopulateAfs(); tabs.SelectedTab = tabAfs; };
            var bSave = Ui.Btn("💾 Save AFS"); bSave.Click += (s, e) => SaveAfs(false);
            var bSaveAs = Ui.Btn("💾 Save AFS as..."); bSaveAs.Click += (s, e) => SaveAfs(true);
            var bExtAll = Ui.Btn("📁 Extract all..."); bExtAll.Click += (s, e) => ExtractAllAfs();
            var bAdd = Ui.Btn("➕ Add file(s)..."); bAdd.Click += (s, e) => AddAfsDlg();
            bar.Controls.AddRange(new Control[] { bOpen, bNew, bSave, bSaveAs, bExtAll, bAdd });

            lblAfsInfo = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = Color.Yellow, BackColor = Ui.Panel, Padding = new Padding(6, 4, 0, 0), Font = new Font("Consolas", 9) };
            lblAfsInfo.Text = "Drop an .AFS file here. Right-click an entry for Extract / Replace / Rename / Remove. Drag entries out to Explorer to extract them.";

            splitAfs = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };

            lvAfs = new ListView { Dock = DockStyle.Fill };
            Ui.StyleList(lvAfs);
            lvAfs.Columns.Add("#", 55); lvAfs.Columns.Add("Name", 230); lvAfs.Columns.Add("Offset", 95);
            lvAfs.Columns.Add("Size", 95); lvAfs.Columns.Add("Type", 60); lvAfs.Columns.Add("Info", 260);
            lvAfs.SelectedIndexChanged += (s, e) => PreviewSelectedAfs(false);
            lvAfs.DoubleClick += (s, e) => PreviewSelectedAfs(true);
            lvAfs.ItemDrag += (s, e) => DragOutAfs();
            lvAfs.ContextMenuStrip = BuildAfsMenu();
            lvAfs.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) { RemoveAfsEntry(); e.Handled = true; } };

            lblAfsView = new Label { Dock = DockStyle.Fill, ForeColor = Color.LightSkyBlue, Padding = new Padding(6, 4, 0, 0), AutoEllipsis = true, Text = "Select an audio entry to preview it." };
            var afsInfo = new Label { Dock = DockStyle.Bottom, Height = 22, BackColor = Ui.Panel, ForeColor = Color.LightGreen, Font = new Font("Consolas", 9), Padding = new Padding(5, 4, 0, 0) };
            afsWave = new WaveformControl { Dock = DockStyle.Fill, InfoLabel = afsInfo, EmptyText = "Select an ADX / VAG / WAV entry to see its waveform" };
            chkLoopAfs = MakeLoopBox(afsWave);
            afsWave.LoopChanged += (s, e) => SyncLoopBox(chkLoopAfs, afsWave);

            var hdr = new Panel { Dock = DockStyle.Top, Height = 24 };
            hdr.Controls.Add(lblAfsView);
            hdr.Controls.Add(chkLoopAfs);

            splitAfs.Panel1.Controls.Add(lvAfs);
            splitAfs.Panel2.Controls.Add(afsWave);
            splitAfs.Panel2.Controls.Add(afsInfo);
            splitAfs.Panel2.Controls.Add(hdr);

            p.Controls.Add(splitAfs);
            p.Controls.Add(lblAfsInfo);
            p.Controls.Add(bar);
            return p;
        }

        private void InitAfsSplitters()
        {
            splitAfs.Panel1MinSize = 100; splitAfs.Panel2MinSize = 120;
            splitAfs.SplitterDistance = Math.Max(100, Math.Min(splitAfs.Height - 120, (int)(splitAfs.Height * 0.55)));
        }

        private ContextMenuStrip BuildAfsMenu()
        {
            var m = Ui.Ctx();
            m.Items.Add(Mi("▶ Play / preview", (s, e) => PreviewSelectedAfs(true)));
            m.Items.Add(Mi("Open in Audio Editor", (s, e) => SendAfsToEditor()));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Mi("Extract...", (s, e) => ExtractAfsEntry(false)));
            m.Items.Add(Mi("Extract as .wav...", (s, e) => ExtractAfsEntry(true)));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Mi("Replace with file...", (s, e) => ReplaceAfsDlg()));
            m.Items.Add(Mi("Replace with Audio Editor contents (encoded as this entry's format)", (s, e) => ReplaceAfsFromEditor()));
            m.Items.Add(Mi("Rename...", (s, e) => RenameAfsEntry()));
            m.Items.Add(Mi("Remove", (s, e) => RemoveAfsEntry()));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Mi("Add file(s)...", (s, e) => AddAfsDlg()));
            m.Items.Add(Mi("Save AFS", (s, e) => SaveAfs(false)));
            m.Items.Add(Mi("Save AFS as...", (s, e) => SaveAfs(true)));
            return m;
        }

        private void ClearAfsTab()
        {
            TransportStop();
            afs = new AfsArchive();
            afs.New();
            afsWave.Buffer = null;
            SyncLoopBox(chkLoopAfs, afsWave);
            lblAfsView.Text = "Select an audio entry to preview it.";
            PopulateAfs();
        }

        // ================================================================= drag out

        private void DragOutAfs()
        {
            var files = new DragFiles();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int idx in lvAfs.SelectedIndices)
            {
                if (idx < 0 || idx >= afs.Entries.Count) continue;
                var en = afs.Entries[idx];
                string n = SafeFile(AfsArchive.SafeName(en.FileName), "entry_" + idx);
                if (!used.Add(n))
                {
                    n = Path.GetFileNameWithoutExtension(n) + "_" + idx + Path.GetExtension(n);
                    used.Add(n);
                }
                Drag(files, n, () => en.Data);
            }
            StartDragOut(lvAfs, files);
        }

        // ================================================================= list

        private static string AfsInfo(AfsEntry e)
        {
            var d = e.Data;
            try
            {
                switch (e.Kind)
                {
                    case FileKind.Adx:
                        {
                            int sr = (d[8] << 24) | (d[9] << 16) | (d[10] << 8) | d[11];
                            int total = (d[12] << 24) | (d[13] << 16) | (d[14] << 8) | d[15];
                            var lp = AdxLoopInfo.Read(d);
                            return $"{sr} Hz, {d[7]} ch, {(sr > 0 ? (double)total / sr : 0):F2}s"
                                + (lp.HasLoop ? $", LOOP {lp.StartSample}→{lp.EndSample}" : "");
                        }
                    case FileKind.Vag:
                        {
                            int sr = (d[0x10] << 24) | (d[0x11] << 16) | (d[0x12] << 8) | d[0x13];
                            int size = (d[0x0C] << 24) | (d[0x0D] << 16) | (d[0x0E] << 8) | d[0x0F];
                            double secs = sr > 0 ? size / 16.0 * 28 / sr : 0;
                            return $"{sr} Hz, mono, {secs:F2}s";
                        }
                    case FileKind.Wav:
                        {
                            int sr = BitConverter.ToInt32(d, 0x18);
                            return $"{sr} Hz, {BitConverter.ToInt16(d, 0x16)} ch";
                        }
                }
            }
            catch { }
            return "";
        }

        private void PopulateAfs()
        {
            lvAfs.BeginUpdate();
            lvAfs.Items.Clear();
            foreach (var e in afs.Entries)
            {
                var k = e.Kind;
                var it = new ListViewItem(new[]
                {
                    e.Index.ToString(), e.FileName, "0x" + e.Offset.ToString("X"), e.Size.ToString("N0"),
                    AudioIO.KindLabel(k), AfsInfo(e)
                });
                if (e.Modified) it.ForeColor = Color.Orange;
                lvAfs.Items.Add(it);
            }
            lvAfs.EndUpdate();
            lblAfsInfo.Text = afs.Entries.Count == 0
                ? "Empty archive."
                : $"{(afs.Path != null ? Path.GetFileName(afs.Path) : "(new archive)")} | {afs.Entries.Count} entries | filename table: {(afs.HasNames ? "yes" : "no")}" + (afs.Modified ? " | UNSAVED changes" : "");
            UpdateTitle();
        }

        private int SelAfs() => lvAfs.SelectedIndices.Count == 0 ? -1 : lvAfs.SelectedIndices[0];

        private void RefreshAfsRow(int i)
        {
            if (i < 0 || i >= lvAfs.Items.Count) { PopulateAfs(); return; }
            var e = afs.Entries[i];
            var it = lvAfs.Items[i];
            it.SubItems[1].Text = e.FileName;
            it.SubItems[3].Text = e.Size.ToString("N0");
            it.SubItems[4].Text = AudioIO.KindLabel(e.Kind);
            it.SubItems[5].Text = AfsInfo(e);
            it.ForeColor = e.Modified ? Color.Orange : Color.White;
            lblAfsInfo.Text = $"{(afs.Path != null ? Path.GetFileName(afs.Path) : "(new archive)")} | {afs.Entries.Count} entries" + (afs.Modified ? " | UNSAVED changes" : "");
            UpdateTitle();
        }

        // ================================================================= open / save

        private void OpenAfsDlg()
        {
            using var d = new OpenFileDialog { Filter = "AFS|*.afs;*.AFS|All|*.*", Title = "Open AFS archive" };
            if (d.ShowDialog(this) == DialogResult.OK) LoadAfsFile(d.FileName);
        }

        private void LoadAfsFile(string path)
        {
            var na = new AfsArchive();
            na.Open(path);
            afs = na;
            PopulateAfs();
            afsWave.Buffer = null;
            SyncLoopBox(chkLoopAfs, afsWave);
            tabs.SelectedTab = tabAfs;
            SetStatus($"Opened {Path.GetFileName(path)}: {afs.Entries.Count} entries.");
        }

        private void SaveAfs(bool saveAs)
        {
            if (afs.Entries.Count == 0) { MessageBox.Show(this, "The archive is empty.", "Save AFS"); return; }
            tabs.SelectedTab = tabAfs;
            string path = afs.Path;
            if (saveAs || path == null)
            {
                using var d = new SaveFileDialog { Filter = "AFS|*.afs", FileName = path != null ? Path.GetFileName(path) : "new.afs", AddExtension = true, InitialDirectory = path != null ? Path.GetDirectoryName(path) : null };
                if (d.ShowDialog(this) != DialogResult.OK) return;
                path = d.FileName;
            }
            else if (MessageBox.Show(this, $"Overwrite\n{path}" + (AppSettings.Current.MakeBackups ? "\n\n(a .bak copy of the original is made once)" : ""), "Save AFS", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            try
            {
                if (File.Exists(path) && afs.Path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(afs.Path), StringComparison.OrdinalIgnoreCase)) BackupOnce(path);
                afs.Save(path);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            PopulateAfs();
            SetStatus("Saved AFS: " + path);
            MessageBox.Show(this, "Saved\n" + path, "Saved");
        }

        // ================================================================= preview

        private void PreviewSelectedAfs(bool play)
        {
            int i = SelAfs();
            if (i < 0 || i >= afs.Entries.Count) return;
            var e = afs.Entries[i];
            if (!AudioIO.IsAudio(e.Kind))
            {
                afsWave.Buffer = null;
                SyncLoopBox(chkLoopAfs, afsWave);
                lblAfsView.Text = $"#{i} {e.FileName}: {AudioIO.KindLabel(e.Kind)} data - not previewable (use Extract / Replace).";
                return;
            }
            if (e.Data.Length > 64 * 1024 * 1024) { lblAfsView.Text = "Entry too large to preview."; return; }
            try
            {
                var b = AudioIO.Decode(e.Data, e.FileName);
                afsWave.Buffer = b;
                SyncLoopBox(chkLoopAfs, afsWave);
                lblAfsView.Text = $"#{i}  {e.FileName}  |  {AudioIO.KindLabel(e.Kind)}  |  {b.SampleRate} Hz  {b.Channels} ch  {b.DurationSeconds:F2}s" + (b.LoopEnabled ? $"  |  LOOP {b.LoopStart}→{b.LoopEnd}" : "");
                if (play) PlayWave(afsWave);
            }
            catch (RawImportRequiredException)
            {
                afsWave.Buffer = null;
                SyncLoopBox(chkLoopAfs, afsWave);
                lblAfsView.Text = $"#{i}: headerless / interleaved VAG - extract it and use File > Open raw PS-ADPCM.";
            }
            catch (Exception ex)
            {
                afsWave.Buffer = null;
                SyncLoopBox(chkLoopAfs, afsWave);
                lblAfsView.Text = $"#{i}: cannot decode ({ex.Message})";
            }
        }

        private void SendAfsToEditor()
        {
            int i = SelAfs(); if (i < 0) return;
            var e = afs.Entries[i];
            try
            {
                var b = AudioIO.Decode(e.Data, e.FileName);
                SetEditorBuffer(b, e.FileName);
            }
            catch (RawImportRequiredException rx)
            {
                var b = ShowRawImport(rx.RawData, e.FileName);
                if (b != null) SetEditorBuffer(b, e.FileName);
            }
            catch (Exception ex) { MessageBox.Show(this, "Cannot decode this entry as audio:\n" + ex.Message, "Open in editor"); }
        }

        // ================================================================= extract

        private void ExtractAfsEntry(bool asWav)
        {
            int i = SelAfs(); if (i < 0) { SetStatus("Select an entry first."); return; }
            var e = afs.Entries[i];
            using var d = new SaveFileDialog { FileName = asWav ? Path.GetFileNameWithoutExtension(e.FileName) + ".wav" : AfsArchive.SafeName(e.FileName), AddExtension = true };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                if (asWav) StandardCodec.EncodeWav(AudioIO.Decode(e.Data, e.FileName), d.FileName);
                else File.WriteAllBytes(d.FileName, e.Data);
                SetStatus("Extracted: " + d.FileName);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Extract error"); }
        }

        private void ExtractAllAfs()
        {
            if (afs.Entries.Count == 0) { MessageBox.Show(this, "Open an AFS first.", "Extract"); return; }
            using var d = new FolderBrowserDialog { Description = "Extract all entries to a folder" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            afs.ExtractAll(d.SelectedPath);
            SetStatus($"Extracted {afs.Entries.Count} files to {d.SelectedPath}");
        }

        // ================================================================= replace / add / remove / rename

        private void ReplaceAfsDlg()
        {
            int i = SelAfs(); if (i < 0) { SetStatus("Select an entry first."); return; }
            using var d = new OpenFileDialog { Filter = "All files|*.*", Title = $"Replace #{i} ({afs.Entries[i].FileName}) with..." };
            if (d.ShowDialog(this) == DialogResult.OK) ReplaceAfsEntryWithFile(i, d.FileName);
        }

        private void ReplaceAfsEntryWithFile(int i, string path)
        {
            try
            {
                var entry = afs.Entries[i];
                byte[] src = File.ReadAllBytes(path);
                FileKind sk = AudioIO.Detect(src, path), dk = entry.Kind;
                byte[] outData = src;

                bool encodable = dk == FileKind.Adx || dk == FileKind.Vag || dk == FileKind.Wav;
                if (AudioIO.IsAudio(sk) && encodable && sk != dk)
                {
                    var r = MessageBox.Show(this,
                        $"Entry #{i} is {AudioIO.KindLabel(dk)} but the new file is {AudioIO.KindLabel(sk)}.\n\nYes = convert it to {AudioIO.KindLabel(dk)} (recommended)\nNo = insert the file unchanged\nCancel = abort",
                        "Replace entry", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Cancel) return;
                    if (r == DialogResult.Yes)
                    {
                        var b = LoadAudioForReplace(path);
                        if (b == null) return;
                        outData = AudioIO.Encode(b, dk, entry.FileName);
                    }
                }
                afs.ReplaceData(i, outData);
                RefreshAfsRow(i);
                SetStatus($"Entry #{i} replaced ({outData.Length:N0} bytes). Use Save AFS to write the archive.");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Replace error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ReplaceAfsFromEditor()
        {
            int i = SelAfs(); if (i < 0) { SetStatus("Select an entry first."); return; }
            var b = wave.Buffer;
            if (b == null || b.TotalFrames == 0) { MessageBox.Show(this, "The Audio Editor is empty.", "Replace"); return; }
            var entry = afs.Entries[i];
            var kind = entry.Kind;
            if (kind != FileKind.Adx && kind != FileKind.Vag && kind != FileKind.Wav) kind = FileKind.Adx;
            try
            {
                b.LoopEnabled = chkLoop.Checked && b.LoopEnd > b.LoopStart;
                afs.ReplaceData(i, AudioIO.Encode(b, kind, entry.FileName));
                RefreshAfsRow(i);
                SetStatus($"Entry #{i} replaced with editor audio as {AudioIO.KindLabel(kind)} (loop {(b.LoopEnabled ? "ON" : "OFF")}).");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Replace error"); }
        }

        private void AddAfsDlg()
        {
            using var d = new OpenFileDialog { Filter = "All files|*.*", Multiselect = true, Title = "Add files to the archive" };
            if (d.ShowDialog(this) == DialogResult.OK) AddAfsFiles(d.FileNames);
        }

        private void AddAfsFiles(string[] paths)
        {
            if (afs.Path == null && afs.Entries.Count == 0) afs.New();
            foreach (var p in paths)
            {
                if (!File.Exists(p)) continue;
                afs.Add(Path.GetFileName(p), File.ReadAllBytes(p));
            }
            PopulateAfs();
            tabs.SelectedTab = tabAfs;
            SetStatus($"Added {paths.Length} file(s). Use Save AFS to write the archive.");
        }

        private void RemoveAfsEntry()
        {
            int i = SelAfs(); if (i < 0) return;
            if (MessageBox.Show(this, $"Remove entry #{i} ({afs.Entries[i].FileName}) from the archive?\n(Later entries shift down by one - game code that uses fixed indices may break.)", "Remove", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            afs.RemoveAt(i);
            PopulateAfs();
        }

        private void RenameAfsEntry()
        {
            int i = SelAfs(); if (i < 0) return;
            string n = Prompt.Input(this, "Rename entry", "New file name (max 31 characters):", afs.Entries[i].FileName);
            if (string.IsNullOrWhiteSpace(n)) return;
            if (n.Length > 31) n = n.Substring(0, 31);
            afs.Entries[i].FileName = n;
            afs.Entries[i].Modified = true;
            afs.Modified = true;
            RefreshAfsRow(i);
        }
    }
}