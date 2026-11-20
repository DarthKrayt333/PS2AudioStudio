using System;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using PS2AudioStudio.Archives;

namespace PS2AudioStudio
{
    partial class MainForm
    {
        private void InstallPsfTools(Control bar)
        {
            var bProbe = Ui.Btn("🧪 AKAO probe...");
            bProbe.Click += (s, e) => PsfProbeDlg();
            bar.Controls.Add(bProbe);
            PsfWrapBar(bar);
        }

        private void PsfWrapBar(Control bar)
        {
            var f = bar as FlowLayoutPanel;
            if (f == null) return;
            f.WrapContents = true;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.MinimumSize = new Size(0, 40);
        }

        private void PsfShowText(string title, string text)
        {
            text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            try { Clipboard.SetText(text); } catch { }
            using (var f = new Form())
            {
                f.Text = title; f.Width = 950; f.Height = 650;
                f.StartPosition = FormStartPosition.CenterParent;
                var tb = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Dock = DockStyle.Fill,
                    Font = new Font("Consolas", 9f),
                    Text = text
                };
                var bc = new Button { Text = "Copy all", Dock = DockStyle.Bottom, Height = 32 };
                bc.Click += (s, e) => { try { Clipboard.SetText(text); } catch { } };
                f.Controls.Add(tb);
                f.Controls.Add(bc);
                f.ShowDialog(this);
            }
        }

        private static string PsfHex(byte[] d, int from, int len)
        {
            if (from < 0) from = 0;
            var sb = new StringBuilder();
            for (int i = from; i < from + len && i < d.Length; i++) sb.Append(d[i].ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }

        private static string PsfTrackDump(byte[] data, string probe)
        {
            var sb = new StringBuilder();
            var rx = new Regex(@"track (\d+): 0x([0-9A-Fa-f]+)-0x([0-9A-Fa-f]+), parsed to 0x([0-9A-Fa-f]+), (\d+) notes(?:, ERROR: ([^\r\n]*?) at 0x([0-9A-Fa-f]+))?");
            foreach (Match m in rx.Matches(probe))
            {
                int start = Convert.ToInt32(m.Groups[2].Value, 16);
                sb.AppendLine("track " + m.Groups[1].Value + "  start 0x" + start.ToString("X") + ":");
                sb.AppendLine("   first 40 bytes: " + PsfHex(data, start, 40));
                if (m.Groups[7].Success)
                {
                    int err = Convert.ToInt32(m.Groups[7].Value, 16);
                    sb.AppendLine("   ERROR " + m.Groups[6].Value + " at 0x" + err.ToString("X") + " (error byte is the 17th): " + PsfHex(data, err - 16, 40));
                }
            }
            return sb.ToString();
        }

        private void PsfProbeDlg()
        {
            if (psfPackage == null)
            {
                MessageBox.Show(this, "Open a PSF first.", "AKAO probe");
                return;
            }
            var sb = new StringBuilder();
            int n = 0;
            foreach (var b in psfPackage.Buffers)
            {
                n++;
                sb.AppendLine("=== buffer " + n + " (" + b.Data.Length.ToString("N0") + " bytes) ===");
                try
                {
                    string ex = AkaoSongReader.Explain(b.Data);
                    sb.AppendLine(ex);
                    sb.AppendLine("--- raw bytes of each track ---");
                    sb.AppendLine(PsfTrackDump(b.Data, ex));
                }
                catch (Exception ex) { sb.AppendLine("probe failed: " + ex); }
            }
            PsfShowText("AKAO probe - copied to the clipboard", sb.ToString());
        }
    }
}
