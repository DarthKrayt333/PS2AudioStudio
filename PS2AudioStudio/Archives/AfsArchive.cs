using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PS2AudioStudio.Audio;

namespace PS2AudioStudio.Archives
{
    public class AfsEntry
    {
        public int Index { get; set; }
        public uint Offset { get; set; }                 // offset in the ORIGINAL archive (informational)
        public uint Size => (uint)(Data?.Length ?? 0);
        public string FileName { get; set; }             // may contain folders: sound/ok_adx/adx/bg18_01.adx
        public byte[] Data { get; set; }
        /// <summary>Raw 48-byte filename-table record (32 byte name, 16 byte date + size) or null.</summary>
        public byte[] NameRecord { get; set; }
        public uint OrigSize { get; set; }               // size when the archive was read / last saved
        public bool Modified { get; set; }
        public FileKind Kind => AudioIO.Detect(Data, FileName);
    }

    /// <summary>
    /// AFS archive reader / writer (DKZ-Studio style).
    ///   "AFS\0", u32 count, count * (u32 offset, u32 size), then (optionally) u32 nameTableOffset, u32 nameTableSize.
    ///   Name table: count * 0x30 bytes = 32 byte name (may be a path, no terminator when exactly 32) + 16 bytes of date / other data.
    /// Files are 0x800 aligned.
    /// </summary>
    public class AfsArchive
    {
        public List<AfsEntry> Entries { get; } = new List<AfsEntry>();
        public bool HasNames { get; set; }
        public string Path { get; private set; }
        public bool Modified { get; set; }
        public int DataStart { get; private set; } = 0x800;
        private bool _namePtrAtEnd;                       // pointer lives at dataStart-8 instead of right after the TOC

        public const int NameBytesMax = 32;

        public void New()
        {
            Entries.Clear();
            HasNames = true;
            Path = null;
            DataStart = 0x800;
            _namePtrAtEnd = false;
            Modified = true;
        }

        private static uint U32(byte[] d, long o) => BitConverter.ToUInt32(d, (int)o);

        public static byte[] NameToBytes(string name)
        {
            return Encoding.Latin1.GetBytes(name ?? "");
        }

        public void Open(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            if (data.Length < 8 || data[0] != 'A' || data[1] != 'F' || data[2] != 'S' || data[3] != 0)
                throw new InvalidDataException("Not a valid AFS archive (missing 'AFS\\0').");

            int count = (int)U32(data, 4);
            if (count < 0 || count > 200000 || 8L + count * 8L > data.Length)
                throw new InvalidDataException("AFS file table is corrupt.");

            var offs = new uint[count];
            var sizes = new uint[count];
            uint firstData = uint.MaxValue;
            for (int i = 0; i < count; i++)
            {
                offs[i] = U32(data, 8 + i * 8);
                sizes[i] = U32(data, 12 + i * 8);
                if (sizes[i] > 0 && offs[i] < firstData) firstData = offs[i];
            }
            if (firstData == uint.MaxValue) firstData = (uint)Math.Min(data.Length, 0x800);

            // ---- filename table pointer ----
            long tocEnd = 8 + count * 8L;
            long nameOff = -1, nameSize = 0;
            bool atEnd = false;
            long[] candidates = { tocEnd, (long)firstData - 8 };
            for (int ci = 0; ci < candidates.Length; ci++)
            {
                long p = candidates[ci];
                if (p < tocEnd || p + 8 > data.Length) continue;
                long no = U32(data, p), ns = U32(data, p + 4);
                if (no >= tocEnd && ns >= count * 0x30L && no + count * 0x30L <= data.Length && ns < 0x4000000)
                {
                    nameOff = no; nameSize = ns; atEnd = ci == 1;
                    break;
                }
            }

            Entries.Clear();
            for (int i = 0; i < count; i++)
            {
                long o = offs[i], s = sizes[i];
                if (o > data.Length) { o = data.Length; s = 0; }
                if (o + s > data.Length) s = data.Length - o;
                byte[] fd = new byte[s];
                Array.Copy(data, o, fd, 0, s);

                string name = null;
                byte[] rec = null;
                if (nameOff >= 0)
                {
                    rec = new byte[0x30];
                    Array.Copy(data, nameOff + i * 0x30L, rec, 0, 0x30);
                    int n = 0;
                    while (n < NameBytesMax && rec[n] != 0) n++;
                    if (n > 0) name = Encoding.Latin1.GetString(rec, 0, n);
                }
                if (string.IsNullOrWhiteSpace(name))
                    name = $"{i:D4}" + AudioIO.ExtensionFor(AudioIO.Detect(fd, null));

                Entries.Add(new AfsEntry { Index = i, Offset = offs[i], FileName = name, Data = fd, NameRecord = rec, OrigSize = (uint)fd.Length });
            }

            HasNames = nameOff >= 0;
            _namePtrAtEnd = atEnd;
            DataStart = (int)firstData;
            Path = filePath;
            Modified = false;
        }

        public void Save(string filePath)
        {
            int count = Entries.Count;

            // names are stored in 32 bytes: refuse to cut them silently
            if (HasNames)
            {
                for (int i = 0; i < count; i++)
                {
                    int len = NameToBytes(Entries[i].FileName).Length;
                    if (len > NameBytesMax)
                        throw new InvalidDataException($"The name of entry #{i} is {len} bytes long, but the AFS name table only holds {NameBytesMax}:\n{Entries[i].FileName}\n\nRename it (shorter) and save again.");
                }
            }

            long headerSize = 8 + count * 8L + (HasNames ? 8 : 0);
            long minStart = (headerSize + 0x7FF) / 0x800 * 0x800;
            long dataStart = Math.Max(DataStart, minStart);
            dataStart = (dataStart + 0x7FF) / 0x800 * 0x800;

            // layout
            var offsets = new long[count];
            long cur = dataStart;
            for (int i = 0; i < count; i++)
            {
                offsets[i] = cur;
                cur += Entries[i].Data.Length;
                cur = (cur + 0x7FF) / 0x800 * 0x800;
            }
            long nameTableOff = cur;

            string tmp = filePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("AFS\0"));
                bw.Write(count);
                for (int i = 0; i < count; i++)
                {
                    bw.Write((uint)offsets[i]);
                    bw.Write((uint)Entries[i].Data.Length);
                }
                long ptrPos = _namePtrAtEnd ? dataStart - 8 : 8 + count * 8L;
                if (HasNames)
                {
                    while (fs.Position < ptrPos) bw.Write((byte)0);
                    bw.Write((uint)nameTableOff);
                    bw.Write((uint)(count * 0x30));
                }
                while (fs.Position < dataStart) bw.Write((byte)0);

                for (int i = 0; i < count; i++)
                {
                    bw.Write(Entries[i].Data);
                    while (fs.Position % 0x800 != 0) bw.Write((byte)0);
                }

                if (HasNames)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var e = Entries[i];
                        byte[] rec = new byte[0x30];
                        bool hadRecord = e.NameRecord != null && e.NameRecord.Length >= 0x30;
                        if (hadRecord) Array.Copy(e.NameRecord, rec, 0x30);
                        else
                        {
                            var now = DateTime.Now;
                            Array.Copy(BitConverter.GetBytes((ushort)now.Year), 0, rec, 32, 2);
                            Array.Copy(BitConverter.GetBytes((ushort)now.Month), 0, rec, 34, 2);
                            Array.Copy(BitConverter.GetBytes((ushort)now.Day), 0, rec, 36, 2);
                            Array.Copy(BitConverter.GetBytes((ushort)now.Hour), 0, rec, 38, 2);
                            Array.Copy(BitConverter.GetBytes((ushort)now.Minute), 0, rec, 40, 2);
                            Array.Copy(BitConverter.GetBytes((ushort)now.Second), 0, rec, 42, 2);
                        }
                        Array.Clear(rec, 0, 32);
                        byte[] nb = NameToBytes(e.FileName);
                        Array.Copy(nb, rec, Math.Min(NameBytesMax, nb.Length));

                        // the last 4 bytes are only updated when they really were this entry's size
                        // (in some archives they hold other data and must stay untouched)
                        bool keepField = hadRecord && BitConverter.ToUInt32(rec, 44) != e.OrigSize;
                        if (!keepField)
                            Array.Copy(BitConverter.GetBytes((uint)e.Data.Length), 0, rec, 44, 4);

                        e.NameRecord = rec;
                        bw.Write(rec);
                    }
                    while (fs.Position % 0x800 != 0) bw.Write((byte)0);
                }
            }

            if (File.Exists(filePath)) File.Delete(filePath);
            File.Move(tmp, filePath);

            for (int i = 0; i < count; i++)
            {
                Entries[i].Offset = (uint)offsets[i];
                Entries[i].Index = i;
                Entries[i].Modified = false;
                Entries[i].OrigSize = (uint)Entries[i].Data.Length;
            }
            DataStart = (int)dataStart;
            Path = filePath;
            Modified = false;
        }

        // ================================================================= names / folders
        /// <summary>"sound/ok_adx/adx/a.adx" -> "sound\ok_adx\adx\a.adx" with every part made safe for Windows.</summary>
        public static string SafeRelativePath(string name)
        {
            var parts = new List<string>();
            foreach (var raw in (name ?? "").Replace('\\', '/').Split('/'))
            {
                string s = raw.Trim();
                if (s.Length == 0 || s == "." || s == "..") continue;
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
                parts.Add(s);
            }
            return parts.Count == 0 ? "unnamed" : System.IO.Path.Combine(parts.ToArray());
        }

        /// <summary>Only the file name without its folders: "sound/ok_adx/adx/a.adx" -> "a.adx".</summary>
        public static string LeafName(string name)
        {
            return System.IO.Path.GetFileName(SafeRelativePath(name));
        }

        public static bool HasFolder(string name)
        {
            return name != null && name.IndexOfAny(new[] { '/', '\\' }) >= 0;
        }

        /// <summary>Extracts everything. With keepFolders the folders stored in the names are recreated. Returns the file count.</summary>
        public int ExtractAll(string outputDir, bool keepFolders = true)
        {
            Directory.CreateDirectory(outputDir);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var e in Entries)
            {
                string rel = keepFolders ? SafeRelativePath(e.FileName) : LeafName(e.FileName);
                if (!used.Add(rel))
                {
                    string dir = System.IO.Path.GetDirectoryName(rel) ?? "";
                    rel = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(rel) + "_" + e.Index + System.IO.Path.GetExtension(rel));
                    used.Add(rel);
                }
                string full = System.IO.Path.Combine(outputDir, rel);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
                File.WriteAllBytes(full, e.Data);
                n++;
            }
            return n;
        }

        public static string SafeName(string n)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n;
        }

        public void ReplaceData(int index, byte[] data)
        {
            Entries[index].Data = data;
            Entries[index].Modified = true;
            Modified = true;
        }

        public AfsEntry Add(string name, byte[] data)
        {
            var e = new AfsEntry { Index = Entries.Count, FileName = name, Data = data, Modified = true, OrigSize = (uint)data.Length };
            Entries.Add(e);
            Modified = true;
            return e;
        }

        public void RemoveAt(int index)
        {
            Entries.RemoveAt(index);
            for (int i = 0; i < Entries.Count; i++) Entries[i].Index = i;
            Modified = true;
        }
    }
}
