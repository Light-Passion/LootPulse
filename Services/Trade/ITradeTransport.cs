using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LootPulse.Services.Trade
{
    /// <summary>
    /// Abstraction over "how an authenticated HTTP request reaches the pathofexile.com trade2 API".
    /// The PoC implementation (<see cref="WebView2TradeTransport"/>) proxies requests through a
    /// logged-in embedded browser session; a future <see cref="OAuthTradeTransport"/> can replace it
    /// behind this same interface without touching <see cref="Poe2TradeClient"/>.
    /// </summary>
    public interface ITradeTransport
    {
        /// <summary>True once the transport has a usable authenticated session.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Bring up whatever interactive auth the transport needs (e.g. show a login browser).
        /// Returns true if a session is ready afterwards.
        /// </summary>
        Task<bool> ConnectAsync(CancellationToken ct = default);

        /// <summary>Issue an authenticated request and return the raw response (status + body + headers).</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "URL is forwarded verbatim to an in-browser fetch() call; a string keeps the transport boundary simple.")]
        Task<TradeHttpResponse> SendAsync(HttpMethod method, string url, string? jsonBody, CancellationToken ct = default);
    }

    /// <summary>Transport-agnostic response carrying just what the client and rate limiter need.</summary>
    public sealed class TradeHttpResponse
    {
        public HttpStatusCode StatusCode { get; init; }
        public string Body { get; init; } = string.Empty;

        /// <summary>Response headers (case-insensitive, so the rate limiter can read X-Rate-Limit-*).</summary>
        public IReadOnlyDictionary<string, string> Headers { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool IsSuccess => (int)StatusCode >= 200 && (int)StatusCode < 300;

        public string? GetHeader(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            return Headers.TryGetValue(name, out var v) ? v : null;
        }
    }
}
