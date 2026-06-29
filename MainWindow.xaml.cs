using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using LootPulse.Models;
using LootPulse.Services;
using LootPulse.Services.Trade;
using LootPulse.Controls;

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
        private readonly MetadataUpdateService _metadataService;
        private readonly PriceHistoryService _priceHistory;

        // Trade Market (PoE2 trade2 API) services
        private readonly WebView2TradeTransport _tradeTransport;
        private readonly TradeRateLimiter _tradeRateLimiter;
        private readonly Poe2TradeClient _tradeClient;
        private readonly List<TradeItemGroup> _tradeGroups = [];
        private bool _isTradeSearchRunning;

        // Active State Data
        private PoeBuild? _activeBuild;
        private List<TradeItemQuery> _tradeQueries = [];

        public static List<ImportanceOption> ImportanceOptions { get; } =
        [
            new() { Name = "Required (AND)", Value = AffixImportance.Required },
            new() { Name = "Very Important (1000)", Value = AffixImportance.VeryImportant },
            new() { Name = "Important (100)", Value = AffixImportance.Important },
            new() { Name = "Wanted (10)", Value = AffixImportance.Wanted }
        ];

        public FilterTheme ActiveTheme { get; set; } = new();
        private List<MarketItem> _marketItems = [];
        private readonly HashSet<string> _marketItemNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly PlayerState _playerState = new();
        private bool _isClickThroughEnabled;
        private string? _selectedBaseFilterPath;
        private string? _selectedBaseFilterDisplayName;
        private FileSystemWatcher? _baseFilterWatcher;
        private bool _isBaseFilterMissingOnStartup;
        private bool _isUiInitialized;

        // Win32 Interop Constants
        private const int _hotkeyId = 9000;
        private const int _hotkeyHudId = 9001;
        private const string _currencyCategory = "Currency";
        private const string _divineOrbName = "Divine Orb";
        private const string _exaltedOrbName = "Exalted Orb";
        private const string _chaosOrbName = "Chaos Orb";
        private const string _mirrorName = "Mirror of Kalandra";
        private const string _exchangeRateCategory = "Exchange Rate";

        private const string _myGamesFolder = "My Games";
        private const string _poe2Folder = "Path of Exile 2";
        private const string _clientLogFile = "Client.txt";
        private const string _steamappsFolder = "steamapps";
        private const string _commonFolder = "common";
        private const string _darkMenuItemStyleKey = "LootMenuItem";
        private const string _darkContextMenuStyleKey = "LootContextMenu";

        private const string _classMercenary = "Mercenary";
        private const string _classMonk = "Monk";
        private const string _classWitch = "Witch";
        private const string _classSorceress = "Sorceress";
        private const string _classDruid = "Druid";
        private const string _classRanger = "Ranger";
        private const string _classHuntress = "Huntress";
        private const string _classWarrior = "Warrior";

        private HudWindow? _hudWindow;
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
        private readonly TaskCompletionSource<bool> _sourceInitializedTcs = new();

        // EKG Market Pulse Graph state
        private DispatcherTimer? _ekgTimer;
        private double _ekgScrollX;
        private double _ekgSegmentWidth = 50;
        private double _ekgAmplitude = 6;
        private readonly Random _ekgRng = new();

        public MainWindow()
        {
            InitializeComponent();

            // Show the actual assembly version in the status bar (sourced from csproj <Version>).
            var asmVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (asmVersion != null)
                VersionText.Text = $"LootPulse Overlay v{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}";

            // Initialize Services
            _ninjaClient = new PoeNinjaClient();
            _buildParser = new BuildProfileParser();
            _logMonitor = new ClientLogMonitor
            {
                ActiveBuildClassSynonymsProvider = GetActiveBuildClassSynonyms
            };
            _metadataService = new MetadataUpdateService();
            _priceHistory = new PriceHistoryService();
            _filterBuilder = new FilterBuilder();

            // Load dynamic base items and currencies data
            var baseConfig = _metadataService.LoadBaseItemsConfig();
            FilterBuilder.Initialize(baseConfig);

            // Trade Market services (WebView2 session-backed; init deferred until first Connect)
            _tradeTransport = new WebView2TradeTransport(this);
            _tradeRateLimiter = new TradeRateLimiter();
            _tradeClient = new Poe2TradeClient(_tradeTransport, _tradeRateLimiter);
            _tradeTransport.ConnectionChanged += TradeTransport_ConnectionChanged;

            // Search button is disabled until trade session is connected.
            // Without a session, the stat resolver can't fetch GGG's stat table,
            // so Best-in-Slot searches would silently drop all affix weights.
            SearchTradeButton.IsEnabled = false;

            // Bind log monitor events
            _logMonitor.ZoneChanged += LogMonitor_ZoneChanged;
            _logMonitor.PlayerLevelChanged += LogMonitor_PlayerLevelChanged;

            // Initialize application asynchronously
            _ = InitializeAppAsync();

            // Start the EKG heartbeat animation in the header
            Loaded += (s, e) =>
            {
                StartEkgAnimation();
                // Start the heartbeat pulse on the status dot
                var heartbeat = (Storyboard)Resources["HeartbeatPulse"];
                heartbeat.Begin();
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Set up Win32 handles and hook messages for Global Hotkeys
            var helper = new WindowInteropHelper(this);
            _hwnd = helper.Handle;
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource.AddHook(HwndHook);

            // Enable DWM Acrylic backdrop for "floating obsidian panel" effect
            EnableAcrylicBackdrop(_hwnd);

            // Register Hotkeys
            RegisterHotKey(_hwnd, _hotkeyId, 0x0006, 0x4F); // Ctrl + Shift + O
            RegisterHotKey(_hwnd, _hotkeyHudId, 0x0006, 0x48); // Ctrl + Shift + H

            // Signal that the window handle is ready
            _sourceInitializedTcs.SetResult(true);
        }

        // ── DWM System Backdrop Interop ────────────────────────────────────
        // NOTE: The system backdrop (Mica/Acrylic) is intentionally DISABLED
        // on the main dashboard window. Mica/Acrylic is painted by DWM behind
        // the window and fills every transparent pixel with a system-tinted
        // blur. That tint (often light/white on a bright wallpaper or light
        // system theme) bleeds through as the Dashboard Opacity slider fades
        // the MainWindowBorder, making the dashboard turn white instead of
        // see-through. Setting DWMSBT_NONE (0) guarantees that transparent
        // regions show the actual desktop/game behind the window, so the
        // opacity slider produces true transparency as documented.
        // The dark "#E6121212" panel background already carries the "floating
        // obsidian panel" aesthetic at full opacity without needing Mica.

        [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMSBT_NONE = 0;            // No system backdrop (true transparency)
        private const int DWMSBT_TRANSIENTWINDOW = 1; // Acrylic
        private const int DWMSBT_MAINWINDOW = 2;      // Mica

        private void EnableAcrylicBackdrop(IntPtr hwnd)
        {
            try
            {
                // Explicitly disable the DWM system backdrop so that transparent
                // regions of the borderless window reveal the desktop/game rather
                // than a Mica/Acrylic tint. This is what lets the Dashboard Opacity
                // slider fade the dashboard to genuinely see-through instead of white.
                int backdropType = DWMSBT_NONE;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            }
            catch
            {
                // Silently ignore — the window falls back to its WPF Background="Transparent".
            }
        }

        /// <summary>
        /// Starts the EKG heartbeat animation in the header bar.
        /// Generates EKG-shaped path geometry and scrolls it continuously.
        /// </summary>
        private void StartEkgAnimation()
        {
            GenerateEkgWave();
            _ekgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30fps
            _ekgTimer.Tick += OnEkgTick;
            _ekgTimer.Start();
        }

        private void OnEkgTick(object? sender, EventArgs e)
        {
            _ekgScrollX += 50.0 / 30.0; // 50px/sec scroll speed
            if (_ekgScrollX >= _ekgSegmentWidth)
            {
                _ekgScrollX -= _ekgSegmentWidth;
                GenerateEkgWave();
            }
            WaveTranslate.X = -_ekgScrollX;
            GlowTranslate.X = -_ekgScrollX;
        }

        private void GenerateEkgWave()
        {
            const double width = 200;
            const double height = 24;
            var centerY = height / 2;
            var totalWidth = width + _ekgSegmentWidth * 4;

            var figure = new PathFigure
            {
                StartPoint = new Point(0, centerY),
                IsClosed = false
            };

            var x = 0.0;
            while (x < totalWidth)
            {
                var amp = _ekgAmplitude;

                // Flat baseline (30%)
                var w = _ekgSegmentWidth * 0.3;
                figure.Segments.Add(new LineSegment(new Point(x + w, centerY), true));
                x += w;

                // Q wave — downward dip (5%)
                w = _ekgSegmentWidth * 0.05;
                figure.Segments.Add(new LineSegment(new Point(x + w, centerY + amp * 0.4), true));
                x += w;

                // R wave — sharp upward spike (10%)
                w = _ekgSegmentWidth * 0.1;
                figure.Segments.Add(new LineSegment(new Point(x + w * 0.5, centerY - amp), true));
                x += w;

                // S wave — sharp downward (10%)
                w = _ekgSegmentWidth * 0.1;
                figure.Segments.Add(new LineSegment(new Point(x + w * 0.5, centerY + amp * 0.6), true));
                x += w;

                // Return to baseline (10%)
                w = _ekgSegmentWidth * 0.1;
                figure.Segments.Add(new LineSegment(new Point(x + w, centerY), true));
                x += w;

                // T wave — gentle bump (15%)
                w = _ekgSegmentWidth * 0.15;
                figure.Segments.Add(new LineSegment(new Point(x + w * 0.5, centerY - amp * 0.3), true));
                x += w;

                // Flat to end (20%)
                w = _ekgSegmentWidth * 0.2;
                figure.Segments.Add(new LineSegment(new Point(x + w, centerY), true));
                x += w;
            }

            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            geo.Freeze();

            EkgPath.Data = geo;
            EkgGlowPath.Data = geo;
        }

        /// <summary>
        /// Toggles the EKG heartbeat between Active (fast, tall spikes) and Calm (slow, gentle).
        /// Called when market data sync starts (active=true) and completes (active=false).
        /// </summary>
        private void SetEkgState(bool active)
        {
            _ekgAmplitude = active ? 12 : 6;
            _ekgSegmentWidth = active ? 30 : 50;
            GenerateEkgWave();
        }

        /// <summary>
        /// Triggers the Data Refresh Pulse — a PoeGold EKG sweep across all cards.
        /// A gradient band travels left-to-right over 600ms, simulating a heartbeat monitor sweep.
        /// </summary>
        private void TriggerDataRefreshSweep()
        {
            // Sweep across each card with a slight stagger for a cascading effect
            var sweepTargets = new (Border sweepBorder, TranslateTransform transform, double width, double delay)[]
            {
                (ActiveStateSweep, ActiveStateSweepTransform, 320, 0),
                (BuildConfigSweep, BuildConfigSweepTransform, 520, 100),
                (EconomySweep, EconomySweepTransform, 820, 200),
            };

            foreach (var (sweepBorder, transform, width, delay) in sweepTargets)
            {
                var sb = new Storyboard();

                // Reset position
                transform.X = -width;

                // Fade in
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100))
                {
                    BeginTime = TimeSpan.FromMilliseconds(delay)
                };
                Storyboard.SetTarget(fadeIn, sweepBorder);
                Storyboard.SetTargetProperty(fadeIn, new PropertyPath("Opacity"));
                sb.Children.Add(fadeIn);

                // Sweep left-to-right
                var sweep = new DoubleAnimation(-width, width, TimeSpan.FromMilliseconds(600))
                {
                    BeginTime = TimeSpan.FromMilliseconds(delay),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(sweep, transform);
                Storyboard.SetTargetProperty(sweep, new PropertyPath("(TranslateTransform.X)"));
                sb.Children.Add(sweep);

                // Fade out
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
                {
                    BeginTime = TimeSpan.FromMilliseconds(delay + 500)
                };
                Storyboard.SetTarget(fadeOut, sweepBorder);
                Storyboard.SetTargetProperty(fadeOut, new PropertyPath("Opacity"));
                sb.Children.Add(fadeOut);

                sb.Begin();
            }
        }

        /// <summary>
        /// Shows a toast notification in the bottom-right corner.
        /// Auto-dismisses after 4 seconds.
        /// </summary>
        public void ShowToast(string title, string message, ToastType type = ToastType.Info)
        {
            Dispatcher.Invoke(() =>
            {
                var toast = new ToastNotification(title, message, type);
                toast.Dismissed += (s, e) =>
                {
                    Dispatcher.Invoke(() => ToastContainer.Children.Remove(toast));
                };
                ToastContainer.Children.Add(toast);

                // Limit to max 4 toasts — remove oldest if exceeded
                while (ToastContainer.Children.Count > 4)
                {
                    if (ToastContainer.Children[0] is ToastNotification oldest)
                        oldest.Dismiss();
                    else
                        ToastContainer.Children.RemoveAt(0);
                }
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _ekgTimer?.Stop();
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

            _tradeTransport.Dispose();
            _tradeRateLimiter.Dispose();

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
                    _ = ToggleHudVisibilityAsync();
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

                _hudWindow?.SetClickThrough(false, _appSettings.HudModeOpacity);
                if (_appSettings.IsHudVisible)
                    _hudWindow?.Show();
                else
                    _hudWindow?.Hide();
            }
        }

        private async void LogMonitor_ZoneChanged(object? sender, ZoneChangedEventArgs e)
        {
            // Invoke on the UI thread to update controls
            await Dispatcher.InvokeAsync(async () =>
            {
                _playerState.CurrentZone = e.ZoneName;
                _playerState.ZoneLevel = e.ZoneLevel;
                CharZoneText.Text = $"{e.ZoneName} (Level {e.ZoneLevel})";
                StatusText.Text = $"Entered zone: {e.ZoneName} (Level {e.ZoneLevel}). Regenerating filter...";

                // Update HUD display
                _hudWindow?.UpdateDisplay(_playerState.CharacterName, _playerState.Level, e.ZoneName, e.ZoneLevel, "Zone Changed");

                // Dynamic regeneration based on zone transition
                await TriggerFilterRegenerationAsync();
            });
        }

        private async void LogMonitor_PlayerLevelChanged(object? sender, PlayerLevelChangedEventArgs e)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                _playerState.CharacterName = e.CharacterName;
                CharNameText.Text = e.CharacterName;

                // Level may be unknown (0) when only the active character name was recovered.
                if (e.Level > 0)
                {
                    _playerState.Level = e.Level;
                    CharLevelText.Text = e.Level.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    StatusText.Text = $"{e.CharacterName} is now level {e.Level}";
                }
                else
                {
                    StatusText.Text = $"Active character: {e.CharacterName}";
                }

                // Update HUD display
                _hudWindow?.UpdateDisplay(e.CharacterName, _playerState.Level, _playerState.CurrentZone, _playerState.ZoneLevel, "Level Up!");

                // Regenerate filter with updated level
                await TriggerFilterRegenerationAsync();
            });
        }

        private async Task TriggerFilterRegenerationAsync()
        {
            _ = double.TryParse(Tier1Box.Text, System.Globalization.CultureInfo.InvariantCulture, out double t1);
            _ = double.TryParse(Tier2Box.Text, System.Globalization.CultureInfo.InvariantCulture, out double t2);

            bool success = await _filterBuilder.GenerateFilterFileAsync(
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
                await SaveSettingsAsync();
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

        private void LoadBuildButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void ToggleSettings_Click(object sender, RoutedEventArgs e)
        {
            bool showSettings = SettingsView.Visibility != Visibility.Visible;
            SettingsView.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
            DashboardView.Visibility = showSettings ? Visibility.Collapsed : Visibility.Visible;
            OverlayModeText.Text = showSettings ? "SETTINGS" : "EDIT MODE";
            SettingsToggleButton.Content = showSettings ? "← Dashboard" : "⚙ Settings";
        }

        private async void SyncAll_Click(object sender, RoutedEventArgs e)
        {
            await SyncEconomyDataAsync();
        }

        private async Task SyncEconomyDataAsync()
        {
            // Spike the EKG heartbeat to show active market sync
            SetEkgState(true);
            StatusText.Text = "Syncing with poe.ninja...";
            string activeLeague = _appSettings.League;
            if (string.IsNullOrEmpty(activeLeague))
            {
                activeLeague = "Runes of Aldur";
            }

            try
            {
                var currencyTask = _ninjaClient.FetchCurrencyPricesAsync(activeLeague);

                var categoryTasks = new List<(string Type, string Name, Task<List<MarketItem>> Task)>();
                var categories = _metadataService.LoadEconomyCategories();

                foreach (var cat in categories)
                {
                    categoryTasks.Add((cat.Type, cat.Name, _ninjaClient.FetchItemPricesAsync(activeLeague, cat.Type, cat.Name)));
                }

                var allTasks = new List<Task> { currencyTask };
                foreach (var ct in categoryTasks)
                {
                    allTasks.Add(ct.Task);
                }

                try
                {
                    await Task.WhenAll(allTasks);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"One or more poe.ninja category sync tasks failed: {ex.Message}");
                }

                var allItems = new List<MarketItem>();
                try
                {
                    var currencyItems = await currencyTask;
                    if (currencyItems != null)
                    {
                        allItems.AddRange(currencyItems);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to fetch Currency category: {ex.Message}");
                }

                foreach (var ct in categoryTasks)
                {
                    try
                    {
                        var items = await ct.Task;
                        if (items != null)
                        {
                            allItems.AddRange(items);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to fetch category {ct.Type}: {ex.Message}");
                    }
                }

                _marketItems.Clear();
                _marketItems.AddRange(allItems);
                
                // Sync IsHudSelected flags from saved settings
                foreach (var item in _marketItems)
                {
                    item.IsHudSelected = (_appSettings.HudCurrencies ?? new()).Contains(item.Name);
                }
                
                _marketItemNames.Clear();
                foreach (var item in allItems)
                {
                    _marketItemNames.Add(item.Name);
                }

                NormalizeMarketValues(_marketItems);
                AddPinnedExchangeRateItem();
                UpdateCategoryDropdown();
                ApplyCategoryFilter();

                await TriggerFilterRegenerationAsync();

                // Record daily price snapshot for sparkline history
                _priceHistory.RecordSnapshot(
                    _marketItems.Select(i => (i.Name, i.ExaltedValue > 0 ? i.ExaltedValue : i.DivineValue)));

                StatusText.Text = "Sync complete. All economy categories updated.";
                SetEkgState(false);

                // Update HUD currency ticker with user-selected currencies
                UpdateHudCurrencyTicker();

                TriggerDataRefreshSweep();
                ShowToast("Economy Synced", "All market categories updated successfully.", ToastType.Success);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Sync failed: {ex.Message}";
                SetEkgState(false);
                ShowToast("Sync Failed", ex.Message, ToastType.Danger);
                Debug.WriteLine($"Error during parallel economy sync: {ex.Message}");
            }
        }

        // ---- Trade Market tab (PoE2 trade2 API) ----

        private bool _tradeConnectionChecked;

        // First time the Trade Market tab is opened, silently check whether the persisted session is
        // still valid so the Connect button can be greyed out when no sign-in is needed.
        private async void CommoditiesTab_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Ignore SelectionChanged bubbling up from inner controls (combo boxes, etc.).
            if (e.OriginalSource is not System.Windows.Controls.TabControl tc) return;
            if (_tradeConnectionChecked || !ReferenceEquals(tc.SelectedItem, TradeMarketTab)) return;

            _tradeConnectionChecked = true;
            try
            {
                await _tradeTransport.RefreshConnectionAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Trade connection check failed: {ex.Message}");
            }
        }

        // Show the per-item budget input only in Best-in-slot mode, and min matched affixes only in Cheapest mode.
        private async void TradeMode_Changed(object sender, RoutedEventArgs e)
        {
            if (BudgetPanel != null && BestInSlotModeRadio != null)
            {
                BudgetPanel.Visibility = BestInSlotModeRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            }
            if (MinMatchedPanel != null && BestInSlotModeRadio != null)
            {
                MinMatchedPanel.Visibility = BestInSlotModeRadio.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            }
            if (AffixWeightsTab != null && BestInSlotModeRadio != null)
            {
                AffixWeightsTab.Visibility = BestInSlotModeRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void TradeTransport_ConnectionChanged(object? sender, TradeConnectionChangedEventArgs e)
        {
            bool connected = e.IsConnected;
            await Dispatcher.InvokeAsync(async () =>
            {
                ConnectTradeButton.IsEnabled = !connected;
                ConnectTradeButton.Content = connected ? "Trade Account Connected" : "Connect Trade Account";
                SearchTradeButton.IsEnabled = connected;
                if (!connected)
                {
                    TradeStatusText.Text = "Trade session expired — click \"Connect Trade Account\" to re-authenticate before searching.";
                }
            });
        }

        private async void ConnectTradeAccount_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TradeStatusText.Text = "Checking Path of Exile session…";
                await _tradeTransport.ConnectAsync();
                SearchTradeButton.IsEnabled = _tradeTransport.IsConnected;
                TradeStatusText.Text = _tradeTransport.IsConnected
                    ? "Connected. Click \"Search Trade Market\" to price your build items."
                    : "Sign in to Path of Exile in the window, then run a search.";
            }
            catch (Exception ex)
            {
                TradeStatusText.Text = $"Could not open trade login: {ex.Message}";
            }
        }

        private async void SearchTradeMarket_Click(object sender, RoutedEventArgs e)
        {
            if (_isTradeSearchRunning)
            {
                return;
            }

            if (!_tradeTransport.IsConnected)
            {
                TradeStatusText.Text = "Not connected to trade — click \"Connect Trade Account\" first. Affix weights and stat filters require an authenticated session.";
                return;
            }

            if (_activeBuild == null || _activeBuild.InventorySlots.Count == 0)
            {
                TradeStatusText.Text = "Load a .build first — no items to search.";
                return;
            }

            var queries = _tradeQueries;
            if (queries.Count == 0)
            {
                TradeStatusText.Text = "No gear with a base type or unique name found in the build.";
                return;
            }

            string league = string.IsNullOrEmpty(_appSettings.League) ? "Runes of Aldur" : _appSettings.League;
            int maxLevel = Math.Max(_playerState.Level, 1);
            CurrencyRates rates = BuildCurrencyRates();
            int minAffixMatches = ReadMinAffixMatches();
            bool bestInSlot = BestInSlotModeRadio?.IsChecked == true;
            TradeSearchMode mode = bestInSlot ? TradeSearchMode.BestInSlot : TradeSearchMode.Cheapest;
            double? budgetDivine = bestInSlot ? ReadBudgetDivine() : null;

            _isTradeSearchRunning = true;
            SearchTradeButton.IsEnabled = false;
            SetEkgState(true);
            _tradeGroups.Clear();
            RefreshTradeGroups();

            try
            {
                for (int i = 0; i < queries.Count; i++)
                {
                    var options = new TradeSearchOptions(mode, minAffixMatches, budgetDivine);
                    TradeItemGroup group = await _tradeClient.SearchAsync(
                        league, queries[i], maxLevel, rates, options);
                    _tradeGroups.Add(group);
                    RefreshTradeGroups();
                }
                TradeStatusText.Text = bestInSlot
                    ? $"Done — best-in-slot for {queries.Count} item(s)."
                    : $"Done — cheapest for {queries.Count} item(s).";
            }
            catch (Exception ex)
            {
                TradeStatusText.Text = $"Trade search stopped: {ex.Message}";
                SetEkgState(false);
            }
            finally
            {
                _isTradeSearchRunning = false;
                SearchTradeButton.IsEnabled = _tradeTransport.IsConnected;
                SetEkgState(false);
            }
        }

        private void OpenTradeSearch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string url || string.IsNullOrEmpty(url))
            {
                TradeStatusText.Text = "No trade search to open yet — run a search first.";
                return;
            }

            // Security: Validate that the URL is a legitimate web link to prevent command injection.
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                TradeStatusText.Text = "Invalid trade search URL.";
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                TradeStatusText.Text = $"Could not open browser: {ex.Message}";
            }
        }

        private void RefreshTradeGroups()
        {
            TradeGroupsList.ItemsSource = null;
            TradeGroupsList.ItemsSource = _tradeGroups;
        }

        // Map build gear slots to trade queries: uniques by name, everything else by base type.
        // Many .build files specify gear only via additional_text (its first line is the base type,
        // e.g. "Topaz Ring", "Varnished Crossbow"); BuildInventorySlot.ItemName already resolves that
        // the same way the loot-filter highlighting does. Deduped. Base searches also carry the slot's
        private static TradeItemQuery? ProcessInventorySlot(BuildInventorySlot slot, Dictionary<string, AffixImportance>? savedWeights)
        {
            TradeItemQuery query;
            if (!string.IsNullOrWhiteSpace(slot.UniqueName))
            {
                query = new TradeItemQuery { Label = slot.UniqueName!, Name = slot.UniqueName };
            }
            else
            {
                // UniqueName is empty here, so ItemName = BaseType ?? first line of additional_text.
                string baseType = slot.ItemName;
                if (string.IsNullOrWhiteSpace(baseType))
                {
                    return null;
                }
                query = new TradeItemQuery { Label = baseType, BaseType = baseType };
                var affixes = ParseRecommendedAffixes(slot.AdditionalText).ToList();
                foreach (var affix in affixes)
                {
                    string affixKey = $"{slot.InventoryId}|{affix.Text}";
                    if (savedWeights != null && savedWeights.TryGetValue(affixKey, out var savedImportance))
                    {
                        affix.Importance = savedImportance;
                    }
                    else
                    {
                        affix.Importance = GetDefaultImportance(slot.InventoryId, affix.Text);
                    }
                }
                query.Affixes.AddRange(affixes);
            }

            query.SlotId = slot.InventoryId;
            if (slot.LevelInterval is { Count: >= 1 } interval)
            {
                query.MinRequiredLevel = interval[0];
            }
            return query;
        }

        // recommended affixes (additional_text lines 2+) and the build's minimum level (level_interval[0]).
        private List<TradeItemQuery> BuildTradeQueries(PoeBuild build)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queries = new List<TradeItemQuery>();

            string buildKey = _appSettings.BuildFilePath ?? string.Empty;
            _appSettings.BuildCustomWeights ??= [];
            _appSettings.BuildCustomWeights.TryGetValue(buildKey, out var savedWeights);

            foreach (var slot in build.InventorySlots)
            {
                var query = ProcessInventorySlot(slot, savedWeights);
                if (query == null) continue;

                string key = $"{query.Name}|{query.BaseType}";
                if (seen.Add(key))
                {
                    queries.Add(query);
                }
            }

            return queries;
        }

        private static AffixImportance GetDefaultImportance(string? slotId, string affixText)
        {
            var bucket = BisWeighting.Categorize(affixText);
            double weight = BisWeighting.Weight(slotId, bucket);
            if (weight >= 100)
            {
                return AffixImportance.Important;
            }
            return AffixImportance.Wanted;
        }

        // additional_text is "<base type>\n1. <affix>\n2. <affix>…"; line 0 is the base type, the rest
        // are numbered recommended affixes. Strip the "N. " prefix and return the mod text.
        private static IEnumerable<BuildAffix> ParseRecommendedAffixes(string? additionalText)
        {
            if (string.IsNullOrWhiteSpace(additionalText))
            {
                yield break;
            }

            var lines = additionalText.Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                string text = AffixPrefixRegex().Replace(lines[i], string.Empty).Trim();
                if (text.Length > 0)
                {
                    yield return new BuildAffix { Text = text };
                }
            }
        }

        [GeneratedRegex(@"^\s*\d+\.\s*")]
        private static partial Regex AffixPrefixRegex();

        // Current Divine/Exalted → Chaos rates from loaded poe.ninja data; falls back to defaults.
        private CurrencyRates BuildCurrencyRates()
        {
            double divine = _marketItems.Find(i => i.Name == _divineOrbName && i.Category == _currencyCategory)?.ChaosValue ?? 0;
            double exalted = _marketItems.Find(i => i.Name == _exaltedOrbName && i.Category == _currencyCategory)?.ChaosValue ?? 0;
            return new CurrencyRates(divine, exalted);
        }

        // Player-chosen minimum number of recommended affixes a listing must match (0 = ignore affixes).
        private int ReadMinAffixMatches()
        {
            if (int.TryParse(MinAffixMatchBox?.Text, out int n) && n >= 0)
            {
                return n;
            }
            return 0;
        }

        // Per-item Best-in-slot budget in Divine Orbs, sent straight to the trade2 "Buyout Price" filter.
        // Null (no/zero/invalid input) = no budget cap.
        private double? ReadBudgetDivine()
        {
            if (double.TryParse(BudgetDivineBox?.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double div) && div > 0)
            {
                return div;
            }
            return null;
        }

        private string? DetectClassFromAscendancy()
        {
            if (!string.IsNullOrEmpty(_activeBuild?.Ascendancy))
            {
                return _activeBuild.Ascendancy;
            }
            return null;
        }

        private string? DetectClassFromPassives()
        {
            if (_activeBuild?.Passives == null)
            {
                return null;
            }

            foreach (var id in _activeBuild.Passives.Select(p => p.Id))
            {
                if (string.IsNullOrEmpty(id)) continue;
                string? className = MapPassiveIdToClass(id);
                if (className != null) return className;
            }

            return null;
        }

        private static string? MapPassiveIdToClass(string id)
        {
            if (id.Contains("mercenary", StringComparison.OrdinalIgnoreCase)) return _classMercenary;
            if (id.Contains("monk", StringComparison.OrdinalIgnoreCase) || id.Contains("martial_artist", StringComparison.OrdinalIgnoreCase) || id.Contains("martialartist", StringComparison.OrdinalIgnoreCase)) return _classMonk;
            if (id.Contains("witch", StringComparison.OrdinalIgnoreCase)) return _classWitch;
            if (id.Contains("sorceress", StringComparison.OrdinalIgnoreCase)) return _classSorceress;
            if (id.Contains("druid", StringComparison.OrdinalIgnoreCase)) return _classDruid;
            if (id.Contains("ranger", StringComparison.OrdinalIgnoreCase)) return _classRanger;
            if (id.Contains("huntress", StringComparison.OrdinalIgnoreCase)) return _classHuntress;
            if (id.Contains("warrior", StringComparison.OrdinalIgnoreCase)) return _classWarrior;
            return null;
        }

        private string? DetectClassFromBuildName()
        {
            if (string.IsNullOrEmpty(_activeBuild?.Name))
            {
                return null;
            }

            string name = _activeBuild.Name;
            if (name.Contains("mercenary", StringComparison.OrdinalIgnoreCase)) return _classMercenary;
            if (name.Contains("monk", StringComparison.OrdinalIgnoreCase)) return _classMonk;
            if (name.Contains("witch", StringComparison.OrdinalIgnoreCase)) return _classWitch;
            if (name.Contains("sorceress", StringComparison.OrdinalIgnoreCase)) return _classSorceress;
            if (name.Contains("druid", StringComparison.OrdinalIgnoreCase)) return _classDruid;
            if (name.Contains("ranger", StringComparison.OrdinalIgnoreCase)) return _classRanger;
            if (name.Contains("huntress", StringComparison.OrdinalIgnoreCase)) return _classHuntress;
            if (name.Contains("warrior", StringComparison.OrdinalIgnoreCase)) return _classWarrior;

            return null;
        }

        private static string[]? GetSynonymsForClass(string? buildClass)
        {
            if (string.IsNullOrEmpty(buildClass)) return null;

            buildClass = buildClass.Trim();
            if (string.Equals(buildClass, _classMonk, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(buildClass, "Martial Artist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(buildClass, "MartialArtist", StringComparison.OrdinalIgnoreCase))
            {
                return [_classMonk, "Martial Artist", "Acolyte of Chayula"];
            }
            if (string.Equals(buildClass, _classMercenary, StringComparison.OrdinalIgnoreCase))
            {
                return [_classMercenary];
            }
            if (string.Equals(buildClass, _classWarrior, StringComparison.OrdinalIgnoreCase))
            {
                return [_classWarrior];
            }
            if (string.Equals(buildClass, _classSorceress, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(buildClass, "Stormweaver", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(buildClass, "Disciple of Varashta", StringComparison.OrdinalIgnoreCase))
            {
                return [_classSorceress, "Stormweaver", "Disciple of Varashta"];
            }
            if (string.Equals(buildClass, _classDruid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(buildClass, "Shaman", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(buildClass, "Oracle", StringComparison.OrdinalIgnoreCase))
            {
                return [_classDruid, "Shaman", "Oracle"];
            }
            if (string.Equals(buildClass, _classWitch, StringComparison.OrdinalIgnoreCase))
            {
                return [_classWitch];
            }
            if (string.Equals(buildClass, _classRanger, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(buildClass, "Deadeye", StringComparison.OrdinalIgnoreCase))
            {
                return [_classRanger, "Deadeye"];
            }
            if (string.Equals(buildClass, _classHuntress, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(buildClass, "Amazon", StringComparison.OrdinalIgnoreCase))
            {
                return [_classHuntress, "Amazon"];
            }

            return [buildClass];
        }

        private string[]? GetActiveBuildClassSynonyms()
        {
            if (_activeBuild == null) return null;

            string? buildClass = DetectClassFromAscendancy()
                ?? DetectClassFromPassives()
                ?? DetectClassFromBuildName();

            return GetSynonymsForClass(buildClass);
        }

        private async Task AutoLoadLastBuildAsync()
        {
            string buildFilePath = _appSettings.BuildFilePath;
            if (string.IsNullOrEmpty(buildFilePath) || !File.Exists(buildFilePath))
            {
                return;
            }

            var build = await _buildParser.ParseBuildFileAsync(buildFilePath);
            if (build == null)
            {
                return;
            }

            OnActiveBuildLoaded(build);
            StatusText.Text = $"Loaded last used build: {build.Name}";
            _logMonitor.TriggerHistoryScan();
            await LoadBuildUniquePricesAsync(build);
        }

        private void OnActiveBuildLoaded(PoeBuild build)
        {
            _activeBuild = build;
            BuildNameText.Text = build.Name;
            _tradeQueries = BuildTradeQueries(build);
            if (AffixWeightsControl != null)
            {
                AffixWeightsControl.ItemsSource = _tradeQueries.Where(q => q.Affixes.Count > 0).ToList();
            }

            // Refresh context menu checkmarks and items
            LoadBuildButtonOptions();
        }

        private async void AffixImportance_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiInitialized) return;
            await SaveActiveBuildCustomWeightsAsync();
        }

        private async Task SaveActiveBuildCustomWeightsAsync()
        {
            if (_activeBuild == null || _tradeQueries == null) return;

            string buildKey = _appSettings.BuildFilePath ?? string.Empty;
            _appSettings.BuildCustomWeights ??= [];

            if (!_appSettings.BuildCustomWeights.TryGetValue(buildKey, out var savedWeights))
            {
                savedWeights = [];
                _appSettings.BuildCustomWeights[buildKey] = savedWeights;
            }

            savedWeights.Clear();
            foreach (var query in _tradeQueries)
            {
                if (string.IsNullOrEmpty(query.SlotId)) continue;
                foreach (var affix in query.Affixes)
                {
                    string affixKey = $"{query.SlotId}|{affix.Text}";
                    savedWeights[affixKey] = affix.Importance;
                }
            }

            await SaveSettingsAsync(force: true);
        }

        private async Task InitializeStartupDataAsync()
        {
            await AutoLoadLastBuildAsync();
            await SyncEconomyDataAsync();
        }

        private async void ImportPob_Click(object sender, RoutedEventArgs e)
        {
            var importWindow = new PobImportWindow { Owner = this };
            if (importWindow.ShowDialog() != true)
            {
                return;
            }

            string pobCode = importWindow.ShareCode;
            if (!string.IsNullOrWhiteSpace(pobCode))
            {
                var xml = _buildParser.DecodePobShareCode(pobCode);
                if (!string.IsNullOrEmpty(xml))
                {
                    var build = _buildParser.ConvertPobXmlToPoeBuild(xml);
                    if (build != null)
                    {
                        OnActiveBuildLoaded(build);
                        StatusText.Text = $"Loaded PoB build: {build.Name}";

                        // Cache the imported build to a .build file and remember it, so it auto-loads
                        // next launch (PoB imports have no source file path of their own).
                        string cachePath = GetCachedBuildPath();
                        if (await _buildParser.SaveBuildFileAsync(build, cachePath))
                        {
                            _appSettings.BuildFilePath = cachePath;
                            await SaveSettingsAsync();
                        }

                        _logMonitor.TriggerHistoryScan();
                        await LoadBuildUniquePricesAsync(build);
                        await TriggerFilterRegenerationAsync();
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
                var build = await _buildParser.ParseBuildFileAsync(ofd.FileName);
                if (build != null)
                {
                    OnActiveBuildLoaded(build);
                    StatusText.Text = $"Loaded .build file: {build.Name}";
                    _appSettings.BuildFilePath = ofd.FileName;
                    await SaveSettingsAsync();
                    _logMonitor.TriggerHistoryScan();
                    await LoadBuildUniquePricesAsync(build);
                    await TriggerFilterRegenerationAsync();
                }
                else
                {
                    MessageBox.Show("Invalid build planner file format.", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BrowseLog_Click(object sender, RoutedEventArgs e)
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
                await SaveSettingsAsync();
            }
        }

        private async void BrowseFilter_Click(object sender, RoutedEventArgs e)
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
                await SaveSettingsAsync();
            }
        }

        private async Task InitializeAppAsync()
        {
            // Load saved settings (or defaults)
            await LoadSettingsAsync();

            // Create HUD Window
            _hudWindow = new HudWindow(_appSettings, OnHudPositionChanged);
            if (_appSettings.IsHudVisible)
                _hudWindow.Show();

            // Populate base filter options
            LoadBaseFilterOptions();

            // Populate base build planner options
            LoadBuildButtonOptions();

            // Initialize FileSystemWatcher for the loaded base filter
            SetupBaseFilterWatcher(_selectedBaseFilterPath);

            // Load Saved Theme
            await LoadActiveThemeAsync();

            // Host the loot-filter style editor in the "Filter Styles" dashboard tab.
            var styleEditor = new StyleEditorControl(ActiveTheme)
            {
                ThemeApplied = StyleEditor_ThemeApplied
            };
            FilterStylesHost.Content = styleEditor;

            // Mock list view items until sync
            LoadMockItems();

            // Wait for Window Handle to be initialized (OnSourceInitialized)
            await _sourceInitializedTcs.Task;

            // Initialize monitoring if log file exists
            if (File.Exists(LogPathBox.Text))
            {
                _logMonitor.StartMonitoring(LogPathBox.Text);
            }

            // Apply opacity immediately to MainWindow (whole dashboard, not just the border)
            if (MainWindowBorder != null && _appSettings != null)
            {
                MainWindowBorder.Opacity = _appSettings.EditModeOpacity;
            }

            // Initialize Tray Icon
            InitializeTrayIcon();

            // Start process detection for Path of Exile 2
            _processCheckCts = new CancellationTokenSource();
            _ = StartProcessDetectionAsync(_processCheckCts.Token);

            // Check if base filter was missing on load
            await CheckMissingBaseFilterAsync();

            // Restore the last used .build file and refresh economy data automatically
            await InitializeStartupDataAsync();

            // Defer setting _isUiInitialized to true until the UI is fully loaded and idle
            Dispatcher.BeginInvoke(new Action(() => _isUiInitialized = true), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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
            _marketItemNames.Clear();
            foreach (var item in _marketItems)
            {
                _marketItemNames.Add(item.Name);
            }
            NormalizeMarketValues(_marketItems);
            AddPinnedExchangeRateItem();
            UpdateCategoryDropdown();
            ApplyCategoryFilter();
        }

        private async Task LoadActiveThemeAsync()
        {
            string themePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "args", "active_theme.json");
            if (File.Exists(themePath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(themePath);
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

        private async Task SaveActiveThemeAsync()
        {
            string themePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "args", "active_theme.json");
            try
            {
                string json = JsonSerializer.Serialize(ActiveTheme, _jsonOptions);
                await File.WriteAllTextAsync(themePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save active theme: {ex.Message}");
            }
        }

        // The "Filter Styles" tab invokes this when the user clicks Save & Apply.
        private async void StyleEditor_ThemeApplied(FilterTheme theme)
        {
            ActiveTheme = theme;
            await SaveActiveThemeAsync();
            await TriggerFilterRegenerationAsync();
        }

        // --- Settings & Base Filter Merging Methods ---

        private static string GetSettingsFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "LootPulse", "settings.json");
        }

        // Where a PoB-imported build is cached so it can be auto-loaded next launch.
        private static string GetCachedBuildPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(appData, "LootPulse");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "last_build.build");
        }

        private async Task LoadSettingsAsync()
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
                    string json = await File.ReadAllTextAsync(settingsFile);
                    settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        loadedLogPath = settings.LogPath;
                        loadedFilterPath = settings.FilterOutputPath;
                        loadedBaseFilterPath = settings.SelectedBaseFilterPath;
                        _appSettings.BuildFilePath = settings.BuildFilePath;
                        _appSettings.BuildCustomWeights = settings.BuildCustomWeights ?? [];

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
                await SaveSettingsAsync(force: true);
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

        private async Task SaveSettingsAsync(bool force = false)
        {
            if (!_isUiInitialized && !force) return;
            try
            {
                string settingsFile = GetSettingsFilePath();
                string? directory = Path.GetDirectoryName(settingsFile);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Capture UI property values before the first await to ensure thread safety
                string tier1Text = Tier1Box.Text;
                string tier2Text = Tier2Box.Text;
                string logPathText = LogPathBox.Text;
                string filterPathText = FilterPathBox.Text;

                _ = double.TryParse(tier1Text, System.Globalization.CultureInfo.InvariantCulture, out double t1);
                _ = double.TryParse(tier2Text, System.Globalization.CultureInfo.InvariantCulture, out double t2);

                _appSettings.LogPath = logPathText;
                _appSettings.FilterOutputPath = filterPathText;
                _appSettings.SelectedBaseFilterPath = _selectedBaseFilterPath ?? string.Empty;
                _appSettings.Tier1Threshold = t1 > 0 ? t1 : 1.0;
                _appSettings.Tier2Threshold = t2 > 0 ? t2 : 1.0;

                string json = JsonSerializer.Serialize(_appSettings, _jsonOptions);
                await File.WriteAllTextAsync(settingsFile, json);
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

        private void UpdateSelectedFilterDisplayText()
        {
            if (string.IsNullOrEmpty(_selectedBaseFilterPath))
            {
                SelectedFilterNameText.Text = "Base Filter: None (LootPulse Highlights Only)";
            }
            else
            {
                string displayName = _selectedBaseFilterDisplayName ?? "Unknown Filter";

                if (displayName == "Unknown Filter")
                {
                    displayName = ParseFilterDisplayName(_selectedBaseFilterPath);
                }

                SelectedFilterNameText.Text = $"Base Filter: {displayName}";
            }
        }

        private void LoadBaseFilterOptions()
        {
            var contextMenu = new ContextMenu { Style = (Style)FindResource(_darkContextMenuStyleKey) };

            var noneMenuItem = new MenuItem
            {
                Header = "None (LootPulse Highlights Only)",
                Style = (Style)FindResource(_darkMenuItemStyleKey),
                IsChecked = string.IsNullOrEmpty(_selectedBaseFilterPath)
            };
            if (noneMenuItem.IsChecked) _selectedBaseFilterDisplayName = noneMenuItem.Header.ToString();
            noneMenuItem.Click += (s, e) => SelectBaseFilterOption(string.Empty, noneMenuItem);
            contextMenu.Items.Add(noneMenuItem);

            string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string onlineFiltersDir = Path.Combine(myDocs, @"My Games\Path of Exile 2\OnlineFilters");

            bool matchedAny = false;

            if (Directory.Exists(onlineFiltersDir))
            {
                try
                {
                    var files = Directory.GetFiles(onlineFiltersDir);
                    foreach (var file in files)
                    {
                        string displayName = ParseFilterDisplayName(file);
                        bool isSelected = string.Equals(file, _selectedBaseFilterPath, StringComparison.OrdinalIgnoreCase);
                        if (isSelected) matchedAny = true;

                        var item = new MenuItem
                        {
                            Header = $"{displayName} (Subscribed)",
                            Style = (Style)FindResource(_darkMenuItemStyleKey),
                            IsChecked = isSelected
                        };
                        if (isSelected) _selectedBaseFilterDisplayName = item.Header.ToString();
                        item.Click += (s, e) => SelectBaseFilterOption(file, item);
                        contextMenu.Items.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error scanning OnlineFilters: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(_selectedBaseFilterPath) && !matchedAny)
            {
                if (File.Exists(_selectedBaseFilterPath))
                {
                    var item = new MenuItem
                    {
                        Header = $"{Path.GetFileName(_selectedBaseFilterPath)} (Local)",
                        Style = (Style)FindResource(_darkMenuItemStyleKey),
                        IsChecked = true
                    };
                    _selectedBaseFilterDisplayName = item.Header.ToString();
                    item.Click += (s, e) => SelectBaseFilterOption(_selectedBaseFilterPath, item);
                    contextMenu.Items.Add(item);
                }
                else
                {
                    _isBaseFilterMissingOnStartup = true;
                }
            }

            contextMenu.Items.Add(new Separator { Style = (Style)FindResource("LootMenuSeparator") });

            var browseItem = new MenuItem
            {
                Header = "Browse for Local Filter File...",
                Style = (Style)FindResource(_darkMenuItemStyleKey)
            };
            browseItem.Click += BrowseBaseFilter_Click;
            contextMenu.Items.Add(browseItem);

            BaseFilterButton.ContextMenu = contextMenu;

            UpdateSelectedFilterDisplayText();
        }

        private static string ParseBuildDisplayName(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    string content = File.ReadAllText(filePath);
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("name", out var prop))
                    {
                        string? name = prop.GetString();
                        if (!string.IsNullOrEmpty(name)) return name;
                    }
                }
            }
            catch
            {
                // Fallback to filename
            }
            return Path.GetFileNameWithoutExtension(filePath);
        }

        private void LoadBuildButtonOptions()
        {
            var contextMenu = new ContextMenu { Style = (Style)FindResource(_darkContextMenuStyleKey) };

            string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string onlineBuildsDir = Path.Combine(myDocs, @"My Games\Path of Exile 2\OnlineBuilds");

            bool matchedAny = false;
            string selectedPath = _appSettings.BuildFilePath ?? string.Empty;

            if (Directory.Exists(onlineBuildsDir))
            {
                try
                {
                    var files = Directory.GetFiles(onlineBuildsDir, "*.build");
                    foreach (var file in files)
                    {
                        string displayName = ParseBuildDisplayName(file);
                        bool isSelected = string.Equals(file, selectedPath, StringComparison.OrdinalIgnoreCase);
                        if (isSelected) matchedAny = true;

                        var item = new MenuItem
                        {
                            Header = $"{displayName} (Subscribed)",
                            Style = (Style)FindResource(_darkMenuItemStyleKey),
                            IsChecked = isSelected
                        };
                        item.Click += (s, e) => SelectBuildOption(file, item);
                        contextMenu.Items.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error scanning OnlineBuilds: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(selectedPath) && !matchedAny)
            {
                if (File.Exists(selectedPath))
                {
                    string displayName = ParseBuildDisplayName(selectedPath);
                    var item = new MenuItem
                    {
                        Header = $"{displayName} (Local)",
                        Style = (Style)FindResource(_darkMenuItemStyleKey),
                        IsChecked = true
                    };
                    item.Click += (s, e) => SelectBuildOption(selectedPath, item);
                    contextMenu.Items.Add(item);
                }
            }

            if (contextMenu.Items.Count > 0)
            {
                contextMenu.Items.Add(new Separator { Style = (Style)FindResource("LootMenuSeparator") });
            }

            var importItem = new MenuItem
            {
                Header = "Import PoB Share Code...",
                Style = (Style)FindResource(_darkMenuItemStyleKey)
            };
            importItem.Click += ImportPob_Click;
            contextMenu.Items.Add(importItem);

            var browseItem = new MenuItem
            {
                Header = "Browse for Local .build File...",
                Style = (Style)FindResource(_darkMenuItemStyleKey)
            };
            browseItem.Click += SelectBuildFile_Click;
            contextMenu.Items.Add(browseItem);

            LoadBuildButton.ContextMenu = contextMenu;
        }

        private async void SelectBuildOption(string filePath, MenuItem selectedItem)
        {
            try
            {
                var build = await _buildParser.ParseBuildFileAsync(filePath);
                if (build != null)
                {
                    OnActiveBuildLoaded(build);
                    StatusText.Text = $"Loaded .build file: {build.Name}";
                    _appSettings.BuildFilePath = filePath;
                    await SaveSettingsAsync();
                    _logMonitor.TriggerHistoryScan();
                    await LoadBuildUniquePricesAsync(build);
                    await TriggerFilterRegenerationAsync();
                }
                else
                {
                    MessageBox.Show("Invalid build planner file format.", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load build: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task CheckMissingBaseFilterAsync()
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
                    _selectedBaseFilterDisplayName = "None (LootPulse Highlights Only)";
                    _appSettings.SelectedBaseFilterPath = string.Empty;
                    await SaveSettingsAsync(force: true);
                    await UpdateOutputFilterPathAsync();
                    await TriggerFilterRegenerationAsync();
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

        private async void BaseFilter_Changed(object sender, FileSystemEventArgs e)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                // Debounce briefly for GGG client to release file lock
                await Task.Delay(500);
                StatusText.Text = "Subscribed base filter update detected. Re-merging filter...";
                await TriggerFilterRegenerationAsync();
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

        private async Task UpdateOutputFilterPathAsync()
        {
            string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string folder = Path.Combine(myDocs, @"My Games\Path of Exile 2");

            string namePart = "LootPulse_Only";
            if (!string.IsNullOrEmpty(_selectedBaseFilterPath))
            {
                string cleanName = ParseFilterDisplayName(_selectedBaseFilterPath);

                cleanName = cleanName
                    .Replace(" (Subscribed)", "", StringComparison.Ordinal)
                    .Replace(" (Local)", "", StringComparison.Ordinal);

                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    cleanName = cleanName.Replace(c.ToString(), "_", StringComparison.Ordinal);
                }
                namePart = $"{cleanName}_LootPulse";
            }

            FilterPathBox.Text = Path.Combine(folder, $"{namePart}.filter");
            await SaveSettingsAsync();
        }

        private async void BaseFilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private async void SelectBaseFilterOption(string filePath, MenuItem selectedItem)
        {
            _selectedBaseFilterPath = filePath;
            _selectedBaseFilterDisplayName = selectedItem.Header.ToString();
            SetupBaseFilterWatcher(_selectedBaseFilterPath);
            await UpdateOutputFilterPathAsync();
            await TriggerFilterRegenerationAsync();

            if (BaseFilterButton.ContextMenu != null)
            {
                foreach (var obj in BaseFilterButton.ContextMenu.Items)
                {
                    if (obj is MenuItem mi)
                    {
                        mi.IsChecked = (mi == selectedItem);
                    }
                }
            }

            UpdateSelectedFilterDisplayText();
        }

        private async void BrowseBaseFilter_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "Filter Files (*.filter)|*.filter|All Files (*.*)|*.*",
                Title = "Select Base Filter to Merge"
            };

            if (ofd.ShowDialog() == true)
            {
                _selectedBaseFilterPath = ofd.FileName;
                SetupBaseFilterWatcher(_selectedBaseFilterPath);
                await UpdateOutputFilterPathAsync();
                await TriggerFilterRegenerationAsync();

                LoadBaseFilterOptions();
            }
        }

        private async void OnHudPositionChanged(AppSettings updatedSettings)
        {
            if (!_isUiInitialized) return;
            _appSettings.HudWidth = updatedSettings.HudWidth;
            _appSettings.HudHeight = updatedSettings.HudHeight;
            _appSettings.HudXPercent = updatedSettings.HudXPercent;
            _appSettings.HudYPercent = updatedSettings.HudYPercent;
            await SaveSettingsAsync();
        }

        private async Task ToggleHudVisibilityAsync()
        {
            _appSettings.IsHudVisible = !_appSettings.IsHudVisible;

            // Sync Checkbox
            HudVisibleCheckBox.Checked -= HudVisibleCheckBox_Changed;
            HudVisibleCheckBox.Unchecked -= HudVisibleCheckBox_Changed;
            HudVisibleCheckBox.IsChecked = _appSettings.IsHudVisible;
            HudVisibleCheckBox.Checked += HudVisibleCheckBox_Changed;
            HudVisibleCheckBox.Unchecked += HudVisibleCheckBox_Changed;

            await SaveSettingsAsync();

            if (_appSettings.IsHudVisible)
            {
                _hudWindow?.Show();
                StatusText.Text = "HUD overlay enabled.";
            }
            else
            {
                _hudWindow?.Hide();
                StatusText.Text = "HUD overlay hidden.";
            }
        }

        private async void HudVisibleCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isUiInitialized) return;
            if (_appSettings == null) return;

            _appSettings.IsHudVisible = HudVisibleCheckBox.IsChecked == true;
            await SaveSettingsAsync();

            if (_hudWindow != null)
            {
                if (_appSettings.IsHudVisible)
                    _hudWindow.Show();
                else
                    _hudWindow.Hide();
            }
        }

        private async void EconomyHighlightsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isUiInitialized) return;
            if (_appSettings == null) return;

            _appSettings.ShowEconomyHighlights = EconomyHighlightsCheckBox.IsChecked == true;
            await TriggerFilterRegenerationAsync();
        }

        private async void OpacitySliders_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isUiInitialized) return;
            if (_appSettings == null || EditOpacitySlider == null || HudOpacitySlider == null) return;

            _appSettings.EditModeOpacity = EditOpacitySlider.Value;
            _appSettings.HudModeOpacity = HudOpacitySlider.Value;
            await SaveSettingsAsync();

            // Dashboard Opacity fades the whole dashboard (panels + content), not just the border.
            if (MainWindowBorder != null)
            {
                MainWindowBorder.Opacity = _appSettings.EditModeOpacity;
            }

            // HUD Overlay Opacity controls only the HUD window, in either mode.
            _hudWindow?.SetClickThrough(_isClickThroughEnabled, _appSettings.HudModeOpacity);
        }

        /// <summary>
        /// Builds the HUD currency ticker from user-selected currencies in AppSettings.
        /// Shows up to 6 items, each valued in Exalted.
        /// </summary>
        private void UpdateHudCurrencyTicker()
        {
            if (_hudWindow == null) return;
            var selected = _appSettings.HudCurrencies ?? new();
            var items = new List<HudCurrencyItem>();
            foreach (var name in selected.Take(6))
            {
                var orb = _marketItems.Find(i => i.Name == name && i.Category == _currencyCategory);
                string shortLabel = GetShortCurrencyLabel(name);
                string value = orb?.ExaltedValue > 0 ? $"{orb.ExaltedValue:F0}ex" : "—";
                items.Add(new HudCurrencyItem { Label = $"{shortLabel}:", Value = value });
            }
            _hudWindow.UpdateCurrencyTicker(items);
        }

        /// <summary>
        /// Short label for currency names in the HUD (e.g. "Divine Orb" → "Div").
        /// </summary>
        private static string GetShortCurrencyLabel(string name)
        {
            return name switch
            {
                "Divine Orb" => "Div",
                "Exalted Orb" => "Ex",
                "Chaos Orb" => "Chaos",
                "Mirror of Kalandra" => "Mirror",
                "Jeweller's Orb" => "Jew",
                "Orb of Fusing" => "Fuse",
                "Orb of Alchemy" => "Alch",
                "Orb of Scouring" => "Scour",
                "Chromatic Orb" => "Chrome",
                _ => name.Length > 6 ? name[..6] : name
            };
        }

        /// <summary>
        /// Handles checkbox toggle on currency list to select/deselect HUD display.
        /// Enforces max 6 selected currencies.
        /// </summary>
        private async void HudCurrencyCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isUiInitialized || _appSettings == null) return;
            if (sender is not System.Windows.Controls.CheckBox cb) return;
            if (cb.DataContext is not MarketItem item) return;

            var selected = _appSettings.HudCurrencies ?? new();

            if (cb.IsChecked == true)
            {
                if (!selected.Contains(item.Name))
                {
                    if (selected.Count >= 6)
                    {
                        cb.IsChecked = false;
                        StatusText.Text = "Maximum 6 currencies can be shown on the HUD.";
                        return;
                    }
                    selected.Add(item.Name);
                }
            }
            else
            {
                selected.Remove(item.Name);
            }

            _appSettings.HudCurrencies = selected;
            await SaveSettingsAsync();
            UpdateHudCurrencyTicker();
        }

        private async void ResetHudPosition_Click(object sender, RoutedEventArgs e)
        {
            _appSettings.HudWidth = 250;
            _appSettings.HudHeight = 120;
            _appSettings.HudXPercent = 0.80;
            _appSettings.HudYPercent = 0.05;
            await SaveSettingsAsync();

            if (_hudWindow != null)
            {
                _hudWindow.Width = 250;
                _hudWindow.Height = 120;
                _hudWindow.RestorePosition();
            }
            StatusText.Text = "HUD size and position reset to defaults.";
        }

        private async void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            string oldLogPath = _appSettings.LogPath;
            string oldBaseFilter = _appSettings.SelectedBaseFilterPath;

            await SaveSettingsAsync(force: true);

            // Restart log monitor if path changed
            if (LogPathBox.Text != oldLogPath && File.Exists(LogPathBox.Text))
            {
                _logMonitor.StopMonitoring();
                _logMonitor.StartMonitoring(LogPathBox.Text);
            }

            // Re-setup base filter watcher if changed
            if (_selectedBaseFilterPath != oldBaseFilter)
            {
                SetupBaseFilterWatcher(_selectedBaseFilterPath);
            }

            await TriggerFilterRegenerationAsync();
            StatusText.Text = "Settings saved successfully.";
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
                        await Dispatcher.InvokeAsync(OnGameLaunchDetectedAsync);
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

        private async Task OnGameLaunchDetectedAsync()
        {
            StatusText.Text = "Path of Exile 2 launch detected! Activating HUD overlay...";
            if (!_isClickThroughEnabled)
            {
                ToggleOverlayMode();
            }
            else
            {
                _appSettings.IsHudVisible = true;
                await SaveSettingsAsync();
                if (_hudWindow != null)
                {
                    _hudWindow.SetClickThrough(true, _appSettings.HudModeOpacity);
                    _hudWindow.Show();
                }
            }
        }

        private static bool IsPoE2Running()
        {
            // We use targeted GetProcessesByName instead of iterating all processes to reduce CPU overhead.
            // These are the two common executable names for Path of Exile 2.
            string[] targetNames = ["PathOfExile2", "PathOfExile_x64"];

            foreach (string name in targetNames)
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Length > 0)
                    {
                        return true;
                    }
                }
                finally
                {
                    foreach (var p in processes)
                    {
                        p.Dispose();
                    }
                }
            }

            return false;
        }

        private static void NormalizeMarketValues(List<MarketItem> items)
        {
            if (items == null) return;

            var (divinePriceInChaos, exaltedPriceInChaos) = ResolveCurrencyRates(items);

            foreach (var item in items)
            {
                NormalizeItemValues(item, divinePriceInChaos, exaltedPriceInChaos);
            }
        }

        private static (double divinePriceInChaos, double exaltedPriceInChaos) ResolveCurrencyRates(List<MarketItem> items)
        {
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

            return (divinePriceInChaos, exaltedPriceInChaos);
        }

        private static void NormalizeItemValues(MarketItem item, double divinePriceInChaos, double exaltedPriceInChaos)
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

        private void AddPinnedExchangeRateItem()
        {
            _marketItems.RemoveAll(i => i.Category == _exchangeRateCategory);
            _marketItemNames.Remove(_divineOrbName);

            // Rank every other commodity highest-to-lowest in Divine terms, so the pinned
            // reference item inserted below always sits above a sensibly ordered list.
            _marketItems.Sort((a, b) => b.DivineValue.CompareTo(a.DivineValue));

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
                Category = _exchangeRateCategory,
                ExaltedValue = rate,
                LastUpdated = DateTime.UtcNow
            };
            _marketItems.Insert(0, exchangeRateItem);
            _marketItemNames.Add(_divineOrbName);
        }

        private void UpdateCategoryDropdown()
        {
            if (EconomyCategoryComboBox == null) return;

            string? currentSelection = EconomyCategoryComboBox.SelectedItem as string;

            List<string> categories = [.. _marketItems
                .Select(i => i.Category)
                .Distinct()
                .Where(c => c != _exchangeRateCategory && !string.IsNullOrEmpty(c))];

            categories.Sort();
            categories.Insert(0, "All Categories");

            EconomyCategoryComboBox.SelectionChanged -= EconomyCategoryComboBox_SelectionChanged;
            EconomyCategoryComboBox.ItemsSource = categories;

            if (currentSelection is not null && categories.Contains(currentSelection))
            {
                EconomyCategoryComboBox.SelectedItem = currentSelection;
            }
            else
            {
                EconomyCategoryComboBox.SelectedIndex = 0;
            }
            EconomyCategoryComboBox.SelectionChanged += EconomyCategoryComboBox_SelectionChanged;
        }

        private void ApplyCategoryFilter()
        {
            if (EconomyCategoryComboBox == null || ItemListView == null) return;

            string selectedCategory = (EconomyCategoryComboBox.SelectedItem as string) ?? "All Categories";

            IEnumerable<MarketItem> source = selectedCategory == "All Categories"
                ? _marketItems
                : _marketItems.Where(i => i.Category == _exchangeRateCategory || i.Category == selectedCategory);

            // Wrap in MarketItemDisplay to provide sparkline/trend bindings
            var displayItems = source.Select(i => new MarketItemDisplay(i, _priceHistory)).ToList();

            ItemListView.ItemsSource = null;
            ItemListView.ItemsSource = displayItems;
        }

        private void EconomyCategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyCategoryFilter();
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
                               !_marketItemNames.Contains(slot.UniqueName!));

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
                        var newItems = items.Where(item => !_marketItemNames.Contains(item.Name)).ToList();
                        if (newItems.Count > 0)
                        {
                            _marketItems.AddRange(newItems);
                            foreach (var ni in newItems)
                            {
                                _marketItemNames.Add(ni.Name);
                            }
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
                UpdateCategoryDropdown();
                ApplyCategoryFilter();
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

        private async void UpdateMetadata_Click(object sender, RoutedEventArgs e)
        {
            UpdateMetadataButton.IsEnabled = false;
            MetadataStatusText.Text = "Status: Connecting to trade session and preparing update...";

            try
            {
                // Ensure WebView2 is initialized
                bool connected = await _tradeTransport.ConnectAsync().ConfigureAwait(true);
                if (!connected)
                {
                    MetadataStatusText.Text = "Status: Update failed. Could not connect to Path of Exile trade session (requires login/WebView2).";
                    return;
                }

                MetadataStatusText.Text = "Status: Checking GGG static data and scanning poe2db.tw (this may take up to a minute)...";

                var (newCurrencies, newBases, log) = await _metadataService.UpdateMetadataAsync(_tradeTransport).ConfigureAwait(true);

                // Reload the updated base items into FilterBuilder
                var baseConfig = _metadataService.LoadBaseItemsConfig();
                FilterBuilder.Initialize(baseConfig);

                MetadataStatusText.Text = $"Status: Update completed. Discovered {newBases} new base items. Local configurations reloaded!";
                System.Diagnostics.Debug.WriteLine(log);
            }
            catch (Exception ex)
            {
                MetadataStatusText.Text = $"Status: Error during update: {ex.Message}";
            }
            finally
            {
                UpdateMetadataButton.IsEnabled = true;
            }
        }

        // --- Cache Maintenance Methods ---

        private void CacheLockToggle_Changed(object sender, RoutedEventArgs e)
        {
            bool unlocked = CacheLockToggle.IsChecked == true;
            ClearPoe2CacheButton.IsEnabled = unlocked;
            ClearNvidiaCacheButton.IsEnabled = unlocked;
            ClearAmdCacheButton.IsEnabled = unlocked;
            ClearSteamCacheButton.IsEnabled = unlocked;
            CacheStatusText.Text = unlocked
                ? "Cache clearing unlocked. Click a button above to delete that cache."
                : "";
        }

        private static long GetDirectorySize(string path)
        {
            if (!Directory.Exists(path)) return 0;
            long size = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(file).Length; } catch { }
                }
            }
            catch { }
            return size;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        private static long ClearDirectoryContents(string path)
        {
            if (!Directory.Exists(path)) return 0;
            long deleted = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.Exists(entry))
                    {
                        var fi = new FileInfo(entry);
                        deleted += fi.Length;
                        fi.Attributes = FileAttributes.Normal;
                        File.Delete(entry);
                    }
                    else if (Directory.Exists(entry))
                    {
                        // Delete subdirectories first (recursive)
                        deleted += ClearDirectoryContents(entry);
                        try { Directory.Delete(entry, false); } catch { }
                    }
                }
                catch { }
            }
            return deleted;
        }

        /// <summary>Locates the PoE2 shader cache directories under %APPDATA%\Path of Exile 2.</summary>
        private static List<string> FindPoe2ShaderCacheDirectories()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string baseDir = Path.Combine(appData, "Path of Exile 2");
            string[] shaderDirs = ["ShaderCacheD3D12", "ShaderCacheVulkan"];
            var found = new List<string>();
            foreach (var sub in shaderDirs)
            {
                string path = Path.Combine(baseDir, sub);
                if (Directory.Exists(path)) found.Add(path);
            }
            return found;
        }

        private async void ClearPoe2Cache_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "This will delete the Path of Exile 2 shader caches (D3D12 and Vulkan) in %APPDATA%\\Path of Exile 2.\n\n" +
                "The game will recompile shaders on next launch (may stutter briefly once).\n\n" +
                "Make sure PoE2 is not running before proceeding.\n\nProceed?",
                "Confirm PoE2 Shader Cache Clear",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            ClearPoe2CacheButton.IsEnabled = false;
            CacheStatusText.Text = "Clearing PoE2 shader caches...";

            try
            {
                long freed = await Task.Run(() =>
                {
                    var cacheDirs = FindPoe2ShaderCacheDirectories();
                    long total = 0;
                    foreach (var dir in cacheDirs)
                    {
                        total += ClearDirectoryContents(dir);
                    }
                    return total;
                });

                CacheStatusText.Text = freed > 0
                    ? $"PoE2 shader caches cleared. Freed {FormatBytes(freed)}."
                    : "PoE2 shader cache directories not found or already empty.";
            }
            catch (Exception ex)
            {
                CacheStatusText.Text = $"Error clearing PoE2 shader cache: {ex.Message}";
            }
            finally
            {
                ClearPoe2CacheButton.IsEnabled = CacheLockToggle.IsChecked == true;
            }
        }

        /// <summary>Locates Nvidia shader cache directories under %LocalAppData%\NVIDIA.</summary>
        private static List<string> FindNvidiaShaderCacheDirectories()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] shaderDirs = ["DXCache", "GLCache"];
            var found = new List<string>();
            foreach (var sub in shaderDirs)
            {
                string path = Path.Combine(localAppData, "NVIDIA", sub);
                if (Directory.Exists(path)) found.Add(path);
            }
            return found;
        }

        private async void ClearNvidiaCache_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "This will delete the Nvidia GPU shader caches (DXCache, GLCache) in %LocalAppData%\\NVIDIA.\n\n" +
                "Shaders will be recompiled on next game/driver use (brief stutter possible).\n\nProceed?",
                "Confirm Nvidia Shader Cache Clear",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            ClearNvidiaCacheButton.IsEnabled = false;
            CacheStatusText.Text = "Clearing Nvidia shader caches...";

            try
            {
                long freed = await Task.Run(() =>
                {
                    var cacheDirs = FindNvidiaShaderCacheDirectories();
                    long total = 0;
                    foreach (var dir in cacheDirs)
                    {
                        total += ClearDirectoryContents(dir);
                    }
                    return total;
                });

                CacheStatusText.Text = freed > 0
                    ? $"Nvidia shader caches cleared. Freed {FormatBytes(freed)}."
                    : "Nvidia shader cache directories not found or already empty.";
            }
            catch (Exception ex)
            {
                CacheStatusText.Text = $"Error clearing Nvidia shader cache: {ex.Message}";
            }
            finally
            {
                ClearNvidiaCacheButton.IsEnabled = CacheLockToggle.IsChecked == true;
            }
        }

        /// <summary>Locates AMD shader cache directories under %LocalAppData%\AMD.</summary>
        private static List<string> FindAmdShaderCacheDirectories()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] shaderDirs = ["DxCache", "GLCache"];
            var found = new List<string>();
            foreach (var sub in shaderDirs)
            {
                string path = Path.Combine(localAppData, "AMD", sub);
                if (Directory.Exists(path)) found.Add(path);
            }
            return found;
        }

        private async void ClearAmdCache_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "This will delete the AMD GPU shader caches (DxCache, GLCache) in %LocalAppData%\\AMD.\n\n" +
                "Shaders will be recompiled on next game/driver use (brief stutter possible).\n\nProceed?",
                "Confirm AMD Shader Cache Clear",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            ClearAmdCacheButton.IsEnabled = false;
            CacheStatusText.Text = "Clearing AMD shader caches...";

            try
            {
                long freed = await Task.Run(() =>
                {
                    var cacheDirs = FindAmdShaderCacheDirectories();
                    long total = 0;
                    foreach (var dir in cacheDirs)
                    {
                        total += ClearDirectoryContents(dir);
                    }
                    return total;
                });

                CacheStatusText.Text = freed > 0
                    ? $"AMD shader caches cleared. Freed {FormatBytes(freed)}."
                    : "AMD shader cache directories not found or already empty.";
            }
            catch (Exception ex)
            {
                CacheStatusText.Text = $"Error clearing AMD shader cache: {ex.Message}";
            }
            finally
            {
                ClearAmdCacheButton.IsEnabled = CacheLockToggle.IsChecked == true;
            }
        }

        /// <summary>Locates Steam's per-game shader cache directory under steamapps\shadercache.</summary>
        private static List<string> FindSteamShaderCacheDirectories()
        {
            // Try registry first
            string? steamPath = null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                steamPath = key?.GetValue("SteamPath") as string;
            }
            catch { }

            // Fallback to common paths
            if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string[] commonPaths =
                [
                    Path.Combine(programFiles, "Steam"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
                ];
                steamPath = commonPaths.FirstOrDefault(Directory.Exists);
            }

            if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath)) return [];

            string shaderCachePath = Path.Combine(steamPath, "steamapps", "shadercache");
            return Directory.Exists(shaderCachePath) ? [shaderCachePath] : [];
        }

        private async void ClearSteamCache_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "This will delete Steam's per-game shader cache (steamapps\\shadercache).\n\n" +
                "Shaders will be recompiled on next game launch (brief stutter possible).\n\n" +
                "Make sure Steam is closed before proceeding.\n\nProceed?",
                "Confirm Steam Shader Cache Clear",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            ClearSteamCacheButton.IsEnabled = false;
            CacheStatusText.Text = "Clearing Steam shader cache...";

            try
            {
                long freed = await Task.Run(() =>
                {
                    var cacheDirs = FindSteamShaderCacheDirectories();
                    long total = 0;
                    foreach (var dir in cacheDirs)
                    {
                        total += ClearDirectoryContents(dir);
                    }
                    return total;
                });

                CacheStatusText.Text = freed > 0
                    ? $"Steam shader cache cleared. Freed {FormatBytes(freed)}."
                    : "Steam shader cache directory not found or already empty.";
            }
            catch (Exception ex)
            {
                CacheStatusText.Text = $"Error clearing Steam shader cache: {ex.Message}";
            }
            finally
            {
                ClearSteamCacheButton.IsEnabled = CacheLockToggle.IsChecked == true;
            }
        }
    }

    public class FilterOption
    {
        public string DisplayName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public bool IsSubscribed { get; set; }

        // The custom LootComboBox template renders the closed selection box from SelectionBoxItem,
        // which falls back to ToString(); return DisplayName so the chosen item shows correctly.
        public override string ToString() => DisplayName;
    }

    public class ImportanceOption
    {
        public string Name { get; set; } = string.Empty;
        public AffixImportance Value { get; set; }

        public override string ToString() => Name;
    }
}
