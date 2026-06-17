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
            var raw = await FetchExchangeDataAsync(league, _currencyLiteral).ConfigureAwait(false);
            if (raw?.Lines == null) return [];

            ExtractRates(raw.Core);
            var itemMap = BuildItemMap(raw.Core?.Items);

            var list = new List<MarketItem>();
            foreach (var line in raw.Lines.Where(line => !string.IsNullOrEmpty(line.Id)))
            {
                list.Add(MapCurrencyLine(line, itemMap));
            }
            return list;
        }

        public async Task<List<MarketItem>> FetchItemPricesAsync(string league, string type, string categoryName)
        {
            var raw = await FetchExchangeDataAsync(league, type).ConfigureAwait(false);
            if (raw?.Lines == null) return [];

            var itemMap = BuildItemMap(raw.Core?.Items);

            var list = new List<MarketItem>();
            foreach (var line in raw.Lines.Where(line => !string.IsNullOrEmpty(line.Id)))
            {
                list.Add(MapItemLine(line, itemMap, categoryName));
            }
            return list;
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

        private MarketItem MapCurrencyLine(NinjaExchangeLine line, Dictionary<string, NinjaExchangeCoreItem> itemMap)
        {
            string name = line.Id;
            string category = _currencyLiteral;

            if (itemMap.TryGetValue(line.Id, out var coreItem))
            {
                name = coreItem.Name;
                category = coreItem.Category;
            }

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

        private MarketItem MapItemLine(NinjaExchangeLine line, Dictionary<string, NinjaExchangeCoreItem> itemMap, string categoryName)
        {
            string name = line.Id;
            string baseType = string.Empty;

            if (itemMap.TryGetValue(line.Id, out var coreItem))
            {
                name = coreItem.Name;
                baseType = coreItem.Category;
            }

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

        private static async Task<NinjaExchangeResponse?> FetchExchangeDataAsync(string league, string type)
        {
            var response = await FetchExchangeDataForLeagueAsync(league, type).ConfigureAwait(false);

            // Self-Healing Fallback: Retry with "Standard" if results are empty or query failed
            if (response == null || response.Lines == null || response.Lines.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"Fetch failed or empty for league '{league}' and type '{type}'. Retrying with fallback league 'Standard'...");
                response = await FetchExchangeDataForLeagueAsync("Standard", type).ConfigureAwait(false);
            }

            return response;
        }

        private static async Task<NinjaExchangeResponse?> FetchExchangeDataForLeagueAsync(string league, string type)
        {
            var url = $"{_baseUrl}/exchange/current/overview?league={Uri.EscapeDataString(league)}&type={Uri.EscapeDataString(type)}";
            try
            {
                var response = await _httpClient.GetStringAsync(new Uri(url)).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(response)) return null;

                return JsonSerializer.Deserialize(response, PoeNinjaJsonContext.Default.NinjaExchangeResponse);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching poe.ninja PoE2 data for league '{league}', type '{type}': {ex.Message}");
                return null;
            }
        }
    }
}
