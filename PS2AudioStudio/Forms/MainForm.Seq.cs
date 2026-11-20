using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PS2AudioStudio.Archives;
using PS2AudioStudio.Audio;

namespace PS2AudioStudio
{
    public partial class MainForm
    {
        // ---------------------------------------------------------------- helpers
        private static bool IsSeqExt(string p)
        {
            string x = Path.GetExtension(p).ToLowerInvariant();
            return x == ".mid" || x == ".midi" || x == ".sq" || x == ".seq";
        }
        private static bool IsSqData(byte[] d)
        {
            return d != null && d.Length > 8 && d[0] == 'I' && d[1] == 'E' && d[2] == 'C' && d[3] == 'S'
                   && d[4] == 's' && d[5] == 'r' && d[6] == 'e' && d[7] == 'V';
        }
        private string PsfSongLabel(SqSong song)
        {
            return $"{song.Name}   [{FormatClock(song.DurationSeconds)} | {song.NoteCount} notes | {song.Ppqn} ppqn{(song.HasLoop ? " | LOOP" : "")}]";
        }

        // ---------------------------------------------------------------- drops: the ACTIVE tab decides
        /// <summary>PSF tab open = into the PSF tab. PSF2 tab open = into the PSF2 tab. Other tabs: .seq -> PSF tab, .mid/.sq -> PSF2 tab.</summary>
        private bool HandleSequenceDrops(List<string> paths)
        {
            if (paths.Count == 0 || !paths.All(IsSeqExt)) return false;
            bool toPsf;
            if (tabs.SelectedTab == tabPsf) toPsf = true;
            else if (tabs.SelectedTab == tabPsf2) toPsf = false;
            else toPsf = paths.All(p => string.Equals(Path.GetExtension(p), ".seq", StringComparison.OrdinalIgnoreCase));
            // a lone .sq with an HD + BD of the same name next to it: OpenPaths loads all three into the PSF2 tab
            if (!toPsf && paths.Count == 1
                && string.Equals(Path.GetExtension(paths[0]), ".sq", StringComparison.OrdinalIgnoreCase)
                && Psf2Set.Find(paths[0]).HasBank) return false;
            OpenSequenceFiles(paths, toPsf);
            return true;
        }

        // ---------------------------------------------------------------- Open MIDI / SQ / SEQ (button, drop, prompt)
        private void OpenSequenceDlg(bool toPsfTab)
        {
            using var d = new OpenFileDialog
            {
                Filter = "MIDI / SQ / SEQ|*.mid;*.midi;*.sq;*.seq|All files|*.*",
                Title = "Open a MIDI, SQ or SEQ file",
                Multiselect = true
            };
            if (d.ShowDialog(this) == DialogResult.OK) OpenSequenceFiles(d.FileNames, toPsfTab);
        }

        /// <summary>MIDI, SQ and SEQ are treated the same in both tabs. The file CONTENT decides the format, not the extension.</summary>
        private void OpenSequenceFiles(IList<string> files, bool toPsfTab)
        {
            var errors = new StringBuilder();
            SqSong last = null;
            int count = 0;
            foreach (string f in files)
            {
                try
                {
                    byte[] data = File.ReadAllBytes(f);
                    if (!toPsfTab && IsSqData(data))
                    {
                        LoadSqFile(f);                       // keeps the SQ itself, so Save SQ works
                        last = sq.Songs[0];
                        count += sq.Songs.Count;
                        continue;
                    }
                    bool mid = data.Length >= 4 && data[0] == 'M' && data[1] == 'T' && data[2] == 'h' && data[3] == 'd';
                    string prefix = mid ? "MIDI: " : IsSqData(data) ? "SQ: " : "SEQ: ";
                    var list = SqSongSeq.LoadAny(data, prefix + Path.GetFileName(f));
                    foreach (var s in list)
                    {
                        s.Index = -1;
                        if (toPsfTab) psfSeqItems.Add(new SongItem { Song = s, Label = PsfSongLabel(s) });
                        else songs.Add(s);
                        last = s;
                        count++;
                    }
                }
                catch (Exception ex) { errors.AppendLine(Path.GetFileName(f) + ": " + ex.Message); }
            }
            if (errors.Length > 0)
                MessageBox.Show(this, errors.ToString(), "Open MIDI / SQ / SEQ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (last == null) return;

            if (toPsfTab)
            {
                TransportStop();
                RebuildPsfSongs();
                for (int i = 0; i < cmbPsfSong.Items.Count; i++)
                    if ((cmbPsfSong.Items[i] as SongItem)?.Song == last) { cmbPsfSong.SelectedIndex = i; break; }
                tabs.SelectedTab = tabPsf;
                lblPsfInfo.Text = (psfPath != null ? Path.GetFileName(psfPath) + " | " : "") + psfRows.Count + " sample(s) | " + cmbPsfSong.Items.Count + " song(s) | preview rate " + psfRate + " Hz";
            }
            else
            {
                RefreshSongList(last);
                tabs.SelectedTab = tabPsf2;
                UpdateBankInfo();
            }
            SetStatus("Loaded " + count + " song(s) into the " + (toPsfTab ? "PSF" : "PSF2") + " tab.");
        }

        /// <summary>"No sequence" prompt, posted to the message queue so a drop finishes first.</summary>
        private void OfferSequenceIfNone(bool psfTab)
        {
            BeginInvoke(new Action(() =>
            {
                bool none = psfTab ? cmbPsfSong.Items.Count == 0 : (bank.Loaded && songs.Count == 0);
                if (!none) return;
                int c = Choose(psfTab ? "No song found in this PSF" : "No sequence with this bank",
                    "No MIDI / SQ / SEQ was found for this " + (psfTab ? "PSF" : "bank") + ".\nPick one now, or drop one onto the window at any time.\nCancel = keep it without a song.",
                    "📂 Open a MIDI / SQ / SEQ file...");
                if (c == 0) OpenSequenceDlg(psfTab);
            }));
        }

        // ---------------------------------------------------------------- PSF tab buttons
        private void InstallPsfSeqButtons(Control bar)
        {
            var bOpenSeq = Ui.Btn("📂 Open MIDI / SQ / SEQ...");
            bOpenSeq.Click += (s, e) => OpenSequenceDlg(true);
            var bRender = Ui.Btn("🔊 Render full song (PSF bank)");
            bRender.Click += (s, e) => PsfRenderFullSong();
            var bSeqSf2 = Ui.Btn("💾 Save SEQ (for this SF2)...");
            bSeqSf2.Click += (s, e) => PsfSaveSeqForSf2();
            bar.Controls.Add(bOpenSeq);
            bar.Controls.Add(bRender);
            bar.Controls.Add(bSeqSf2);
        }

        // ---------------------------------------------------------------- render the selected song through the PSF's own bank
        private CancellationTokenSource psfRenderCts;

        /// <summary>
        /// Like "Play song (through HD/BD)" in the PSF2 tab, for the PSF tab. Works for the PSF's own songs AND for a MIDI / SQ / SEQ
        /// loaded by hand (that one is played through the bank of the PSF's first AKAO song). Shows the waveform player.
        /// </summary>
        private async void PsfRenderFullSong()
        {
            var song = SelectedPsfSong();
            if (song == null) { MessageBox.Show(this, "No song selected. Open a PSF, or load a MIDI / SQ / SEQ first.", "Render full song"); return; }
            SqSong bankSong = psfGm.ContainsKey(song) ? song : psfGm.Keys.FirstOrDefault();
            AkaoSongData d = bankSong != null ? PfsAkaoFor(bankSong) : null;
            if (d == null)
            {
                MessageBox.Show(this, "This PSF has no recognised AKAO song, so there is no instrument bank to render with.\n\n" +
                                      "Open a PSF whose songs are recognised, or use 'Send to PSF2 tab' / the PSF2 tab with an HD + BD.", "Render full song");
                return;
            }
            bool fx = chkPfsEffects != null && chkPfsEffects.Checked;
            string why;
            PsxExport px = BuildPfsDriverExport(bankSong, d, fx, out why);
            if (px == null) { MessageBox.Show(this, "The bank for rendering could not be built:\n" + why, "Render full song", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            SqSong renderSong = psfGm.ContainsKey(song) ? px.Song : song;
            Psf2Bank nb;
            try
            {
                nb = new Psf2Bank();
                nb.LoadFromBytes(px.Hd, px.Bd, "psf.hd", "psf.bd");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Render full song", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

            TransportStop();
            psfRenderCts?.Cancel();
            var cts = new CancellationTokenSource();
            psfRenderCts = cts;
            SetStatus("Rendering the song through the PSF bank...");
            var prog = new Progress<int>(pct => { if (!cts.IsCancellationRequested) SetStatus("Rendering the song... " + pct + "%"); });
            var report = new Psf2RenderReport();
            try
            {
                var token = cts.Token;
                var result = await Task.Run(() => Psf2Renderer.Render(nb, renderSong, prog, token, false, report, fx));
                if (cts.IsCancellationRequested) return;
                ApplySongLoopToBuffer(result, renderSong);
                psfWave.Buffer = result;
                psfWave.MarkFullFit();
                SyncLoopBox(chkLoopPsf, psfWave);
                lblPsfView.Text = "Rendered song: " + song.Name + "  |  " + result.DurationSeconds.ToString("F1") + "s  |  effects " + (fx ? "ON" : "OFF");
                tabs.SelectedTab = tabPsf;
                SetStatus("Rendered " + result.DurationSeconds.ToString("F1") + "s. Playing.");
                PlayWave(psfWave);
            }
            catch (OperationCanceledException) { SetStatus("Render cancelled."); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Render full song", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        // ---------------------------------------------------------------- SEQ for the SF2 + proof that it holds the same MIDI
        private static List<(long, int, int, int, int)> SeqNorm(SqSong s)
        {
            var l = new List<(long, int, int, int, int)>();
            foreach (var e in s.Events)
            {
                int type = (int)e.Type, ch = (int)(e.Channel & 15), a = (int)e.A, b = (int)e.B;
                if (e.Type == SqEventType.NoteOn && b == 0) type = (int)SqEventType.NoteOff;      // note-on vel 0 = note-off
                if (type == (int)SqEventType.NoteOff) b = 0;
                if (e.Type == SqEventType.Tempo) { a = e.Tempo; b = 0; ch = 0; }
                l.Add(((long)e.Tick, type, ch, a, b));
            }
            return l;
        }
        private static string SeqFmt((long, int, int, int, int) t)
        {
            return "tick " + t.Item1 + " " + (SqEventType)t.Item2 + " ch" + (t.Item3 + 1) + " " + t.Item4 + "," + t.Item5;
        }
        /// <summary>"" = identical event lists. Otherwise the events that exist only on one side.</summary>
        private static string SeqDiff(SqSong midi, SqSong seq)
        {
            var ca = new Dictionary<(long, int, int, int, int), int>();
            var cb = new Dictionary<(long, int, int, int, int), int>();
            foreach (var k in SeqNorm(midi)) { ca.TryGetValue(k, out int n); ca[k] = n + 1; }
            foreach (var k in SeqNorm(seq)) { cb.TryGetValue(k, out int n); cb[k] = n + 1; }
            var onlyMidi = new List<string>();
            var onlySeq = new List<string>();
            foreach (var kv in ca) { cb.TryGetValue(kv.Key, out int n); for (int i = 0; i < kv.Value - n; i++) onlyMidi.Add(SeqFmt(kv.Key)); }
            foreach (var kv in cb) { ca.TryGetValue(kv.Key, out int n); for (int i = 0; i < kv.Value - n; i++) onlySeq.Add(SeqFmt(kv.Key)); }
            if (onlyMidi.Count == 0 && onlySeq.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine("Events only in the MIDI: " + onlyMidi.Count);
            foreach (var s in onlyMidi.Take(10)) sb.AppendLine("   " + s);
            sb.AppendLine("Events only in the SEQ: " + onlySeq.Count);
            foreach (var s in onlySeq.Take(10)) sb.AppendLine("   " + s);
            return sb.ToString();
        }

        /// <summary>SEQ made from the SAME song as "Save MIDI (for this SF2)", then read back and compared with that MIDI.</summary>
        private void PsfSaveSeqForSf2()
        {
            var song = SelectedPsfSong();
            if (song == null) { MessageBox.Show(this, "No song in this file.", "Save SEQ"); return; }
            SqSong outSong = song;
            var d = PfsAkaoFor(song);
            if (d != null)
            {
                bool fx = chkPfsEffects != null && chkPfsEffects.Checked;
                string why;
                PsxExport px = BuildPfsDriverExport(song, d, fx, out why);
                if (px == null)
                {
                    MessageBox.Show(this, "The matching export could not be built:\n" + why, "Save SEQ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                outSong = px.Song;
            }
            string stem = psfPath != null ? Path.GetFileNameWithoutExtension(psfPath) : "song";
            using var sd = new SaveFileDialog
            {
                Filter = "Sony PlayStation SEQ (*.seq)|*.seq",
                FileName = SafeFile(stem + "_" + Math.Max(0, cmbPsfSong.SelectedIndex) + "_sf2"),
                AddExtension = true
            };
            if (sd.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                byte[] data = outSong.ToSeq();
                File.WriteAllBytes(sd.FileName, data);
                string check, diff = "";
                try
                {
                    var back = SqSongSeq.LoadAny(data, "check")[0];
                    var midi = SqSong.FromSmf(outSong.ToSmf(), "check");
                    diff = SeqDiff(midi, back);
                    check = diff.Length == 0
                        ? "check OK: the SEQ holds exactly the same events as the MIDI (" + back.NoteCount + " notes)."
                        : "DIFFERENT: the SEQ does not hold the same events as the MIDI.";
                }
                catch (Exception ex) { check = "WARNING: the saved SEQ cannot be read back: " + ex.Message; }
                SetStatus("Saved SEQ: " + sd.FileName + "  -  " + check);
                if (!check.StartsWith("check OK"))
                {
                    try { Clipboard.SetText(check + Environment.NewLine + diff); } catch { }
                    MessageBox.Show(this, check + "\n\n" + diff + "\n(The text is on your clipboard.)", "Save SEQ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save SEQ", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}
