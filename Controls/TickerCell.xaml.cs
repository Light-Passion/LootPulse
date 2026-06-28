using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace LootPulse.Controls
{
    /// <summary>
    /// A stock-ticker-style cell for the Currency Exchange ListView.
    /// Displays a sparkline, trend percentage, and absolute change.
    /// Supports flash-on-update animation when the trend value changes.
    /// </summary>
    public partial class TickerCell : UserControl
    {
        private string? _lastTrendPercent;

        public TickerCell()
        {
            InitializeComponent();
            DataContextChanged += TickerCell_DataContextChanged;
        }

        private void TickerCell_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Reset tracking when the data context changes (row recycling)
            _lastTrendPercent = null;
            UpdateTrendTracking();
        }

        /// <summary>
        /// Called externally (e.g., from the parent ListView's ItemsChanged) to
        /// trigger the flash animation if the trend has changed.
        /// </summary>
        public void UpdateTrendTracking()
        {
            if (DataContext is Models.MarketItemDisplay item)
            {
                string currentTrend = item.TrendPercent;
                if (_lastTrendPercent != null && _lastTrendPercent != currentTrend)
                {
                    // Determine flash direction
                    bool wentUp = currentTrend.CompareTo(_lastTrendPercent) > 0;
                    var storyboard = Resources[wentUp ? "FlashUp" : "FlashDown"] as Storyboard;
                    storyboard?.Begin();
                }
                _lastTrendPercent = currentTrend;
            }
        }
    }
}
