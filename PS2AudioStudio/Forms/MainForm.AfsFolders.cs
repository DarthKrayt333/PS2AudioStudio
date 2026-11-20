using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using PS2AudioStudio.Archives;

namespace PS2AudioStudio
{
    // AFS tab: folder tree on the left, Explorer-like list on the right (folders first, then the files of that folder)
    public partial class MainForm
    {
        private TreeView tvAfs;
        private CheckBox chkAfsSub;                 // kept only so older code compiles; never shown, always false
        private SplitContainer splitAfsTree;
        private string afsFolder = "";              // "" = top level, otherwise e.g. "sound/ok_adx"
        private bool _afsTreeBusy;
        private Label lblAfsPath;
        private ContextMenuStrip _afsFolderMenu;
        private string _afsMenuFolder = "";
        private bool _afsMenuSwapped;
        private ContextMenuStrip _afsListMenuSaved;
        private readonly HashSet<string> _afsVirtual = new HashSet<string>(StringComparer.Ordinal);   // new, still empty folders
        private object _afsVirtualOwner;
        private readonly Dictionary<string, TreeNode> _afsNodes = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
        private const int AfsNameMax = 32;

        // ================================================================= row / panel helper classes
        private sealed class AfsFolderRow : ListViewItem
        {
            public string Folder = "";
            public bool IsUp;
        }

        /// <summary>Holds the list. It refuses the selection of folder rows, so folder rows can never be treated as archive entries.</summary>
        private sealed class AfsVetoPanel : Panel
        {
            public ListView Target;

            [StructLayout(LayoutKind.Sequential)]
            private struct NMHDR { public IntPtr hwndFrom; public IntPtr idFrom; public int code; }

            [StructLayout(LayoutKind.Sequential)]
            private struct NMLISTVIEW
            {
                public NMHDR hdr; public int iItem; public int iSubItem;
                public uint uNewState; public uint uOldState; public uint uChanged;
                public int ptX; public int ptY; public IntPtr lParam;
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x004E && Target != null)          // WM_NOTIFY
                {
                    try
                    {
                        var h = Marshal.PtrToStructure<NMHDR>(m.LParam);
                        if (h.code == -100 && h.hwndFrom == Target.Handle)      // LVN_ITEMCHANGING
                        {
                            var nm = Marshal.PtrToStructure<NMLISTVIEW>(m.LParam);
                            if ((nm.uChanged & 8) != 0 && (nm.uNewState & 2) != 0 && (nm.uOldState & 2) == 0 &&
                                nm.iItem >= 0 && nm.iItem < Target.Items.Count && Target.Items[nm.iItem] is AfsFolderRow)
                            {
                                m.Result = (IntPtr)1;           // veto: folder rows cannot be selected
                                return;
                            }
                        }
                    }
                    catch { }
                }
                base.WndProc(ref m);
            }
        }

        // ================================================================= names
        private static string AfsNorm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\\', '/');
            return string.Join("/", s.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
        }
        private static string AfsDir(string name)
        {
            name = AfsNorm(name);
            int k = name.LastIndexOf('/');
            return k < 0 ? "" : name.Substring(0, k);
        }
        private static string AfsLeaf(string name)
        {
            name = AfsNorm(name);
            int k = name.LastIndexOf('/');
            return k < 0 ? name : name.Substring(k + 1);
        }
        private static string AfsJoin(string folder, string leaf)
        {
            folder = AfsNorm(folder);
            return folder.Length == 0 ? AfsNorm(leaf) : folder + "/" + AfsNorm(leaf);
        }
        private static bool AfsInFolder(string name, string folder, bool sub)
        {
            name = AfsNorm(name); folder = AfsNorm(folder);
            string dir = AfsDir(name);
            if (folder.Length == 0) return sub || dir.Length == 0;
            if (dir == folder) return true;
            return sub && dir.StartsWith(folder + "/", StringComparison.Ordinal);
        }
        /// <summary>The archive name for a new file: the folder that is open + the file name.</summary>
        private string AfsAddName(string leaf)
        {
            return AfsJoin(afsFolder, AfsLeaf(leaf));
        }
        private string AfsName(int i)
        {
            return AfsNorm(afs.Entries[i].FileName ?? "");
        }
        private long AfsSizeOf(int i)
        {
            var e = afs.Entries[i];
            return e.Data != null ? e.Data.Length : Convert.ToInt64(e.Size);
        }
        private List<int> AfsFolderEntries(string folder, bool sub)
        {
            var l = new List<int>();
            if (afs == null) return l;
            for (int i = 0; i < afs.Entries.Count; i++)
                if (AfsInFolder(AfsName(i), folder, sub)) l.Add(i);
            return l;
        }
        private List<string> AfsAllFolders()
        {
            var set = new HashSet<string>(StringComparer.Ordinal) { "" };
            if (afs != null)
                for (int i = 0; i < afs.Entries.Count; i++)
                {
                    string d = AfsDir(AfsName(i));
                    while (d.Length > 0) { set.Add(d); d = AfsDir(d); }
                }
            foreach (string v in _afsVirtual)
            {
                string d = v;
                while (d.Length > 0) { set.Add(d); d = AfsDir(d); }
            }
            var l = set.ToList();
            l.Sort(AfsNatural);
            return l;
        }
        private static int AfsNatural(string a, string b)
        {
            a = a ?? ""; b = b ?? "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string x = a.Substring(si, i - si).TrimStart('0'), y = b.Substring(sj, j - sj).TrimStart('0');
                    if (x.Length != y.Length) return x.Length < y.Length ? -1 : 1;
                    int c = string.CompareOrdinal(x, y);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
        private static uint AfsBe32(byte[] d, int o)
        {
            return ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
        }
        private string AfsInfoText(int i)
        {
            var e = afs.Entries[i];
            var d = e.Data;
            string s = "";
            if (d != null && d.Length >= 20)
            {
                if (d[0] == 0x80 && d[1] == 0x00) s = "ADX, " + AfsBe32(d, 8) + " Hz, " + d[7] + " ch";
                else if (d[0] == 'V' && d[1] == 'A' && d[2] == 'G' && d[3] == 'p') s = "VAG, " + AfsBe32(d, 16) + " Hz";
                else if (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F') s = "WAV";
                else if (d[0] == 0 && d[1] == 0 && d[2] == 1 && d[3] == 0xBA) s = "MPEG / SFD video";
            }
            if (e.Modified) s = s.Length > 0 ? s + ", modified" : "modified";
            return s;
        }
        private void AfsMarkDirty()
        {
            try
            {
                var t = typeof(AfsArchive);
                var p = t.GetProperty("Modified");
                if (p != null && p.CanWrite) { p.SetValue(afs, true); return; }
                var f = t.GetField("Modified");
                if (f != null) f.SetValue(afs, true);
            }
            catch { }
        }
        private void AfsCheckVirtualOwner()
        {
            if (!ReferenceEquals(_afsVirtualOwner, afs)) { _afsVirtual.Clear(); _afsVirtualOwner = afs; }
        }

        // ================================================================= layout
        private Control BuildAfsFolderPane(ListView lv)
        {
            chkAfsSub = new CheckBox { Checked = false, Visible = false };

            tvAfs = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                BackColor = Ui.Edit,
                ForeColor = Color.White
            };
            tvAfs.AfterSelect += (s, e) =>
            {
                if (_afsTreeBusy || e.Node == null) return;
                afsFolder = (string)(e.Node.Tag ?? "");
                FillAfsList();
            };
            tvAfs.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var n = tvAfs.GetNodeAt(e.Location);
                if (n != null) tvAfs.SelectedNode = n;
            };

            _afsFolderMenu = BuildAfsFolderMenu();
            tvAfs.ContextMenuStrip = _afsFolderMenu;

            splitAfsTree = new SplitContainer { Dock = DockStyle.Fill, BackColor = Ui.Bg };
            splitAfsTree.Panel1.Controls.Add(tvAfs);

            var host = new AfsVetoPanel { Dock = DockStyle.Fill, Target = lv, BackColor = Ui.Bg };
            lv.Dock = DockStyle.Fill;
            host.Controls.Add(lv);                       // the list first (Fill), then the bar (Top)

            var bar = Ui.Bar1(36);
            bar.Dock = DockStyle.Top;
            var bUp = Ui.Btn("⬆ Up"); bUp.Click += (s, e) => AfsGoUp();
            var bNew = Ui.Btn("📁 New folder..."); bNew.Click += (s, e) => AfsNewFolder(afsFolder);
            var bAdd = Ui.Btn("➕ Add files here..."); bAdd.Click += (s, e) => AfsAddFilesTo(afsFolder);
            var bExt = Ui.Btn("📤 Extract this folder..."); bExt.Click += (s, e) => ExtractAfsFolder(afsFolder);
            lblAfsPath = new Label { AutoSize = true, ForeColor = Color.LightSkyBlue, Margin = new Padding(10, 10, 0, 0), Text = "/" };
            bar.Controls.AddRange(new Control[] { bUp, bNew, bAdd, bExt, lblAfsPath });
            host.Controls.Add(bar);
            splitAfsTree.Panel2.Controls.Add(host);

            lv.MouseDoubleClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var row = lv.HitTest(e.Location).Item as AfsFolderRow;
                if (row != null) AfsGoTo(row.Folder);
            };
            lv.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Back) { AfsGoUp(); e.Handled = true; }
            };
            lv.MouseDown += (s, e) =>
            {
                RestoreAfsListMenu();
                if (e.Button != MouseButtons.Right) return;
                var row = lv.HitTest(e.Location).Item as AfsFolderRow;
                if (row != null && !row.IsUp)
                {
                    _afsMenuFolder = row.Folder;
                    _afsListMenuSaved = lv.ContextMenuStrip;
                    lv.ContextMenuStrip = _afsFolderMenu;
                    _afsMenuSwapped = true;
                }
            };
            _afsFolderMenu.Closed += (s, e) => RestoreAfsListMenu();
            return splitAfsTree;
        }

        private void RestoreAfsListMenu()
        {
            if (!_afsMenuSwapped) return;
            if (lvAfs != null) lvAfs.ContextMenuStrip = _afsListMenuSaved;
            _afsListMenuSaved = null;
            _afsMenuSwapped = false;
        }

        private void InitAfsTreeSplit()
        {
            try
            {
                if (splitAfsTree == null) return;
                splitAfsTree.SplitterDistance = Math.Max(120, Math.Min(300, splitAfsTree.Width / 4));
            }
            catch { }
        }

        private ContextMenuStrip BuildAfsFolderMenu()
        {
            var m = new ContextMenuStrip { BackColor = Ui.Panel, ForeColor = Color.White };
            m.Items.Add("📂 Open this folder", null, (s, e) => AfsGoTo(_afsMenuFolder));
            m.Items.Add("📁 New folder inside...", null, (s, e) => AfsNewFolder(_afsMenuFolder));
            m.Items.Add("➕ Add file(s) to this folder...", null, (s, e) => AfsAddFilesTo(_afsMenuFolder));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("📤 Extract this folder...", null, (s, e) => ExtractAfsFolder(_afsMenuFolder));
            m.Items.Add("✏ Rename / move this folder...", null, (s, e) => RenameAfsFolder(_afsMenuFolder));
            m.Items.Add("🗑 Remove this folder and its files", null, (s, e) => RemoveAfsFolder(_afsMenuFolder));
            m.Opening += (s, e) =>
            {
                if (m.SourceControl == tvAfs)
                {
                    var n = tvAfs.SelectedNode;
                    _afsMenuFolder = n == null ? "" : (string)(n.Tag ?? "");
                }
            };
            return m;
        }

        // ================================================================= tree
        private void RebuildAfsTree()
        {
            if (tvAfs == null) return;
            AfsCheckVirtualOwner();
            _afsTreeBusy = true;
            tvAfs.BeginUpdate();
            try
            {
                tvAfs.Nodes.Clear();
                _afsNodes.Clear();
                if (afs == null) return;
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < afs.Entries.Count; i++)
                {
                    string d = AfsDir(AfsName(i));
                    while (d.Length > 0)
                    {
                        int c; counts.TryGetValue(d, out c); counts[d] = c + 1;
                        d = AfsDir(d);
                    }
                }
                foreach (string v in _afsVirtual)
                {
                    string d = v;
                    while (d.Length > 0) { if (!counts.ContainsKey(d)) counts[d] = 0; d = AfsDir(d); }
                }
                string rootName = string.IsNullOrEmpty(afs.Path) ? "new AFS" : System.IO.Path.GetFileName(afs.Path);
                var root = new TreeNode("📦 " + rootName + " (" + afs.Entries.Count + ")") { Tag = "" };
                tvAfs.Nodes.Add(root);
                _afsNodes[""] = root;
                var keys = counts.Keys.ToList();
                keys.Sort(AfsNatural);
                foreach (string k in keys) AfsTreeNode(k, counts);
                if (_afsNodes.Count <= 250) tvAfs.ExpandAll(); else root.Expand();
                string want = AfsNorm(afsFolder);
                TreeNode sel;
                if (!_afsNodes.TryGetValue(want, out sel)) { want = ""; sel = root; }
                afsFolder = want;
                tvAfs.SelectedNode = sel;
                sel.EnsureVisible();
            }
            finally
            {
                tvAfs.EndUpdate();
                _afsTreeBusy = false;
            }
        }

        private TreeNode AfsTreeNode(string dir, Dictionary<string, int> counts)
        {
            TreeNode n;
            if (_afsNodes.TryGetValue(dir, out n)) return n;
            TreeNode parent = AfsTreeNode(AfsDir(dir), counts);
            int c;
            counts.TryGetValue(dir, out c);
            n = new TreeNode("📁 " + AfsLeaf(dir) + " (" + c + ")") { Tag = dir };
            parent.Nodes.Add(n);
            _afsNodes[dir] = n;
            return n;
        }

        private void AfsGoTo(string folder)
        {
            afsFolder = AfsNorm(folder);
            TreeNode n;
            if (tvAfs != null && _afsNodes.TryGetValue(afsFolder, out n))
            {
                _afsTreeBusy = true;
                try { tvAfs.SelectedNode = n; n.EnsureVisible(); }
                finally { _afsTreeBusy = false; }
            }
            FillAfsList();
        }
        private void AfsGoUp()
        {
            string cur = AfsNorm(afsFolder);
            if (cur.Length > 0) AfsGoTo(AfsDir(cur));
        }

        // ================================================================= list
        private void FillAfsList()
        {
            var lv = lvAfs;
            if (lv == null) return;
            string cur = AfsNorm(afsFolder);
            lv.BeginUpdate();
            try
            {
                lv.Items.Clear();
                if (afs != null)
                {
                    if (cur.Length > 0) lv.Items.Add(MakeAfsFolderRow(AfsDir(cur), ".. (up)", true, 0, 0));
                    string prefix = cur.Length == 0 ? "" : cur + "/";
                    var kids = new Dictionary<string, long[]>(StringComparer.Ordinal);     // child folder -> { files, bytes }
                    var files = new List<int>();
                    for (int i = 0; i < afs.Entries.Count; i++)
                    {
                        string n = AfsName(i);
                        if (prefix.Length > 0 && !n.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        string rel = n.Substring(prefix.Length);
                        int k = rel.IndexOf('/');
                        if (k < 0) { files.Add(i); continue; }
                        string child = rel.Substring(0, k);
                        long[] c;
                        if (!kids.TryGetValue(child, out c)) { c = new long[2]; kids[child] = c; }
                        c[0]++; c[1] += AfsSizeOf(i);
                    }
                    foreach (string v in _afsVirtual)
                    {
                        if (prefix.Length > 0 && !v.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        string rel = v.Substring(prefix.Length);
                        if (rel.Length == 0) continue;
                        int k = rel.IndexOf('/');
                        string child = k < 0 ? rel : rel.Substring(0, k);
                        if (!kids.ContainsKey(child)) kids[child] = new long[2];
                    }
                    var names = kids.Keys.ToList();
                    names.Sort(AfsNatural);
                    foreach (string nm in names)
                        lv.Items.Add(MakeAfsFolderRow(prefix + nm, nm, false, kids[nm][0], kids[nm][1]));
                    foreach (int i in files) lv.Items.Add(MakeAfsFileRow(i));
                }
            }
            finally { lv.EndUpdate(); }
            if (lblAfsPath != null) lblAfsPath.Text = "/" + cur;
        }

        private AfsFolderRow MakeAfsFolderRow(string path, string label, bool up, long files, long bytes)
        {
            var r = new AfsFolderRow { Folder = path, IsUp = up };
            r.Text = "";
            r.SubItems.Add("📁 " + label);
            r.SubItems.Add("");
            r.SubItems.Add(up ? "" : bytes.ToString("N0"));
            r.SubItems.Add("Folder");
            r.SubItems.Add(up ? "go up" : files.ToString("N0") + " file(s)");
            r.Tag = -1;
            r.ForeColor = Color.Gold;
            return r;
        }

        private ListViewItem MakeAfsFileRow(int i)
        {
            var e = afs.Entries[i];
            string leaf = AfsLeaf(AfsName(i));
            if (leaf.Length == 0) leaf = "(no name)";
            var li = new ListViewItem(new[]
            {
                i.ToString(),
                leaf,
                "0x" + e.Offset.ToString("X8"),
                AfsSizeOf(i).ToString("N0"),
                Convert.ToString(e.Kind),
                AfsInfoText(i)
            });
            li.Tag = i;
            return li;
        }

        // ================================================================= small input dialog
        private string AfsAskText(string title, string label, string initial, IEnumerable<string> choices = null)
        {
            using (var f = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(520, 135),
                BackColor = Ui.Bg,
                ForeColor = Color.White
            })
            {
                var lb = new Label { Left = 12, Top = 12, Width = 496, Height = 38, Text = label };
                Control input;
                if (choices != null)
                {
                    var cb = new ComboBox { Left = 12, Top = 54, Width = 496, DropDownStyle = ComboBoxStyle.DropDown, BackColor = Ui.Edit, ForeColor = Color.White };
                    foreach (var c in choices) cb.Items.Add(c);
                    cb.Text = initial ?? "";
                    input = cb;
                }
                else
                {
                    input = new TextBox { Left = 12, Top = 54, Width = 496, Text = initial ?? "", BackColor = Ui.Edit, ForeColor = Color.White };
                }
                var ok = Ui.Btn("OK", 80); ok.DialogResult = DialogResult.OK; ok.Left = 340; ok.Top = 92;
                var cancel = Ui.Btn("Cancel", 80); cancel.DialogResult = DialogResult.Cancel; cancel.Left = 428; cancel.Top = 92;
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Controls.AddRange(new Control[] { lb, input, ok, cancel });
                return f.ShowDialog(this) == DialogResult.OK ? input.Text : null;
            }
        }

        // ================================================================= folder operations
        private void AfsNewFolder(string parent)
        {
            if (afs == null) { MessageBox.Show(this, "Open or create an AFS first.", "New folder"); return; }
            parent = AfsNorm(parent);
            string name = AfsAskText("New folder", "Name of the new folder inside \"/" + parent + "\"\n(type a/b to make a sub folder too):", "new_folder");
            if (name == null) return;
            name = AfsNorm(name);
            if (name.Length == 0) return;
            foreach (char ch in name)
                if (ch < 32 || ":*?\"<>|".IndexOf(ch) >= 0)
                {
                    MessageBox.Show(this, "The name has a character that is not allowed: " + ch, "New folder");
                    return;
                }
            string full = AfsJoin(parent, name);
            if (Encoding.Latin1.GetByteCount(full) + 2 > AfsNameMax)
            {
                MessageBox.Show(this, "This path is too long. An AFS name (folder + file name together) is at most " + AfsNameMax +
                    " bytes, so no file could fit in a folder named\n\n" + full, "New folder");
                return;
            }
            AfsCheckVirtualOwner();
            _afsVirtual.Add(full);
            afsFolder = full;
            RebuildAfsTree();
            FillAfsList();
            SetStatus("Folder /" + full + " created. An AFS cannot store an empty folder: it stays until you close the archive. Put files in it with 'Add files here' or select entries and use 'Move to folder...'.");
        }

        private void AfsAddFilesTo(string folder)
        {
            if (afs == null) { MessageBox.Show(this, "Open or create an AFS first.", "Add files"); return; }
            folder = AfsNorm(folder);
            using (var od = new OpenFileDialog { Multiselect = true, Title = "Add file(s) to /" + folder, Filter = "All files|*.*" })
            {
                if (od.ShowDialog(this) != DialogResult.OK) return;
                int added = 0;
                var tooLong = new List<string>();
                foreach (string f in od.FileNames)
                {
                    string name = AfsJoin(folder, Path.GetFileName(f));
                    if (Encoding.Latin1.GetByteCount(name) > AfsNameMax) { tooLong.Add(name); continue; }
                    try { afs.Add(name, File.ReadAllBytes(f)); added++; }
                    catch (Exception ex) { MessageBox.Show(this, f + "\n\n" + ex.Message, "Add files"); }
                }
                afsFolder = folder;
                PopulateAfs();
                if (tooLong.Count > 0)
                    MessageBox.Show(this, tooLong.Count + " file(s) were NOT added: the whole path may be at most " + AfsNameMax + " bytes.\n\n" +
                        string.Join("\n", tooLong.Take(10)), "Add files");
                SetStatus("Added " + added + " file(s) to /" + folder + ".");
            }
        }

        private static string AfsSafeSegment(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private void ExtractAfsFolder(string folder)
        {
            if (afs == null) return;
            folder = AfsNorm(folder);
            var idx = AfsFolderEntries(folder, true);
            if (idx.Count == 0) { MessageBox.Show(this, "There are no files in this folder.", "Extract folder"); return; }
            using (var fb = new FolderBrowserDialog { Description = "Extract " + idx.Count + " file(s) of /" + folder + " here (the folders are re-created)", UseDescriptionForTitle = true })
            {
                if (fb.ShowDialog(this) != DialogResult.OK) return;
                string outRoot = folder.Length == 0 ? fb.SelectedPath : Path.Combine(fb.SelectedPath, AfsSafeSegment(AfsLeaf(folder)));
                string prefix = folder.Length == 0 ? "" : folder + "/";
                int ok = 0, bad = 0;
                foreach (int i in idx)
                {
                    try
                    {
                        var data = afs.Entries[i].Data;
                        if (data == null) { bad++; continue; }
                        string rel = AfsName(i).Substring(prefix.Length);
                        string path = outRoot;
                        var parts = rel.Split('/').Where(p => p.Length > 0 && p != "." && p != "..").ToList();
                        if (parts.Count == 0) parts.Add("entry_" + i);
                        foreach (string p in parts) path = Path.Combine(path, AfsSafeSegment(p));
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllBytes(path, data);
                        ok++;
                    }
                    catch { bad++; }
                }
                SetStatus("Extracted " + ok + " file(s) to " + outRoot + (bad > 0 ? " (" + bad + " failed)." : "."));
            }
        }

        /// <summary>Changes the names of the given entries. Nothing is changed if any new name is empty or over 32 bytes.</summary>
        private bool AfsApplyNames(List<int> idxs, List<string> names, string title)
        {
            for (int k = 0; k < idxs.Count; k++)
            {
                string n = names[k];
                if (n.Length == 0)
                {
                    MessageBox.Show(this, "A name would become empty. Nothing was changed.", title);
                    return false;
                }
                if (Encoding.Latin1.GetByteCount(n) > AfsNameMax)
                {
                    MessageBox.Show(this, "This name would be longer than " + AfsNameMax + " bytes (the AFS name field):\n\n" + n +
                        "\n\nNothing was changed. Use a shorter folder name.", title);
                    return false;
                }
            }
            for (int k = 0; k < idxs.Count; k++) afs.Entries[idxs[k]].FileName = names[k];
            AfsMarkDirty();
            return true;
        }

        private void RenameAfsFolder(string folder)
        {
            if (afs == null) return;
            folder = AfsNorm(folder);
            if (folder.Length == 0) { MessageBox.Show(this, "The top level cannot be renamed.", "Rename folder"); return; }
            string nw = AfsAskText("Rename / move folder", "New path of the folder /" + folder + "\n(empty = move its files to the top level):", folder, AfsAllFolders());
            if (nw == null) return;
            nw = AfsNorm(nw);
            if (nw == folder) return;
            if ((nw + "/").StartsWith(folder + "/", StringComparison.Ordinal))
            {
                MessageBox.Show(this, "A folder cannot be moved into itself.", "Rename folder");
                return;
            }
            var idx = AfsFolderEntries(folder, true);
            var names = new List<string>();
            foreach (int i in idx) names.Add(AfsJoin(nw, AfsName(i).Substring(folder.Length + 1)));
            if (idx.Count > 0 && !AfsApplyNames(idx, names, "Rename folder")) return;
            var vs = _afsVirtual.Where(v => v == folder || v.StartsWith(folder + "/", StringComparison.Ordinal)).ToList();
            foreach (string v in vs) _afsVirtual.Remove(v);
            if (nw.Length > 0) foreach (string v in vs) _afsVirtual.Add(nw + v.Substring(folder.Length));
            afsFolder = nw;
            PopulateAfs();
            SetStatus("Folder /" + folder + " is now /" + nw + " (" + idx.Count + " file(s)).");
        }

        /// <summary>Moves the selected list entries into a folder (an existing one, or a new one that is typed in).</summary>
        private void MoveAfsToFolder()
        {
            if (afs == null) return;
            var sel = new List<int>();
            foreach (int i in SelAfsAll()) sel.Add(i);
            if (sel.Count == 0) { SetStatus("Select one or more files in the list first."); return; }
            string dest = AfsAskText("Move to folder", "Destination folder for " + sel.Count + " file(s)\n(empty = top level; type a new name to make a new folder):", afsFolder, AfsAllFolders());
            if (dest == null) return;
            dest = AfsNorm(dest);
            var names = new List<string>();
            foreach (int i in sel) names.Add(AfsJoin(dest, AfsLeaf(AfsName(i))));
            if (!AfsApplyNames(sel, names, "Move to folder")) return;
            afsFolder = dest;
            PopulateAfs();
            SetStatus(sel.Count + " file(s) moved to /" + dest + ".");
        }

        private void RemoveAfsFolder(string folder)
        {
            if (afs == null) return;
            folder = AfsNorm(folder);
            if (folder.Length == 0) { MessageBox.Show(this, "The top level cannot be removed.", "Remove folder"); return; }
            var idx = AfsFolderEntries(folder, true);
            if (MessageBox.Show(this, "Remove the folder /" + folder + " and the " + idx.Count + " file(s) in it from the archive?",
                    "Remove folder", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            foreach (int i in idx.OrderByDescending(x => x)) afs.RemoveAt(i);
            foreach (string v in _afsVirtual.Where(v => v == folder || v.StartsWith(folder + "/", StringComparison.Ordinal)).ToList())
                _afsVirtual.Remove(v);
            afsFolder = AfsDir(folder);
            PopulateAfs();
            SetStatus("Removed /" + folder + " (" + idx.Count + " file(s)).");
        }

        /// <summary>The selected entries are replaced by the chosen files, in file-name order (1, 2, 10 - not 1, 10, 2).
        /// Fewer files: only that many are replaced. More files: the extra ones are added to the open folder.</summary>
        private void ReplaceAfsByNameOrder()
        {
            if (afs == null) return;
            var sel = new List<int>();
            foreach (int i in SelAfsAll()) sel.Add(i);
            sel = sel.Distinct().OrderBy(x => x).ToList();
            if (sel.Count == 0) { MessageBox.Show(this, "Select the entries to replace in the list first.", "Replace by file name order"); return; }
            using (var od = new OpenFileDialog { Multiselect = true, Title = "Choose the files (they are used in file-name order)", Filter = "All files|*.*" })
            {
                if (od.ShowDialog(this) != DialogResult.OK) return;
                var files = od.FileNames.OrderBy(f => Path.GetFileName(f), Comparer<string>.Create(AfsNatural)).ToList();
                int pairs = Math.Min(sel.Count, files.Count);
                int extra = Math.Max(0, files.Count - sel.Count);
                string msg = pairs + " entry(ies) will be replaced.\nFirst: " + AfsName(sel[0]) + "  <-  " + Path.GetFileName(files[0]) +
                             "\nLast: " + AfsName(sel[pairs - 1]) + "  <-  " + Path.GetFileName(files[pairs - 1]);
                if (extra > 0) msg += "\n\n" + extra + " more file(s) will be ADDED to /" + AfsNorm(afsFolder) + ".";
                if (sel.Count > files.Count) msg += "\n\n" + (sel.Count - files.Count) + " selected entry(ies) stay untouched (not enough files).";
                if (MessageBox.Show(this, msg + "\n\nContinue?", "Replace by file name order", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                int done = 0, added = 0, skipped = 0;
                try
                {
                    for (int k = 0; k < pairs; k++) { afs.ReplaceData(sel[k], File.ReadAllBytes(files[k])); done++; }
                    for (int k = sel.Count; k < files.Count; k++)
                    {
                        string name = AfsAddName(Path.GetFileName(files[k]));
                        if (Encoding.Latin1.GetByteCount(name) > AfsNameMax) { skipped++; continue; }
                        afs.Add(name, File.ReadAllBytes(files[k]));
                        added++;
                    }
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Replace by file name order", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                PopulateAfs();
                SetStatus("Replaced " + done + ", added " + added + (skipped > 0 ? ", skipped " + skipped + " (name over " + AfsNameMax + " bytes)" : "") + ". The files were put in as they are (no conversion).");
            }
        }
    }
}
