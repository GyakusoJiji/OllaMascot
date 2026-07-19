using System;
using System.IO;
using System.Windows;

namespace OllaMonitor
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static readonly string LogFile = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "debug.log"
        );

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.FFF}] {message}{Environment.NewLine}");
            }
            catch 
            {
                // Fallback silently if disk write fails
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            Log("App starting...");
            base.OnStartup(e);
            
            try
            {
                Log("Initializing NVML...");
                bool nvmlOk = Nvml.Initialize();
                Log($"NVML initialization result: {nvmlOk}. GPU: {Nvml.GpuName}");
            }
            catch (Exception ex)
            {
                Log($"Exception during NVML initialization: {ex}");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log("App exiting...");
            try
            {
                Nvml.Shutdown();
                Log("NVML shutdown completed.");
            }
            catch (Exception ex)
            {
                Log($"Exception during NVML shutdown: {ex}");
            }
            base.OnExit(e);
        }
    }
}
