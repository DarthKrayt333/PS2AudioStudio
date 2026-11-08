using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace PS2AudioStudio
{
    public static class AppIcon
    {
        private static Icon _icon;
        private static readonly ConditionalWeakTable<Form, object> _done = new ConditionalWeakTable<Form, object>();

        public static Icon Icon
        {
            get
            {
                if (_icon == null)
                {
                    try { _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                }
                return _icon;
            }
        }

        public static void Apply(Form f)
        {
            if (f == null || Icon == null) return;
            f.Icon = Icon;
        }

        /// <summary>Call once at startup: every form (also dialogs created later) gets the icon automatically.</summary>
        public static void HookAll()
        {
            Application.Idle += (s, e) =>
            {
                if (Icon == null) return;
                foreach (Form f in Application.OpenForms)
                {
                    if (_done.TryGetValue(f, out _)) continue;
                    f.Icon = Icon;
                    _done.Add(f, null);
                }
            };
        }
    }
}