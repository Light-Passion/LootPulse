using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LootPulse.Models;

namespace LootPulse.Services
{
    public class PoeNinjaClient
    {
        private readonly HttpClient _httpClient;
        private const string BaseUrl = "https://poe.ninja/api/data";

        public PoeNinjaClient()
        {
            // Set up client with a standard user-agent to prevent blocks
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "LootPulseOverlay/1.0 (Windows Native App)");
        }

        public async Task<List<MarketItem>> FetchCurrencyPricesAsync(string league)
        {
            var url = $"{BaseUrl}/currencyoverview?league={Uri.EscapeDataString(league)}&type=Currency";
            try
            {
                var response = await _httpClient.GetStringAsync(url);
                var root = JsonSerializer.Deserialize<NinjaCurrencyRoot>(response, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var list = new List<MarketItem>();
                if (root?.Lines != null)
                {
                    foreach (var line in root.Lines)
                    {
                        list.Add(new MarketItem
                        {
                            Name = line.CurrencyTypeName,
                            Category = "Currency",
                            ChaosValue = line.ChaosEquivalent,
                            LastUpdated = DateTime.UtcNow
                        });
                    }
                }
                return list;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching currency prices: {ex.Message}");
                return new List<MarketItem>();
            }
        }

        public async Task<List<MarketItem>> FetchItemPricesAsync(string league, string type, string categoryName)
        {
            var url = $"{BaseUrl}/itemoverview?league={Uri.EscapeDataString(league)}&type={Uri.EscapeDataString(type)}";
            try
            {
                var response = await _httpClient.GetStringAsync(url);
                var root = JsonSerializer.Deserialize<NinjaItemRoot>(response, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var list = new List<MarketItem>();
                if (root?.Lines != null)
                {
                    foreach (var line in root.Lines)
                    {
                        list.Add(new MarketItem
                        {
                            Name = line.Name,
                            Category = categoryName,
                            ChaosValue = line.ChaosValue,
                            ExaltedValue = line.ExaltedValue,
                            DivineValue = line.DivineValue,
                            LastUpdated = DateTime.UtcNow
                        });
                    }
                }
                return list;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching item prices for {type}: {ex.Message}");
                return new List<MarketItem>();
            }
        }

        // Helper classes for parsing poe.ninja Currency JSON
        private class NinjaCurrencyRoot
        {
            [JsonPropertyName("lines")]
            public List<NinjaCurrencyLine>? Lines { get; set; }
        }

        private class NinjaCurrencyLine
        {
            [JsonPropertyName("currencyTypeName")]
            public string CurrencyTypeName { get; set; } = string.Empty;

            [JsonPropertyName("chaosEquivalent")]
            public double ChaosEquivalent { get; set; }
        }

        // Helper classes for parsing poe.ninja Item JSON
        private class NinjaItemRoot
        {
            [JsonPropertyName("lines")]
            public List<NinjaItemLine>? Lines { get; set; }
        }

        private class NinjaItemLine
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("chaosValue")]
            public double ChaosValue { get; set; }

            [JsonPropertyName("exaltedValue")]
            public double ExaltedValue { get; set; }

            [JsonPropertyName("divineValue")]
            public double DivineValue { get; set; }
        }
    }
}
