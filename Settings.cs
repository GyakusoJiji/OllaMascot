using System;
using System.IO;
using System.Text.Json;

namespace OllaMascot
{
    public class Settings
    {
        public string OllamaUrl { get; set; } = "http://localhost:11434";
        public string OllamaStartCommand { get; set; } = "ollama serve";
        public double RefreshIntervalSeconds { get; set; } = 2.0;
        public bool AlwaysOnTop { get; set; } = true;

        /// <summary>
        /// Where the mascot sits on the desktop. Kept separate from the dashboard's bounds below:
        /// the mascot is a fixed 64x64 and would otherwise inherit the dashboard's size.
        /// </summary>
        public double? MascotLeft { get; set; }
        public double? MascotTop { get; set; }

        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }

        private static readonly string FolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OllaMascot"
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
