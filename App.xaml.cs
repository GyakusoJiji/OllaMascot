using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace OllaMascot
{
    /// <summary>
    /// Interaction logic for App.xaml. The app lives on the desktop: a 64x64 mascot window is always
    /// on screen and the dashboard is a second window it opens and hides. This class owns the
    /// metrics poller and both windows so polling keeps running while the dashboard is hidden.
    /// </summary>
    public partial class App : Application
    {
        private const string ActivateEventName = @"Local\OllaMascot_Activate";

        private Mutex? _instanceMutex;
        private EventWaitHandle? _activateEvent;
        private RegisteredWaitHandle? _activateWait;

        private MetricsService? _metrics;
        private MascotWindow? _mascotWindow;
        private DashboardWindow? _dashboard;
        private MascotIcons? _mascot;

        public static Settings SettingsInstance { get; private set; } = new Settings();

        /// <summary>
        /// Set once the app is genuinely shutting down, so the dashboard stops turning its close
        /// into a hide.
        /// </summary>
        public static bool IsExiting { get; private set; }

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
            _instanceMutex = new Mutex(true, @"Local\OllaMascot_SingleInstance", out bool isNewInstance);
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            if (!isNewInstance)
            {
                // Signalling the handle is more reliable than hunting for the window by title,
                // which fails whenever the running copy is minimised
                Log("Another instance is already running. Signalling it to activate.");
                _activateEvent.Set();
                Shutdown();
                return;
            }

            Log("App starting...");
            base.OnStartup(e);

            SettingsInstance = Settings.Load();

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

            _metrics = new MetricsService(SettingsInstance);
            _metrics.OllamaWentOffline += OnOllamaWentOffline;

            // The sheet is sliced once and shared: the mascot window animates through it, the
            // dashboard uses a frame as its window icon
            _mascot = MascotIcons.Load();

            // Built up front, not on first open: it subscribes to MetricsService in its constructor,
            // so this is what gives its sparklines a history to draw the moment it is shown
            _dashboard = new DashboardWindow(_metrics, SettingsInstance, _mascot);

            _mascotWindow = new MascotWindow(_metrics, SettingsInstance, _mascot);
            MainWindow = _mascotWindow;

            _mascotWindow.ShowDashboardRequested += (s, args) => ToggleDashboard();
            _mascotWindow.AlwaysOnTopChanged += (s, value) => _dashboard?.SyncAlwaysOnTop(value);
            _dashboard.AlwaysOnTopChanged += (s, value) => _mascotWindow?.SyncAlwaysOnTop(value);

            // The mascot is the app's presence now, so it is always on screen and the dashboard
            // stays hidden until it is asked for
            _mascotWindow.Show();

            // A later instance signals this handle rather than starting a second copy
            _activateWait = ThreadPool.RegisterWaitForSingleObject(
                _activateEvent,
                (state, timedOut) => Dispatcher.Invoke(ActivateMascot),
                null,
                Timeout.Infinite,
                false);

            _metrics.Start();
            Log("Mascot window shown and metrics service started.");
        }

        /// <summary>
        /// Opens the dashboard, or puts it away if it is already up. Clicking the mascot is both
        /// the way in and the way out.
        /// </summary>
        private void ToggleDashboard()
        {
            if (_dashboard == null)
                return;

            if (_dashboard.IsVisible && _dashboard.WindowState != WindowState.Minimized)
            {
                // Goes through Close so the hide path saves the bounds
                _dashboard.Close();
                return;
            }

            if (!_dashboard.IsVisible)
            {
                _dashboard.Show();
            }
            if (_dashboard.WindowState == WindowState.Minimized)
            {
                _dashboard.WindowState = WindowState.Normal;
            }
            _dashboard.Activate();
        }

        /// <summary>
        /// Brings the mascot forward. This is what a second launch asks for: the mascot is the app,
        /// so pointing the user at it is more useful than opening the dashboard.
        /// </summary>
        private void ActivateMascot()
        {
            if (_mascotWindow == null)
                return;

            if (!_mascotWindow.IsVisible)
            {
                _mascotWindow.Show();
            }
            if (_mascotWindow.WindowState != WindowState.Normal)
            {
                _mascotWindow.WindowState = WindowState.Normal;
            }
            _mascotWindow.Activate();
        }

        /// <summary>Ends the process. The dashboard's close button only hides it, so this is the way out.</summary>
        public static void RequestExit()
        {
            IsExiting = true;
            Current.Shutdown();
        }

        private void OnOllamaWentOffline(object? sender, EventArgs e)
        {
            Log("Ollama reported offline.");
            _dashboard?.PromptOllamaRestart();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log("App exiting...");
            IsExiting = true;

            _activateWait?.Unregister(null);
            _metrics?.Stop();

            // The dashboard is normally still open-but-hidden at this point, so its own close path
            // never runs
            _dashboard?.SaveBounds();
            _mascotWindow?.SavePosition();

            try
            {
                Nvml.Shutdown();
                Log("NVML shutdown completed.");
            }
            catch (Exception ex)
            {
                Log($"Exception during NVML shutdown: {ex}");
            }

            _activateEvent?.Dispose();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
