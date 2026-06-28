using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LootPulse.Services
{
    /// <summary>
    /// Generates sparkline path geometry and trend indicators from price history.
    /// </summary>
    public static class SparklineGenerator
    {
        /// <summary>
        /// Builds a sparkline path string from price snapshots.
        /// Returns null if there's not enough data to draw a meaningful line.
        /// </summary>
        public static string? BuildPathData(IReadOnlyList<PriceSnapshot> history, double width = 110, double height = 22)
        {
            if (history == null || history.Count < 2) return null;

            double min = double.MaxValue;
            double max = double.MinValue;
            foreach (var snap in history)
            {
                if (snap.Value < min) min = snap.Value;
                if (snap.Value > max) max = snap.Value;
            }

            // Avoid divide-by-zero for flat data
            double range = max - min;
            if (range < 0.0001) range = 1;

            // Map values to Y coordinates (inverted: higher value = lower Y)
            // Leave 2px padding top and bottom
            double stepX = width / (history.Count - 1);

            var points = new List<Point>(history.Count);
            for (int i = 0; i < history.Count; i++)
            {
                double x = i * stepX;
                double normalized = (history[i].Value - min) / range;
                double y = height - 2 - (normalized * (height - 4));
                // Clamp to keep within bounds
                y = Math.Max(1, Math.Min(height - 1, y));
                points.Add(new Point(x, y));
            }

            // Build path string: M x0,y0 L x1,y1 L x2,y2 ...
            var sb = new System.Text.StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"M {points[0].X:F1},{points[0].Y:F1}");
            for (int i = 1; i < points.Count; i++)
            {
                sb.Append(CultureInfo.InvariantCulture, $" L {points[i].X:F1},{points[i].Y:F1}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Builds a closed area-fill path (stroke path + baseline closure) for
        /// gradient fill under the sparkline. Returns null if not enough data.
        /// </summary>
        public static string? BuildFillPathData(IReadOnlyList<PriceSnapshot> history, double width = 110, double height = 22)
        {
            string? strokePath = BuildPathData(history, width, height);
            if (strokePath == null) return null;

            // Close to baseline: append L width,height L 0,height Z
            return string.Create(CultureInfo.InvariantCulture, $"{strokePath} L {width:F1},{height:F1} L 0,{height:F1} Z");
        }

        /// <summary>
        /// Returns the (x, y) pixel position of the last data point in the sparkline,
        /// for rendering the end-point dot. Returns null if not enough data.
        /// </summary>
        public static Point? GetEndPoint(IReadOnlyList<PriceSnapshot> history, double width = 110, double height = 22)
        {
            if (history == null || history.Count < 2) return null;

            double min = double.MaxValue;
            double max = double.MinValue;
            foreach (var snap in history)
            {
                if (snap.Value < min) min = snap.Value;
                if (snap.Value > max) max = snap.Value;
            }

            double range = max - min;
            if (range < 0.0001) range = 1;

            double stepX = width / (history.Count - 1);
            int i = history.Count - 1;
            double x = i * stepX;
            double normalized = (history[i].Value - min) / range;
            double y = height - 2 - (normalized * (height - 4));
            y = Math.Max(1, Math.Min(height - 1, y));
            return new Point(x, y);
        }

        /// <summary>
        /// Returns the absolute price change between the first and last recorded value.
        /// </summary>
        public static string FormatAbsoluteChange(IReadOnlyList<PriceSnapshot> history)
        {
            if (history == null || history.Count < 2) return "";
            double first = history[0].Value;
            double last = history[^1].Value;
            double diff = last - first;
            string sign = diff >= 0 ? "+" : "";
            return $"{sign}{diff:F1}c";
        }

        /// <summary>
        /// Returns the stroke color for the sparkline based on trend direction.
        /// Green (#4ADE80) for up, red (#F87171) for down, gray (#888888) for flat.
        /// </summary>
        public static string GetTrendColor(double? trendPercent)
        {
            if (!trendPercent.HasValue) return "#E5B560";
            return trendPercent.Value switch
            {
                > 1.0 => "#4ADE80",      // green — price up
                < -1.0 => "#F87171",      // red — price down
                _ => "#E5B560"            // gold — stable (±1%)
            };
        }

        /// <summary>
        /// Returns a trend arrow character for display.
        /// </summary>
        public static string GetTrendArrow(double? trendPercent)
        {
            if (!trendPercent.HasValue) return "◆";
            return trendPercent.Value switch
            {
                > 1.0 => "▲",
                < -1.0 => "▼",
                _ => "◆"
            };
        }

        /// <summary>
        /// Formats the trend percentage for display (e.g. "+12.3%", "-5.1%").
        /// </summary>
        public static string FormatTrend(double? trendPercent)
        {
            if (!trendPercent.HasValue) return "";
            double val = trendPercent.Value;
            string sign = val > 0 ? "+" : "";
            return $"{sign}{val:F1}%";
        }
    }
}
