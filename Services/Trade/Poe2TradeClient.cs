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
    /// <summary>How the Trade Market ranks listings for a build item.</summary>
    public enum TradeSearchMode
    {
        /// <summary>League-start: show the genuine cheapest listing that fills the slot.</summary>
        Cheapest,

        /// <summary>Reroll: within a per-item budget, show the best roll by per-slot affix weighting.</summary>
        BestInSlot,
    }

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
        /// Search the trade site for one build item and return a populated group, plus a browser deep link.
        /// Searches at the character's level first; if the recommended affixes push every listing out of
        /// reach, it retries a few levels higher (graduated overshoot) so the player can buy ahead.
        /// <paramref name="mode"/> selects Cheapest (price-ranked) or BestInSlot (weighted, within budget).
        /// </summary>
        public async Task<TradeItemGroup> SearchAsync(
            string league, TradeItemQuery item, int characterLevel, CurrencyRates rates,
            TradeSearchMode mode = TradeSearchMode.Cheapest,
            int minMatchedAffixes = 0, double? budgetDivine = null, int take = 5,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            rates ??= CurrencyRates.Default;

            var group = new TradeItemGroup { ItemLabel = item.Label, MaxLevel = characterLevel };

            // Resolve the build's recommended affixes to trade2 stat IDs. Needed for the Cheapest "count"
            // filter (≥N present) and for the Best-in-slot "weight2" group (rank by per-slot weighting).
            bool needAffixes = item.Affixes.Count > 0 && (mode == TradeSearchMode.BestInSlot || minMatchedAffixes > 0);
            IReadOnlyList<ResolvedAffix> resolved = needAffixes
                ? await _statResolver.ResolveDetailedAsync(item.Affixes, ct).ConfigureAwait(false)
                : Array.Empty<ResolvedAffix>();

            (TradeStatGroup statGroup, TradeSort sort, bool constrains) = BuildStatGroupAndSort(item, mode, resolved, minMatchedAffixes);

            // Best-in-slot budget → the server-side "Buyout Price" filter (in Divine), so the trade-site
            // deep link and the in-app results agree. Cheapest mode is unbudgeted.
            double? buyoutDivine = mode == TradeSearchMode.BestInSlot ? budgetDivine : null;

            // Primary search at the character's level — naturally limits results to affix tiers the
            // character can already equip.
            var search = await RunSearchAsync(league, item, characterLevel, statGroup, sort, buyoutDivine, ct).ConfigureAwait(false);
            int usedCap = characterLevel;

            // Fallback: if requiring those affixes left nothing equippable now, raise the cap a little
            // (levels come fast early, slow late) so the player can pre-buy something usable soon.
            if (search.NoResults && constrains)
            {
                int overshootCap = characterLevel + LevelOvershoot(characterLevel);
                if (overshootCap > characterLevel)
                {
                    var retry = await RunSearchAsync(league, item, overshootCap, statGroup, sort, buyoutDivine, ct).ConfigureAwait(false);
                    if (!retry.NoResults)
                    {
                        search = retry;
                        usedCap = overshootCap;
                    }
                }
            }

            group.MaxLevel = usedCap;

            if (search.Error != null)
            {
                group.StatusText = search.Error;
                return group;
            }
            if (search.NoResults)
            {
                group.StatusText = buyoutDivine is { } b
                    ? $"no listings within budget ({b:0.##} div)"
                    : usedCap > characterLevel ? $"no listings ≤ Lv {usedCap}" : "no listings found";
                return group;
            }

            group.BrowserUrl = $"{Host}/trade2/search/poe2/{Uri.EscapeDataString(league)}/{search.QueryId}";

            // --- FETCH a candidate pool (larger than `take`) so we can re-rank locally. ---
            var ids = search.Hashes!.Take(CandidatePoolSize).ToList();
            await _rateLimiter.WaitTurnAsync(ct).ConfigureAwait(false);
            var fetchUrl = $"{Host}/api/trade2/fetch/{string.Join(",", ids)}?query={search.QueryId}";
            var fetchResp = await _transport.SendAsync(HttpMethod.Get, fetchUrl, null, ct).ConfigureAwait(false);
            _rateLimiter.Observe(fetchResp);

            if (!fetchResp.IsSuccess)
            {
                group.StatusText = DescribeError(fetchResp);
                return group;
            }

            var fetched = Deserialize<TradeFetchResponse>(fetchResp.Body, Poe2TradeJsonContext.Default.TradeFetchResponse);
            var rows = BuildRows(fetched, item, rates);

            IEnumerable<TradeListingRow> ranked;
            if (mode == TradeSearchMode.BestInSlot && constrains)
            {
                // Best-in-slot: the server already filtered to within budget and ranked by the
                // weighted-sum (weight2) group, so keep that order as-is.
                ranked = rows;
            }
            else
            {
                // Cheapest (or BIS with nothing to weight): genuine cheapest across currencies
                // (unvalued currencies sort last). Budget, if any, was already applied server-side.
                ranked = rows.OrderBy(r => r.NormalizedExalted ?? double.MaxValue);
            }

            foreach (var row in ranked.Take(take))
            {
                group.Listings.Add(row);
            }

            if (group.Listings.Count == 0)
            {
                group.StatusText = "no listings found";
            }
            else
            {
                // Buy-ahead outline: the shown listings can't be equipped at the character's current level.
                int? minReq = group.Listings.Where(r => r.RequiredLevel.HasValue).Min(r => r.RequiredLevel);
                group.IsAboveCharacterLevel = minReq is { } req && req > characterLevel;
            }
            return group;
        }

        // One search request at a given level cap; returns the query id + result hashes (or an error).
        private async Task<SearchOutcome> RunSearchAsync(
            string league, TradeItemQuery item, int levelCap,
            TradeStatGroup statGroup, TradeSort sort, double? buyoutDivine, CancellationToken ct)
        {
            var requestBody = BuildSearchBody(item, levelCap, statGroup, sort, buyoutDivine);
            string json = JsonSerializer.Serialize(requestBody, Poe2TradeJsonContext.Default.TradeSearchRequest);

            // The API path has NO realm segment: "trade2" (vs PoE1 "trade") is what selects PoE2.
            await _rateLimiter.WaitTurnAsync(ct).ConfigureAwait(false);
            var searchUrl = $"{Host}/api/trade2/search/{Uri.EscapeDataString(league)}";
            var resp = await _transport.SendAsync(HttpMethod.Post, searchUrl, json, ct).ConfigureAwait(false);
            _rateLimiter.Observe(resp);

            if (!resp.IsSuccess)
            {
                return new SearchOutcome { Error = DescribeError(resp) };
            }

            var search = Deserialize<TradeSearchResponse>(resp.Body, Poe2TradeJsonContext.Default.TradeSearchResponse);
            if (search?.Id == null || search.Result == null || search.Result.Count == 0)
            {
                return new SearchOutcome { NoResults = true };
            }
            return new SearchOutcome { QueryId = search.Id, Hashes = search.Result };
        }

        private static List<TradeListingRow> BuildRows(TradeFetchResponse? fetched, TradeItemQuery item, CurrencyRates rates)
        {
            var rows = new List<TradeListingRow>();
            if (fetched?.Result == null)
            {
                return rows;
            }

            foreach (var entry in fetched.Result.Where(e => e?.Listing?.Price != null))
            {
                var price = entry!.Listing!.Price!;
                double? exalted = rates.ToExalted(price.Amount, price.Currency);
                var listingMods = CombineMods(entry.Item);
                rows.Add(new TradeListingRow
                {
                    PriceText = FormatPrice(price),
                    NormalizedExalted = exalted,
                    NormalizedText = rates.Format(exalted),
                    Seller = entry.Listing.Account?.Name ?? "?",
                    ItemLabel = entry.Item?.Name is { Length: > 0 } n
                        ? $"{n} {entry.Item?.TypeLine ?? entry.Item?.BaseType}".Trim()
                        : entry.Item?.TypeLine ?? entry.Item?.BaseType ?? string.Empty,
                    RequiredLevel = ParseRequiredLevel(entry.Item),
                    Score = BisWeighting.ScoreListing(item.SlotId, item.Affixes, listingMods),
                });
            }
            return rows;
        }

        private static IEnumerable<string> CombineMods(TradeFetchItem? item)
        {
            foreach (var text in ModTexts(item?.ExplicitMods)) yield return text;
            foreach (var text in ModTexts(item?.ImplicitMods)) yield return text;
        }

        // A mod entry is either a plain string (implicitMods) or an object with a "description"
        // (explicitMods) — GGG mixes both. Pull the display text out of whichever shape it is.
        private static IEnumerable<string> ModTexts(List<JsonElement>? mods)
        {
            if (mods == null) yield break;
            foreach (var el in mods)
            {
                string? text = el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString(),
                    JsonValueKind.Object => el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString()
                        : null,
                    _ => null,
                };
                if (text is { Length: > 0 }) yield return text;
            }
        }

        // Read the "Level" entry from the listing's requirements (values like [["65", 0]]).
        private static int? ParseRequiredLevel(TradeFetchItem? item)
        {
            if (item?.Requirements == null) return null;
            foreach (var req in item.Requirements)
            {
                if (!string.Equals(req?.Name, "Level", StringComparison.OrdinalIgnoreCase)) continue;
                var first = req!.Values?.Count > 0 ? req.Values[0] : null;
                if (first == null || first.Count == 0) return null;
                var v = first[0];
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
                return null;
            }
            return null;
        }

        // Levels come fast early and slowly late, so allow more overshoot when low-level.
        private static int LevelOvershoot(int characterLevel) =>
            characterLevel <= 70 ? 3 : characterLevel <= 85 ? 2 : 1;

        private sealed class SearchOutcome
        {
            public string? QueryId { get; init; }
            public List<string>? Hashes { get; init; }
            public bool NoResults { get; init; }
            public string? Error { get; init; }
        }

        // Choose the stats group + sort for the request based on mode. Returns whether the group
        // constrains results (so the caller knows an empty result is worth a level-overshoot retry).
        private static (TradeStatGroup Group, TradeSort Sort, bool Constrains) BuildStatGroupAndSort(
            TradeItemQuery item, TradeSearchMode mode, IReadOnlyList<ResolvedAffix> resolved, int minMatchedAffixes)
        {
            if (mode == TradeSearchMode.BestInSlot && resolved.Count > 0)
            {
                // Weight each recommended affix's stat by its per-slot priority, and let GGG rank by the
                // normalized weighted sum (weight2). value.min = 1 → must carry at least one of them.
                var weighted = resolved
                    .Select(r => new TradeStatFilter(
                        r.StatId,
                        new TradeMinMax(Weight: BisWeighting.Weight(item.SlotId, BisWeighting.Categorize(r.Affix.Text)))))
                    .ToArray();
                var group = new TradeStatGroup("weight2", weighted, new TradeMinMax(Min: 1));
                return (group, new TradeSort(StatGroup0: "desc"), true);
            }

            // Cheapest (or BIS with nothing resolved): require ≥N recommended affixes present, sort price.
            int matchMin = Math.Min(minMatchedAffixes, resolved.Count);
            TradeStatGroup countGroup = matchMin > 0
                ? new TradeStatGroup("count", resolved.Select(r => new TradeStatFilter(r.StatId)).ToArray(), new TradeMinMax(Min: matchMin))
                : new TradeStatGroup("and", Array.Empty<TradeStatFilter>());
            return (countGroup, new TradeSort(Price: "asc"), matchMin > 0);
        }

        private static TradeSearchRequest BuildSearchBody(
            TradeItemQuery item, int maxLevel, TradeStatGroup statGroup, TradeSort sort, double? buyoutDivine)
        {
            // Base-type searches must exclude uniques (which share base-type names with rares/normals)
            // via the "Any Non-Unique" rarity filter. Unique-name searches stay unfiltered.
            bool isBaseSearch = string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.BaseType);
            TradeTypeFilters? typeFilters = isBaseSearch
                ? new TradeTypeFilters(new TradeTypeFilterValues(Rarity: new TradeOption("nonunique")))
                : null;

            // Budget → the site's "Buyout Price" filter (max, in Divine). Verified live on trade2.
            TradeBuyoutFilters? buyoutFilters = buyoutDivine is { } b
                ? new TradeBuyoutFilters(new TradeBuyoutFilterValues(new TradePriceFilter(Max: b, Option: "divine")))
                : null;

            var query = new TradeQuery(
                Status: new TradeStatus("online"),
                Stats: new[] { statGroup },
                Filters: new TradeFilters(
                    TypeFilters: typeFilters,
                    // Only items the character can equip at this cap (raised a little for buy-ahead).
                    ReqFilters: new TradeReqFilters(new TradeReqFilterValues(new TradeMinMax(Max: maxLevel))),
                    BuyoutFilters: buyoutFilters),
                Name: string.IsNullOrWhiteSpace(item.Name) ? null : item.Name,
                Type: string.IsNullOrWhiteSpace(item.BaseType) ? null : item.BaseType
            );
            return new TradeSearchRequest(query, sort);
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
