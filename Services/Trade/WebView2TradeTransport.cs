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

        // Logged in if the page header carries a logout control (present on every pathofexile.com page).
        private const string LoginCheckScript =
            "(function(){try{return !!document.querySelector('form[action=\"/logout\"], a[href=\"/logout\"]');}catch(e){return false;}})()";

        // Pending fetch() calls keyed by request id; resolved when the page posts the result back.
        private readonly ConcurrentDictionary<string, TaskCompletionSource<TradeHttpResponse>> _pending = new();

        public WebView2TradeTransport(Window owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public bool IsConnected { get; private set; }

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

            // Show the browser so the user can sign in, then navigate to the trade page.
            _browserWindow!.Show();
            _browserWindow.Activate();
            _webView!.CoreWebView2.Navigate($"{Origin}/trade2");

            // Auto-close the login window as soon as we detect a logged-in session.
            _loginPollTimer!.Start();

            // We can't reliably auto-detect login from outside the page, so connection is confirmed
            // lazily: the first successful (200) API call flips IsConnected. We optimistically set it
            // here so the user can run a search; a 401/403 will surface a "please log in" message.
            IsConnected = true;
            return true;
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
                IsConnected = true;
            }
            return response;
        }

        private async Task EnsureInitializedAsync()
        {
            if (_initialized) return;

            // Create a hidden host window for the WebView2 (it needs an HWND to live in).
            _browserWindow = new Window
            {
                Title = "LootPulse — Path of Exile Trade Login",
                Width = 1100,
                Height = 800,
                Owner = _owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = true,
            };
            _webView = new WebView2();
            _browserWindow.Content = _webView;

            // Closing the login window just hides it so the session/CoreWebView2 stays alive
            // (unless we're actually disposing the transport).
            _browserWindow.Closing += OnBrowserClosing;

            // The WPF WebView2 only finishes initializing once its control is realized in a shown
            // window — so show the window BEFORE awaiting EnsureCoreWebView2Async, otherwise that
            // await never completes and nothing appears to happen.
            _browserWindow.Show();
            _browserWindow.Activate();

            Directory.CreateDirectory(UserDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataFolder).ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.Navigate($"{Origin}/trade2");

            _loginPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _loginPollTimer.Tick += OnLoginPollTick;

            _initialized = true;
        }

        private async void OnLoginPollTick(object? sender, EventArgs e)
        {
            if (_webView?.CoreWebView2 == null || _browserWindow == null || !_browserWindow.IsVisible)
            {
                _loginPollTimer?.Stop();
                return;
            }

            try
            {
                string raw = await _webView.CoreWebView2.ExecuteScriptAsync(LoginCheckScript);
                if (raw != null && raw.Trim() == "true")
                {
                    IsConnected = true;
                    _loginPollTimer?.Stop();
                    _browserWindow.Hide();
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
            e.Cancel = true;
            _browserWindow!.Hide();
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
}
