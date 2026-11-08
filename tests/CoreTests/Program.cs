using System;
using System.IO;
using System.Linq;
using System.Text;
using PS2AudioStudio.Audio;
using PS2AudioStudio.Archives;

static class T {
  static int fails = 0;
  static void Check(bool ok, string msg) { Console.WriteLine((ok ? "PASS " : "FAIL ") + msg); if (!ok) fails++; }
  static double Snr(AudioBuffer a, AudioBuffer b, int ch) {
    int n = Math.Min(a.TotalFrames, b.TotalFrames); double s=0,e=0;
    for (int i=0;i<n;i++){ double x=a.Samples[i*a.Channels+Math.Min(ch,a.Channels-1)], y=b.Samples[i*b.Channels+Math.Min(ch,b.Channels-1)]; s+=x*x; e+=(x-y)*(x-y);} 
    return 10*Math.Log10(s/Math.Max(e,1e-12)); }
  static AudioBuffer Sine(int sr, int ch, double sec, double hz) {
    var b = new AudioBuffer(sr, ch, (int)(sr*sec));
    for (int i=0;i<b.TotalFrames;i++) for (int c=0;c<ch;c++) b.Samples[i*ch+c]=(float)(0.6*Math.Sin(2*Math.PI*hz*(c+1)*i/sr));
    return b; }
  static void W32(MemoryStream m, uint v){ m.Write(BitConverter.GetBytes(v),0,4);} 
  static void W16(MemoryStream m, int v){ m.Write(BitConverter.GetBytes((ushort)v),0,2);} 
  static byte[] Chunk(string magic, int max, byte[][] items, int itemStart) {
    // generic chunk: magic(8) size max offsets... items placed at itemStart
    var m = new MemoryStream(); m.Write(Encoding.ASCII.GetBytes(magic),0,8); W32(m,0); W32(m,(uint)max);
    int pos = itemStart; foreach (var it in items){ W32(m,(uint)pos); pos += it.Length; }
    while (m.Length < itemStart) m.WriteByte(0);
    foreach (var it in items) m.Write(it,0,it.Length);
    var arr = m.ToArray(); Array.Copy(BitConverter.GetBytes(arr.Length),0,arr,8,4); return arr; }

  static int Main() {
    var src = Sine(44100, 2, 1.0, 440);
    // ---------- VAG
    var mono = Sine(44100,1,1.0,440);
    mono.LoopEnabled=false; var vOff = new VagCodec().Encode(mono);
    var dOff = new VagCodec().Decode(vOff);
    Check(!dOff.LoopEnabled, "VAG loop OFF -> decoded has no loop");
    Check(Snr(mono,dOff,0) > 20, $"VAG roundtrip SNR {Snr(mono,dOff,0):F1} dB");
    bool anyLoopFlag = false; for (int o=0x30;o+16<=vOff.Length;o+=16) if ((vOff[o+1]&2)!=0||(vOff[o+1]&4)!=0) anyLoopFlag=true;
    Check(!anyLoopFlag, "VAG loop OFF -> no loop flags written");
    mono.LoopEnabled=true; mono.LoopStart=1000; mono.LoopEnd=30000; mono.SnapLoop(28);
    var vOn = new VagCodec().Encode(mono); var dOn = new VagCodec().Decode(vOn);
    Check(dOn.LoopEnabled && dOn.LoopStart==mono.LoopStart && dOn.LoopEnd==mono.LoopEnd, $"VAG loop ON -> decoded loop {dOn.LoopStart}->{dOn.LoopEnd} (expected {mono.LoopStart}->{mono.LoopEnd})");
    Check(Encoding.ASCII.GetString(vOn,0,4)=="VAGp" && vOn.Length==0x30+ (mono.LoopEnd/28)*16, "VAG header + truncated at loop end");
    // interleaved stereo
    src.LoopEnabled=false; var il = VagCodec.EncodeInterleaved(src, 0x1000); var ild = VagCodec.DecodeInterleaved(il,0,2,0x1000,44100);
    Check(ild.Channels==2 && Snr(src,ild,0)>20 && Snr(src,ild,1)>20, "Interleaved stereo VAG roundtrip");

    // ---------- ADX
    src.LoopEnabled=false; var aOff = new AdxCodec().Encode(src);
    int cpy = (aOff[2]<<8)|aOff[3];
    Check(aOff[0]==0x80&&aOff[1]==0&&Encoding.ASCII.GetString(aOff,cpy-2,6)=="(c)CRI", $"ADX magic + (c)CRI at cpyOff-2 (cpy=0x{cpy:X})");
    Check(((aOff[0x18]<<24)|(aOff[0x19]<<16)|(aOff[0x1a]<<8)|aOff[0x1b])==0 || cpy==0x20, "ADX loop OFF -> no loop flag");
    var adOff = new AdxCodec().Decode(aOff);
    Check(!adOff.LoopEnabled && adOff.Channels==2 && adOff.TotalFrames==src.TotalFrames, "ADX decode (no loop) frames/channels");
    Check(Snr(src,adOff,0)>25 && Snr(src,adOff,1)>20, $"ADX roundtrip SNR {Snr(src,adOff,0):F1}/{Snr(src,adOff,1):F1} dB");
    src.LoopEnabled=true; src.LoopStart=5000; src.LoopEnd=40000;
    var aOn = new AdxCodec().Encode(src); var adOn = new AdxCodec().Decode(aOn);
    Check(adOn.LoopEnabled && adOn.LoopStart==(5000/32)*32 && adOn.LoopEnd==((40000+31)/32)*32, $"ADX loop ON -> {adOn.LoopStart}->{adOn.LoopEnd}");
    int cpy2=(aOn[2]<<8)|aOn[3]; int lsOff=(aOn[0x20]<<24)|(aOn[0x21]<<16)|(aOn[0x22]<<8)|aOn[0x23];
    Check(lsOff==cpy2+4+(adOn.LoopStart/32)*18*2, $"ADX loop start byte offset 0x{lsOff:X} matches data layout");
    Check(aOn[aOn.Length-18]==0x80 && aOn[aOn.Length-17]==0x01, "ADX end-of-stream frame present");
    Check(AudioIO.Detect(aOn,"x.adx")==FileKind.Adx && AudioIO.Detect(vOn,"x.vag")==FileKind.Vag, "format detection");

    // ---------- WAV
    src.LoopEnabled=false; var w0 = StandardCodec.EncodeWavBytes(src);
    Check(Encoding.ASCII.GetString(w0).IndexOf("smpl")<0, "WAV loop OFF -> no smpl chunk");
    src.LoopEnabled=true; var w1 = StandardCodec.EncodeWavBytes(src);
    Check(Encoding.ASCII.GetString(w1).IndexOf("smpl")>0, "WAV loop ON -> smpl chunk");
    var tmpw = Path.GetTempFileName()+".wav"; File.WriteAllBytes(tmpw,w1);
    var wd = StandardCodec.DecodeFile(tmpw); Check(wd.LoopEnabled && wd.LoopStart==5000 && wd.LoopEnd==40000, $"WAV smpl loop read back {wd.LoopStart}->{wd.LoopEnd}");

    // ---------- resample
    var rs = Sine(44100,1,1.0,1000).Resample(22050); Check(Math.Abs(rs.TotalFrames-22050)<=1, "resample length");

    // ---------- AFS
    var afs = new AfsArchive(); afs.New();
    afs.Add("a.adx", aOff); afs.Add("b.vag", vOff); afs.Add("c.bin", new byte[]{1,2,3,4,5});
    var ap = Path.GetTempFileName(); afs.Save(ap);
    var a2 = new AfsArchive(); a2.Open(ap);
    Check(a2.Entries.Count==3 && a2.HasNames && a2.Entries[0].FileName=="a.adx" && a2.Entries[1].Data.SequenceEqual(vOff) && a2.Entries[2].Data.Length==5, "AFS save/open roundtrip with filename table");
    a2.ReplaceData(2, new byte[3000]); a2.Save(ap); var a3=new AfsArchive(); a3.Open(ap);
    Check(a3.Entries[2].Data.Length==3000 && a3.Entries[0].Data.SequenceEqual(aOff) && (a3.Entries[1].Offset%0x800)==0, "AFS replace + realign");

    // ---------- Synthetic PSF2 bank + SQ
    var tone = Sine(44100,1,28*100/44100.0,440); // exactly 2800 samples
    // make a loop-friendly 440Hz cycle count: 2800/44100*440 = 27.94 cycles -> small click, fine for test
    tone.LoopEnabled=true; tone.LoopStart=0; tone.LoopEnd=2800;
    byte[] adpcm = VagCodec.EncodeAdpcm(tone.Samples,true,0,2800);
    var vagi = new MemoryStream(); W32(vagi,0); W16(vagi,44100); vagi.WriteByte(1); vagi.WriteByte(0);
    var smp = new MemoryStream(); W16(smp,0); smp.WriteByte(0); smp.WriteByte(0); smp.WriteByte(127); while(smp.Length<11) smp.WriteByte(0); smp.WriteByte(69); smp.WriteByte(0); smp.WriteByte(64); smp.WriteByte(0); smp.WriteByte(0); smp.WriteByte(127); smp.WriteByte(0); W16(smp,0x000F); W16(smp,0x1F08); while(smp.Length<42) smp.WriteByte(0);
    var set = new MemoryStream(); set.WriteByte(0); set.WriteByte(0); set.WriteByte(127); set.WriteByte(1); W16(set,0);
    var prog = new MemoryStream(); W32(prog,0x24); prog.WriteByte(1); prog.WriteByte(20); prog.WriteByte(127); prog.WriteByte(64); prog.WriteByte(0); prog.WriteByte(0); while(prog.Length<0x24) prog.WriteByte(0);
    W16(prog,0); prog.WriteByte(0); prog.WriteByte(0); prog.WriteByte(127); prog.WriteByte(0); while(prog.Length<0x24+16) prog.WriteByte(0); prog.WriteByte(127); prog.WriteByte(64); prog.WriteByte(0); prog.WriteByte(0);
    var cProg = Chunk("IECSgorP",0,new[]{prog.ToArray()},0x20);
    var cSet  = Chunk("IECSlpmS",0,new[]{set.ToArray()},0x20);
    var cSmp  = Chunk("IECSpmaS",0,new[]{smp.ToArray()},0x20);
    var cVagi = Chunk("IECSigaV",0,new[]{vagi.ToArray()},0x20);
    var hd = new MemoryStream();
    hd.Write(Encoding.ASCII.GetBytes("IECSsreV"),0,8); W32(hd,0x10); W32(hd,0x00020001);
    int hdrAt = 0x10; int pA=0x50, sA=pA+cProg.Length, mA=sA+cSet.Length, vA=mA+cSmp.Length;
    hd.Write(Encoding.ASCII.GetBytes("IECSdaeH"),0,8); W32(hd,0x40); W32(hd,(uint)(vA+cVagi.Length)); W32(hd,(uint)adpcm.Length); W32(hd,(uint)pA); W32(hd,(uint)sA); W32(hd,(uint)mA); W32(hd,(uint)vA);
    while (hd.Length<0x50) hd.WriteByte(0);
    hd.Write(cProg,0,cProg.Length); hd.Write(cSet,0,cSet.Length); hd.Write(cSmp,0,cSmp.Length); hd.Write(cVagi,0,cVagi.Length);
    string dir = Path.Combine(Path.GetTempPath(),"psf2t"); Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir,"test.hd"),hd.ToArray()); File.WriteAllBytes(Path.Combine(dir,"test.bd"),adpcm);
    var bank = new Psf2Bank(); bank.LoadHD(Path.Combine(dir,"test.hd"));
    Check(bank.Samples.Count==1 && bank.Samples[0].Valid && bank.Samples[0].Loops && bank.Samples[0].Decoded.LoopEnabled, "bank: HD parsed, 1 looped sample");
    Check(bank.Programs.ContainsKey(0) && bank.Programs[0].Splits.Count==1 && bank.SampleSets[0].SampleParams.Count==1 && bank.SampleParams[0].BaseNote==69, "bank: program/split/set/param parsed");
    bank.BuildFiles(out var nh, out var nb);
    Check(nb.SequenceEqual(adpcm) && nh.SequenceEqual(hd.ToArray()), "bank: unmodified rebuild is byte-identical");

    // SQ (hand-built stream): tempo, note on A4, note off after 480 ticks, end
    var ev = new MemoryStream(); W32(ev,6); W16(ev,480);
    ev.Write(new byte[]{0x00,0xFF,0x51,0x03,0x07,0xA1,0x20, 0x00,0x90,69,100, 0x83,0x60,0x80,69, 0x00,0xFF,0x2F,0x00},0,19);
    while(ev.Length%16!=0) ev.WriteByte(0);
    var mid = new MemoryStream(); mid.Write(Encoding.ASCII.GetBytes("IECSidiM"),0,8); W32(mid,0); W32(mid,0); W32(mid,0x20); while(mid.Length<0x20) mid.WriteByte(0); mid.Write(ev.ToArray(),0,(int)ev.Length);
    var midArr = mid.ToArray(); Array.Copy(BitConverter.GetBytes(midArr.Length),0,midArr,8,4);
    var sqb = new MemoryStream(); sqb.Write(Encoding.ASCII.GetBytes("IECSsreV"),0,8); W32(sqb,0x10); W32(sqb,0x00020001);
    sqb.Write(Encoding.ASCII.GetBytes("IECSdaeH"),0,8); W32(sqb,0x20); W32(sqb,0); W32(sqb,0); W32(sqb,0); W32(sqb,0); W32(sqb,0);
    sqb.Write(midArr,0,midArr.Length);
    Check(Psf2Bank.IsSequenceFile(sqb.ToArray()) && !Psf2Bank.IsSequenceFile(hd.ToArray()), "SQ vs HD identification");
    var sq = new SqFile(); sq.Parse(sqb.ToArray());
    var song = sq.Songs[0];
    Check(song.Ppqn==480 && song.NoteCount==1 && Math.Abs(song.DurationSeconds-0.5)<0.001, $"SQ parsed: ppqn {song.Ppqn}, notes {song.NoteCount}, dur {song.DurationSeconds:F3}s");
    var rendered = Psf2Renderer.Render(bank, song);
    int crossings=0; int i0=(int)(0.1*48000), i1=(int)(0.4*48000);
    for (int i=i0+1;i<i1;i++){ if ((rendered.Samples[i*2-2]<0) != (rendered.Samples[i*2]<0)) crossings++; }
    double hz = crossings/2.0/((i1-i0)/48000.0);
    float peak = rendered.Samples.Max(x=>Math.Abs(x));
    Check(peak>0.05f && Math.Abs(hz-440)<20, $"render: peak {peak:F2}, measured pitch {hz:F0} Hz (expected ~440), length {rendered.DurationSeconds:F2}s");
    // SMF roundtrip
    var smf = song.ToSmf(); var back = SqSong.FromSmf(smf,"x");
    Check(back.NoteCount==1 && back.Ppqn==480 && Math.Abs(back.DurationSeconds-0.5)<0.001, "SMF export -> import roundtrip");
    var newSq = sq.BuildWith(0, back); var sq2 = new SqFile(); sq2.Parse(newSq);
    Check(sq2.Songs.Count==1 && sq2.Songs[0].NoteCount==1 && Math.Abs(sq2.Songs[0].DurationSeconds-0.5)<0.001 && Psf2Bank.IsSequenceFile(newSq), "SQ replace song -> reparse OK");
    // replace sample
    var repl = Sine(22050,1,0.5,880); repl.LoopEnabled=false;
    bank.ReplaceSample(0, repl, true);
    Check(bank.Samples[0].SampleRate==44100 && !bank.Samples[0].Loops && bank.Samples[0].Replaced, "bank: replace sample (rate kept, loop off)");
    bank.BuildFiles(out var h2, out var b2); File.WriteAllBytes(Path.Combine(dir,"t2.hd"),h2); File.WriteAllBytes(Path.Combine(dir,"t2.bd"),b2);
    var bank2 = new Psf2Bank(); bank2.LoadHD(Path.Combine(dir,"t2.hd"));
    Check(bank2.Samples[0].Valid && !bank2.Samples[0].Loops && Math.Abs(bank2.Samples[0].Decoded.DurationSeconds-0.5)<0.02, "bank: saved replacement reloads correctly");

    Console.WriteLine(fails==0 ? "ALL TESTS PASSED" : fails+" FAILURES");
    return fails;
  }
}
