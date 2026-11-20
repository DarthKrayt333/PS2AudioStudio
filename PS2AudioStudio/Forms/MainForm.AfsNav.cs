using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PS2AudioStudio
{
    public partial class MainForm
    {
        private readonly List<string> _afsBackStack = new List<string>();
        private readonly List<string> _afsFwdStack = new List<string>();
        private string _afsNavLast = "";
        private object _afsNavOwner;
        private Button _bAfsBack, _bAfsFwd;

        /// <summary>Called once from BuildAfsTab, after the folder pane exists.</summary>
        private void InstallAfsNav(Control mainBar)
        {
            Button up = FindAfsUpButton(splitAfs.Panel1);

            _bAfsBack = MakeNavButton("◀ Back", up);
            _bAfsFwd = MakeNavButton("Forward ▶", up);
            _bAfsBack.Click += (s, e) => AfsNavBack();
            _bAfsFwd.Click += (s, e) => AfsNavForward();

            if (up != null && up.Parent is FlowLayoutPanel)
            {
                Control parent = up.Parent;
                int at = parent.Controls.GetChildIndex(up);
                parent.Controls.Add(_bAfsBack);
                parent.Controls.Add(_bAfsFwd);
                parent.Controls.SetChildIndex(_bAfsBack, at);
                parent.Controls.SetChildIndex(_bAfsFwd, at + 1);
                up.Visible = false;
            }
            else
            {
                // could not find the folder toolbar: put the buttons at the start of the main AFS bar
                mainBar.Controls.Add(_bAfsBack);
                mainBar.Controls.Add(_bAfsFwd);
                mainBar.Controls.SetChildIndex(_bAfsBack, 0);
                mainBar.Controls.SetChildIndex(_bAfsFwd, 1);
            }

            KeyEventHandler keys = (s, e) =>
            {
                if (e.Alt && e.KeyCode == Keys.Left) { AfsNavBack(); e.Handled = true; }
                else if (e.Alt && e.KeyCode == Keys.Right) { AfsNavForward(); e.Handled = true; }
            };
            MouseEventHandler mouse = (s, e) =>
            {
                if (e.Button == MouseButtons.XButton1) AfsNavBack();
                else if (e.Button == MouseButtons.XButton2) AfsNavForward();
            };
            lvAfs.KeyDown += keys;
            lvAfs.MouseUp += mouse;
            if (tvAfs != null) { tvAfs.KeyDown += keys; tvAfs.MouseUp += mouse; }

            // every folder change (tree click, double-click, .. row) is noticed here and put into the history
            var tm = new System.Windows.Forms.Timer { Interval = 120 };
            tm.Tick += (s, e) => AfsNavSync();
            tm.Start();
            splitAfs.Disposed += (s, e) => tm.Dispose();
            AfsNavSync();
        }

        private static Button MakeNavButton(string text, Button like)
        {
            var b = new Button { Text = text, Enabled = false };
            if (like != null)
            {
                b.Size = new System.Drawing.Size(Math.Max(like.Width, 90), like.Height);
                b.Font = like.Font;
                b.FlatStyle = like.FlatStyle;
                b.BackColor = like.BackColor;
                b.ForeColor = like.ForeColor;
                b.Margin = like.Margin;
                b.UseVisualStyleBackColor = like.UseVisualStyleBackColor;
            }
            else b.Size = new System.Drawing.Size(90, 28);
            return b;
        }

        private static Button FindAfsUpButton(Control root)
        {
            foreach (Control c in root.Controls)
            {
                var b = c as Button;
                if (b != null && b.Text != null && b.Text.EndsWith("Up", StringComparison.Ordinal)) return b;
                var r = FindAfsUpButton(c);
                if (r != null) return r;
            }
            return null;
        }

        private void AfsNavSync()
        {
            if (_bAfsBack == null) return;
            if (!ReferenceEquals(_afsNavOwner, afs))        // another archive: new history
            {
                _afsBackStack.Clear(); _afsFwdStack.Clear();
                _afsNavOwner = afs;
                _afsNavLast = afsFolder ?? "";
            }
            string cur = afsFolder ?? "";
            if (cur != _afsNavLast)
            {
                _afsBackStack.Add(_afsNavLast);
                if (_afsBackStack.Count > 100) _afsBackStack.RemoveAt(0);
                _afsFwdStack.Clear();
                _afsNavLast = cur;
            }
            _bAfsBack.Enabled = _afsBackStack.Count > 0;
            _bAfsFwd.Enabled = _afsFwdStack.Count > 0;
        }

        private void AfsNavBack()
        {
            AfsNavSync();
            if (_afsBackStack.Count == 0) return;
            string target = _afsBackStack[_afsBackStack.Count - 1];
            _afsBackStack.RemoveAt(_afsBackStack.Count - 1);
            _afsFwdStack.Add(afsFolder ?? "");
            AfsNavGo(target);
        }

        private void AfsNavForward()
        {
            AfsNavSync();
            if (_afsFwdStack.Count == 0) return;
            string target = _afsFwdStack[_afsFwdStack.Count - 1];
            _afsFwdStack.RemoveAt(_afsFwdStack.Count - 1);
            _afsBackStack.Add(afsFolder ?? "");
            AfsNavGo(target);
        }

        private void AfsNavGo(string folder)
        {
            afsFolder = folder;
            _afsNavLast = folder;
            PopulateAfs();
            AfsNavSync();
        }
    }
}
