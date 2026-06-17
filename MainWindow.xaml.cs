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
        private const string _currencyCategory = "Currency";
        private const string _divineOrbName = "Divine Orb";
        private const string _exaltedOrbName = "Exalted Orb";
        private const string _chaosOrbName = "Chaos Orb";
        private const string _mirrorName = "Mirror of Kalandra";

        private const string _myGamesFolder = "My Games";
        private const string _poe2Folder = "Path of Exile 2";
        private const string _clientLogFile = "Client.txt";
        private const string _steamappsFolder = "steamapps";
        private const string _commonFolder = "common";

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
                t1 > 0 ? t1 : 1.0,
                t2 > 0 ? t2 : 1.0,
                ActiveTheme,
                _appSettings.ShowEconomyHighlights
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
            string activeLeague = _appSettings.League;
            if (string.IsNullOrEmpty(activeLeague))
            {
                activeLeague = "Runes of Aldur";
            }

            var fetchedCurrencies = await _ninjaClient.FetchCurrencyPricesAsync(activeLeague);
            var fetchedGems = await _ninjaClient.FetchItemPricesAsync(activeLeague, "SkillGem", "Gems");
            var fetchedUniques = await _ninjaClient.FetchItemPricesAsync(activeLeague, "UniqueWeapon", "Unique Weapons");

            _marketItems.Clear();
            _marketItems.AddRange(fetchedCurrencies);
            _marketItems.AddRange(fetchedGems);
            _marketItems.AddRange(fetchedUniques);
            NormalizeMarketValues(_marketItems);
            AddPinnedExchangeRateItem();

            ItemListView.ItemsSource = null;
            ItemListView.ItemsSource = _marketItems;

            TriggerFilterRegeneration();
        }

        private async void ImportPob_Click(object sender, RoutedEventArgs e)
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
                        await LoadBuildUniquePricesAsync(build);
                        TriggerFilterRegeneration();
                        return;
                    }
                }
                MessageBox.Show("Failed to decode share code. Ensure it is a valid PoB export code.", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SelectBuildFile_Click(object sender, RoutedEventArgs e)
        {
            string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), _myGamesFolder, _poe2Folder);
            var ofd = new OpenFileDialog
            {
                Filter = "PoE2 Build Planner Files (*.build)|*.build|All Files (*.*)|*.*",
                Title = "Select Path of Exile 2 .build File",
                InitialDirectory = Directory.Exists(defaultDir) ? defaultDir : string.Empty
            };

            if (ofd.ShowDialog() == true)
            {
                var build = _buildParser.ParseBuildFile(ofd.FileName);
                if (build != null)
                {
                    _activeBuild = build;
                    BuildNameText.Text = build.Name;
                    StatusText.Text = $"Loaded .build file: {build.Name}";
                    await LoadBuildUniquePricesAsync(build);
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
            string logFolder = string.Empty;
            try
            {
                if (!string.IsNullOrEmpty(LogPathBox.Text))
                {
                    string? dir = Path.GetDirectoryName(LogPathBox.Text);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        logFolder = dir;
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }

            var ofd = new OpenFileDialog
            {
                Filter = "Log Files (Client.txt)|Client.txt|All Files (*.*)|*.*",
                Title = "Select Path of Exile 2 Client.txt Log",
                InitialDirectory = logFolder
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
            string filterFolder = string.Empty;
            try
            {
                if (!string.IsNullOrEmpty(FilterPathBox.Text))
                {
                    string? dir = Path.GetDirectoryName(FilterPathBox.Text);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        filterFolder = dir;
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }

            if (string.IsNullOrEmpty(filterFolder))
            {
                string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), _myGamesFolder, _poe2Folder);
                if (Directory.Exists(defaultDir))
                {
                    filterFolder = defaultDir;
                }
            }

            var sfd = new SaveFileDialog
            {
                Filter = "Filter Files (*.filter)|*.filter|All Files (*.*)|*.*",
                Title = "Select PoE2 Output Filter Path",
                InitialDirectory = filterFolder
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
                new() { Name = _divineOrbName, Category = _currencyCategory, ChaosValue = 125.0, LastUpdated = DateTime.UtcNow },
                new() { Name = _exaltedOrbName, Category = _currencyCategory, ChaosValue = 15.0, LastUpdated = DateTime.UtcNow },
                new() { Name = _chaosOrbName, Category = _currencyCategory, ChaosValue = 1.0, LastUpdated = DateTime.UtcNow },
                new() { Name = _mirrorName, Category = _currencyCategory, ChaosValue = 100000.0, LastUpdated = DateTime.UtcNow },
                new() { Name = "Uncut Skill Gem (Level 19)", Category = "Gems", ChaosValue = 45.0, LastUpdated = DateTime.UtcNow }
            ];
            NormalizeMarketValues(_marketItems);
            AddPinnedExchangeRateItem();
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
            string loadedLogPath = string.Empty;
            string loadedFilterPath = string.Empty;
            string loadedBaseFilterPath = string.Empty;
            AppSettings? settings = null;

            if (File.Exists(settingsFile))
            {
                try
                {
                    string json = File.ReadAllText(settingsFile);
                    settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        loadedLogPath = settings.LogPath;
                        loadedFilterPath = settings.FilterOutputPath;
                        loadedBaseFilterPath = settings.SelectedBaseFilterPath;

                        _appSettings.HudWidth = settings.HudWidth;
                        _appSettings.HudHeight = settings.HudHeight;
                        _appSettings.HudXPercent = settings.HudXPercent;
                        _appSettings.HudYPercent = settings.HudYPercent;
                        _appSettings.EditModeOpacity = settings.EditModeOpacity;
                        _appSettings.HudModeOpacity = settings.HudModeOpacity;
                        _appSettings.IsHudVisible = settings.IsHudVisible;
                        _appSettings.ShowEconomyHighlights = settings.ShowEconomyHighlights;
                        if (!string.IsNullOrEmpty(settings.League))
                        {
                            _appSettings.League = settings.League;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
                }
            }

            // Verify paths and resolve from system environment / common folders if invalid
            string resolvedLogPath = ResolveClientLogPath(loadedLogPath);
            string resolvedFilterPath = ResolveFilterOutputPath(loadedFilterPath);

            _appSettings.LogPath = resolvedLogPath;
            _appSettings.FilterOutputPath = resolvedFilterPath;
            _selectedBaseFilterPath = loadedBaseFilterPath;
            _appSettings.SelectedBaseFilterPath = loadedBaseFilterPath;

            if (settings != null)
            {
                _appSettings.Tier1Threshold = settings.Tier1Threshold;
                _appSettings.Tier2Threshold = settings.Tier2Threshold;
            }
            else
            {
                _appSettings.Tier1Threshold = 1.0;
                _appSettings.Tier2Threshold = 1.0;
            }

            LogPathBox.Text = resolvedLogPath;
            FilterPathBox.Text = resolvedFilterPath;
            Tier1Box.Text = _appSettings.Tier1Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Tier2Box.Text = _appSettings.Tier2Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture);

            SyncSettingsToUi(_appSettings);

            if (resolvedLogPath != loadedLogPath || resolvedFilterPath != loadedFilterPath)
            {
                SaveSettings();
            }
        }

        private void SyncSettingsToUi(AppSettings settings)
        {
            if (EditOpacitySlider != null)
            {
                EditOpacitySlider.Value = settings.EditModeOpacity;
            }
            if (HudOpacitySlider != null)
            {
                HudOpacitySlider.Value = settings.HudModeOpacity;
            }

            if (HudVisibleCheckBox != null)
            {
                HudVisibleCheckBox.Checked -= HudVisibleCheckBox_Changed;
                HudVisibleCheckBox.Unchecked -= HudVisibleCheckBox_Changed;
                HudVisibleCheckBox.IsChecked = settings.IsHudVisible;
                HudVisibleCheckBox.Checked += HudVisibleCheckBox_Changed;
                HudVisibleCheckBox.Unchecked += HudVisibleCheckBox_Changed;
            }

            if (EconomyHighlightsCheckBox != null)
            {
                EconomyHighlightsCheckBox.Checked -= EconomyHighlightsCheckBox_Changed;
                EconomyHighlightsCheckBox.Unchecked -= EconomyHighlightsCheckBox_Changed;
                EconomyHighlightsCheckBox.IsChecked = settings.ShowEconomyHighlights;
                EconomyHighlightsCheckBox.Checked += EconomyHighlightsCheckBox_Changed;
                EconomyHighlightsCheckBox.Unchecked += EconomyHighlightsCheckBox_Changed;
            }
        }

        private static string ResolveClientLogPath(string? currentPath)
        {
            if (!string.IsNullOrEmpty(currentPath) && File.Exists(currentPath))
            {
                return currentPath;
            }

            string? standalonePath = TryGetStandaloneInstallPath();
            if (!string.IsNullOrEmpty(standalonePath))
            {
                return standalonePath;
            }

            string? steamPath = TryGetSteamInstallPath();
            if (!string.IsNullOrEmpty(steamPath))
            {
                return steamPath;
            }

            string? commonPath = TryGetCommonInstallPath();
            if (!string.IsNullOrEmpty(commonPath))
            {
                return commonPath;
            }

            string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(myDocuments, _myGamesFolder, _poe2Folder, "logs", _clientLogFile);
        }

        private static string? TryGetStandaloneInstallPath()
        {
            string? path = TryReadRegistryPath(@"Software\Grinding Gear Games\Path of Exile 2");
            if (string.IsNullOrEmpty(path))
            {
                path = TryReadRegistryPath(@"Software\Grinding Gear Games\Path of Exile");
            }
            return path;
        }

        private static string? TryReadRegistryPath(string subKeyPath)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKeyPath);
                if (key != null)
                {
                    string? installLoc = key.GetValue("InstallLocation") as string ?? key.GetValue("Path") as string;
                    if (!string.IsNullOrEmpty(installLoc))
                    {
                        string path = Path.Combine(installLoc, "logs", _clientLogFile);
                        if (File.Exists(path)) return path;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading registry {subKeyPath}: {ex.Message}");
            }
            return null;
        }

        private static string? TryGetSteamInstallPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key != null)
                {
                    string? steamPath = key.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(steamPath))
                    {
                        string path = Path.Combine(steamPath, _steamappsFolder, _commonFolder, _poe2Folder, "logs", _clientLogFile);
                        if (File.Exists(path)) return path;

                        string libraryConfig = Path.Combine(steamPath, _steamappsFolder, "libraryfolders.vdf");
                        if (File.Exists(libraryConfig))
                        {
                            string? altPath = ParseSteamLibraryFolders(libraryConfig);
                            if (!string.IsNullOrEmpty(altPath)) return altPath;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading Steam registry: {ex.Message}");
            }
            return null;
        }

        private static string? ParseSteamLibraryFolders(string libraryConfigPath)
        {
            try
            {
                var lines = File.ReadAllLines(libraryConfigPath);
                var pathLines = lines.Where(line => line.Contains("\"path\"", StringComparison.Ordinal));
                foreach (var line in pathLines)
                {
                    var parts = line.Split('\"', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4)
                    {
                        string libPath = parts[3].Replace(@"\\", @"\", StringComparison.Ordinal);
                        string altPath = Path.Combine(libPath, _steamappsFolder, _commonFolder, _poe2Folder, "logs", _clientLogFile);
                        if (File.Exists(altPath)) return altPath;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing Steam library folders: {ex.Message}");
            }
            return null;
        }

        private static string? TryGetCommonInstallPath()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] commonPaths = [
                Path.Combine(programFiles, "Grinding Gear Games", _poe2Folder, "logs", _clientLogFile),
                Path.Combine(programFiles, "Steam", _steamappsFolder, _commonFolder, _poe2Folder, "logs", _clientLogFile),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", _steamappsFolder, _commonFolder, _poe2Folder, "logs", _clientLogFile)
            ];

            return commonPaths.FirstOrDefault(File.Exists);
        }

        private static string ResolveFilterOutputPath(string? currentPath)
        {
            if (!string.IsNullOrEmpty(currentPath))
            {
                try
                {
                    string? dir = Path.GetDirectoryName(currentPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        return currentPath;
                    }
                }
                catch
                {
                    // Ignore invalid format
                }
            }

            string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), _myGamesFolder, _poe2Folder);
            try
            {
                if (!Directory.Exists(defaultDir))
                {
                    Directory.CreateDirectory(defaultDir);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create PoE2 My Games directory: {ex.Message}");
            }

            return Path.Combine(defaultDir, "LootPulse_Only.filter");
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

                _appSettings.LogPath = LogPathBox.Text;
                _appSettings.FilterOutputPath = FilterPathBox.Text;
                _appSettings.SelectedBaseFilterPath = _selectedBaseFilterPath ?? string.Empty;
                _appSettings.Tier1Threshold = t1 > 0 ? t1 : 1.0;
                _appSettings.Tier2Threshold = t2 > 0 ? t2 : 1.0;

                string json = JsonSerializer.Serialize(_appSettings, _jsonOptions);
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

        private void EconomyHighlightsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_appSettings == null) return;

            _appSettings.ShowEconomyHighlights = EconomyHighlightsCheckBox.IsChecked == true;
            TriggerFilterRegeneration();
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

        private void InitializeTrayIcon()
        {
            try
            {
                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Text = "LootPulse Overlay",
                    Visible = true
                };

                // Extract icon from the running executable binary to guarantee display
                try
                {
                    string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to extract process icon: {ex.Message}");
                }

                _notifyIcon.Icon ??= System.Drawing.SystemIcons.Application;

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

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
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

        private static void NormalizeMarketValues(List<MarketItem> items)
        {
            if (items == null) return;

            double divinePriceInChaos = 120.0;
            double exaltedPriceInChaos = 15.0;

            var divOrb = items.Find(i => i.Name == _divineOrbName && i.Category == _currencyCategory);
            if (divOrb?.ChaosValue > 0)
            {
                divinePriceInChaos = divOrb.ChaosValue;
            }

            var exOrb = items.Find(i => i.Name == _exaltedOrbName && i.Category == _currencyCategory);
            if (exOrb?.ChaosValue > 0)
            {
                exaltedPriceInChaos = exOrb.ChaosValue;
            }

            foreach (var item in items)
            {
                if (item.DivineValue <= 0 && item.ChaosValue > 0)
                {
                    item.DivineValue = item.ChaosValue / divinePriceInChaos;
                }
                if (item.ExaltedValue <= 0 && item.ChaosValue > 0)
                {
                    item.ExaltedValue = item.ChaosValue / exaltedPriceInChaos;
                }

                if (item.ChaosValue <= 0 && item.DivineValue > 0)
                {
                    item.ChaosValue = item.DivineValue * divinePriceInChaos;
                }
                if (item.ExaltedValue <= 0 && item.DivineValue > 0)
                {
                    item.ExaltedValue = item.DivineValue * (divinePriceInChaos / exaltedPriceInChaos);
                }
            }
        }

        private void AddPinnedExchangeRateItem()
        {
            _marketItems.RemoveAll(i => i.Category == "Exchange Rate");

            double divinePriceInChaos = 120.0;
            double exaltedPriceInChaos = 15.0;

            var divOrb = _marketItems.Find(i => i.Name == _divineOrbName && i.Category == _currencyCategory);
            if (divOrb?.ChaosValue > 0)
            {
                divinePriceInChaos = divOrb.ChaosValue;
            }

            var exOrb = _marketItems.Find(i => i.Name == _exaltedOrbName && i.Category == _currencyCategory);
            if (exOrb?.ChaosValue > 0)
            {
                exaltedPriceInChaos = exOrb.ChaosValue;
            }

            double rate = divinePriceInChaos / exaltedPriceInChaos;
            var exchangeRateItem = new MarketItem
            {
                Name = _divineOrbName,
                Category = "Exchange Rate",
                ExaltedValue = rate,
                LastUpdated = DateTime.UtcNow
            };
            _marketItems.Insert(0, exchangeRateItem);
        }

        private async Task LoadBuildUniquePricesAsync(PoeBuild build)
        {
            if (build?.InventorySlots == null) return;

            string activeLeague = _appSettings.League;
            if (string.IsNullOrEmpty(activeLeague))
            {
                activeLeague = "Runes of Aldur";
            }
            var categoriesToFetch = new HashSet<(string type, string name)>();

            var missingUniques = build.InventorySlots
                .Where(slot => !string.IsNullOrEmpty(slot.UniqueName) &&
                               !_marketItems.Exists(i => i.Name.Equals(slot.UniqueName, StringComparison.OrdinalIgnoreCase)));

            foreach (var slot in missingUniques)
            {
                categoriesToFetch.Add(MapInventoryIdToUniqueCategory(slot.InventoryId));
            }

            if (categoriesToFetch.Count == 0) return;

            StatusText.Text = "Fetching required build unique item prices from poe.ninja...";
            bool addedAny = false;

            foreach (var (type, name) in categoriesToFetch)
            {
                try
                {
                    var items = await _ninjaClient.FetchItemPricesAsync(activeLeague, type, name).ConfigureAwait(true);
                    if (items?.Count > 0)
                    {
                        var newItems = items.Where(item => !_marketItems.Exists(m => m.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                        if (newItems.Count > 0)
                        {
                            _marketItems.AddRange(newItems);
                            addedAny = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to fetch build uniques for category {type}: {ex.Message}");
                }
            }

            if (addedAny)
            {
                NormalizeMarketValues(_marketItems);
                AddPinnedExchangeRateItem();
                ItemListView.ItemsSource = null;
                ItemListView.ItemsSource = _marketItems;
                StatusText.Text = $"Loaded build: {build.Name} (fetched missing unique prices)";
            }
        }

        private static (string type, string name) MapInventoryIdToUniqueCategory(string inventoryId)
        {
            if (string.IsNullOrEmpty(inventoryId))
                return ("UniqueArmour", "Unique Armour");

            if (inventoryId.Contains("weapon", StringComparison.OrdinalIgnoreCase) ||
                inventoryId.Contains("offhand", StringComparison.OrdinalIgnoreCase) ||
                inventoryId.Contains("shield", StringComparison.OrdinalIgnoreCase))
            {
                return ("UniqueWeapon", "Unique Weapons");
            }
            if (inventoryId.Contains("body", StringComparison.OrdinalIgnoreCase) ||
                inventoryId.Contains("chest", StringComparison.OrdinalIgnoreCase) ||
                inventoryId.Contains("helmet", StringComparison.OrdinalIgnoreCase) ||
                inventoryId.Contains("boots", StringComparison.OrdinalIgnoreCase) ||
                inventoryId.Contains("gloves", StringComparison.OrdinalIgnoreCase) ||
                inventoryId.Contains("glove", StringComparison.OrdinalIgnoreCase))
            {
                return ("UniqueArmour", "Unique Armour");
            }
            if (inventoryId.Contains("ring", StringComparison.OrdinalIgnoreCase) ||
                inventoryId.Contains("amulet", StringComparison.OrdinalIgnoreCase) ||
                inventoryId.Contains("belt", StringComparison.OrdinalIgnoreCase))
            {
                return ("UniqueAccessory", "Unique Accessories");
            }
            if (inventoryId.Contains("flask", StringComparison.OrdinalIgnoreCase))
            {
                return ("UniqueFlask", "Unique Flasks");
            }

            return ("UniqueJewel", "Unique Jewels");
        }
    }

    public class FilterOption
    {
        public string DisplayName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public bool IsSubscribed { get; set; }
    }
}
