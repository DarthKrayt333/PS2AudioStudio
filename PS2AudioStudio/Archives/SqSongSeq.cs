using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PS2AudioStudio.Archives
{
    public static class SqSongSeq
    {
        // ---------- SQ song -> Sony SEQ (pQES), written straight from the song events
        public static byte[] ToSeq(this SqSong song)
        {
            var ev = song.Events.Select((e, n) => new { e, n }).OrderBy(x => x.e.Tick).ThenBy(x => SeqOrder(x.e.Type)).ThenBy(x => x.n).Select(x => x.e).ToList();      // stable sort
            int tempo = 500000;
            int ti = ev.FindIndex(e => e.Type == SqEventType.Tempo);
            if (ti >= 0 && ev[ti].Tick == 0 && ev[ti].Tempo > 0) tempo = ev[ti].Tempo;   // header tempo
            else ti = -1;
            tempo = Math.Max(1, Math.Min(0xFFFFFF, tempo));

            var o = new List<byte>();
            o.AddRange(Encoding.ASCII.GetBytes("pQES"));
            o.AddRange(new byte[] { 0, 0, 0, 1 });                   // version 1 (big endian)
            o.Add((byte)(song.Ppqn >> 8)); o.Add((byte)song.Ppqn);   // resolution
            o.Add((byte)(tempo >> 16)); o.Add((byte)(tempo >> 8)); o.Add((byte)tempo);
            o.Add(4); o.Add(2);                                      // 4/4 (denominator = power of 2)

            long last = 0;
            int run = -1;
            for (int i = 0; i < ev.Count; i++)
            {
                if (i == ti) continue;
                var e = ev[i];
                Vlq(o, e.Tick - last); last = e.Tick;
                int st;
                switch (e.Type)
                {
                    case SqEventType.NoteOn:
                    case SqEventType.NoteOff:
                        st = 0x90 | (e.Channel & 15);                // note-off = note-on, velocity 0 (as real SEQs)
                        if (st != run) o.Add((byte)st); run = st;
                        o.Add((byte)(e.A & 0x7F));
                        o.Add((byte)(e.Type == SqEventType.NoteOff ? 0 : Math.Max(1, e.B & 0x7F)));
                        break;
                    case SqEventType.Cc:
                        st = 0xB0 | (e.Channel & 15);
                        if (st != run) o.Add((byte)st); run = st;
                        o.Add((byte)(e.A & 0x7F)); o.Add((byte)(e.B & 0x7F));
                        break;
                    case SqEventType.Program:
                        st = 0xC0 | (e.Channel & 15);
                        if (st != run) o.Add((byte)st); run = st;
                        o.Add((byte)(e.A & 0x7F));
                        break;
                    case SqEventType.PitchBend:
                        st = 0xE0 | (e.Channel & 15);
                        if (st != run) o.Add((byte)st); run = st;
                        o.Add((byte)(e.A & 0x7F)); o.Add((byte)(e.B & 0x7F));
                        break;
                    case SqEventType.Tempo:
                        int t = Math.Max(1, Math.Min(0xFFFFFF, e.Tempo));
                        o.AddRange(new byte[] { 0xFF, 0x51, 0x03, (byte)(t >> 16), (byte)(t >> 8), (byte)t });
                        run = -1;
                        break;
                }
            }
            o.Add(0); o.Add(0xFF); o.Add(0x2F); o.Add(0);
            return o.ToArray();
        }

        private static int SeqOrder(SqEventType t)
        {
            return t == SqEventType.NoteOff ? 0 : t == SqEventType.NoteOn ? 2 : 1;
        }

        private static void Vlq(List<byte> o, long v)
        {
            if (v < 0) v = 0;
            var s = new Stack<byte>();
            s.Push((byte)(v & 0x7F)); v >>= 7;
            while (v > 0) { s.Push((byte)((v & 0x7F) | 0x80)); v >>= 7; }
            o.AddRange(s);
        }

        // ---------- MIDI for playing together with an exported SF2 (Awave Studio etc.)
        // 1) channel 10 (index 9) is a drum channel in every SF2 player, so it is moved to a free channel
        // 2) the Sony loop markers (CC99 + the CC6/CC38 that follow) are NRPN messages: removed
        public static byte[] ToSmfForSoundFont(this SqSong song)
        {
            var c = new SqSong { Name = song.Name, Ppqn = song.Ppqn };
            var used = new HashSet<int>();
            foreach (var e in song.Events)
                if (e.Type != SqEventType.Tempo) used.Add(e.Channel & 15);
            int free = -1;
            for (int ch = 0; ch < 16; ch++) if (ch != 9 && !used.Contains(ch)) { free = ch; break; }

            var loopMarks = new List<KeyValuePair<int, long>>();
            foreach (var e in song.Events)
                if (e.Type == SqEventType.Cc && e.A == 99) loopMarks.Add(new KeyValuePair<int, long>(e.Channel, e.Tick));

            foreach (var e0 in song.Events)
            {
                var e = e0;
                if (e.Type == SqEventType.Cc)
                {
                    if (e.A == 99) continue;
                    if (e.A == 6 || e.A == 38)
                    {
                        bool drop = false;
                        foreach (var m in loopMarks)
                            if (m.Key == e.Channel && e.Tick - m.Value >= 0 && e.Tick - m.Value <= 3) { drop = true; break; }
                        if (drop) continue;
                    }
                }
                if (free >= 0 && e.Type != SqEventType.Tempo && (e.Channel & 15) == 9) e.Channel = (byte)free;
                c.Events.Add(e);
            }
            return c.ToSmf();
        }

        // ---------- any MIDI / SQ / SEQ file -> songs  (manual sequence loading, drag and drop)
        public static List<SqSong> LoadAny(byte[] d, string name)
        {
            if (d == null || d.Length < 8) throw new InvalidDataException("The file is empty or too small.");
            string h4 = Encoding.ASCII.GetString(d, 0, 4);
            if (h4 == "MThd") return new List<SqSong> { SqSong.FromSmf(d, name) };
            if (h4 == "IECS" && d.Length > 8 && Encoding.ASCII.GetString(d, 0, 8) == "IECSsreV")
            {
                var f = new SqFile();
                f.Parse(d);
                if (f.Songs.Count == 0) throw new InvalidDataException("The SQ file contains no songs.");
                return new List<SqSong>(f.Songs);
            }
            int p = -1;
            for (int i = 0; i + 4 <= d.Length; i++)
                if (d[i] == 'p' && d[i + 1] == 'Q' && d[i + 2] == 'E' && d[i + 3] == 'S') { p = i; break; }
            if (p >= 0)
            {
                string note;
                byte[] smf = PsfSeq.ToSmf(d, p, out note);       // p = offset of "pQES"
                if (smf == null) throw new InvalidDataException("The SEQ could not be read: " + note);
                return new List<SqSong> { SqSong.FromSmf(smf, name) };
            }
            throw new InvalidDataException("This is not a MIDI, SQ or SEQ file.");
        }
    }
}
