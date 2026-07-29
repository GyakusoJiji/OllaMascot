using System;
using System.Diagnostics;
using System.IO;

namespace OllaMascot
{
    /// <summary>
    /// Starts the Ollama server. Kept out of MainWindow so launching does not depend on any UI state.
    /// </summary>
    public static class OllamaLauncher
    {
        public static bool Start(Settings settings)
        {
            try
            {
                // Discover the ollama CLI; fall back to "ollama" on PATH
                string ollamaPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Ollama", "ollama.exe"
                );

                if (!File.Exists(ollamaPath))
                {
                    ollamaPath = "ollama";
                }

                // The stored command can be pasted verbatim (e.g. "ollama run gemma3:12b");
                // strip the leading "ollama" token since the executable path is resolved above
                string command = (settings.OllamaStartCommand ?? "").Trim();
                if (command.Length == 0 || command.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                {
                    command = "serve";
                }
                else if (command.StartsWith("ollama ", StringComparison.OrdinalIgnoreCase))
                {
                    command = command.Substring("ollama ".Length).Trim();
                }

                // Launch the server headless: no console window, no tray icon
                Process.Start(new ProcessStartInfo
                {
                    FileName = ollamaPath,
                    Arguments = command,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                App.Log($"Started Ollama: {ollamaPath} {command}");
                return true;
            }
            catch (Exception ex)
            {
                App.Log($"Failed to start Ollama: {ex}");
                return false;
            }
        }
    }
}
