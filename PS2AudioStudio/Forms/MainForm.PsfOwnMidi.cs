using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PS2AudioStudio.Archives;
using PS2AudioStudio.Audio;
using PS2AudioStudio.Controls;

namespace PS2AudioStudio
{
    public partial class MainForm
    {
        private Psf2Bank psfOwnBank;
        private string psfOwnKey, psfOwnHow;
        private bool psfOwnBusy;

        private static void PsfCollectButtons(Control root, List<Button> into)
        {
            foreach (Control c in root.Controls)
            {
                var b = c as Button;
                if (b != null) into.Add(b);
                PsfCollectButtons(c, into);
            }
        }

        private static void PsfInvokeClick(Button b)
        {
            var m = typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
            if (m != null) m.Invoke(b, new object[] { EventArgs.Empty });
        }

        /// <summary>
        /// Replaces the "Play full song" button of the Full-song bar: AKAO songs keep the old behaviour,
        /// every other song (your own MIDI / SQ / SEQ) goes to PsfPlayOwnSong. Hides the extra "Render full song" button.
        /// </summary>
        private void PsfRehookButtons(Control root)
        {
            var all = new List<Button>();
            PsfCollectButtons(root, all);
            Button oldFull = all.FirstOrDefault(b => b.Text.Contains("Play full song"));
            if (oldFull == null || oldFull.Parent == null) return;
            foreach (var b in all.Where(b => b.Text.Contains("Render full song")).ToList()) b.Visible = false;

            var parent = oldFull.Parent;
            int idx = parent.Controls.GetChildIndex(oldFull);
            var nb = Ui.Btn(oldFull.Text);
            nb.Enabled = oldFull.Enabled;
            oldFull.EnabledChanged += (s, e) => nb.Enabled = oldFull.Enabled;
            nb.Click += (s, e) =>
            {
                var song = SelectedPsfSong();
                if (song != null && psfGm.ContainsKey(song)) PsfInvokeClick(oldFull);
                else PsfPlayOwnSong();
            };
            oldFull.Visible = false;
            parent.Controls.Add(nb);
            parent.Controls.SetChildIndex(nb, idx);
        }

        private Psf2Bank PsfOwnBankGet(bool fx, out string how, out string problem)
        {
            how = ""; problem = null;
            string key = (psfPath ?? "") + "|" + fx;
            if (psfOwnBank != null && psfOwnKey == key) { how = psfOwnHow; return psfOwnBank; }

            SqSong rawAk = psfGm.Keys.FirstOrDefault();
            var d = rawAk != null ? PfsAkaoFor(rawAk) : null;
            if (d == null)
            {
                problem = "This PSF has no recognised AKAO song, so there is no sound bank to play your MIDI through.\n\n" +
                          "Open a PSF that shows an AKAO song in the Song box, or load an HD + BD in the PSF2 tab and play it there.";
                return null;
            }

            string stem = SafeFile(Path.GetFileNameWithoutExtension(psfPath ?? "psf"));
            string why = "";
            try
            {
                PsxExport px = BuildPfsDriverExport(rawAk, d, fx, out why);
                if (px != null)
                {
                    var nb = new Psf2Bank();
                    nb.LoadFromBytes(px.Hd, px.Bd, stem + ".hd", stem + ".bd");
                    psfOwnBank = nb; psfOwnKey = key; psfOwnHow = "exact bank from the game's driver";
                    how = psfOwnHow;
                    return nb;
                }
            }
            catch (Exception ex) { why = ex.Message; }

            string w = (why ?? "").Replace("\r", "").Split('\n')[0].Trim();
            try
            {
                string err;
                var bk = EnsurePfsBank(d, out err);
                if (bk == null) { problem = "No bank could be built from the AKAO data: " + (err ?? "unknown reason"); return null; }
                byte[] hd, bd;
                bk.BuildFiles(out hd, out bd);
                var nb2 = new Psf2Bank();
                nb2.LoadFromBytes(hd, bd, bk.HdPath, bk.BdPath);
                psfOwnBank = nb2; psfOwnKey = key;
                psfOwnHow = "bank built from the AKAO data";
                how = psfOwnHow;
                return nb2;
            }
            catch (Exception ex) { problem = "Building the bank failed: " + ex.Message; return null; }
        }

        /// <summary>Calls Psf2Renderer.Render by looking its signature up at run time.</summary>
        private AudioBuffer PsfOwnRender(Psf2Bank bank, SqSong song, bool fx)
        {
            MethodInfo mi = null;
            foreach (var m in typeof(Psf2Renderer).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                if (m.Name == "Render" && m.GetParameters().Any(p => p.ParameterType == typeof(Psf2Bank))) { mi = m; break; }
            if (mi == null) throw new Exception("Psf2Renderer.Render was not found.");

            var ps = mi.GetParameters();
            int boolTotal = ps.Count(p => p.ParameterType == typeof(bool));
            var args = new object[ps.Length];
            int boolSeen = 0;
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;
                if (t == typeof(Psf2Bank)) args[i] = bank;
                else if (t == typeof(SqSong)) args[i] = song;
                else if (t == typeof(CancellationToken)) args[i] = CancellationToken.None;
                else if (t == typeof(bool)) { boolSeen++; args[i] = (boolTotal == 1 || boolSeen == boolTotal) ? fx : false; }
                else if (t == typeof(StringBuilder)) args[i] = new StringBuilder();
                else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) args[i] = Activator.CreateInstance(t);
                else if (t.IsValueType) args[i] = Activator.CreateInstance(t);
                else args[i] = null;
            }
            object r = mi.Invoke(null, args);
            var buf = r as AudioBuffer;
            if (buf == null) throw new Exception("The renderer returned " + (r == null ? "nothing" : r.GetType().Name) + " instead of audio.");
            return buf;
        }

        private async void PsfPlayOwnSong()
        {
            if (psfOwnBusy) { SetStatus("Still rendering - please wait."); return; }
            var song = SelectedPsfSong();
            if (song == null)
            {
                MessageBox.Show(this, "No song is selected. Load one with 📂 Open MIDI / SQ / SEQ... first.", "Play full song");
                return;
            }
            bool fx = chkPfsEffects != null && chkPfsEffects.Checked;
            psfOwnBusy = true;
            try
            {
                TransportStop();
                SetStatus("Building the PSF's sound bank...");
                await Task.Delay(30);
                string how, problem;
                var bank = PsfOwnBankGet(fx, out how, out problem);
                if (bank == null)
                {
                    MessageBox.Show(this, problem ?? "No bank.", "Play full song");
                    SetStatus("Full song: no bank.");
                    return;
                }
                SetStatus("Rendering " + song.Name + " through the PSF bank (" + how + ")...");
                AudioBuffer buf = null; string err = null;
                await Task.Run(() =>
                {
                    try { buf = PsfOwnRender(bank, song, fx); }
                    catch (Exception ex) { err = ex.InnerException != null ? ex.InnerException.Message : ex.Message; }
                });
                if (buf == null)
                {
                    MessageBox.Show(this, "Rendering failed:\n\n" + err, "Play full song");
                    SetStatus("Full song: rendering failed.");
                    return;
                }
                psfWave.Buffer = buf.Clone();
                SyncLoopBox(chkLoopPsf, psfWave);
                lblPsfView.Text = song.Name + "  |  full song through the PSF bank  |  " + buf.DurationSeconds.ToString("F1") + "s  |  " + how;

                { var lb = psfWave.Buffer; if (lb != null) { ApplySongLoopToBuffer(lb, song); psfWave.Buffer = lb; SyncLoopBox(chkLoopPsf, psfWave); } }

                PlayWave(psfWave);
                SetStatus("Playing " + song.Name + " - " + how + ".");
            }
            finally { psfOwnBusy = false; }
        }
    }
}
