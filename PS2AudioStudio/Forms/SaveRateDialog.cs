using System;
using System.Drawing;
using System.Windows.Forms;
using PS2AudioStudio.Audio;

namespace PS2AudioStudio
{
    /// <summary>
    /// "Save as which sample rate / channels?" dialog.
    ///   var toSave = SaveRateDialog.Ask(this, buf, "ADX");  if (toSave == null) return;   // cancelled
    /// Returns the same buffer when nothing changes, or a converted copy (loop points scaled).
    /// Whether this dialog is shown at all is controlled only in Settings (AskSaveRate, default ON).
    /// </summary>
    public sealed class SaveRateDialog : Form
    {
        private readonly ComboBox cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
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
            ClientSize = new Size(420, 214);
            AppIcon.Apply(this);

            string up = (label ?? "").ToUpperInvariant();
            bool restricted = up.Contains("MP3") || up.Contains("M4A") || up.Contains("AAC");
            bool monoOnly = up.Contains("VAG") && !up.Contains("INTERLEAV");

            var info = new Label
            {
                Left = 14,
                Top = 12,
                Width = 392,
                Height = 34,
                Text = $"Saving as {label}.\r\nSource: {buf.SampleRate} Hz, {buf.Channels} ch, {buf.DurationSeconds:F2} s"
            };

            // ---- sample rate ----
            var lbl = new Label { Left = 14, Top = 56, Width = 110, Height = 20, Text = "Sample rate (Hz):" };
            cb.SetBounds(130, 52, 200, 24);
            cb.Items.Add($"Keep original ({buf.SampleRate} Hz)");
            int[] presets = restricted
                ? new[] { 32000, 44100, 48000 }
                : new[] { 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 88200, 96000 };
            foreach (int r in presets) cb.Items.Add(r + " Hz");
            cb.SelectedIndex = 0;

            // ---- channels ----
            var lblCh = new Label { Left = 14, Top = 90, Width = 110, Height = 20, Text = "Channels:" };
            cbCh.SetBounds(130, 86, 200, 24);
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
                Top = 120,
                Width = 392,
                Height = 50,
                ForeColor = SystemColors.GrayText,
                Text = (restricted
                    ? "The Windows MP3/M4A encoder only accepts some rates (usually 32000 / 44100 / 48000).\r\n"
                    : "Pick a preset or type any rate (1000 - 192000). Loop points are converted automatically.\r\n")
                    + "Stereo to mono averages both channels; mono to stereo duplicates the channel.\r\n"
                    + "This question can be turned off in Settings."
            };

            var ok = new Button { Text = "Save", Left = 238, Top = 176, Width = 84, Height = 28 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 330, Top = 176, Width = 78, Height = 28 };
            AcceptButton = ok; CancelButton = cancel;
            Controls.AddRange(new Control[] { info, lbl, cb, lblCh, cbCh, hint, ok, cancel });

            ok.Click += (s, e) =>
            {
                if (!TryGetRate(out int rate))
                {
                    MessageBox.Show(this, "Enter a sample rate between 1000 and 192000 Hz.", "Sample rate",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                SelectedRate = rate;
                int ci = cbCh.SelectedIndex;
                SelectedChannels = (ci >= 0 && ci < chValues.Length) ? chValues[ci] : 0;
                DialogResult = DialogResult.OK;
            };
        }

        private bool TryGetRate(out int rate)
        {
            rate = 0;
            string t = (cb.Text ?? "").Trim();
            if (t.Length == 0 || t.StartsWith("Keep", StringComparison.OrdinalIgnoreCase)) return true;
            var digits = new System.Text.StringBuilder();
            foreach (char ch in t) if (char.IsDigit(ch)) digits.Append(ch);
            if (digits.Length == 0 || digits.Length > 6) return false;
            if (!int.TryParse(digits.ToString(), out int v)) return false;
            if (v < 1000 || v > 192000) return false;
            rate = v == srcRate ? 0 : v;
            return true;
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
