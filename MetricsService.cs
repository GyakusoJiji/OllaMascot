using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace OllaMascot
{
    /// <summary>
    /// The single always-on poller for hardware and Ollama state. Owned by <see cref="App"/> rather
    /// than by a window so polling is independent of window state — the desktop mascot keeps
    /// tracking GPU load while the dashboard is hidden.
    /// </summary>
    public class MetricsService
    {
        /// <summary>Immutable result of one poll, handed to every subscriber.</summary>
        public class Snapshot
        {
            public double CpuPercent { get; init; }
            public double RamUsedGb { get; init; }
            public double RamTotalGb { get; init; }
            public double RamPercent { get; init; }

            public bool GpuAvailable { get; init; }
            public double GpuPercent { get; init; }
            public double VramUsedGb { get; init; }
            public double VramTotalGb { get; init; }
            public double VramPercent { get; init; }

            public bool OllamaReachable { get; init; }
            public string? OllamaVersion { get; init; }
            public List<OllamaClient.ActiveModel> ActiveModels { get; init; } = new List<OllamaClient.ActiveModel>();
        }

        /// <summary>
        /// One-line readout of a poll, used for the mascot's tooltip and the dashboard's title.
        /// </summary>
        public static string FormatSummary(Snapshot s)
        {
            string gpu = s.GpuAvailable ? $"GPU {s.GpuPercent:F0}%" : "GPU N/A";
            string vram = s.GpuAvailable ? $" | VRAM {s.VramUsedGb:F1}GB" : "";
            return $"{gpu} | CPU {s.CpuPercent:F0}% | RAM {s.RamPercent:F0}%{vram}";
        }

        // One missed /api/ps is not evidence that Ollama is gone; require a run of them so a single
        // slow response under GPU load cannot trigger the offline warning
        private const int OfflineFailureThreshold = 2;

        private readonly Settings _settings;
        private readonly DispatcherTimer _timer;
        private readonly Dictionary<string, OllamaClient.ShowResponse?> _modelInfoCache = new Dictionary<string, OllamaClient.ShowResponse?>();
        private string? _ollamaVersion;
        private bool _isPolling;
        private bool _hasPolled;
        private int _consecutiveOllamaFailures;

        /// <summary>Raised on the UI thread after every completed poll.</summary>
        public event EventHandler<Snapshot>? Updated;

        /// <summary>
        /// Raised once each time Ollama transitions from reachable to unreachable, so a listener can
        /// offer to restart it without re-prompting every tick.
        /// </summary>
        public event EventHandler? OllamaWentOffline;

        public Snapshot Current { get; private set; } = new Snapshot();

        public MetricsService(Settings settings)
        {
            _settings = settings;
            _timer = new DispatcherTimer();
            _timer.Tick += (s, e) => _ = PollAsync();
            ApplyInterval();
        }

        public void Start()
        {
            _timer.Start();
            _ = PollAsync();
        }

        public void Stop() => _timer.Stop();

        /// <summary>Re-reads the refresh interval from settings; call after the user edits it.</summary>
        public void ApplyInterval()
        {
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(0.5, _settings.RefreshIntervalSeconds));
        }

        public void RefreshNow() => _ = PollAsync();

        /// <summary>Cached /api/show detail for a model, or null if it has not been fetched yet.</summary>
        public OllamaClient.ShowResponse? GetModelInfo(string modelName)
        {
            _modelInfoCache.TryGetValue(modelName, out var info);
            return info;
        }

        private async Task PollAsync()
        {
            // The Ollama calls are awaited, so a slow endpoint could otherwise stack up ticks
            if (_isPolling)
                return;
            _isPolling = true;

            try
            {
                double cpu = HardwareMonitor.GetCpuUtilization();

                double ramUsedGb = 0, ramTotalGb = 0, ramPercent = 0;
                if (HardwareMonitor.GetRamMetrics(out ulong totalRam, out ulong usedRam))
                {
                    ramTotalGb = totalRam / 1073741824.0;
                    ramUsedGb = usedRam / 1073741824.0;
                    ramPercent = ramTotalGb > 0 ? ramUsedGb / ramTotalGb * 100.0 : 0.0;
                }

                bool gpuOk = false;
                double gpuPercent = 0, vramUsedGb = 0, vramTotalGb = 0, vramPercent = 0;
                if (Nvml.IsAvailable && Nvml.GetGpuMetrics(out uint gpuUtil, out ulong totalVram, out ulong usedVram))
                {
                    gpuOk = true;
                    gpuPercent = gpuUtil;
                    vramTotalGb = totalVram / 1073741824.0;
                    vramUsedGb = usedVram / 1073741824.0;
                    vramPercent = vramTotalGb > 0 ? vramUsedGb / vramTotalGb * 100.0 : 0.0;
                }

                // Reachability is judged by the /api/ps call itself; a separate ping timed out
                // routinely and reported failure even while Ollama was healthy
                var (responded, models) = await OllamaClient.TryGetActiveModelsAsync(_settings.OllamaUrl);

                if (responded)
                {
                    _consecutiveOllamaFailures = 0;
                }
                else
                {
                    _consecutiveOllamaFailures++;
                }

                // Hold the previous verdict until the failures form a run, and keep showing the last
                // known model list meanwhile so a blip does not flash the UI through "idle"
                bool reachable = responded || _consecutiveOllamaFailures < OfflineFailureThreshold;
                if (!responded && reachable)
                {
                    models = Current.ActiveModels;
                }

                // Fetch the server version once per online period; drop it when offline
                if (!reachable)
                {
                    _ollamaVersion = null;
                }
                else if (_ollamaVersion == null && responded)
                {
                    _ollamaVersion = await OllamaClient.GetVersionAsync(_settings.OllamaUrl);
                }

                // Fetch /api/show info once per model; retry on a later tick if it failed
                foreach (var model in models)
                {
                    if (!_modelInfoCache.TryGetValue(model.Name, out var cached) || cached == null)
                    {
                        _modelInfoCache[model.Name] = await OllamaClient.GetModelInfoAsync(_settings.OllamaUrl, model.Name);
                    }
                }

                // Treat the very first poll as a transition so an already-dead Ollama is reported
                bool wasReachable = !_hasPolled || Current.OllamaReachable;
                _hasPolled = true;

                Current = new Snapshot
                {
                    CpuPercent = cpu,
                    RamUsedGb = ramUsedGb,
                    RamTotalGb = ramTotalGb,
                    RamPercent = ramPercent,
                    GpuAvailable = gpuOk,
                    GpuPercent = gpuPercent,
                    VramUsedGb = vramUsedGb,
                    VramTotalGb = vramTotalGb,
                    VramPercent = vramPercent,
                    OllamaReachable = reachable,
                    OllamaVersion = _ollamaVersion,
                    ActiveModels = models,
                };

                Updated?.Invoke(this, Current);

                if (wasReachable && !reachable)
                {
                    OllamaWentOffline?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                App.Log($"Exception in MetricsService.PollAsync: {ex}");
            }
            finally
            {
                _isPolling = false;
            }
        }
    }
}
