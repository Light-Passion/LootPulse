using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using LootPulse.Models;
using LootPulse.Services;

namespace LootPulse
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Catching generic Exception in UI/Tray/Process-detection controllers is necessary to prevent app crashes.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "This desktop overlay utility does not support localized resource tables.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "MainWindow resources are explicitly cleaned up and disposed on the OnClosed event override, which handles the window closing lifecycle.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "WPF UI synchronization context is required for dispatching UI updates.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "MainWindow is the entry point window of the WPF application and is publicly instantiated by the framework.")]
    public partial class MainWindow : Window
    {
        // Services
        private readonly PoeNinjaClient _ninjaClient;
        private readonly BuildProfileParser _buildParser;
        private readonly ClientLogMonitor _logMonitor;
        private readonly FilterBuilder _filterBuilder;

        // Active State Data
        private PoeBuild? _activeBuild;
        public FilterTheme ActiveTheme { get; set; } = new();
        private List<MarketItem> _marketItems = [];
        private readonly PlayerState _playerState = new();
        private bool _isClickThroughEnabled;
        private string? _selectedBaseFilterPath;
        private FileSystemWatcher? _baseFilterWatcher;
        private bool _isBaseFilterMissingOnStartup;

        // Win32 Interop Constants
        private const int _hotkeyId = 9000;
        private const int _hotkeyHudId = 9001;

        private readonly HudWindow? _hudWindow;
        private readonly AppSettings _appSettings = new();
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        // Tray Icon and Process Detection State
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isExitingFromTray;
        private bool _wasGameRunning;
        private CancellationTokenSource? _processCheckCts;

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [LibraryImport("user32.dll", EntryPoint = "RegisterHotKey")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [LibraryImport("user32.dll", EntryPoint = "UnregisterHotKey")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

        private IntPtr _hwnd;
        private HwndSource? _hwndSource;

        public MainWindow()
        {
            InitializeComponent();

            // Initialize Services
            _ninjaClient = new PoeNinjaClient();
            _buildParser = new BuildProfileParser();
            _logMonitor = new ClientLogMonitor();
            _filterBuilder = new FilterBuilder();

            // Bind log monitor event
            _logMonitor.ZoneChanged += LogMonitor_ZoneChanged;

            // Load saved settings (or defaults)
            LoadSettings();

            // Create HUD Window
            _hudWindow = new HudWindow(_appSettings, OnHudPositionChanged);
            _hudWindow.Show();

            // Populate base filter options
            LoadBaseFilterOptions();

            // Initialize FileSystemWatcher for the loaded base filter
            SetupBaseFilterWatcher(_selectedBaseFilterPath);

            // Load Saved Theme
            LoadActiveTheme();

            // Mock list view items until sync
            LoadMockItems();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Set up Win32 handles and hook messages for Global Hotkeys
            var helper = new WindowInteropHelper(this);
            _hwnd = helper.Handle;
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource.AddHook(HwndHook);

            // Register Hotkeys
            RegisterHotKey(_hwnd, _hotkeyId, 0x0006, 0x4F); // Ctrl + Shift + O
            RegisterHotKey(_hwnd, _hotkeyHudId, 0x0006, 0x48); // Ctrl + Shift + H

            // Initialize monitoring if log file exists
            if (File.Exists(LogPathBox.Text))
            {
                _logMonitor.StartMonitoring(LogPathBox.Text);
            }

            // Apply opacity immediately to MainWindow
            if (MainWindowBorder != null && _appSettings != null)
            {
                MainWindowBorder.Background = new SolidColorBrush(Color.FromArgb((byte)(_appSettings.EditModeOpacity * 255), 18, 18, 18));
            }

            // Initialize Tray Icon
            InitializeTrayIcon();

            // Start process detection for Path of Exile 2
            _processCheckCts = new CancellationTokenSource();
            _ = StartProcessDetectionAsync(_processCheckCts.Token);

            // Check if base filter was missing on load
            CheckMissingBaseFilter();
        }

        protected override void OnClosed(EventArgs e)
        {
            _logMonitor.StopMonitoring();
            _hwndSource?.RemoveHook(HwndHook);
            UnregisterHotKey(_hwnd, _hotkeyId);
            UnregisterHotKey(_hwnd, _hotkeyHudId);

            if (_baseFilterWatcher != null)
            {
                _baseFilterWatcher.EnableRaisingEvents = false;
                _baseFilterWatcher.Dispose();
            }

            _processCheckCts?.Cancel();
            _processCheckCts?.Dispose();

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }

            _hudWindow?.Close();

            base.OnClosed(e);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            ArgumentNullException.ThrowIfNull(e);

            if (!_isExitingFromTray)
            {
                e.Cancel = true;
                this.Hide();
                StatusText.Text = "LootPulse minimized to tray.";
            }
            else
            {
                base.OnClosing(e);
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == _hotkeyId)
                {
                    ToggleOverlayMode();
                    handled = true;
                }
                else if (id == _hotkeyHudId)
                {
                    ToggleHudVisibility();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void ToggleOverlayMode()
        {
            _isClickThroughEnabled = !_isClickThroughEnabled;

            if (_isClickThroughEnabled)
            {
                // Enter HUD Mode: Hide dashboard, lock HUD and make click-through
                this.Hide();

                _hudWindow?.SetClickThrough(true, _appSettings.HudModeOpacity);
                if (_appSettings.IsHudVisible)
                {
                    _hudWindow?.Show();
                }
                else
                {
                    _hudWindow?.Hide();
                }
            }
            else
            {
                // Enter Edit Mode: Show dashboard, unlock HUD
                this.Show();
                this.Activate();

                _hudWindow?.SetClickThrough(false, _appSettings.EditModeOpacity);
                _hudWindow?.Show();
            }
        }

        private void LogMonitor_ZoneChanged(object? sender, ZoneChangedEventArgs e)
        {
            // Invoke on the UI thread to update controls
            Dispatcher.Invoke(() =>
            {
                _playerState.CurrentZone = e.ZoneName;
                _playerState.ZoneLevel = e.ZoneLevel;
                CharZoneText.Text = $"{e.ZoneName} (Level {e.ZoneLevel})";
                StatusText.Text = $"Entered zone: {e.ZoneName} (Level {e.ZoneLevel}). Regenerating filter...";

                // Update HUD display
                _hudWindow?.UpdateDisplay(_playerState.CharacterName, _playerState.Level, e.ZoneName, e.ZoneLevel, "Zone Changed");

                // Dynamic regeneration based on zone transition
                TriggerFilterRegeneration();
            });
        }

        private void TriggerFilterRegeneration()
        {
            _ = double.TryParse(Tier1Box.Text, System.Globalization.CultureInfo.InvariantCulture, out double t1);
            _ = double.TryParse(Tier2Box.Text, System.Globalization.CultureInfo.InvariantCulture, out double t2);

            bool success = _filterBuilder.GenerateFilterFile(
                FilterPathBox.Text,
                _selectedBaseFilterPath,
                _marketItems,
                _activeBuild,
                _playerState.Level,
                _playerState.ZoneLevel,
                t1 > 0 ? t1 : 100,
                t2 > 0 ? t2 : 10,
                ActiveTheme
            );

            if (success)
            {
                StatusText.Text = "Filter updated! Remember to reload in PoE2 Settings (Options -> Game -> Item Filter -> Reload).";
                _hudWindow?.UpdateDisplay(_playerState.CharacterName, _playerState.Level, _playerState.CurrentZone, _playerState.ZoneLevel, "Filter Merged");
                SaveSettings();
            }
            else
            {
                StatusText.Text = "Failed to compile filter file.";
            }
        }

        // --- Event Handlers & Button Clicks ---

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void SyncAll_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Syncing with poe.ninja...";
            // We use Mirage/Standard or configurable league (Default to Mirage)
            const string activeLeague = "Mirage";

            var fetchedCurrencies = await _ninjaClient.FetchCurrencyPricesAsync(activeLeague);
            var fetchedGems = await _ninjaClient.FetchItemPricesAsync(activeLeague, "SkillGem", "Gems");
            var fetchedUniques = await _ninjaClient.FetchItemPricesAsync(activeLeague, "UniqueWeapon", "Unique Weapons");

            _marketItems.Clear();
            _marketItems.AddRange(fetchedCurrencies);
            _marketItems.AddRange(fetchedGems);
            _marketItems.AddRange(fetchedUniques);

            ItemListView.ItemsSource = null;
            ItemListView.ItemsSource = _marketItems;

            TriggerFilterRegeneration();
        }

        private void ImportPob_Click(object sender, RoutedEventArgs e)
        {
            // Open simple input box prompt
            string pobCode = Microsoft.VisualBasic.Interaction.InputBox(
                "Paste your Path of Building (PoB2) Share Code here:",
                "Import PoB2 Build",
                "");

            if (!string.IsNullOrWhiteSpace(pobCode))
            {
                var xml = _buildParser.DecodePobShareCode(pobCode);
                if (!string.IsNullOrEmpty(xml))
                {
                    var build = _buildParser.ConvertPobXmlToPoeBuild(xml);
                    if (build != null)
                    {
                        _activeBuild = build;
                        BuildNameText.Text = build.Name;
                        StatusText.Text = $"Loaded PoB build: {build.Name}";
                        TriggerFilterRegeneration();
                        return;
                    }
                }
                MessageBox.Show("Failed to decode share code. Ensure it is a valid PoB export code.", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SelectBuildFile_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "PoE2 Build Planner Files (*.build)|*.build|All Files (*.*)|*.*",
                Title = "Select Path of Exile 2 .build File"
            };

            if (ofd.ShowDialog() == true)
            {
                var build = _buildParser.ParseBuildFile(ofd.FileName);
                if (build != null)
                {
                    _activeBuild = build;
                    BuildNameText.Text = build.Name;
                    StatusText.Text = $"Loaded .build file: {build.Name}";
                    TriggerFilterRegeneration();
                }
                else
                {
                    MessageBox.Show("Invalid build planner file format.", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BrowseLog_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "Log Files (Client.txt)|Client.txt|All Files (*.*)|*.*",
                Title = "Select Path of Exile 2 Client.txt Log"
            };

            if (ofd.ShowDialog() == true)
            {
                LogPathBox.Text = ofd.FileName;
                _logMonitor.StopMonitoring();
                _logMonitor.StartMonitoring(ofd.FileName);
                SaveSettings();
            }
        }

        private void BrowseFilter_Click(object sender, RoutedEventArgs e)
        {
            var sfd = new SaveFileDialog
            {
                Filter = "Filter Files (*.filter)|*.filter|All Files (*.*)|*.*",
                Title = "Select PoE2 Output Filter Path"
            };

            if (sfd.ShowDialog() == true)
            {
                FilterPathBox.Text = sfd.FileName;
                SaveSettings();
            }
        }

        private void LoadMockItems()
        {
            _marketItems =
            [
                new() { Name = "Divine Orb", Category = "Currency", ChaosValue = 125.0, LastUpdated = DateTime.UtcNow },
                new() { Name = "Exalted Orb", Category = "Currency", ChaosValue = 15.0, LastUpdated = DateTime.UtcNow },
                new() { Name = "Chaos Orb", Category = "Currency", ChaosValue = 1.0, LastUpdated = DateTime.UtcNow },
                new() { Name = "Uncut Skill Gem (Level 19)", Category = "Gems", ChaosValue = 45.0, LastUpdated = DateTime.UtcNow }
            ];
            ItemListView.ItemsSource = _marketItems;
        }

        private void LoadActiveTheme()
        {
            string themePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "args", "active_theme.json");
            if (File.Exists(themePath))
            {
                try
                {
                    string json = File.ReadAllText(themePath);
                    var theme = JsonSerializer.Deserialize<FilterTheme>(json);
                    if (theme != null)
                    {
                        ActiveTheme = theme;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load active theme: {ex.Message}");
                }
            }
        }

        private void SaveActiveTheme()
        {
            string themePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "args", "active_theme.json");
            try
            {
                string json = JsonSerializer.Serialize(ActiveTheme, _jsonOptions);
                File.WriteAllText(themePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save active theme: {ex.Message}");
            }
        }

        private void CustomizeStyles_Click(object sender, RoutedEventArgs e)
        {
            var editor = new StyleEditorWindow(ActiveTheme) { Owner = this };
            if (editor.ShowDialog() == true)
            {
                ActiveTheme = editor.WorkingTheme;
                SaveActiveTheme();
                TriggerFilterRegeneration();
            }
        }

        // --- Settings & Base Filter Merging Methods ---

        private static string GetSettingsFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "LootPulse", "settings.json");
        }

        private void LoadSettings()
        {
            string settingsFile = GetSettingsFilePath();
            if (File.Exists(settingsFile))
            {
                try
                {
                    string json = File.ReadAllText(settingsFile);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        LogPathBox.Text = settings.LogPath;
                        FilterPathBox.Text = settings.FilterOutputPath;
                        _selectedBaseFilterPath = settings.SelectedBaseFilterPath;
                        Tier1Box.Text = settings.Tier1Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        Tier2Box.Text = settings.Tier2Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
                }
            }

            // Default Fallbacks
            string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            LogPathBox.Text = Path.Combine(myDocuments, @"My Games\Path of Exile 2\logs\Client.txt");
            FilterPathBox.Text = Path.Combine(myDocuments, @"My Games\Path of Exile 2\LootPulse_Only.filter");
            _selectedBaseFilterPath = string.Empty;
        }

        private void SaveSettings()
        {
            try
            {
                string settingsFile = GetSettingsFilePath();
                string? directory = Path.GetDirectoryName(settingsFile);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _ = double.TryParse(Tier1Box.Text, System.Globalization.CultureInfo.InvariantCulture, out double t1);
                _ = double.TryParse(Tier2Box.Text, System.Globalization.CultureInfo.InvariantCulture, out double t2);

                var settings = new AppSettings
                {
                    LogPath = LogPathBox.Text,
                    FilterOutputPath = FilterPathBox.Text,
                    SelectedBaseFilterPath = _selectedBaseFilterPath ?? string.Empty,
                    Tier1Threshold = t1 > 0 ? t1 : 100,
                    Tier2Threshold = t2 > 0 ? t2 : 10
                };

                string json = JsonSerializer.Serialize(settings, _jsonOptions);
                File.WriteAllText(settingsFile, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        public static string ParseFilterDisplayName(string filepath)
        {
            try
            {
                using var reader = new StreamReader(filepath);
                for (int i = 0; i < 15; i++)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;

                    if (line.StartsWith("#name:", StringComparison.OrdinalIgnoreCase))
                    {
                        return line[6..].Trim();
                    }
                }
            }
            catch
            {
                // Fallback
            }
            return Path.GetFileName(filepath);
        }

        private void LoadBaseFilterOptions()
        {
            BaseFilterComboBox.SelectionChanged -= BaseFilterComboBox_SelectionChanged;
            BaseFilterComboBox.Items.Clear();

            var noneOption = new FilterOption { DisplayName = "None (LootPulse Highlights Only)", FilePath = string.Empty, IsSubscribed = false };
            BaseFilterComboBox.Items.Add(noneOption);

            string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string onlineFiltersDir = Path.Combine(myDocs, @"My Games\Path of Exile 2\OnlineFilters");

            FilterOption? selectedOption = noneOption;

            if (Directory.Exists(onlineFiltersDir))
            {
                try
                {
                    var files = Directory.GetFiles(onlineFiltersDir);
                    foreach (var file in files)
                    {
                        string displayName = ParseFilterDisplayName(file);
                        var option = new FilterOption
                        {
                            DisplayName = $"{displayName} (Subscribed)",
                            FilePath = file,
                            IsSubscribed = true
                        };
                        BaseFilterComboBox.Items.Add(option);

                        if (string.Equals(file, _selectedBaseFilterPath, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedOption = option;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error scanning OnlineFilters: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(_selectedBaseFilterPath) && selectedOption == noneOption)
            {
                if (File.Exists(_selectedBaseFilterPath))
                {
                    var customOption = new FilterOption
                    {
                        DisplayName = $"{Path.GetFileName(_selectedBaseFilterPath)} (Local)",
                        FilePath = _selectedBaseFilterPath,
                        IsSubscribed = false
                    };
                    BaseFilterComboBox.Items.Add(customOption);
                    selectedOption = customOption;
                }
                else
                {
                    _isBaseFilterMissingOnStartup = true;
                }
            }

            BaseFilterComboBox.SelectedItem = selectedOption;
            BaseFilterComboBox.SelectionChanged += BaseFilterComboBox_SelectionChanged;
        }

        private void CheckMissingBaseFilter()
        {
            if (_isBaseFilterMissingOnStartup)
            {
                _isBaseFilterMissingOnStartup = false;
                var result = MessageBox.Show(
                    $"The previously selected base filter is no longer available at:\n{_selectedBaseFilterPath}\n\nWould you like to browse and select a new base filter? (Selecting 'No' will fall back to LootPulse highlights only)",
                    "Base Filter Missing",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    BrowseBaseFilter_Click(this, new RoutedEventArgs());
                }
                else
                {
                    _selectedBaseFilterPath = string.Empty;
                    BaseFilterComboBox.SelectedIndex = 0;
                    SaveSettings();
                    UpdateOutputFilterPath();
                    TriggerFilterRegeneration();
                }
            }
        }

        private void SetupBaseFilterWatcher(string? path)
        {
            if (_baseFilterWatcher != null)
            {
                _baseFilterWatcher.EnableRaisingEvents = false;
                _baseFilterWatcher.Dispose();
                _baseFilterWatcher = null;
            }

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            try
            {
                string? directory = Path.GetDirectoryName(path);
                string filename = Path.GetFileName(path);

                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                    return;

                _baseFilterWatcher = new FileSystemWatcher(directory, filename)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _baseFilterWatcher.Changed += BaseFilter_Changed;
                _baseFilterWatcher.Created += BaseFilter_Changed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to set up base filter watcher: {ex.Message}");
            }
        }

        private void BaseFilter_Changed(object sender, FileSystemEventArgs e)
        {
            Dispatcher.Invoke(async () =>
            {
                // Debounce briefly for GGG client to release file lock
                await Task.Delay(500);
                StatusText.Text = "Subscribed base filter update detected. Re-merging filter...";
                TriggerFilterRegeneration();
                FlashBorderAndPlaySound();
            });
        }

        private void FlashBorderAndPlaySound()
        {
            if (!_isClickThroughEnabled)
            {
                try
                {
                    System.Media.SystemSounds.Asterisk.Play();
                }
                catch (Exception ex)
                {
                    // Ignore exceptions if system sounds are not available or fail to play
                    System.Diagnostics.Debug.WriteLine($"Failed to play system sound: {ex.Message}");
                }
            }

            Color targetColor = _isClickThroughEnabled ? Colors.Transparent : Color.FromRgb(61, 61, 61);
            var flashBrush = new SolidColorBrush(Color.FromRgb(255, 97, 36));
            MainWindowBorder.BorderBrush = flashBrush;

            var animation = new ColorAnimation
            {
                From = Color.FromRgb(255, 255, 255),
                To = targetColor,
                Duration = new Duration(TimeSpan.FromSeconds(1.5)),
                FillBehavior = FillBehavior.HoldEnd
            };

            flashBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }

        private void UpdateOutputFilterPath()
        {
            if (BaseFilterComboBox.SelectedItem is FilterOption selectedOption)
            {
                string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string folder = Path.Combine(myDocs, @"My Games\Path of Exile 2");

                string namePart = "LootPulse_Only";
                if (!string.IsNullOrEmpty(selectedOption.FilePath))
                {
                    string cleanName = selectedOption.DisplayName
                        .Replace(" (Subscribed)", "", StringComparison.Ordinal)
                        .Replace(" (Local)", "", StringComparison.Ordinal);

                    foreach (char c in Path.GetInvalidFileNameChars())
                    {
                        cleanName = cleanName.Replace(c.ToString(), "_", StringComparison.Ordinal);
                    }
                    namePart = $"{cleanName}_LootPulse";
                }

                FilterPathBox.Text = Path.Combine(folder, $"{namePart}.filter");
                SaveSettings();
            }
        }

        private void BaseFilterComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (BaseFilterComboBox.SelectedItem is FilterOption selectedOption)
            {
                _selectedBaseFilterPath = selectedOption.FilePath;
                SetupBaseFilterWatcher(_selectedBaseFilterPath);
                UpdateOutputFilterPath();
                TriggerFilterRegeneration();
            }
        }

        private void BrowseBaseFilter_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "Filter Files (*.filter)|*.filter|All Files (*.*)|*.*",
                Title = "Select Base Filter to Merge"
            };

            if (ofd.ShowDialog() == true)
            {
                string selectedPath = ofd.FileName;

                FilterOption? existing = null;
                foreach (FilterOption item in BaseFilterComboBox.Items)
                {
                    if (string.Equals(item.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        existing = item;
                        break;
                    }
                }

                if (existing == null)
                {
                    existing = new FilterOption
                    {
                        DisplayName = $"{Path.GetFileName(selectedPath)} (Local)",
                        FilePath = selectedPath,
                        IsSubscribed = false
                    };
                    BaseFilterComboBox.Items.Add(existing);
                }

                BaseFilterComboBox.SelectedItem = existing;
            }
        }

        private void OnHudPositionChanged(AppSettings updatedSettings)
        {
            _appSettings.HudWidth = updatedSettings.HudWidth;
            _appSettings.HudHeight = updatedSettings.HudHeight;
            _appSettings.HudXPercent = updatedSettings.HudXPercent;
            _appSettings.HudYPercent = updatedSettings.HudYPercent;
            SaveSettings();
        }

        private void ToggleHudVisibility()
        {
            _appSettings.IsHudVisible = !_appSettings.IsHudVisible;

            // Sync Checkbox
            HudVisibleCheckBox.Checked -= HudVisibleCheckBox_Changed;
            HudVisibleCheckBox.Unchecked -= HudVisibleCheckBox_Changed;
            HudVisibleCheckBox.IsChecked = _appSettings.IsHudVisible;
            HudVisibleCheckBox.Checked += HudVisibleCheckBox_Changed;
            HudVisibleCheckBox.Unchecked += HudVisibleCheckBox_Changed;

            SaveSettings();

            if (_appSettings.IsHudVisible)
            {
                if (_isClickThroughEnabled)
                {
                    _hudWindow?.Show();
                }
                StatusText.Text = "HUD overlay enabled.";
            }
            else
            {
                _hudWindow?.Hide();
                StatusText.Text = "HUD overlay hidden.";
            }
        }

        private void HudVisibleCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_appSettings == null) return;

            _appSettings.IsHudVisible = HudVisibleCheckBox.IsChecked == true;
            SaveSettings();

            if (_hudWindow != null && _isClickThroughEnabled)
            {
                if (_appSettings.IsHudVisible)
                    _hudWindow.Show();
                else
                    _hudWindow.Hide();
            }
        }

        private void OpacitySliders_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_appSettings == null || EditOpacitySlider == null || HudOpacitySlider == null) return;

            _appSettings.EditModeOpacity = EditOpacitySlider.Value;
            _appSettings.HudModeOpacity = HudOpacitySlider.Value;
            SaveSettings();

            if (_isClickThroughEnabled)
            {
                _hudWindow?.SetClickThrough(true, _appSettings.HudModeOpacity);
            }
            else
            {
                _hudWindow?.SetClickThrough(false, _appSettings.EditModeOpacity);

                // Adjust MainWindow background opacity
                if (MainWindowBorder != null)
                {
                    MainWindowBorder.Background = new SolidColorBrush(Color.FromArgb((byte)(_appSettings.EditModeOpacity * 255), 18, 18, 18));
                }
            }
        }

        private void ResetHudPosition_Click(object sender, RoutedEventArgs e)
        {
            _appSettings.HudWidth = 250;
            _appSettings.HudHeight = 120;
            _appSettings.HudXPercent = 0.80;
            _appSettings.HudYPercent = 0.05;
            SaveSettings();

            if (_hudWindow != null)
            {
                _hudWindow.Width = 250;
                _hudWindow.Height = 120;
                _hudWindow.RestorePosition();
            }
            StatusText.Text = "HUD size and position reset to defaults.";
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S1075:URIs should not be hardcoded", Justification = "WPF Pack URIs are internal application resource addresses, not external endpoints.")]
        private void InitializeTrayIcon()
        {
            try
            {
                _notifyIcon = new() { Text = "LootPulse Overlay" };

                // Load icon from pack URI resource
                var iconUri = new Uri("pack://application:,,,/lootpulse.ico", UriKind.Absolute);
                var streamInfo = System.Windows.Application.GetResourceStream(iconUri);
                if (streamInfo != null)
                {
                    using var stream = streamInfo.Stream;
                    _notifyIcon.Icon = new System.Drawing.Icon(stream);
                }
                else
                {
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                }

                // Create Context Menu
                System.Windows.Forms.ContextMenuStrip contextMenu = new();

                System.Windows.Forms.ToolStripMenuItem headerItem = new("LootPulse Overlay") { Enabled = false };
                contextMenu.Items.Add(headerItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

                System.Windows.Forms.ToolStripMenuItem showDashboardItem = new("Show Dashboard", null, (s, e) => ShowDashboard());
                contextMenu.Items.Add(showDashboardItem);

                System.Windows.Forms.ToolStripMenuItem toggleOverlayItem = new("Toggle Overlay Mode (Ctrl+Shift+O)", null, (s, e) => ToggleOverlayMode());
                contextMenu.Items.Add(toggleOverlayItem);

                System.Windows.Forms.ToolStripMenuItem resetHudItem = new("Reset HUD Position", null, (s, e) => ResetHudPosition_Click(this, new RoutedEventArgs()));
                contextMenu.Items.Add(resetHudItem);

                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

                System.Windows.Forms.ToolStripMenuItem exitItem = new("Exit", null, (s, e) => ExitApplication());
                contextMenu.Items.Add(exitItem);

                _notifyIcon.ContextMenuStrip = contextMenu;
                _notifyIcon.Visible = true;

                // Double click behavior
                _notifyIcon.DoubleClick += (s, e) => ShowDashboard();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize tray icon: {ex.Message}");
            }
        }

        private void ShowDashboard()
        {
            if (_isClickThroughEnabled)
            {
                ToggleOverlayMode();
            }
            else
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            }
        }

        private void ExitApplication()
        {
            _isExitingFromTray = true;
            Close();
        }

        private async Task StartProcessDetectionAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool isRunning = IsPoE2Running();
                    if (isRunning && !_wasGameRunning)
                    {
                        await Dispatcher.InvokeAsync(OnGameLaunchDetected);
                    }
                    _wasGameRunning = isRunning;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error in process detection: {ex.Message}");
                }

                try
                {
                    await Task.Delay(2000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void OnGameLaunchDetected()
        {
            StatusText.Text = "Path of Exile 2 launch detected! Activating HUD overlay...";
            if (!_isClickThroughEnabled)
            {
                ToggleOverlayMode();
            }
            else
            {
                _appSettings.IsHudVisible = true;
                SaveSettings();
                if (_hudWindow != null)
                {
                    _hudWindow.SetClickThrough(true, _appSettings.HudModeOpacity);
                    _hudWindow.Show();
                }
            }
        }

        private static bool IsPoE2Running()
        {
            var processes = Process.GetProcesses();
            try
            {
                return processes.Any(p =>
                {
                    try
                    {
                        string name = p.ProcessName;
                        return name.Contains("PathOfExile2", StringComparison.OrdinalIgnoreCase) ||
                               name.Contains("PathOfExile_x64", StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception ex)
                    {
                        // Ignore process property access errors for processes that might be protected or have exited
                        System.Diagnostics.Debug.WriteLine($"Failed to access process name: {ex.Message}");
                        return false;
                    }
                });
            }
            finally
            {
                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
        }
    }

    public class FilterOption
    {
        public string DisplayName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public bool IsSubscribed { get; set; }
    }
}
