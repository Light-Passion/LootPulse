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

        private readonly ITradeTransport _transport;
        private readonly TradeRateLimiter _rateLimiter;

        public Poe2TradeClient(ITradeTransport transport, TradeRateLimiter rateLimiter)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        }

        /// <summary>
        /// Search the trade site for one build item and return a populated group (cheapest <paramref name="take"/>
        /// listings whose required level is ≤ <paramref name="maxLevel"/>), plus a browser deep link.
        /// </summary>
        public async Task<TradeItemGroup> SearchCheapestAsync(
            string league, TradeItemQuery item, int maxLevel, int take = 5, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(item);

            var group = new TradeItemGroup { ItemLabel = item.Label, MaxLevel = maxLevel };

            var requestBody = BuildSearchBody(item, maxLevel);
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
                group.StatusText = $"no listings ≤ Lv {maxLevel}";
                return group;
            }

            // Browser deep link to the identical search (web UI uses the realm-qualified path).
            group.BrowserUrl = $"{Host}/trade2/search/poe2/{Uri.EscapeDataString(league)}/{search.Id}";

            // --- FETCH (top N hashes; results already sorted cheapest-first by the search) ---
            var ids = search.Result.Take(take).ToList();
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
                foreach (var entry in fetched.Result.Where(e => e?.Listing?.Price != null))
                {
                    group.Listings.Add(new TradeListingRow
                    {
                        PriceText = FormatPrice(entry!.Listing!.Price!),
                        Seller = entry.Listing.Account?.Name ?? "?",
                        ItemLabel = entry.Item?.Name is { Length: > 0 } n
                            ? $"{n} {entry.Item?.TypeLine ?? entry.Item?.BaseType}".Trim()
                            : entry.Item?.TypeLine ?? entry.Item?.BaseType ?? string.Empty,
                    });
                }
            }

            if (group.Listings.Count == 0)
            {
                group.StatusText = $"no listings ≤ Lv {maxLevel}";
            }
            return group;
        }

        private static TradeSearchRequest BuildSearchBody(TradeItemQuery item, int maxLevel)
        {
            var query = new TradeQuery(
                Status: new TradeStatus("online"),
                Stats: new[] { new TradeStatGroup("and", Array.Empty<object>()) },
                // The requested feature: only items the character can currently equip.
                Filters: new TradeFilters(
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
