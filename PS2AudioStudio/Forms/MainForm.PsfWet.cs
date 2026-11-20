using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using PS2AudioStudio.Archives;

namespace PS2AudioStudio
{
    public partial class MainForm
    {
        private ComboBox cmbPfsWet;
        private bool _pfsWetBulk;
        private bool _pfsWetUnlocked;

        private static bool TryParseHexByte(string text, out int value)
        {
            value = 0;
            if (text == null) return false;
            string t = text.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            if (t.Length == 0 || t.Length > 2) return false;
            return int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) && value >= 0 && value <= 255;
        }

        private void ShowPfsWetValue()
        {
            if (cmbPfsWet == null) return;
            _pfsWetBulk = true;
            try { cmbPfsWet.Text = PsfWet.Byte41.ToString("X2"); }
            finally { _pfsWetBulk = false; }
        }

        private void UpdatePfsWetEnabled()
        {
            if (cmbPfsWet == null) return;
            cmbPfsWet.Enabled = _pfsWetUnlocked && chkPfsEffects != null && chkPfsEffects.Checked;
        }

        private void OnPfsWetGear()
        {
            if (_pfsWetUnlocked)
            {
                _pfsWetUnlocked = false;
                PsfWet.Byte41 = PsfWet.Default;
                ShowPfsWetValue();
                UpdatePfsWetEnabled();
                SetStatus("Wet byte locked again and set back to 27.");
                return;
            }
            var r = MessageBox.Show(this,
                "It is NOT recommended to change this.\n\n" +
                "27 is the tested value: it sounded right in the real game and in foobar. 27 is the best default.\n" +
                "Other values were not tested. They can sound wrong, too wet, one-sided or silent.\n\n" +
                "Unlock the box anyway?",
                "Wet byte", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;
            _pfsWetUnlocked = true;
            UpdatePfsWetEnabled();
            SetStatus("Wet byte unlocked. Click the gear again to lock it and go back to 27.");
        }

        /// <summary>"Wet byte" label + ⚙ button + hex box. The box stays greyed out until the gear is clicked and the warning is confirmed.</summary>
        private Control MakePfsWetBox()
        {
            var panel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            var gear = Ui.Btn("⚙", 34);
            gear.Click += (s, e) => OnPfsWetGear();
            var cmb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                Width = 60,
                MaxLength = 4,
                Margin = new Padding(2, 6, 8, 0),
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            cmbPfsWet = cmb;
            cmb.Items.Add("1F");
            for (int v = 0x20; v <= 0x3F; v++) cmb.Items.Add(v.ToString("X2"));
            ShowPfsWetValue();
            cmb.TextChanged += (s, e) =>
            {
                if (_pfsWetBulk) return;
                int v;
                if (TryParseHexByte(cmb.Text, out v)) PsfWet.Byte41 = v;
            };
            cmb.Leave += (s, e) => ShowPfsWetValue();
            cmb.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { ShowPfsWetValue(); e.SuppressKeyPress = true; }
            };
            if (chkPfsEffects != null) chkPfsEffects.CheckedChanged += (s, e) => UpdatePfsWetEnabled();
            UpdatePfsWetEnabled();
            panel.Controls.Add(Ui.Lbl("Wet byte:"));
            panel.Controls.Add(gear);
            panel.Controls.Add(cmb);
            return panel;
        }
    }
}
