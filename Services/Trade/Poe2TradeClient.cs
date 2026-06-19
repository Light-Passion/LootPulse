using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LootPulse.Models;

namespace LootPulse.Services.Trade
{
    /// <summary>
    /// Talks to GGG's official PoE2 trade API (pathofexile.com/api/trade2) through an
    /// <see cref="ITradeTransport"/>. Builds the search body, applies the character-level requirement
    /// filter, sorts cheapest-first, and fetches the top listings. All requests pass through a shared
    /// <see cref="TradeRateLimiter"/> so we stay within GGG's limits.
    /// </summary>
    public sealed class Poe2TradeClient
    {
        private const string Host = "https://www.pathofexile.com";

        // Fetch a few more than we show so cross-currency normalization can pick the true cheapest
        // (a single fetch call covers up to 10 hashes).
        private const int CandidatePoolSize = 10;

        private readonly ITradeTransport _transport;
        private readonly TradeRateLimiter _rateLimiter;
        private readonly TradeStatResolver _statResolver;

        public Poe2TradeClient(ITradeTransport transport, TradeRateLimiter rateLimiter)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
            _statResolver = new TradeStatResolver(_transport, _rateLimiter);
        }

        /// <summary>
        /// Search the trade site for one build item and return a populated group (cheapest <paramref name="take"/>
        /// listings whose required level is ≤ <paramref name="maxLevel"/>), plus a browser deep link.
        /// </summary>
        public async Task<TradeItemGroup> SearchCheapestAsync(
            string league, TradeItemQuery item, int characterLevel, CurrencyRates rates,
            int minMatchedAffixes = 0, int take = 5, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            rates ??= CurrencyRates.Default;

            // Buy-ahead: if the build wants this item before the character can equip it, search at the
            // item's own minimum level so it still shows up — and flag it so the UI can outline it red.
            bool aboveLevel = item.MinRequiredLevel is { } min && min > characterLevel;
            int effectiveLevel = Math.Max(characterLevel, item.MinRequiredLevel ?? 0);

            var group = new TradeItemGroup
            {
                ItemLabel = item.Label,
                MaxLevel = effectiveLevel,
                IsAboveCharacterLevel = aboveLevel,
            };

            // Resolve the build's recommended affixes to stat IDs (presence-only); the count group then
            // requires at least the user-selected number of them to be present on a listing.
            IReadOnlyList<TradeStatFilter> statFilters = item.Affixes.Count > 0 && minMatchedAffixes > 0
                ? await _statResolver.ResolveAsync(item.Affixes, ct).ConfigureAwait(false)
                : Array.Empty<TradeStatFilter>();

            var requestBody = BuildSearchBody(item, effectiveLevel, statFilters, minMatchedAffixes);
            string json = JsonSerializer.Serialize(requestBody, Poe2TradeJsonContext.Default.TradeSearchRequest);

            // --- SEARCH ---
            // The API path has NO realm segment: "trade2" (vs PoE1 "trade") is what selects PoE2, per
            // the working Exiled-Exchange-2 client. Only the *web UI* URL carries the "poe2/" segment
            // (see BrowserUrl below). Confirmed against EE2 renderer/.../trade/pathofexile-trade.ts.
            await _rateLimiter.WaitTurnAsync(ct).ConfigureAwait(false);
            var searchUrl = $"{Host}/api/trade2/search/{Uri.EscapeDataString(league)}";
            var searchResp = await _transport.SendAsync(HttpMethod.Post, searchUrl, json, ct).ConfigureAwait(false);
            _rateLimiter.Observe(searchResp);

            if (!searchResp.IsSuccess)
            {
                group.StatusText = DescribeError(searchResp);
                return group;
            }

            var search = Deserialize<TradeSearchResponse>(searchResp.Body, Poe2TradeJsonContext.Default.TradeSearchResponse);
            if (search?.Id == null || search.Result == null || search.Result.Count == 0)
            {
                group.StatusText = $"no listings ≤ Lv {effectiveLevel}";
                return group;
            }

            // Browser deep link to the identical search (web UI uses the realm-qualified path).
            group.BrowserUrl = $"{Host}/trade2/search/poe2/{Uri.EscapeDataString(league)}/{search.Id}";

            // --- FETCH ---
            // Pull a candidate pool (a bit larger than `take`) so we can re-pick the cheapest *after*
            // normalizing mixed currencies — the server's price.asc sort can rank a 1-divine listing
            // below a 30-exalted one even when divine is worth more chaos.
            var ids = search.Result.Take(Math.Max(take, CandidatePoolSize)).ToList();
            await _rateLimiter.WaitTurnAsync(ct).ConfigureAwait(false);
            var fetchUrl = $"{Host}/api/trade2/fetch/{string.Join(",", ids)}?query={search.Id}";
            var fetchResp = await _transport.SendAsync(HttpMethod.Get, fetchUrl, null, ct).ConfigureAwait(false);
            _rateLimiter.Observe(fetchResp);

            if (!fetchResp.IsSuccess)
            {
                group.StatusText = DescribeError(fetchResp);
                return group;
            }

            var fetched = Deserialize<TradeFetchResponse>(fetchResp.Body, Poe2TradeJsonContext.Default.TradeFetchResponse);
            if (fetched?.Result != null)
            {
                var rows = new List<TradeListingRow>();
                foreach (var entry in fetched.Result.Where(e => e?.Listing?.Price != null))
                {
                    var price = entry!.Listing!.Price!;
                    double? exalted = rates.ToExalted(price.Amount, price.Currency);
                    rows.Add(new TradeListingRow
                    {
                        PriceText = FormatPrice(price),
                        NormalizedExalted = exalted,
                        NormalizedText = rates.Format(exalted),
                        Seller = entry.Listing.Account?.Name ?? "?",
                        ItemLabel = entry.Item?.Name is { Length: > 0 } n
                            ? $"{n} {entry.Item?.TypeLine ?? entry.Item?.BaseType}".Trim()
                            : entry.Item?.TypeLine ?? entry.Item?.BaseType ?? string.Empty,
                    });
                }

                // Re-sort by the Exalted-equivalent value so the shown rows are the genuine cheapest
                // across currencies; listings in a currency we don't value (null) fall to the end.
                foreach (var row in rows
                    .OrderBy(r => r.NormalizedExalted ?? double.MaxValue)
                    .Take(take))
                {
                    group.Listings.Add(row);
                }
            }

            if (group.Listings.Count == 0)
            {
                group.StatusText = $"no listings ≤ Lv {effectiveLevel}";
            }
            return group;
        }

        private static TradeSearchRequest BuildSearchBody(
            TradeItemQuery item, int maxLevel, IReadOnlyList<TradeStatFilter> statFilters, int minMatchedAffixes)
        {
            // Base-type searches must exclude uniques (which share base-type names with rares/normals)
            // via the "Any Non-Unique" rarity filter. Unique-name searches stay unfiltered.
            bool isBaseSearch = string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.BaseType);
            TradeTypeFilters? typeFilters = isBaseSearch
                ? new TradeTypeFilters(new TradeTypeFilterValues(Rarity: new TradeOption("nonunique")))
                : null;

            // When the build supplies recommended affixes, require at least N of them to be present
            // (N clamped to how many actually resolved). Otherwise send the empty "and" group as before.
            int matchMin = Math.Min(minMatchedAffixes, statFilters.Count);
            TradeStatGroup statGroup = matchMin > 0
                ? new TradeStatGroup("count", statFilters, new TradeMinMax(Min: matchMin))
                : new TradeStatGroup("and", Array.Empty<TradeStatFilter>());

            var query = new TradeQuery(
                Status: new TradeStatus("online"),
                Stats: new[] { statGroup },
                Filters: new TradeFilters(
                    TypeFilters: typeFilters,
                    // The requested feature: only items the character can currently equip.
                    ReqFilters: new TradeReqFilters(new TradeReqFilterValues(new TradeMinMax(Max: maxLevel)))),
                Name: string.IsNullOrWhiteSpace(item.Name) ? null : item.Name,
                Type: string.IsNullOrWhiteSpace(item.BaseType) ? null : item.BaseType
            );
            return new TradeSearchRequest(query, new TradeSort("asc"));
        }

        private static string FormatPrice(TradePrice price)
        {
            // Trade amounts can be fractional (e.g. 0.5 divine).
            string amount = price.Amount % 1 == 0
                ? price.Amount.ToString("0", CultureInfo.InvariantCulture)
                : price.Amount.ToString("0.##", CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(price.Currency) ? amount : $"{amount} {price.Currency}";
        }

        private static string DescribeError(TradeHttpResponse resp) => (int)resp.StatusCode switch
        {
            401 or 403 => "not logged in — click Connect Trade Account",
            429 => "rate limited — please wait",
            0 => "network/browser error",
            _ => $"error {(int)resp.StatusCode}",
        };

        private static T? Deserialize<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
            where T : class
        {
            try
            {
                if (string.IsNullOrWhiteSpace(body)) return null;
                return JsonSerializer.Deserialize(body, typeInfo);
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Poe2TradeClient deserialize error: {ex.Message}");
                return null;
            }
        }
    }
}
