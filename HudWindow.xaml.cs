using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LootPulse.Models;

namespace LootPulse
{
    /// <summary>
    /// A single currency entry shown in the HUD ticker panel.
    /// </summary>
    public class HudCurrencyItem
    {
        public string Label { get; set; } = string.Empty;  // e.g. "Div:"
        public string Value { get; set; } = string.Empty;  // e.g. "488ex"
    }

    public partial class HudWindow : Window
    {
        private bool _isClickThroughActive;
        private bool _isInitialized;
        private readonly Action<AppSettings> _onPositionChanged;
        private readonly AppSettings _settings;
        private bool _ekgActive = true;

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

            // Set Initial Size — larger for the expanded HUD
            Width = settings.HudWidth > 50 ? settings.HudWidth : 280;
            Height = settings.HudHeight > 30 ? settings.HudHeight : 160;

            // Hook sizing/moving events
            LocationChanged += HudWindow_LocationChanged;
            SizeChanged += HudWindow_SizeChanged;

            // Restore position when window handle is ready
            SourceInitialized += (s, e) => RestorePosition();

            Loaded += (s, e) => {
                RestorePosition();
                StartHudEkgAnimation();
                // Defer setting _isInitialized to true until all layout and DPI scaling shifts have completed
                Dispatcher.BeginInvoke(new Action(() => {
                    _isInitialized = true;
                    // Force initial styling (HUD uses its own opacity setting)
                    SetClickThrough(false, _settings.HudModeOpacity);
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
        }

        /// <summary>
        /// Starts the animated EKG heartbeat line in the HUD.
        /// Uses DoubleAnimation on the rendering clock so it never slows
        /// when the main dashboard is hidden or the Dispatcher queue backs up.
        /// The path is a duplicated 520px waveform; translating by -260 and
        /// looping makes the second copy land exactly where the first started.
        /// </summary>
        private void StartHudEkgAnimation()
        {
            const double loopDistance = 260.0;       // one waveform period
            const double pixelsPerSecond = 60.0;      // scroll speed
            var duration = TimeSpan.FromSeconds(loopDistance / pixelsPerSecond);

            var anim = new DoubleAnimation
            {
                From = 0,
                To = -loopDistance,
                Duration = duration,
                RepeatBehavior = RepeatBehavior.Forever
            };

            // BeginAnimation drives the property directly on the rendering clock
            // — no Storyboard needed, and it runs even when the Dispatcher is idle.
            HudEkgTransform.BeginAnimation(TranslateTransform.XProperty, anim);
            HudEkgGlowTransform.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        /// <summary>
        /// Sets the EKG state — flatline on disconnect, active pulse when connected.
        /// </summary>
        public void SetEkgState(bool active)
        {
            _ekgActive = active;
            if (!active)
            {
                // Stop scrolling — pass null to remove the active animation
                HudEkgTransform.BeginAnimation(TranslateTransform.XProperty, null);
                HudEkgGlowTransform.BeginAnimation(TranslateTransform.XProperty, null);

                // Flatline — straight line
                HudEkgPath.Data = Geometry.Parse("M 0,6 L 520,6");
                HudEkgGlow.Data = Geometry.Parse("M 0,6 L 520,6");
                HudEkgPath.Stroke = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x7A)); // Dim gray
                HudEkgGlow.Stroke = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x7A));
            }
            else
            {
                // Restore heartbeat wave — duplicated 520px for seamless loop
                HudEkgPath.Data = Geometry.Parse("M 0,6 L 40,6 L 48,2 L 52,10 L 56,6 L 100,6 L 108,3 L 112,9 L 116,6 L 160,6 L 168,2 L 172,10 L 176,6 L 220,6 L 228,3 L 232,9 L 236,6 L 260,6 L 300,6 L 308,2 L 312,10 L 316,6 L 360,6 L 368,3 L 372,9 L 376,6 L 420,6 L 428,2 L 432,10 L 436,6 L 480,6 L 488,3 L 492,9 L 496,6 L 520,6");
                HudEkgGlow.Data = Geometry.Parse("M 0,6 L 40,6 L 48,2 L 52,10 L 56,6 L 100,6 L 108,3 L 112,9 L 116,6 L 160,6 L 168,2 L 172,10 L 176,6 L 220,6 L 228,3 L 232,9 L 236,6 L 260,6 L 300,6 L 308,2 L 312,10 L 316,6 L 360,6 L 368,3 L 372,9 L 376,6 L 420,6 L 428,2 L 432,10 L 436,6 L 480,6 L 488,3 L 492,9 L 496,6 L 520,6");
                HudEkgPath.Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0xB5, 0x60)); // PoeGold
                HudEkgGlow.Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x61, 0x24)); // PoeOrange

                // Resume scrolling
                StartHudEkgAnimation();
            }
        }

        /// <summary>
        /// Updates the currency ticker with a dynamic list of items.
        /// Each item has a short label and a value string (e.g. "Div: 488ex").
        /// Up to 6 items display in a 2×3 WrapPanel grid.
        /// </summary>
        public void UpdateCurrencyTicker(List<HudCurrencyItem> items)
        {
            HudCurrencyPanel.ItemsSource = items;
        }

        public void RestorePosition()
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenWidth > 0 ? SystemParameters.PrimaryScreenHeight : 1080;

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
            HudCharNameText.Text = $"  ({charName})";
            HudLevelText.Text = level.ToString(System.Globalization.CultureInfo.InvariantCulture);
            HudZoneText.Text = zoneName;
            HudStatusText.Text = $"{statusText} (Zone Lvl: {zoneLevel})";
        }
    }
}

