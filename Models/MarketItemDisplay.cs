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

        // Cached brush instances (frozen for WPF performance)
        private static readonly SolidColorBrush _upStroke = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));
        private static readonly SolidColorBrush _upFill = FreezeBrush(new SolidColorBrush(Color.FromArgb(0x26, 0x4A, 0xDE, 0x80)));
        private static readonly SolidColorBrush _downStroke = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)));
        private static readonly SolidColorBrush _downFill = FreezeBrush(new SolidColorBrush(Color.FromArgb(0x26, 0xF8, 0x71, 0x71)));
        private static readonly SolidColorBrush _stableStroke = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xE5, 0xB5, 0x60)));
        private static readonly SolidColorBrush _stableFill = FreezeBrush(new SolidColorBrush(Color.FromArgb(0x26, 0xE5, 0xB5, 0x60)));

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
            IsHudSelected = item.IsHudSelected;
        }

        /// <summary>Mini path geometry string for the sparkline, or null if no history.</summary>
        public string? SparklinePath => SparklineGenerator.BuildPathData(
            _historyService.GetHistory(Name), width: 110, height: 22);

        /// <summary>Area-fill path (closed shape) for gradient fill under the sparkline.</summary>
        public string? SparklineFillPath => SparklineGenerator.BuildFillPathData(
            _historyService.GetHistory(Name), width: 110, height: 22);

        /// <summary>Stroke color for the sparkline (green/red/gold).</summary>
        public string SparklineColor => SparklineGenerator.GetTrendColor(
            _historyService.GetTrendPercent(Name));

        /// <summary>Brush for sparkline stroke, resolved from trend direction.</summary>
        public Brush SparklineStrokeBrush => GetStrokeBrush(_historyService.GetTrendPercent(Name));

        /// <summary>Brush for sparkline area fill (trend color at ~15% opacity).</summary>
        public Brush SparklineFillBrush => GetFillBrush(_historyService.GetTrendPercent(Name));

        /// <summary>Trend arrow (▲/▼/◆ or empty).</summary>
        public string TrendArrow => SparklineGenerator.GetTrendArrow(
            _historyService.GetTrendPercent(Name));

        /// <summary>Formatted trend percentage (e.g. "+12.3%").</summary>
        public string TrendPercent => SparklineGenerator.FormatTrend(
            _historyService.GetTrendPercent(Name));

        /// <summary>Absolute price change over 7 days (e.g. "+15.2c", "-3.0c").</summary>
        public string TrendAbsoluteChange => SparklineGenerator.FormatAbsoluteChange(
            _historyService.GetHistory(Name));

        /// <summary>True if we have enough history to show a sparkline.</summary>
        public bool HasSparkline => _historyService.GetHistory(Name).Count >= 2;

        /// <summary>X position of the end-point dot in the sparkline canvas.</summary>
        public double SparklineEndDotX
        {
            get
            {
                var ep = SparklineGenerator.GetEndPoint(_historyService.GetHistory(Name), 110, 22);
                return ep.HasValue ? ep.Value.X - 2 : 0; // -2 to center the 4px dot
            }
        }

        /// <summary>Y position of the end-point dot in the sparkline canvas.</summary>
        public double SparklineEndDotY
        {
            get
            {
                var ep = SparklineGenerator.GetEndPoint(_historyService.GetHistory(Name), 110, 22);
                return ep.HasValue ? ep.Value.Y - 2 : 0; // -2 to center the 4px dot
            }
        }

        private static Brush GetStrokeBrush(double? trendPercent)
        {
            if (!trendPercent.HasValue) return _stableStroke;
            return trendPercent.Value switch
            {
                > 1.0 => _upStroke,
                < -1.0 => _downStroke,
                _ => _stableStroke
            };
        }

        private static Brush GetFillBrush(double? trendPercent)
        {
            if (!trendPercent.HasValue) return _stableFill;
            return trendPercent.Value switch
            {
                > 1.0 => _upFill,
                < -1.0 => _downFill,
                _ => _stableFill
            };
        }

        private static SolidColorBrush FreezeBrush(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
