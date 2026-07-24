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

namespace OllaMonitor
{
    public partial class MainWindow : Window
    {
        private readonly Settings _settings;
        private readonly DispatcherTimer _timer;
        
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
        private List<OllamaClient.ActiveModel> _activeModels = new List<OllamaClient.ActiveModel>();
        private readonly Dictionary<string, OllamaClient.ShowResponse?> _modelInfoCache = new Dictionary<string, OllamaClient.ShowResponse?>();
        private string? _ollamaVersion = null;
        private bool _isOllamaReachable = false;
        private bool _restartPromptShown = false;

        public MainWindow()
        {
            App.Log("MainWindow constructor started.");
            _settings = Settings.Load();
            _timer = new DispatcherTimer();
            try
            {
                InitializeComponent();

                // Apply window behavior based on settings
                Topmost = _settings.AlwaysOnTop;
                RestoreWindowBounds();
                
                // Initialize refresh timer
                _timer.Tick += Timer_Tick;
                UpdateTimerInterval();
                
                // Attach size changed events to redraw sparklines when layout settles
                CpuCanvas.SizeChanged += (s, e) => RedrawAllSparklines();
                RamCanvas.SizeChanged += (s, e) => RedrawAllSparklines();
                GpuCanvas.SizeChanged += (s, e) => RedrawAllSparklines();
                VramCanvas.SizeChanged += (s, e) => RedrawAllSparklines();
            }
            catch (Exception ex)
            {
                App.Log($"Exception in MainWindow constructor: {ex}");
            }
            App.Log("MainWindow constructor completed.");
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            App.Log("MainWindow OnSourceInitialized started.");
            try
            {
                base.OnSourceInitialized(e);
                
                // Apply Mica or Acrylic background effect for Windows 11
                App.Log("Applying system backdrop...");
                SystemBackdropHelper.ApplyBackdrop(this, useAcrylic: true);
                
                // Initialize UI elements state
                OllamaUrlInput.Text = _settings.OllamaUrl;
                RefreshRateSlider.Value = _settings.RefreshIntervalSeconds;
                RefreshRateLabel.Text = $"{_settings.RefreshIntervalSeconds:F0}s";
                AlwaysOnTopCheckbox.IsChecked = _settings.AlwaysOnTop;
                ContextAlwaysOnTop.IsChecked = _settings.AlwaysOnTop;
                
                UpdatePinButtonState();

                // Set GPU name label if NVML is active
                if (Nvml.IsAvailable)
                {
                    GpuModelLabel.Text = Nvml.GpuName;
                }
                else
                {
                    GpuModelLabel.Text = "No Nvidia GPU";
                }

                // Start polling system metrics
                App.Log("Starting timer...");
                _timer.Start();
                
                // Run first update immediately
                App.Log("Running initial metrics update...");
                UpdateMetrics();
            }
            catch (Exception ex)
            {
                App.Log($"Exception in OnSourceInitialized: {ex}");
            }
            App.Log("MainWindow OnSourceInitialized completed.");
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
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.Save();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            UpdateMetrics();
        }

        private async void UpdateMetrics()
        {
            try
            {
                // 1. CPU Metrics
                float cpuUsage = HardwareMonitor.GetCpuUtilization();
                CpuText.Text = $"{cpuUsage:F1}%";
                CpuProgress.Value = cpuUsage;
                UpdateSparkline(CpuPolyline, CpuCanvas, _cpuHistory, cpuUsage);

                // 2. RAM Metrics
                if (HardwareMonitor.GetRamMetrics(out ulong totalRam, out ulong usedRam))
                {
                    double totalRamGb = totalRam / 1073741824.0;
                    double usedRamGb = usedRam / 1073741824.0;
                    double ramPercent = totalRamGb > 0 ? (usedRamGb / totalRamGb) * 100.0 : 0.0;

                    RamText.Text = $"{ramPercent:F1}%";
                    RamProgress.Value = ramPercent;
                    UpdateSparkline(RamPolyline, RamCanvas, _ramHistory, ramPercent);
                }

                // 3. GPU & VRAM Metrics (NVIDIA NVML)
                if (Nvml.IsAvailable && Nvml.GetGpuMetrics(out uint gpuUtilization, out ulong totalVram, out ulong usedVram))
                {
                    double totalVramGb = totalVram / 1073741824.0;
                    double usedVramGb = usedVram / 1073741824.0;
                    double vramPercent = totalVramGb > 0 ? (usedVramGb / totalVramGb) * 100.0 : 0.0;

                    GpuText.Text = $"{gpuUtilization}%";
                    GpuProgress.Value = gpuUtilization;
                    UpdateSparkline(GpuPolyline, GpuCanvas, _gpuHistory, gpuUtilization);

                    VramText.Text = $"{usedVramGb:F1} GB";
                    VramProgress.Value = vramPercent;
                    UpdateSparkline(VramPolyline, VramCanvas, _vramHistory, vramPercent);
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

                // 4. Ollama Active Models Query
                await QueryOllamaStatus();
                
                // 5. Update Unified Details List
                RefreshDetailsDisplay();
            }
            catch (Exception ex)
            {
                App.Log($"Exception in UpdateMetrics: {ex}");
            }
        }

        private async Task QueryOllamaStatus()
        {
            try
            {
                // Reachability is judged by the /api/ps call itself; the previous
                // separate 500ms ping to /api/tags timed out routinely and reported
                // "connection failed" even while Ollama was healthy
                var (reachable, models) = await OllamaClient.TryGetActiveModelsAsync(_settings.OllamaUrl);
                _activeModels = models;
                _isOllamaReachable = reachable;

                // Fetch the server version once per online period; drop it when offline
                if (!reachable)
                {
                    _ollamaVersion = null;
                }
                else if (_ollamaVersion == null)
                {
                    _ollamaVersion = await OllamaClient.GetVersionAsync(_settings.OllamaUrl);
                }

                // Fetch /api/show info once per model; retry next tick if it failed
                foreach (var model in _activeModels)
                {
                    if (!_modelInfoCache.TryGetValue(model.Name, out var cached) || cached == null)
                    {
                        _modelInfoCache[model.Name] = await OllamaClient.GetModelInfoAsync(_settings.OllamaUrl, model.Name);
                    }
                }

                if (!_isOllamaReachable)
                {
                    OllamaStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 69, 58)); // System Red
                    StatusFooterText.Text = "Ollama connection failed";
                    OllamaRestartButton.Visibility = Visibility.Visible;
                    PromptOllamaRestart();
                }
                else if (_activeModels.Count == 0)
                {
                    OllamaStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(142, 142, 147)); // System Gray
                    StatusFooterText.Text = "Ollama is idle";
                    OllamaRestartButton.Visibility = Visibility.Collapsed;
                    _restartPromptShown = false;
                }
                else
                {
                    OllamaStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(48, 209, 88)); // System Green
                    StatusFooterText.Text = $"{_activeModels.Count} model(s) active";
                    OllamaRestartButton.Visibility = Visibility.Collapsed;
                    _restartPromptShown = false;
                }
            }
            catch
            {
                _isOllamaReachable = false;
                OllamaStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 69, 58)); // System Red
                StatusFooterText.Text = "Ollama connection error";
                OllamaRestartButton.Visibility = Visibility.Visible;
                PromptOllamaRestart();
            }
        }

        private void PromptOllamaRestart()
        {
            // Show the dialog only once per offline period; reset when Ollama comes back online
            if (_restartPromptShown)
                return;
            _restartPromptShown = true;

            var result = MessageBox.Show(
                this,
                "Ollama is not running.\nDo you want to restart Ollama?",
                "OllaMonitor - Ollama Not Detected",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

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
                    if (HardwareMonitor.GetRamMetrics(out ulong totalRam, out ulong usedRam))
                    {
                        double totalRamGb = totalRam / 1073741824.0;
                        double usedRamGb = usedRam / 1073741824.0;
                        double ramPercent = totalRamGb > 0 ? (usedRamGb / totalRamGb) * 100.0 : 0.0;
                        DetailsListBox.Items.Add(CreateSystemDetailItem("System Memory:", $"{usedRamGb:F1} / {totalRamGb:F1} GB ({ramPercent:F0}%)"));
                    }
                    
                    // 2. VRAM info
                    if (Nvml.IsAvailable && Nvml.GetGpuMetrics(out _, out ulong totalVram, out ulong usedVram))
                    {
                        double totalVramGb = totalVram / 1073741824.0;
                        double usedVramGb = usedVram / 1073741824.0;
                        double vramPercent = totalVramGb > 0 ? (usedVramGb / totalVramGb) * 100.0 : 0.0;
                        DetailsListBox.Items.Add(CreateSystemDetailItem("Graphics VRAM:", $"{usedVramGb:F1} / {totalVramGb:F1} GB ({vramPercent:F0}%)"));
                    }
                    else
                    {
                        DetailsListBox.Items.Add(CreateSystemDetailItem("Graphics VRAM:", "N/A"));
                    }

                    // 3. GPU Model
                    DetailsListBox.Items.Add(CreateSystemDetailItem("GPU Model:", Nvml.IsAvailable ? Nvml.GpuName : "None / Non-Nvidia"));
                    
                    // 4. Ollama URL
                    DetailsListBox.Items.Add(CreateSystemDetailItem("Ollama Endpoint:", _settings.OllamaUrl));

                    // 5. Ollama version
                    DetailsListBox.Items.Add(CreateSystemDetailItem("Ollama Version:", _ollamaVersion ?? "N/A"));

                    // 6. Currently loaded LLM model(s)
                    string modelNames = _activeModels.Count > 0
                        ? string.Join(", ", _activeModels.ConvertAll(m => m.Name))
                        : "None";
                    DetailsListBox.Items.Add(CreateSystemDetailItem("Active Model:", modelNames));
                }
                else // ActiveModels
                {
                    if (!_isOllamaReachable)
                    {
                        DetailsListBox.Items.Add(CreateMessageItem("Ollama is offline or unreachable."));
                    }
                    else if (_activeModels.Count == 0)
                    {
                        DetailsListBox.Items.Add(CreateMessageItem("Ollama is idle. No models loaded."));
                    }
                    else
                    {
                        foreach (var model in _activeModels)
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
            _modelInfoCache.TryGetValue(model.Name, out var showInfo);

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

        private void UpdateTimerInterval()
        {
            _timer.Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds);
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
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            _settings.AlwaysOnTop = Topmost;
            _settings.Save();
            
            ContextAlwaysOnTop.IsChecked = Topmost;
            UpdatePinButtonState();
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

            Topmost = _settings.AlwaysOnTop;
            ContextAlwaysOnTop.IsChecked = Topmost;
            UpdatePinButtonState();
            UpdateTimerInterval();

            SettingsPanel.Visibility = Visibility.Collapsed;
            UpdateMetrics();
        }

        // Context Menu handlers
        private void ContextAlwaysOnTop_Click(object sender, RoutedEventArgs e)
        {
            Topmost = ContextAlwaysOnTop.IsChecked;
            _settings.AlwaysOnTop = Topmost;
            _settings.Save();
            
            UpdatePinButtonState();
        }

        private void ContextRefresh_Click(object sender, RoutedEventArgs e)
        {
            UpdateMetrics();
        }

        private void ContextExit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // Ollama restart handler
        private void OllamaRestartButton_Click(object sender, RoutedEventArgs e)
        {
            StartOllama();
        }

        private void StartOllama()
        {
            try
            {
                // Discover ollama CLI executable path; fall back to "ollama" on PATH
                string ollamaPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Ollama", "ollama.exe"
                );

                if (!System.IO.File.Exists(ollamaPath))
                {
                    ollamaPath = "ollama";
                }

                // The stored command can be pasted verbatim (e.g. "ollama run gemma3:12b");
                // strip the leading "ollama" token since the executable path is resolved above
                string command = (_settings.OllamaStartCommand ?? "").Trim();
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

                StatusFooterText.Text = "Starting Ollama...";
                OllamaRestartButton.Visibility = Visibility.Collapsed;
                App.Log("Ollama restart requested.");
            }
            catch (Exception ex)
            {
                StatusFooterText.Text = "Failed to start Ollama";
                App.Log($"Exception starting Ollama: {ex}");
            }
        }
    }
}