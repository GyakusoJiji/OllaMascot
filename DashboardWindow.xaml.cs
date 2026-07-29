using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace OllaMascot
{
    public partial class DashboardWindow : Window
    {
        private readonly Settings _settings;
        private readonly MetricsService _metrics;

        // History collections for trend lines
        private readonly List<double> _cpuHistory = new List<double>();
        private readonly List<double> _ramHistory = new List<double>();
        private readonly List<double> _gpuHistory = new List<double>();
        private readonly List<double> _vramHistory = new List<double>();

        private enum DetailsViewMode
        {
            SystemDetails,
            ActiveModels
        }
        private DetailsViewMode _currentMode = DetailsViewMode.SystemDetails;
        private bool _detailsExpanded = false;
        private MetricsService.Snapshot _snapshot = new MetricsService.Snapshot();
        private bool _restartPromptShown = false;

        // Last bounds observed while the window was actually restored on screen. A minimized window
        // reports Left/Top of -32000, and one that has never been shown reports NaN, so neither can
        // be read at save time — the position has to be captured as the user moves it.
        private Rect? _lastNormalBounds;

        // The desktop mascot plays the animation; this is the same sprite used as the window icon,
        // posed by GPU load, which is what Alt-Tab and dialogs show
        private readonly MascotIcons? _mascot;
        private int _lastMascotIndex = -1;

        /// <summary>Raised when the user toggles always-on-top here, so the mascot can follow.</summary>
        public event EventHandler<bool>? AlwaysOnTopChanged;

        public DashboardWindow(MetricsService metrics, Settings settings, MascotIcons? mascot)
        {
            App.Log("DashboardWindow constructor started.");
            _metrics = metrics;
            _settings = settings;
            _mascot = mascot;
            try
            {
                InitializeComponent();

                // Apply window behavior based on settings
                Topmost = _settings.AlwaysOnTop;
                RestoreWindowBounds();

                // Anything that does not need an HWND is initialized here rather than in
                // OnSourceInitialized
                OllamaUrlInput.Text = _settings.OllamaUrl;
                OllamaStartCommandInput.Text = _settings.OllamaStartCommand;
                RefreshRateSlider.Value = _settings.RefreshIntervalSeconds;
                RefreshRateLabel.Text = $"{_settings.RefreshIntervalSeconds:F0}s";
                AlwaysOnTopCheckbox.IsChecked = _settings.AlwaysOnTop;
                ContextAlwaysOnTop.IsChecked = _settings.AlwaysOnTop;
                GpuModelLabel.Text = Nvml.IsAvailable ? Nvml.GpuName : "No Nvidia GPU";

                // Start on the idle pose so the window never flashes a default icon
                if (_mascot != null)
                {
                    _lastMascotIndex = 0;
                    Icon = _mascot[0];
                }

                // Attach size changed events to redraw sparklines when layout settles
                CpuCanvas.SizeChanged += (s, e) => RedrawAllSparklines();
                RamCanvas.SizeChanged += (s, e) => RedrawAllSparklines();
                GpuCanvas.SizeChanged += (s, e) => RedrawAllSparklines();
                VramCanvas.SizeChanged += (s, e) => RedrawAllSparklines();

                // Follow the window as the user drags or resizes it, so the position carries over to
                // the next launch even though it is hidden rather than closed when the app exits
                LocationChanged += (s, e) => CaptureNormalBounds();
                SizeChanged += (s, e) => CaptureNormalBounds();
                StateChanged += (s, e) => CaptureNormalBounds();

                // The service polls whether or not this window is on screen; history accumulates
                // either way and is drawn once the canvases have a size
                _metrics.Updated += OnMetricsUpdated;
            }
            catch (Exception ex)
            {
                App.Log($"Exception in DashboardWindow constructor: {ex}");
            }
            App.Log("DashboardWindow constructor completed.");
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            App.Log("DashboardWindow OnSourceInitialized started.");
            try
            {
                base.OnSourceInitialized(e);

                // Apply Mica or Acrylic background effect for Windows 11
                App.Log("Applying system backdrop...");
                SystemBackdropHelper.ApplyBackdrop(this, useAcrylic: true);

                UpdatePinButtonState();
            }
            catch (Exception ex)
            {
                App.Log($"Exception in OnSourceInitialized: {ex}");
            }
            App.Log("DashboardWindow OnSourceInitialized completed.");
        }

        private void RestoreWindowBounds()
        {
            if (_settings.WindowWidth is double width && width >= MinWidth)
            {
                Width = width;
            }
            if (_settings.WindowHeight is double height && height >= MinHeight)
            {
                Height = height;
            }
            if (_settings.WindowLeft is double left && _settings.WindowTop is double top)
            {
                // Only restore the position if it is still within the virtual screen,
                // e.g. a monitor may have been disconnected since last run
                double screenLeft = SystemParameters.VirtualScreenLeft;
                double screenTop = SystemParameters.VirtualScreenTop;
                double screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
                double screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
                if (left + Width > screenLeft && left < screenRight &&
                    top + Height > screenTop && top < screenBottom)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = left;
                    Top = top;
                }
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel)
                return;

            // The desktop mascot is the app's real presence, so closing the dashboard only puts it
            // away; monitoring carries on and Exit from the mascot's menu is what ends the process
            if (!App.IsExiting)
            {
                e.Cancel = true;
                HideDashboard();
            }
        }

        /// <summary>Puts the dashboard away, keeping its position for the next time it is opened.</summary>
        private void HideDashboard()
        {
            SaveBounds();
            Hide();
        }

        /// <summary>Records the bounds whenever the window is restored, ignoring minimized state.</summary>
        private void CaptureNormalBounds()
        {
            if (WindowState != WindowState.Normal || !IsVisible)
                return;
            if (double.IsNaN(Left) || double.IsNaN(Top))
                return;

            _lastNormalBounds = new Rect(Left, Top, Width, Height);
        }

        /// <summary>Persists the last on-screen bounds so the next run reopens where the user left it.</summary>
        public void SaveBounds()
        {
            // No captured bounds means the dashboard was never opened this run; keeping whatever is
            // already on disk is correct, and it avoids writing the -32000 / NaN placeholders that a
            // minimized or never-shown window reports
            if (_lastNormalBounds is not Rect bounds)
                return;

            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.Save();
        }

        /// <summary>Applies an always-on-top change made elsewhere, without echoing it back.</summary>
        public void SyncAlwaysOnTop(bool value)
        {
            Topmost = value;
            ContextAlwaysOnTop.IsChecked = value;
            AlwaysOnTopCheckbox.IsChecked = value;
            UpdatePinButtonState();
        }

        private void ApplyAlwaysOnTop(bool value)
        {
            SyncAlwaysOnTop(value);
            AlwaysOnTopChanged?.Invoke(this, value);
        }

        private void OnMetricsUpdated(object? sender, MetricsService.Snapshot snapshot)
        {
            _snapshot = snapshot;
            try
            {
                // 0. Window icon: the mascot's pose is the GPU load indicator, mirroring the tray.
                // Reassigning Icon costs a WM_SETICON round trip, so only do it on a frame change.
                if (_mascot != null)
                {
                    int index = _mascot.IndexForPercent(snapshot.GpuAvailable ? snapshot.GpuPercent : 0);
                    if (index != _lastMascotIndex)
                    {
                        _lastMascotIndex = index;
                        Icon = _mascot[index];
                    }
                }

                // 0b. The live readout the tray tooltip carries, repeated here for Alt-Tab
                Title = MetricsService.FormatSummary(snapshot);

                // 1. CPU Metrics
                CpuText.Text = $"{snapshot.CpuPercent:F1}%";
                CpuProgress.Value = snapshot.CpuPercent;
                UpdateSparkline(CpuPolyline, CpuCanvas, _cpuHistory, snapshot.CpuPercent);

                // 2. RAM Metrics
                RamText.Text = $"{snapshot.RamPercent:F1}%";
                RamProgress.Value = snapshot.RamPercent;
                UpdateSparkline(RamPolyline, RamCanvas, _ramHistory, snapshot.RamPercent);

                // 3. GPU & VRAM Metrics (NVIDIA NVML)
                if (snapshot.GpuAvailable)
                {
                    GpuText.Text = $"{snapshot.GpuPercent:F0}%";
                    GpuProgress.Value = snapshot.GpuPercent;
                    UpdateSparkline(GpuPolyline, GpuCanvas, _gpuHistory, snapshot.GpuPercent);

                    VramText.Text = $"{snapshot.VramUsedGb:F1} GB";
                    VramProgress.Value = snapshot.VramPercent;
                    UpdateSparkline(VramPolyline, VramCanvas, _vramHistory, snapshot.VramPercent);
                }
                else
                {
                    // Fallback / Inactive GPU state
                    GpuText.Text = "N/A";
                    GpuProgress.Value = 0;
                    UpdateSparkline(GpuPolyline, GpuCanvas, _gpuHistory, 0);

                    VramText.Text = "N/A";
                    VramProgress.Value = 0;
                    UpdateSparkline(VramPolyline, VramCanvas, _vramHistory, 0);
                }

                // 4. Ollama status footer
                if (!snapshot.OllamaReachable)
                {
                    OllamaStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 69, 58)); // System Red
                    StatusFooterText.Text = "Ollama connection failed";
                    OllamaRestartButton.Visibility = Visibility.Visible;
                }
                else if (snapshot.ActiveModels.Count == 0)
                {
                    OllamaStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(142, 142, 147)); // System Gray
                    StatusFooterText.Text = "Ollama is idle";
                    OllamaRestartButton.Visibility = Visibility.Collapsed;
                    _restartPromptShown = false;
                }
                else
                {
                    OllamaStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(48, 209, 88)); // System Green
                    StatusFooterText.Text = $"{snapshot.ActiveModels.Count} model(s) active";
                    OllamaRestartButton.Visibility = Visibility.Collapsed;
                    _restartPromptShown = false;
                }

                // 5. Update Unified Details List
                RefreshDetailsDisplay();
            }
            catch (Exception ex)
            {
                App.Log($"Exception in OnMetricsUpdated: {ex}");
            }
        }

        /// <summary>Offers to start Ollama. Shown once per offline period; reset when it returns.</summary>
        public void PromptOllamaRestart()
        {
            if (_restartPromptShown)
                return;
            _restartPromptShown = true;

            const string message = "Ollama is not running.\nDo you want to restart Ollama?";
            const string caption = "OllaMascot - Ollama Not Detected";

            // The dashboard may never have been opened this run, in which case it has no HWND to
            // own the dialog and the ownerless overload is the only one that works
            bool hasHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle != IntPtr.Zero;
            var result = hasHandle
                ? MessageBox.Show(this, message, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : MessageBox.Show(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                StartOllama();
            }
        }

        private void DetailsHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            _detailsExpanded = !_detailsExpanded;
            ApplyDetailsExpansion();
        }

        private void ApplyDetailsExpansion()
        {
            if (_detailsExpanded)
            {
                // Shrink the graph area to its compact size and give the rest to the list
                CardsRow.Height = new GridLength(130);
                DetailsRow.Height = new GridLength(1, GridUnitType.Star);
                DetailsListBox.Visibility = Visibility.Visible;
                DetailsModeButton.Visibility = Visibility.Visible;
                DetailsChevron.Text = "▲";
                RefreshDetailsDisplay();
            }
            else
            {
                // Collapse the list; the graph area reclaims all flexible space
                CardsRow.Height = new GridLength(1, GridUnitType.Star);
                DetailsRow.Height = GridLength.Auto;
                DetailsListBox.Visibility = Visibility.Collapsed;
                DetailsModeButton.Visibility = Visibility.Collapsed;
                DetailsChevron.Text = "▼";
            }
        }

        private void DetailsModeButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle view mode
            _currentMode = _currentMode == DetailsViewMode.SystemDetails
                ? DetailsViewMode.ActiveModels
                : DetailsViewMode.SystemDetails;

            // Update header text
            DetailsHeaderText.Text = _currentMode == DetailsViewMode.SystemDetails
                ? "SYSTEM DETAILS"
                : "ACTIVE MODELS";

            // Refresh list display immediately
            RefreshDetailsDisplay();
        }

        private void RefreshDetailsDisplay()
        {
            try
            {
                if (!_detailsExpanded)
                    return;

                DetailsListBox.Items.Clear();

                if (_currentMode == DetailsViewMode.SystemDetails)
                {
                    // 1. RAM info
                    DetailsListBox.Items.Add(CreateSystemDetailItem("System Memory:", $"{_snapshot.RamUsedGb:F1} / {_snapshot.RamTotalGb:F1} GB ({_snapshot.RamPercent:F0}%)"));

                    // 2. VRAM info
                    DetailsListBox.Items.Add(CreateSystemDetailItem("Graphics VRAM:", _snapshot.GpuAvailable
                        ? $"{_snapshot.VramUsedGb:F1} / {_snapshot.VramTotalGb:F1} GB ({_snapshot.VramPercent:F0}%)"
                        : "N/A"));

                    // 3. GPU Model
                    DetailsListBox.Items.Add(CreateSystemDetailItem("GPU Model:", Nvml.IsAvailable ? Nvml.GpuName : "None / Non-Nvidia"));

                    // 4. Ollama URL
                    DetailsListBox.Items.Add(CreateSystemDetailItem("Ollama Endpoint:", _settings.OllamaUrl));

                    // 5. Ollama version
                    DetailsListBox.Items.Add(CreateSystemDetailItem("Ollama Version:", _snapshot.OllamaVersion ?? "N/A"));

                    // 6. Currently loaded LLM model(s)
                    string modelNames = _snapshot.ActiveModels.Count > 0
                        ? string.Join(", ", _snapshot.ActiveModels.ConvertAll(m => m.Name))
                        : "None";
                    DetailsListBox.Items.Add(CreateSystemDetailItem("Active Model:", modelNames));
                }
                else // ActiveModels
                {
                    if (!_snapshot.OllamaReachable)
                    {
                        DetailsListBox.Items.Add(CreateMessageItem("Ollama is offline or unreachable."));
                    }
                    else if (_snapshot.ActiveModels.Count == 0)
                    {
                        DetailsListBox.Items.Add(CreateMessageItem("Ollama is idle. No models loaded."));
                    }
                    else
                    {
                        foreach (var model in _snapshot.ActiveModels)
                        {
                            DetailsListBox.Items.Add(CreateActiveModelItem(model));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log($"Exception in RefreshDetailsDisplay: {ex}");
            }
        }

        private ListBoxItem CreateSystemDetailItem(string label, string value)
        {
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            
            var labelTxt = new TextBlock 
            { 
                Text = label, 
                Foreground = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)), 
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center 
            };
            
            var valTxt = new TextBlock 
            { 
                Text = value, 
                Foreground = Brushes.White, 
                FontSize = 12, 
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center 
            };
            Grid.SetColumn(valTxt, 1);
            
            grid.Children.Add(labelTxt);
            grid.Children.Add(valTxt);
            
            return new ListBoxItem { Content = grid, Padding = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        }

        private ListBoxItem CreateActiveModelItem(OllamaClient.ActiveModel model)
        {
            var mainStack = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };
            
            var headerGrid = new Grid();
            var nameStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(new TextBlock { Text = "⬤ ", Foreground = new SolidColorBrush(Color.FromRgb(48, 213, 200)), FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            nameStack.Children.Add(new TextBlock { Text = model.Name, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            
            var badgeBorder = new Border 
            { 
                Background = new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)), 
                CornerRadius = new CornerRadius(3), 
                Padding = new Thickness(4, 1, 4, 1), 
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            badgeBorder.Child = new TextBlock { Text = model.Details.ParameterSize, Foreground = new SolidColorBrush(Color.FromRgb(142, 142, 147)), FontSize = 10, FontWeight = FontWeights.SemiBold };
            
            headerGrid.Children.Add(nameStack);
            headerGrid.Children.Add(badgeBorder);
            
            var infoTxt = new TextBlock { Text = model.FormattedVramInfo, Foreground = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)), FontSize = 11, Margin = new Thickness(0, 3, 0, 3) };

            // Extended model settings from /api/ps and cached /api/show data
            var showInfo = _metrics.GetModelInfo(model.Name);

            var specs = new List<string>();
            if (!string.IsNullOrEmpty(model.Details.Family))
                specs.Add($"Family: {model.Details.Family}");
            if (!string.IsNullOrEmpty(model.Details.QuantizationLevel))
                specs.Add($"Quant: {model.Details.QuantizationLevel}");
            if (!string.IsNullOrEmpty(model.Details.Format))
                specs.Add($"Format: {model.Details.Format}");
            long? contextLength = model.ContextLength ?? showInfo?.GetModelInfoNumber(".context_length");
            if (contextLength.HasValue)
                specs.Add($"Context: {contextLength:N0}");
            if (model.ExpiresAt.HasValue)
                specs.Add($"Expires: {model.ExpiresAt.Value.ToLocalTime():HH:mm}");

            var extraLines = new List<string>();
            if (specs.Count > 0)
                extraLines.Add(string.Join("  •  ", specs));
            if (showInfo != null && showInfo.Capabilities.Count > 0)
                extraLines.Add($"Capabilities: {string.Join(", ", showInfo.Capabilities)}");
            if (showInfo != null && !string.IsNullOrWhiteSpace(showInfo.Parameters))
            {
                string formatted = FormatModelParameters(showInfo.Parameters);
                if (formatted.Length > 0)
                    extraLines.Add($"Params: {formatted}");
            }

            var progress = new ProgressBar
            { 
                Height = 3, 
                Value = model.VramPercentage, 
                Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), 
                Foreground = new SolidColorBrush(Color.FromRgb(48, 213, 200)), 
                BorderThickness = new Thickness(0) 
            };
            
            mainStack.Children.Add(headerGrid);
            mainStack.Children.Add(infoTxt);
            foreach (var line in extraLines)
            {
                mainStack.Children.Add(new TextBlock
                {
                    Text = line,
                    Foreground = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 3)
                });
            }
            mainStack.Children.Add(progress);
            
            var border = new Border 
            { 
                Background = new SolidColorBrush(Color.FromArgb(12, 255, 255, 255)), 
                CornerRadius = new CornerRadius(6), 
                Padding = new Thickness(8, 6, 8, 6), 
                Margin = new Thickness(0, 0, 0, 4) 
            };
            border.Child = mainStack;
            
            return new ListBoxItem { Content = border, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        }

        private static string FormatModelParameters(string parameters)
        {
            // /api/show "parameters" is newline-separated "key   value" pairs
            var parts = new List<string>();
            foreach (var raw in parameters.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;
                int split = line.IndexOf(' ');
                if (split <= 0)
                    continue;
                string key = line.Substring(0, split);
                string value = line.Substring(split).Trim();
                parts.Add($"{key}={value}");
            }
            return string.Join(", ", parts);
        }

        private ListBoxItem CreateMessageItem(string message)
        {
            var txt = new TextBlock 
            { 
                Text = message, 
                Foreground = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)), 
                FontSize = 12, 
                HorizontalAlignment = HorizontalAlignment.Center, 
                TextAlignment = TextAlignment.Center, 
                Margin = new Thickness(10) 
            };
            return new ListBoxItem { Content = txt, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        }

        private void UpdateSparkline(Polyline polyline, Canvas canvas, List<double> history, double newValue)
        {
            history.Add(newValue);
            if (history.Count > 30)
            {
                history.RemoveAt(0);
            }

            DrawSparkline(polyline, canvas, history);
        }

        private void DrawSparkline(Polyline polyline, Canvas canvas, List<double> history)
        {
            polyline.Points.Clear();

            double width = canvas.ActualWidth;
            double height = canvas.ActualHeight;

            if (width <= 0 || height <= 0 || history.Count == 0)
                return;

            double xStep = width / 29.0; // Draw across 30 total steps (0 to 29)
            
            for (int i = 0; i < history.Count; i++)
            {
                double x = i * xStep;
                // Invert Y because (0,0) is top-left in WPF canvas
                double y = height - (history[i] / 100.0 * (height - 4)) - 2;
                polyline.Points.Add(new Point(x, y));
            }
        }

        private void RedrawAllSparklines()
        {
            DrawSparkline(CpuPolyline, CpuCanvas, _cpuHistory);
            DrawSparkline(RamPolyline, RamCanvas, _ramHistory);
            DrawSparkline(GpuPolyline, GpuCanvas, _gpuHistory);
            DrawSparkline(VramPolyline, VramCanvas, _vramHistory);
        }

        private void UpdatePinButtonState()
        {
            try
            {
                var pinPath = (Path?)PinButton.Template.FindName("PinPath", PinButton);
                if (pinPath != null)
                {
                    if (Topmost)
                    {
                        pinPath.Fill = new SolidColorBrush(Color.FromRgb(10, 132, 255)); // Bright System Blue
                    }
                    else
                    {
                        pinPath.Fill = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)); // Semi-transparent white
                    }
                }
            }
            catch
            {
                // Can fail if called before template is applied
            }
        }

        // Window interaction handlers
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            // There is no taskbar button to minimize to; the mascot stays on the desktop and this
            // just tucks the dashboard back behind it
            HideDashboard();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            _settings.AlwaysOnTop = !Topmost;
            _settings.Save();

            ApplyAlwaysOnTop(_settings.AlwaysOnTop);
        }

        // Settings Panel handlers
        private void GearButton_Click(object sender, RoutedEventArgs e)
        {
            // Populate inputs from live settings
            OllamaUrlInput.Text = _settings.OllamaUrl;
            OllamaStartCommandInput.Text = _settings.OllamaStartCommand;
            RefreshRateSlider.Value = _settings.RefreshIntervalSeconds;
            RefreshRateLabel.Text = $"{_settings.RefreshIntervalSeconds:F0}s";
            AlwaysOnTopCheckbox.IsChecked = _settings.AlwaysOnTop;
            
            // Simple animated panel visibility switch
            SettingsPanel.Visibility = Visibility.Visible;
        }

        private void SettingsCloseButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
        }

        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            _settings.OllamaUrl = OllamaUrlInput.Text;
            _settings.OllamaStartCommand = string.IsNullOrWhiteSpace(OllamaStartCommandInput.Text)
                ? "ollama serve"
                : OllamaStartCommandInput.Text.Trim();
            _settings.RefreshIntervalSeconds = RefreshRateSlider.Value;
            _settings.AlwaysOnTop = AlwaysOnTopCheckbox.IsChecked == true;
            _settings.Save();

            ApplyAlwaysOnTop(_settings.AlwaysOnTop);
            _metrics.ApplyInterval();

            SettingsPanel.Visibility = Visibility.Collapsed;
            _metrics.RefreshNow();
        }

        // Context Menu handlers
        private void ContextAlwaysOnTop_Click(object sender, RoutedEventArgs e)
        {
            _settings.AlwaysOnTop = ContextAlwaysOnTop.IsChecked;
            _settings.Save();

            ApplyAlwaysOnTop(_settings.AlwaysOnTop);
        }

        private void ContextRefresh_Click(object sender, RoutedEventArgs e)
        {
            _metrics.RefreshNow();
        }

        private void ContextExit_Click(object sender, RoutedEventArgs e)
        {
            // Unlike the close button, this really does end the app, mascot included
            SaveBounds();
            App.RequestExit();
        }

        // Ollama restart handler
        private void OllamaRestartButton_Click(object sender, RoutedEventArgs e)
        {
            StartOllama();
        }

        private void StartOllama()
        {
            if (OllamaLauncher.Start(_settings))
            {
                StatusFooterText.Text = "Starting Ollama...";
                OllamaRestartButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                StatusFooterText.Text = "Failed to start Ollama";
            }
        }
    }
}