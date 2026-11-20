using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace PS2AudioStudio
{
    // PSF tab: "Diagnose PSF" scan. Shows what is really inside a PSF and what the app's own loader makes of it.
    public partial class MainForm
    {
        private void InstallPsfExtras(Control bar)
        {
            var b = Ui.Btn("🔍 Diagnose PSF...");
            b.Click += (s, e) => PsfDiagnoseDlg();
            bar.Controls.Add(b);
        }

        private void PsfDiagnoseDlg()
        {
            string path;
            using (var od = new OpenFileDialog
            {
                Title = "Choose the PSF to scan",
                Filter = "PSF files|*.psf;*.minipsf;*.psflib;*.psf1|All files|*.*"
            })
            {
                if (od.ShowDialog(this) != DialogResult.OK) return;
                path = od.FileName;
            }
            string text;
            try { text = PdgReport(path); }
            catch (Exception ex) { text = "The scan itself failed:\r\n" + ex; }
            PdgShow(path, text);
        }

        private void PdgShow(string path, string text)
        {
            using (var f = new Form
            {
                Text = "PSF scan - " + System.IO.Path.GetFileName(path),
                Width = 1000,
                Height = 720,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Ui.Bg,
                ForeColor = Color.White
            })
            {
                var tb = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Dock = DockStyle.Fill,
                    Font = new Font("Consolas", 9f),
                    BackColor = Ui.Edit,
                    ForeColor = Color.White,
                    MaxLength = int.MaxValue,
                    Text = text
                };
                var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, BackColor = Ui.Bg };
                var bCopy = Ui.Btn("📋 Copy all");
                bCopy.Click += (s, e) => { try { Clipboard.SetText(text); } catch { } };
                var bSave = Ui.Btn("💾 Save as .txt...");
                bSave.Click += (s, e) =>
                {
                    using (var sd = new SaveFileDialog { Filter = "Text|*.txt", FileName = System.IO.Path.GetFileNameWithoutExtension(path) + "_scan.txt" })
                        if (sd.ShowDialog(f) == DialogResult.OK) File.WriteAllText(sd.FileName, text, Encoding.UTF8);
                };
                bar.Controls.Add(bCopy);
                bar.Controls.Add(bSave);
                f.Controls.Add(tb);      // Fill first, then the bar (Top)
                f.Controls.Add(bar);
                f.Shown += (s, e) => tb.Select(0, 0);
                f.ShowDialog(this);
            }
        }

        // ------------------------------------------------------------------ report
        private static string PdgReport(string path)
        {
            var sb = new StringBuilder();
            byte[] f = File.ReadAllBytes(path);
            sb.AppendLine("File: " + path);
            sb.AppendLine("Size: " + f.Length.ToString("N0") + " bytes");
            sb.AppendLine();
            if (f.Length < 16) { sb.AppendLine("Too small to be a PSF."); return sb.ToString(); }

            string magic = Encoding.ASCII.GetString(f, 0, 3);
            int ver = f[3];
            uint res = BitConverter.ToUInt32(f, 4);
            uint prog = BitConverter.ToUInt32(f, 8);
            uint crc = BitConverter.ToUInt32(f, 12);
            string verName = ver == 1 ? "PlayStation 1" : ver == 2 ? "PlayStation 2" : ver == 0x11 ? "Saturn" : ver == 0x12 ? "Dreamcast" : "other";
            sb.AppendLine("=== Header ===");
            sb.AppendLine("Magic: " + magic + (magic == "PSF" ? "" : "   <-- NOT 'PSF'"));
            sb.AppendLine("Version byte: 0x" + ver.ToString("X2") + " (" + verName + ")");
            sb.AppendLine("Reserved area size: " + res + " bytes");
            sb.AppendLine("Compressed program size: " + prog + " bytes (0x" + prog.ToString("X") + ")");
            sb.AppendLine("Stored CRC32: 0x" + crc.ToString("X8"));

            long progOff = 16L + res;
            long progLen = prog;
            if (progOff > f.Length) { sb.AppendLine("The reserved area runs past the end of the file."); return sb.ToString(); }
            if (progOff + progLen > f.Length)
            {
                sb.AppendLine("*** The file is TRUNCATED: the program section should be " + progLen + " bytes but only " + (f.Length - progOff) + " are there.");
                progLen = f.Length - progOff;
            }
            uint real = PdgCrc((byte[])f, (int)progOff, (int)progLen);
            sb.AppendLine("Real CRC32 of the program section: 0x" + real.ToString("X8") + (real == crc ? "  (matches)" : "  (MISMATCH - the file is damaged or edited; most players ignore this)"));
            if (progLen >= 2) sb.AppendLine("First 2 bytes of the program (zlib header): " + PdgHex(f, (int)progOff, 2));
            sb.AppendLine();

            // ---- tags
            long tagOff = progOff + prog;
            sb.AppendLine("=== Tags ===");
            if (tagOff + 5 <= f.Length && Encoding.ASCII.GetString(f, (int)tagOff, 5) == "[TAG]")
            {
                string tags = Encoding.UTF8.GetString(f, (int)tagOff + 5, (int)(f.Length - tagOff - 5));
                sb.AppendLine(tags.Replace("\r", "").TrimEnd('\n'));
                if (tags.IndexOf("_lib", StringComparison.OrdinalIgnoreCase) >= 0)
                    sb.AppendLine(">> This PSF depends on a library file (_lib). Keep the .psflib in the same folder.");
                else
                    sb.AppendLine(">> No _lib tag: the PSF is standalone.");
            }
            else sb.AppendLine("(no [TAG] block after the program section)");
            sb.AppendLine();

            // ---- inflate
            sb.AppendLine("=== Decompressed program ===");
            string err;
            byte[] d = PdgInflate(f, (int)progOff, (int)progLen, out err);
            sb.AppendLine("Decompressed size: " + d.Length.ToString("N0") + " bytes (0x" + d.Length.ToString("X") + ")");
            if (err != null) sb.AppendLine("*** zlib stopped with an error after that many bytes: " + err);
            if (d.Length == 0) { sb.AppendLine("Nothing to scan."); }
            else
            {
                uint textAddr = 0;
                bool exe = d.Length >= 0x800 && Encoding.ASCII.GetString(d, 0, 8) == "PS-X EXE";
                if (exe)
                {
                    textAddr = BitConverter.ToUInt32(d, 0x18);
                    uint textSize = BitConverter.ToUInt32(d, 0x1C);
                    sb.AppendLine("PS-X EXE header: yes");
                    sb.AppendLine("  Start PC: 0x" + BitConverter.ToUInt32(d, 0x10).ToString("X8"));
                    sb.AppendLine("  Load address: 0x" + textAddr.ToString("X8") + ", text size: 0x" + textSize.ToString("X") + " (" + textSize + ")");
                    sb.AppendLine("  Stack: 0x" + BitConverter.ToUInt32(d, 0x30).ToString("X8"));
                    long expect = 0x800L + textSize;
                    sb.AppendLine("  Header says " + expect + " bytes in total, the data has " + d.Length + (expect == d.Length ? "  (matches)" : "  (DIFFERENT)"));
                }
                else sb.AppendLine("PS-X EXE header: NO. First 16 bytes: " + PdgHex(d, 0, 16));
                sb.AppendLine();

                // ---- signatures
                sb.AppendLine("=== Signatures found in the data ===");
                foreach (string sig in new[] { "AKAO", "pBAV", "pQES", "VAGp" })
                {
                    var hits = PdgFind(d, sig);
                    sb.AppendLine(sig + ": " + hits.Count + " hit(s)");
                    int shown = 0;
                    foreach (int o in hits)
                    {
                        if (shown++ >= 40) { sb.AppendLine("    ..."); break; }
                        string ram = exe ? "  RAM 0x" + ((long)textAddr + o - 0x800).ToString("X8") : "";
                        sb.AppendLine("    file+0x" + o.ToString("X6") + ram + "   " + PdgHex(d, o, 16));
                    }
                }
                sb.AppendLine();

                // ---- where is the data
                sb.AppendLine("=== Non-zero data per 64 KB block ===");
                int last = -1;
                for (int i = d.Length - 1; i >= 0; i--) if (d[i] != 0) { last = i; break; }
                sb.AppendLine("Last non-zero byte: file+0x" + (last < 0 ? 0 : last).ToString("X"));
                for (int p = 0; p < d.Length; p += 0x10000)
                {
                    int n = Math.Min(0x10000, d.Length - p), nz = 0;
                    for (int i = 0; i < n; i++) if (d[p + i] != 0) nz++;
                    sb.AppendLine("  file+0x" + p.ToString("X6") + ": " + (nz * 100 / Math.Max(1, n)) + "% filled");
                }
            }
            sb.AppendLine();

            // ---- the app's own loader, called by name so this tool cannot break the build
            sb.AppendLine("=== What the app's own PsfLoader returns for this file ===");
            try
            {
                var lt = typeof(MainForm).Assembly.GetTypes().FirstOrDefault(t => t.Name == "PsfLoader");
                if (lt == null) sb.AppendLine("(no class named PsfLoader was found)");
                else
                {
                    var m = lt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        .FirstOrDefault(x => x.Name == "Load" && x.GetParameters().Length > 0 && x.GetParameters()[0].ParameterType == typeof(string));
                    if (m == null) sb.AppendLine("(PsfLoader has no Load(string, ...) method)");
                    else
                    {
                        var ps = m.GetParameters();
                        var args = new object[ps.Length];
                        args[0] = path;
                        for (int i = 1; i < ps.Length; i++)
                            args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                        object r = m.Invoke(null, args);
                        PdgDump(sb, r, 0);
                    }
                }
            }
            catch (TargetInvocationException tie)
            {
                var ie = tie.InnerException ?? tie;
                sb.AppendLine("*** The loader THREW an error: " + ie.GetType().Name + ": " + ie.Message);
                var lines = (ie.StackTrace ?? "").Split('\n');
                for (int i = 0; i < lines.Length && i < 6; i++) sb.AppendLine("    " + lines[i].Trim());
            }
            catch (Exception ex) { sb.AppendLine("(could not call the loader: " + ex.Message + ")"); }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ helpers (all prefixed Pdg)
        private static uint PdgCrc(byte[] d, int o, int n)
        {
            uint c = 0xFFFFFFFF;
            for (int i = 0; i < n; i++)
            {
                c ^= d[o + i];
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            }
            return ~c;
        }

        private static byte[] PdgInflate(byte[] src, int off, int len, out string err)
        {
            err = null;
            var ms = new MemoryStream();
            try
            {
                using (var z = new ZLibStream(new MemoryStream(src, off, len), CompressionMode.Decompress))
                {
                    var buf = new byte[65536];
                    int n;
                    while ((n = z.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                }
            }
            catch (Exception ex) { err = ex.Message; }
            return ms.ToArray();
        }

        private static string PdgHex(byte[] d, int o, int n)
        {
            n = Math.Min(n, d.Length - o);
            if (n <= 0) return "";
            return BitConverter.ToString(d, o, n).Replace('-', ' ');
        }

        private static List<int> PdgFind(byte[] d, string sig)
        {
            var l = new List<int>();
            var p = Encoding.ASCII.GetBytes(sig);
            for (int i = 0; i + p.Length <= d.Length; i++)
            {
                bool ok = true;
                for (int k = 0; k < p.Length; k++) if (d[i + k] != p[k]) { ok = false; break; }
                if (ok) l.Add(i);
            }
            return l;
        }

        private static void PdgDump(StringBuilder sb, object o, int depth)
        {
            string pad = new string(' ', depth * 2);
            if (o == null) { sb.AppendLine(pad + "(null)"); return; }
            var t = o.GetType();
            sb.AppendLine(pad + "Type: " + t.FullName);
            const BindingFlags bf = BindingFlags.Public | BindingFlags.Instance;
            foreach (var p in t.GetProperties(bf))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                object v;
                try { v = p.GetValue(o); } catch (Exception ex) { v = "(error: " + ex.Message + ")"; }
                PdgLine(sb, pad + "  ", p.Name, v, depth);
            }
            foreach (var fl in t.GetFields(bf))
            {
                object v;
                try { v = fl.GetValue(o); } catch (Exception ex) { v = "(error: " + ex.Message + ")"; }
                PdgLine(sb, pad + "  ", fl.Name, v, depth);
            }
        }

        private static void PdgLine(StringBuilder sb, string pad, string name, object v, int depth)
        {
            if (v == null) { sb.AppendLine(pad + name + " = null"); return; }
            if (v is byte[] bb) { sb.AppendLine(pad + name + " = byte[" + bb.Length + "]"); return; }
            if (v is string s) { sb.AppendLine(pad + name + " = \"" + (s.Length > 100 ? s.Substring(0, 100) + "..." : s) + "\""); return; }
            var t = v.GetType();
            if (t.IsPrimitive || v is Enum || v is decimal) { sb.AppendLine(pad + name + " = " + v); return; }
            if (v is IEnumerable en)
            {
                int n = 0; string et = "";
                foreach (var x in en)
                {
                    if (n == 0 && x != null) et = x.GetType().Name;
                    if (++n >= 1000000) break;
                }
                sb.AppendLine(pad + name + " = " + n + " item(s)" + (et.Length > 0 ? " of " + et : ""));
                return;
            }
            sb.AppendLine(pad + name + " = (" + t.Name + ")");
            if (depth < 1 && t.Namespace != null && t.Namespace.StartsWith("PS2AudioStudio", StringComparison.Ordinal))
                PdgDump(sb, v, depth + 2);
        }
    }
}
