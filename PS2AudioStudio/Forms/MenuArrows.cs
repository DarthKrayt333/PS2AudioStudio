using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PS2AudioStudio
{
    /// <summary>Makes the small submenu arrows white on the dark menu theme.</summary>
    internal static class MenuArrows
    {
        public static void Hook(MenuStrip m)
        {
            HookRenderer(m.Renderer);
            foreach (ToolStripItem it in m.Items) HookItem(it);
        }

        private static void HookItem(ToolStripItem it)
        {
            if (it is ToolStripDropDownItem d && d.HasDropDownItems)
            {
                HookRenderer(d.DropDown.Renderer);
                foreach (ToolStripItem c in d.DropDownItems) HookItem(c);
            }
        }

        private static void HookRenderer(ToolStripRenderer r)
        {
            if (r == null) return;
            r.RenderArrow -= OnArrow;
            r.RenderArrow += OnArrow;
        }

        private static void OnArrow(object sender, ToolStripArrowRenderEventArgs e)
        {
            if (e.Direction != ArrowDirection.Right) return;
            var r = e.ArrowRectangle;
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;
            var pts = new[]
            {
                new Point(cx - 3, cy - 5),
                new Point(cx + 3, cy),
                new Point(cx - 3, cy + 5)
            };
            var old = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var br = new SolidBrush(e.Item != null && !e.Item.Enabled ? Color.Gray : Color.White))
                e.Graphics.FillPolygon(br, pts);
            e.Graphics.SmoothingMode = old;
        }
    }
}
