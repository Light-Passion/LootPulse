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
        private bool _isClickThroughActive = false;
        private bool _isInitialized = false;
        private readonly Action<AppSettings> _onPositionChanged;
        private readonly AppSettings _settings;

        // Win32 Interop
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public HudWindow(AppSettings settings, Action<AppSettings> onPositionChanged)
        {
            InitializeComponent();
            _settings = settings;
            _onPositionChanged = onPositionChanged;

            // Set Initial Size & Position
            Width = settings.HudWidth > 50 ? settings.HudWidth : 250;
            Height = settings.HudHeight > 30 ? settings.HudHeight : 120;

            RestorePosition();

            // Hook sizing/moving events
            LocationChanged += HudWindow_LocationChanged;
            SizeChanged += HudWindow_SizeChanged;

            Loaded += (s, e) => {
                _isInitialized = true;
                // Force initial styling
                SetClickThrough(false, _settings.EditModeOpacity);
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

            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if (enabled)
            {
                // Make mouse penetrable (click-through)
                SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);

                // HUD opacity & look
                byte alpha = (byte)(opacity * 255);
                HudOuterBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 12, 12, 12));
                HudOuterBorder.BorderBrush = Brushes.Transparent;
                DragNoticeText.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Make mouse interactive (solid)
                SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);

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
            HudLevelText.Text = level.ToString();
            HudZoneText.Text = zoneName;
            HudStatusText.Text = $"{statusText} (Zone Lvl: {zoneLevel})";
        }
    }
}
