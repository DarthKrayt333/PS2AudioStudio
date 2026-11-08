namespace PS2AudioStudio.Audio
{
    public interface IAudioCodec
    {
        string FormatName { get; }
        string FileExtension { get; }
        AudioBuffer Decode(byte[] fileData);
        byte[] Encode(AudioBuffer buffer);
    }
}
