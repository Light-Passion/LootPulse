using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LootPulse.Services.Trade
{
    /// <summary>
    /// PLACEHOLDER for a future GGG-OAuth-based transport. Not wired up — kept so the auth strategy is
    /// swappable behind <see cref="ITradeTransport"/> once an OAuth application is approved by GGG.
    ///
    /// IMPORTANT CAVEAT (verified against https://www.pathofexile.com/developer/docs/reference,
    /// June 2026): the official OAuth API has **no trade/market listing scope or endpoint** for PoE2.
    /// The live <c>/api/trade2/search</c> + <c>/api/trade2/fetch</c> endpoints this app needs are part
    /// of the website (browser-session) surface, not the scoped developer API. Therefore OAuth alone
    /// CANNOT power the Trade Market tab today — <see cref="WebView2TradeTransport"/> remains required
    /// for trade search. OAuth would instead enable adjacent features:
    ///   - account:characters  -> GET /character/poe2/&lt;name&gt;  (equipped items + current level,
    ///                            a cleaner source than Client.txt log parsing for the level filter)
    ///   - account:item_filter  -> GET/POST/PATCH /item-filter?realm=poe2  (push generated filters)
    ///   - service:cxapi        -> GET /currency-exchange/poe2            (currency exchange digests)
    ///
    /// OAuth 2.1 reference (https://www.pathofexile.com/developer/docs/authorization):
    ///   Authorize : https://www.pathofexile.com/oauth/authorize
    ///   Token     : https://www.pathofexile.com/oauth/token
    ///   Flow      : Authorization Code + PKCE (public client) — code_challenge_method=S256,
    ///               redirect_uri = http://127.0.0.1:&lt;port&gt;/ (loopback), response_type=code.
    ///   Token POST: application/x-www-form-urlencoded with client_id, grant_type=authorization_code,
    ///               code, redirect_uri, scope, code_verifier. Refresh via grant_type=refresh_token.
    ///   Headers   : Authorization: Bearer &lt;access_token&gt; on API calls, and a descriptive
    ///               User-Agent of the form "OAuth &lt;clientId&gt;/&lt;version&gt; (contact: &lt;email&gt;)".
    /// </summary>
    public sealed class OAuthTradeTransport : ITradeTransport
    {
        // TODO: GGG OAuth — populate from a registered application once approved.
        private readonly string _clientId;
        private readonly string? _accessToken;

        public OAuthTradeTransport(string clientId, string? accessToken = null)
        {
            _clientId = clientId;
            _accessToken = accessToken;
        }

        public bool IsConnected => false;

        public Task<bool> ConnectAsync(CancellationToken ct = default) =>
            throw new NotSupportedException(
                "OAuthTradeTransport is a placeholder. GGG OAuth has no trade2 scope; use WebView2TradeTransport " +
                "for trade search. See class remarks for the OAuth flow to implement for adjacent (account/cx) APIs.");

        public Task<TradeHttpResponse> SendAsync(HttpMethod method, string url, string? jsonBody, CancellationToken ct = default) =>
            throw new NotSupportedException(
                "OAuthTradeTransport is a placeholder. GGG OAuth cannot authorize /api/trade2 requests.");
    }
}
