using System.Windows.Media;
using LootPulse.Models;
using LootPulse.Services;

namespace LootPulse.Models
{
    /// <summary>
    /// Display wrapper for MarketItem that includes sparkline path data
    /// and trend information for the Currency Exchange ListView.
    /// </summary>
    public class MarketItemDisplay : MarketItem
    {
        private readonly PriceHistoryService _historyService;

        public MarketItemDisplay(MarketItem item, PriceHistoryService historyService)
        {
            _historyService = historyService;
            Name = item.Name;
            BaseType = item.BaseType;
            Category = item.Category;
            ChaosValue = item.ChaosValue;
            ExaltedValue = item.ExaltedValue;
            DivineValue = item.DivineValue;
            LastUpdated = item.LastUpdated;
        }

        /// <summary>Mini path geometry string for the sparkline, or null if no history.</summary>
        public string? SparklinePath => SparklineGenerator.BuildPathData(
            _historyService.GetHistory(Name), width: 60, height: 14);

        /// <summary>Stroke color for the sparkline (green/red/gray).</summary>
        public string SparklineColor => SparklineGenerator.GetTrendColor(
            _historyService.GetTrendPercent(Name));

        /// <summary>Trend arrow (▲/▼/◆ or empty).</summary>
        public string TrendArrow => SparklineGenerator.GetTrendArrow(
            _historyService.GetTrendPercent(Name));

        /// <summary>Formatted trend percentage (e.g. "+12.3%").</summary>
        public string TrendPercent => SparklineGenerator.FormatTrend(
            _historyService.GetTrendPercent(Name));

        /// <summary>True if we have enough history to show a sparkline.</summary>
        public bool HasSparkline => _historyService.GetHistory(Name).Count >= 2;
    }
}
