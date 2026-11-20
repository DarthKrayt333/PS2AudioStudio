using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using PS2AudioStudio.Audio;
using PS2AudioStudio.Controls;

namespace PS2AudioStudio
{
    public sealed class SettingsForm : Form
    {
        private readonly ComboBox cbDev = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button btnRefresh = new Button { Text = "Refresh" };
        private readonly RatePicker pkRate = new RatePicker();
        private readonly RadioButton rbActive = new RadioButton { Text = "Only the active tab" };
        private readonly RadioButton rbAll = new RadioButton { Text = "All tabs (Editor + PSF2 + PSF + AFS)  (default)" };
        private readonly RadioButton rbCustom = new RadioButton { Text = "Choose which tabs:" };
        private readonly CheckBox ckEd = new CheckBox { Text = "Audio Editor" };
        private readonly CheckBox ckPsf2 = new CheckBox { Text = "PSF2 Bank" };
        private readonly CheckBox ckPsf = new CheckBox { Text = "PSF (PS1)" };
        private readonly CheckBox ckAfs = new CheckBox { Text = "AFS Archive" };
        private readonly CheckBox ckLoop = new CheckBox { Text = "Play looped (by the loop points) when Loop is ticked" };
        private readonly CheckBox ckAsk = new CheckBox { Text = "Ask for the sample rate every time I save" };
        private readonly CheckBox ckBak = new CheckBox { Text = "Make .bak backup files when overwriting (default: on)" };
        private readonly ComboBox cbAdx = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private List<RecordDevice> devs;

        public SettingsForm()
        {
            Text = "Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(560, 558);
            AppIcon.Apply(this);

            var gRec = new GroupBox { Text = "Recording", Left = 12, Top = 12, Width = 536, Height = 172 };
            cbDev.SetBounds(12, 28, 420, 24);
            btnRefresh.SetBounds(442, 27, 80, 26);
            var hint = new Label
            {
                Left = 12,
                Top = 60,
                Width = 510,
                Height = 44,
                Text = "Input = microphone / line-in / Stereo Mix.\r\nPC audio (loopback) = records whatever Windows is playing on that output - no Stereo Mix needed."
            };
            var lblRate = new Label { Left = 12, Top = 114, Width = 150, Height = 20, Text = "Recording sample rate:" };
            pkRate.Left = 170; pkRate.Top = 110;
            var rateHint = new Label
            {
                Left = 12,
                Top = 140,
                Width = 510,
                Height = 28,
                ForeColor = SystemColors.GrayText,
                Text = "Used every time you press Rec (default 48000 Hz). Windows records at its own device rate; the result is converted to this rate."
            };
            gRec.Controls.AddRange(new Control[] { cbDev, btnRefresh, hint, lblRate, pkRate, rateHint });

            var gNew = new GroupBox { Text = "File > New project clears", Left = 12, Top = 194, Width = 536, Height = 150 };
            rbActive.SetBounds(14, 24, 400, 22);
            rbAll.SetBounds(14, 48, 400, 22);
            rbCustom.SetBounds(14, 72, 400, 22);
            ckEd.SetBounds(40, 98, 105, 22);
            ckPsf2.SetBounds(150, 98, 105, 22);
            ckPsf.SetBounds(260, 98, 110, 22);
            ckAfs.SetBounds(375, 98, 120, 22);
            gNew.Controls.AddRange(new Control[] { rbActive, rbAll, rbCustom, ckEd, ckPsf2, ckPsf, ckAfs });

            var gPlay = new GroupBox { Text = "Playback / saving", Left = 12, Top = 354, Width = 536, Height = 110 };
            ckLoop.SetBounds(14, 22, 500, 22);
            ckAsk.SetBounds(14, 48, 500, 22);
            ckBak.SetBounds(14, 74, 500, 22);
            gPlay.Controls.AddRange(new Control[] { ckLoop, ckAsk, ckBak });

            var lblAdx = new Label { Left = 16, Top = 482, Width = 150, Height = 20, Text = "ADX loop layout:" };
            cbAdx.SetBounds(170, 478, 370, 24);
            cbAdx.Items.Add("CRI standard header (07 FC) - adds a little silence");
            cbAdx.Items.Add("Compact (no silence added)  - default");

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 380, Top = 516, Width = 80, Height = 28 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 468, Top = 516, Width = 80, Height = 28 };
            AcceptButton = ok; CancelButton = cancel;
            Controls.AddRange(new Control[] { gRec, gNew, gPlay, lblAdx, cbAdx, ok, cancel });

            btnRefresh.Click += (s, e) => FillDevices();
            rbCustom.CheckedChanged += (s, e) => UpdateCustom();
            ok.Click += (s, e) => Apply();

            Load += (s, e) => { FillDevices(); LoadValues(); };
        }

        private void FillDevices()
        {
            devs = RecordDevices.List();
            cbDev.Items.Clear();
            foreach (var d in devs) cbDev.Items.Add(d);
            var cur = AppSettings.Current;
            int sel = 0;
            for (int i = 0; i < devs.Count; i++)
                if (devs[i].Kind == cur.RecordKind && devs[i].Id == cur.RecordDeviceId) { sel = i; break; }
            cbDev.SelectedIndex = cbDev.Items.Count > 0 ? sel : -1;
        }

        private void LoadValues()
        {
            var s = AppSettings.Current;
            pkRate.Value = s.RecordRate;
            rbActive.Checked = s.NewMode == NewProjectMode.ActiveTabOnly;
            rbAll.Checked = s.NewMode == NewProjectMode.AllTabs;
            rbCustom.Checked = s.NewMode == NewProjectMode.Custom;
            ckEd.Checked = s.NewClearEditor;
            ckPsf2.Checked = s.NewClearPsf2;
            ckPsf.Checked = s.NewClearPsf;
            ckAfs.Checked = s.NewClearAfs;
            ckLoop.Checked = s.PlayLoopedWhenChecked;
            ckAsk.Checked = s.AskSaveRate;
            ckBak.Checked = s.MakeBackups;
            cbAdx.SelectedIndex = s.AdxLayoutMode == 0 ? 0 : 1;
            UpdateCustom();
        }

        private void UpdateCustom() { ckEd.Enabled = ckPsf2.Enabled = ckPsf.Enabled = ckAfs.Enabled = rbCustom.Checked; }

        private void Apply()
        {
            var s = AppSettings.Current;
            if (cbDev.SelectedItem is RecordDevice d) { s.RecordKind = d.Kind; s.RecordDeviceId = d.Id; s.RecordDeviceName = d.Name; }
            s.RecordRate = pkRate.Value;
            s.NewMode = rbAll.Checked ? NewProjectMode.AllTabs : rbCustom.Checked ? NewProjectMode.Custom : NewProjectMode.ActiveTabOnly;
            s.NewClearEditor = ckEd.Checked;
            s.NewClearPsf2 = ckPsf2.Checked;
            s.NewClearPsf = ckPsf.Checked;
            s.NewClearAfs = ckAfs.Checked;
            s.PlayLoopedWhenChecked = ckLoop.Checked;
            s.AskSaveRate = ckAsk.Checked;
            s.MakeBackups = ckBak.Checked;
            s.AdxLayoutMode = cbAdx.SelectedIndex == 0 ? 0 : 1;
            s.Save();
        }
    }
}
