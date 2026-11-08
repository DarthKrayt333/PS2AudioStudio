using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace PS2AudioStudio
{
    public static class DragOut
    {
        /// <summary>True while a drag started by this app is running. Drop handlers should ignore it, so you don't re-open your own files.</summary>
        public static bool Active { get; private set; }

        private static string Root => Path.Combine(Path.GetTempPath(), "PS2AudioStudio_drag");

        public static void Start(Control source, IEnumerable<KeyValuePair<string, Func<byte[]>>> files)
        {
            if (Active) return;
            string dir = Path.Combine(Root, Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var paths = new List<string>();
            foreach (var kv in files)
            {
                string name = Safe(kv.Key);
                string p = Path.Combine(dir, name);
                for (int n = 2; File.Exists(p); n++)
                    p = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "_" + n + Path.GetExtension(name));
                File.WriteAllBytes(p, kv.Value());
                paths.Add(p);
            }
            if (paths.Count == 0) return;

            Active = true;
            try { source.DoDragDrop(new DataObject(DataFormats.FileDrop, paths.ToArray()), DragDropEffects.Copy); }
            finally { Active = false; }
        }

        public static void CleanOld()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                foreach (var d in Directory.GetDirectories(Root))
                    if (Directory.GetCreationTime(d) < DateTime.Now.AddHours(-2)) Directory.Delete(d, true);
            }
            catch { }
        }

        private static string Safe(string n)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return string.IsNullOrWhiteSpace(n) ? "file.bin" : n;
        }
    }
}