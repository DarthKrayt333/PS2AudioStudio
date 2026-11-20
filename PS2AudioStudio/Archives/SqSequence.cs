using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PS2AudioStudio.Archives
{
    public enum SqEventType { NoteOn, NoteOff, Cc, Program, PitchBend, Tempo }

    public struct SqEvent
    {
        public long Tick;
        public SqEventType Type;
        public byte Channel;
        public byte A;          // key / controller / program / pitch-bend LSB
        public byte B;          // velocity / value / pitch-bend MSB
        public int Tempo;       // microseconds per quarter (Tempo events)
    }

    /// <summary>One song (MIDI-like event list) from an SQ file or a standard MIDI file.</summary>
    public class SqSong
    {
        public int Index = -1;                 // index inside the SQ (or -1 for external MIDI)
        public string Name;
        public int Ppqn = 480;
        public List<SqEvent> Events = new List<SqEvent>();
        public byte[] RawBlock;                // original bytes inside the SQ chunk

        // ---- loop (controller #99: value 0 = loop start, 127 = loop end)
        public int LoopStartIndex = -1;        // event index of the CC99=0 marker (-1 = none)
        public int LoopEndIndex = -1;          // event index of the CC99=127 marker (-1 = none)
        public bool HasLoop => LoopStartIndex >= 0 || LoopEndIndex >= 0;

        public override string ToString() => Name ?? ("Song " + Index);

        public byte LoopEndValue = 1;          // CC#99 value that ends the loop (original SQ files use 1, some tools write 127)
        public byte LoopChannel = 0;           // channel the markers sit on

        public void FindLoop()
        {
            LoopStartIndex = -1; LoopEndIndex = -1;
            for (int i = 0; i < Events.Count; i++)
            {
                var e = Events[i];
                if (e.Type != SqEventType.Cc || e.A != 99) continue;
                if (e.B == 0)
                {
                    if (LoopStartIndex < 0) { LoopStartIndex = i; LoopChannel = e.Channel; }
                }
                else if (e.B == 1 || e.B == 127)
                {
                    if (LoopEndIndex < 0 && i > LoopStartIndex) { LoopEndIndex = i; LoopEndValue = e.B; LoopChannel = e.Channel; }
                }
            }
        }

        /// <summary>Tick of a time in seconds (follows the tempo changes).</summary>
        public long SecondsToTick(double sec)
        {
            int ppqn = Ppqn > 0 ? Ppqn : 480;
            double tempo = 500000;
            long lastTick = 0; double lastSec = 0;
            foreach (var e in Events)
            {
                if (e.Type != SqEventType.Tempo || e.Tempo <= 0) continue;
                double segSec = (e.Tick - lastTick) * tempo / 1e6 / ppqn;
                if (lastSec + segSec >= sec) break;
                lastSec += segSec; lastTick = e.Tick; tempo = e.Tempo;
            }
            double ticks = (sec - lastSec) * 1e6 * ppqn / tempo;
            return Math.Max(0, lastTick + (long)Math.Round(ticks));
        }

        /// <summary>Moves (or creates) the two CC#99 loop markers at the given ticks.</summary>
        public void SetLoopTicks(long startTick, long endTick)
        {
            if (endTick <= startTick) throw new ArgumentException("The loop end must be after the loop start.");
            FindLoop();
            var kill = new List<int>();
            if (LoopStartIndex >= 0) kill.Add(LoopStartIndex);
            if (LoopEndIndex >= 0) kill.Add(LoopEndIndex);
            foreach (int i in kill.OrderByDescending(x => x)) Events.RemoveAt(i);

            int p = 0;                                            // start marker: before every event at that tick
            while (p < Events.Count && Events[p].Tick < startTick) p++;
            Events.Insert(p, new SqEvent { Tick = startTick, Type = SqEventType.Cc, Channel = LoopChannel, A = 99, B = 0 });

            int q = 0;                                            // end marker: before the events at that tick, after their note-offs
            while (q < Events.Count && Events[q].Tick < endTick) q++;
            while (q < Events.Count && Events[q].Tick == endTick && Events[q].Type == SqEventType.NoteOff) q++;
            Events.Insert(q, new SqEvent { Tick = endTick, Type = SqEventType.Cc, Channel = LoopChannel, A = 99, B = LoopEndValue });
            FindLoop();
        }

        /// <summary>Short text about the CC#99 events (for the status line).</summary>
        public string Cc99Summary
        {
            get
            {
                var parts = Events.Where(e => e.Type == SqEventType.Cc && e.A == 99).Take(6).Select(e => $"{e.B}@tick {e.Tick}").ToList();
                return parts.Count == 0 ? "no CC#99 events" : "CC#99: " + string.Join(", ", parts);
            }
        }

        /// <summary>startIdx = -1 means "from the beginning"; endIdx = Events.Count means "to the end of the track".</summary>
        public bool TryGetLoopRange(out int startIdx, out int endIdx)
        {
            startIdx = LoopStartIndex;
            endIdx = LoopEndIndex >= 0 ? LoopEndIndex : Events.Count;
            return HasLoop && Events.Count > 0;
        }

        public string LoopDescription
        {
            get
            {
                if (!HasLoop || Events.Count == 0) return "no loop markers (CC#99)";
                var t = EventSeconds();
                double a = LoopStartIndex < 0 ? 0 : t[LoopStartIndex];
                double b = LoopEndIndex < 0 ? t[t.Length - 1] : t[LoopEndIndex];
                return $"loop {a:F2}s -> {b:F2}s";
            }
        }

        public double[] EventSeconds()
        {
            var t = new double[Events.Count];
            double tempo = 500000;
            long lastTick = 0; double sec = 0;
            int ppqn = Ppqn > 0 ? Ppqn : 480;
            for (int i = 0; i < Events.Count; i++)
            {
                var e = Events[i];
                sec += (e.Tick - lastTick) * tempo / 1e6 / ppqn;
                lastTick = e.Tick;
                if (e.Type == SqEventType.Tempo && e.Tempo > 0) tempo = e.Tempo;
                t[i] = sec;
            }
            return t;
        }

        public double DurationSeconds
        {
            get { var t = EventSeconds(); return t.Length == 0 ? 0 : t[t.Length - 1]; }
        }

        public int NoteCount => Events.Count(e => e.Type == SqEventType.NoteOn);
        public IEnumerable<int> Channels => Events.Where(e => e.Type == SqEventType.NoteOn).Select(e => (int)e.Channel).Distinct().OrderBy(x => x);

        /// <summary>Writes the loop markers the way the game's own .sq files do (checked against BGM_FRM.sq):
        /// start = CC99 0, then CC6 1 one tick later; end = CC99 1, then CC6 1 one tick later, then CC38 0 one more tick later.
        /// Takes the CC99 start/end events already in the song and replaces them. Returns false if there are none.</summary>
        public bool AddSonyLoopData()
        {
            int si = -1, ei = -1;
            for (int i = 0; i < Events.Count; i++)
            {
                SqEvent e = Events[i];
                if (e.Type != SqEventType.Cc || e.A != 99) continue;
                if (e.B == 0) { if (si < 0) si = i; }
                else if (e.B == 1 || e.B == 127) { if (ei < 0) ei = i; }
            }
            if (si < 0 || ei < 0) return false;
            long tStart = Events[si].Tick;
            long tEnd = Events[ei].Tick;
            Events.RemoveAt(Math.Max(si, ei));
            Events.RemoveAt(Math.Min(si, ei));
            // note-offs just behind the end marker would be skipped by the jump: put the marker behind them (real file: marker after the last note-off)
            long lastOff = tEnd;
            for (int i = 0; i < Events.Count; i++)
                if (Events[i].Type == SqEventType.NoteOff && Events[i].Tick > lastOff && Events[i].Tick <= tEnd + 48) lastOff = Events[i].Tick;
            tEnd = lastOff;
            // before the note-ons of that tick (after note-offs / controllers)
            int PosStart(long t)
            {
                int i = 0;
                while (i < Events.Count && (Events[i].Tick < t || (Events[i].Tick == t && Events[i].Type != SqEventType.NoteOn))) i++;
                return i;
            }
            // after everything up to and including that tick
            int PosAfter(long t)
            {
                int i = 0;
                while (i < Events.Count && Events[i].Tick <= t) i++;
                return i;
            }
            Events.Insert(PosStart(tStart), new SqEvent { Tick = tStart, Type = SqEventType.Cc, Channel = 0, A = 99, B = 0 });
            Events.Insert(PosAfter(tStart + 1), new SqEvent { Tick = tStart + 1, Type = SqEventType.Cc, Channel = 0, A = 6, B = 1 });
            Events.Insert(PosStart(tEnd), new SqEvent { Tick = tEnd, Type = SqEventType.Cc, Channel = 0, A = 99, B = 1 });
            Events.Insert(PosAfter(tEnd + 1), new SqEvent { Tick = tEnd + 1, Type = SqEventType.Cc, Channel = 0, A = 6, B = 1 });
            Events.Insert(PosAfter(tEnd + 2), new SqEvent { Tick = tEnd + 2, Type = SqEventType.Cc, Channel = 0, A = 38, B = 0 });
            FindLoop();
            return true;
        }

        // ================================================================= standard MIDI export
        public byte[] ToSmf()
        {
            // exact export: only the events that are in the song, nothing added
            using var track = new MemoryStream();
            long last = 0;
            int ppqn = Math.Max(1, Math.Min(32767, Ppqn));
            foreach (var e in Events)
            {
                long delta = Math.Max(0, e.Tick - last);
                last = Math.Max(last, e.Tick);
                WriteVlq(track, delta);
                int ch = e.Channel & 0x0F;
                switch (e.Type)
                {
                    case SqEventType.NoteOn: track.WriteByte((byte)(0x90 | ch)); track.WriteByte((byte)(e.A & 0x7F)); track.WriteByte((byte)Math.Max(1, e.B & 0x7F)); break;
                    case SqEventType.NoteOff: track.WriteByte((byte)(0x80 | ch)); track.WriteByte((byte)(e.A & 0x7F)); track.WriteByte(0x40); break;
                    case SqEventType.Cc: track.WriteByte((byte)(0xB0 | ch)); track.WriteByte((byte)(e.A & 0x7F)); track.WriteByte((byte)(e.B & 0x7F)); break;
                    case SqEventType.Program: track.WriteByte((byte)(0xC0 | ch)); track.WriteByte((byte)(e.A & 0x7F)); break;
                    case SqEventType.PitchBend: track.WriteByte((byte)(0xE0 | ch)); track.WriteByte((byte)(e.A & 0x7F)); track.WriteByte((byte)(e.B & 0x7F)); break;
                    case SqEventType.Tempo:
                        track.WriteByte(0xFF); track.WriteByte(0x51); track.WriteByte(0x03);
                        track.WriteByte((byte)(e.Tempo >> 16)); track.WriteByte((byte)(e.Tempo >> 8)); track.WriteByte((byte)e.Tempo);
                        break;
                }
            }
            WriteVlq(track, 0);
            track.Write(new byte[] { 0xFF, 0x2F, 0x00 }, 0, 3);
            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("MThd"), 0, 4);
            WriteBE32(ms, 6); WriteBE16(ms, 0); WriteBE16(ms, 1); WriteBE16(ms, ppqn);
            ms.Write(Encoding.ASCII.GetBytes("MTrk"), 0, 4);
            WriteBE32(ms, (int)track.Length);
            track.WriteTo(ms);
            return ms.ToArray();
        }

        // ============ Sony PS1/PS2 SEQ ("pQES") writer ============
        // Layout: "pQES", u32 BE version=1, u16 BE resolution, 3 bytes tempo (us/quarter, BE),
        // 1 byte time-sig numerator, 1 byte denominator (power of 2) = 15 bytes, then MIDI events, end FF 2F 00.
        public byte[] ToSeq()
        {
            using var ms = new MemoryStream();
            int ppqn = Math.Max(1, Math.Min(65535, Ppqn));
            int tempo0 = 500000, firstTempo = -1;
            for (int i = 0; i < Events.Count; i++)
                if (Events[i].Type == SqEventType.Tempo && Events[i].Tempo > 0 && Events[i].Tick == 0)
                { tempo0 = Math.Min(0xFFFFFF, Events[i].Tempo); firstTempo = i; break; }

            ms.Write(Encoding.ASCII.GetBytes("pQES"), 0, 4);
            WriteBE32(ms, 1);
            WriteBE16(ms, ppqn);
            ms.WriteByte((byte)(tempo0 >> 16)); ms.WriteByte((byte)(tempo0 >> 8)); ms.WriteByte((byte)tempo0);
            ms.WriteByte(4); ms.WriteByte(2);                       // 4/4

            long last = 0; int running = -1;
            for (int i = 0; i < Events.Count; i++)
            {
                if (i == firstTempo) continue;                      // already in the header
                var e = Events[i];
                WriteVlq(ms, Math.Max(0, e.Tick - last));
                last = Math.Max(last, e.Tick);
                if (e.Type == SqEventType.Tempo)
                {
                    if (e.Tempo <= 0) { ms.WriteByte(0xFF); ms.WriteByte(0x51); ms.WriteByte(3); ms.WriteByte(0x07); ms.WriteByte(0xA1); ms.WriteByte(0x20); }
                    else
                    {
                        ms.WriteByte(0xFF); ms.WriteByte(0x51); ms.WriteByte(3);
                        ms.WriteByte((byte)(e.Tempo >> 16)); ms.WriteByte((byte)(e.Tempo >> 8)); ms.WriteByte((byte)e.Tempo);
                    }
                    running = -1;                                   // a meta event ends running status
                    continue;
                }
                int ch = e.Channel & 0x0F, status; byte d1 = (byte)(e.A & 0x7F), d2 = 0; int n = 2;
                switch (e.Type)
                {
                    case SqEventType.NoteOn: status = 0x90 | ch; d2 = (byte)Math.Max(1, e.B & 0x7F); break;
                    case SqEventType.NoteOff: status = 0x90 | ch; d2 = 0; break;      // note-on vel 0 = note-off
                    case SqEventType.Cc: status = 0xB0 | ch; d2 = (byte)(e.B & 0x7F); break;
                    case SqEventType.Program: status = 0xC0 | ch; n = 1; break;
                    case SqEventType.PitchBend: status = 0xE0 | ch; d2 = (byte)(e.B & 0x7F); break;
                    default: continue;
                }
                if (status != running) ms.WriteByte((byte)status);
                running = status;
                ms.WriteByte(d1); if (n == 2) ms.WriteByte(d2);
            }
            WriteVlq(ms, 0);
            ms.Write(new byte[] { 0xFF, 0x2F, 0x00 }, 0, 3);
            return ms.ToArray();
        }

        // ============ Sony SEQ reader ============
        public static SqSong FromSeq(byte[] d, int off, string name)
        {
            if (d == null || off < 0 || d.Length < off + 15 || d[off] != 'p' || d[off + 1] != 'Q' || d[off + 2] != 'E' || d[off + 3] != 'S')
                throw new InvalidDataException("Not a Sony SEQ file ('pQES').");
            int ppqn = BE16(d, off + 8); if (ppqn == 0) ppqn = 480;
            int tempo0 = (d[off + 10] << 16) | (d[off + 11] << 8) | d[off + 12];
            var song = new SqSong { Name = name, Ppqn = ppqn };
            if (tempo0 > 0) song.Events.Add(new SqEvent { Tick = 0, Type = SqEventType.Tempo, Tempo = tempo0 });
            int p = off + 15, end = d.Length, running = 0; long tick = 0;
            while (p < end)
            {
                tick += ReadVlq(d, ref p);
                if (p >= end) break;
                int st = d[p];
                if (st >= 0x80) { p++; if (st < 0xF0) running = st; }
                else { st = running; if (st == 0) break; }
                if (st == 0xFF)
                {
                    if (p >= end) break;
                    byte type = d[p++]; int ml = (int)ReadVlq(d, ref p);
                    if (type == 0x2F) break;
                    if (type == 0x51 && ml == 3 && p + 3 <= end)
                        song.Events.Add(new SqEvent { Tick = tick, Type = SqEventType.Tempo, Tempo = (d[p] << 16) | (d[p + 1] << 8) | d[p + 2] });
                    p += ml; continue;
                }
                if (st == 0xF0 || st == 0xF7) { p += (int)ReadVlq(d, ref p); continue; }
                int hi = st & 0xF0; byte c = (byte)(st & 0x0F);
                int nData = (hi == 0xC0 || hi == 0xD0) ? 1 : 2;
                if (p + nData > end) break;
                byte a = d[p], b = nData == 2 ? d[p + 1] : (byte)0; p += nData;
                switch (hi)
                {
                    case 0x80: song.Events.Add(new SqEvent { Tick = tick, Channel = c, A = a, Type = SqEventType.NoteOff }); break;
                    case 0x90: song.Events.Add(new SqEvent { Tick = tick, Channel = c, A = a, B = b, Type = b > 0 ? SqEventType.NoteOn : SqEventType.NoteOff }); break;
                    case 0xB0: song.Events.Add(new SqEvent { Tick = tick, Channel = c, A = a, B = b, Type = SqEventType.Cc }); break;
                    case 0xC0: song.Events.Add(new SqEvent { Tick = tick, Channel = c, A = a, Type = SqEventType.Program }); break;
                    case 0xE0: song.Events.Add(new SqEvent { Tick = tick, Channel = c, A = a, B = b, Type = SqEventType.PitchBend }); break;
                }
            }
            song.FindLoop();
            return song;
        }

        /// <summary>Opens .mid / .sq / .seq (anything with a known magic) as one song. Use this for "place MIDI / SQ / SEQ manually" and drag-and-drop.</summary>
        public static SqSong LoadAny(byte[] d, string name, int songIndex = 0)
        {
            if (d.Length >= 4 && d[0] == 'M' && d[1] == 'T' && d[2] == 'h' && d[3] == 'd') return FromSmf(d, name);
            if (d.Length >= 4 && d[0] == 'p' && d[1] == 'Q' && d[2] == 'E' && d[3] == 'S') return FromSeq(d, 0, name);
            if (d.Length >= 4 && d[0] == 'I' && d[1] == 'E' && d[2] == 'C' && d[3] == 'S')
            {
                var f = new SqFile(); f.Parse(d);
                var s = f.Songs[Math.Min(Math.Max(0, songIndex), f.Songs.Count - 1)];
                s.Name = name; return s;
            }
            throw new InvalidDataException("Unknown sequence format (expected MIDI, SQ or SEQ).");
        }

        private static void WriteMeta(Stream s, byte type, string text)
        {
            byte[] b = Encoding.ASCII.GetBytes(text ?? "");
            WriteVlq(s, 0);
            s.WriteByte(0xFF); s.WriteByte(type);
            WriteVlq(s, b.Length);
            s.Write(b, 0, b.Length);
        }

        // ================================================================= standard MIDI import
        public static SqSong FromSmf(byte[] d, string name)
        {
            if (d.Length < 14 || d[0] != 'M' || d[1] != 'T' || d[2] != 'h' || d[3] != 'd')
                throw new InvalidDataException("Not a standard MIDI file.");
            int hdrLen = BE32(d, 4);
            int ntrks = BE16(d, 10);
            int div = BE16(d, 12);
            if ((div & 0x8000) != 0) throw new InvalidDataException("SMPTE-timed MIDI files are not supported.");
            if (div == 0) div = 480;
            var song = new SqSong { Name = name, Ppqn = div };
            var all = new List<(SqEvent ev, int order)>();
            var ports = new List<int>();                       // MIDI port (FF 21 prefix) of every entry in 'all'
            var markers = new List<(SqEvent ev, int order)>();
            int order = 0;
            int port = 0;
            void Add(SqEvent e) { all.Add((e, order++)); ports.Add(port); }
            int p = 8 + hdrLen;
            for (int t = 0; t < ntrks && p + 8 <= d.Length; t++)
            {
                string id = Encoding.ASCII.GetString(d, p, 4);
                int len = BE32(d, p + 4);
                int s = p + 8, end = Math.Min(d.Length, s + len);
                p = s + len;
                if (id != "MTrk") { continue; }
                port = 0;
                long tick = 0; byte running = 0;
                int q = s;
                while (q < end)
                {
                    tick += ReadVlq(d, ref q);
                    if (q >= end) break;
                    byte st = d[q];
                    if (st >= 0x80) { q++; if (st < 0xF0) running = st; }
                    else st = running;
                    if (st == 0xFF)
                    {
                        if (q >= end) break;
                        byte type = d[q++];
                        int ml = (int)ReadVlq(d, ref q);
                        if (type == 0x21 && ml == 1 && q < end)
                            port = d[q];
                        else if (type == 0x51 && ml == 3 && q + 3 <= end)
                            Add(new SqEvent { Tick = tick, Type = SqEventType.Tempo, Tempo = (d[q] << 16) | (d[q + 1] << 8) | d[q + 2] });
                        else if (type == 0x06 && ml > 0 && q + ml <= end)
                        {
                            string txt = Encoding.ASCII.GetString(d, q, ml).Trim();
                            if (txt.Equals("loopStart", StringComparison.OrdinalIgnoreCase))
                                markers.Add((new SqEvent { Tick = tick, Type = SqEventType.Cc, A = 99, B = 0 }, order++));
                            else if (txt.Equals("loopEnd", StringComparison.OrdinalIgnoreCase))
                                markers.Add((new SqEvent { Tick = tick, Type = SqEventType.Cc, A = 99, B = 1 }, order++));
                        }
                        q += ml;
                        if (type == 0x2F) break;
                    }
                    else if (st == 0xF0 || st == 0xF7)
                    {
                        int ml = (int)ReadVlq(d, ref q);
                        q += ml;
                    }
                    else
                    {
                        int hi = st & 0xF0; byte ch = (byte)(st & 0x0F);
                        int nData = (hi == 0xC0 || hi == 0xD0) ? 1 : 2;
                        if (q + nData > end) break;
                        byte a = d[q], b = nData == 2 ? d[q + 1] : (byte)0;
                        q += nData;
                        switch (hi)
                        {
                            case 0x90:
                                Add(new SqEvent { Tick = tick, Channel = ch, A = a, B = b, Type = b > 0 ? SqEventType.NoteOn : SqEventType.NoteOff }); break;
                            case 0x80:
                                Add(new SqEvent { Tick = tick, Channel = ch, A = a, Type = SqEventType.NoteOff }); break;
                            case 0xB0:
                                Add(new SqEvent { Tick = tick, Channel = ch, A = a, B = b, Type = SqEventType.Cc }); break;
                            case 0xC0:
                                Add(new SqEvent { Tick = tick, Channel = ch, A = a, Type = SqEventType.Program }); break;
                            case 0xE0:
                                Add(new SqEvent { Tick = tick, Channel = ch, A = a, B = b, Type = SqEventType.PitchBend }); break;
                        }
                    }
                }
            }
            // MIDI ports: port 1+ has its own 16 channels, our song model has 16. A port-1+ channel that port 0 already uses
            // is moved to a free channel (channel 10 is chosen last), so two tracks never share a channel.
            var used = new bool[16];
            for (int i = 0; i < all.Count; i++)
                if (ports[i] == 0 && all[i].ev.Type != SqEventType.Tempo) used[all[i].ev.Channel & 15] = true;
            var map = new Dictionary<int, int>();              // key = port * 16 + channel
            for (int i = 0; i < all.Count; i++)
            {
                if (ports[i] == 0 || all[i].ev.Type == SqEventType.Tempo) continue;
                int ch0 = all[i].ev.Channel & 15;
                int key = ports[i] * 16 + ch0;
                if (map.ContainsKey(key)) continue;
                int target = ch0;
                if (used[ch0])
                {
                    target = -1;
                    for (int c = 0; c < 16; c++) if (c != 9 && !used[c]) { target = c; break; }
                    if (target < 0 && !used[9]) target = 9;
                    if (target < 0) target = ch0;              // all 16 channels taken: unavoidable
                }
                used[target] = true;
                map[key] = target;
            }
            for (int i = 0; i < all.Count; i++)
            {
                if (ports[i] == 0 || all[i].ev.Type == SqEventType.Tempo) continue;
                var tup = all[i];
                tup.ev.Channel = (byte)map[ports[i] * 16 + (tup.ev.Channel & 15)];
                all[i] = tup;
            }
            // text markers only count when the file has no real CC#99 loop controllers
            bool hasCc99 = all.Any(x => x.ev.Type == SqEventType.Cc && x.ev.A == 99);
            if (!hasCc99) all.AddRange(markers);
            song.Events = all.OrderBy(x => x.ev.Tick).ThenBy(x => x.order).Select(x => x.ev).ToList();
            song.FindLoop();
            return song;
        }

        // ================================================================= SCEI SQ block writer
        public byte[] ToSqBlock()
        {
            using var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(6), 0, 4);                       // sequence data offset
            ms.Write(BitConverter.GetBytes((ushort)Ppqn), 0, 2);            // resolution

            long last = 0;
            int running = -1;           // last channel status byte written (meta events do not change it)
            bool skipDelta = false;     // previous event had bit 7 set -> this event's delta (0) is not written

            for (int i = 0; i < Events.Count; i++)
            {
                var e = Events[i];
                long delta = Math.Max(0, e.Tick - last);
                last = Math.Max(last, e.Tick);

                if (!skipDelta) WriteVlq(ms, delta);
                skipDelta = false;

                if (e.Type == SqEventType.Tempo)
                {
                    ms.WriteByte(0xFF); ms.WriteByte(0x51); ms.WriteByte(0x03);
                    ms.WriteByte((byte)(e.Tempo >> 16)); ms.WriteByte((byte)(e.Tempo >> 8)); ms.WriteByte((byte)e.Tempo);
                    continue;
                }

                // next event at the same tick (and not a meta event)
                bool zeroNext = false;
                if (i + 1 < Events.Count)
                {
                    var n = Events[i + 1];
                    zeroNext = n.Type != SqEventType.Tempo && n.Tick <= last;
                }

                int ch = e.Channel & 0x0F;
                int status, nData;
                byte d1 = (byte)(e.A & 0x7F), d2 = 0;
                switch (e.Type)
                {
                    case SqEventType.NoteOn: status = 0x90 | ch; nData = 2; d2 = (byte)Math.Max(1, e.B & 0x7F); break;
                    case SqEventType.NoteOff: status = 0x80 | ch; nData = 1; break;       // SQ note-off has NO velocity byte
                    case SqEventType.Cc: status = 0xB0 | ch; nData = 2; d2 = (byte)(e.B & 0x7F); break;
                    case SqEventType.Program: status = 0xC0 | ch; nData = 1; break;
                    case SqEventType.PitchBend: status = 0xE0 | ch; nData = 2; d2 = (byte)(e.B & 0x7F); break;
                    default: continue;
                }

                // running status is used when the status matches (never when the first data byte is 0)
                bool writeStatus = running != status || d1 == 0;
                if (writeStatus) ms.WriteByte((byte)status);
                running = status;

                // the "next delta is 0" flag (bit 7) is ONLY used on events that write their own status byte;
                // running-status events get a normal explicit 00 delta, exactly like the original files
                if (!writeStatus) zeroNext = false;

                if (nData == 1) ms.WriteByte((byte)(zeroNext ? (d1 | 0x80) : d1));
                else { ms.WriteByte(d1); ms.WriteByte((byte)(zeroNext ? (d2 | 0x80) : d2)); }
                skipDelta = zeroNext;
            }

            WriteVlq(ms, 0);
            ms.Write(new byte[] { 0xFF, 0x2F, 0x00 }, 0, 3);
            return ms.ToArray();                     // padding is added by the MIDI chunk
        }

        // ================================================================= helpers
        public static int BE16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
        public static int BE32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        private static void WriteBE16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        private static void WriteBE32(Stream s, int v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

        public static long ReadVlq(byte[] d, ref int p)
        {
            long v = 0;
            for (int i = 0; i < 5 && p < d.Length; i++)
            {
                byte b = d[p++];
                v = (v << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0) break;
            }
            return v;
        }

        public static void WriteVlq(Stream s, long v)
        {
            if (v < 0) v = 0;
            var tmp = new Stack<byte>();
            tmp.Push((byte)(v & 0x7F));
            v >>= 7;
            while (v > 0) { tmp.Push((byte)((v & 0x7F) | 0x80)); v >>= 7; }
            foreach (var b in tmp) s.WriteByte(b);
        }
    }

    /// <summary>
    /// SCEI "SQ" file (PS2 MIDI sequence): version chunk, header chunk, then the "IECSidiM" MIDI chunk
    /// holding one or more songs. Event stream = MIDI-like, but note-off has NO velocity and any data byte with bit 7 set
    /// means "next event has delta time 0".
    /// </summary>
    public class SqFile
    {
        public List<SqSong> Songs { get; } = new List<SqSong>();
        public string Path { get; private set; }
        public byte[] Data { get; private set; }
        private int midiChunk;
        private int midiChunkSize;

        private static int I32(byte[] d, long o) => BitConverter.ToInt32(d, (int)o);

        public static SqFile Load(string path)
        {
            var f = new SqFile();
            f.Parse(File.ReadAllBytes(path));
            f.Path = path;
            return f;
        }

        public void Parse(byte[] d)
        {
            if (d.Length < 0x30 || Encoding.ASCII.GetString(d, 0, 8) != "IECSsreV")
                throw new InvalidDataException("Not a SCEI SQ file (missing 'IECSsreV').");

            int vers = I32(d, 8);
            if (vers < 0x10 || vers > 0x100) vers = 0x10;
            int hdrSize = I32(d, vers + 8);
            int mid = vers + hdrSize;
            if (mid + 0x14 > d.Length || Encoding.ASCII.GetString(d, mid, 8) != "IECSidiM")
                throw new InvalidDataException("No 'IECSidiM' MIDI chunk found in this SQ.");

            int chunkSize = I32(d, mid + 8);
            if (chunkSize <= 0 || mid + chunkSize > d.Length) chunkSize = d.Length - mid;
            int count = Math.Min(I32(d, mid + 12) + 1, 512);
            if (count < 1) count = 1;

            var offs = new int[count];
            for (int i = 0; i < count; i++)
            {
                int rel = I32(d, mid + 16 + i * 4);
                offs[i] = mid + rel;
            }

            Songs.Clear();
            var sorted = offs.Where(o => o > mid && o < d.Length).Distinct().OrderBy(x => x).ToList();
            int chunkEnd = mid + chunkSize;
            for (int i = 0; i < count; i++)
            {
                int start = offs[i];
                if (start <= mid || start + 6 > d.Length) continue;
                int idx = sorted.IndexOf(start);
                int end = idx >= 0 && idx + 1 < sorted.Count ? sorted[idx + 1] : chunkEnd;
                if (end <= start) end = d.Length;

                var song = ParseBlock(d, start, end);
                song.Index = i;
                song.Name = $"SQ song #{i}";
                song.RawBlock = d.Skip(start).Take(end - start).ToArray();
                Songs.Add(song);
            }
            if (Songs.Count == 0) throw new InvalidDataException("The SQ contains no readable songs.");

            Data = d; midiChunk = mid; midiChunkSize = chunkSize;
        }

        private static SqSong ParseBlock(byte[] d, int start, int end)
        {
            var song = new SqSong();
            int seqOff = I32(d, start);
            if (seqOff < 6 || seqOff > end - start) seqOff = 6;
            song.Ppqn = BitConverter.ToUInt16(d, start + 4);
            if (song.Ppqn == 0) song.Ppqn = 480;

            int p = start + seqOff;
            int limit = Math.Min(end, d.Length);
            long tick = 0;
            byte running = 0;
            bool skip = false;

            byte DataByte(ref int q)
            {
                byte v = d[q++];
                if ((v & 0x80) != 0) { skip = true; v &= 0x7F; } else skip = false;
                return v;
            }

            while (p < limit)
            {
                long delta = skip ? 0 : SqSong.ReadVlq(d, ref p);
                tick += delta;
                if (p >= limit) break;
                skip = false;

                byte st = d[p++];
                if (st <= 0x7F)
                {
                    if (st == 0 && p + 3 <= limit && BitConverter.ToInt32(d, p) == 0) break;
                    st = running;
                    p--;
                    if (st == 0) break;
                }
                else if (st != 0xFF) running = st;

                byte ch = (byte)(st & 0x0F);
                switch (st & 0xF0)
                {
                    case 0x80:
                        {
                            if (p >= limit) goto done;
                            byte key = DataByte(ref p);
                            song.Events.Add(new SqEvent { Tick = tick, Channel = ch, Type = SqEventType.NoteOff, A = key });
                            break;
                        }
                    case 0x90:
                        {
                            if (p + 2 > limit) goto done;
                            byte key = (byte)(d[p++] & 0x7F);
                            byte vel = DataByte(ref p);
                            song.Events.Add(new SqEvent { Tick = tick, Channel = ch, Type = vel > 0 ? SqEventType.NoteOn : SqEventType.NoteOff, A = key, B = vel });
                            break;
                        }
                    case 0xB0:
                        {
                            if (p + 2 > limit) goto done;
                            byte cc = (byte)(d[p++] & 0x7F);
                            byte val = DataByte(ref p);
                            song.Events.Add(new SqEvent { Tick = tick, Channel = ch, Type = SqEventType.Cc, A = cc, B = val });
                            break;
                        }
                    case 0xC0:
                        {
                            if (p >= limit) goto done;
                            byte prog = DataByte(ref p);
                            song.Events.Add(new SqEvent { Tick = tick, Channel = ch, Type = SqEventType.Program, A = prog });
                            break;
                        }
                    case 0xE0:
                        {
                            if (p + 2 > limit) goto done;
                            byte lsb = (byte)(d[p++] & 0x7F);
                            byte msb = DataByte(ref p);
                            song.Events.Add(new SqEvent { Tick = tick, Channel = ch, Type = SqEventType.PitchBend, A = lsb, B = msb });
                            break;
                        }

                    case 0xA0:   // poly aftertouch: key + pressure (ignored, but must not end the song)
                        {
                            if (p + 2 > limit) goto done;
                            p++;
                            DataByte(ref p);
                            break;
                        }
                    case 0xD0:   // channel pressure (ignored)
                        {
                            if (p >= limit) goto done;
                            DataByte(ref p);
                            break;
                        }

                    case 0xF0:
                        if (st == 0xFF)
                        {
                            if (p >= limit) goto done;
                            byte type = d[p++];
                            if (type == 0x51)
                            {
                                if (p + 4 > limit) goto done;
                                int tempo = (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
                                p += 4;
                                song.Events.Add(new SqEvent { Tick = tick, Type = SqEventType.Tempo, Tempo = tempo });
                            }
                            else if (type == 0x2F) goto done;     // end of track
                            else
                            {
                                long ml = SqSong.ReadVlq(d, ref p);   // unknown meta: skip it instead of ending the song
                                if (ml < 0 || p + ml > limit) goto done;
                                p += (int)ml;
                            }
                        }
                        break;
                    default:
                        goto done;
                }
            }
        done:
            song.FindLoop();
            return song;
        }

        // ================================================================= building SQ files

        private static byte[] BuildMidiChunk(List<byte[]> blocks)
        {
            int count = blocks.Count;
            int first = 16 + count * 4;                       // first song starts right after the offset table (0x14 for one song)
            using var chunk = new MemoryStream();
            chunk.Write(Encoding.ASCII.GetBytes("IECSidiM"), 0, 8);
            chunk.Write(BitConverter.GetBytes(0), 0, 4);                      // size (patched below)
            chunk.Write(BitConverter.GetBytes(count - 1), 0, 4);              // max index
            int pos = first;
            for (int i = 0; i < count; i++)
            {
                chunk.Write(BitConverter.GetBytes(pos), 0, 4);
                pos += blocks[i].Length;
                if (i < count - 1) pos = (pos + 3) & ~3;                      // songs in the middle are 4-byte aligned
            }
            for (int i = 0; i < count; i++)
            {
                chunk.Write(blocks[i], 0, blocks[i].Length);
                if (i < count - 1) while (chunk.Length % 4 != 0) chunk.WriteByte(0xFF);
            }
            while (chunk.Length % 16 != 0) chunk.WriteByte(0xFF);             // whole chunk padded to 16 with FF
            byte[] cb = chunk.ToArray();
            Array.Copy(BitConverter.GetBytes(cb.Length), 0, cb, 8, 4);
            return cb;
        }

        /// <summary>Returns a new SQ file image where song 'index' is replaced (or appended when index == Songs.Count).</summary>
        public byte[] BuildWith(int index, SqSong replacement)
        {
            var blocks = new List<byte[]>();
            for (int i = 0; i < Songs.Count; i++) blocks.Add(Songs[i].RawBlock);
            byte[] nb = replacement.ToSqBlock();
            if (index >= blocks.Count) blocks.Add(nb); else blocks[index] = nb;

            byte[] cb = BuildMidiChunk(blocks);

            using var outp = new MemoryStream();
            outp.Write(Data, 0, midiChunk);
            outp.Write(cb, 0, cb.Length);
            int tail = midiChunk + midiChunkSize;
            if (tail < Data.Length) outp.Write(Data, tail, Data.Length - tail);
            byte[] file = outp.ToArray();
            PatchTotalSize(file, Data);
            return file;
        }

        /// <summary>
        /// Builds an SQ file from scratch out of songs (e.g. imported .mid files).
        /// With a template the template's header is copied (recommended); without one a header is synthesized.
        /// </summary>
        public static byte[] BuildNew(IList<SqSong> songs, SqFile template = null)
        {
            if (songs == null || songs.Count == 0) throw new ArgumentException("No songs to write.");
            var blocks = new List<byte[]>();
            foreach (var s in songs) blocks.Add(s.ToSqBlock());
            byte[] cb = BuildMidiChunk(blocks);

            byte[] head;
            byte[] old = null;
            if (template != null && template.Data != null)
            {
                head = new byte[template.midiChunk];
                Array.Copy(template.Data, head, head.Length);
                old = template.Data;
            }
            else
            {
                using var h = new MemoryStream();
                h.Write(Encoding.ASCII.GetBytes("IECSsreV"), 0, 8);
                h.Write(BitConverter.GetBytes(0x10), 0, 4);
                h.Write(BitConverter.GetBytes(0x00020000), 0, 4);
                h.Write(Encoding.ASCII.GetBytes("IECSuqeS"), 0, 8);
                h.Write(BitConverter.GetBytes(0x20), 0, 4);
                h.Write(BitConverter.GetBytes(0), 0, 4);          // total file size (patched below)
                h.Write(BitConverter.GetBytes(-1), 0, 4);
                h.Write(BitConverter.GetBytes(0x30), 0, 4);       // offset of the MIDI chunk
                h.Write(BitConverter.GetBytes(-1), 0, 4);
                h.Write(BitConverter.GetBytes(-1), 0, 4);
                head = h.ToArray();
            }

            byte[] file = new byte[head.Length + cb.Length];
            Array.Copy(head, file, head.Length);
            Array.Copy(cb, 0, file, head.Length, cb.Length);

            if (old != null) PatchTotalSize(file, old);
            else Array.Copy(BitConverter.GetBytes(file.Length), 0, file, 0x10 + 12, 4);
            return file;
        }

        /// <summary>Updates the "file size" field of the header chunk, but only if it held the old file length.</summary>
        private static void PatchTotalSize(byte[] file, byte[] oldData)
        {
            try
            {
                int vers = I32(oldData, 8);
                if (vers < 0x10 || vers > 0x100) vers = 0x10;
                int o = vers + 12;
                if (o + 4 <= file.Length && o + 4 <= oldData.Length && I32(oldData, o) == oldData.Length)
                    Array.Copy(BitConverter.GetBytes(file.Length), 0, file, o, 4);
            }
            catch { }
        }
    }
}
