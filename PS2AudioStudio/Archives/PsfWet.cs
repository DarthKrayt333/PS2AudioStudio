namespace PS2AudioStudio.Archives
{
    /// <summary>Byte 41 of every sample entry that has reverb on (the value the export writes). Dry notes always get 03.</summary>
    public static class PsfWet
    {
        public const int Default = 0x27;

        public static int Byte41 = Default;
    }
}
