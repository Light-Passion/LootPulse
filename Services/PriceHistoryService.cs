using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LootPulse.Services
{
    /// <summary>
    /// Persists daily price snapshots for market items and provides historical data
    /// for sparkline rendering. Stores up to 7 days of history per item in
    /// %localappdata%\LootPulse\price_history.json
    /// </summary>
    public class PriceHistoryService
    {
        private const int MaxHistoryDays = 7;
        private readonly string _historyFile;
        private Dictionary<string, List<PriceSnapshot>> _history = [];
        private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

        public PriceHistoryService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _historyFile = Path.Combine(appData, "LootPulse", "price_history.json");
            Load();
        }

        /// <summary>
        /// Records today's price for each item. If an entry already exists for today,
        /// it's updated. Entries older than MaxHistoryDays are pruned.
        /// </summary>
        public void RecordSnapshot(IEnumerable<(string Name, double Value)> items)
        {
            if (items == null) return;
            var today = DateOnly.FromDateTime(DateTime.Now);

            foreach (var (name, value) in items)
            {
                if (string.IsNullOrEmpty(name) || value <= 0) continue;

                if (!_history.TryGetValue(name, out var snapshots))
                {
                    snapshots = [];
                    _history[name] = snapshots;
                }

                // Update today's entry if it exists, otherwise add a new one
                if (snapshots.Count > 0 && snapshots[^1].Date == today)
                {
                    snapshots[^1] = new PriceSnapshot(today, value);
                }
                else
                {
                    snapshots.Add(new PriceSnapshot(today, value));
                }

                // Prune old entries
                if (snapshots.Count > MaxHistoryDays)
                {
                    snapshots.RemoveRange(0, snapshots.Count - MaxHistoryDays);
                }
            }

            Save();
        }

        /// <summary>
        /// Returns the price history for an item (oldest to newest).
        /// Empty list if no history exists.
        /// </summary>
        public IReadOnlyList<PriceSnapshot> GetHistory(string itemName)
        {
            if (string.IsNullOrEmpty(itemName) || !_history.TryGetValue(itemName, out var snapshots))
            {
                return [];
            }
            return snapshots;
        }

        /// <summary>
        /// Returns the percentage change between the first and last recorded value.
        /// Positive = price went up, negative = went down, null = not enough data.
        /// </summary>
        public double? GetTrendPercent(string itemName)
        {
            var history = GetHistory(itemName);
            if (history.Count < 2) return null;

            var first = history[0].Value;
            var last = history[^1].Value;

            if (first <= 0) return null;
            return ((last - first) / first) * 100.0;
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_historyFile))
                {
                    string json = File.ReadAllText(_historyFile);
                    _history = JsonSerializer.Deserialize<Dictionary<string, List<PriceSnapshot>>>(json) ?? [];
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load price history: {ex.Message}");
                _history = [];
            }
        }

        private void Save()
        {
            try
            {
                string? directory = Path.GetDirectoryName(_historyFile);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                string json = JsonSerializer.Serialize(_history, _jsonOpts);
                File.WriteAllText(_historyFile, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save price history: {ex.Message}");
            }
        }
    }

    /// <summary>A single daily price snapshot.</summary>
    public record PriceSnapshot(DateOnly Date, double Value)
    {
        // JSON serialization needs a parameterless constructor + settable props
        public DateOnly Date { get; init; } = Date;
        public double Value { get; init; } = Value;
    }
}
