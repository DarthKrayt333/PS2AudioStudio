using System;
using System.Drawing;
using System.Windows.Forms;
using PS2AudioStudio.Audio;
using PS2AudioStudio.Controls;

namespace PS2AudioStudio
{
    /// <summary>
    /// "Save as which sample rate / channels?" dialog.
    ///   var toSave = SaveRateDialog.Ask(this, buf, "ADX");  if (toSave == null) return;   // cancelled
    /// Returns the same buffer when nothing changes, or a converted copy (loop points scaled).
    /// Whether this dialog is shown at all is controlled only in Settings (AskSaveRate, default ON).
    /// The chosen rate is remembered in the settings (SaveRate).
    /// </summary>
    public sealed class SaveRateDialog : Form
    {
        private readonly RatePicker pick;
        private readonly ComboBox cbCh = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly int srcRate;
        private readonly int[] chValues;

        public int SelectedRate { get; private set; }        // 0 = keep original
        public int SelectedChannels { get; private set; }    // 0 = keep original, 1 = mono, 2 = stereo

        private SaveRateDialog(AudioBuffer buf, string label)
        {
            srcRate = buf.SampleRate;
            Text = "Save - sample rate / channels";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(460, 222);
            AppIcon.Apply(this);

            string up = (label ?? "").ToUpperInvariant();
            bool restricted = up.Contains("MP3") || up.Contains("M4A") || up.Contains("AAC");
            bool monoOnly = up.Contains("VAG") && !up.Contains("INTERLEAV");

            var info = new Label
            {
                Left = 14,
                Top = 12,
                Width = 432,
                Height = 34,
                Text = $"Saving as {label}.\r\nSource: {buf.SampleRate} Hz, {buf.Channels} ch, {buf.DurationSeconds:F2} s"
            };

            // ---- sample rate ----
            var lbl = new Label { Left = 14, Top = 56, Width = 110, Height = 20, Text = "Sample rate (Hz):" };
            pick = new RatePicker($"Keep original ({buf.SampleRate} Hz)");
            pick.Left = 130; pick.Top = 52;
            pick.Value = AppSettings.Current.SaveRate;       // the last choice (0 = keep original)

            // ---- channels ----
            var lblCh = new Label { Left = 14, Top = 94, Width = 110, Height = 20, Text = "Channels:" };
            cbCh.SetBounds(130, 90, 200, 24);
            if (monoOnly)
            {
                chValues = new[] { 1 };
                cbCh.Items.Add("Mono (VAG is always mono)");
            }
            else
            {
                chValues = new[] { 0, 1, 2 };
                cbCh.Items.Add($"Keep original ({buf.Channels} ch)");
                cbCh.Items.Add("Mono (1 ch)");
                cbCh.Items.Add("Stereo (2 ch)");
            }
            cbCh.SelectedIndex = 0;

            var hint = new Label
            {
                Left = 14,
                Top = 126,
                Width = 432,
                Height = 50,
                ForeColor = SystemColors.GrayText,
                Text = (restricted
                    ? "MP3 / M4A: the Windows encoder only accepts some rates. If saving fails, pick 44100 or 48000.\r\n"
                    : "Pick a rate or choose Custom to type any rate. Loop points are converted automatically.\r\n")
                    + "Stereo to mono averages both channels; mono to stereo duplicates the channel.\r\n"
                    + "Your choice is remembered. This question can be turned off in Settings."
            };

            var ok = new Button { Text = "Save", Left = 278, Top = 184, Width = 84, Height = 28 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 370, Top = 184, Width = 78, Height = 28 };
            AcceptButton = ok; CancelButton = cancel;
            Controls.AddRange(new Control[] { info, lbl, pick, lblCh, cbCh, hint, ok, cancel });

            ok.Click += (s, e) =>
            {
                int chosen = pick.Value;                      // 0 = keep original
                AppSettings.Current.SaveRate = chosen;        // remember it
                AppSettings.Current.Save();
                SelectedRate = chosen == srcRate ? 0 : chosen;
                int ci = cbCh.SelectedIndex;
                SelectedChannels = (ci >= 0 && ci < chValues.Length) ? chValues[ci] : 0;
                DialogResult = DialogResult.OK;
            };
        }

        /// <summary>Returns the buffer to save (original or converted) or null if the user cancelled.</summary>
        public static AudioBuffer Ask(IWin32Window owner, AudioBuffer buf, string formatLabel)
        {
            if (buf == null) return null;
            if (!AppSettings.Current.AskSaveRate) return buf;

            int rate, chans;
            using (var dlg = new SaveRateDialog(buf, formatLabel))
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                rate = dlg.SelectedRate;
                chans = dlg.SelectedChannels;
            }

            bool changeCh = chans > 0 && chans != buf.Channels;
            bool changeRate = rate > 0 && rate != buf.SampleRate;
            if (!changeCh && !changeRate) return buf;

            var old = Cursor.Current;
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                AudioBuffer work = buf;
                if (changeCh) work = work.ToChannels(chans);               // first: less data to resample
                if (changeRate) work = AudioIO.Resample(work, rate);
                return work;
            }
            finally { Cursor.Current = old; }
        }
    }
}
