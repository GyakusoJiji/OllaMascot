using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace OllaMonitor
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private Mutex? _instanceMutex;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

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
            // Enforce a single instance: a second copy silently stacks on the same
            // saved position and later overwrites settings.json with stale bounds
            _instanceMutex = new Mutex(true, @"Local\OllaMonitor_SingleInstance", out bool isNewInstance);
            if (!isNewInstance)
            {
                Log("Another instance is already running. Activating it and exiting.");
                IntPtr existing = FindWindow(null, "OllaMonitor");
                if (existing != IntPtr.Zero)
                {
                    SetForegroundWindow(existing);
                }
                Shutdown();
                return;
            }

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
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
