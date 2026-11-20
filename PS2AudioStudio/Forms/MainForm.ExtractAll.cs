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
    public partial class MainForm
    {
        private static readonly string[] ExtractExts = { ".wav", ".vag", ".adx", ".mp3", ".m4a" };
        private static readonly int[] ExtractRates = { 0, 11025, 22050, 32000, 44100, 48000 };

        /// <summary>PSF2 tab: extract every valid sample as WAV / VAG / ADX / MP3 / M4A.</summary>
        private void ExtractAllSamples()
        {
            if (bank == null) { SetStatus("Open a PSF2 / BD+HD bank first."); return; }
            var list = bank.Samples.Where(x => x.Valid).ToList();
            if (list.Count == 0) { SetStatus("The bank has no samples."); return; }

            if (!AskExtractFormat(list.Count, out string ext, out int rate)) return;

            string dir;
            using (var d = new FolderBrowserDialog { Description = "Folder for the " + list.Count + " extracted samples (" + ext + ")" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                dir = d.SelectedPath;
            }

            int exists = 0;
            foreach (var s in list)
                if (File.Exists(Path.Combine(dir, bank.SampleFileName(s.Index) + ext))) exists++;
            if (exists > 0)
            {
                var r = MessageBox.Show(this, exists + " file(s) with the same name already exist in that folder.\nOverwrite them?",
                    "Extract all", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }

            int ok = 0;
            var bad = new List<string>();
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (var s in list)
                {
                    string path = Path.Combine(dir, bank.SampleFileName(s.Index) + ext);
                    try
                    {
                        if (ext == ".vag")
                        {
                            File.WriteAllBytes(path, bank.ExtractVag(s.Index));
                        }
                        else
                        {
                            AudioBuffer b = s.Decoded.Clone();
                            if (rate > 0 && rate != b.SampleRate) b = b.Resample(rate);
                            switch (ext)
                            {
                                case ".wav": StandardCodec.EncodeWav(b, path); break;
                                case ".adx": File.WriteAllBytes(path, AudioIO.Encode(b, FileKind.Adx, path)); break;
                                case ".mp3": StandardCodec.ExportCompressed(b, path, true); break;
                                case ".m4a": StandardCodec.ExportCompressed(b, path, false); break;
                            }
                        }
                        ok++;
                    }
                    catch (Exception ex) { bad.Add(Path.GetFileName(path) + ": " + ex.Message); }
                }
            }
            finally { Cursor = Cursors.Default; }

            SetStatus("Extracted " + ok + " of " + list.Count + " samples (" + ext + ") to " + dir);
            if (bad.Count > 0)
                MessageBox.Show(this, bad.Count + " sample(s) could not be written:\n\n" + string.Join("\n", bad.Take(15)) +
                    (bad.Count > 15 ? "\n..." : ""), "Extract all", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private bool AskExtractFormat(int count, out string ext, out int rate)
        {
            ext = ".wav"; rate = 0;
            using (var f = new Form())
            {
                f.Text = "Extract all samples";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                f.ClientSize = new Size(380, 160);
                f.BackColor = Ui.Panel; f.ForeColor = Color.White;
                AppIcon.Apply(f);

                var l0 = new Label { Text = count + " samples will be extracted.", AutoSize = true, Location = new Point(14, 12) };
                var l1 = new Label { Text = "Format:", AutoSize = true, Location = new Point(14, 44) };
                var l2 = new Label { Text = "Sample rate:", AutoSize = true, Location = new Point(14, 78) };

                var cf = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 40), Width = 250, BackColor = Ui.Edit, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
                cf.Items.AddRange(new object[] { "WAV (PCM 16-bit)", "VAG (original data, lossless)", "ADX (CRI ADPCM)", "MP3", "M4A (AAC)" });
                cf.SelectedIndex = 0;

                var cr = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 74), Width = 250, BackColor = Ui.Edit, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
                cr.Items.AddRange(new object[] { "Each sample's own rate", "11025 Hz", "22050 Hz", "32000 Hz", "44100 Hz", "48000 Hz" });
                cr.SelectedIndex = 0;

                cf.SelectedIndexChanged += (s, e) => { cr.Enabled = cf.SelectedIndex != 1; };

                var ok = Ui.Btn("Extract...", 100); ok.Location = new Point(150, 118); ok.DialogResult = DialogResult.OK;
                var cancel = Ui.Btn("Cancel", 100); cancel.Location = new Point(260, 118); cancel.DialogResult = DialogResult.Cancel;

                f.Controls.AddRange(new Control[] { l0, l1, l2, cf, cr, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;

                if (f.ShowDialog(this) != DialogResult.OK) return false;
                ext = ExtractExts[cf.SelectedIndex];
                rate = cf.SelectedIndex == 1 ? 0 : ExtractRates[cr.SelectedIndex];
                return true;
            }
        }
    }
}
