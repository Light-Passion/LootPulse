using System;
using System.Collections.Generic;
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
        private const string _baseUrl = "https://poe.ninja/api/data";
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            client.DefaultRequestHeaders.Add("User-Agent", "LootPulseOverlay/1.0 (Windows Native App)");
            return client;
        }

        public PoeNinjaClient()
        {
        }

        public async Task<List<MarketItem>> FetchCurrencyPricesAsync(string league)
        {
            var url = $"{_baseUrl}/currencyoverview?league={Uri.EscapeDataString(league)}&type=Currency";
            try
            {
                var response = await _httpClient.GetStringAsync(new Uri(url)).ConfigureAwait(false);
                var root = JsonSerializer.Deserialize<NinjaCurrencyRoot>(response, _jsonOptions);

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
                return [];
            }
        }

        public async Task<List<MarketItem>> FetchItemPricesAsync(string league, string type, string categoryName)
        {
            var url = $"{_baseUrl}/itemoverview?league={Uri.EscapeDataString(league)}&type={Uri.EscapeDataString(type)}";
            try
            {
                var response = await _httpClient.GetStringAsync(new Uri(url)).ConfigureAwait(false);
                var root = JsonSerializer.Deserialize<NinjaItemRoot>(response, _jsonOptions);

                var list = new List<MarketItem>();
                if (root?.Lines != null)
                {
                    foreach (var line in root.Lines)
                    {
                        list.Add(new MarketItem
                        {
                            Name = line.Name,
                            BaseType = line.BaseType,
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
                return [];
            }
        }

        // Helper classes for parsing poe.ninja Currency JSON
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Deserialized by JsonSerializer")]
        private sealed class NinjaCurrencyRoot
        {
            [JsonPropertyName("lines")]
            public List<NinjaCurrencyLine>? Lines { get; set; }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Deserialized by JsonSerializer")]
        private sealed class NinjaCurrencyLine
        {
            [JsonPropertyName("currencyTypeName")]
            public string CurrencyTypeName { get; set; } = string.Empty;

            [JsonPropertyName("chaosEquivalent")]
            public double ChaosEquivalent { get; set; }
        }

        // Helper classes for parsing poe.ninja Item JSON
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Deserialized by JsonSerializer")]
        private sealed class NinjaItemRoot
        {
            [JsonPropertyName("lines")]
            public List<NinjaItemLine>? Lines { get; set; }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Deserialized by JsonSerializer")]
        private sealed class NinjaItemLine
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("baseType")]
            public string BaseType { get; set; } = string.Empty;

            [JsonPropertyName("chaosValue")]
            public double ChaosValue { get; set; }

            [JsonPropertyName("exaltedValue")]
            public double ExaltedValue { get; set; }

            [JsonPropertyName("divineValue")]
            public double DivineValue { get; set; }
        }
    }
}
