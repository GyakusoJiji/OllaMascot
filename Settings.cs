using System;
using System.IO;
using System.Text.Json;

namespace OllaMonitor
{
    public class Settings
    {
        public string OllamaUrl { get; set; } = "http://localhost:11434";
        public double RefreshIntervalSeconds { get; set; } = 2.0;
        public bool AlwaysOnTop { get; set; } = true;

        private static readonly string FolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OllaMonitor"
        );
        private static readonly string FilePath = Path.Combine(FolderPath, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                }
            }
            catch
            {
                // Fallback to default settings if file read or parse fails
            }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(FolderPath))
                {
                    Directory.CreateDirectory(FolderPath);
                }
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // Fail silently or handle
            }
        }
    }
}
