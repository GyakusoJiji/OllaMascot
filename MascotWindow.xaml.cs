using System;
using System.Windows;
using System.Windows.Input;

namespace OllaMascot
{
    /// <summary>
    /// The app's presence on the desktop: a frameless 64x64 sprite whose pose is the GPU load
    /// readout — 0% shows the first frame of the sheet, 100% the last — plus the menu commands.
    /// It is always on screen: neither window appears in the taskbar, so hiding this one would
    /// leave the app unreachable.
    /// </summary>
    public partial class MascotWindow : Window
    {
        /// <summary>Edge length of the window, and of the sprite drawn into it.</summary>
        private const double MascotSize = 64;

        /// <summary>Gap left between the mascot and the corner of the work area on first run.</summary>
        private const double DefaultMargin = 24;

        private readonly Settings _settings;
        private readonly MetricsService _metrics;
        private readonly MascotIcons? _mascot;

        // The pose only moves when the frame it maps to changes, so a steady load does not reassign
        // the image on every poll
        private int _lastFrame = -1;

        // Press state, held between the button going down and coming back up so a click can be told
        // apart from a drag
        private Point _grabOffset;
        private Point _pressScreenPoint;
        private bool _pressed;
        private bool _dragged;

        /// <summary>Raised when the mascot is clicked or "Open Dashboard" is chosen.</summary>
        public event EventHandler? ShowDashboardRequested;

        /// <summary>Raised when the user toggles always-on-top here, so the dashboard can follow.</summary>
        public event EventHandler<bool>? AlwaysOnTopChanged;

        public MascotWindow(MetricsService metrics, Settings settings, MascotIcons? mascot)
        {
            App.Log("MascotWindow constructor started.");
            _metrics = metrics;
            _settings = settings;
            _mascot = mascot;
            try
            {
                InitializeComponent();

                Topmost = _settings.AlwaysOnTop;
                ContextAlwaysOnTop.IsChecked = _settings.AlwaysOnTop;
                RestorePosition();

                if (_mascot != null && _mascot.FrameCount > 0)
                {
                    // Start on the idle pose so the window never flashes an empty frame on the way
                    // to the first poll
                    _lastFrame = 0;
                    MascotImage.Source = _mascot[0];
                    Icon = _mascot[0];
                }
                else
                {
                    // No sheet: leave the image empty rather than failing to start, matching how the
                    // rest of the app treats a missing mascot
                    App.Log("MascotWindow has no frames; the mascot will not be drawn.");
                }

                // Keep the position current as the user drags; it is written to disk when the drag
                // ends and again on exit
                LocationChanged += (s, e) => CapturePosition();

                _metrics.Updated += OnMetricsUpdated;
            }
            catch (Exception ex)
            {
                App.Log($"Exception in MascotWindow constructor: {ex}");
            }
            App.Log("MascotWindow constructor completed.");
        }

        /// <summary>
        /// Refuses to stay minimized. Win+D and "show desktop" minimize every window, and with no
        /// taskbar button and no tray icon there would be nothing left to restore the mascot from —
        /// it would be gone until the app was restarted.
        /// </summary>
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
            }
        }

        // Pose

        private void OnMetricsUpdated(object? sender, MetricsService.Snapshot snapshot)
        {
            try
            {
                SummaryToolTip.Content = MetricsService.FormatSummary(snapshot);

                if (_mascot == null || _mascot.FrameCount == 0)
                    return;

                // The sheet is the load gauge: IndexForPercent maps 0% onto the first frame and
                // 100% onto the last. No GPU reads as idle rather than leaving the pose stale.
                int frame = _mascot.IndexForPercent(snapshot.GpuAvailable ? snapshot.GpuPercent : 0);
                if (frame == _lastFrame)
                    return;

                _lastFrame = frame;
                MascotImage.Source = _mascot[frame];
            }
            catch (Exception ex)
            {
                App.Log($"Exception in MascotWindow.OnMetricsUpdated: {ex}");
            }
        }

        // Position

        /// <summary>
        /// Puts the mascot back where the last run left it. A saved position that no longer lands on
        /// screen — the monitor it sat on was unplugged, or the resolution shrank — is replaced with
        /// the middle of the work area, where it cannot be missed. With no tray icon and no taskbar
        /// button, a mascot left off screen could not be recovered at all.
        /// </summary>
        private void RestorePosition()
        {
            if (_settings.MascotLeft is double left && _settings.MascotTop is double top)
            {
                if (IsFullyOnScreen(left, top))
                {
                    Left = left;
                    Top = top;
                }
                else
                {
                    App.Log($"Saved mascot position {left},{top} is off screen; centring instead.");
                    MoveToWorkAreaCentre();
                }
                return;
            }

            // First run: the bottom-right corner keeps the mascot out of the way of whatever is
            // already open, which centring would land right on top of
            var work = SystemParameters.WorkArea;
            Left = work.Right - MascotSize - DefaultMargin;
            Top = work.Bottom - MascotSize - DefaultMargin;
        }

        /// <summary>
        /// True only if the whole 64x64 fits inside the virtual screen. Partial overlap is not good
        /// enough: a mascot hanging off an edge is clipped and awkward to grab.
        /// </summary>
        private static bool IsFullyOnScreen(double left, double top)
        {
            double screenLeft = SystemParameters.VirtualScreenLeft;
            double screenTop = SystemParameters.VirtualScreenTop;
            double screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
            double screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

            return left >= screenLeft && top >= screenTop &&
                   left + MascotSize <= screenRight && top + MascotSize <= screenBottom;
        }

        private void MoveToWorkAreaCentre()
        {
            var work = SystemParameters.WorkArea;
            Left = work.Left + (work.Width - MascotSize) / 2;
            Top = work.Top + (work.Height - MascotSize) / 2;
        }

        private void CapturePosition()
        {
            if (double.IsNaN(Left) || double.IsNaN(Top))
                return;

            _settings.MascotLeft = Left;
            _settings.MascotTop = Top;
        }

        /// <summary>Writes the current position to disk. Called when a drag ends and on exit.</summary>
        public void SavePosition()
        {
            CapturePosition();
            _settings.Save();
        }

        // Drag and click

        // The mascot is dragged by moving the window from the mouse events rather than by calling
        // DragMove: DragMove runs a modal move loop that blocks until the button comes up, which
        // swallows the release a click has to be told apart by.

        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pressed = true;
            _dragged = false;
            _grabOffset = e.GetPosition(this);
            _pressScreenPoint = PointToScreen(_grabOffset);
            Root.CaptureMouse();
        }

        private void Root_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_pressed)
                return;

            // The release can land somewhere the window never hears about, e.g. over an app that
            // took focus while the button was down
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndPress(wasClick: false);
                return;
            }

            var screen = PointToScreen(e.GetPosition(this));

            if (!_dragged)
            {
                // Below the shell's drag threshold this is still a click in progress; moving the
                // window on the first stray pixel would make the mascot impossible to click
                if (Math.Abs(screen.X - _pressScreenPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(screen.Y - _pressScreenPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }
                _dragged = true;
            }

            // The grab point stays under the cursor, so the mascot does not jump on the first move
            Left = screen.X - _grabOffset.X;
            Top = screen.Y - _grabOffset.Y;
        }

        private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndPress(wasClick: _pressed && !_dragged);
        }

        private void EndPress(bool wasClick)
        {
            bool dragged = _dragged;
            _pressed = false;
            _dragged = false;
            Root.ReleaseMouseCapture();

            if (wasClick)
            {
                ShowDashboardRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (dragged)
            {
                SavePosition();
            }
        }

        // Menu

        private void ContextOpenDashboard_Click(object sender, RoutedEventArgs e)
        {
            ShowDashboardRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ContextRefresh_Click(object sender, RoutedEventArgs e)
        {
            _metrics.RefreshNow();
        }

        private void ContextAlwaysOnTop_Click(object sender, RoutedEventArgs e)
        {
            _settings.AlwaysOnTop = ContextAlwaysOnTop.IsChecked;
            _settings.Save();

            SyncAlwaysOnTop(_settings.AlwaysOnTop);
            AlwaysOnTopChanged?.Invoke(this, _settings.AlwaysOnTop);
        }

        private void ContextExit_Click(object sender, RoutedEventArgs e)
        {
            SavePosition();
            App.RequestExit();
        }

        /// <summary>Applies an always-on-top change made elsewhere, without echoing it back.</summary>
        public void SyncAlwaysOnTop(bool value)
        {
            Topmost = value;
            ContextAlwaysOnTop.IsChecked = value;
        }
    }
}
