using System;
using System.Drawing;
using System.Windows.Forms;

namespace PS2AudioStudio
{
    internal static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(30, 30, 35);
        public static readonly Color Panel = Color.FromArgb(25, 25, 30);
        public static readonly Color Bar = Color.FromArgb(45, 45, 50);
        public static readonly Color Btn1 = Color.FromArgb(62, 62, 70);
        public static readonly Color Edit = Color.FromArgb(55, 55, 60);

        public static Button Btn(string text, int width = 0)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = Btn1,
                ForeColor = Color.White,
                Height = 28,
                Margin = new Padding(2),
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(95, 95, 105);
            if (width > 0) { b.AutoSize = false; b.Width = width; }
            else { b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink; b.Padding = new Padding(4, 0, 4, 0); }
            return b;
        }

        public static Label Lbl(string text, Color? color = null) =>
            new Label { Text = text, AutoSize = true, ForeColor = color ?? Color.White, Margin = new Padding(8, 8, 2, 0) };

        public static FlowLayoutPanel Bar1(int height = 38) =>
            new FlowLayoutPanel { Dock = DockStyle.Top, Height = height, BackColor = Bar, Padding = new Padding(3), WrapContents = false };

        public static void StyleList(ListView lv)
        {
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            lv.GridLines = true;
            lv.MultiSelect = false;
            lv.BackColor = Panel;
            lv.ForeColor = Color.White;
            lv.Font = new Font("Consolas", 9);
        }

        public static ContextMenuStrip Ctx()
        {
            return new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false }, BackColor = Color.FromArgb(45, 45, 50), ForeColor = Color.White };
        }

        public static void StyleMenu(MenuStrip m)
        {
            m.Renderer = new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false };
            m.BackColor = Color.FromArgb(45, 45, 50);
            m.ForeColor = Color.White;
        }
    }

    internal class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Back = Color.FromArgb(45, 45, 50);
        private static readonly Color Sel = Color.FromArgb(75, 75, 90);
        public override Color MenuItemSelected => Sel;
        public override Color MenuItemSelectedGradientBegin => Sel;
        public override Color MenuItemSelectedGradientEnd => Sel;
        public override Color MenuItemPressedGradientBegin => Sel;
        public override Color MenuItemPressedGradientMiddle => Sel;
        public override Color MenuItemPressedGradientEnd => Sel;
        public override Color MenuItemBorder => Color.FromArgb(95, 95, 110);
        public override Color MenuBorder => Color.FromArgb(80, 80, 90);
        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuStripGradientBegin => Back;
        public override Color MenuStripGradientEnd => Back;
        public override Color SeparatorDark => Color.FromArgb(90, 90, 100);
        public override Color SeparatorLight => Color.FromArgb(90, 90, 100);
    }

    internal static class Prompt
    {
        public static string Input(IWin32Window owner, string title, string label, string def)
        {
            using var f = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(420, 120),
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = Ui.Bg,
                ForeColor = Color.White
            };
            var l = new Label { Text = label, Left = 12, Top = 12, Width = 396, Height = 34 };
            var tb = new TextBox { Left = 12, Top = 50, Width = 396, Text = def, BackColor = Ui.Edit, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            var ok = new Button { Text = "OK", Left = 232, Top = 82, Width = 80, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Ui.Btn1 };
            var cancel = new Button { Text = "Cancel", Left = 328, Top = 82, Width = 80, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Ui.Btn1 };
            f.Controls.AddRange(new Control[] { l, tb, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            tb.SelectAll();
            return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
        }

        public static bool Number(IWin32Window owner, string title, string label, double def, out double value)
        {
            value = def;
            string s = Input(owner, title, label, def.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (s == null) return false;
            s = s.Trim().Replace(',', '.');
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out int hx)) { value = hx; return true; }
            if (!double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                MessageBox.Show(owner, "Not a valid number.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }
    }

    /// <summary>"Import raw PS-ADPCM" dialog - for headerless VAG, interleaved VAGi, MI4 / GTA style streams, etc.</summary>
    internal class RawImportDialog : Form
    {
        private readonly NumericUpDown nRate, nCh, nInter, nOff;
        public int Rate => (int)nRate.Value;
        public int Channels => (int)nCh.Value;
        public int Interleave => (int)nInter.Value;
        public int DataOffset => (int)nOff.Value;

        public RawImportDialog(string fileName, int rate, int channels, int interleave, int offset)
        {
            Text = "Import raw PS-ADPCM - " + System.IO.Path.GetFileName(fileName);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(380, 230);
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Ui.Bg; ForeColor = Color.White;

            int y = 14;
            NumericUpDown Row(string label, decimal min, decimal max, decimal val)
            {
                Controls.Add(new Label { Text = label, Left = 14, Top = y + 3, Width = 190 });
                var n = new NumericUpDown { Left = 210, Top = y, Width = 150, Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, val)), BackColor = Ui.Edit, ForeColor = Color.White };
                Controls.Add(n);
                y += 32;
                return n;
            }
            nRate = Row("Sample rate (Hz)", 1000, 192000, rate);
            nCh = Row("Channels", 1, 8, channels);
            nInter = Row("Interleave (bytes / channel)", 16, 1048576, interleave);
            nOff = Row("Data start offset (bytes)", 0, 100000000, offset);

            var info = new Label { Left = 14, Top = y, Width = 350, Height = 30, ForeColor = Color.Silver, Text = "Mono / single stream: channels = 1. Interleave must be a multiple of 16." };
            Controls.Add(info);

            var ok = new Button { Text = "Import", Left = 190, Top = 192, Width = 80, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Ui.Btn1 };
            var cancel = new Button { Text = "Cancel", Left = 280, Top = 192, Width = 80, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Ui.Btn1 };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }
    }
}
