using System;
using System.IO;
using System.Text.Json;

namespace PS2AudioStudio
{
    public enum NewProjectMode { ActiveTabOnly = 0, AllTabs = 1, Custom = 2 }
    public enum RecordSourceKind { Capture = 0, Loopback = 1 }

    public sealed class AppSettings
    {
        private const int CurrentVersion = 5;

        /// <summary>Settings file version. Older files get the newer defaults once (see Load).</summary>
        public int Version { get; set; } = 0;

        // default = PC / desktop audio (Windows default playback device)
        public RecordSourceKind RecordKind { get; set; } = RecordSourceKind.Loopback;
        public string RecordDeviceId { get; set; } = "";       // empty = Windows default of that kind
        public string RecordDeviceName { get; set; } = "";

        /// <summary>Recording sample rate in Hz. Default 48000. The recording is converted to this rate when it stops.</summary>
        public int RecordRate { get; set; } = 48000;

        /// <summary>Last rate picked in the Save dialog (0 = keep the original rate).</summary>
        public int SaveRate { get; set; } = 0;

        // File > New: default = clear ALL tabs
        public NewProjectMode NewMode { get; set; } = NewProjectMode.AllTabs;
        public bool NewClearEditor { get; set; } = true;       // used by Custom
        public bool NewClearPsf2 { get; set; } = true;
        public bool NewClearPsf { get; set; } = true;          // PSF (PS1) tab, used by Custom
        public bool NewClearAfs { get; set; } = true;

        public bool PlayLoopedWhenChecked { get; set; } = true;

        /// <summary>Show the "sample rate" question when saving. Default ON; only changeable in Settings.</summary>
        public bool AskSaveRate { get; set; } = true;

        public bool MakeBackups { get; set; } = true;

        /// <summary>ADX loop layout: 0 = CRI standard header (07 FC, adds a little silence), 1 = compact (no silence, non-standard header size).</summary>
        public int AdxLayoutMode { get; set; } = 1;


        public static AppSettings Current { get; private set; } = Load();

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PS2AudioStudio", "settings.json");

        public static AppSettings Load()
        {
            AppSettings s = null;
            try
            {
                if (File.Exists(FilePath))
                    s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            }
            catch { }

            if (s == null) return new AppSettings { Version = CurrentVersion };

            if (s.Version < CurrentVersion)
            {
                // one-time migrations, each runs only for files older than that version
                if (s.Version < 2) s.NewMode = NewProjectMode.AllTabs;   // File > New default = all tabs
                if (s.Version < 3) s.AskSaveRate = true;                 // always ask for the sample rate by default
                if (s.Version < 4)                                       // recording default = desktop audio
                {
                    s.RecordKind = RecordSourceKind.Loopback;
                    s.RecordDeviceId = "";
                    s.RecordDeviceName = "";
                }
                if (s.Version < 5)                                       // recording rate default = 48000
                {
                    s.RecordRate = 48000;
                    s.SaveRate = 0;
                }
                s.Version = CurrentVersion;
                s.Save();
            }
            if (s.RecordRate < 1000 || s.RecordRate > 384000) s.RecordRate = 48000;
            if (s.SaveRate != 0 && (s.SaveRate < 1000 || s.SaveRate > 384000)) s.SaveRate = 0;
            return s;
        }

        public void Save()
        {
            try
            {
                Version = CurrentVersion;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        /// <summary>tab: 0 = Audio Editor, 1 = PSF2, 2 = AFS, 3 = PSF (PS1). The active tab is always cleared.</summary>
        public bool ShouldClear(int tab, int activeTab)
        {
            if (tab == activeTab) return true;
            switch (NewMode)
            {
                case NewProjectMode.AllTabs: return true;
                case NewProjectMode.Custom:
                    return (tab == 0 && NewClearEditor) || (tab == 1 && NewClearPsf2)
                        || (tab == 2 && NewClearAfs) || (tab == 3 && NewClearPsf);
                default: return false;
            }
        }
    }
}
