using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LootPulse.Models;

namespace LootPulse.Services.Trade
{
    /// <summary>A recommended build affix paired with the trade2 stat id it resolved to.</summary>
    public sealed record ResolvedAffix(BuildAffix Affix, string StatId, string? GggText = null);

    public sealed record StatInfo(string Id, string Text);

    /// <summary>
    /// Maps a build's human-readable recommended affix text (e.g. "253% increased Physical Damage")
    /// to the trade2 stat IDs that the search "stats" filter needs. Fetches GGG's stat table from
    /// <c>/api/trade2/data/stats</c> once, caches it, and matches by reducing both the build text and
    /// each catalogue entry to a numeric-agnostic template (digits → "#"). Unresolved affixes are
    /// simply dropped so a search still runs.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S1075:URIs should not be hardcoded", Justification = "Path of Exile trade data endpoint is fixed by the service provider.")]
    public sealed class TradeStatResolver(ITradeTransport transport, TradeRateLimiter rateLimiter)
    {
        private const string _statsUrl = "https://www.pathofexile.com/api/trade2/data/stats";

        private readonly ITradeTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        private readonly TradeRateLimiter _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));

        // template text -> stat info. Cached once populated; an empty/failed fetch is retried next time
        // (e.g. the first attempt happened before the trade session was connected).
        private Dictionary<string, StatInfo>? _byTemplate;

        /// <summary>
        /// Resolve the given affixes to their trade2 stat IDs, keeping the originating affix so callers
        /// can weight each stat (Best-in-slot). De-duplicated by stat id. Empty if the table is
        /// unavailable or nothing matched.
        /// </summary>
        public async Task<IReadOnlyList<ResolvedAffix>> ResolveDetailedAsync(
            IReadOnlyList<BuildAffix> affixes, CancellationToken ct = default)
        {
            if (affixes == null || affixes.Count == 0)
            {
                return [];
            }

            var table = await EnsureTableAsync(ct).ConfigureAwait(false);
            if (table.Count == 0)
            {
                return [];
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var resolved = new List<ResolvedAffix>();
            foreach (var affix in affixes)
            {
                if (table.TryGetValue(TradeAffixText.Templatize(affix.Text), out var info) && seen.Add(info.Id))
                {
                    resolved.Add(new ResolvedAffix(affix, info.Id, info.Text));
                }
            }
            return resolved;
        }

        private async Task<Dictionary<string, StatInfo>> EnsureTableAsync(CancellationToken ct)
        {
            // Searches resolve affixes sequentially (awaited in a loop), so a plain field cache is sufficient.
            // A concurrent race would at worst fetch the table twice, which is harmless. We only keep a
            // populated table so a fetch attempted before login can be retried once connected.
            if (_byTemplate is { Count: > 0 })
            {
                return _byTemplate;
            }

            _byTemplate = await FetchTableAsync(ct).ConfigureAwait(false);
            return _byTemplate;
        }

        private async Task<Dictionary<string, StatInfo>> FetchTableAsync(CancellationToken ct)
        {
            var map = new Dictionary<string, StatInfo>(StringComparer.Ordinal);

            // No point spending a rate-limit turn if we can't authenticate the request anyway.
            if (!_transport.IsConnected)
            {
                return map;
            }

            try
            {
                await _rateLimiter.WaitTurnAsync(ct).ConfigureAwait(false);
                var resp = await _transport.SendAsync(HttpMethod.Get, _statsUrl, null, ct).ConfigureAwait(false);
                _rateLimiter.Observe(resp);
                if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body))
                {
                    return map;
                }

                var data = JsonSerializer.Deserialize(resp.Body, Poe2TradeJsonContext.Default.TradeStatsDataResponse);
                if (data?.Result != null)
                {
                    var allEntries = data.Result
                        .Where(c => c?.Entries != null)
                        .SelectMany(c => c!.Entries!)
                        .OrderBy(e => e?.Type == "explicit" ? 0 : 1);

                    foreach (var entry in allEntries)
                    {
                        if (entry?.Id == null || string.IsNullOrWhiteSpace(entry.Text))
                        {
                            continue;
                        }
                        map.TryAdd(TradeAffixText.Templatize(entry.Text), new StatInfo(entry.Id, entry.Text));
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine($"TradeStatResolver fetch error: {ex.Message}");
            }

            return map;
        }
    }
}
