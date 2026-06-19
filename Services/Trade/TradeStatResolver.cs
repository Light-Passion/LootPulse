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
    /// <summary>
    /// Maps a build's human-readable recommended affix text (e.g. "253% increased Physical Damage")
    /// to the trade2 stat IDs that the search "stats" filter needs. Fetches GGG's stat table from
    /// <c>/api/trade2/data/stats</c> once, caches it, and matches by reducing both the build text and
    /// each catalogue entry to a numeric-agnostic template (digits → "#"). Unresolved affixes are
    /// simply dropped so a search still runs.
    /// </summary>
    public sealed class TradeStatResolver
    {
        private const string StatsUrl = "https://www.pathofexile.com/api/trade2/data/stats";

        private readonly ITradeTransport _transport;
        private readonly TradeRateLimiter _rateLimiter;

        // template text -> stat id. Cached once populated; an empty/failed fetch is retried next time
        // (e.g. the first attempt happened before the trade session was connected).
        private Dictionary<string, string>? _byTemplate;

        public TradeStatResolver(ITradeTransport transport, TradeRateLimiter rateLimiter)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        }

        /// <summary>
        /// Resolve the given affixes to presence-only stat filters (id only). Returns an empty list if
        /// the stat table is unavailable or nothing matched.
        /// </summary>
        public async Task<IReadOnlyList<TradeStatFilter>> ResolveAsync(
            IReadOnlyList<BuildAffix> affixes, CancellationToken ct = default)
        {
            if (affixes == null || affixes.Count == 0)
            {
                return Array.Empty<TradeStatFilter>();
            }

            var table = await EnsureTableAsync(ct).ConfigureAwait(false);
            if (table.Count == 0)
            {
                return Array.Empty<TradeStatFilter>();
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var filters = new List<TradeStatFilter>();
            foreach (var affix in affixes)
            {
                if (table.TryGetValue(TradeAffixText.Templatize(affix.Text), out var id) && seen.Add(id))
                {
                    filters.Add(new TradeStatFilter(id));
                }
            }
            return filters;
        }

        private async Task<Dictionary<string, string>> EnsureTableAsync(CancellationToken ct)
        {
            // Searches resolve affixes sequentially (awaited in a loop), so a plain field cache is enough;
            // a concurrent race would at worst fetch the table twice, which is harmless. We only keep a
            // populated table so a fetch attempted before login can be retried once connected.
            if (_byTemplate is { Count: > 0 })
            {
                return _byTemplate;
            }

            _byTemplate = await FetchTableAsync(ct).ConfigureAwait(false);
            return _byTemplate;
        }

        private async Task<Dictionary<string, string>> FetchTableAsync(CancellationToken ct)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            // No point spending a rate-limit turn if we can't authenticate the request anyway.
            if (!_transport.IsConnected)
            {
                return map;
            }

            try
            {
                await _rateLimiter.WaitTurnAsync(ct).ConfigureAwait(false);
                var resp = await _transport.SendAsync(HttpMethod.Get, StatsUrl, null, ct).ConfigureAwait(false);
                _rateLimiter.Observe(resp);
                if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body))
                {
                    return map;
                }

                var data = JsonSerializer.Deserialize(resp.Body, Poe2TradeJsonContext.Default.TradeStatsDataResponse);
                if (data?.Result == null)
                {
                    return map;
                }

                foreach (var category in data.Result)
                {
                    if (category?.Entries == null)
                    {
                        continue;
                    }
                    // Prefer "explicit" mods: a recommended build affix is almost always an explicit roll,
                    // and several categories (implicit/rune) can share the same text. First write wins per
                    // template, and we iterate explicit-first to make that the winner.
                    foreach (var entry in category.Entries.OrderBy(e => e?.Type == "explicit" ? 0 : 1))
                    {
                        if (entry?.Id == null || string.IsNullOrWhiteSpace(entry.Text))
                        {
                            continue;
                        }
                        map.TryAdd(TradeAffixText.Templatize(entry.Text), entry.Id);
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
