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
    public partial class StyleEditorWindow : Window
    {
        public FilterTheme WorkingTheme { get; private set; }
        private Dictionary<string, FilterTheme> _presets = new();
        private string _presetsFilePath = "";
        private string _currentCategory = "Uniques";
        private bool _isSynchronizing = false;

        public StyleEditorWindow(FilterTheme currentTheme)
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
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void SaveApply_Click(object sender, RoutedEventArgs e)
        {
            // Save state of current category
            SaveCategoryState(_currentCategory);

            this.DialogResult = true;
            this.Close();
        }

        #region Swatches Grid Setup

        private void PopulateSwatchGrids()
        {
            var swatches = new string[]
            {
                "#FFFF6124", // PoE2 Orange
                "#FFE5B560", // Gold
                "#FFFF0000", // Red
                "#FFFF00FF", // Pink
                "#FF00FF00", // Green
                "#FF00C8FF", // Teal
                "#FFA832A4", // Purple
                "#FFFFFFFF", // White
                "#FF888888", // Grey
                "#FF0C7B93", // Cyan
                "#FF1E1E1E", // Dark Slate
                "#00000000"  // Transparent
            };

            PopulateGrid(TextColorGrid, swatches, TextColorTextBox, TextColorPopup);
            PopulateGrid(BorderColorGrid, swatches, BorderColorTextBox, BorderColorPopup);
            PopulateGrid(BackgroundColorGrid, swatches, BackgroundColorTextBox, BackgroundColorPopup);
        }

        private void PopulateGrid(UniformGrid grid, string[] swatches, TextBox targetTextBox, Popup targetPopup)
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

            if (!hex.StartsWith("#"))
            {
                hex = "#" + hex;
            }

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                
                if (textBox == TextColorTextBox)
                {
                    TextColorBtn.Background = new SolidColorBrush(color);
                    _isSynchronizing = true;
                    TextOpacitySlider.Value = color.A;
                    _isSynchronizing = false;
                }
                else if (textBox == BorderColorTextBox)
                {
                    BorderColorBtn.Background = new SolidColorBrush(color);
                    _isSynchronizing = true;
                    BorderOpacitySlider.Value = color.A;
                    _isSynchronizing = false;
                }
                else if (textBox == BackgroundColorTextBox)
                {
                    BackgroundColorBtn.Background = new SolidColorBrush(color);
                    _isSynchronizing = true;
                    BackgroundOpacitySlider.Value = color.A;
                    _isSynchronizing = false;
                }

                UpdateLivePreview();
            }
            catch
            {
                // Ignore parsing errors while user is actively typing
            }
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

            if (!hex.StartsWith("#")) hex = "#" + hex;

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

        #endregion

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
                _currentCategory = item.Tag?.ToString() ?? "Uniques";
                LoadCategoryState(_currentCategory);
            }
        }

        private void SaveCategoryState(string categoryName)
        {
            var style = GetCategoryStyle(categoryName);
            if (style == null) return;

            style.Enabled = CategoryEnabledCheckBox.IsChecked == true;
            style.TextColor = TextColorTextBox.Text.Trim();
            style.BorderColor = BorderColorTextBox.Text.Trim();
            style.BackgroundColor = BackgroundColorTextBox.Text.Trim();
            style.FontSize = (int)FontSizeSlider.Value;

            style.SoundType = ((ComboBoxItem)SoundTypeComboBox.SelectedItem)?.Tag?.ToString() ?? "None";
            if (int.TryParse(((ComboBoxItem)SoundIdComboBox.SelectedItem)?.Tag?.ToString(), out int soundId))
            {
                style.AlertSoundId = soundId;
            }
            style.CustomSoundPath = CustomSoundTextBox.Text.Trim();
            style.MuteDefaultDropSound = MuteDefaultDropSoundCheckBox.IsChecked == true;

            // Minimap Icon: Size Color Shape
            string size = ((ComboBoxItem)MapSizeComboBox.SelectedItem)?.Tag?.ToString() ?? "0";
            string color = ((ComboBoxItem)MapColorComboBox.SelectedItem)?.Tag?.ToString() ?? "";
            string shape = ((ComboBoxItem)MapShapeComboBox.SelectedItem)?.Tag?.ToString() ?? "";
            if (string.IsNullOrEmpty(shape))
            {
                style.MinimapIcon = "";
            }
            else
            {
                style.MinimapIcon = $"{size} {color} {shape}".Trim();
            }

            // PlayEffect: Color Temp
            string beamColor = ((ComboBoxItem)BeamColorComboBox.SelectedItem)?.Tag?.ToString() ?? "";
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
            SetComboBoxSelectedTag(SoundIdComboBox, style.AlertSoundId.ToString());
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
            if (PreviewLabelBorder == null || PreviewLabelText == null || PreviewIconIndicator == null || PreviewIconChar == null || PreviewBeamIndicator == null || PreviewBeamText == null) return;

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
                    "Uniques" => "Exotic Hammer of Flame",
                    "Gems" => "Uncut Skill Gem",
                    "ProgressionBases" => "Steel Greaves (Build Base)",
                    "EconomyTier1" => "Divine Orb",
                    "EconomyTier2" => "Exalted Orb",
                    _ => "Sample Ground Drop"
                };

                // Font Size scaling (Visual conversion)
                double rawSize = FontSizeSlider.Value;
                // Scale 18-45 to 11-20 for WPF visual layout size
                PreviewLabelText.FontSize = 10 + ((rawSize - 18) / (45 - 18)) * 10;

                // Color mappings
                var textCol = (Color)ColorConverter.ConvertFromString(TextColorTextBox.Text.Trim().StartsWith("#") ? TextColorTextBox.Text.Trim() : "#" + TextColorTextBox.Text.Trim());
                var borderCol = (Color)ColorConverter.ConvertFromString(BorderColorTextBox.Text.Trim().StartsWith("#") ? BorderColorTextBox.Text.Trim() : "#" + BorderColorTextBox.Text.Trim());
                var bgCol = (Color)ColorConverter.ConvertFromString(BackgroundColorTextBox.Text.Trim().StartsWith("#") ? BackgroundColorTextBox.Text.Trim() : "#" + BackgroundColorTextBox.Text.Trim());

                PreviewLabelText.Foreground = new SolidColorBrush(textCol);
                PreviewLabelBorder.BorderBrush = new SolidColorBrush(borderCol);
                PreviewLabelBorder.Background = bgCol.A == 0 ? Brushes.Black : new SolidColorBrush(bgCol); // default black ground shadow if transparent

                // Minimap Icon preview
                string iconColor = ((ComboBoxItem)MapColorComboBox.SelectedItem)?.Tag?.ToString() ?? "";
                string iconShape = ((ComboBoxItem)MapShapeComboBox.SelectedItem)?.Tag?.ToString() ?? "";
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
                string beamColor = ((ComboBoxItem)BeamColorComboBox.SelectedItem)?.Tag?.ToString() ?? "";
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

        private string GetShapeChar(string shape)
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

        private Brush GetColorBrush(string colorName)
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
            _presets["Default LootPulse"] = new FilterTheme { ThemeName = "Default LootPulse" };
            _presets["Teal Eclipse"] = GetTealEclipsePreset();
            _presets["Crimson Haze"] = GetCrimsonHazePreset();

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
            PresetComboBox.SelectedItem = "Default LootPulse";
        }

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PresetComboBox.SelectedItem is string presetName && _presets.TryGetValue(presetName, out var theme))
            {
                // Deep clone selected theme to WorkingTheme
                string json = JsonSerializer.Serialize(theme);
                WorkingTheme = JsonSerializer.Deserialize<FilterTheme>(json) ?? new FilterTheme();
                
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
                if (kvp.Key != "Default LootPulse" && kvp.Key != "Teal Eclipse" && kvp.Key != "Crimson Haze")
                {
                    userPresets[kvp.Key] = kvp.Value;
                }
            }

            try
            {
                string json = JsonSerializer.Serialize(userPresets, new JsonSerializerOptions { WriteIndented = true });
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

            if (name == "Default LootPulse" || name == "Teal Eclipse" || name == "Crimson Haze")
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
                    if (kvp.Key != "Default LootPulse" && kvp.Key != "Teal Eclipse" && kvp.Key != "Crimson Haze")
                    {
                        userPresets[kvp.Key] = kvp.Value;
                    }
                }

                try
                {
                    string json = JsonSerializer.Serialize(userPresets, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_presetsFilePath, json);
                    
                    LoadPresetsList();
                    PresetComboBox.SelectedItem = "Default LootPulse";
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
                PresetNameTextBox.Text = "Default LootPulse";
                PresetComboBox.SelectedItem = "Default LootPulse";
            }
        }

        #endregion

        #region ComboBox Helpers

        private void SetComboBoxSelectedTag(ComboBox comboBox, string tag)
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

        private void SetComboBoxSelectedTag(ComboBox comboBox, int tag)
        {
            SetComboBoxSelectedTag(comboBox, tag.ToString());
        }

        #endregion

        #region Pre-built Theme Presets

        private FilterTheme GetTealEclipsePreset()
        {
            return new FilterTheme
            {
                ThemeName = "Teal Eclipse",
                Uniques = new() 
                { 
                    TextColor = "#FF00FFFF", BorderColor = "#FF00FFFF", BackgroundColor = "#FF002233", FontSize = 45, 
                    SoundType = "BuiltIn", AlertSoundId = 3, MinimapIcon = "0 Cyan Star", PlayEffect = "Cyan Temp" 
                },
                Gems = new() 
                { 
                    TextColor = "#FF00C8FF", BorderColor = "#FF0088AA", FontSize = 40, 
                    SoundType = "None" 
                },
                ProgressionBases = new() 
                { 
                    TextColor = "#FFE0FFFF", BorderColor = "#FF00C8FF", FontSize = 35, 
                    SoundType = "None", PlayEffect = "Cyan Temp" 
                },
                EconomyTier1 = new() 
                { 
                    TextColor = "#FF00C8FF", BorderColor = "#FF00C8FF", BackgroundColor = "#FF002233", FontSize = 45, 
                    SoundType = "BuiltIn", AlertSoundId = 6, MinimapIcon = "0 Cyan Star", PlayEffect = "Cyan" 
                },
                EconomyTier2 = new() 
                { 
                    TextColor = "#FF0088AA", BorderColor = "#FF0088AA", FontSize = 40, 
                    SoundType = "BuiltIn", AlertSoundId = 2, MinimapIcon = "1 Blue Star", PlayEffect = "Blue Temp" 
                }
            };
        }

        private FilterTheme GetCrimsonHazePreset()
        {
            return new FilterTheme
            {
                ThemeName = "Crimson Haze",
                Uniques = new() 
                { 
                    TextColor = "#FFFF0055", BorderColor = "#FFFF0055", BackgroundColor = "#FF330011", FontSize = 45, 
                    SoundType = "BuiltIn", AlertSoundId = 1, MinimapIcon = "0 Red Star", PlayEffect = "Red Temp" 
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
                    SoundType = "BuiltIn", AlertSoundId = 6, MinimapIcon = "0 Red Star", PlayEffect = "Red" 
                },
                EconomyTier2 = new() 
                { 
                    TextColor = "#FFBB0022", BorderColor = "#FFBB0022", FontSize = 40, 
                    SoundType = "BuiltIn", AlertSoundId = 2, MinimapIcon = "1 Red Circle", PlayEffect = "Red Temp" 
                }
            };
        }

        private CategoryStyle? GetCategoryStyle(string categoryName)
        {
            return categoryName switch
            {
                "Uniques" => WorkingTheme.Uniques,
                "Gems" => WorkingTheme.Gems,
                "ProgressionBases" => WorkingTheme.ProgressionBases,
                "EconomyTier1" => WorkingTheme.EconomyTier1,
                "EconomyTier2" => WorkingTheme.EconomyTier2,
                _ => null
            };
        }

        #endregion
    }
}
