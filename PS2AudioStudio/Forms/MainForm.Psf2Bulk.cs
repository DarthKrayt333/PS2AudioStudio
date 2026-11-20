using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PS2AudioStudio.Archives;
using PS2AudioStudio.Audio;

namespace PS2AudioStudio
{
    public partial class MainForm
    {
        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);

        /// <summary>Select several samples (Ctrl+A = all), right-click: the 1st selected sample gets the 1st file (file name order, 2 before 10),
        /// the 2nd the 2nd, and so on. Only the audio is replaced; programs, sample sets and sample parameters stay as they are.</summary>
        private void ReplaceSelectedByNameOrder()
        {
            if (!bank.Loaded) { SetStatus("Load a bank first."); return; }
            var idx = SelectedSampleIndices().Where(i => i >= 0 && i < bank.Samples.Count && bank.Samples[i].Valid).ToList();
            if (idx.Count < 2) { SetStatus("Select two or more samples first (Ctrl+A selects all)."); return; }
            using var d = new OpenFileDialog
            {
                Filter = AudioIO.AudioOnlyFilter,
                Title = $"Choose the files for the {idx.Count} selected samples (they are used in file name order)",
                Multiselect = true
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            var files = d.FileNames.ToList();
            files.Sort((a, b) => StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)));
            int n = Math.Min(idx.Count, files.Count);
            var extras = files.Skip(idx.Count).ToList();
            string extra = extras.Count > 0
                ? $"\n\nThe last {extras.Count} file(s) in the name order have no sample to go to: they are ADDED as NEW samples (empty slots first, then at the end of the bank). Programs, sample sets and sample params are not touched."
                : files.Count < idx.Count ? $"\n\nThe last {idx.Count - files.Count} selected sample(s) stay as they are." : "";
            string first = $"sample #{idx[0]} <- {Path.GetFileName(files[0])}";
            string lastTxt = $"sample #{idx[n - 1]} <- {Path.GetFileName(files[n - 1])}";
            if (MessageBox.Show(this, $"Replace {n} sample(s) in order?\n\n{first}\n...\n{lastTxt}{extra}", "Replace by name order",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                for (int k = 0; k < n; k++) ReplaceSampleFromFile(idx[k], files[k], false);
                if (extras.Count == 0)
                {
                    SetStatus($"Replaced {n} sample(s) by file name order, each with its file's own sample rate. Use Save HD + BD to write the bank.");
                    return;
                }
                // extras: new samples, built the same way PSF2 Studio adds them, then the rebuilt bank is applied
                bank.BuildFiles(out byte[] hd, out byte[] bd);
                int before = HdFile.Parse(hd).Vags.Count(v => v != null);
                byte[] nhd, nbd; string err; List<string> bad; List<int> slots;
                bool ok;
                using (var sf = new Psf2StudioForm(hd, bd))
                    ok = sf.AppendFiles(extras, out nhd, out nbd, out err, out bad, out slots);
                if (!ok)
                {
                    MessageBox.Show(this, "The extra files could not be added:\n" + err + "\n\nThe " + n + " replaced samples are kept.", "Replace by name order", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                int after = HdFile.Parse(nhd).Vags.Count(v => v != null);
                if (after != before + slots.Count)
                {
                    MessageBox.Show(this, "The rebuilt bank does not have the expected number of samples (" + after + " instead of " + (before + slots.Count) + "). Nothing was changed for the extra files.", "Replace by name order", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                ApplyStudioResult(nhd, nbd);
                if (bad.Count > 0)
                    MessageBox.Show(this, "These files could not be added:\n\n" + string.Join("\n", bad.Take(15)), "Replace by name order", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus($"Replaced {n} sample(s) and added {slots.Count} new sample(s) as #{string.Join(", #", slots.Take(20))}{(slots.Count > 20 ? " ..." : "")}. Not saved yet: use Save HD + BD.");
            }
            finally { Cursor = Cursors.Default; }
        }
    }
}
