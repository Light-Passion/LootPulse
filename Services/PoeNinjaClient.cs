using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LootPulse.Models;

namespace LootPulse.Services
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept as instance methods to support future dependency injection, mockability, and extension.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Prevent external API and network failures from crashing the WPF overlay.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S1075:URIs should not be hardcoded", Justification = "poe.ninja endpoint URL is fixed by the service provider.")]
    public class PoeNinjaClient
    {
        private static readonly HttpClient _httpClient = CreateHttpClient();
        private const string _baseUrl = "https://poe.ninja/poe2/api/economy";
        private const string _currencyLiteral = "Currency";

        private static readonly Dictionary<string, (string finalType, bool isItem)> _typeMappings = new(StringComparer.OrdinalIgnoreCase)
        {
            { "SkillGem", ("UncutGems", false) },
            { "SkillGems", ("UncutGems", false) },
            { "UniqueWeapon", ("UniqueWeapons", true) },
            { "UniqueWeapons", ("UniqueWeapons", true) },
            { "UniqueArmour", ("UniqueArmours", true) },
            { "UniqueArmours", ("UniqueArmours", true) },
            { "UniqueAccessory", ("UniqueAccessories", true) },
            { "UniqueAccessories", ("UniqueAccessories", true) },
            { "UniqueFlask", ("UniqueFlasks", true) },
            { "UniqueFlasks", ("UniqueFlasks", true) },
            { "UniqueJewel", ("UniqueJewels", true) },
            { "UniqueJewels", ("UniqueJewels", true) },
            { "UniqueCharm", ("UniqueCharms", true) },
            { "UniqueCharms", ("UniqueCharms", true) },
            { "UniqueRelic", ("UniqueSanctumRelics", true) },
            { "UniqueSanctumRelics", ("UniqueSanctumRelics", true) },
            { "UniqueTablet", ("UniqueTablets", true) },
            { "UniqueTablets", ("UniqueTablets", true) },
            { "PrecursorTablet", ("PrecursorTablets", true) },
            { "PrecursorTablets", ("PrecursorTablets", true) }
        };

        private double _cachedExaltedRate = 200.0;
        private double _cachedChaosRate = 10.0;

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            return client;
        }

        public PoeNinjaClient()
        {
        }

        public async Task<List<MarketItem>> FetchCurrencyPricesAsync(string league)
        {
            var (baseUrl, finalType, _) = ResolveEndpointAndType(_currencyLiteral);
            var raw = await FetchExchangeDataWithUrlAsync(baseUrl, league, finalType).ConfigureAwait(false);
            if (raw?.Lines == null) return [];

            ExtractRates(raw.Core);
            var itemMap = BuildItemMap(raw.Items);

            var list = new List<MarketItem>();
            foreach (var line in raw.Lines.Where(line => !string.IsNullOrEmpty(line.Id)))
            {
                var item = MapCurrencyLine(line, itemMap);
                if (item != null)
                {
                    list.Add(item);
                }
            }
            return list;
        }

        public async Task<List<MarketItem>> FetchItemPricesAsync(string league, string type, string categoryName)
        {
            ArgumentNullException.ThrowIfNull(type);
            var (baseUrl, finalType, isItemEndpoint) = ResolveEndpointAndType(type);

            if (isItemEndpoint)
            {
                var raw = await FetchStashDataAsync(baseUrl, league, finalType).ConfigureAwait(false);
                if (raw?.Lines == null) return [];

                ExtractRates(raw.Core);

                var list = new List<MarketItem>();
                foreach (var line in raw.Lines.Where(line => !string.IsNullOrEmpty(line.Name)))
                {
                    list.Add(MapStashLine(line, categoryName));
                }
                return list;
            }
            else
            {
                var raw = await FetchExchangeDataWithUrlAsync(baseUrl, league, finalType).ConfigureAwait(false);
                if (raw?.Lines == null) return [];

                ExtractRates(raw.Core);
                var itemMap = BuildItemMap(raw.Items);

                var list = new List<MarketItem>();
                foreach (var line in raw.Lines.Where(line => !string.IsNullOrEmpty(line.Id)))
                {
                    var item = MapItemLine(line, itemMap, categoryName);
                    if (item != null)
                    {
                        list.Add(item);
                    }
                }
                return list;
            }
        }

        private void ExtractRates(NinjaExchangeCore? core)
        {
            if (core?.Rates == null) return;

            if (core.Rates.TryGetValue("exalted", out double exRate) && exRate > 0)
            {
                _cachedExaltedRate = exRate;
            }
            if (core.Rates.TryGetValue("chaos", out double chRate) && chRate > 0)
            {
                _cachedChaosRate = chRate;
            }
        }

        private static Dictionary<string, NinjaExchangeCoreItem> BuildItemMap(List<NinjaExchangeCoreItem>? items)
        {
            var map = new Dictionary<string, NinjaExchangeCoreItem>(StringComparer.OrdinalIgnoreCase);
            if (items == null) return map;

            foreach (var item in items.Where(i => !string.IsNullOrEmpty(i.Id)))
            {
                map[item.Id] = item;
            }
            return map;
        }

        private MarketItem? MapCurrencyLine(NinjaExchangeLine line, Dictionary<string, NinjaExchangeCoreItem> itemMap)
        {
            // poe.ninja's "id" is an internal slug (e.g. "ancient-infuser"), not a valid
            // in-game BaseType string. Skip the item rather than write the slug into the
            // filter, which would make PoE2 reject the filter as referencing an unknown item.
            if (!itemMap.TryGetValue(line.Id, out var coreItem))
            {
                System.Diagnostics.Debug.WriteLine($"Skipping currency line with unresolved display name: {line.Id}");
                return null;
            }

            string name = coreItem.Name;
            string category = coreItem.Category;

            if (category.Equals(_currencyLiteral, StringComparison.OrdinalIgnoreCase) ||
                category.Equals("Vaal", StringComparison.OrdinalIgnoreCase))
            {
                category = _currencyLiteral;
            }

            double divineValue = line.PrimaryValue;
            double exaltedValue = divineValue * _cachedExaltedRate;
            double chaosValue = divineValue * _cachedChaosRate;

            return new MarketItem
            {
                Name = name,
                Category = category,
                DivineValue = divineValue,
                ExaltedValue = exaltedValue,
                ChaosValue = chaosValue,
                LastUpdated = DateTime.UtcNow
            };
        }

        private MarketItem? MapItemLine(NinjaExchangeLine line, Dictionary<string, NinjaExchangeCoreItem> itemMap, string categoryName)
        {
            // Same reasoning as MapCurrencyLine: never write poe.ninja's internal slug id
            // into the filter as a BaseType, since it isn't a real PoE2 item name.
            if (!itemMap.TryGetValue(line.Id, out var coreItem))
            {
                System.Diagnostics.Debug.WriteLine($"Skipping item line with unresolved display name: {line.Id}");
                return null;
            }

            string name = coreItem.Name;
            string baseType = coreItem.Category;

            double divineValue = line.PrimaryValue;
            double exaltedValue = divineValue * _cachedExaltedRate;
            double chaosValue = divineValue * _cachedChaosRate;

            return new MarketItem
            {
                Name = name,
                BaseType = baseType,
                Category = categoryName,
                DivineValue = divineValue,
                ExaltedValue = exaltedValue,
                ChaosValue = chaosValue,
                LastUpdated = DateTime.UtcNow
            };
        }

        private MarketItem MapStashLine(NinjaStashLine line, string categoryName)
        {
            double divineValue = line.PrimaryValue;
            double exaltedValue = divineValue * _cachedExaltedRate;
            double chaosValue = divineValue * _cachedChaosRate;

            return new MarketItem
            {
                Name = line.Name,
                BaseType = line.BaseType,
                Category = categoryName,
                DivineValue = divineValue,
                ExaltedValue = exaltedValue,
                ChaosValue = chaosValue,
                LastUpdated = DateTime.UtcNow
            };
        }

        private static (string url, string finalType, bool isItemEndpoint) ResolveEndpointAndType(string type)
        {
            string normalized = type.Trim();
            if (_typeMappings.TryGetValue(normalized, out var mapping))
            {
                string path = mapping.isItem ? "stash/current/item/overview" : "exchange/current/overview";
                return ($"{_baseUrl}/{path}", mapping.finalType, mapping.isItem);
            }

            bool isItem = normalized.StartsWith("Unique", StringComparison.OrdinalIgnoreCase);
            string endPath = isItem ? "stash/current/item/overview" : "exchange/current/overview";
            return ($"{_baseUrl}/{endPath}", normalized, isItem);
        }

        private static async Task<NinjaStashResponse?> FetchStashDataAsync(string baseUrl, string league, string type)
        {
            var response = await FetchStashDataForLeagueAsync(baseUrl, league, type).ConfigureAwait(false);
            if (response == null || response.Lines == null || response.Lines.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"Stash fetch failed or empty for league '{league}' and type '{type}'. Retrying with fallback league 'Standard'...");
                response = await FetchStashDataForLeagueAsync(baseUrl, "Standard", type).ConfigureAwait(false);
            }
            return response;
        }

        private static async Task<NinjaStashResponse?> FetchStashDataForLeagueAsync(string baseUrl, string league, string type)
        {
            var url = $"{baseUrl}?league={Uri.EscapeDataString(league)}&type={Uri.EscapeDataString(type)}";
            try
            {
                var response = await _httpClient.GetStringAsync(new Uri(url)).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(response)) return null;

                return JsonSerializer.Deserialize(response, PoeNinjaJsonContext.Default.NinjaStashResponse);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching poe.ninja PoE2 stash data for league '{league}', type '{type}': {ex.Message}");
                return null;
            }
        }

        private static async Task<NinjaExchangeResponse?> FetchExchangeDataWithUrlAsync(string baseUrl, string league, string type)
        {
            var response = await FetchExchangeDataForLeagueWithUrlAsync(baseUrl, league, type).ConfigureAwait(false);
            if (response == null || response.Lines == null || response.Lines.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"Exchange fetch failed or empty for league '{league}' and type '{type}'. Retrying with fallback league 'Standard'...");
                response = await FetchExchangeDataForLeagueWithUrlAsync(baseUrl, "Standard", type).ConfigureAwait(false);
            }
            return response;
        }

        private static async Task<NinjaExchangeResponse?> FetchExchangeDataForLeagueWithUrlAsync(string baseUrl, string league, string type)
        {
            var url = $"{baseUrl}?league={Uri.EscapeDataString(league)}&type={Uri.EscapeDataString(type)}";
            try
            {
                var response = await _httpClient.GetStringAsync(new Uri(url)).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(response)) return null;

                return JsonSerializer.Deserialize(response, PoeNinjaJsonContext.Default.NinjaExchangeResponse);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching poe.ninja PoE2 exchange data for league '{league}', type '{type}': {ex.Message}");
                return null;
            }
        }
    }
}
