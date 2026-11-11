using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace LinkDownloader
{
    public static class HttpUtil
    {
        private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "download";
            }

            name = name.Trim();
            int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (slash >= 0 && slash < name.Length - 1)
            {
                name = name.Substring(slash + 1);
            }

            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool bad = c < 32 || Array.IndexOf(InvalidNameChars, c) >= 0;
                sb.Append(bad ? '_' : c);
            }

            name = sb.ToString().Trim().TrimEnd('.');
            if (name.Length == 0)
            {
                name = "download";
            }

            if (name.Length > 160)
            {
                string ext = Path.GetExtension(name);
                if (ext.Length > 20)
                {
                    ext = "";
                }
                name = name.Substring(0, 160 - ext.Length) + ext;
            }

            if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            {
                name = "_" + name;
            }

            return name;
        }

        public static string InferFileName(string url)
        {
            string result = "";
            try
            {
                Uri uri = new Uri(url);
                string path = Uri.UnescapeDataString(uri.AbsolutePath);
                result = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(result) || result == "/")
                {
                    result = uri.Host;
                }
            }
            catch
            {
                result = "download";
            }

            result = SanitizeFileName(result);
            if (result.IndexOf('.') < 0)
            {
                result = result + ".bin";
            }
            return result;
        }

        public static string CategoryForExtension(string ext)
        {
            if (ext == null) ext = "";
            ext = ext.TrimStart('.').ToLowerInvariant();

            if (In(ext, "mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "mpg", "mpeg", "ts", "m2ts", "3gp", "ogv")) return "Video";
            if (In(ext, "mp3", "wav", "flac", "aac", "ogg", "oga", "m4a", "wma", "opus", "aiff")) return "Audio";
            if (In(ext, "jpg", "jpeg", "png", "gif", "webp", "bmp", "tif", "tiff", "svg", "ico", "avif", "heic")) return "Image";
            if (In(ext, "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab")) return "Archive";
            if (In(ext, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "csv", "epub", "mobi", "md")) return "Document";
            if (In(ext, "exe", "msi", "dll", "apk", "appx", "msix", "bat", "cmd", "ps1", "jar")) return "Program";
            if (In(ext, "html", "htm", "css", "js", "json", "xml", "php", "asp", "aspx", "jsp")) return "Web";
            return "Other";
        }

        private static bool In(string value, params string[] set)
        {
            for (int i = 0; i < set.Length; i++)
            {
                if (set[i] == value) return true;
            }
            return false;
        }

        public static bool IsTextContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return false;
            contentType = contentType.ToLowerInvariant();
            return contentType.StartsWith("text/") ||
                   contentType.Contains("html") ||
                   contentType.Contains("xml") ||
                   contentType.Contains("json") ||
                   contentType.Contains("javascript");
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "?";
            if (bytes < 1024L) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            double value = bytes;
            string[] units = new string[] { "KB", "MB", "GB", "TB" };
            int unit = -1;
            do
            {
                value /= 1024.0;
                unit++;
            }
            while (value >= 1024.0 && unit < units.Length - 1);

            return value.ToString(value >= 100 ? "0" : (value >= 10 ? "0.0" : "0.00"), CultureInfo.InvariantCulture) + " " + units[unit];
        }

        public static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "";
            return FormatBytes((long)bytesPerSecond) + "/s";
        }

        public static string FormatEta(double seconds)
        {
            if (seconds <= 0 || double.IsInfinity(seconds) || double.IsNaN(seconds)) return "";
            if (seconds > 359999) return ">99h";
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 1) return string.Format(CultureInfo.InvariantCulture, "{0:0}h {1:00}m", Math.Floor(t.TotalHours), t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format(CultureInfo.InvariantCulture, "{0:0}m {1:00}s", Math.Floor(t.TotalMinutes), t.Seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0:0}s", Math.Ceiling(t.TotalSeconds));
        }

        public static string GetDownloadsFolder()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                {
                    if (key != null)
                    {
                        string value = key.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
                        if (!string.IsNullOrEmpty(value))
                        {
                            value = Environment.ExpandEnvironmentVariables(value);
                            if (Directory.Exists(value)) return value;
                        }
                    }
                }
            }
            catch
            {
            }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloads = Path.Combine(profile, "Downloads");
            if (Directory.Exists(downloads)) return downloads;
            return profile;
        }

        public static List<KeyValuePair<string, string>> ParseExtraHeaders(string text)
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(text)) return result;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (name.Length > 0)
                {
                    result.Add(new KeyValuePair<string, string>(name, value));
                }
            }
            return result;
        }
    }
}
