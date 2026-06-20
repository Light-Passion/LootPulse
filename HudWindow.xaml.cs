using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LootPulse.Models;

namespace LootPulse
{
    public partial class HudWindow : Window
    {
        private bool _isClickThroughActive;
        private bool _isInitialized;
        private readonly Action<AppSettings> _onPositionChanged;
        private readonly AppSettings _settings;

        // Win32 Interop
        private const int _gwlExStyle = -20;
        private const int _wsExTransparent = 0x20;

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public HudWindow(AppSettings settings, Action<AppSettings> onPositionChanged)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(onPositionChanged);

            InitializeComponent();
            _settings = settings;
            _onPositionChanged = onPositionChanged;

            // Set Initial Size
            Width = settings.HudWidth > 50 ? settings.HudWidth : 250;
            Height = settings.HudHeight > 30 ? settings.HudHeight : 120;

            // Hook sizing/moving events
            LocationChanged += HudWindow_LocationChanged;
            SizeChanged += HudWindow_SizeChanged;

            // Restore position when window handle is ready
            SourceInitialized += (s, e) => RestorePosition();

            Loaded += (s, e) => {
                RestorePosition();
                // Defer setting _isInitialized to true until all layout and DPI scaling shifts have completed
                Dispatcher.BeginInvoke(new Action(() => {
                    _isInitialized = true;
                    // Force initial styling (HUD uses its own opacity setting)
                    SetClickThrough(false, _settings.HudModeOpacity);
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
        }

        public void RestorePosition()
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;

            // Position relative to screen resolution
            Left = _settings.HudXPercent * screenWidth;
            Top = _settings.HudYPercent * screenHeight;
        }

        private void SavePosition()
        {
            if (!_isInitialized || _isClickThroughActive) return;

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;

            if (screenWidth > 0 && screenHeight > 0)
            {
                _settings.HudXPercent = Left / screenWidth;
                _settings.HudYPercent = Top / screenHeight;
                _settings.HudWidth = Width;
                _settings.HudHeight = Height;

                // Notify main window to persist settings
                _onPositionChanged?.Invoke(_settings);
            }
        }

        private void HudWindow_LocationChanged(object? sender, EventArgs e)
        {
            SavePosition();
        }

        private void HudWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            SavePosition();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isClickThroughActive && e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        public void SetClickThrough(bool enabled, double opacity)
        {
            _isClickThroughActive = enabled;

            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.Handle;

            if (hwnd == IntPtr.Zero) return;

            int extendedStyle = GetWindowLong(hwnd, _gwlExStyle);
            if (enabled)
            {
                // Make mouse penetrable (click-through)
                _ = SetWindowLong(hwnd, _gwlExStyle, extendedStyle | _wsExTransparent);

                // HUD opacity & look
                byte alpha = (byte)(opacity * 255);
                HudOuterBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 12, 12, 12));
                HudOuterBorder.BorderBrush = Brushes.Transparent;
                DragNoticeText.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Make mouse interactive (solid)
                _ = SetWindowLong(hwnd, _gwlExStyle, extendedStyle & ~_wsExTransparent);

                // Edit Mode opacity & orange outline
                byte alpha = (byte)(opacity * 255);
                HudOuterBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 18, 18, 18));
                HudOuterBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 97, 36)); // Orange border
                DragNoticeText.Visibility = Visibility.Visible;
            }
        }

        public void UpdateDisplay(string charName, int level, string zoneName, int zoneLevel, string statusText)
        {
            HudCharNameText.Text = $" ({charName})";
            HudLevelText.Text = level.ToString(System.Globalization.CultureInfo.InvariantCulture);
            HudZoneText.Text = zoneName;
            HudStatusText.Text = $"{statusText} (Zone Lvl: {zoneLevel})";
        }
    }
}
