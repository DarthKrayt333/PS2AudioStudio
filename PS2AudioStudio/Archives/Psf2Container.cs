using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PS2AudioStudio.Archives
{
    public class Psf2Entry
    {
        public string Name;
        public bool IsDirectory;
        public byte[] Data;                                       // files only
        public List<Psf2Entry> Children = new List<Psf2Entry>();  // directories only
    }

    /// <summary>Reads, unpacks and builds .psf2 containers (PSF v2: zlib-compressed virtual file system).</summary>
    public static class Psf2Container
    {
        const int EntrySize = 0x30;
        const int NameSize = 36;
        const int BlockSize = 0x2000;

        public static bool IsPsf2(byte[] d)
        {
            return d != null && d.Length >= 16 && d[0] == 'P' && d[1] == 'S' && d[2] == 'F' && d[3] == 0x02;
        }

        // ---------- read ----------
        public static Psf2Entry Read(byte[] d)
        {
            if (!IsPsf2(d)) throw new InvalidDataException("Not a PSF2 file.");
            uint resSize = BitConverter.ToUInt32(d, 4);
            if (16L + resSize > d.Length) throw new InvalidDataException("PSF2 reserved area is truncated.");
            byte[] res = new byte[resSize];
            Buffer.BlockCopy(d, 16, res, 0, (int)resSize);
            Psf2Entry root = new Psf2Entry { Name = "", IsDirectory = true };
            if (resSize >= 4) ReadDir(res, 0, root, 0);
            return root;
        }

        static void ReadDir(byte[] res, long off, Psf2Entry dir, int depth)
        {
            if (depth > 16) throw new InvalidDataException("PSF2 directory nesting too deep.");
            if (off < 0 || off + 4 > res.Length) throw new InvalidDataException("Bad PSF2 directory offset.");
            uint count = BitConverter.ToUInt32(res, (int)off);
            if (count > 100000 || off + 4 + (long)count * EntrySize > res.Length)
                throw new InvalidDataException("Bad PSF2 directory size.");
            for (int i = 0; i < count; i++)
            {
                int p = (int)(off + 4 + (long)i * EntrySize);
                int n = 0;
                while (n < NameSize && res[p + n] != 0) n++;
                string name = Encoding.ASCII.GetString(res, p, n);
                uint o = BitConverter.ToUInt32(res, p + 36);
                uint size = BitConverter.ToUInt32(res, p + 40);
                uint bs = BitConverter.ToUInt32(res, p + 44);
                Psf2Entry e = new Psf2Entry { Name = name };
                if (size == 0 && bs == 0)
                {
                    e.IsDirectory = true;
                    ReadDir(res, o, e, depth + 1);
                }
                else
                {
                    e.Data = ReadFile(res, o, size, bs);
                }
                dir.Children.Add(e);
            }
        }

        static byte[] ReadFile(byte[] res, uint off, uint size, uint bs)
        {
            if (bs == 0) throw new InvalidDataException("Bad PSF2 block size.");
            if (size > 512u * 1024u * 1024u) throw new InvalidDataException("PSF2 file too large.");
            long blocks = ((long)size + bs - 1) / bs;
            long dataPos = (long)off + blocks * 4;
            if (dataPos > res.Length) throw new InvalidDataException("Bad PSF2 block table.");
            byte[] outb = new byte[size];
            long written = 0;
            for (long b = 0; b < blocks; b++)
            {
                uint csz = BitConverter.ToUInt32(res, (int)(off + b * 4));
                if (dataPos + csz > res.Length) throw new InvalidDataException("PSF2 block is truncated.");
                int want = (int)Math.Min((long)bs, size - written);
                using (MemoryStream ms = new MemoryStream(res, (int)dataPos, (int)csz))
                using (ZLibStream z = new ZLibStream(ms, CompressionMode.Decompress))
                {
                    int got = 0;
                    while (got < want)
                    {
                        int r = z.Read(outb, (int)written + got, want - got);
                        if (r <= 0) break;
                        got += r;
                    }
                    if (got != want) throw new InvalidDataException("PSF2 block size mismatch.");
                }
                written += want;
                dataPos += csz;
            }
            return outb;
        }

        // ---------- helpers ----------
        public static List<KeyValuePair<string, Psf2Entry>> Files(Psf2Entry root)
        {
            List<KeyValuePair<string, Psf2Entry>> l = new List<KeyValuePair<string, Psf2Entry>>();
            Walk(root, "", l);
            return l;
        }

        static void Walk(Psf2Entry d, string prefix, List<KeyValuePair<string, Psf2Entry>> l)
        {
            foreach (Psf2Entry e in d.Children)
            {
                string p = prefix.Length == 0 ? e.Name : prefix + "/" + e.Name;
                if (e.IsDirectory) Walk(e, p, l);
                else l.Add(new KeyValuePair<string, Psf2Entry>(p, e));
            }
        }

        /// <summary>First file whose name ends with the extension (for example ".hd"), or null.</summary>
        public static Psf2Entry FindByExt(Psf2Entry root, string ext)
        {
            foreach (KeyValuePair<string, Psf2Entry> kv in Files(root))
                if (kv.Key.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        // ---------- unpack to folder ----------
        public static int ExtractTo(Psf2Entry root, string folder)
        {
            Directory.CreateDirectory(folder);
            return ExtractDir(root, folder);
        }

        static int ExtractDir(Psf2Entry dir, string path)
        {
            int n = 0;
            foreach (Psf2Entry e in dir.Children)
            {
                string p = Path.Combine(path, SafeName(e.Name));
                if (e.IsDirectory)
                {
                    Directory.CreateDirectory(p);
                    n += ExtractDir(e, p);
                }
                else
                {
                    File.WriteAllBytes(p, e.Data);
                    n++;
                }
            }
            return n;
        }

        static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            if (s.Length == 0 || s == "." || s == "..") s = "_";
            return s;
        }

        // ---------- build from folder ----------
        public static Psf2Entry FromFolder(string folder)
        {
            Psf2Entry root = new Psf2Entry { Name = "", IsDirectory = true };
            string[] dirs = Directory.GetDirectories(folder);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string d in dirs)
            {
                Psf2Entry c = FromFolder(d);
                c.Name = Path.GetFileName(d);
                root.Children.Add(c);
            }
            string[] files = Directory.GetFiles(folder);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
                root.Children.Add(new Psf2Entry { Name = Path.GetFileName(f), Data = File.ReadAllBytes(f) });
            return root;
        }

        // ---------- write ----------
        public static byte[] Write(Psf2Entry root)
        {
            MemoryStream res = new MemoryStream();
            WriteDir(root, res);
            byte[] resBytes = res.ToArray();

            MemoryStream o = new MemoryStream();
            o.Write(new byte[] { (byte)'P', (byte)'S', (byte)'F', 0x02 }, 0, 4);
            o.Write(BitConverter.GetBytes((uint)resBytes.Length), 0, 4); // reserved area size
            o.Write(BitConverter.GetBytes((uint)0), 0, 4);               // program size (empty)
            o.Write(BitConverter.GetBytes((uint)0), 0, 4);               // program CRC (empty)
            o.Write(resBytes, 0, resBytes.Length);
            return o.ToArray();
        }

        static long WriteDir(Psf2Entry dir, MemoryStream ms)
        {
            long tablePos = ms.Position;
            int count = dir.Children.Count;
            byte[] table = new byte[4 + count * EntrySize];
            ms.Write(table, 0, table.Length); // placeholder, patched below
            BitConverter.GetBytes((uint)count).CopyTo(table, 0);
            for (int i = 0; i < count; i++)
            {
                Psf2Entry e = dir.Children[i];
                byte[] nb = Encoding.ASCII.GetBytes(e.Name);
                if (nb.Length > NameSize - 1)
                    throw new InvalidDataException("Name too long for PSF2 (max 35 characters): " + e.Name);
                int p = 4 + i * EntrySize;
                Buffer.BlockCopy(nb, 0, table, p, nb.Length);
                uint off, size, bs;
                if (e.IsDirectory)
                {
                    off = (uint)WriteDir(e, ms);
                    size = 0;
                    bs = 0;
                }
                else
                {
                    off = (uint)ms.Position;
                    size = (uint)e.Data.Length;
                    bs = BlockSize;
                    WriteFile(e.Data, ms);
                }
                BitConverter.GetBytes(off).CopyTo(table, p + 36);
                BitConverter.GetBytes(size).CopyTo(table, p + 40);
                BitConverter.GetBytes(bs).CopyTo(table, p + 44);
            }
            long end = ms.Position;
            ms.Position = tablePos;
            ms.Write(table, 0, table.Length);
            ms.Position = end;
            return tablePos;
        }

        static void WriteFile(byte[] data, MemoryStream ms)
        {
            int blocks = (data.Length + BlockSize - 1) / BlockSize;
            List<byte[]> comp = new List<byte[]>();
            for (int b = 0; b < blocks; b++)
            {
                int len = Math.Min(BlockSize, data.Length - b * BlockSize);
                using (MemoryStream o = new MemoryStream())
                {
                    using (ZLibStream z = new ZLibStream(o, CompressionLevel.Optimal, true))
                        z.Write(data, b * BlockSize, len);
                    comp.Add(o.ToArray());
                }
            }
            foreach (byte[] c in comp) ms.Write(BitConverter.GetBytes((uint)c.Length), 0, 4);
            foreach (byte[] c in comp) ms.Write(c, 0, c.Length);
        }
    }

    /// <summary>Finds the BD / HD / SQ files that share a name in one folder.</summary>
    public class Psf2Set
    {
        public string Hd;
        public string Bd;
        public string Sq;

        public bool HasBank { get { return Hd != null && Bd != null; } }
        public bool HasSequence { get { return Sq != null; } }

        public static Psf2Set Find(string anyPath)
        {
            string full = Path.GetFullPath(anyPath);
            string dir = Path.GetDirectoryName(full);
            string stem = Path.GetFileNameWithoutExtension(full);
            Psf2Set s = new Psf2Set();
            s.Hd = Pick(dir, stem, ".hd");
            s.Bd = Pick(dir, stem, ".bd");
            s.Sq = Pick(dir, stem, ".sq");
            return s;
        }

        static string Pick(string dir, string stem, string ext)
        {
            string p = Path.Combine(dir, stem + ext);   // Windows paths are case-insensitive
            return File.Exists(p) ? p : null;
        }
    }
}
