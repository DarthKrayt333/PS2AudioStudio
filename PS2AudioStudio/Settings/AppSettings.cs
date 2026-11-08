using System;
using System.IO;
using System.Text.Json;

namespace PS2AudioStudio
{
    public enum NewProjectMode { ActiveTabOnly = 0, AllTabs = 1, Custom = 2 }
    public enum RecordSourceKind { Capture = 0, Loopback = 1 }

    public sealed class AppSettings
    {
        private const int CurrentVersion = 3;

        /// <summary>Settings file version. Older files get the newer defaults once (see Load).</summary>
        public int Version { get; set; } = 0;

        public RecordSourceKind RecordKind { get; set; } = RecordSourceKind.Capture;
        public string RecordDeviceId { get; set; } = "";       // empty = Windows default of that kind
        public string RecordDeviceName { get; set; } = "";

        // File > New: default = clear ALL tabs
        public NewProjectMode NewMode { get; set; } = NewProjectMode.AllTabs;
        public bool NewClearEditor { get; set; } = true;       // used by Custom
        public bool NewClearPsf2 { get; set; } = true;
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
                s.Version = CurrentVersion;
                s.Save();
            }
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

        /// <summary>tab: 0 = Audio Editor, 1 = PSF2, 2 = AFS. The active tab is always cleared.</summary>
        public bool ShouldClear(int tab, int activeTab)
        {
            if (tab == activeTab) return true;
            switch (NewMode)
            {
                case NewProjectMode.AllTabs: return true;
                case NewProjectMode.Custom:
                    return (tab == 0 && NewClearEditor) || (tab == 1 && NewClearPsf2) || (tab == 2 && NewClearAfs);
                default: return false;
            }
        }
    }
}
