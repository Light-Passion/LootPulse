using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LootPulse.Services.Trade
{
    /// <summary>
    /// Proof-of-concept transport: authenticates by hosting a WebView2 browser in which the user logs
    /// into pathofexile.com once. API calls are then executed as same-origin <c>fetch()</c> inside that
    /// page, so they carry the session cookie and pass Cloudflare exactly like the real site does.
    ///
    /// All WebView2 access happens on the WPF UI thread (the control is a UI element). Callers should
    /// await these methods from the UI context (do not ConfigureAwait(false) before touching it).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "WebView2 is a UI element; awaits intentionally resume on the WPF dispatcher thread.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Browser/transport callbacks must never crash the WPF overlay.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "Transport is called only internally by Poe2TradeClient with non-null arguments.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "URL is forwarded verbatim to an in-browser fetch() call.")]
    public sealed class WebView2TradeTransport : ITradeTransport, IDisposable
    {
        private const string Origin = "https://www.pathofexile.com";

        private readonly Window _owner;
        private Window? _browserWindow;
        private WebView2? _webView;
        private DispatcherTimer? _loginPollTimer;
        private bool _initialized;
        private bool _disposing;
        private bool _awaitingLogin;   // true only while the login window is shown for the user

        // Park position for the realized-but-hidden host window (keeps it off any real monitor).
        private const double OffscreenCoord = -32000;

        // Logged in if the page header carries a logout control (present on every pathofexile.com page).
        private const string LoginCheckScript =
            "(function(){try{return !!document.querySelector('form[action=\"/logout\"], a[href=\"/logout\"]');}catch(e){return false;}})()";

        // Pending fetch() calls keyed by request id; resolved when the page posts the result back.
        private readonly ConcurrentDictionary<string, TaskCompletionSource<TradeHttpResponse>> _pending = new();

        public WebView2TradeTransport(Window owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        private bool _isConnected;

        /// <summary>True once we have a usable authenticated pathofexile.com session.</summary>
        public bool IsConnected => _isConnected;

        /// <summary>Raised whenever the connected state changes, so the UI can enable/disable the
        /// Connect button without polling.</summary>
        public event EventHandler<TradeConnectionChangedEventArgs>? ConnectionChanged;

        private void SetConnected(bool value)
        {
            if (_isConnected == value)
            {
                return;
            }
            _isConnected = value;
            ConnectionChanged?.Invoke(this, new TradeConnectionChangedEventArgs(value));
        }

        private static string UserDataFolder
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(appData, "LootPulse", "WebView2");
            }
        }

        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            // WebView2 is a UI element — always operate on the owner's dispatcher thread.
            if (!_owner.Dispatcher.CheckAccess())
            {
                return await _owner.Dispatcher.InvokeAsync(() => ConnectAsync(ct)).Task.Unwrap().ConfigureAwait(false);
            }

            await EnsureInitializedAsync().ConfigureAwait(true);
            await EnsureOnOriginAsync().ConfigureAwait(true);

            // If the persisted session is still valid we don't need to bother the user at all.
            if (await CheckLoggedInAsync().ConfigureAwait(true))
            {
                SetConnected(true);
                return true;
            }

            // Not logged in — show the browser so the user can sign in. The poll timer auto-closes it
            // the moment a logged-in session is detected.
            _awaitingLogin = true;
            ShowForLogin();
            _webView!.CoreWebView2.Navigate($"{Origin}/trade2");
            _loginPollTimer!.Start();
            return true;
        }

        /// <summary>
        /// Silently check whether the persisted session is still authenticated and update
        /// <see cref="IsConnected"/> (and raise <see cref="ConnectionChanged"/>) — without ever showing
        /// the login window. Used to decide whether the Connect button needs to be offered.
        /// </summary>
        public async Task<bool> RefreshConnectionAsync(CancellationToken ct = default)
        {
            if (!_owner.Dispatcher.CheckAccess())
            {
                return await _owner.Dispatcher.InvokeAsync(() => RefreshConnectionAsync(ct)).Task.Unwrap().ConfigureAwait(false);
            }

            await EnsureInitializedAsync().ConfigureAwait(true);
            await EnsureOnOriginAsync().ConfigureAwait(true);
            bool loggedIn = await CheckLoggedInAsync().ConfigureAwait(true);
            SetConnected(loggedIn);
            return loggedIn;
        }

        // Poll the page a few times for the logout control — the trade page is an SPA, so right after a
        // NavigationCompleted the header may not be in the DOM yet.
        private async Task<bool> CheckLoggedInAsync()
        {
            if (_webView?.CoreWebView2 == null)
            {
                return false;
            }

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    string raw = await _webView.CoreWebView2.ExecuteScriptAsync(LoginCheckScript);
                    if (raw != null && raw.Trim() == "true")
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Login check error: {ex.Message}");
                }
                await Task.Delay(500).ConfigureAwait(true);
            }
            return false;
        }

        // Bring the realized host window onto the owner's monitor for an interactive login.
        private void ShowForLogin()
        {
            if (_browserWindow == null)
            {
                return;
            }
            try
            {
                _browserWindow.Left = _owner.Left + Math.Max(0, (_owner.ActualWidth - _browserWindow.Width) / 2);
                _browserWindow.Top = _owner.Top + Math.Max(0, (_owner.ActualHeight - _browserWindow.Height) / 2);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Center login window error: {ex.Message}");
            }
            _browserWindow.ShowInTaskbar = true;
            _browserWindow.Show();
            _browserWindow.Activate();
        }

        // Hide the host window and move it back off-screen (kept realized so the session stays alive).
        private void ParkWindow()
        {
            _awaitingLogin = false;
            if (_browserWindow == null)
            {
                return;
            }
            _browserWindow.Hide();
            _browserWindow.ShowInTaskbar = false;
            _browserWindow.Left = OffscreenCoord;
            _browserWindow.Top = OffscreenCoord;
        }

        public async Task<TradeHttpResponse> SendAsync(HttpMethod method, string url, string? jsonBody, CancellationToken ct = default)
        {
            // WebView2 is a UI element — marshal back to the owner's dispatcher if a prior await
            // (e.g. the rate limiter's Task.Delay) resumed us on a thread-pool thread.
            if (!_owner.Dispatcher.CheckAccess())
            {
                return await _owner.Dispatcher.InvokeAsync(() => SendAsync(method, url, jsonBody, ct)).Task.Unwrap().ConfigureAwait(false);
            }

            await EnsureInitializedAsync().ConfigureAwait(true);
            await EnsureOnOriginAsync().ConfigureAwait(true);

            string requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<TradeHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = tcs;

            using var reg = ct.Register(() => tcs.TrySetCanceled());

            string script = BuildFetchScript(requestId, method.Method, url, jsonBody);
            await _webView!.CoreWebView2.ExecuteScriptAsync(script);

            var response = await tcs.Task.ConfigureAwait(true);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                SetConnected(true);
            }
            else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Session expired/insufficient — re-offer the Connect button.
                SetConnected(false);
            }
            return response;
        }

        private async Task EnsureInitializedAsync()
        {
            if (_initialized) return;

            // Host window for the WebView2 (it needs an HWND to live in). Created off-screen and
            // un-activated so realizing it doesn't flash anything in front of the user — it's only
            // brought on-screen (ShowForLogin) when an interactive sign-in is actually required.
            _browserWindow = new Window
            {
                Title = "LootPulse — Path of Exile Trade Login",
                Width = 1100,
                Height = 800,
                Owner = _owner,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = OffscreenCoord,
                Top = OffscreenCoord,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            _webView = new WebView2();
            _browserWindow.Content = _webView;

            // Closing the login window just hides/parks it so the session/CoreWebView2 stays alive
            // (unless we're actually disposing the transport).
            _browserWindow.Closing += OnBrowserClosing;

            // The WPF WebView2 only finishes initializing once its control is realized in a shown
            // window — so show the window (off-screen) BEFORE awaiting EnsureCoreWebView2Async,
            // otherwise that await never completes and nothing appears to happen.
            _browserWindow.Show();

            Directory.CreateDirectory(UserDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataFolder).ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.Navigate($"{Origin}/trade2");

            _loginPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _loginPollTimer.Tick += OnLoginPollTick;

            // Realized; keep it hidden off-screen until a login is actually needed.
            _browserWindow.Hide();
            _initialized = true;
        }

        private async void OnLoginPollTick(object? sender, EventArgs e)
        {
            if (!_awaitingLogin || _webView?.CoreWebView2 == null || _browserWindow == null)
            {
                _loginPollTimer?.Stop();
                return;
            }

            try
            {
                string raw = await _webView.CoreWebView2.ExecuteScriptAsync(LoginCheckScript);
                if (raw != null && raw.Trim() == "true")
                {
                    _loginPollTimer?.Stop();
                    ParkWindow();              // auto-close the login window on success
                    SetConnected(true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Login poll error: {ex.Message}");
            }
        }

        private async Task EnsureOnOriginAsync()
        {
            // Same-origin fetch requires the page to be on www.pathofexile.com.
            var source = _webView!.CoreWebView2.Source;
            if (!string.IsNullOrEmpty(source) && source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var navDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                _webView!.CoreWebView2.NavigationCompleted -= Handler;
                navDone.TrySetResult(e.IsSuccess);
            }
            _webView.CoreWebView2.NavigationCompleted += Handler;
            _webView.CoreWebView2.Navigate($"{Origin}/trade2");
            await navDone.Task.ConfigureAwait(true);
        }

        private void OnBrowserClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_disposing) return;
            // User dismissed the login window: keep the WebView2 alive, just park it and stop polling.
            e.Cancel = true;
            _loginPollTimer?.Stop();
            ParkWindow();
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string id = root.GetProperty("id").GetString() ?? string.Empty;
                if (!_pending.TryRemove(id, out var tcs)) return;

                int status = root.TryGetProperty("status", out var st) ? st.GetInt32() : 0;
                string body = root.TryGetProperty("body", out var bd) ? (bd.GetString() ?? string.Empty) : string.Empty;

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("headers", out var hd) && hd.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in hd.EnumerateObject())
                    {
                        headers[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }

                tcs.TrySetResult(new TradeHttpResponse
                {
                    StatusCode = (HttpStatusCode)status,
                    Body = body,
                    Headers = headers,
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebView2TradeTransport message parse error: {ex.Message}");
            }
        }

        // Injected script: run fetch in-page (same-origin, carries cookies) and post the result back.
        private static string BuildFetchScript(string requestId, string method, string url, string? jsonBody)
        {
            string urlLit = JsonSerializer.Serialize(url);
            string idLit = JsonSerializer.Serialize(requestId);
            string methodLit = JsonSerializer.Serialize(method);
            string bodyLit = jsonBody is null ? "undefined" : JsonSerializer.Serialize(jsonBody);

            return $@"(async () => {{
  const id = {idLit};
  try {{
    const opts = {{
      method: {methodLit},
      headers: {{ 'Accept': 'application/json', 'Content-Type': 'application/json' }},
      credentials: 'include'
    }};
    const body = {bodyLit};
    if (body !== undefined) opts.body = body;
    const r = await fetch({urlLit}, opts);
    const text = await r.text();
    const headers = {{}};
    r.headers.forEach((v, k) => {{ headers[k] = v; }});
    window.chrome.webview.postMessage(JSON.stringify({{ id, status: r.status, body: text, headers }}));
  }} catch (e) {{
    window.chrome.webview.postMessage(JSON.stringify({{ id, status: 0, body: String(e), headers: {{}} }}));
  }}
}})();";
        }

        public async Task<string> ScrapePageAsync(string url, string scrapeScript, CancellationToken ct = default)
        {
            if (!_owner.Dispatcher.CheckAccess())
            {
                return await _owner.Dispatcher.InvokeAsync(() => ScrapePageAsync(url, scrapeScript, ct)).Task.Unwrap().ConfigureAwait(false);
            }

            await EnsureInitializedAsync().ConfigureAwait(true);

            // Navigate to the target URL
            var navigationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
            handler = (s, e) =>
            {
                _webView!.CoreWebView2.NavigationCompleted -= handler;
                if (e.IsSuccess)
                    navigationTcs.TrySetResult(true);
                else
                    navigationTcs.TrySetException(new InvalidOperationException($"Failed to navigate to {url}: {e.WebErrorStatus}"));
            };
            _webView!.CoreWebView2.NavigationCompleted += handler;
            _webView.CoreWebView2.Navigate(url);

            // Wait for navigation or cancellation
            using var reg = ct.Register(() => navigationTcs.TrySetCanceled());
            await navigationTcs.Task.ConfigureAwait(true);

            // Give the page an extra 800ms to render table elements via JS
            await Task.Delay(800, ct).ConfigureAwait(true);

            // Execute the scraping script
            string result = await _webView.CoreWebView2.ExecuteScriptAsync(scrapeScript).ConfigureAwait(true);

            // Return back to the trade2 origin if we were there, to avoid breaking other logic
            _webView.CoreWebView2.Navigate($"{Origin}/trade2");

            return result;
        }

        public void Dispose()
        {
            try
            {
                _disposing = true;
                if (_loginPollTimer != null)
                {
                    _loginPollTimer.Stop();
                    _loginPollTimer.Tick -= OnLoginPollTick;
                }
                if (_webView?.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                }
                _webView?.Dispose();
                if (_browserWindow != null)
                {
                    _browserWindow.Closing -= OnBrowserClosing;
                    _browserWindow.Close();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebView2TradeTransport dispose error: {ex.Message}");
            }
        }
    }

    /// <summary>Carries the new connected state when the trade session connects or disconnects.</summary>
    public sealed class TradeConnectionChangedEventArgs : EventArgs
    {
        public TradeConnectionChangedEventArgs(bool isConnected) => IsConnected = isConnected;

        public bool IsConnected { get; }
    }
}
