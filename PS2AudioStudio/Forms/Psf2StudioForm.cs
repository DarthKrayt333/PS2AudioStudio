using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using PS2AudioStudio.Archives;
using PS2AudioStudio.Audio;
using PS2AudioStudio.Controls;

namespace PS2AudioStudio
{
    /// <summary>PSF2 Studio: edits every part of an HD (VAGs, sample params, sample sets, programs, splits) and rebuilds HD + BD.</summary>
    internal sealed class Psf2StudioForm : Form
    {
        private sealed class VagSlot
        {
            public byte[] Raw; public long Order; public ushort Rate; public byte Attr; public byte Pad = 0xFF;
        }

        // working templates (taken from your working one-VAG mini hack)
        private static readonly byte[] TplParam = { 0, 0, 1, 0x40, 0x7F, 0, 1, 0, 0, 1, 0, 0x3C, 0, 0x40, 0, 0x0A, 0xFF, 0xFF, 0xFF, 0xAF, 0xCB, 0x5F, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3 };
        private static readonly byte[] TplProgHdr = { 0x24, 0, 0, 0, 1, 0x14, 0xFF, 0x40, 0, 0, 0, 0x3C, 0, 0xFF, 0, 0, 0, 0, 0, 0, 0x64, 0, 0x64, 0, 0x80, 0, 0x80, 0, 0x80, 0, 0x80, 0, 0, 0, 0, 0 };
        private static readonly byte[] TplSplit = { 0, 0, 0x0C, 0x3C, 0x77, 0, 0, 6, 0, 6, 0, 0x3C, 0, 0x3C, 0, 0x3C, 0xFF, 0x40, 0, 0 };

        private HdFile hf;
        private readonly List<VagSlot> vags = new List<VagSlot>();
        private byte[] lead = new byte[0];
        private long nextOrder = 1L << 40;
        private bool dirty;
        private bool _bulk;
        private readonly AudioPlayer player = new AudioPlayer();
        private readonly List<KeyValuePair<SplitContainer, int>> splitters = new List<KeyValuePair<SplitContainer, int>>();

        public byte[] ResultHd, ResultBd;

        private TabControl tabs;
        private ListView lvVag, lvPar, lvSet, lvProg, lvSplit;
        private PropertyGrid pgPar, pgSet, pgProg, pgSplit;
        private NumericUpDown numRate;
        private CheckBox chkLoop;
        private ComboBox cmbImport;
        private Label lblVag, lblStatus;

        public Psf2StudioForm(byte[] hd, byte[] bd)
        {
            Text = "PSF2 Studio - HD / BD editor";
            Size = new Size(1200, 780);
            MinimumSize = new Size(900, 560);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Bg;
            ForeColor = Color.White;
            try { AppIcon.Apply(this); } catch { }

            BuildUi();

            if (hd != null && bd != null)
            {
                try { LoadModel(hd, bd); Status("Loaded the current bank. Edit anything (you can drop audio files here), then press Apply."); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "The current HD could not be read:\n" + ex.Message + "\n\nStarting with an empty bank.", "PSF2 Studio");
                    LoadEmpty();
                }
            }
            else { LoadEmpty(); Status("No bank was loaded: starting with an empty bank. Drop audio files here, then add programs."); }

            FormClosing += (s, e) =>
            {
                if (DialogResult != DialogResult.OK && dirty &&
                    MessageBox.Show(this, "Discard your edits?", "PSF2 Studio", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    e.Cancel = true;
            };
            FormClosed += (s, e) => { try { player.Dispose(); } catch { } };
        }

        // ================================================================= UI
        private static Button B(string text, EventHandler click, int width = 0)
        {
            var b = width > 0 ? Ui.Btn(text, width) : Ui.Btn(text);
            b.Click += click;
            return b;
        }

        private static FlowLayoutPanel MakeBar(params Button[] btns)
        {
            var f = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Ui.Panel, Padding = new Padding(2) };
            f.Controls.AddRange(btns);
            return f;
        }

        private static ListView MakeList(string[] names, int[] widths)
        {
            var lv = new ListView { Dock = DockStyle.Fill };
            Ui.StyleList(lv);
            for (int i = 0; i < names.Length; i++) lv.Columns.Add(names[i], widths[i]);
            lv.MultiSelect = true;      // Shift+click and Ctrl+click select several rows
            lv.HideSelection = false;   // the selection stays visible when the list loses focus
            return lv;                  // Ctrl+A is handled by ProcessCmdKey in this form
        }

        private static PropertyGrid MakeGrid()
        {
            return new PropertyGrid
            {
                Dock = DockStyle.Fill,
                ToolbarVisible = false,
                PropertySort = PropertySort.NoSort,
                BackColor = Ui.Panel,
                ViewBackColor = Ui.Edit,
                ViewForeColor = Color.White,
                LineColor = Color.FromArgb(70, 70, 85),
                HelpBackColor = Ui.Panel,
                HelpForeColor = Color.White,
                CategoryForeColor = Color.LightSkyBlue
            };
        }

        private void BuildUi()
        {
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, BackColor = Ui.Panel, Padding = new Padding(6, 6, 6, 0) };
            bottom.Controls.AddRange(new Control[]
            {
                B("✔ Apply to the PSF2 tab", (s, e) => OnApply(), 190),
                B("🔍 Check links", (s, e) => OnCheck(), 130),
                B("📂 Load other HD + BD...", (s, e) => LoadOther(), 190),
                B("🆕 New empty bank", (s, e) => NewEmpty(), 150),
                B("Cancel", (s, e) => Close(), 90)
            });
            lblStatus = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = Color.Yellow, BackColor = Ui.Panel, Padding = new Padding(6, 4, 0, 0), AutoEllipsis = true };

            tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildVagPage());
            tabs.TabPages.Add(BuildParPage());
            tabs.TabPages.Add(BuildSetPage());
            tabs.TabPages.Add(BuildProgPage());
            foreach (TabPage t in tabs.TabPages) { t.BackColor = Ui.Bg; t.ForeColor = Color.White; }

            Controls.Add(tabs);
            Controls.Add(lblStatus);
            Controls.Add(bottom);

            HookDrop(this);

            Shown += (s, e) =>
            {
                foreach (var kv in splitters)
                {
                    try
                    {
                        int dim = kv.Key.Orientation == Orientation.Vertical ? kv.Key.Width : kv.Key.Height;
                        kv.Key.SplitterDistance = Math.Max(60, Math.Min(dim - 60, kv.Value));
                    }
                    catch { }
                }
            };
        }

        private TabPage TwoPane(string title, Control list, Control bar, Control right, int leftWidth)
        {
            var tp = new TabPage(title);
            var sc = new SplitContainer { Dock = DockStyle.Fill };
            sc.Panel1.Controls.Add(list);
            sc.Panel1.Controls.Add(bar);
            sc.Panel2.Controls.Add(right);
            tp.Controls.Add(sc);
            splitters.Add(new KeyValuePair<SplitContainer, int>(sc, leftWidth));
            return tp;
        }

        // ---------------------------------------------------------------- drag and drop
        private void HookDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += OnDropEnter;
            c.DragDrop += OnDropFiles;
            foreach (Control k in c.Controls) HookDrop(k);
        }

        private void OnDropEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDropFiles(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            files = files.Where(File.Exists).ToArray();
            if (files.Length == 0) return;
            // do the work after the drop has finished (so Explorer is not blocked by message boxes)
            BeginInvoke(new Action(() =>
            {
                tabs.SelectedIndex = 0;
                ImportFiles(files);
            }));
        }

        // ---------------------------------------------------------------- VAG page
        private TabPage BuildVagPage()
        {
            lvVag = MakeList(new[] { "#", "Rate", "Loop", "Bytes", "Length" }, new[] { 45, 70, 55, 90, 75 });
            lvVag.MultiSelect = true;
            lvVag.SelectedIndexChanged += (s, e) => OnVagSelect();
            lvVag.DoubleClick += (s, e) => PlayVag();

            var bar = MakeBar(
                B("➕ Add audio file(s)...", (s, e) => AddVags(), 170),
                B("🔁 Replace...", (s, e) => ReplaceVag(), 110),
                B("📄 Duplicate", (s, e) => DuplicateVag(), 110),
                B("🗑 Remove", (s, e) => RemoveVag(), 100),
                B("▲", (s, e) => MoveVag(-1), 44),
                B("▼", (s, e) => MoveVag(1), 44),
                B("▶ Play", (s, e) => PlayVag(), 80),
                B("⏹", (s, e) => player.Stop(), 44),
                B("💾 Extract...", (s, e) => ExtractVags(), 110));

            var right = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
            lblVag = new Label { Left = 10, Top = 10, Width = 520, Height = 120, ForeColor = Color.LightGreen, Font = new Font("Consolas", 9.5f), Text = "" };
            var l1 = new Label { Left = 10, Top = 142, Width = 200, Height = 20, ForeColor = Color.White, Text = "Sample rate (Hz) of the selected VAG(s):" };
            l1.AutoSize = true;
            numRate = new NumericUpDown { Left = 10, Top = 166, Width = 110, Minimum = 1000, Maximum = 65535, Value = 22050, BackColor = Ui.Edit, ForeColor = Color.White };
            chkLoop = new CheckBox { Left = 10, Top = 198, Width = 480, Height = 24, ForeColor = Color.Lime, Text = "Loop attribute (the game loops this VAG by itself)" };
            var bApply = B("Apply rate / loop to the selected VAG(s)", (s, e) => ApplyRateLoop(), 300);
            bApply.Left = 10; bApply.Top = 230;

            var l2 = new Label { Left = 10, Top = 282, Width = 300, Height = 20, ForeColor = Color.White, Text = "Rate used when importing WAV / MP3 / M4A / ADX:" };
            l2.AutoSize = true;
            cmbImport = new ComboBox { Left = 10, Top = 306, Width = 240, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Ui.Edit, ForeColor = Color.White };
            cmbImport.Items.AddRange(new object[] { "Keep the file's own rate", "11025", "16000", "22050", "32000", "44100", "48000" });
            cmbImport.SelectedIndex = 0;

            var help = new Label
            {
                Left = 10,
                Top = 346,
                Width = 520,
                Height = 220,
                ForeColor = Color.Gainsboro,
                Text = "• DROP audio files onto this window, or use Add / Replace. Any format works (WAV, MP3, M4A, FLAC, OGG, ADX, VAG).\r\n" +
                       "• Non-VAG audio is converted to PS-ADPCM here (mono). Loop points of the file become the VAG loop.\r\n" +
                       "• A .vag file is used as it is (no re-encoding).\r\n" +
                       "• Remove makes a slot EMPTY. Empty slots at the very END are deleted completely.\r\n" +
                       "• ▲ ▼ swap two slots and fix the sample params that use them.\r\n" +
                       "• Add fills the first empty slot, or appends at the end.\r\n" +
                       "• The VAG numbers are the ones the SAMPLE PARAMS tab points to.\r\n" +
                       "• To make a sound playable: Programs tab → Add program... and tick the VAG(s)."
            };
            right.Controls.AddRange(new Control[] { lblVag, l1, numRate, chkLoop, bApply, l2, cmbImport, help });
            return TwoPane("🔊 VAGs (BD samples)", lvVag, bar, right, 560);
        }

        // ---------------------------------------------------------------- sample params page
        private TabPage BuildParPage()
        {
            lvPar = MakeList(new[] { "#", "VAG", "Base note", "Vel", "ADSR1 ADSR2" }, new[] { 45, 70, 80, 70, 110 });
            lvPar.SelectedIndexChanged += (s, e) => OnParSelect();
            pgPar = MakeGrid();
            pgPar.PropertyValueChanged += (s, e) => { dirty = true; UpdateSelectedRows(lvPar, ParRow); };
            var bar = MakeBar(
                B("➕ Add / copy selected", (s, e) => ParAdd(), 170),
                B("🗑 Remove", (s, e) => { if (RemoveSel(hf.Params, lvPar)) RefreshPars(-1); }, 100),
                B("▲", (s, e) => { int j = MoveSel(hf.Params, lvPar, -1, RemapParams); if (j >= 0) RefreshPars(j); }, 44),
                B("▼", (s, e) => { int j = MoveSel(hf.Params, lvPar, 1, RemapParams); if (j >= 0) RefreshPars(j); }, 44));
            return TwoPane("🎚 Sample params (SMPL)", lvPar, bar, pgPar, 520);
        }
        private void OnParSelect()
        {
            if (_bulk) return;
            var l = new List<object>();
            foreach (int i in SelIdxs(lvPar))
                if (i < hf.Params.Count && hf.Params[i] != null) l.Add(new SdParamView(hf.Params[i]));
            pgPar.SelectedObjects = l.ToArray();
        }

        // ---------------------------------------------------------------- sample sets page
        private TabPage BuildSetPage()
        {
            lvSet = MakeList(new[] { "#", "Velocity", "Params", "Param numbers" }, new[] { 45, 80, 60, 200 });
            lvSet.SelectedIndexChanged += (s, e) => OnSetSelect();
            pgSet = MakeGrid();
            pgSet.PropertyValueChanged += (s, e) => { dirty = true; UpdateSelectedRows(lvSet, SetRow); };
            var bar = MakeBar(
                B("➕ Add / copy selected", (s, e) => SetAdd(), 170),
                B("🗑 Remove", (s, e) => { if (RemoveSel(hf.Sets, lvSet)) RefreshSets(-1); }, 100),
                B("▲", (s, e) => { int j = MoveSel(hf.Sets, lvSet, -1, RemapSets); if (j >= 0) RefreshSets(j); }, 44),
                B("▼", (s, e) => { int j = MoveSel(hf.Sets, lvSet, 1, RemapSets); if (j >= 0) RefreshSets(j); }, 44));
            return TwoPane("📚 Sample sets (SSET)", lvSet, bar, pgSet, 520);
        }
        private void OnSetSelect()
        {
            if (_bulk) return;
            var l = new List<object>();
            foreach (int i in SelIdxs(lvSet))
                if (i < hf.Sets.Count && hf.Sets[i] != null) l.Add(new SdSetView(hf.Sets[i]));
            pgSet.SelectedObjects = l.ToArray();
        }

        // ---------------------------------------------------------------- programs page
        private TabPage BuildProgPage()
        {
            lvProg = MakeList(new[] { "#", "Bank / program", "Splits" }, new[] { 45, 120, 55 });
            lvProg.SelectedIndexChanged += (s, e) => OnProgSelect();
            var bar = MakeBar(
                B("➕ Add program...", (s, e) => ProgAdd(), 150),
                B("📄 Copy selected", (s, e) => ProgCopy(), 130),
                B("🗑 Remove", (s, e) => { if (RemoveSel(hf.Programs, lvProg)) RefreshProgs(-1); }, 100),
                B("▲", (s, e) => { int j = MoveSel(hf.Programs, lvProg, -1, null); if (j >= 0) RefreshProgs(j); }, 44),
                B("▼", (s, e) => { int j = MoveSel(hf.Programs, lvProg, 1, null); if (j >= 0) RefreshProgs(j); }, 44));
            pgProg = MakeGrid();
            pgProg.PropertyValueChanged += (s, e) => { dirty = true; UpdateSelectedRows(lvProg, ProgRow); };
            lvSplit = MakeList(new[] { "#", "Keys", "Sample set", "VAG(s)" }, new[] { 40, 110, 80, 90 });
            lvSplit.SelectedIndexChanged += (s, e) => OnSplitSelect();
            var sbar = MakeBar(
                B("🎯 Pick sample...", (s, e) => SplitPick(), 130),
                B("➕ Split from sample(s)...", (s, e) => SplitAddSample(), 190),
                B("➕ Copy split", (s, e) => SplitAdd(), 110),
                B("🗑 Remove", (s, e) => SplitRemove(), 100),
                B("▲", (s, e) => SplitMove(-1), 44),
                B("▼", (s, e) => SplitMove(1), 44));
            pgSplit = MakeGrid();
            pgSplit.PropertyValueChanged += (s, e) =>
            {
                dirty = true;
                var p = SelProgram();
                if (p == null) return;
                foreach (int j in SelIdxs(lvSplit))
                    if (j < p.Splits.Count) UpdateRow(lvSplit, j, SplitRow(p, j));
            };
            var tp = new TabPage("🎹 Programs / splits");
            var sc = new SplitContainer { Dock = DockStyle.Fill };
            sc.Panel1.Controls.Add(lvProg);
            sc.Panel1.Controls.Add(bar);
            var sc2 = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            sc2.Panel1.Controls.Add(pgProg);
            var sc3 = new SplitContainer { Dock = DockStyle.Fill };
            sc3.Panel1.Controls.Add(lvSplit);
            sc3.Panel1.Controls.Add(sbar);
            sc3.Panel2.Controls.Add(pgSplit);
            sc2.Panel2.Controls.Add(sc3);
            sc.Panel2.Controls.Add(sc2);
            tp.Controls.Add(sc);
            splitters.Add(new KeyValuePair<SplitContainer, int>(sc, 400));
            splitters.Add(new KeyValuePair<SplitContainer, int>(sc2, 200));
            splitters.Add(new KeyValuePair<SplitContainer, int>(sc3, 460));
            return tp;
        }

        // ================================================================= helpers
        private void Status(string s) { lblStatus.Text = s; }

        private static int SelIdx(ListView lv) { return lv.SelectedIndices.Count > 0 ? lv.SelectedIndices[0] : -1; }

        private static List<int> SelIdxs(ListView lv)
        {
            var l = new List<int>();
            foreach (int i in lv.SelectedIndices) l.Add(i);
            l.Sort();
            return l;
        }
        private static void UpdateSelectedRows(ListView lv, Func<int, string[]> row)
        {
            foreach (int i in SelIdxs(lv)) UpdateRow(lv, i, row(i));
        }
        private void SelectAllRows(ListView lv)
        {
            if (lv.Items.Count == 0) return;
            _bulk = true;
            lv.BeginUpdate();
            try { foreach (ListViewItem it in lv.Items) it.Selected = true; }
            finally { lv.EndUpdate(); _bulk = false; }
            if (lv == lvVag) OnVagSelect();
            else if (lv == lvPar) OnParSelect();
            else if (lv == lvSet) OnSetSelect();
            else if (lv == lvProg) OnProgSelect();
            else if (lv == lvSplit) OnSplitSelect();
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.A))
            {
                Control c = this;
                while (c is ContainerControl && ((ContainerControl)c).ActiveControl != null)
                    c = ((ContainerControl)c).ActiveControl;
                var lv = c as ListView;
                if (lv != null) { SelectAllRows(lv); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private static string NoteName(int n)
        {
            if (n < 0 || n > 127) return n.ToString();
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            return names[n % 12] + (n / 12 - 1);
        }

        private static void FillList(ListView lv, int count, Func<int, string[]> row, Func<int, bool> empty, int select)
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            for (int i = 0; i < count; i++)
            {
                var it = new ListViewItem(row(i));
                it.ForeColor = empty(i) ? Color.Gray : Color.White;
                lv.Items.Add(it);
            }
            lv.EndUpdate();
            if (select >= 0 && select < lv.Items.Count) { lv.Items[select].Selected = true; lv.Items[select].EnsureVisible(); }
        }

        private static void UpdateRow(ListView lv, int i, string[] cells)
        {
            if (i < 0 || i >= lv.Items.Count) return;
            var it = lv.Items[i];
            for (int c = 0; c < cells.Length && c < it.SubItems.Count; c++) it.SubItems[c].Text = cells[c];
        }

        /// <summary>Deletes the empty slots at the very END of a list (only used by Remove).</summary>
        private static void TrimTail<T>(List<T> l) where T : class
        {
            while (l.Count > 0 && l[l.Count - 1] == null) l.RemoveAt(l.Count - 1);
        }

        private bool RemoveSel<T>(List<T> list, ListView lv) where T : class
        {
            bool any = false;
            foreach (int i in SelIdxs(lv))
                if (i < list.Count && list[i] != null) { list[i] = null; any = true; }
            if (!any) return false;
            TrimTail(list);
            dirty = true;
            return true;
        }
        private int MoveSel<T>(List<T> list, ListView lv, int dir, Action<int, int> remap)
        {
            if (lv.SelectedIndices.Count > 1) { Status("Select exactly one row to move it up or down."); return -1; }
            int i = SelIdx(lv), j = i + dir;
            if (i < 0 || j < 0 || j >= list.Count) return -1;
            T t = list[i]; list[i] = list[j]; list[j] = t;
            if (remap != null) remap(i, j);
            dirty = true;
            return j;
        }

        private void EnsureSlots()
        {
            if (vags.Count == 0) vags.Add(null);
            if (hf.Vags.Count == 0) hf.Vags.Add(null);
            if (hf.Params.Count == 0) hf.Params.Add(null);
            if (hf.Sets.Count == 0) hf.Sets.Add(null);
            if (hf.Programs.Count == 0) hf.Programs.Add(null);
        }

        private bool ConfirmDiscard()
        {
            return !dirty || MessageBox.Show(this, "This replaces everything you edited in the Studio. Continue?", "PSF2 Studio", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        // ---- keep references right when two slots swap places
        private void RemapVags(int a, int b)
        {
            foreach (var p in hf.Params)
            {
                if (p == null) continue;
                if (p.VagIndex == a) p.VagIndex = b; else if (p.VagIndex == b) p.VagIndex = a;
            }
        }
        private void RemapParams(int a, int b)
        {
            foreach (var s in hf.Sets)
            {
                if (s == null) continue;
                for (int i = 0; i < s.Params.Count; i++)
                {
                    if (s.Params[i] == a) s.Params[i] = (ushort)b; else if (s.Params[i] == b) s.Params[i] = (ushort)a;
                }
            }
        }
        private void RemapSets(int a, int b)
        {
            foreach (var pr in hf.Programs)
            {
                if (pr == null) continue;
                foreach (var sp in pr.Splits)
                {
                    if (sp.SetIndex == a) sp.SetIndex = b; else if (sp.SetIndex == b) sp.SetIndex = a;
                }
            }
        }

        // ================================================================= load
        private void LoadModel(byte[] hd, byte[] bd)
        {
            var f = HdFile.Parse(hd);
            var offs = new SortedSet<uint>();
            foreach (var v in f.Vags) if (v != null) offs.Add(v.BdOffset);
            var sorted = offs.ToList();
            var map = new Dictionary<uint, byte[]>();
            for (int i = 0; i < sorted.Count; i++)
            {
                long a = sorted[i];
                long b = i + 1 < sorted.Count ? sorted[i + 1] : bd.Length;
                if (a > bd.Length) a = bd.Length;
                if (b > bd.Length) b = bd.Length;
                if (b < a) b = a;
                var raw = new byte[b - a];
                Array.Copy(bd, a, raw, 0, raw.Length);
                map[sorted[i]] = raw;
            }
            var nl = new byte[0];
            if (sorted.Count > 0 && sorted[0] > 0)
            {
                nl = new byte[Math.Min((long)sorted[0], (long)bd.Length)];
                Array.Copy(bd, 0, nl, 0, nl.Length);
            }
            hf = f;
            lead = nl;
            vags.Clear();
            foreach (var v in f.Vags)
                vags.Add(v == null ? null : new VagSlot { Raw = map[v.BdOffset], Order = v.BdOffset, Rate = v.Rate, Attr = v.Attribute, Pad = v.Pad });
            EnsureSlots();
            dirty = false;
            RefreshAll();
        }

        private void LoadEmpty()
        {
            var f = new HdFile();
            f.VersionBytes = new byte[] { 0x49, 0x45, 0x43, 0x53, 0x73, 0x72, 0x65, 0x56, 0x10, 0, 0, 0, 0, 0, 2, 0 };
            var head = new byte[0x40];
            for (int i = 0; i < head.Length; i++) head[i] = 0xFF;
            Encoding.ASCII.GetBytes("IECSdaeH").CopyTo(head, 0);
            BitConverter.GetBytes(0x40u).CopyTo(head, 8);
            for (int i = 12; i < 36; i++) head[i] = 0;
            f.HeadBytes = head;
            hf = f;
            lead = new byte[0];
            vags.Clear();
            EnsureSlots();
            dirty = false;
            RefreshAll();
        }

        private void NewEmpty()
        {
            if (!ConfirmDiscard()) return;
            LoadEmpty();
            Status("Empty bank. Add VAGs first, then programs (Programs tab → Add program...).");
        }

        private void LoadOther()
        {
            if (!ConfirmDiscard()) return;
            using (var d = new OpenFileDialog { Filter = "HD|*.hd;*.HD|All files|*.*", Title = "Open an HD (its BD is found next to it)" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string hdp = d.FileName, bdp = null;
                foreach (string ext in new[] { ".bd", ".BD", ".Bd" })
                {
                    string c = Path.ChangeExtension(hdp, ext);
                    if (File.Exists(c)) { bdp = c; break; }
                }
                if (bdp == null)
                {
                    using (var d2 = new OpenFileDialog { Filter = "BD|*.bd;*.bin|All files|*.*", Title = "Locate the BD for " + Path.GetFileName(hdp) })
                    {
                        if (d2.ShowDialog(this) != DialogResult.OK) return;
                        bdp = d2.FileName;
                    }
                }
                try
                {
                    LoadModel(File.ReadAllBytes(hdp), File.ReadAllBytes(bdp));
                    dirty = true;
                    Status("Loaded " + Path.GetFileName(hdp) + " + " + Path.GetFileName(bdp) + " into the Studio.");
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "PSF2 Studio", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        // ================================================================= refresh
        private string[] VagRow(int i)
        {
            var v = vags[i];
            if (v == null) return new[] { i.ToString(), "(empty)", "", "", "" };
            double sec = v.Rate > 0 ? v.Raw.Length / 16 * 28.0 / v.Rate : 0;
            return new[] { i.ToString(), v.Rate.ToString(), v.Attr == 1 ? "LOOP" : "-", v.Raw.Length.ToString("N0"), sec.ToString("F2") + " s" };
        }

        private string[] ParRow(int i)
        {
            var p = hf.Params[i];
            if (p == null) return new[] { i.ToString(), "(empty)", "", "", "" };
            var r = p.Raw;
            return new[] { i.ToString(), "VAG " + p.VagIndex, NoteName(SdBytes.B(r, 11)), SdBytes.B(r, 2) + "-" + SdBytes.B(r, 4), SdBytes.W(r, 18).ToString("X4") + " " + SdBytes.W(r, 20).ToString("X4") };
        }

        private string[] SetRow(int i)
        {
            var s = hf.Sets[i];
            if (s == null) return new[] { i.ToString(), "(empty)", "", "" };
            return new[] { i.ToString(), s.VelLow + "-" + s.VelHigh, s.Params.Count.ToString(), string.Join(", ", s.Params) };
        }

        private string[] ProgRow(int i)
        {
            var p = hf.Programs[i];
            if (p == null) return new[] { i.ToString(), "(empty)", "" };
            return new[] { i.ToString(), "bank " + (i / 128) + " / #" + (i % 128), p.Splits.Count.ToString() };
        }

        private string SplitVagText(HdSplit sp)
        {
            if (sp.SetIndex < 0 || sp.SetIndex >= hf.Sets.Count || hf.Sets[sp.SetIndex] == null) return "?";
            var l = new List<string>();
            foreach (int pi in hf.Sets[sp.SetIndex].Params)
                l.Add(pi >= 0 && pi < hf.Params.Count && hf.Params[pi] != null ? hf.Params[pi].VagIndex.ToString() : "?");
            return string.Join(", ", l);
        }

        private string[] SplitRow(HdProgram p, int j)
        {
            var sp = p.Splits[j];
            return new[] { j.ToString(), NoteName(sp.KeyLow) + " - " + NoteName(sp.KeyHigh), "set " + sp.SetIndex, SplitVagText(sp) };
        }

        private void RefreshVags(int sel) { FillList(lvVag, vags.Count, VagRow, i => vags[i] == null, sel); OnVagSelect(); }
        private void RefreshPars(int sel) { FillList(lvPar, hf.Params.Count, ParRow, i => hf.Params[i] == null, sel); }
        private void RefreshSets(int sel) { FillList(lvSet, hf.Sets.Count, SetRow, i => hf.Sets[i] == null, sel); }
        private void RefreshProgs(int sel) { FillList(lvProg, hf.Programs.Count, ProgRow, i => hf.Programs[i] == null, sel); OnProgSelect(); }
        private void RefreshSplits(int sel)
        {
            var p = SelProgram();
            if (p == null) { lvSplit.Items.Clear(); return; }
            FillList(lvSplit, p.Splits.Count, j => SplitRow(p, j), j => false, sel);
        }

        private void RefreshAll()
        {
            RefreshVags(-1); RefreshPars(-1); RefreshSets(-1); RefreshProgs(-1);
        }

        // ================================================================= VAGs
        private void OnVagSelect()
        {
            if (_bulk) return;
            var sel = lvVag.SelectedIndices.Cast<int>().ToList();
            long total = lead.Length;
            var seen = new HashSet<byte[]>();
            int used = 0;
            foreach (var v in vags) if (v != null) { used++; if (seen.Add(v.Raw)) total += v.Raw.Length; }
            string head = used + " VAG(s) in " + vags.Count + " slot(s)   |   BD size " + total.ToString("N0") + " bytes (0x" + total.ToString("X") + ")\r\n\r\n";
            if (sel.Count == 0 || sel[0] >= vags.Count) { lblVag.Text = head + "Select a VAG."; return; }
            var one = vags[sel[0]];
            if (one == null) { lblVag.Text = head + "Slot " + sel[0] + " is empty. Add or Replace puts a VAG in it."; return; }
            numRate.Value = Math.Max(numRate.Minimum, Math.Min(numRate.Maximum, one.Rate));
            chkLoop.Checked = one.Attr == 1;
            double sec = one.Rate > 0 ? one.Raw.Length / 16 * 28.0 / one.Rate : 0;
            lblVag.Text = head + "VAG " + sel[0] + (sel.Count > 1 ? "  (+" + (sel.Count - 1) + " more selected)" : "") +
                          "\r\n  " + one.Rate + " Hz, " + (one.Attr == 1 ? "LOOP" : "one-shot") +
                          "\r\n  " + one.Raw.Length.ToString("N0") + " bytes = " + (one.Raw.Length / 16) + " blocks = " + sec.ToString("F2") + " s";
        }

        /// <summary>A .vag (VAGp or headerless) is used as it is.</summary>
        private static VagSlot ReadVagBytes(byte[] data)
        {
            int off = 0, rate = 22050, len = data.Length;
            bool vagp = data.Length >= 0x30 && data[0] == 'V' && data[1] == 'A' && data[2] == 'G' && data[3] == 'p';
            if (vagp)
            {
                rate = (data[0x10] << 24) | (data[0x11] << 16) | (data[0x12] << 8) | data[0x13];
                int decl = (data[0x0C] << 24) | (data[0x0D] << 16) | (data[0x0E] << 8) | data[0x0F];
                off = 0x30;
                len = data.Length - off;
                if (decl > 0 && decl <= len) len = decl;
            }
            else if (data.Length >= 4 && data[0] == 'V' && data[1] == 'A' && data[2] == 'G' && data[3] == 'i')
                throw new InvalidDataException("This is an interleaved (VAGi, stereo) file. A bank needs mono VAGs.");
            len -= len % 16;
            if (len < 32) throw new InvalidDataException("The file has no usable PS-ADPCM data.");
            if (rate < 1000 || rate > 65535) throw new InvalidDataException("Sample rate " + rate + " Hz cannot be stored in an HD (1000 - 65535).");
            var raw = new byte[len];
            Array.Copy(data, off, raw, 0, len);
            bool loop = false;
            for (int p = 0; p + 16 <= len; p += 16)
            {
                int fl = raw[p + 1];
                if (fl == 6 || fl == 4 || (p + 16 == len && fl == 3)) loop = true;
            }
            return new VagSlot { Raw = raw, Rate = (ushort)rate, Attr = (byte)(loop ? 1 : 0) };
        }

        private int ImportRate()
        {
            string t = cmbImport.SelectedItem as string;
            int v;
            return (t != null && int.TryParse(t, out v)) ? v : 0;
        }

        /// <summary>Any audio file: VAG is used as it is, everything else is decoded and encoded to PS-ADPCM.</summary>
        private VagSlot ReadAudioFile(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            FileKind kind = AudioIO.Detect(data, path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (kind == FileKind.Vag || ext == ".vag") return ReadVagBytes(data);

            AudioBuffer buf;
            try { buf = AudioIO.Load(path); }
            catch (RawImportRequiredException) { throw new InvalidDataException("This file needs the raw PS-ADPCM import. Open it in the Audio Editor first."); }
            if (buf == null || buf.TotalFrames < 1) throw new InvalidDataException("The file has no audio.");

            int rate = buf.SampleRate;
            int target = ImportRate();
            if (target > 0 && target != rate) { buf = AudioIO.Resample(buf, target); rate = target; }
            if (rate > 65535) { buf = AudioIO.Resample(buf, 48000); rate = 48000; }
            if (rate < 1000) throw new InvalidDataException("Sample rate " + rate + " Hz is too low for an HD (minimum 1000).");

            float[] mono = buf.MixToMono();
            bool loop = buf.LoopEnabled && buf.LoopEnd > buf.LoopStart && buf.LoopStart >= 0;
            byte[] adpcm = VagCodec.EncodeAdpcm(mono, loop, buf.LoopStart, buf.LoopEnd);
            return new VagSlot { Raw = adpcm, Rate = (ushort)rate, Attr = (byte)(loop ? 1 : 0) };
        }

        private int PutNewVag(VagSlot n)
        {
            n.Order = nextOrder++;
            int at = vags.IndexOf(null);
            if (at < 0) { vags.Add(n); at = vags.Count - 1; } else vags[at] = n;
            dirty = true;
            return at;
        }

        private void ImportFiles(IEnumerable<string> files)
        {
            int last = -1, n = 0;
            var bad = new List<string>();
            var old = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (string f in files)
                {
                    try { last = PutNewVag(ReadAudioFile(f)); n++; }
                    catch (Exception ex) { bad.Add(Path.GetFileName(f) + ": " + ex.Message); }
                }
            }
            finally { Cursor = old; }
            RefreshVags(last);
            if (bad.Count > 0)
                MessageBox.Show(this, "These files could not be added:\n\n" + string.Join("\n", bad.Take(15)), "Add audio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Status(n + " VAG(s) added" + (last >= 0 ? " (last one is slot " + last + ")." : "."));
        }

        /// <summary>Used by the PSF2 tab (no window is shown): adds the files as new VAGs with their OWN sample rate
        /// (empty slots first, then at the end) and returns the rebuilt HD + BD. Programs, sets and params are not touched.</summary>
        public bool AppendFiles(IEnumerable<string> files, out byte[] hd, out byte[] bd, out string error, out List<string> bad, out List<int> slots)
        {
            bad = new List<string>();
            slots = new List<int>();
            foreach (string f in files)
            {
                try { slots.Add(PutNewVag(ReadAudioFile(f))); }
                catch (Exception ex) { bad.Add(Path.GetFileName(f) + ": " + ex.Message); }
            }
            bool ok = TryBuild(out hd, out bd, out error);
            try { player.Dispose(); } catch { }
            return ok;
        }

        private void AddVags()
        {
            using (var d = new OpenFileDialog { Filter = AudioIO.AudioOnlyFilter, Multiselect = true, Title = "Add audio file(s) - any format" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                ImportFiles(d.FileNames);
            }
        }

        private void ReplaceVag()
        {
            int i = SelIdx(lvVag);
            if (i < 0) { Status("Select a VAG slot first."); return; }
            using (var d = new OpenFileDialog { Filter = AudioIO.AudioOnlyFilter, Title = "Replace VAG " + i + " with any audio file" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var old = Cursor;
                Cursor = Cursors.WaitCursor;
                try
                {
                    var n = ReadAudioFile(d.FileName);
                    if (vags[i] == null) { n.Order = nextOrder++; vags[i] = n; }
                    else { vags[i].Raw = n.Raw; vags[i].Rate = n.Rate; vags[i].Attr = n.Attr; }
                    dirty = true;
                    RefreshVags(i);
                    Status("VAG " + i + " replaced (" + n.Rate + " Hz, " + (n.Attr == 1 ? "loop" : "one-shot") + ").");
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Replace VAG", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { Cursor = old; }
            }
        }

        private void DuplicateVag()
        {
            int last = -1;
            foreach (int i in lvVag.SelectedIndices.Cast<int>().ToList())
            {
                var v = vags[i];
                if (v == null) continue;
                last = PutNewVag(new VagSlot { Raw = (byte[])v.Raw.Clone(), Rate = v.Rate, Attr = v.Attr, Pad = v.Pad });
            }
            if (last >= 0) { RefreshVags(last); Status("Duplicated (last copy is slot " + last + ")."); }
        }

        private void RemoveVag()
        {
            var sel = lvVag.SelectedIndices.Cast<int>().ToList();
            if (sel.Count == 0) return;
            int before = vags.Count;
            foreach (int i in sel) if (i < vags.Count) vags[i] = null;
            TrimTail(vags);
            dirty = true;
            RefreshVags(-1);
            int cut = before - vags.Count;
            Status(sel.Count + " VAG(s) removed" + (cut > 0 ? " (" + cut + " empty slot(s) at the end deleted)" : " (the slot stays empty so the other numbers do not move)") +
                   ". Use Check links to see which sample params still point to them.");
        }

        private void MoveVag(int dir)
        {
            int j = MoveSel(vags, lvVag, dir, RemapVags);
            if (j >= 0) { RefreshVags(j); RefreshPars(-1); }
        }

        private void ApplyRateLoop()
        {
            int n = 0;
            foreach (int i in lvVag.SelectedIndices.Cast<int>().ToList())
            {
                if (vags[i] == null) continue;
                vags[i].Rate = (ushort)numRate.Value;
                vags[i].Attr = (byte)(chkLoop.Checked ? 1 : 0);
                n++;
            }
            if (n == 0) return;
            dirty = true;
            for (int k = 0; k < vags.Count; k++) UpdateRow(lvVag, k, VagRow(k));
            Status(n + " VAG(s) updated.");
        }

        private void PlayVag()
        {
            int i = SelIdx(lvVag);
            if (i < 0 || vags[i] == null) { Status("Select a VAG first."); return; }
            try
            {
                var v = vags[i];
                var b = VagCodec.DecodeAdpcm(v.Raw, 0, v.Raw.Length, v.Rate);
                player.Play(b, 0, b.TotalFrames, true);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Play VAG"); }
        }

        private static void BE(byte[] o, int at, int v)
        {
            o[at] = (byte)(v >> 24); o[at + 1] = (byte)(v >> 16); o[at + 2] = (byte)(v >> 8); o[at + 3] = (byte)v;
        }

        private static byte[] VagFile(VagSlot v, string name)
        {
            var o = new byte[0x30 + v.Raw.Length];
            o[0] = (byte)'V'; o[1] = (byte)'A'; o[2] = (byte)'G'; o[3] = (byte)'p';
            o[7] = 0x20;
            BE(o, 0x0C, v.Raw.Length);
            BE(o, 0x10, v.Rate);
            var nm = Encoding.ASCII.GetBytes(name);
            Array.Copy(nm, 0, o, 0x20, Math.Min(16, nm.Length));
            Array.Copy(v.Raw, 0, o, 0x30, v.Raw.Length);
            return o;
        }

        private void ExtractVags()
        {
            var sel = lvVag.SelectedIndices.Cast<int>().Where(i => vags[i] != null).ToList();
            if (sel.Count == 0) { Status("Select a VAG first."); return; }
            try
            {
                if (sel.Count == 1)
                {
                    using (var d = new SaveFileDialog { Filter = "VAG|*.vag", FileName = "vag_" + sel[0].ToString("000") + ".vag", AddExtension = true })
                    {
                        if (d.ShowDialog(this) != DialogResult.OK) return;
                        File.WriteAllBytes(d.FileName, VagFile(vags[sel[0]], Path.GetFileNameWithoutExtension(d.FileName)));
                        Status("Saved " + d.FileName);
                    }
                }
                else
                {
                    using (var fb = new FolderBrowserDialog { Description = "Extract " + sel.Count + " VAGs into a folder" })
                    {
                        if (fb.ShowDialog(this) != DialogResult.OK) return;
                        foreach (int i in sel)
                        {
                            string nm = "vag_" + i.ToString("000");
                            File.WriteAllBytes(Path.Combine(fb.SelectedPath, nm + ".vag"), VagFile(vags[i], nm));
                        }
                        Status("Saved " + sel.Count + " VAG files to " + fb.SelectedPath);
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Extract", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        // ================================================================= params / sets
        private int PutParam(HdParam p)
        {
            int at = hf.Params.IndexOf(null);
            if (at < 0) { hf.Params.Add(p); at = hf.Params.Count - 1; } else hf.Params[at] = p;
            dirty = true;
            return at;
        }

        private int PutSet(HdSet s)
        {
            int at = hf.Sets.IndexOf(null);
            if (at < 0) { hf.Sets.Add(s); at = hf.Sets.Count - 1; } else hf.Sets[at] = s;
            dirty = true;
            return at;
        }

        private void ParAdd()
        {
            var src = SelIdxs(lvPar).Where(k => k < hf.Params.Count && hf.Params[k] != null).ToList();
            int at = -1;
            if (src.Count == 0) at = PutParam(new HdParam { Raw = (byte[])TplParam.Clone() });
            else foreach (int i in src) at = PutParam(new HdParam { Raw = (byte[])hf.Params[i].Raw.Clone() });
            RefreshPars(at);
            Status(Math.Max(1, src.Count) + " sample param(s) added (last one is " + at + "). Set the VAG number, base note and ADSR on the right.");
        }
        private void SetAdd()
        {
            var src = SelIdxs(lvSet).Where(k => k < hf.Sets.Count && hf.Sets[k] != null).ToList();
            int at = -1;
            if (src.Count == 0)
            {
                var n = new HdSet();
                n.Params.Add(0);
                at = PutSet(n);
            }
            else
            {
                foreach (int i in src)
                {
                    var o = hf.Sets[i];
                    var n = new HdSet { VelCurve = o.VelCurve, VelLow = o.VelLow, VelHigh = o.VelHigh, Params = new List<ushort>(o.Params) };
                    at = PutSet(n);
                }
            }
            RefreshSets(at);
            Status(Math.Max(1, src.Count) + " sample set(s) added (last one is " + at + "). Type the sample param numbers it uses (e.g. 0, 1).");
        }

        // ================================================================= programs / splits
        private HdProgram SelProgram()
        {
            if (lvProg.SelectedIndices.Count != 1) return null;
            int i = lvProg.SelectedIndices[0];
            return (i < hf.Programs.Count) ? hf.Programs[i] : null;
        }
        private void OnProgSelect()
        {
            if (_bulk) return;
            var l = new List<object>();
            foreach (int i in SelIdxs(lvProg))
                if (i < hf.Programs.Count && hf.Programs[i] != null) l.Add(new SdProgView(hf.Programs[i]));
            pgProg.SelectedObjects = l.ToArray();
            pgSplit.SelectedObjects = new object[0];
            RefreshSplits(-1);
        }
        private void OnSplitSelect()
        {
            if (_bulk) return;
            var p = SelProgram();
            var l = new List<object>();
            if (p != null)
                foreach (int j in SelIdxs(lvSplit))
                    if (j < p.Splits.Count) l.Add(new SdSplitView(p.Splits[j]));
            pgSplit.SelectedObjects = l.ToArray();
        }

        private static HdProgram CloneProgram(HdProgram o)
        {
            var n = new HdProgram { Header = (byte[])o.Header.Clone(), SplitSize = o.SplitSize };
            foreach (var s in o.Splits) n.Splits.Add(new HdSplit { Raw = (byte[])s.Raw.Clone() });
            return n;
        }

        /// <summary>Shows the VAG picker. Returns null when cancelled or when there is no VAG.</summary>
        private List<VagPickResult> Pick(string title, string info, bool multi, int preselect)
        {
            var rows = new List<string[]>();
            for (int i = 0; i < vags.Count; i++)
                if (vags[i] != null) rows.Add(VagRow(i));
            if (rows.Count == 0) { Status("There is no VAG yet. Add or drop audio files on the VAGs tab first."); return null; }
            using (var d = new VagPickerForm(title, info, rows, multi, preselect))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return null;
                return d.Result;
            }
        }

        private const string PickInfoMulti =
            "Tick the VAG(s) to play. Each ticked VAG becomes one split with its own key range.\r\n" +
            "Key numbers 0 - 127 (60 = C4, 36 = C2, 72 = C5). Base note = the key where the VAG sounds at its own pitch.\r\n" +
            "The sample param, the sample set and the split are created and linked for you.";

        /// <summary>Creates a sample param + a sample set + a split for one picked VAG, all linked.</summary>
        private HdSplit MakeSplitForVag(VagPickResult pk)
        {
            var pr = new HdParam { Raw = (byte[])TplParam.Clone() };
            pr.VagIndex = pk.Vag;
            SdBytes.SetB(pr.Raw, 11, pk.BaseNote);
            int pi = PutParam(pr);
            var st = new HdSet();
            st.Params.Add((ushort)pi);
            int si = PutSet(st);
            var sp = new HdSplit { Raw = (byte[])TplSplit.Clone() };
            sp.SetIndex = si;
            sp.KeyLow = pk.KeyLow;
            sp.KeyHigh = pk.KeyHigh;
            return sp;
        }

        private void ProgAdd()
        {
            int def = hf.Programs.IndexOf(null);
            if (def < 0) def = hf.Programs.Count;
            double v;
            if (!Prompt.Number(this, "Add program", "Program number (the SQ selects it with bank + program change; 0-based, 128 programs per bank):", def, out v)) return;
            int idx = (int)v;
            if (idx < 0 || idx > 2047) { Status("Program number must be 0 - 2047."); return; }
            if (idx < hf.Programs.Count && hf.Programs[idx] != null) { MessageBox.Show(this, "Program " + idx + " already exists. Remove it first.", "PSF2 Studio"); return; }

            var picks = Pick("New program " + idx + " - choose the sample(s)", PickInfoMulti, true, -1);
            if (picks == null) return;

            var np = new HdProgram { Header = (byte[])TplProgHdr.Clone(), SplitSize = 20 };
            foreach (var pk in picks) np.Splits.Add(MakeSplitForVag(pk));

            while (hf.Programs.Count <= idx) hf.Programs.Add(null);
            hf.Programs[idx] = np;
            dirty = true;
            RefreshPars(-1); RefreshSets(-1);
            RefreshProgs(idx);
            Status("Program " + idx + " added with " + picks.Count + " split(s). Sample params and sets were created automatically.");
        }

        private void ProgCopy()
        {
            var sel = SelIdxs(lvProg).Where(k => k < hf.Programs.Count && hf.Programs[k] != null).ToList();
            if (sel.Count == 0) { Status("Select a program to copy first."); return; }
            if (sel.Count > 1)
            {
                if (MessageBox.Show(this, "Copy " + sel.Count + " programs into the first free program numbers?", "PSF2 Studio",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                var made = new List<string>();
                int lastAt = -1;
                foreach (int k in sel)
                {
                    int at = hf.Programs.IndexOf(null);
                    if (at < 0) { hf.Programs.Add(null); at = hf.Programs.Count - 1; }
                    if (at > 2047) { Status("No free program number left (maximum 2047)."); break; }
                    hf.Programs[at] = CloneProgram(hf.Programs[k]);
                    made.Add(k + "→" + at);
                    lastAt = at;
                }
                dirty = true;
                RefreshProgs(lastAt);
                Status("Copied (old→new): " + string.Join(", ", made.Take(20)) + (made.Count > 20 ? " ..." : ""));
                return;
            }
            var src = hf.Programs[sel[0]];
            int def = hf.Programs.IndexOf(null);
            if (def < 0) def = hf.Programs.Count;
            double v;
            if (!Prompt.Number(this, "Copy program", "Number of the new program (0-based, 128 per bank):", def, out v)) return;
            int idx = (int)v;
            if (idx < 0 || idx > 2047) { Status("Program number must be 0 - 2047."); return; }
            if (idx < hf.Programs.Count && hf.Programs[idx] != null) { MessageBox.Show(this, "Program " + idx + " already exists. Remove it first.", "PSF2 Studio"); return; }
            while (hf.Programs.Count <= idx) hf.Programs.Add(null);
            hf.Programs[idx] = CloneProgram(src);
            dirty = true;
            RefreshProgs(idx);
            Status("Program " + idx + " added (copy of the selected one; it shares its sample sets).");
        }
        private void SplitAdd()
        {
            var p = SelProgram();
            if (p == null) { Status("Select ONE program first."); return; }
            var sel = SelIdxs(lvSplit).Where(k => k < p.Splits.Count).ToList();
            if (sel.Count == 0) p.Splits.Add(new HdSplit { Raw = (byte[])TplSplit.Clone() });
            else foreach (int k in sel) p.Splits.Add(new HdSplit { Raw = (byte[])p.Splits[k].Raw.Clone() });
            dirty = true;
            RefreshSplits(p.Splits.Count - 1);
            UpdateRow(lvProg, SelIdx(lvProg), ProgRow(SelIdx(lvProg)));
        }
        private void SplitRemove()
        {
            var p = SelProgram();
            var sel = SelIdxs(lvSplit);
            if (p == null || sel.Count == 0) return;
            for (int n = sel.Count - 1; n >= 0; n--)
                if (sel[n] < p.Splits.Count) p.Splits.RemoveAt(sel[n]);
            dirty = true;
            RefreshSplits(Math.Min(sel[0], p.Splits.Count - 1));
            UpdateRow(lvProg, SelIdx(lvProg), ProgRow(SelIdx(lvProg)));
        }

        private void SplitAddSample()
        {
            var p = SelProgram();
            if (p == null) { Status("Select a program first."); return; }
            var picks = Pick("Add split(s) to the program - choose the sample(s)", PickInfoMulti, true, -1);
            if (picks == null) return;
            foreach (var pk in picks) p.Splits.Add(MakeSplitForVag(pk));
            dirty = true;
            RefreshPars(-1); RefreshSets(-1);
            RefreshSplits(p.Splits.Count - 1);
            UpdateRow(lvProg, SelIdx(lvProg), ProgRow(SelIdx(lvProg)));
            Status(picks.Count + " split(s) added.");
        }

        /// <summary>Changes which VAG a split plays. Makes a NEW param + set so other programs that share the old ones are not touched.</summary>
        private void SplitPick()
        {
            var p = SelProgram(); int j = SelIdx(lvSplit);
            if (p == null || j < 0 || j >= p.Splits.Count) { Status("Select a split first."); return; }
            var sp = p.Splits[j];

            HdSet oldSet = (sp.SetIndex >= 0 && sp.SetIndex < hf.Sets.Count) ? hf.Sets[sp.SetIndex] : null;
            HdParam basis = null;
            int cur = -1;
            if (oldSet != null && oldSet.Params.Count > 0)
            {
                int pi = oldSet.Params[0];
                if (pi < hf.Params.Count && hf.Params[pi] != null) { basis = hf.Params[pi]; cur = basis.VagIndex; }
            }

            var picks = Pick("Split " + j + " - choose the sample it plays",
                "Select the VAG this split should play, then OK.\r\nA new sample param and sample set are made for it (other programs are not changed).", false, cur);
            if (picks == null || picks.Count == 0) return;

            var pr = new HdParam { Raw = basis != null ? (byte[])basis.Raw.Clone() : (byte[])TplParam.Clone() };
            pr.VagIndex = picks[0].Vag;
            int npi = PutParam(pr);
            var st = new HdSet();
            if (oldSet != null) { st.VelCurve = oldSet.VelCurve; st.VelLow = oldSet.VelLow; st.VelHigh = oldSet.VelHigh; }
            st.Params.Add((ushort)npi);
            sp.SetIndex = PutSet(st);

            dirty = true;
            RefreshPars(-1); RefreshSets(-1);
            RefreshSplits(j);
            Status("Split " + j + " now plays VAG " + picks[0].Vag + " (new param " + npi + ", set " + sp.SetIndex + ").");
        }

        private void SplitMove(int dir)
        {
            var p = SelProgram();
            if (p == null) return;
            int j = MoveSel(p.Splits, lvSplit, dir, null);
            if (j >= 0) RefreshSplits(j);
        }

        // ================================================================= check / apply
        private new List<string> Validate()
        {
            var r = new List<string>();
            int used = 0;
            for (int i = 0; i < vags.Count; i++)
            {
                var v = vags[i];
                if (v == null) continue;
                used++;
                if (v.Rate < 1000) r.Add("VAG " + i + ": sample rate " + v.Rate + " Hz looks wrong.");
                if (v.Raw.Length < 16 || v.Raw.Length % 16 != 0) r.Add("VAG " + i + ": size " + v.Raw.Length + " is not a multiple of 16 bytes.");
            }
            if (used == 0) r.Add("The bank has no VAGs.");
            for (int i = 0; i < hf.Params.Count; i++)
            {
                var p = hf.Params[i];
                if (p == null) continue;
                int vi = p.VagIndex;
                if (vi >= vags.Count || vags[vi] == null) r.Add("Sample param " + i + " uses VAG " + vi + ", which is empty or missing.");
            }
            for (int i = 0; i < hf.Sets.Count; i++)
            {
                var s = hf.Sets[i];
                if (s == null) continue;
                if (s.Params.Count == 0) r.Add("Sample set " + i + " lists no sample params.");
                foreach (int pi in s.Params)
                    if (pi >= hf.Params.Count || hf.Params[pi] == null) r.Add("Sample set " + i + " lists sample param " + pi + ", which is empty or missing.");
            }
            for (int i = 0; i < hf.Programs.Count; i++)
            {
                var pr = hf.Programs[i];
                if (pr == null) continue;
                if (pr.Splits.Count > 255) r.Add("Program " + i + " has more than 255 splits.");
                for (int j = 0; j < pr.Splits.Count; j++)
                {
                    var sp = pr.Splits[j];
                    if (sp.SetIndex >= hf.Sets.Count || hf.Sets[sp.SetIndex] == null) r.Add("Program " + i + " split " + j + " uses sample set " + sp.SetIndex + ", which is empty or missing.");
                    if (sp.KeyLow > sp.KeyHigh) r.Add("Program " + i + " split " + j + ": low key is above the high key.");
                }
            }
            return r;
        }

        private void OnCheck()
        {
            var r = Validate();
            if (r.Count == 0) { MessageBox.Show(this, "No problems found: every link (program → set → param → VAG) is valid.", "Check links"); return; }
            MessageBox.Show(this, r.Count + " problem(s):\n\n" + string.Join("\n", r.Take(25)) + (r.Count > 25 ? "\n..." : ""), "Check links", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private bool TryBuild(out byte[] hd, out byte[] bd, out string error)
        {
            hd = null; bd = null; error = null;
            try
            {
                EnsureSlots();
                var uniq = new List<KeyValuePair<long, byte[]>>();
                var seen = new HashSet<byte[]>();
                foreach (var v in vags) if (v != null && seen.Add(v.Raw)) uniq.Add(new KeyValuePair<long, byte[]>(v.Order, v.Raw));
                uniq.Sort((a, b) => a.Key.CompareTo(b.Key));
                var offs = new Dictionary<byte[], uint>();
                long pos = lead.Length;
                foreach (var kv in uniq) { offs[kv.Value] = (uint)pos; pos += kv.Value.Length; }
                if (pos > 0x7FFFFFF0L) throw new InvalidDataException("The BD would be larger than 2 GB.");
                var nbd = new byte[pos];
                Array.Copy(lead, nbd, lead.Length);
                foreach (var kv in uniq) Array.Copy(kv.Value, 0, nbd, offs[kv.Value], kv.Value.Length);

                hf.Vags.Clear();
                foreach (var v in vags)
                    hf.Vags.Add(v == null ? null : new HdVag { BdOffset = offs[v.Raw], Rate = v.Rate, Attribute = v.Attr, Pad = v.Pad });

                byte[] nhd = hf.Build((uint)nbd.Length);
                HdFile.Parse(nhd);                      // sanity: the result must read back
                hd = nhd; bd = nbd;
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private void OnApply()
        {
            var issues = Validate();
            if (issues.Count > 0)
            {
                var r = MessageBox.Show(this, issues.Count + " problem(s) found:\n\n" + string.Join("\n", issues.Take(15)) + (issues.Count > 15 ? "\n..." : "") + "\n\nApply anyway?",
                    "PSF2 Studio", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }
            byte[] hd, bd; string err;
            if (!TryBuild(out hd, out bd, out err))
            {
                MessageBox.Show(this, err, "PSF2 Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            ResultHd = hd; ResultBd = bd;
            DialogResult = DialogResult.OK;
        }
    }

    // ===================================================================== VAG picker dialog
    internal sealed class VagPickResult
    {
        public int Vag;
        public int KeyLow = 0;
        public int KeyHigh = 127;
        public int BaseNote = 60;
    }

    /// <summary>Lists the VAGs of the bank. Multi mode: tick VAGs and give each a key range. Single mode: pick one VAG.</summary>
    internal sealed class VagPickerForm : Form
    {
        private readonly DataGridView grid;
        private readonly bool multi;
        public readonly List<VagPickResult> Result = new List<VagPickResult>();

        public VagPickerForm(string title, string info, List<string[]> rows, bool multi, int preselect)
        {
            this.multi = multi;
            Text = title;
            Size = new Size(820, 500);
            MinimumSize = new Size(640, 360);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Ui.Bg;
            ForeColor = Color.White;
            try { AppIcon.Apply(this); } catch { }

            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                BackgroundColor = Ui.Edit,
                GridColor = Color.FromArgb(70, 70, 85),
                BorderStyle = BorderStyle.None,
                EnableHeadersVisualStyles = false,
                MultiSelect = false,
                SelectionMode = multi ? DataGridViewSelectionMode.CellSelect : DataGridViewSelectionMode.FullRowSelect,
                EditMode = multi ? DataGridViewEditMode.EditOnEnter : DataGridViewEditMode.EditProgrammatically,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
            };
            grid.ColumnHeadersDefaultCellStyle.BackColor = Ui.Panel;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Ui.Panel;
            grid.DefaultCellStyle.BackColor = Ui.Edit;
            grid.DefaultCellStyle.ForeColor = Color.White;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(60, 90, 140);
            grid.DefaultCellStyle.SelectionForeColor = Color.White;

            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "Use", Width = 45 });
            AddText("vag", "VAG", 55, true);
            AddText("rate", "Rate", 70, true);
            AddText("loop", "Loop", 60, true);
            AddText("len", "Length", 80, true);
            AddText("lo", "Key low", 75, false);
            AddText("hi", "Key high", 75, false);
            AddText("base", "Base note", 80, false);

            int selRow = -1;
            foreach (var r in rows)
            {
                int slot = int.Parse(r[0]);
                bool tick = multi && (slot == preselect || rows.Count == 1);
                int ri = grid.Rows.Add(tick, r[0], r[1], r[2], r[4], "0", "127", "60");
                if (slot == preselect || (selRow < 0)) selRow = ri;
            }

            if (!multi)
            {
                grid.Columns["use"].Visible = false;
                grid.Columns["lo"].Visible = false;
                grid.Columns["hi"].Visible = false;
                grid.Columns["base"].Visible = false;
                grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) OnOk(); };
            }

            var lbl = new Label { Dock = DockStyle.Top, Height = 62, ForeColor = Color.Gainsboro, Padding = new Padding(8, 6, 8, 0), Text = info };

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, BackColor = Ui.Panel, Padding = new Padding(6, 6, 6, 0) };
            if (multi)
            {
                bottom.Controls.Add(MakeBtn("☑ Tick all", 100, (s, e) => TickAll()));
                bottom.Controls.Add(MakeBtn("🎹 Whole keyboard", 150, (s, e) => SetRanges(false)));
                bottom.Controls.Add(MakeBtn("↔ Split keyboard evenly", 200, (s, e) => SetRanges(true)));
            }
            bottom.Controls.Add(MakeBtn("OK", 90, (s, e) => OnOk()));
            bottom.Controls.Add(MakeBtn("Cancel", 90, (s, e) => { DialogResult = DialogResult.Cancel; }));

            Controls.Add(grid);
            Controls.Add(lbl);
            Controls.Add(bottom);

            Shown += (s, e) =>
            {
                if (selRow >= 0 && selRow < grid.Rows.Count)
                {
                    grid.ClearSelection();
                    grid.CurrentCell = grid.Rows[selRow].Cells[multi ? "lo" : "vag"];
                    if (!multi) grid.Rows[selRow].Selected = true;
                }
            };
        }

        private void AddText(string name, string head, int w, bool ro)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = head, Width = w, ReadOnly = ro, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        private static Button MakeBtn(string text, int w, EventHandler click)
        {
            var b = Ui.Btn(text, w);
            b.Click += click;
            return b;
        }

        private static bool IsTicked(DataGridViewRow r)
        {
            object o = r.Cells["use"].Value;
            return o is bool && (bool)o;
        }

        private void TickAll()
        {
            grid.EndEdit();
            foreach (DataGridViewRow r in grid.Rows) r.Cells["use"].Value = true;
        }

        private void SetRanges(bool even)
        {
            grid.EndEdit();
            var ticked = grid.Rows.Cast<DataGridViewRow>().Where(IsTicked).ToList();
            if (ticked.Count == 0) { MessageBox.Show(this, "Tick at least one VAG first.", "Pick samples"); return; }
            int n = ticked.Count;
            for (int k = 0; k < n; k++)
            {
                int lo = even ? k * 128 / n : 0;
                int hi = even ? (k + 1) * 128 / n - 1 : 127;
                ticked[k].Cells["lo"].Value = lo.ToString();
                ticked[k].Cells["hi"].Value = hi.ToString();
            }
        }

        private static bool Num(DataGridViewRow r, string col, out int v)
        {
            string t = (Convert.ToString(r.Cells[col].Value) ?? "").Trim();
            return int.TryParse(t, out v) && v >= 0 && v <= 127;
        }

        private void OnOk()
        {
            grid.EndEdit();
            Result.Clear();
            if (multi)
            {
                foreach (DataGridViewRow r in grid.Rows)
                {
                    if (!IsTicked(r)) continue;
                    string vagText = Convert.ToString(r.Cells["vag"].Value);
                    int lo, hi, bn;
                    if (!Num(r, "lo", out lo) || !Num(r, "hi", out hi) || !Num(r, "base", out bn))
                    {
                        MessageBox.Show(this, "VAG " + vagText + ": key low, key high and base note must be whole numbers 0 - 127.", "Pick samples");
                        Result.Clear();
                        return;
                    }
                    if (lo > hi)
                    {
                        MessageBox.Show(this, "VAG " + vagText + ": key low is above key high.", "Pick samples");
                        Result.Clear();
                        return;
                    }
                    Result.Add(new VagPickResult { Vag = int.Parse(vagText), KeyLow = lo, KeyHigh = hi, BaseNote = bn });
                }
                if (Result.Count == 0) { MessageBox.Show(this, "Tick at least one VAG.", "Pick samples"); return; }
            }
            else
            {
                var row = grid.CurrentRow;
                if (row == null) { MessageBox.Show(this, "Select a VAG.", "Pick sample"); return; }
                Result.Add(new VagPickResult { Vag = int.Parse(Convert.ToString(row.Cells["vag"].Value)) });
            }
            DialogResult = DialogResult.OK;
        }
    }

    // ===================================================================== property-grid views
    internal static class SdBytes
    {
        public static int B(byte[] r, int o) { return o < r.Length ? r[o] : 0; }
        public static void SetB(byte[] r, int o, int v) { if (o < r.Length) r[o] = (byte)Math.Max(0, Math.Min(255, v)); }
        public static int W(byte[] r, int o) { return o + 1 < r.Length ? r[o] | (r[o + 1] << 8) : 0; }
        public static void SetW(byte[] r, int o, int v)
        {
            v = Math.Max(0, Math.Min(65535, v));
            if (o + 1 < r.Length) { r[o] = (byte)v; r[o + 1] = (byte)(v >> 8); }
        }
        public static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", " "); }
        public static void SetHex(byte[] target, string s)
        {
            string t = (s ?? "").Replace(" ", "").Replace("-", "").Replace(",", "");
            if (t.Length != target.Length * 2) throw new FormatException("Expected exactly " + target.Length + " bytes (" + target.Length * 2 + " hex digits).");
            var nb = new byte[target.Length];
            for (int i = 0; i < nb.Length; i++) nb[i] = Convert.ToByte(t.Substring(i * 2, 2), 16);
            Array.Copy(nb, target, nb.Length);
        }
        public static int Hex16(string s)
        {
            int v = Convert.ToInt32((s ?? "").Trim(), 16);
            if (v < 0 || v > 0xFFFF) throw new FormatException("Enter up to 4 hex digits (0000 - FFFF).");
            return v;
        }
        public static string AdsrText(int a, int s)
        {
            string atk = (((a >> 15) & 1) == 1 ? "exp" : "lin") + " shift " + ((a >> 10) & 31) + " step " + ((a >> 8) & 3);
            string sus = (((s >> 15) & 1) == 1 ? "exp " : "lin ") + (((s >> 14) & 1) == 1 ? "decrease" : "increase") + " shift " + ((s >> 8) & 31) + " step " + ((s >> 6) & 3);
            string rel = (((s >> 5) & 1) == 1 ? "exp" : "lin") + " shift " + (s & 31);
            return "Attack " + atk + " | Decay shift " + ((a >> 4) & 15) + " | Sustain level " + (a & 15) + " | Sustain " + sus + " | Release " + rel;
        }
    }

    internal sealed class SdParamView
    {
        private readonly byte[] r;
        public SdParamView(HdParam p) { r = p.Raw; }

        [Description("Number of the VAG (slot on the VAGs tab) this sample plays.")]
        public int VagIndex { get { return SdBytes.W(r, 0); } set { SdBytes.SetW(r, 0, value); } }
        [Description("Lowest velocity this sample answers to.")]
        public int VelLow { get { return SdBytes.B(r, 2); } set { SdBytes.SetB(r, 2, value); } }
        [Description("Highest velocity this sample answers to.")]
        public int VelHigh { get { return SdBytes.B(r, 4); } set { SdBytes.SetB(r, 4, value); } }
        [Description("MIDI note at which the VAG plays at its own pitch (60 = C4).")]
        public int BaseNote { get { return SdBytes.B(r, 11); } set { SdBytes.SetB(r, 11, value); } }
        [Description("Fine tune.")]
        public int Detune { get { return SdBytes.B(r, 12); } set { SdBytes.SetB(r, 12, value); } }
        [Description("Pan (64 = centre).")]
        public int Pan { get { return SdBytes.B(r, 13); } set { SdBytes.SetB(r, 13, value); } }
        [Description("Volume.")]
        public int Volume { get { return SdBytes.B(r, 16); } set { SdBytes.SetB(r, 16, value); } }
        [DisplayName("Adsr1 (hex)"), Description("SPU2 ADSR register 1: attack mode / shift / step, decay shift, sustain level.")]
        public string Adsr1Hex { get { return SdBytes.W(r, 18).ToString("X4"); } set { SdBytes.SetW(r, 18, SdBytes.Hex16(value)); } }
        [DisplayName("Adsr2 (hex)"), Description("SPU2 ADSR register 2: sustain mode / direction / shift / step, release mode / shift.")]
        public string Adsr2Hex { get { return SdBytes.W(r, 20).ToString("X4"); } set { SdBytes.SetW(r, 20, SdBytes.Hex16(value)); } }
        [ReadOnly(true), Description("The ADSR registers decoded (read only).")]
        public string AdsrInfo { get { return SdBytes.AdsrText(SdBytes.W(r, 18), SdBytes.W(r, 20)); } }
        [DisplayName("All 42 bytes (hex)"), Description("Every byte of the entry, for the fields that have no name here.")]
        public string RawHex { get { return SdBytes.Hex(r); } set { SdBytes.SetHex(r, value); } }
    }

    internal sealed class SdSetView
    {
        private readonly HdSet s;
        public SdSetView(HdSet set) { s = set; }

        [Description("Velocity curve byte.")]
        public int VelCurve { get { return s.VelCurve; } set { s.VelCurve = (byte)Math.Max(0, Math.Min(255, value)); } }
        public int VelLow { get { return s.VelLow; } set { s.VelLow = (byte)Math.Max(0, Math.Min(255, value)); } }
        public int VelHigh { get { return s.VelHigh; } set { s.VelHigh = (byte)Math.Max(0, Math.Min(255, value)); } }
        [DisplayName("Sample param numbers"), Description("Numbers of the sample params (Sample params tab) this set plays together, separated by commas.")]
        public string ParamIndexes
        {
            get { return string.Join(", ", s.Params); }
            set
            {
                var l = new List<ushort>();
                foreach (var t in (value ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int n;
                    if (!int.TryParse(t, out n) || n < 0 || n > 65535) throw new FormatException("'" + t + "' is not a sample param number (0 - 65535).");
                    l.Add((ushort)n);
                }
                if (l.Count > 255) throw new FormatException("A sample set holds at most 255 params.");
                s.Params = l;
            }
        }
    }

    internal sealed class SdProgView
    {
        private readonly HdProgram p;
        public SdProgView(HdProgram prog) { p = prog; }

        public int Volume { get { return SdBytes.B(p.Header, 6); } set { SdBytes.SetB(p.Header, 6, value); } }
        public int Pan { get { return SdBytes.B(p.Header, 7); } set { SdBytes.SetB(p.Header, 7, value); } }
        public int Transpose { get { return SdBytes.B(p.Header, 8); } set { SdBytes.SetB(p.Header, 8, value); } }
        public int Detune { get { return SdBytes.B(p.Header, 9); } set { SdBytes.SetB(p.Header, 9, value); } }
        [ReadOnly(true), Description("Edit the splits in the list below.")]
        public int SplitCount { get { return p.Splits.Count; } }
        [DisplayName("Header bytes (hex)"), Description("The program header. The first 6 bytes (offset, split count, split size) are rewritten automatically.")]
        public string HeaderHex { get { return SdBytes.Hex(p.Header); } set { SdBytes.SetHex(p.Header, value); } }
    }

    internal sealed class SdSplitView
    {
        private readonly HdSplit sp;
        public SdSplitView(HdSplit split) { sp = split; }

        [Description("Number of the sample set (Sample sets tab) played for this key range.")]
        public int SetIndex { get { return sp.SetIndex; } set { sp.SetIndex = Math.Max(0, Math.Min(65535, value)); } }
        [Description("Lowest MIDI note of the split.")]
        public int KeyLow { get { return sp.KeyLow; } set { sp.KeyLow = Math.Max(0, Math.Min(127, value)); } }
        [Description("Highest MIDI note of the split.")]
        public int KeyHigh { get { return sp.KeyHigh; } set { sp.KeyHigh = Math.Max(0, Math.Min(127, value)); } }
        public int Volume { get { return SdBytes.B(sp.Raw, 16); } set { SdBytes.SetB(sp.Raw, 16, value); } }
        public int Pan { get { return SdBytes.B(sp.Raw, 17); } set { SdBytes.SetB(sp.Raw, 17, value); } }
        public int Transpose { get { return SdBytes.B(sp.Raw, 18); } set { SdBytes.SetB(sp.Raw, 18, value); } }
        public int Detune { get { return SdBytes.B(sp.Raw, 19); } set { SdBytes.SetB(sp.Raw, 19, value); } }
        [DisplayName("All 20 bytes (hex)"), Description("Every byte of the split, for the fields that have no name here.")]
        public string RawHex { get { return SdBytes.Hex(sp.Raw); } set { SdBytes.SetHex(sp.Raw, value); } }
    }
}
