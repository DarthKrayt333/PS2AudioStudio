using System;
using System.Windows.Forms;

namespace PS2AudioStudio.Controls
{
    /// <summary>Sample-rate dropdown: 8000 ... 96000 and Custom (a box to type any rate).
    /// new RatePicker("Keep original (44100 Hz)") adds a first item; Value is then 0 for it.</summary>
    public sealed class RatePicker : FlowLayoutPanel
    {
        public static readonly int[] Presets = { 8000, 11025, 16000, 22050, 32000, 44100, 48000, 96000 };
        public const int MinRate = 1000;
        public const int MaxRate = 384000;

        private readonly ComboBox cmb;
        private readonly NumericUpDown num;
        private readonly Label lblHz;
        private readonly int off;           // 1 when the first item is "Keep original"
        private bool _setting;

        public event EventHandler ValueChanged;

        public RatePicker() : this(null) { }

        public RatePicker(string keepText)
        {
            off = keepText == null ? 0 : 1;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            WrapContents = false;
            FlowDirection = FlowDirection.LeftToRight;
            Margin = Padding.Empty;

            cmb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = off == 1 ? 175 : 110,
                Margin = new Padding(0, 0, 6, 0)
            };
            if (off == 1) cmb.Items.Add(keepText);
            foreach (int r in Presets) cmb.Items.Add(r + " Hz");
            cmb.Items.Add("Custom...");

            num = new NumericUpDown
            {
                Minimum = MinRate,
                Maximum = MaxRate,
                Value = 48000,
                Width = 80,
                Visible = false,
                Margin = new Padding(0, 0, 4, 0)
            };
            lblHz = new Label { Text = "Hz", AutoSize = true, Visible = false, Margin = new Padding(0, 4, 0, 0) };

            cmb.SelectedIndexChanged += (s, e) =>
            {
                bool custom = cmb.SelectedIndex == off + Presets.Length;
                num.Visible = custom;
                lblHz.Visible = custom;
                if (!_setting) ValueChanged?.Invoke(this, EventArgs.Empty);
            };
            num.ValueChanged += (s, e) => { if (!_setting) ValueChanged?.Invoke(this, EventArgs.Empty); };

            Controls.Add(cmb);
            Controls.Add(num);
            Controls.Add(lblHz);
            cmb.SelectedIndex = off == 1 ? 0 : Array.IndexOf(Presets, 48000);
        }

        /// <summary>The chosen rate in Hz (0 = "Keep original" when that item exists).
        /// Setting a value that is not in the list selects Custom.</summary>
        public int Value
        {
            get
            {
                int i = cmb.SelectedIndex;
                if (off == 1 && i == 0) return 0;
                int p = i - off;
                if (p >= 0 && p < Presets.Length) return Presets[p];
                return (int)num.Value;
            }
            set
            {
                _setting = true;
                try
                {
                    if (off == 1 && value <= 0) { cmb.SelectedIndex = 0; return; }
                    int v = Math.Max(MinRate, Math.Min(MaxRate, value));
                    int i = Array.IndexOf(Presets, v);
                    if (i >= 0) cmb.SelectedIndex = i + off;
                    else { num.Value = v; cmb.SelectedIndex = off + Presets.Length; }
                }
                finally { _setting = false; }
            }
        }
    }
}
