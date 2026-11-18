using System;
using System.IO;
using System.Windows.Forms;
using PS2AudioStudio.Archives;

namespace PS2AudioStudio
{
    public partial class MainForm
    {
        private Button MakeStudioButton()
        {
            var b = Ui.Btn("🎛 PSF2 Studio...");
            b.Click += (s, e) => OpenPsf2Studio();
            return b;
        }

        private void OpenPsf2Studio()
        {
            StopRecordingQuietly();
            TransportStop();

            byte[] hd = null, bd = null;
            if (bank != null && bank.Loaded)
            {
                try { bank.BuildFiles(out hd, out bd); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "PSF2 Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            using (var f = new Psf2StudioForm(hd, bd))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                ApplyStudioResult(f.ResultHd, f.ResultBd);
            }
        }

        private void ApplyStudioResult(byte[] hd, byte[] bd)
        {
            string hdPath, bdPath;
            if (bank != null && bank.Loaded) { hdPath = bank.HdPath; bdPath = bank.BdPath; }
            else
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                hdPath = Path.Combine(dir, "new_bank.hd");
                bdPath = Path.Combine(dir, "new_bank.bd");
            }
            try
            {
                var nb = new Psf2Bank();
                nb.LoadFromBytes(hd, bd, hdPath, bdPath);
                ApplyBank(nb);
                sqModified = true;      // makes the "unsaved changes" warning appear when closing
                SetStatus("Studio changes applied (" + bd.Length.ToString("N0") + " byte BD). NOT saved yet: use Save HD + BD..., or Save as .psf2. Press Play song to hear it.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The rebuilt bank could not be loaded:\n" + ex.Message, "PSF2 Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
