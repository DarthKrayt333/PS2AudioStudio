using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PS2AudioStudio.Archives;
using PS2AudioStudio.Audio;

namespace PS2AudioStudio
{
    // PSF2 tab: loop Start/End boxes (frames, decimal) + saving the loop into SQ (+ HD + BD)
    public partial class MainForm
    {
        private NumericUpDown numP2LS, numP2LE;
        private Label lblP2Loop;
        private bool _p2Bulk;
        private AudioBuffer _p2Buf;
        private int _p2Ls = -1, _p2Le = -1, _p2Tot = -1;
        private bool _p2En;

        private NumericUpDown NewP2Num()
        {
            return new NumericUpDown
            {
                Width = 100,
                Minimum = 0,
                Maximum = 0,
                Enabled = false,
                BackColor = Ui.Edit,
                ForeColor = Color.White,
                Margin = new Padding(2, 6, 8, 0)
            };
        }

        private Control BuildPsf2LoopBar()
        {
            var box = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = Ui.Panel };
            var row1 = Ui.Bar1(40);
            var row2 = Ui.Bar1(40);

            numP2LS = NewP2Num(); numP2LE = NewP2Num();
            numP2LS.ValueChanged += (s, e) => OnPsf2LoopNum();
            numP2LE.ValueChanged += (s, e) => OnPsf2LoopNum();
            lblP2Loop = new Label { AutoSize = true, ForeColor = Color.LightSkyBlue, Margin = new Padding(6, 10, 0, 0), Text = "" };

            row1.Controls.Add(Ui.Lbl("Loop start (frames):"));
            row1.Controls.Add(numP2LS);
            row1.Controls.Add(Ui.Lbl("end:"));
            row1.Controls.Add(numP2LE);
            row1.Controls.Add(lblP2Loop);

            var bApply = Ui.Btn("📌 Apply loop to the SQ"); bApply.Click += (s, e) => ApplyPsf2Loop(true);
            var bSaveAll = Ui.Btn("💾 Save SQ + HD + BD with this loop..."); bSaveAll.Click += (s, e) => SavePsf2LoopAll();
            row2.Controls.Add(bApply);
            row2.Controls.Add(bSaveAll);

            box.Controls.Add(row2);
            box.Controls.Add(row1);

            // keeps the boxes in step with the waveform (buffer changes, edge dragging, Loop tick)
            var tm = new System.Windows.Forms.Timer { Interval = 200 };
            tm.Tick += (s, e) => SyncPsf2LoopNums(false);
            tm.Start();
            box.Disposed += (s, e) => tm.Dispose();
            return box;
        }

        private void SyncPsf2LoopNums(bool force)
        {
            if (numP2LS == null || psf2Wave == null) return;
            var b = psf2Wave.Buffer;
            int ls = b == null ? 0 : b.LoopStart, le = b == null ? 0 : b.LoopEnd;
            int tot = b == null ? 0 : b.TotalFrames;
            bool en = b != null && b.LoopEnabled;
            if (!force && ReferenceEquals(b, _p2Buf) && ls == _p2Ls && le == _p2Le && tot == _p2Tot && en == _p2En) return;
            _p2Buf = b; _p2Ls = ls; _p2Le = le; _p2Tot = tot; _p2En = en;

            _p2Bulk = true;
            try
            {
                bool has = b != null && tot > 0;
                numP2LS.Enabled = has; numP2LE.Enabled = has;
                numP2LS.Maximum = Math.Max(0, tot); numP2LE.Maximum = Math.Max(0, tot);
                numP2LS.Value = Math.Max(0, Math.Min(tot, ls));
                numP2LE.Value = Math.Max(0, Math.Min(tot, le));
            }
            finally { _p2Bulk = false; }
            UpdatePsf2LoopLabel();
        }

        private void UpdatePsf2LoopLabel()
        {
            var b = psf2Wave.Buffer;
            if (b == null || b.TotalFrames == 0) { lblP2Loop.Text = ""; return; }
            double r = b.SampleRate;
            lblP2Loop.Text = $"{b.LoopStart / r:F3}s → {b.LoopEnd / r:F3}s  (length {(b.LoopEnd - b.LoopStart) / r:F3}s)" + (b.LoopEnabled ? "" : "   [Loop is OFF]");
        }

        private void OnPsf2LoopNum()
        {
            if (_p2Bulk) return;
            var b = psf2Wave.Buffer;
            if (b == null) return;
            int ls = (int)numP2LS.Value, le = (int)numP2LE.Value;
            if (le <= ls) { lblP2Loop.Text = "The loop end must be bigger than the start."; return; }
            b.LoopStart = ls; b.LoopEnd = le; b.LoopEnabled = true;
            SyncLoopBox(chkLoopPsf2, psf2Wave);
            psf2Wave.Invalidate();
            _p2Buf = b; _p2Ls = ls; _p2Le = le; _p2Tot = b.TotalFrames; _p2En = true;
            UpdatePsf2LoopLabel();
        }

        /// <summary>Puts the SQ loop markers (CC#99) on the rendered buffer as loop points.</summary>
        private static void ApplySongLoopToBuffer(AudioBuffer b, SqSong song)
        {
            if (b == null || song == null || !song.HasLoop || song.Events.Count == 0) return;
            double[] t = song.EventSeconds();
            double a = song.LoopStartIndex < 0 ? 0 : t[song.LoopStartIndex];
            double e = song.LoopEndIndex < 0 ? t[t.Length - 1] : t[song.LoopEndIndex];
            int ls = Math.Max(0, (int)Math.Round(a * b.SampleRate));
            int le = Math.Min(b.TotalFrames, (int)Math.Round(e * b.SampleRate));
            if (le - ls < 16) return;
            b.LoopStart = ls; b.LoopEnd = le; b.LoopEnabled = true;
        }

        private SqSong TwinSong(SqSong old)
        {
            if (old.Index >= 0 && sq != null && old.Index < sq.Songs.Count) return sq.Songs[old.Index];
            return old;
        }

        /// <summary>Writes the loop of the full-song view into the song's CC#99 markers. Returns the song (a new object for SQ songs), or null.</summary>
        private SqSong ApplyPsf2Loop(bool announce)
        {
            var song = SelectedSong();
            if (song == null) { SetStatus("No song selected."); return null; }
            var b = psf2Wave.Buffer;
            if (b == null || !renderCache.TryGetValue(song, out var rb) || !ReferenceEquals(rb, b))
            {
                SetStatus("Press 🎼 Show full song first - the loop is saved from the full rendered song, not from a sample.");
                return null;
            }
            if (!b.LoopEnabled || b.LoopEnd <= b.LoopStart)
            {
                SetStatus("Tick Loop and set a loop (Start smaller than End) first.");
                return null;
            }

            double rate = b.SampleRate;
            long t0 = song.SecondsToTick(b.LoopStart / rate);
            long t1 = song.SecondsToTick(b.LoopEnd / rate);
            if (t1 <= t0) { SetStatus("The loop is shorter than one SQ tick - make it longer."); return null; }

            // the audio does not depend on the loop markers, so every cached render stays valid
            var oldRender = renderCache.ToList();
            var oldReport = reportCache.ToList();

            try { song.SetLoopTicks(t0, t1); }
            catch (Exception ex) { SetStatus("Cannot set the loop: " + ex.Message); return null; }

            // show exactly what is stored (rounded to SQ ticks)
            var secs = song.EventSeconds();
            b.LoopStart = Math.Max(0, (int)Math.Round(secs[song.LoopStartIndex] * rate));
            b.LoopEnd = Math.Min(b.TotalFrames, (int)Math.Round(secs[song.LoopEndIndex] * rate));

            if (song.Index >= 0 && sq != null)
            {
                int idx = song.Index;
                byte[] data = sq.BuildWith(idx, song);
                sq.Parse(data);
                songs.RemoveAll(x => x.Index >= 0);
                songs.InsertRange(0, sq.Songs);
                InvalidateRenders();
                foreach (var kv in oldRender) renderCache[TwinSong(kv.Key)] = kv.Value;
                foreach (var kv in oldReport) reportCache[TwinSong(kv.Key)] = kv.Value;
                song = sq.Songs[Math.Min(idx, sq.Songs.Count - 1)];
            }
            sqModified = true;
            RefreshSongList(song);
            SyncPsf2LoopNums(true);
            UpdateBankInfo();
            if (announce)
                SetStatus($"Loop written into the SQ: {secs[song.LoopStartIndex]:F3}s → {secs[song.LoopEndIndex]:F3}s (ticks {t0} → {t1}). Use 💾 Save SQ... or the Save SQ + HD + BD button.");
            return song;
        }

        /// <summary>One click: loop into the SQ, then HD + BD + SQ written next to each other with the same name.</summary>
        private void SavePsf2LoopAll()
        {
            var song = ApplyPsf2Loop(false);
            if (song == null) return;

            byte[] sqBytes = song.Index >= 0 && sq != null ? sq.Data : SqFile.BuildNew(new List<SqSong> { song }, sq);
            string a = "", z = "";
            var b = psf2Wave.Buffer;
            if (b != null && song.LoopStartIndex >= 0 && song.LoopEndIndex >= 0)
            {
                var secs = song.EventSeconds();
                a = secs[song.LoopStartIndex].ToString("F3"); z = secs[song.LoopEndIndex].ToString("F3");
            }

            if (bank.Loaded)
            {
                using var d = new SaveFileDialog
                {
                    Filter = "HD|*.hd;*.HD",
                    FileName = Path.GetFileName(bank.HdPath),
                    InitialDirectory = Path.GetDirectoryName(bank.HdPath),
                    Title = "Save HD (the BD and the SQ are saved next to it with the same name)",
                    AddExtension = true
                };
                if (d.ShowDialog(this) != DialogResult.OK) return;

                string hd = d.FileName;
                string stem = Path.Combine(Path.GetDirectoryName(hd), Path.GetFileNameWithoutExtension(hd));
                string bdExt = Path.GetExtension(bank.BdPath);
                if (string.IsNullOrEmpty(bdExt)) bdExt = ".bd";
                string bd = stem + bdExt;
                string sqOut = stem + ".sq";
                try
                {
                    BackupOnce(hd); BackupOnce(bd); BackupOnce(sqOut);
                    bank.Save(hd, bd);
                    File.WriteAllBytes(sqOut, sqBytes);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

                if (song.Index >= 0) { sqPath = sqOut; sqLabel = Path.GetFileName(sqOut); sqModified = false; }
                PopulateSamples(); UpdateBankInfo(); UpdateTitle();
                SetStatus($"Saved {Path.GetFileName(hd)} + {Path.GetFileName(bd)} + {Path.GetFileName(sqOut)}" + (a != "" ? $" - loop {a}s → {z}s." : "."));
            }
            else
            {
                using var d = new SaveFileDialog
                {
                    Filter = "SQ|*.sq;*.SQ",
                    FileName = sqPath != null ? Path.GetFileName(sqPath) : SongBaseName(song) + ".sq",
                    AddExtension = true,
                    Title = "No HD/BD loaded - saving the SQ only"
                };
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { BackupOnce(d.FileName); File.WriteAllBytes(d.FileName, sqBytes); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                if (song.Index >= 0) { sqPath = d.FileName; sqLabel = Path.GetFileName(d.FileName); sqModified = false; }
                UpdateBankInfo();
                SetStatus("Saved SQ: " + d.FileName + (a != "" ? $" - loop {a}s → {z}s." : "."));
            }
        }
    }
}
