using System;
using System.IO;
using System.Runtime.Serialization;

namespace LinkDownloader
{
    [DataContract]
    public class AppSettings
    {
        [DataMember] public string OutputFolder { get; set; }
        [DataMember] public int ParallelFiles { get; set; }
        [DataMember] public int SegmentsPerFile { get; set; }
        [DataMember] public int Retries { get; set; }
        [DataMember] public bool AutoResume { get; set; }
        [DataMember] public string ExtraHeaders { get; set; }

        public static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AllLinksDownloader",
                    "settings.json");
            }
        }

        public static AppSettings CreateDefault()
        {
            AppSettings s = new AppSettings();
            s.OutputFolder = Path.Combine(HttpUtil.GetDownloadsFolder(), "AllLinks");
            s.ParallelFiles = 2;
            s.SegmentsPerFile = 4;
            s.Retries = 4;
            s.AutoResume = false;
            s.ExtraHeaders = "";
            return s;
        }

        public static AppSettings Load()
        {
            AppSettings s = JsonStore.Load<AppSettings>(SettingsPath);
            if (s == null)
            {
                return CreateDefault();
            }
            s.Normalize();
            return s;
        }

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(OutputFolder))
            {
                OutputFolder = CreateDefault().OutputFolder;
            }
            if (ParallelFiles < 1) ParallelFiles = 1;
            if (ParallelFiles > 6) ParallelFiles = 6;
            if (SegmentsPerFile < 1) SegmentsPerFile = 1;
            if (SegmentsPerFile > 8) SegmentsPerFile = 8;
            if (Retries < 0) Retries = 0;
            if (Retries > 10) Retries = 10;
            if (ExtraHeaders == null) ExtraHeaders = "";
        }

        public void Save()
        {
            JsonStore.Save(SettingsPath, this);
        }
    }
}
