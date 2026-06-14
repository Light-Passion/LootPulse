using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using LootPulse.Models;
using LootPulse.Services;

namespace LootPulse
{
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
        private List<MarketItem> _marketItems = new();
        private PlayerState _playerState = new();
        private bool _isClickThroughEnabled = false;

        // Win32 Interop Constants
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int HOTKEY_ID = 9000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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

            // Load default paths
            string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            LogPathBox.Text = Path.Combine(myDocuments, @"My Games\Path of Exile 2\logs\Client.txt");
            FilterPathBox.Text = Path.Combine(myDocuments, @"My Games\Path of Exile 2\MyMarketFilter.filter");

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

            // Register Hotkey: Ctrl + Shift + O
            // Modifiers: Ctrl (0x0002) | Shift (0x0004) = 0x0006
            // Key: 'O' = 0x4F (79)
            RegisterHotKey(_hwnd, HOTKEY_ID, 0x0006, 0x4F);

            // Initialize monitoring if log file exists
            if (File.Exists(LogPathBox.Text))
            {
                _logMonitor.StartMonitoring(LogPathBox.Text);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _logMonitor.StopMonitoring();
            _hwndSource?.RemoveHook(HwndHook);
            UnregisterHotKey(_hwnd, HOTKEY_ID);
            base.OnClosed(e);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                ToggleOverlayMode();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void ToggleOverlayMode()
        {
            _isClickThroughEnabled = !_isClickThroughEnabled;

            if (_isClickThroughEnabled)
            {
                // Enter Overlay Mode (Transparent, click-through, HUD mode)
                OverlayModeText.Text = "HUD MODE";
                OverlayModeText.Foreground = new SolidColorBrush(Color.FromRgb(229, 181, 96)); // Gold
                MainWindowBorder.Background = new SolidColorBrush(Color.FromArgb(50, 24, 24, 24)); // Extremely transparent
                MainWindowBorder.BorderBrush = Brushes.Transparent;
                MainContentGrid.Visibility = Visibility.Collapsed; // Hide control panel
                
                // P/Invoke click-through
                int extendedStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
                SetWindowLong(_hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
                
                StatusText.Text = "HUD Click-Through Active. Press Ctrl+Shift+O to edit.";
            }
            else
            {
                // Enter Interactive Edit Mode (opaque, handles inputs)
                OverlayModeText.Text = "EDIT MODE";
                OverlayModeText.Foreground = new SolidColorBrush(Color.FromRgb(255, 97, 36)); // Orange
                MainWindowBorder.Background = new SolidColorBrush(Color.FromArgb(242, 18, 18, 18)); // Solid Slate
                MainWindowBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(61, 61, 61));
                MainContentGrid.Visibility = Visibility.Visible;
                
                // Remove click-through style
                int extendedStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
                SetWindowLong(_hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);
                
                StatusText.Text = "Interactive Edit Mode active.";
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
                
                // Dynamic regeneration based on zone transition
                TriggerFilterRegeneration();
            });
        }

        private void TriggerFilterRegeneration()
        {
            double.TryParse(Tier1Box.Text, out double t1);
            double.TryParse(Tier2Box.Text, out double t2);

            bool success = _filterBuilder.GenerateFilterFile(
                FilterPathBox.Text,
                null,
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
                StatusText.Text = $"Filter updated! Remember to reload in PoE2 Settings (Options -> Game -> Item Filter -> Reload).";
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
            string activeLeague = "Mirage";
            
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
            }
        }

        private void LoadMockItems()
        {
            _marketItems = new List<MarketItem>
            {
                new MarketItem { Name = "Divine Orb", Category = "Currency", ChaosValue = 125.0, LastUpdated = DateTime.UtcNow },
                new MarketItem { Name = "Exalted Orb", Category = "Currency", ChaosValue = 15.0, LastUpdated = DateTime.UtcNow },
                new MarketItem { Name = "Chaos Orb", Category = "Currency", ChaosValue = 1.0, LastUpdated = DateTime.UtcNow },
                new MarketItem { Name = "Uncut Skill Gem (Level 19)", Category = "Gems", ChaosValue = 45.0, LastUpdated = DateTime.UtcNow }
            };
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
                string json = JsonSerializer.Serialize(ActiveTheme, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(themePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save active theme: {ex.Message}");
            }
        }

        private void CustomizeStyles_Click(object sender, RoutedEventArgs e)
        {
            var editor = new StyleEditorWindow(ActiveTheme);
            editor.Owner = this;
            if (editor.ShowDialog() == true)
            {
                ActiveTheme = editor.WorkingTheme;
                SaveActiveTheme();
                TriggerFilterRegeneration();
            }
        }
    }
}