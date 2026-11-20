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

namespace PS2AudioStudio
{
    // PSF tab: bank built from the AKAO data, full-song player, loop boxes, exports, PSF Studio
    public partial class MainForm
    {
        private Psf2Bank pfsBank;                   // bank built from the AKAO data (or the one edited in PSF Studio)
        private PsfPackage pfsBankPkg;              // the package this bank belongs to
        private string pfsBankKey;
        private bool pfsBankEdited;
        private readonly Dictionary<SqSong, AudioBuffer> pfsRenders = new Dictionary<SqSong, AudioBuffer>();
        private readonly Dictionary<SqSong, Psf2RenderReport> pfsReports = new Dictionary<SqSong, Psf2RenderReport>();
        private CancellationTokenSource pfsCts;
        private ProgressBar pbPfsRender;
        private Button bPfsPlay, bPfsCancel;
        private CheckBox chkPfsEffects;
        private int _pfsGen;                        // changes whenever a running render must be thrown away

        private NumericUpDown numPfsLS, numPfsLE;
        private Label lblPfsLoop;
        private bool _pfsBulk;
        private AudioBuffer _pfsBuf;
        private int _pfsLs = -1, _pfsLe = -1, _pfsTot = -1;
        private bool _pfsEn;

        // ================================================================= bars
        private Control MakePfsSongBar()
        {
            var bar = Ui.Bar1(40);
            bPfsPlay = Ui.Btn("▶ Play full song"); bPfsPlay.Click += (s, e) => PlayPfsFull();
            chkPfsEffects = new CheckBox
            {
                Text = "Apply effects from PSF",
                AutoSize = true,
                Checked = true,
                ForeColor = Color.White,
                Margin = new Padding(8, 10, 8, 0)
            };
            chkPfsEffects.CheckedChanged += (s, e) => OnPfsEffectsChanged();
            var wetBox = MakePfsWetBox();
            var bStop = Ui.Btn("⏹ Stop"); bStop.Click += (s, e) => { pfsCts?.Cancel(); TransportStop(); };
            var bShow = Ui.Btn("🎼 Show full song"); bShow.Click += (s, e) => ShowPfsFull();
            var bEd = Ui.Btn("→ Rendered song to Editor"); bEd.Click += (s, e) => PfsRenderToEditor();

            pbPfsRender = new ProgressBar { Width = 120, Height = 18, Margin = new Padding(8, 8, 2, 0), Visible = false };
            bPfsCancel = Ui.Btn("Cancel", 60); bPfsCancel.Visible = false; bPfsCancel.Click += (s, e) => pfsCts?.Cancel();
            bar.Controls.Add(Ui.Lbl("Full song:"));
            bar.Controls.AddRange(new Control[] { bPfsPlay, chkPfsEffects, wetBox, bStop, bShow, bEd, pbPfsRender, bPfsCancel });
            return bar;
        }

        /// <summary>The effects checkbox changed: the song has to be rendered again (press Play full song).</summary>
        private void OnPfsEffectsChanged()
        {
            StopRecordingQuietly();
            _pfsGen++;
            pfsCts?.Cancel();
            TransportStop();

            bool showingRender = psfWave != null && psfWave.Buffer != null &&
                                 pfsRenders.Values.Any(v => ReferenceEquals(v, psfWave.Buffer));
            pfsRenders.Clear();
            pfsReports.Clear();

            if (showingRender)
            {
                psfWave.Buffer = null;
                SyncLoopBox(chkLoopPsf, psfWave);
                SyncPfsLoopNums(true);
                lblPsfView.Text = "Effects setting changed - press Play full song to render again.";
            }
            SetStatus(chkPfsEffects.Checked
                ? "PSF effects ON (reverb the .psf programs). Press Play full song."
                : "PSF effects OFF (dry). Press Play full song.");
        }

        private Control MakePfsBankBar()
        {
            var bar = Ui.Bar1(40);
            var bAll = Ui.Btn("💾 Save BD + HD + SQ..."); bAll.Click += (s, e) => SavePfsAll();
            var bSf2 = Ui.Btn("💾 Save SF2..."); bSf2.Click += (s, e) => PfsExportSf2();
            var bMidi = Ui.Btn("🎼 Save MIDI (for this SF2)..."); bMidi.Click += (s, e) => PfsSaveMidi();
            var bPsf2 = Ui.Btn("📥 Save as .psf2..."); bPsf2.Click += (s, e) => PfsSavePsf2();
            var bStudio = Ui.Btn("🎛 PSF Studio..."); bStudio.Click += (s, e) => OpenPfsStudio();
            bar.Controls.Add(Ui.Lbl("Bank:"));
            bar.Controls.AddRange(new Control[] { bAll, bSf2, bMidi, bPsf2, bStudio });
            return bar;
        }

        private Control BuildPfsLoopBar()
        {
            var box = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = Ui.Panel };
            var row1 = Ui.Bar1(40);
            var row2 = Ui.Bar1(40);

            numPfsLS = NewP2Num(); numPfsLE = NewP2Num();
            numPfsLS.ValueChanged += (s, e) => OnPfsLoopNum();
            numPfsLE.ValueChanged += (s, e) => OnPfsLoopNum();
            lblPfsLoop = new Label { AutoSize = true, ForeColor = Color.LightSkyBlue, Margin = new Padding(6, 10, 0, 0), Text = "" };

            row1.Controls.Add(Ui.Lbl("Loop start (frames):"));
            row1.Controls.Add(numPfsLS);
            row1.Controls.Add(Ui.Lbl("end:"));
            row1.Controls.Add(numPfsLE);
            row1.Controls.Add(lblPfsLoop);

            var bApply = Ui.Btn("📌 Apply loop to the song"); bApply.Click += (s, e) => ApplyPfsLoop(true);
            var bSaveAll = Ui.Btn("💾 Save BD + HD + SQ with this loop..."); bSaveAll.Click += (s, e) => SavePfsAll();
            row2.Controls.Add(bApply);
            row2.Controls.Add(bSaveAll);

            box.Controls.Add(row2);
            box.Controls.Add(row1);

            var tm = new System.Windows.Forms.Timer { Interval = 200 };
            tm.Tick += (s, e) => SyncPfsLoopNums(false);
            tm.Start();
            box.Disposed += (s, e) => tm.Dispose();
            return box;
        }

        // ================================================================= loop boxes
        private void SyncPfsLoopNums(bool force)
        {
            if (numPfsLS == null || psfWave == null) return;
            var b = psfWave.Buffer;
            int ls = b == null ? 0 : b.LoopStart, le = b == null ? 0 : b.LoopEnd;
            int tot = b == null ? 0 : b.TotalFrames;
            bool en = b != null && b.LoopEnabled;
            if (!force && ReferenceEquals(b, _pfsBuf) && ls == _pfsLs && le == _pfsLe && tot == _pfsTot && en == _pfsEn) return;
            _pfsBuf = b; _pfsLs = ls; _pfsLe = le; _pfsTot = tot; _pfsEn = en;

            _pfsBulk = true;
            try
            {
                bool has = b != null && tot > 0;
                numPfsLS.Enabled = has; numPfsLE.Enabled = has;
                numPfsLS.Maximum = Math.Max(0, tot); numPfsLE.Maximum = Math.Max(0, tot);
                numPfsLS.Value = Math.Max(0, Math.Min(tot, ls));
                numPfsLE.Value = Math.Max(0, Math.Min(tot, le));
            }
            finally { _pfsBulk = false; }
            UpdatePfsLoopLabel();
        }

        private void UpdatePfsLoopLabel()
        {
            var b = psfWave.Buffer;
            if (b == null || b.TotalFrames == 0) { lblPfsLoop.Text = ""; return; }
            double r = b.SampleRate;
            lblPfsLoop.Text = $"{b.LoopStart / r:F3}s → {b.LoopEnd / r:F3}s  (length {(b.LoopEnd - b.LoopStart) / r:F3}s)" + (b.LoopEnabled ? "" : "   [Loop is OFF]");
        }

        private void OnPfsLoopNum()
        {
            if (_pfsBulk) return;
            var b = psfWave.Buffer;
            if (b == null) return;
            int ls = (int)numPfsLS.Value, le = (int)numPfsLE.Value;
            if (le <= ls) { lblPfsLoop.Text = "The loop end must be bigger than the start."; return; }
            b.LoopStart = ls; b.LoopEnd = le; b.LoopEnabled = true;
            SyncLoopBox(chkLoopPsf, psfWave);
            psfWave.Invalidate();
            _pfsBuf = b; _pfsLs = ls; _pfsLe = le; _pfsTot = b.TotalFrames; _pfsEn = true;
            UpdatePfsLoopLabel();
        }

        /// <summary>Writes the loop of the full-song view into the song's loop markers (raw song and its General MIDI copy).</summary>
        private SqSong ApplyPfsLoop(bool announce)
        {
            var song = SelectedPsfSong();
            if (song == null) { SetStatus("No song selected."); return null; }
            var b = psfWave.Buffer;
            AudioBuffer rb;
            if (b == null || !pfsRenders.TryGetValue(song, out rb) || !ReferenceEquals(rb, b))
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
            if (t1 <= t0) { SetStatus("The loop is shorter than one tick - make it longer."); return null; }

            try
            {
                song.SetLoopTicks(t0, t1);
                SqSong g;
                if (psfGm.TryGetValue(song, out g)) g.SetLoopTicks(t0, t1);
            }
            catch (Exception ex) { SetStatus("Cannot set the loop: " + ex.Message); return null; }

            var secs = song.EventSeconds();
            b.LoopStart = Math.Max(0, (int)Math.Round(secs[song.LoopStartIndex] * rate));
            b.LoopEnd = Math.Min(b.TotalFrames, (int)Math.Round(secs[song.LoopEndIndex] * rate));
            SyncPfsLoopNums(true);
            if (announce)
                SetStatus($"Loop written into the song: {secs[song.LoopStartIndex]:F3}s → {secs[song.LoopEndIndex]:F3}s (ticks {t0} → {t1}). Use 💾 Save BD + HD + SQ.");
            return song;
        }

        // ================================================================= state / bank
        private void ClearPfsState()
        {
            _pfsGen++;
            pfsCts?.Cancel();
            pfsBank = null; pfsBankPkg = null; pfsBankKey = null; pfsBankEdited = false;
            pfsRenders.Clear(); pfsReports.Clear();
            SyncPfsLoopNums(true);
        }

        private string PfsStem()
        {
            return psfPath != null ? SafeFile(Path.GetFileNameWithoutExtension(psfPath)) : "psf_bank";
        }

        private string PfsPath(string ext)
        {
            string dir = psfPath != null ? Path.GetDirectoryName(Path.GetFullPath(psfPath)) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(dir, PfsStem() + ext);
        }

        /// <summary>The AKAO data of the selected song (null for a Sony SEQ song).</summary>
        private AkaoSongData PfsAkaoFor(SqSong song)
        {
            if (song != null && !psfGm.ContainsKey(song)) return null;
            if (song != null)
            {
                for (int i = 0; i < cmbPsfSong.Items.Count; i++)
                {
                    var it = cmbPsfSong.Items[i] as SongItem;
                    if (it != null && ReferenceEquals(it.Song, song))
                    {
                        int k = i - psfSeqItems.Count;
                        if (k >= 0 && k < psfAkao.Count) return psfAkao[k];
                        break;
                    }
                }
            }
            return psfAkao.Count > 0 ? psfAkao[0] : null;
        }

        private AkaoBank FindPfsAkaoBank()
        {
            if (psfPackage == null) return null;
            foreach (var b in psfPackage.Buffers)
            {
                var banks = AkaoBankReader.Find(b.Data);
                if (banks.Count > 0) return banks[0];
            }
            return null;
        }

        /// <summary>The bank for the selected AKAO song. Built once and kept; rebuilt when the sample rate or the song changes (never after PSF Studio edits).</summary>
        private Psf2Bank EnsurePfsBank(AkaoSongData d, out string error)
        {
            error = null;
            if (!ReferenceEquals(pfsBankPkg, psfPackage))
            {
                pfsBank = null; pfsBankKey = null; pfsBankEdited = false;
                pfsBankPkg = psfPackage;
                pfsRenders.Clear(); pfsReports.Clear();
            }
            if (pfsBankEdited && pfsBank != null) return pfsBank;
            if (psfPackage == null) { error = "Open a .psf first."; return null; }
            if (d == null) { error = "The bank can only be built from an AKAO song (Square games). Pick an AKAO song in the Song box."; return null; }

            string key = d.Offset + "|" + psfRate;
            if (pfsBank != null && key == pfsBankKey) return pfsBank;
            try
            {
                AkaoBank ab = FindPfsAkaoBank();
                if (ab == null) { error = "No AKAO sample bank was found in this file."; return null; }
                byte[] hd, bd;
                string rep;
                AkaoToPsf2.Build(ab, d, psfRate, out hd, out bd, out rep);
                var nb = new Psf2Bank();
                nb.LoadFromBytes(hd, bd, PfsPath(".hd"), PfsPath(".bd"));
                pfsBank = nb;
                pfsBankKey = key;
                pfsRenders.Clear(); pfsReports.Clear();
                tbPsfReport.AppendText(Environment.NewLine + "--- Bank built from the AKAO data (HD + BD) ---" + Environment.NewLine + rep);
                return nb;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        private void PrunePfsCaches()
        {
            var live = new HashSet<SqSong>();
            foreach (var o in cmbPsfSong.Items)
            {
                var it = o as SongItem;
                if (it != null) live.Add(it.Song);
            }
            foreach (var k in pfsRenders.Keys.Where(x => !live.Contains(x)).ToList()) pfsRenders.Remove(k);
            foreach (var k in pfsReports.Keys.Where(x => !live.Contains(x)).ToList()) pfsReports.Remove(k);
        }

        // ================================================================= full song
        private async Task<AudioBuffer> GetPfsRenderAsync(SqSong song, AkaoSongData d)
        {
            if (song == null) return null;
            string err;
            var b = EnsurePfsBank(d, out err);          // also resets the caches when another .psf was opened
            AudioBuffer cached;
            if (pfsRenders.TryGetValue(song, out cached)) return cached;

            // PSF Studio edits only exist in the bank, so an edited bank is rendered through the bank.
            // An unedited song is played by the game's own sound driver.
            bool useDriver = !pfsBankEdited;
            bool effects = chkPfsEffects.Checked;
            int gen = _pfsGen;
            if (!useDriver && b == null) { MessageBox.Show(this, err ?? "No bank.", "Play full song"); return null; }

            TransportStop();
            pfsCts?.Cancel();
            var cts = new CancellationTokenSource();
            pfsCts = cts;
            pbPfsRender.Value = 0; pbPfsRender.Visible = true; bPfsCancel.Visible = true; bPfsPlay.Enabled = false;
            var prog = new Progress<int>(pct => { if (!cts.IsCancellationRequested) pbPfsRender.Value = Math.Max(0, Math.Min(100, pct)); });
            try
            {
                var token = cts.Token;
                AudioBuffer result = null;

                if (useDriver)
                {
                    SetStatus("Running the game's own sound driver (emulated PS1)" + (effects ? " with the PSF effects..." : ", dry (no effects)..."));
                    try
                    {
                        result = await RenderPfsDriverAsync(d, effects, prog, token);
                        pfsReports.Remove(song);
                    }
                    catch (PsxUnsupportedException ux)
                    {
                        var ask = MessageBox.Show(this,
                            "The game's sound driver cannot be run for this file:\n\n" + ux.Message +
                            "\n\nNothing was played.\n\nDo you want an APPROXIMATE render through the bank built from the AKAO samples instead? " +
                            "It will NOT sound exactly like the game.",
                            "Play full song", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                        if (ask != DialogResult.Yes) { SetStatus("Not played: " + ux.Message); return null; }
                        if (b == null) { MessageBox.Show(this, err ?? "No bank.", "Play full song"); return null; }
                    }
                }

                if (result == null)
                {
                    SetStatus("Rendering the song through the bank built from the AKAO samples...");
                    var report = new Psf2RenderReport();
                    result = await Task.Run(() => Psf2Renderer.Render(b, song, prog, token, false, report));
                    ApplySongLoopToBuffer(result, song);
                    pfsReports[song] = report;
                }

                if (gen != _pfsGen || token.IsCancellationRequested)
                {
                    SetStatus("Render discarded: the file or the effects setting changed while it was rendering. Press Play full song again.");
                    return null;
                }

                pfsRenders[song] = result;
                return result;
            }
            catch (OperationCanceledException) { SetStatus("Render cancelled."); return null; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Render error", MessageBoxButtons.OK, MessageBoxIcon.Error); return null; }
            finally
            {
                if (pfsCts == cts)
                {
                    pbPfsRender.Visible = false; bPfsCancel.Visible = false; bPfsPlay.Enabled = true;
                }
            }
        }

        /// <summary>Runs the .psf's own sound driver for exactly the length of the song itself (one pass, from the AKAO data).
        /// effects = true: the reverb the .psf programs into the sound chip is applied; false: dry.
        /// The loop is put on the buffer from the AKAO loop marker. The tag length / fade is never used.</summary>
        private async Task<AudioBuffer> RenderPfsDriverAsync(AkaoSongData d, bool effects, IProgress<int> prog, CancellationToken token)
        {
            if (psfPath == null || !File.Exists(psfPath))
                throw new InvalidOperationException("The .psf file is not available: " + psfPath);
            byte[] psf = File.ReadAllBytes(psfPath);

            // the driver ignores the Tempo button, so the song is measured at its own tempo
            var refSong = SqSong.FromSmf(AkaoMidi.Build(d, false, d.AutoBpm), "tempo reference");
            double a = 0, e = 0;
            bool loop = refSong.HasLoop && refSong.Events.Count > 0;
            if (loop)
            {
                double[] t = refSong.EventSeconds();
                a = refSong.LoopStartIndex < 0 ? 0 : t[refSong.LoopStartIndex];
                e = refSong.LoopEndIndex < 0 ? t[t.Length - 1] : t[refSong.LoopEndIndex];
                loop = e - a > 1;
            }
            double seconds = Math.Max(refSong.DurationSeconds, loop ? e : 0);
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
                throw new InvalidDataException("The length of the song could not be determined.");

            var res = await Task.Run(() =>
            {
                PsxResult r = PsxDriver.Render(psf, seconds, prog, token, effects);
                int n = Math.Min(r.Pcm.Length, r.Frames * 2);
                var f = new float[n];
                for (int i = 0; i < n; i++) f[i] = r.Pcm[i] / 32768f;
                return (r, new AudioBuffer(f, r.Rate, 2));
            });

            PsxResult pr = res.Item1;
            AudioBuffer buf = res.Item2;
            if (loop) ApplySongLoopToBuffer(buf, refSong);

            tbPsfReport.AppendText(Environment.NewLine + "--- Game driver (emulated PS1 sound program) ---" + Environment.NewLine +
                                   pr.Report + Environment.NewLine +
                                   "PSF effects: " + (effects ? "ON (reverb as programmed by the .psf)" : "OFF (dry)") + Environment.NewLine +
                                   $"Length = the song's own length, one pass at its original tempo: {seconds:F3}s." +
                                   (loop ? $" Loop from the AKAO loop marker: {a:F3}s -> {e:F3}s." : " No loop marker in this song.") + Environment.NewLine);
            return buf;
        }

        private string PfsRenderSummary(AudioBuffer b, SqSong song)
        {
            string s = $"Rendered {b.DurationSeconds:F1}s." + (b.LoopEnabled ? $" Loop {b.LoopStart:N0} → {b.LoopEnd:N0}." : "");
            Psf2RenderReport rep;
            if (pfsReports.TryGetValue(song, out rep) && rep.NotesSilent > 0)
                s += $"  {rep.NotesSilent:N0} of {rep.NotesTotal:N0} notes made no sound (see the report below the list).";
            return s;
        }

        private async void PlayPfsFull()
        {
            var song = SelectedPsfSong();
            if (song == null) { PlayWholePsf(); return; }
            var d = PfsAkaoFor(song);
            if (d == null)
            {
                PlayMidiSong(PsfListenSong(song), chkLoopPsf.Checked);
                SetStatus("Full-song rendering through a bank works for AKAO songs only. Playing with the Windows MIDI synth instead.");
                return;
            }
            PrunePfsCaches();
            bool was = pfsRenders.ContainsKey(song);
            var b = await GetPfsRenderAsync(song, d);
            if (b == null) return;
            ShowPfsRendered(b, song);
            if (!was)
            {
                SetStatus(PfsRenderSummary(b, song));
                Psf2RenderReport rep;
                if (pfsReports.TryGetValue(song, out rep))
                    tbPsfReport.AppendText(Environment.NewLine + "--- Render report: " + song.Name + " ---" + Environment.NewLine + rep.ToText());
            }
            PlayWave(psfWave);
        }

        private void ShowPfsRendered(AudioBuffer b, SqSong song)
        {
            psfWave.Buffer = b;
            psfWave.MarkFullFit();
            SyncLoopBox(chkLoopPsf, psfWave);
            SyncPfsLoopNums(true);
            lblPsfView.Text = $"Rendered song: {song.Name}  |  {b.DurationSeconds:F1}s  |  {b.SampleRate} Hz {(b.Channels == 1 ? "mono" : "stereo")}  |  " +
                              (song.HasLoop ? "loop: " + song.LoopDescription : "no loop markers");
        }

        private void ShowPfsFull()
        {
            var song = SelectedPsfSong();
            AudioBuffer b;
            if (song == null || !pfsRenders.TryGetValue(song, out b))
            {
                SetStatus("Nothing rendered for this song yet - press Play full song first.");
                return;
            }
            ShowPfsRendered(b, song);
            SetStatus("Showing the full rendered song again (kept in memory, no re-render).");
        }

        private void PfsRenderToEditor()
        {
            var song = SelectedPsfSong();
            AudioBuffer b;
            if (song != null && pfsRenders.TryGetValue(song, out b))
                SetEditorBuffer(b.Clone(), SafeFile(song.Name ?? "song") + ".wav");
            else MessageBox.Show(this, "Press Play full song first - then the rendered audio can be sent to the editor.", "Render");
        }

        private void PfsTiming(AkaoSongData d, out double seconds, out bool loop, out double a, out double e)
        {
            var refSong = SqSong.FromSmf(AkaoMidi.Build(d, false, d.AutoBpm), "tempo reference");
            a = 0; e = 0;
            loop = refSong.HasLoop && refSong.Events.Count > 0;
            if (loop)
            {
                double[] t = refSong.EventSeconds();
                a = refSong.LoopStartIndex < 0 ? 0 : t[refSong.LoopStartIndex];
                e = refSong.LoopEndIndex < 0 ? t[t.Length - 1] : t[refSong.LoopEndIndex];
                loop = e - a > 1;
            }
            seconds = Math.Max(refSong.DurationSeconds, loop ? e : 0);
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
                throw new InvalidDataException("The length of the song could not be determined.");
        }

        /// <summary>Runs the game's driver once more with logging and builds HD + BD + SQ from what it did.
        /// Returns null (and the reason) when that is not possible. A loop set in the full-song view is used.</summary>
        private PsxExport BuildPfsDriverExport(SqSong song, AkaoSongData d, bool effects, out string why)
        {
            why = null;
            if (pfsBankEdited) { why = "The bank was edited in PSF Studio."; return null; }
            if (d == null) { why = "This is not an AKAO song (its length and loop come from the AKAO data)."; return null; }
            if (psfPath == null || !File.Exists(psfPath)) { why = "The .psf file is not available."; return null; }
            Cursor = Cursors.WaitCursor;
            try
            {
                double seconds, a, e; bool loop;
                PfsTiming(d, out seconds, out loop, out a, out e);

                AudioBuffer rb;
                var wb = psfWave.Buffer;
                if (wb != null && pfsRenders.TryGetValue(song, out rb) && ReferenceEquals(rb, wb))
                {
                    if (wb.LoopEnabled && wb.LoopEnd > wb.LoopStart)
                    {
                        loop = true;
                        a = wb.LoopStart / (double)wb.SampleRate;
                        e = wb.LoopEnd / (double)wb.SampleRate;
                    }
                    else loop = false;
                }

                PsxResult r = PsxDriver.Render(File.ReadAllBytes(psfPath), seconds, null, CancellationToken.None, false, true);
                return PsxToPsf2.Build(r.Capture, seconds, loop ? a : -1, loop ? e : -1, effects, SafeFile(song.Name ?? "song"));
            }
            catch (PsxUnsupportedException ux) { why = ux.Message; return null; }
            catch (Exception ex) { why = ex.Message; return null; }
            finally { Cursor = Cursors.Default; }
        }

        private bool AskPfsApproxExport(string why)
        {
            var ask = MessageBox.Show(this,
                "The export from the game's driver is not possible:\n\n" + why +
                "\n\nDo you want the APPROXIMATE export built from the AKAO conversion instead? It will NOT sound exactly like the game, and it carries no effects.",
                "Save", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            return ask == DialogResult.Yes;
        }

        // ================================================================= export
        private void SavePfsAll()
        {
            var song = SelectedPsfSong();
            if (song == null) { WholePsfBdHdSq(); return; }
            var ad = PfsAkaoFor(song);
            bool effects = chkPfsEffects.Checked;

            string why;
            var exp = BuildPfsDriverExport(song, ad, effects, out why);
            byte[] hd, bd, sqBytes;
            string how, fx = "";
            if (exp != null)
            {
                try { sqBytes = SqFile.BuildNew(new List<SqSong> { exp.Song }, null); }
                catch (Exception exc) { MessageBox.Show(this, exc.Message, "Save BD + HD + SQ", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                hd = exp.Hd; bd = exp.Bd;
                how = "from the game's driver";
                fx = " " + exp.Effects;
                tbPsfReport.AppendText(Environment.NewLine + "--- Export from the game's driver ---" + Environment.NewLine + exp.Report);
            }
            else
            {
                if (!pfsBankEdited && !AskPfsApproxExport(why)) return;
                string err;
                var bk = EnsurePfsBank(ad, out err);
                if (bk == null) { MessageBox.Show(this, err ?? "No bank.", "Save BD + HD + SQ"); return; }

                AudioBuffer rb;
                if (psfWave.Buffer != null && pfsRenders.TryGetValue(song, out rb) && ReferenceEquals(rb, psfWave.Buffer) && psfWave.Buffer.LoopEnabled)
                    ApplyPfsLoop(false);
                try
                {
                    bk.BuildFiles(out hd, out bd);
                    sqBytes = SqFile.BuildNew(new List<SqSong> { song }, null);
                }
                catch (Exception exc) { MessageBox.Show(this, exc.Message, "Save BD + HD + SQ", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                how = pfsBankEdited ? "from the bank edited in PSF Studio, no effects" : "APPROXIMATE, from the AKAO conversion, no effects";
            }

            using var d = new SaveFileDialog
            {
                Filter = "HD|*.hd;*.HD",
                FileName = PfsStem() + ".hd",
                InitialDirectory = psfPath != null ? Path.GetDirectoryName(Path.GetFullPath(psfPath)) : null,
                Title = "Save HD (the BD and the SQ are saved next to it with the same name)",
                AddExtension = true
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string hdOut = d.FileName;
            string stem = Path.Combine(Path.GetDirectoryName(hdOut), Path.GetFileNameWithoutExtension(hdOut));
            string bdOut = stem + ".bd", sqOut = stem + ".sq";
            try
            {
                BackupOnce(hdOut); BackupOnce(bdOut); BackupOnce(sqOut);
                File.WriteAllBytes(hdOut, hd);
                File.WriteAllBytes(bdOut, bd);
                File.WriteAllBytes(sqOut, sqBytes);
            }
            catch (Exception exc) { MessageBox.Show(this, exc.Message, "Save error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            SetStatus("Saved " + Path.GetFileName(hdOut) + " + " + Path.GetFileName(bdOut) + " + " + Path.GetFileName(sqOut) + " (" + how + ")." + fx);
        }

        // ===== Save SF2 and Save MIDI use the SAME export, so they always match each other =====
        private PsxExport _pfsPairExp;
        private SqSong _pfsPairSong;
        private string _pfsPairKey;

        /// <summary>HD + BD + song from the game's driver (the same route as "send to PSF2 tab"). Kept in memory so the second button is instant.</summary>
        private PsxExport PfsMatchingExport(SqSong song, AkaoSongData d, bool effects, out string why)
        {
            string loopKey = "";
            AudioBuffer rb;
            var wb = psfWave.Buffer;
            if (wb != null && pfsRenders.TryGetValue(song, out rb) && ReferenceEquals(rb, wb))
                loopKey = wb.LoopEnabled + ":" + wb.LoopStart + ":" + wb.LoopEnd;
            string key = (psfPath ?? "") + "|" + effects + "|" + _pfsGen + "|" + pfsBankEdited + "|" + loopKey;
            if (_pfsPairExp != null && ReferenceEquals(_pfsPairSong, song) && _pfsPairKey == key)
            {
                why = null;
                return _pfsPairExp;
            }
            var exp = BuildPfsDriverExport(song, d, effects, out why);
            _pfsPairExp = exp;
            _pfsPairSong = song;
            _pfsPairKey = key;
            return exp;
        }
        /// <summary>Save SF2: the bank of the game's driver export (like the PSF2 tab after "Send to PSF2 tab").</summary>
        private void PfsExportSf2()
        {
            var song = SelectedPsfSong();
            if (song == null) { WholePsfSf2(); return; }
            var ad = PfsAkaoFor(song);
            bool fx = chkPfsEffects.Checked;
            string why;
            var exp = PfsMatchingExport(song, ad, fx, out why);
            Psf2Bank bk;
            if (exp != null)
            {
                try
                {
                    bk = new Psf2Bank();
                    bk.LoadFromBytes(exp.Hd, exp.Bd, PfsPath(".hd"), PfsPath(".bd"));
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save SF2", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            }
            else
            {
                if (ad != null && !pfsBankEdited && !AskPfsApproxExport(why)) return;
                string err;
                bk = EnsurePfsBank(ad, out err);
                if (bk == null) { MessageBox.Show(this, err ?? "No bank.", "Save SF2"); return; }
            }
            var keep = bank;
            bank = bk;
            try { ExportSf2(fx); }
            finally { bank = keep; }
        }
        /// <summary>Save MIDI: the song that belongs to the SF2 above (same export). Not the General MIDI preview copy.</summary>
        private void PfsSaveMidi()
        {
            var song = SelectedPsfSong();
            if (song == null) { WholePsfMidi(); return; }
            var ad = PfsAkaoFor(song);
            byte[] smf;
            string how;
            try
            {
                if (ad == null)
                {
                    smf = song.ToSmf();
                    how = "the song as stored in the file";
                }
                else
                {
                    string why;
                    var exp = PfsMatchingExport(song, ad, chkPfsEffects.Checked, out why);
                    if (exp != null)
                    {
                        smf = exp.Song.ToSmf();
                        how = "from the game's driver, same as Save SF2";
                    }
                    else
                    {
                        if (!pfsBankEdited && !AskPfsApproxExport(why)) return;
                        smf = song.ToSmf();
                        how = "APPROXIMATE (AKAO conversion)";
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save MIDI", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            using var d = new SaveFileDialog
            {
                Filter = "MIDI|*.mid",
                FileName = PfsStem() + ".mid",
                InitialDirectory = psfPath != null ? Path.GetDirectoryName(Path.GetFullPath(psfPath)) : null,
                Title = "Save MIDI",
                AddExtension = true
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                BackupOnce(d.FileName);
                File.WriteAllBytes(d.FileName, smf);
                SetStatus("Saved " + Path.GetFileName(d.FileName) + " (" + how + "). In Awave: load the SF2 first, then this MIDI.");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save MIDI", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void PfsSavePsf2()
        {
            var song = SelectedPsfSong();
            if (song == null) { MessageBox.Show(this, "No song in this file.", "Save as .psf2"); return; }
            var ad = PfsAkaoFor(song);
            bool effects = chkPfsEffects.Checked;

            string why;
            var exp = BuildPfsDriverExport(song, ad, effects, out why);
            Psf2Bank bk = null;
            if (exp == null)
            {
                if (!pfsBankEdited && !AskPfsApproxExport(why)) return;
                string err;
                bk = EnsurePfsBank(ad, out err);
                if (bk == null) { MessageBox.Show(this, err ?? "No bank.", "Save as .psf2"); return; }
            }

            Psf2Entry root;
            try
            {
                if (psf2Template != null) root = Psf2Container.Read(Psf2Container.Write(psf2Template));   // independent copy
                else
                {
                    MessageBox.Show(this, "A .psf2 also needs the IOP modules (.irx files) which a PS1 file does not have.\n\nChoose any .psf2 to borrow them from. Its bank and song are replaced by this one; the .irx files stay.", "Save as .psf2", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    using var od = new OpenFileDialog { Filter = "PSF2|*.psf2|All|*.*", Title = "Choose a .psf2 to borrow the .irx files from" };
                    if (od.ShowDialog(this) != DialogResult.OK) return;
                    byte[] data = File.ReadAllBytes(od.FileName);
                    if (!Psf2Container.IsPsf2(data)) { MessageBox.Show(this, "That is not a PSF2 package.", "Save as .psf2"); return; }
                    root = Psf2Container.Read(data);
                }

                using var d = new SaveFileDialog { Filter = "PSF2|*.psf2", FileName = PfsStem() + ".psf2", AddExtension = true };
                if (d.ShowDialog(this) != DialogResult.OK) return;

                byte[] h, b, sqBytes;
                if (exp != null)
                {
                    h = exp.Hd; b = exp.Bd;
                    sqBytes = SqFile.BuildNew(new List<SqSong> { exp.Song }, null);
                    tbPsfReport.AppendText(Environment.NewLine + "--- Export from the game's driver ---" + Environment.NewLine + exp.Report);
                }
                else
                {
                    AudioBuffer rb;
                    if (psfWave.Buffer != null && pfsRenders.TryGetValue(song, out rb) && ReferenceEquals(rb, psfWave.Buffer) && psfWave.Buffer.LoopEnabled)
                        ApplyPfsLoop(false);
                    bk.BuildFiles(out h, out b);
                    sqBytes = SqFile.BuildNew(new List<SqSong> { song }, null);
                }

                bool doneHd = false, doneBd = false, doneSq = false, doneIni = false;
                string iniNote = "";
                foreach (var kv in Psf2Container.Files(root))
                {
                    string k = kv.Key.ToLowerInvariant();
                    if (k.EndsWith(".hd") && !doneHd) { kv.Value.Data = h; doneHd = true; }
                    else if (k.EndsWith(".bd") && !doneBd) { kv.Value.Data = b; doneBd = true; }
                    else if (k.EndsWith(".sq") && !doneSq) { kv.Value.Data = sqBytes; doneSq = true; }
                    else if (k.EndsWith("psf2.ini") && !doneIni)
                    {
                        doneIni = true;
                        string txt = System.Text.Encoding.Latin1.GetString(kv.Value.Data);
                        if (!System.Text.RegularExpressions.Regex.IsMatch(txt, @"-r=\d+"))
                            iniNote = " psf2.ini has no -r= setting: reverb mode not touched.";
                        else if (!effects)
                        {
                            txt = System.Text.RegularExpressions.Regex.Replace(txt, @"-r=\d+", "-r=0");
                            iniNote = " Effects off: psf2.ini reverb mode set to -r=0.";
                        }
                        else if (exp != null && exp.ReverbMode >= 0)
                        {
                            txt = System.Text.RegularExpressions.Regex.Replace(txt, @"-r=\d+", "-r=" + exp.ReverbMode);
                            txt = System.Text.RegularExpressions.Regex.Replace(txt, @"-d=\d+", "-d=" + exp.ReverbDepth);
                            iniNote = " psf2.ini reverb: -r=" + exp.ReverbMode + " -d=" + exp.ReverbDepth + " (unverified on a PS2 player).";
                        }
                        else iniNote = " psf2.ini reverb mode left as the borrowed file has it (no known preset matched, or no bank-driver export).";
                        kv.Value.Data = System.Text.Encoding.Latin1.GetBytes(txt);
                    }
                }
                byte[] outb = Psf2Container.Write(root);
                BackupOnce(d.FileName);
                File.WriteAllBytes(d.FileName, outb);
                SetStatus("Saved " + d.FileName + " (bank" + (doneSq ? " + SQ" : "") + " replaced, .irx files kept; " + (exp != null ? "from the game's driver" : "approximate, AKAO conversion") + ")." +
                          (doneSq ? "" : " The .psf2 you borrowed from had no .sq - save the SQ separately.") + iniNote +
                          (exp != null ? " " + exp.Effects : ""));
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save as .psf2", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        // ================================================================= whole PSF (game driver, no song search)
        private AudioBuffer _wholeBuf;
        private PsxExport _wholeExp;
        private string _wholeExpKey;

        private void WholePsfMenu(Button anchor)
        {
            var m = new ContextMenuStrip();
            m.Items.Add("▶ Play whole PSF (runs the game's own driver)", null, (s, e) => PlayWholePsf());
            m.Items.Add("💾 Save SF2...", null, (s, e) => WholePsfSf2());
            m.Items.Add("🎼 Save MIDI (for that SF2)...", null, (s, e) => WholePsfMidi());
            m.Items.Add("💾 Save BD + HD + SQ...", null, (s, e) => WholePsfBdHdSq());
            m.Items.Add("→ Rendered audio to Editor", null, (s, e) =>
            {
                if (_wholeBuf == null) MessageBox.Show(this, "Play the whole PSF first.", "Whole PSF");
                else SetEditorBuffer(_wholeBuf.Clone(), PfsStem() + ".wav");
            });
            m.Show(anchor, new Point(0, anchor.Height));
        }

        private static double ParsePsfTime(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim().Replace(',', '.');
            double total = 0;
            foreach (var part in s.Split(':'))
            {
                double v;
                if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return 0;
                total = total * 60 + v;
            }
            return total;
        }

        /// <summary>Length from the PSF's own [TAG] (length + fade). No tag: 3:00.</summary>
        private double WholePsfSeconds(out string how)
        {
            double len = 0, fade = 0;
            try
            {
                Dictionary<string, string> tags; int ver;
                PsfFile.ReadProgram(File.ReadAllBytes(psfPath), out tags, out ver);
                foreach (var kv in tags)
                {
                    if (kv.Key.Equals("length", StringComparison.OrdinalIgnoreCase)) len = ParsePsfTime(kv.Value);
                    else if (kv.Key.Equals("fade", StringComparison.OrdinalIgnoreCase)) fade = ParsePsfTime(kv.Value);
                }
            }
            catch { }
            if (len <= 0) { how = "no length tag, 3:00 used"; return 180; }
            double total = Math.Min(len + fade, 900);
            how = "length tag " + len.ToString("F1") + " s + fade " + fade.ToString("F1") + " s";
            return total;
        }

        private async Task<PsxResult> WholePsfRunAsync(double seconds, bool capture, bool effects, IProgress<int> prog, CancellationToken token)
        {
            PsxDriver.LastPsfPath = psfPath;                 // where the .psflib files are looked for
            byte[] psf = File.ReadAllBytes(psfPath);
            return await Task.Run(() => capture
                ? PsxDriver.Render(psf, seconds, prog, token, false, true)
                : PsxDriver.Render(psf, seconds, prog, token, effects));
        }

        private async void PlayWholePsf()
        {
            if (psfPath == null || !File.Exists(psfPath)) { MessageBox.Show(this, "Open a .psf / .minipsf first.", "Whole PSF"); return; }
            bool effects = chkPfsEffects.Checked;
            string how;
            double secs = WholePsfSeconds(out how);
            TransportStop();
            pfsCts?.Cancel();
            var cts = new CancellationTokenSource();
            pfsCts = cts;
            int gen = _pfsGen;
            pbPfsRender.Value = 0; pbPfsRender.Visible = true; bPfsCancel.Visible = true; bPfsPlay.Enabled = false;
            var prog = new Progress<int>(pct => { if (!cts.IsCancellationRequested) pbPfsRender.Value = Math.Max(0, Math.Min(100, pct)); });
            SetStatus("Running the game's own sound driver for " + secs.ToString("F0") + " s (" + how + ")...");
            try
            {
                PsxResult r = await WholePsfRunAsync(secs, false, effects, prog, cts.Token);
                if (cts.IsCancellationRequested || gen != _pfsGen) { SetStatus("Render discarded."); return; }
                int n = Math.Min(r.Pcm.Length, r.Frames * 2);
                var f = new float[n];
                for (int i = 0; i < n; i++) f[i] = r.Pcm[i] / 32768f;
                var buf = new AudioBuffer(f, r.Rate, 2);
                _wholeBuf = buf;
                psfWave.Buffer = buf;
                psfWave.MarkFullFit();
                SyncLoopBox(chkLoopPsf, psfWave);
                SyncPfsLoopNums(true);
                lblPsfView.Text = "Whole PSF (game driver): " + Path.GetFileName(psfPath) + "  |  " + buf.DurationSeconds.ToString("F1") + "s  |  " + buf.SampleRate + " Hz stereo";
                tbPsfReport.AppendText(Environment.NewLine + "--- Whole PSF (game driver) ---" + Environment.NewLine + r.Report + Environment.NewLine);
                SetStatus("Whole PSF rendered: " + buf.DurationSeconds.ToString("F1") + " s (" + how + ").");
                PlayWave(psfWave);
            }
            catch (OperationCanceledException) { SetStatus("Render cancelled."); }

            catch (PsxUnsupportedException ux)
            {
                SetStatus("Not played: " + ux.Message);
                MessageBox.Show(this, ux.Message + "\n\n" + PsxDriver.DescribeLoad(File.ReadAllBytes(psfPath)), "Whole PSF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Whole PSF", MessageBoxButtons.OK, MessageBoxIcon.Error); }

            finally
            {
                if (pfsCts == cts) { pbPfsRender.Visible = false; bPfsCancel.Visible = false; bPfsPlay.Enabled = true; }
            }
        }

        /// <summary>HD + BD + SQ built from what the game's driver did for the whole length. A loop set in the waveform is used.</summary>
        private async Task<(PsxExport, string)> WholePsfExportAsync()
        {
            if (psfPath == null || !File.Exists(psfPath)) return (null, "Open a .psf / .minipsf first.");
            bool effects = chkPfsEffects.Checked;
            string how;
            double secs = WholePsfSeconds(out how);
            double a = -1, e = -1;
            var wb = psfWave.Buffer;
            if (wb != null && ReferenceEquals(wb, _wholeBuf) && wb.LoopEnabled && wb.LoopEnd > wb.LoopStart)
            {
                a = wb.LoopStart / (double)wb.SampleRate;
                e = wb.LoopEnd / (double)wb.SampleRate;
            }
            string key = psfPath + "|" + effects + "|" + secs + "|" + a + "|" + e + "|" + File.GetLastWriteTimeUtc(psfPath).Ticks;
            if (_wholeExp != null && key == _wholeExpKey) return (_wholeExp, null);
            Cursor = Cursors.WaitCursor;
            SetStatus("Running the game's driver with logging (" + secs.ToString("F0") + " s)...");
            try
            {
                PsxResult r = await WholePsfRunAsync(secs, true, effects, null, CancellationToken.None);
                string name = SafeFile(Path.GetFileNameWithoutExtension(psfPath));
                PsxExport exp = await Task.Run(() => PsxToPsf2.Build(r.Capture, secs, a, e, effects, name));
                _wholeExp = exp; _wholeExpKey = key;
                tbPsfReport.AppendText(Environment.NewLine + "--- Export from the game's driver (whole PSF) ---" + Environment.NewLine + exp.Report);
                return (exp, null);
            }
            catch (Exception ex) { return (null, ex.Message); }
            finally { Cursor = Cursors.Default; }
        }

        private async void WholePsfSf2()
        {
            var (exp, why) = await WholePsfExportAsync();
            if (exp == null) { MessageBox.Show(this, why ?? "No export.", "Whole PSF - Save SF2"); return; }
            Psf2Bank bk;
            try
            {
                bk = new Psf2Bank();
                bk.LoadFromBytes(exp.Hd, exp.Bd, PfsPath(".hd"), PfsPath(".bd"));
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Whole PSF - Save SF2", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            var keep = bank;
            bank = bk;
            try { ExportSf2(chkPfsEffects.Checked); }
            finally { bank = keep; }
        }

        private async void WholePsfMidi()
        {
            var (exp, why) = await WholePsfExportAsync();
            if (exp == null) { MessageBox.Show(this, why ?? "No export.", "Whole PSF - Save MIDI"); return; }
            using var d = new SaveFileDialog
            {
                Filter = "MIDI|*.mid",
                FileName = PfsStem() + ".mid",
                InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(psfPath)),
                Title = "Save MIDI",
                AddExtension = true
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                BackupOnce(d.FileName);
                File.WriteAllBytes(d.FileName, exp.Song.ToSmf());
                SetStatus("Saved " + Path.GetFileName(d.FileName) + " (from the game's driver, same as Save SF2). In Awave: load the SF2 first, then this MIDI.");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save MIDI", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private async void WholePsfBdHdSq()
        {
            var (exp, why) = await WholePsfExportAsync();
            if (exp == null) { MessageBox.Show(this, why ?? "No export.", "Whole PSF - Save BD + HD + SQ"); return; }
            using var d = new SaveFileDialog
            {
                Filter = "HD|*.hd;*.HD",
                FileName = PfsStem() + ".hd",
                InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(psfPath)),
                Title = "Save HD (the BD and the SQ are saved next to it with the same name)",
                AddExtension = true
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string stem = Path.Combine(Path.GetDirectoryName(d.FileName), Path.GetFileNameWithoutExtension(d.FileName));
            try
            {
                byte[] sq = SqFile.BuildNew(new List<SqSong> { exp.Song }, null);
                BackupOnce(d.FileName); BackupOnce(stem + ".bd"); BackupOnce(stem + ".sq");
                File.WriteAllBytes(d.FileName, exp.Hd);
                File.WriteAllBytes(stem + ".bd", exp.Bd);
                File.WriteAllBytes(stem + ".sq", sq);
                SetStatus("Saved " + Path.GetFileName(d.FileName) + " + .bd + .sq (from the game's driver). " + exp.Effects);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        // ================================================================= PSF Studio
        private void OpenPfsStudio()
        {
            StopRecordingQuietly();
            TransportStop();

            byte[] hd = null, bd = null;
            if (psfPackage != null || (pfsBankEdited && pfsBank != null))
            {
                string err;
                var bk = EnsurePfsBank(PfsAkaoFor(SelectedPsfSong()), out err);
                if (bk == null) { MessageBox.Show(this, err ?? "No bank.", "PSF Studio"); return; }
                try { bk.BuildFiles(out hd, out bd); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "PSF Studio", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            }

            using (var f = new Psf2StudioForm(hd, bd))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                ApplyPfsStudio(f.ResultHd, f.ResultBd);
            }
        }

        private void ApplyPfsStudio(byte[] hd, byte[] bd)
        {
            try
            {
                var nb = new Psf2Bank();
                nb.LoadFromBytes(hd, bd, PfsPath(".hd"), PfsPath(".bd"));
                _pfsGen++;
                pfsBank = nb;
                pfsBankPkg = psfPackage;
                pfsBankKey = null;
                pfsBankEdited = true;
                pfsRenders.Clear(); pfsReports.Clear();
                SetStatus("Studio changes applied (" + bd.Length.ToString("N0") + " byte BD). NOT saved yet: use Save BD + HD + SQ..., Save SF2... or Save as .psf2. Press Play full song to hear it (rendered through the bank, so the PSF effects checkbox has no effect).");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The rebuilt bank could not be loaded:\n" + ex.Message, "PSF Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
