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
    public sealed record TradeSearchOptions(
        TradeSearchMode Mode = TradeSearchMode.Cheapest,
        int MinMatchedAffixes = 0,
        double? BudgetDivine = null,
        int Take = 5
    );

    /// <summary>
    /// Talks to GGG's official PoE2 trade API (pathofexile.com/api/trade2) through an
    /// <see cref="ITradeTransport"/>. Builds the search body, applies the character-level requirement
    /// filter, sorts cheapest-first, and fetches the top listings. All requests pass through a shared
    /// <see cref="TradeRateLimiter"/> so we stay within GGG's limits.
    /// </summary>
    public sealed class Poe2TradeClient
    {
        private const string _host = "https://www.pathofexile.com";

        // Fetch a few more than we show so cross-currency normalization can pick the true cheapest
        // (a single fetch call covers up to 10 hashes).
        private const int _candidatePoolSize = 10;

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
        /// </summary>
        private sealed record SearchContext(
            string League,
            TradeItemQuery Item,
            int CharacterLevel,
            TradeSearchOptions Options,
            CancellationToken Ct
        );

        private sealed record FetchOutcome(
            IReadOnlyList<TradeListingRow> Listings,
            string? Error = null
        );

        /// <summary>
        /// Search the trade site for one build item and return a populated group, plus a browser deep link.
        /// Searches at the character's level first; if the recommended affixes push every listing out of
        /// reach, it retries a few levels higher (graduated overshoot) so the player can buy ahead.
        /// </summary>
        public async Task<TradeItemGroup> SearchAsync(
            string league, TradeItemQuery item, int characterLevel, CurrencyRates rates,
            TradeSearchOptions options, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(options);
            rates ??= CurrencyRates.Default;

            var group = new TradeItemGroup { ItemLabel = item.Label, MaxLevel = characterLevel };
            var context = new SearchContext(league, item, characterLevel, options, ct);

            var resolved = await ResolveAffixesForSearchAsync(item, options, ct).ConfigureAwait(false);
            (TradeStatGroup statGroup, TradeSort sort, bool constrains) = BuildStatGroupAndSort(item, options.Mode, resolved, options.MinMatchedAffixes);

            var (search, usedCap) = await ExecuteSearchWithRetryAsync(context, statGroup, sort, constrains).ConfigureAwait(false);

            group.MaxLevel = usedCap ?? characterLevel;

            if (search.Error != null)
            {
                group.StatusText = search.Error;
                return group;
            }
            if (search.NoResults)
            {
                double? buyoutDivine = options.Mode == TradeSearchMode.BestInSlot ? options.BudgetDivine : null;
                if (buyoutDivine is { } b)
                {
                    group.StatusText = $"no listings within budget ({b:0.##} div)";
                }
                else if (usedCap.HasValue && usedCap.Value > characterLevel)
                {
                    group.StatusText = $"no listings ≤ Lv {usedCap.Value}";
                }
                else
                {
                    group.StatusText = "no listings found";
                }
                return group;
            }

            group.BrowserUrl = $"{_host}/trade2/search/poe2/{Uri.EscapeDataString(league)}/{search.QueryId}";

            var fetchResult = await FetchAndRankListingsAsync(context, search.QueryId!, search.Hashes ?? [], rates, constrains).ConfigureAwait(false);

            if (fetchResult.Error != null)
            {
                group.StatusText = fetchResult.Error;
                return group;
            }

            group.Listings.AddRange(fetchResult.Listings.Take(options.Take));

            if (group.Listings.Count == 0)
            {
                group.StatusText = "no listings found";
            }
            else
            {
                int? minReq = group.Listings.Where(r => r.RequiredLevel.HasValue).Min(r => r.RequiredLevel);
                group.IsAboveCharacterLevel = minReq is { } req && req > characterLevel;
                if (!usedCap.HasValue && minReq.HasValue)
                {
                    group.MaxLevel = minReq.Value;
                }
            }

            return group;
        }

        private async Task<IReadOnlyList<ResolvedAffix>> ResolveAffixesForSearchAsync(
            TradeItemQuery item, TradeSearchOptions options, CancellationToken ct)
        {
            bool needAffixes = item.Affixes.Count > 0 && (options.Mode == TradeSearchMode.BestInSlot || options.MinMatchedAffixes > 0);
            if (!needAffixes)
            {
                return [];
            }
            return await _statResolver.ResolveDetailedAsync(item.Affixes, ct).ConfigureAwait(false);
        }

        private async Task<(SearchOutcome Search, int? UsedCap)> ExecuteSearchWithRetryAsync(
            SearchContext context, TradeStatGroup statGroup, TradeSort sort, bool constrains)
        {
            double? buyoutDivine = context.Options.Mode == TradeSearchMode.BestInSlot ? context.Options.BudgetDivine : null;
            var search = await RunSearchAsync(context.League, context.Item, context.CharacterLevel, statGroup, sort, buyoutDivine, context.Ct).ConfigureAwait(false);
            int? usedCap = context.CharacterLevel;

            if (search.NoResults)
            {
                if (constrains)
                {
                    int overshootCap = context.CharacterLevel + LevelOvershoot(context.CharacterLevel);
                    if (overshootCap > context.CharacterLevel)
                    {
                        var retry = await RunSearchAsync(context.League, context.Item, overshootCap, statGroup, sort, buyoutDivine, context.Ct).ConfigureAwait(false);
                        if (!retry.NoResults)
                        {
                            search = retry;
                            usedCap = overshootCap;
                        }
                    }
                }

                if (search.NoResults)
                {
                    var retryNoCap = await RunSearchAsync(context.League, context.Item, null, statGroup, sort, buyoutDivine, context.Ct).ConfigureAwait(false);
                    if (!retryNoCap.NoResults)
                    {
                        search = retryNoCap;
                        usedCap = null;
                    }
                }
            }

            return (search, usedCap);
        }

        private async Task<FetchOutcome> FetchAndRankListingsAsync(
            SearchContext context, string queryId, IReadOnlyList<string> resultHashes,
            CurrencyRates rates, bool constrains)
        {
            var ids = resultHashes.Take(_candidatePoolSize).ToList();
            if (ids.Count == 0)
            {
                return new FetchOutcome([]);
            }

            await _rateLimiter.WaitTurnAsync(context.Ct).ConfigureAwait(false);
            var fetchUrl = $"{_host}/api/trade2/fetch/{string.Join(",", ids)}?query={queryId}";
            var fetchResp = await _transport.SendAsync(HttpMethod.Get, fetchUrl, null, context.Ct).ConfigureAwait(false);
            _rateLimiter.Observe(fetchResp);

            if (!fetchResp.IsSuccess)
            {
                return new FetchOutcome([], DescribeError(fetchResp));
            }

            var fetched = Deserialize<TradeFetchResponse>(fetchResp.Body, Poe2TradeJsonContext.Default.TradeFetchResponse);
            var rows = BuildRows(fetched, context.Item, rates);

            IEnumerable<TradeListingRow> ranked = (context.Options.Mode == TradeSearchMode.BestInSlot && constrains)
                ? rows
                : rows.OrderBy(r => r.NormalizedExalted ?? double.MaxValue);

            return new FetchOutcome(ranked.ToList());
        }

        // One search request at a given level cap; returns the query id + result hashes (or an error).
        private async Task<SearchOutcome> RunSearchAsync(
            string league, TradeItemQuery item, int? levelCap,
            TradeStatGroup statGroup, TradeSort sort, double? buyoutDivine, CancellationToken ct)
        {
            var requestBody = BuildSearchBody(item, levelCap, statGroup, sort, buyoutDivine);
            string json = JsonSerializer.Serialize(requestBody, Poe2TradeJsonContext.Default.TradeSearchRequest);

            // The API path has NO realm segment: "trade2" (vs PoE1 "trade") is what selects PoE2.
            await _rateLimiter.WaitTurnAsync(ct).ConfigureAwait(false);
            var searchUrl = $"{_host}/api/trade2/search/{Uri.EscapeDataString(league)}";
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
            List<TradeListingRow> rows = [];
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
        private static int LevelOvershoot(int characterLevel)
        {
            if (characterLevel <= 70) return 3;
            if (characterLevel <= 85) return 2;
            return 1;
        }

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
                    .Select(r => {
                        var minVal = TradeAffixText.ExtractMinConstraint(r.Affix.Text, r.GggText);
                        return new TradeStatFilter(
                            r.StatId,
                            new TradeMinMax(
                                Min: minVal,
                                Weight: BisWeighting.Weight(item.SlotId, BisWeighting.Categorize(r.Affix.Text))
                            ));
                    })
                    .ToArray();
                var group = new TradeStatGroup("weight2", weighted, new TradeMinMax(Min: 1));
                return (group, new TradeSort(StatGroup0: "desc"), true);
            }

            // Cheapest (or BIS with nothing resolved): require ≥N recommended affixes present, sort price.
            int matchMin = Math.Min(minMatchedAffixes, resolved.Count);
            TradeStatGroup countGroup = matchMin > 0
                ? new TradeStatGroup(
                    "count",
                    resolved.Select(r => {
                        var minVal = TradeAffixText.ExtractMinConstraint(r.Affix.Text, r.GggText);
                        var val = minVal.HasValue ? new TradeMinMax(Min: minVal) : null;
                        return new TradeStatFilter(r.StatId, val);
                    }).ToArray(),
                    new TradeMinMax(Min: matchMin))
                : new TradeStatGroup("and", []);
            return (countGroup, new TradeSort(Price: "asc"), matchMin > 0);
        }

        private static TradeSearchRequest BuildSearchBody(
            TradeItemQuery item, int? maxLevel, TradeStatGroup statGroup, TradeSort sort, double? buyoutDivine)
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

            TradeReqFilters? reqFilters = maxLevel.HasValue
                ? new TradeReqFilters(new TradeReqFilterValues(new TradeMinMax(Max: maxLevel.Value)))
                : null;

            var query = new TradeQuery(
                // "securable" = Instant Buyout listings (auto-purchasable; seller needn't be online),
                // which is what we want to surface — not "online" (In Person). Verified live on trade2.
                Status: new TradeStatus("securable"),
                Stats: [statGroup],
                Filters: new TradeFilters(
                    TypeFilters: typeFilters,
                    ReqFilters: reqFilters,
                    BuyoutFilters: buyoutFilters),
                Name: string.IsNullOrWhiteSpace(item.Name) ? null : item.Name,
                Type: string.IsNullOrWhiteSpace(item.BaseType) ? null : item.BaseType
            );
            return new TradeSearchRequest(query, sort);
        }

        private static string FormatPrice(TradePrice price)
        {
            // Trade amounts can be fractional (e.g. 0.5 divine).
            string amount = Math.Abs(price.Amount % 1) < 1e-9
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
