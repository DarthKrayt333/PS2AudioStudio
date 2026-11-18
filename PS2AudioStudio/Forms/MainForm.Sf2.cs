using System;
using System.IO;
using System.Windows.Forms;
using PS2AudioStudio.Archives;

namespace PS2AudioStudio
{
    partial class MainForm
    {
        private Control MakeSf2Button()
        {
            var panel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            var bExp = Ui.Btn("🎹 Export SF2...", 130);
            bExp.Click += (s, e) => ExportSf2();
            var bImp = Ui.Btn("🎹 Import SF2...", 130);
            bImp.Click += (s, e) => ImportSf2Dlg();
            panel.Controls.Add(bExp);
            panel.Controls.Add(bImp);
            return panel;
        }

        private void ExportSf2()
        {
            if (bank == null || !bank.Loaded)
            {
                SetStatus("Open a PSF2 bank (BD + HD) first.");
                return;
            }

            string baseName = "bank";
            if (!string.IsNullOrEmpty(bank.HdPath))
                baseName = Path.GetFileNameWithoutExtension(bank.HdPath);

            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Export bank as SoundFont 2";
                dlg.Filter = "SoundFont 2 (*.sf2)|*.sf2";
                dlg.FileName = baseName + ".sf2";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    Cursor = Cursors.WaitCursor;
                    string report;
                    var sf = Psf2ToSf2.Convert(bank, Path.GetFileNameWithoutExtension(dlg.FileName), out report);
                    byte[] data = sf.Build();
                    if (File.Exists(dlg.FileName)) BackupOnce(dlg.FileName);
                    File.WriteAllBytes(dlg.FileName, data);
                    SetStatus("Exported SF2: " + Path.GetFileName(dlg.FileName) + " (" + (data.Length / 1024) + " KB)");
                    MessageBox.Show(this, report, "SF2 exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "SF2 export failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    Cursor = Cursors.Default;
                }
            }
        }

        private void ImportSf2Dlg()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "Import a SoundFont 2 as a PSF2 bank";
                d.Filter = "SoundFont 2 (*.sf2)|*.sf2|All files|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                ImportSf2File(d.FileName);
            }
        }

        /// <summary>SF2 -> PSF2 bank (HD + BD), loaded straight into the PSF2 tab. Save it with 'Save HD + BD...'.</summary>
        private void ImportSf2File(string path)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                byte[] raw = File.ReadAllBytes(path);
                Sf2File sf = Sf2File.Parse(raw);

                byte[] hd, bd;
                string report;
                Sf2ToPsf2.Build(sf, out hd, out bd, out report);

                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                string stem = Path.GetFileNameWithoutExtension(path);
                var nb = new Psf2Bank();
                nb.LoadFromBytes(hd, bd, Path.Combine(dir, stem + ".hd"), Path.Combine(dir, stem + ".bd"));
                nb.Modified = true;
                ApplyBank(nb);

                SetStatus("Imported " + Path.GetFileName(path) + " as a PSF2 bank. Use Save HD + BD... to write the files.");
                Cursor = Cursors.Default;
                MessageBox.Show(this, report, "SF2 imported", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, ex.Message, "SF2 import failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }
    }
}
