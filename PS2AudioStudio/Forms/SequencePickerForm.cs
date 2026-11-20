using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using PS2AudioStudio.Archives;

namespace PS2AudioStudio
{
    public sealed class SequencePickerForm : Form
    {
        public List<SqSong> Result;

        public SequencePickerForm(string why)
        {
            Text = "Place a sequence manually";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(480, 250);
            BackColor = Ui.Bg; ForeColor = Color.White;

            var info = new Label
            {
                Left = 12,
                Top = 10,
                Width = 456,
                Height = 70,
                Text = why + "\r\n\r\nChoose a MIDI (.mid), SQ (.sq) or PS1 SEQ (.seq) file, or drop it on the box. " +
                       "It then runs as the MIDI sequence of this soundbank."
            };
            var drop = new Label
            {
                Left = 12,
                Top = 88,
                Width = 456,
                Height = 100,
                AllowDrop = true,
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Ui.Panel,
                Text = "Drop a .mid / .sq / .seq file here"
            };
            drop.DragEnter += (s, e) =>
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            drop.DragDrop += (s, e) =>
            {
                var f = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (f != null && f.Length > 0) TryLoad(f[0]);
            };
            var open = new Button { Text = "Open file...", Left = 12, Top = 202, Width = 130, Height = 32 };
            open.Click += (s, e) =>
            {
                using var d = new OpenFileDialog { Filter = "MIDI / SQ / SEQ|*.mid;*.midi;*.sq;*.seq|All|*.*" };
                if (d.ShowDialog(this) == DialogResult.OK) TryLoad(d.FileName);
            };
            var skip = new Button { Text = "No sequence", Left = 338, Top = 202, Width = 130, Height = 32, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { info, drop, open, skip });
            CancelButton = skip;
        }

        private void TryLoad(string path)
        {
            try
            {
                Result = SqSongSeq.LoadAny(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Sequence", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        public static List<SqSong> Ask(IWin32Window owner, string why)
        {
            using var f = new SequencePickerForm(why);
            return f.ShowDialog(owner) == DialogResult.OK ? f.Result : null;
        }
    }
}
