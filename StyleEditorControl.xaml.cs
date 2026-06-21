using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using LootPulse.Models;

namespace LootPulse
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Catching generic Exception in UI controllers and event handlers is necessary to prevent app crashes and display error alerts to the user.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "This desktop overlay utility does not support localized resource tables.")]
    public partial class StyleEditorControl : UserControl
    {
        private const string _uniquesCategory = "Uniques";
        private const string _defaultThemeName = "Default LootPulse";
        private const string _tealThemeName = "Teal Eclipse";
        private const string _crimsonThemeName = "Crimson Haze";
        private const string _tealColorCode = "#FF00C8FF";
        private const string _builtInSoundType = "BuiltIn";
        private const string _textColorKey = "TextColor";
        private const string _borderColorKey = "BorderColor";
        private const string _backgroundColorKey = "BackgroundColor";
        private const double _hsvCanvasWidth = 200.0;
        private const double _hsvCanvasHeight = 140.0;
        private const double _hueBarHeight = 140.0;
        private const string _unknownTargetMessage = "Unknown target";
        private const double _floatEpsilon = 1e-10;

        /// <summary>
        /// Invoked when the user clicks "Save &amp; Apply". The argument is the edited theme,
        /// which the host should persist and use to regenerate the loot filter.
        /// </summary>
        public Action<FilterTheme>? ThemeApplied { get; set; }

        public FilterTheme WorkingTheme { get; private set; }
        internal string? TestSelectedPresetName => PresetComboBox?.SelectedItem as string;
        private readonly Dictionary<string, FilterTheme> _presets = [];
        private readonly string _presetsFilePath;
        private string _currentCategory = _uniquesCategory;
        private bool _isSynchronizing;
        private bool _isInitializing = true;
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        public StyleEditorControl(FilterTheme currentTheme)
        {
            InitializeComponent();

            // Deep clone theme to work on
            string json = JsonSerializer.Serialize(currentTheme);
            WorkingTheme = JsonSerializer.Deserialize<FilterTheme>(json) ?? new FilterTheme();

            // Set presets path
            _presetsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "args", "filter_presets.json");

            // Ensure directory exists
            string? dir = Path.GetDirectoryName(_presetsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Populate Grid popups with premium swatches
            PopulateSwatchGrids();

            // Load presets
            LoadPresetsList();

            // Load category
            LoadCategoryState(_currentCategory);

            _isInitializing = false;
        }

        private void SaveApply_Click(object sender, RoutedEventArgs e)
        {
            // Save state of current category
            SaveCategoryState(_currentCategory);

            // Hand the edited theme back to the host to persist & regenerate the filter.
            string json = JsonSerializer.Serialize(WorkingTheme);
            var applied = JsonSerializer.Deserialize<FilterTheme>(json) ?? new FilterTheme();
            ThemeApplied?.Invoke(applied);

            if (FooterStatusText != null)
            {
                FooterStatusText.Text = "Saved & applied. Your loot filter has been regenerated.";
            }
        }

        #region Swatches Grid Setup

        private void PopulateSwatchGrids()
        {
            var swatches = new string[]
            {
                "#FFFF6124", // PoE2 Orange
                "#FFE5B560", // Gold / Currency
                "#FFFFFF77", // Rare Yellow
                "#FF8888FF", // Magic Blue
                "#FF1BFF1B", // Gem Green
                "#FF00C8FF", // Teal Eclipse
                "#FFA832A4", // Purple
                "#FFFFFFFF", // White
                "#FF888888", // Grey
                "#00000000", // Transparent
                "#FF000000", // Black
                "#FF111111", // Dark Charcoal
                "#FF101030", // Dark Blue
                "#FF301010", // Dark Red
                "#FF103010", // Dark Green
                "#FF2B1A0A", // Dark Brown
                "#FFFF0000", // Bright Red
                "#FFFF00FF", // Bright Magenta
                "#FF00FFFF", // Cyan
                "#FFFFD700", // Bright Gold
                "#FFFF69B4", // Hot Pink
                "#FF8B0000", // Deep Red
                "#FFB87333", // Copper / Bronze
                "#FF4B0082"  // Indigo / Dark Purple
            };

            PopulateGrid(TextColorGrid, swatches, TextColorTextBox, TextColorPopup);
            PopulateGrid(BorderColorGrid, swatches, BorderColorTextBox, BorderColorPopup);
            PopulateGrid(BackgroundColorGrid, swatches, BackgroundColorTextBox, BackgroundColorPopup);
        }

        private static void PopulateGrid(UniformGrid grid, string[] swatches, TextBox targetTextBox, Popup targetPopup)
        {
            grid.Children.Clear();
            foreach (var swatch in swatches)
            {
                var btn = new Button
                {
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(2),
                    BorderThickness = new Thickness(1),
                    BorderBrush = System.Windows.Media.Brushes.Gray,
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                if (swatch == "#00000000")
                {
                    btn.Background = System.Windows.Media.Brushes.Transparent;
                    btn.Content = new TextBlock
                    {
                        Text = "✕",
                        Foreground = System.Windows.Media.Brushes.Red,
                        FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontWeight = FontWeights.Bold
                    };
                }
                else
                {
                    try
                    {
                        var color = (Color)ColorConverter.ConvertFromString(swatch);
                        btn.Background = new SolidColorBrush(color);
                    }
                    catch
                    {
                        btn.Background = System.Windows.Media.Brushes.Gray;
                    }
                }

                btn.Click += (s, e) =>
                {
                    targetTextBox.Text = swatch;
                    targetPopup.IsOpen = false;
                };

                grid.Children.Add(btn);
            }
        }

        private void ColorBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender == TextColorBtn) TextColorPopup.IsOpen = true;
            else if (sender == BorderColorBtn) BorderColorPopup.IsOpen = true;
            else if (sender == BackgroundColorBtn) BackgroundColorPopup.IsOpen = true;
        }

        #endregion

        #region Opacity & Text Synchronization

        private void ColorTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSynchronizing) return;
            if (sender is not TextBox textBox) return;

            string hex = textBox.Text.Trim();
            if (string.IsNullOrEmpty(hex)) return;

            if (!hex.StartsWith('#'))
            {
                hex = "#" + hex;
            }

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                SyncColorControls(textBox, color);
                UpdateLivePreview();
            }
            catch
            {
                // Ignore parsing errors while user is actively typing
            }
        }

        private void SyncColorControls(TextBox textBox, Color color)
        {
            string target;
            Button btn;
            Slider opacitySlider;

            if (textBox == TextColorTextBox)
            {
                target = _textColorKey;
                btn = TextColorBtn;
                opacitySlider = TextOpacitySlider;
            }
            else if (textBox == BorderColorTextBox)
            {
                target = _borderColorKey;
                btn = BorderColorBtn;
                opacitySlider = BorderOpacitySlider;
            }
            else if (textBox == BackgroundColorTextBox)
            {
                target = _backgroundColorKey;
                btn = BackgroundColorBtn;
                opacitySlider = BackgroundOpacitySlider;
            }
            else
            {
                return;
            }

            btn.Background = new SolidColorBrush(color);
            _isSynchronizing = true;
            opacitySlider.Value = color.A;
            _isSynchronizing = false;

            UpdateHsvControlsFromColor(target, color);
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSynchronizing) return;
            if (sender is not Slider slider) return;

            TextBox targetTextBox;
            if (slider == TextOpacitySlider) targetTextBox = TextColorTextBox;
            else if (slider == BorderOpacitySlider) targetTextBox = BorderColorTextBox;
            else if (slider == BackgroundOpacitySlider) targetTextBox = BackgroundColorTextBox;
            else return;

            string hex = targetTextBox.Text.Trim();
            if (string.IsNullOrEmpty(hex)) return;

            if (!hex.StartsWith('#')) hex = "#" + hex;

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var newColor = Color.FromArgb((byte)slider.Value, color.R, color.G, color.B);

                _isSynchronizing = true;
                targetTextBox.Text = $"#{newColor.A:X2}{newColor.R:X2}{newColor.G:X2}{newColor.B:X2}";
                _isSynchronizing = false;

                if (slider == TextOpacitySlider) TextColorBtn.Background = new SolidColorBrush(newColor);
                else if (slider == BorderOpacitySlider) BorderColorBtn.Background = new SolidColorBrush(newColor);
                else if (slider == BackgroundOpacitySlider) BackgroundColorBtn.Background = new SolidColorBrush(newColor);

                UpdateLivePreview();
            }
            catch
            {
                // Ignore
            }
        }

        #region HSV Color Area / Canvas Interaction

        private bool _isMouseDownOnCanvas;
        private bool _isMouseDownOnHueBar;

        private void Canvas_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border border)
            {
                border.CaptureMouse();
                _isMouseDownOnCanvas = true;
                UpdateColorFromCanvasClick(border, e.GetPosition(border));
            }
        }

        private void Canvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isMouseDownOnCanvas && sender is Border border)
            {
                UpdateColorFromCanvasClick(border, e.GetPosition(border));
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border border)
            {
                border.ReleaseMouseCapture();
                _isMouseDownOnCanvas = false;
            }
        }

        private void HueBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border border)
            {
                border.CaptureMouse();
                _isMouseDownOnHueBar = true;
                UpdateColorFromHueClick(border, e.GetPosition(border));
            }
        }

        private void HueBar_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isMouseDownOnHueBar && sender is Border border)
            {
                UpdateColorFromHueClick(border, e.GetPosition(border));
            }
        }

        private void HueBar_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border border)
            {
                border.ReleaseMouseCapture();
                _isMouseDownOnHueBar = false;
            }
        }

        private void UpdateColorFromCanvasClick(Border border, Point p)
        {
            string target = border.Tag?.ToString() ?? "";

            // Clamp points to border bounds
            double x = Math.Max(0, Math.Min(border.ActualWidth > 0 ? border.ActualWidth : _hsvCanvasWidth, p.X));
            double y = Math.Max(0, Math.Min(border.ActualHeight > 0 ? border.ActualHeight : _hsvCanvasHeight, p.Y));

            double width = border.ActualWidth > 0 ? border.ActualWidth : _hsvCanvasWidth;
            double height = border.ActualHeight > 0 ? border.ActualHeight : _hsvCanvasHeight;

            double s = x / width;
            double v = 1.0 - (y / height);

            double h = GetHueForTarget(target);
            byte opacity = (byte)GetOpacitySliderForTarget(target).Value;

            Color newColor = ColorFromHsv(h, s, v, opacity);
            UpdateTargetColor(target, newColor);
        }

        private void UpdateColorFromHueClick(Border border, Point p)
        {
            string target = border.Tag?.ToString() ?? "";

            double height = border.ActualHeight > 0 ? border.ActualHeight : _hueBarHeight;
            double y = Math.Max(0, Math.Min(height, p.Y));
            double h = (y / height) * 360.0;

            // Update the S-V Canvas background of the target to the pure Hue
            Color pureHueColor = ColorFromHsv(h, 1.0, 1.0);
            GetCanvasGridForTarget(target).Background = new SolidColorBrush(pureHueColor);

            // Update the cursor position in HueBar
            var hueCursor = GetHueCursorForTarget(target);
            Canvas.SetTop(hueCursor, y);

            // Re-evaluate color with the new Hue but old Saturation and Value
            GetSvForTarget(target, out double s, out double v);
            byte opacity = (byte)GetOpacitySliderForTarget(target).Value;

            Color newColor = ColorFromHsv(h, s, v, opacity);
            UpdateTargetColor(target, newColor);
        }

        private void UpdateTargetColor(string target, Color color)
        {
            var textBox = GetTextBoxForTarget(target);
            _isSynchronizing = true;
            textBox.Text = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            _isSynchronizing = false;

            var btn = target switch
            {
                _textColorKey => TextColorBtn,
                _borderColorKey => BorderColorBtn,
                _backgroundColorKey => BackgroundColorBtn,
                _ => throw new ArgumentException(_unknownTargetMessage)
            };
            btn.Background = new SolidColorBrush(color);

            UpdateCanvasCursorPosition(target, color);
            UpdateLivePreview();
        }

        private void UpdateHsvControlsFromColor(string target, Color color)
        {
            ColorToHsv(color, out double h, out _, out _);

            // Update Canvas background to pure Hue
            Color pureHue = ColorFromHsv(h, 1.0, 1.0);
            var canvasGrid = GetCanvasGridForTarget(target);
            if (canvasGrid != null)
            {
                canvasGrid.Background = new SolidColorBrush(pureHue);
            }

            // Update S-V cursor position
            UpdateCanvasCursorPosition(target, color);

            // Update Hue cursor position
            var hueCursor = GetHueCursorForTarget(target);
            var hueBar = target switch
            {
                _textColorKey => TextColorHueBar,
                _borderColorKey => BorderColorHueBar,
                _backgroundColorKey => BackgroundColorHueBar,
                _ => null
            };
            if (hueCursor != null && hueBar != null)
            {
                double barHeight = hueBar.ActualHeight > 0 ? hueBar.ActualHeight : _hueBarHeight;
                double y = (h / 360.0) * barHeight;
                Canvas.SetTop(hueCursor, y);
            }
        }

        private void UpdateCanvasCursorPosition(string target, Color color)
        {
            ColorToHsv(color, out _, out double s, out double v);
            var cursor = GetCanvasCursorForTarget(target);
            var canvas = GetCanvasForTarget(target);
            if (cursor != null && canvas != null)
            {
                double width = canvas.ActualWidth > 0 ? canvas.ActualWidth : _hsvCanvasWidth;
                double height = canvas.ActualHeight > 0 ? canvas.ActualHeight : _hsvCanvasHeight;
                double x = s * width;
                double y = (1.0 - v) * height;
                Canvas.SetLeft(cursor, x);
                Canvas.SetTop(cursor, y);
            }
        }

        #region HSV / RGB Conversion Helpers

        private static void ColorToHsv(Color color, out double h, out double s, out double v)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            v = max;
            s = max < _floatEpsilon ? 0.0 : delta / max;

            if (delta < _floatEpsilon)
            {
                h = 0.0;
            }
            else
            {
                if (Math.Abs(r - max) < _floatEpsilon)
                {
                    h = (g - b) / delta;
                }
                else if (Math.Abs(g - max) < _floatEpsilon)
                {
                    h = 2.0 + ((b - r) / delta);
                }
                else
                {
                    h = 4.0 + ((r - g) / delta);
                }

                h *= 60.0;
                if (h < 0.0)
                {
                    h += 360.0;
                }
            }
        }

        private static Color ColorFromHsv(double h, double s, double v, byte alpha = 255)
        {
            if (s < _floatEpsilon)
            {
                byte val = (byte)(v * 255.0);
                return Color.FromArgb(alpha, val, val, val);
            }

            double sector = h / 60.0;
            int i = (int)Math.Floor(sector);
            double f = sector - i;

            double p = v * (1.0 - s);
            double q = v * (1.0 - (s * f));
            double t = v * (1.0 - (s * (1.0 - f)));

            double r = 0.0, g = 0.0, b = 0.0;
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                case 5: r = v; g = p; b = q; break;
            }

            return Color.FromArgb(alpha, (byte)(r * 255.0), (byte)(g * 255.0), (byte)(b * 255.0));
        }

        #endregion

        #region UI Mapping Helpers

        private Grid GetCanvasGridForTarget(string target) => target switch
        {
            _textColorKey => TextColorCanvasGrid,
            _borderColorKey => BorderColorCanvasGrid,
            _backgroundColorKey => BackgroundColorCanvasGrid,
            _ => throw new ArgumentException(_unknownTargetMessage)
        };

        private System.Windows.Shapes.Rectangle GetHueCursorForTarget(string target) => target switch
        {
            _textColorKey => TextColorHueCursor,
            _borderColorKey => BorderColorHueCursor,
            _backgroundColorKey => BackgroundColorHueCursor,
            _ => throw new ArgumentException(_unknownTargetMessage)
        };

        private Slider GetOpacitySliderForTarget(string target) => target switch
        {
            _textColorKey => TextOpacitySlider,
            _borderColorKey => BorderOpacitySlider,
            _backgroundColorKey => BackgroundOpacitySlider,
            _ => throw new ArgumentException(_unknownTargetMessage)
        };

        private TextBox GetTextBoxForTarget(string target) => target switch
        {
            _textColorKey => TextColorTextBox,
            _borderColorKey => BorderColorTextBox,
            _backgroundColorKey => BackgroundColorTextBox,
            _ => throw new ArgumentException(_unknownTargetMessage)
        };

        private Border GetCanvasForTarget(string target) => target switch
        {
            _textColorKey => TextColorCanvas,
            _borderColorKey => BorderColorCanvas,
            _backgroundColorKey => BackgroundColorCanvas,
            _ => throw new ArgumentException(_unknownTargetMessage)
        };

        private System.Windows.Shapes.Ellipse GetCanvasCursorForTarget(string target) => target switch
        {
            _textColorKey => TextColorCursor,
            _borderColorKey => BorderColorCursor,
            _backgroundColorKey => BackgroundColorCursor,
            _ => throw new ArgumentException(_unknownTargetMessage)
        };

        private double GetHueForTarget(string target)
        {
            var textBox = GetTextBoxForTarget(target);
            try
            {
                string hex = textBox.Text.Trim();
                if (!hex.StartsWith('#')) hex = "#" + hex;
                Color color = (Color)ColorConverter.ConvertFromString(hex);
                ColorToHsv(color, out double h, out _, out _);
                return h;
            }
            catch
            {
                return 0.0;
            }
        }

        private void GetSvForTarget(string target, out double s, out double v)
        {
            var textBox = GetTextBoxForTarget(target);
            try
            {
                string hex = textBox.Text.Trim();
                if (!hex.StartsWith('#')) hex = "#" + hex;
                Color color = (Color)ColorConverter.ConvertFromString(hex);
                ColorToHsv(color, out _, out s, out v);
            }
            catch
            {
                s = 0.0;
                v = 1.0;
            }
        }

        #endregion

        #endregion

        #endregion

        private void ScreenPicker_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string target = btn.Tag?.ToString() ?? "";

            // Hide the host window briefly so colors can be picked from the screen behind it.
            var hostWindow = Window.GetWindow(this);
            hostWindow?.Hide();
            System.Threading.Thread.Sleep(150);

            Color? pickedColor = ScreenColorPicker.ShowPicker();

            // Re-show the host window
            hostWindow?.Show();
            hostWindow?.Activate();

            if (pickedColor.HasValue)
            {
                var color = pickedColor.Value;

                TextBox? targetTextBox = target switch
                {
                    _textColorKey => TextColorTextBox,
                    _borderColorKey => BorderColorTextBox,
                    _backgroundColorKey => BackgroundColorTextBox,
                    _ => null
                };

                Slider? opacitySlider = target switch
                {
                    _textColorKey => TextOpacitySlider,
                    _borderColorKey => BorderOpacitySlider,
                    _backgroundColorKey => BackgroundOpacitySlider,
                    _ => null
                };

                if (targetTextBox != null && opacitySlider != null)
                {
                    byte a = (byte)opacitySlider.Value;
                    string hex = $"#{a:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

                    _isSynchronizing = true;
                    targetTextBox.Text = hex;
                    _isSynchronizing = false;

                    ColorTextBox_TextChanged(targetTextBox, null!);
                }
            }
        }

        #region Sound Picker

        private void SoundTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SoundTypeComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                string tag = selectedItem.Tag?.ToString() ?? "None";
                UpdateSoundUiVisibility(tag);
                ControlChanged(sender, e);
            }
        }

        private void UpdateSoundUiVisibility(string soundType)
        {
            if (GameSoundPanel == null || CustomSoundPanel == null) return;

            if (soundType == "BuiltIn")
            {
                GameSoundPanel.Visibility = Visibility.Visible;
                CustomSoundPanel.Visibility = Visibility.Collapsed;
            }
            else if (soundType == "Custom")
            {
                GameSoundPanel.Visibility = Visibility.Collapsed;
                CustomSoundPanel.Visibility = Visibility.Visible;
            }
            else
            {
                GameSoundPanel.Visibility = Visibility.Collapsed;
                CustomSoundPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void BrowseSound_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Sound Files (*.wav;*.mp3)|*.wav;*.mp3|All files (*.*)|*.*",
                Title = "Select Custom Alert Sound"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                CustomSoundTextBox.Text = openFileDialog.FileName;
            }
        }

        #endregion

        #region Category Switching & State Management

        private void CategorySelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CategorySelector == null) return;

            // Save old state
            SaveCategoryState(_currentCategory);

            // Load new state
            if (CategorySelector.SelectedItem is ComboBoxItem item)
            {
                _currentCategory = item.Tag?.ToString() ?? _uniquesCategory;
                LoadCategoryState(_currentCategory);
            }
        }

        private void SaveCategoryState(string categoryName)
        {
            if (CategoryEnabledCheckBox == null || TextColorTextBox == null || BorderColorTextBox == null ||
                BackgroundColorTextBox == null || FontSizeSlider == null || SoundTypeComboBox == null ||
                SoundIdComboBox == null || CustomSoundTextBox == null || MuteDefaultDropSoundCheckBox == null ||
                MapSizeComboBox == null || MapColorComboBox == null || MapShapeComboBox == null ||
                BeamColorComboBox == null || BeamTempCheckBox == null)
            {
                return;
            }

            var style = GetCategoryStyle(categoryName);
            if (style == null) return;

            style.Enabled = CategoryEnabledCheckBox.IsChecked == true;
            style.TextColor = TextColorTextBox.Text.Trim();
            style.BorderColor = BorderColorTextBox.Text.Trim();
            style.BackgroundColor = BackgroundColorTextBox.Text.Trim();
            style.FontSize = (int)FontSizeSlider.Value;

            style.SoundType = (SoundTypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "None";
            if (int.TryParse((SoundIdComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out int soundId))
            {
                style.AlertSoundId = soundId;
            }
            style.CustomSoundPath = CustomSoundTextBox.Text.Trim();
            style.MuteDefaultDropSound = MuteDefaultDropSoundCheckBox.IsChecked == true;

            // Minimap Icon: Size Color Shape
            string size = (MapSizeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0";
            string color = (MapColorComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            string shape = (MapShapeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            if (string.IsNullOrEmpty(shape))
            {
                style.MinimapIcon = "";
            }
            else
            {
                style.MinimapIcon = $"{size} {color} {shape}".Trim();
            }

            // PlayEffect: Color Temp
            string beamColor = (BeamColorComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            bool isTemp = BeamTempCheckBox.IsChecked == true;
            if (string.IsNullOrEmpty(beamColor))
            {
                style.PlayEffect = "";
            }
            else
            {
                style.PlayEffect = isTemp ? $"{beamColor} Temp" : beamColor;
            }
        }

        private void LoadCategoryState(string categoryName)
        {
            if (CategoryEnabledCheckBox == null || TextColorTextBox == null || BorderColorTextBox == null ||
                BackgroundColorTextBox == null || FontSizeSlider == null || SoundTypeComboBox == null ||
                SoundIdComboBox == null || CustomSoundTextBox == null || MuteDefaultDropSoundCheckBox == null ||
                MapSizeComboBox == null || MapColorComboBox == null || MapShapeComboBox == null ||
                BeamColorComboBox == null || BeamTempCheckBox == null)
            {
                return;
            }

            var style = GetCategoryStyle(categoryName);
            if (style == null) return;

            _isSynchronizing = true;

            CategoryEnabledCheckBox.IsChecked = style.Enabled;
            TextColorTextBox.Text = style.TextColor;
            BorderColorTextBox.Text = style.BorderColor;
            BackgroundColorTextBox.Text = style.BackgroundColor;
            FontSizeSlider.Value = style.FontSize;

            // Sound UI Loading
            SetComboBoxSelectedTag(SoundTypeComboBox, style.SoundType);
            SetComboBoxSelectedTag(SoundIdComboBox, style.AlertSoundId);
            CustomSoundTextBox.Text = style.CustomSoundPath;
            MuteDefaultDropSoundCheckBox.IsChecked = style.MuteDefaultDropSound;

            UpdateSoundUiVisibility(style.SoundType);

            // Minimap Icon Loading
            string minimap = style.MinimapIcon ?? "";
            string mSize = "0", mColor = "Orange", mShape = "";
            var mParts = minimap.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (mParts.Length >= 3)
            {
                mSize = mParts[0];
                mColor = mParts[1];
                mShape = mParts[2];
            }
            else if (mParts.Length == 2)
            {
                mColor = mParts[0];
                mShape = mParts[1];
            }
            SetComboBoxSelectedTag(MapSizeComboBox, mSize);
            SetComboBoxSelectedTag(MapColorComboBox, mColor);
            SetComboBoxSelectedTag(MapShapeComboBox, mShape);

            // PlayEffect Loading
            string effect = style.PlayEffect ?? "";
            string bColor = "";
            bool isTemp = false;
            if (!string.IsNullOrEmpty(effect))
            {
                var eParts = effect.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bColor = eParts[0];
                if (eParts.Length > 1 && eParts[1].Equals("Temp", StringComparison.OrdinalIgnoreCase))
                {
                    isTemp = true;
                }
            }
            SetComboBoxSelectedTag(BeamColorComboBox, bColor);
            BeamTempCheckBox.IsChecked = isTemp;

            _isSynchronizing = false;

            // Update UI elements manually
            ColorTextBox_TextChanged(TextColorTextBox, null!);
            ColorTextBox_TextChanged(BorderColorTextBox, null!);
            ColorTextBox_TextChanged(BackgroundColorTextBox, null!);

            UpdateLivePreview();
        }

        private void ControlChanged(object sender, EventArgs e)
        {
            if (_isSynchronizing) return;
            UpdateLivePreview();
        }

        private void Minimap_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ControlChanged(sender, e);
        }

        private void Beam_SelectionChanged(object sender, RoutedEventArgs e)
        {
            ControlChanged(sender, e);
        }

        #endregion

        #region Live Preview Renderer

        private void UpdateLivePreview()
        {
            if (CategoryEnabledCheckBox == null || TextColorTextBox == null || BorderColorTextBox == null ||
                BackgroundColorTextBox == null || FontSizeSlider == null || SoundTypeComboBox == null ||
                SoundIdComboBox == null || CustomSoundTextBox == null || MuteDefaultDropSoundCheckBox == null ||
                MapSizeComboBox == null || MapColorComboBox == null || MapShapeComboBox == null ||
                BeamColorComboBox == null || BeamTempCheckBox == null || PreviewLabelBorder == null ||
                PreviewLabelText == null || PreviewIconIndicator == null || PreviewIconChar == null ||
                PreviewBeamIndicator == null || PreviewBeamText == null)
            {
                return;
            }

            try
            {
                // Disable/Gray out preview if category is disabled
                if (CategoryEnabledCheckBox.IsChecked == false)
                {
                    PreviewLabelBorder.Background = new SolidColorBrush(Color.FromArgb(30, 20, 20, 20));
                    PreviewLabelBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(50, 80, 80, 80));
                    PreviewLabelText.Foreground = new SolidColorBrush(Color.FromArgb(100, 100, 100, 100));
                    PreviewLabelText.Text = "Disabled Category";

                    PreviewIconIndicator.Fill = Brushes.Transparent;
                    PreviewIconChar.Text = "";
                    PreviewBeamIndicator.Background = Brushes.Transparent;
                    PreviewBeamText.Text = "Disabled";
                    PreviewBeamText.Foreground = Brushes.Gray;
                    return;
                }

                PreviewLabelText.Text = _currentCategory switch
                {
                    _uniquesCategory => "Exotic Hammer of Flame",
                    "Gems" => "Uncut Skill Gem",
                    "ProgressionBases" => "Steel Greaves (Build Base)",
                    "EconomyTier1" => "Divine Orb",
                    "EconomyTier2" => "Exalted Orb",
                    _ => "Sample Ground Drop"
                };

                // Font Size scaling (Visual conversion)
                double rawSize = FontSizeSlider.Value;
                // Scale 18-45 to 11-20 for WPF visual layout size
                PreviewLabelText.FontSize = 10 + (((rawSize - 18) / (45 - 18)) * 10);

                // Color mappings
                var textCol = (Color)ColorConverter.ConvertFromString(TextColorTextBox.Text.Trim().StartsWith('#') ? TextColorTextBox.Text.Trim() : "#" + TextColorTextBox.Text.Trim());
                var borderCol = (Color)ColorConverter.ConvertFromString(BorderColorTextBox.Text.Trim().StartsWith('#') ? BorderColorTextBox.Text.Trim() : "#" + BorderColorTextBox.Text.Trim());
                var bgCol = (Color)ColorConverter.ConvertFromString(BackgroundColorTextBox.Text.Trim().StartsWith('#') ? BackgroundColorTextBox.Text.Trim() : "#" + BackgroundColorTextBox.Text.Trim());

                PreviewLabelText.Foreground = new SolidColorBrush(textCol);
                PreviewLabelBorder.BorderBrush = new SolidColorBrush(borderCol);
                PreviewLabelBorder.Background = bgCol.A == 0 ? Brushes.Black : new SolidColorBrush(bgCol); // default black ground shadow if transparent

                // Minimap Icon preview
                string iconColor = (MapColorComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
                string iconShape = (MapShapeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
                if (!string.IsNullOrEmpty(iconShape))
                {
                    var mapBrush = GetColorBrush(iconColor);
                    PreviewIconChar.Text = GetShapeChar(iconShape);
                    PreviewIconChar.Foreground = mapBrush;
                    PreviewIconIndicator.Fill = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
                }
                else
                {
                    PreviewIconChar.Text = "";
                    PreviewIconIndicator.Fill = Brushes.Transparent;
                }

                // Light Beam Effect preview
                string beamColor = (BeamColorComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
                bool isTemp = BeamTempCheckBox.IsChecked == true;
                if (!string.IsNullOrEmpty(beamColor))
                {
                    var beamBrush = GetColorBrush(beamColor);
                    PreviewBeamIndicator.Background = beamBrush;
                    PreviewBeamText.Text = isTemp ? $"{beamColor} (Temp)" : beamColor;
                    PreviewBeamText.Foreground = Brushes.Black;
                }
                else
                {
                    PreviewBeamIndicator.Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
                    PreviewBeamText.Text = "No Beam";
                    PreviewBeamText.Foreground = Brushes.Gray;
                }
            }
            catch
            {
                // Keep default preview state if user is typing incomplete hex values
            }
        }

        private static string GetShapeChar(string shape)
        {
            return shape switch
            {
                "Star" => "✦",
                "Circle" => "●",
                "Diamond" => "◆",
                "Hexagon" => "⬢",
                "Square" => "■",
                "Triangle" => "▲",
                "Cross" => "✚",
                "Moon" => "🌙",
                "Raindrop" => "💧",
                "Kite" => "♢",
                "Pentagon" => "⬠",
                "UpsideDownHouse" => "⌂",
                _ => ""
            };
        }

        private static SolidColorBrush GetColorBrush(string colorName)
        {
            return colorName switch
            {
                "Red" => Brushes.Red,
                "Orange" => new SolidColorBrush(Color.FromRgb(255, 97, 36)),
                "Yellow" => Brushes.Yellow,
                "Green" => Brushes.Green,
                "Blue" => Brushes.DeepSkyBlue,
                "Cyan" => Brushes.Cyan,
                "Purple" => Brushes.Purple,
                "Pink" => Brushes.Pink,
                "White" => Brushes.White,
                "Brown" => Brushes.SaddleBrown,
                "Grey" => Brushes.Gray,
                _ => Brushes.Transparent
            };
        }

        #endregion

        #region Presets Manager (args/filter_presets.json)

        private void LoadPresetsList()
        {
            _presets.Clear();

            // Insert core pre-built themes
            _presets[_defaultThemeName] = new FilterTheme { ThemeName = _defaultThemeName };
            _presets[_tealThemeName] = GetTealEclipsePreset();
            _presets[_crimsonThemeName] = GetCrimsonHazePreset();

            // Load saved user presets if file exists
            if (File.Exists(_presetsFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_presetsFilePath);
                    var customPresets = JsonSerializer.Deserialize<Dictionary<string, FilterTheme>>(json);
                    if (customPresets != null)
                    {
                        foreach (var kvp in customPresets)
                        {
                            _presets[kvp.Key] = kvp.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load presets: {ex.Message}");
                }
            }

            // Bind to ComboBox
            PresetComboBox.ItemsSource = _presets.Keys.ToList();

            if (_isInitializing)
            {
                string initialThemeName = WorkingTheme?.ThemeName ?? _defaultThemeName;
                if (_presets.ContainsKey(initialThemeName))
                {
                    PresetComboBox.SelectedItem = initialThemeName;
                }
                else
                {
                    PresetComboBox.SelectedItem = _defaultThemeName;
                }
            }
        }

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PresetNameTextBox == null) return;

            if (PresetComboBox.SelectedItem is string presetName && _presets.TryGetValue(presetName, out var theme))
            {
                if (!_isInitializing)
                {
                    // Deep clone selected theme to WorkingTheme
                    string json = JsonSerializer.Serialize(theme);
                    WorkingTheme = JsonSerializer.Deserialize<FilterTheme>(json) ?? new FilterTheme();
                }

                PresetNameTextBox.Text = presetName;

                // Load active category
                LoadCategoryState(_currentCategory);
            }
        }

        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            string name = PresetNameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Please enter a name for the preset.", "Preset Name Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Save state of current category
            SaveCategoryState(_currentCategory);

            // Clone WorkingTheme
            string jsonTheme = JsonSerializer.Serialize(WorkingTheme);
            var themeClone = JsonSerializer.Deserialize<FilterTheme>(jsonTheme) ?? new FilterTheme();
            themeClone.ThemeName = name;

            _presets[name] = themeClone;

            // Save custom presets to file
            var userPresets = new Dictionary<string, FilterTheme>();
            foreach (var kvp in _presets)
            {
                // Only save custom presets (exclude defaults)
                if (kvp.Key != _defaultThemeName && kvp.Key != _tealThemeName && kvp.Key != _crimsonThemeName)
                {
                    userPresets[kvp.Key] = kvp.Value;
                }
            }

            try
            {
                string json = JsonSerializer.Serialize(userPresets, _jsonOptions);
                File.WriteAllText(_presetsFilePath, json);

                // Refresh list
                LoadPresetsList();
                PresetComboBox.SelectedItem = name;

                MessageBox.Show($"Preset '{name}' saved successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save preset: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetComboBox.SelectedItem is not string name) return;

            if (name == _defaultThemeName || name == _tealThemeName || name == _crimsonThemeName)
            {
                MessageBox.Show("Default themes cannot be deleted.", "Protected Preset", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"Are you sure you want to delete preset '{name}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _presets.Remove(name);

                // Re-save custom list
                var userPresets = new Dictionary<string, FilterTheme>();
                foreach (var kvp in _presets)
                {
                    if (kvp.Key != _defaultThemeName && kvp.Key != _tealThemeName && kvp.Key != _crimsonThemeName)
                    {
                        userPresets[kvp.Key] = kvp.Value;
                    }
                }

                try
                {
                    string json = JsonSerializer.Serialize(userPresets, _jsonOptions);
                    File.WriteAllText(_presetsFilePath, json);

                    LoadPresetsList();
                    PresetComboBox.SelectedItem = _defaultThemeName;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to delete preset: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Reset theme to base defaults?", "Confirm Reset", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                WorkingTheme = new FilterTheme();
                LoadCategoryState(_currentCategory);
                PresetNameTextBox.Text = _defaultThemeName;
                PresetComboBox.SelectedItem = _defaultThemeName;
            }
        }

        #endregion

        #region ComboBox Helpers

        private static void SetComboBoxSelectedTag(ComboBox comboBox, string tag)
        {
            foreach (ComboBoxItem item in comboBox.Items)
            {
                if (item.Tag?.ToString() == tag)
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }
        }

        private static void SetComboBoxSelectedTag(ComboBox comboBox, int tag)
        {
            SetComboBoxSelectedTag(comboBox, tag.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        #endregion

        #region Pre-built Theme Presets

        private static FilterTheme GetTealEclipsePreset()
        {
            return new FilterTheme
            {
                ThemeName = _tealThemeName,
                Uniques = new()
                {
                    TextColor = "#FF00FFFF", BorderColor = "#FF00FFFF", BackgroundColor = "#FF002233", FontSize = 45,
                    SoundType = _builtInSoundType, AlertSoundId = 3, MinimapIcon = "0 Cyan Star", PlayEffect = "Cyan Temp"
                },
                Gems = new()
                {
                    TextColor = _tealColorCode, BorderColor = "#FF0088AA", FontSize = 40,
                    SoundType = "None"
                },
                ProgressionBases = new()
                {
                    TextColor = "#FFE0FFFF", BorderColor = _tealColorCode, FontSize = 35,
                    SoundType = "None", PlayEffect = "Cyan Temp"
                },
                EconomyTier1 = new()
                {
                    TextColor = _tealColorCode, BorderColor = _tealColorCode, BackgroundColor = "#FF002233", FontSize = 45,
                    SoundType = _builtInSoundType, AlertSoundId = 6, MinimapIcon = "0 Cyan Star", PlayEffect = "Cyan"
                },
                EconomyTier2 = new()
                {
                    TextColor = "#FF0088AA", BorderColor = "#FF0088AA", FontSize = 40,
                    SoundType = _builtInSoundType, AlertSoundId = 2, MinimapIcon = "1 Blue Star", PlayEffect = "Blue Temp"
                }
            };
        }

        private static FilterTheme GetCrimsonHazePreset()
        {
            return new FilterTheme
            {
                ThemeName = _crimsonThemeName,
                Uniques = new()
                {
                    TextColor = "#FFFF0055", BorderColor = "#FFFF0055", BackgroundColor = "#FF330011", FontSize = 45,
                    SoundType = _builtInSoundType, AlertSoundId = 1, MinimapIcon = "0 Red Star", PlayEffect = "Red Temp"
                },
                Gems = new()
                {
                    TextColor = "#FFFF3366", BorderColor = "#FF880022", FontSize = 40,
                    SoundType = "None"
                },
                ProgressionBases = new()
                {
                    TextColor = "#FFFFE0E5", BorderColor = "#FFFF0055", FontSize = 35,
                    SoundType = "None", PlayEffect = "Red Temp"
                },
                EconomyTier1 = new()
                {
                    TextColor = "#FFFF0000", BorderColor = "#FFFF0000", BackgroundColor = "#FF330011", FontSize = 45,
                    SoundType = _builtInSoundType, AlertSoundId = 6, MinimapIcon = "0 Red Star", PlayEffect = "Red"
                },
                EconomyTier2 = new()
                {
                    TextColor = "#FFBB0022", BorderColor = "#FFBB0022", FontSize = 40,
                    SoundType = _builtInSoundType, AlertSoundId = 2, MinimapIcon = "1 Red Circle", PlayEffect = "Red Temp"
                }
            };
        }

        private CategoryStyle? GetCategoryStyle(string categoryName)
        {
            return categoryName switch
            {
                _uniquesCategory => WorkingTheme.Uniques,
                "Gems" => WorkingTheme.Gems,
                "ProgressionBases" => WorkingTheme.ProgressionBases,
                "EconomyTier1" => WorkingTheme.EconomyTier1,
                "EconomyTier2" => WorkingTheme.EconomyTier2,
                _ => null
            };
        }

        #endregion
    }

    internal static partial class ScreenColorPicker
    {
        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        private static partial IntPtr GetDC(IntPtr hwnd);

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        private static partial int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [System.Runtime.InteropServices.LibraryImport("gdi32.dll")]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        private static partial uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

        public static Color? ShowPicker()
        {
            Color? selectedColor = null;

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                Topmost = true,
                ShowInTaskbar = false,
                Cursor = System.Windows.Input.Cursors.Cross,
                WindowStartupLocation = WindowStartupLocation.Manual
            };

            double left = SystemParameters.VirtualScreenLeft;
            double top = SystemParameters.VirtualScreenTop;
            double width = SystemParameters.VirtualScreenWidth;
            double height = SystemParameters.VirtualScreenHeight;

            window.Left = left;
            window.Top = top;
            window.Width = width;
            window.Height = height;

            var grid = new Grid();
            window.Content = grid;

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(255, 97, 36)),
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Width = 150,
                Height = 65,
                IsHitTestVisible = false
            };

            var stackPanel = new StackPanel();
            var colorPreview = new Border
            {
                Height = 15,
                Margin = new Thickness(0, 0, 0, 4),
                BorderThickness = new Thickness(1),
                BorderBrush = System.Windows.Media.Brushes.Gray,
                CornerRadius = new CornerRadius(2)
            };
            var hexText = new TextBlock
            {
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = new FontFamily("Consolas")
            };
            var tipText = new TextBlock
            {
                Text = "Click to pick | ESC to cancel",
                Foreground = System.Windows.Media.Brushes.DarkGray,
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };

            stackPanel.Children.Add(colorPreview);
            stackPanel.Children.Add(hexText);
            stackPanel.Children.Add(tipText);
            border.Child = stackPanel;
            grid.Children.Add(border);

            window.MouseMove += (s, e) =>
            {
                var pos = e.GetPosition(window);
                var screenPos = window.PointToScreen(pos);
                var color = GetPixelAt((int)screenPos.X, (int)screenPos.Y);

                colorPreview.Background = new SolidColorBrush(color);
                hexText.Text = $"RGB: {color.R},{color.G},{color.B} (#{color.R:X2}{color.G:X2}{color.B:X2})";

                double offsetX = pos.X + 20;
                double offsetY = pos.Y + 20;

                if (offsetX + border.Width > window.Width)
                {
                    offsetX = pos.X - border.Width - 20;
                }
                if (offsetY + border.Height > window.Height)
                {
                    offsetY = pos.Y - border.Height - 20;
                }

                border.Margin = new Thickness(offsetX, offsetY, 0, 0);
            };

            window.MouseLeftButtonDown += (s, e) =>
            {
                var pos = e.GetPosition(window);
                var screenPos = window.PointToScreen(pos);
                selectedColor = GetPixelAt((int)screenPos.X, (int)screenPos.Y);
                window.Close();
            };

            window.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    window.Close();
                }
            };

            window.ShowDialog();
            return selectedColor;
        }

        private static Color GetPixelAt(int x, int y)
        {
            IntPtr hdc = GetDC(IntPtr.Zero);
            uint pixel = GetPixel(hdc, x, y);
            _ = ReleaseDC(IntPtr.Zero, hdc);

            return Color.FromRgb(
                (byte)(pixel & 0x000000FF),
                (byte)((pixel & 0x0000FF00) >> 8),
                (byte)((pixel & 0x00FF0000) >> 16)
            );
        }
    }
}
