using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using PS2AudioStudio.Archives;

namespace PS2AudioStudio
{
    public partial class MainForm
    {
        private Button MakeHdTestButton()
        {
            var b = Ui.Btn("🧪 HD round-trip test...");
            b.Click += (s, e) => HdRoundTripTest();
            return b;
        }

        /// <summary>Reads each chosen .HD into the editable structure, rebuilds it and compares it byte for byte.</summary>
        private void HdRoundTripTest()
        {
            using (var d = new OpenFileDialog { Filter = "HD files|*.hd;*.HD|All|*.*", Title = "Select one or more .HD files to test", Multiselect = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var sb = new StringBuilder();
                foreach (string path in d.FileNames)
                {
                    sb.AppendLine(Path.GetFileName(path));
                    try
                    {
                        byte[] h = File.ReadAllBytes(path);
                        var f = HdFile.Parse(h);
                        byte[] r = f.Build(f.BdSize);
                        sb.AppendLine("   " + f.Summary());
                        sb.AppendLine("   " + HdFile.Compare(h, r));
                    }
                    catch (Exception ex) { sb.AppendLine("   ERROR: " + ex.Message); }
                    sb.AppendLine();
                }
                try { Clipboard.SetText(sb.ToString()); } catch { }
                MessageBox.Show(this, sb.ToString() + "(the result was copied to the clipboard)", "HD round-trip test");
            }
        }
    }
}
