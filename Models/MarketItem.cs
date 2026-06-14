using System;

namespace LootPulse.Models
{
    public class MarketItem
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty; // Currency, Map, Waystone, Unique, Rune, etc.
        public double ChaosValue { get; set; }
        public double ExaltedValue { get; set; }
        public double DivineValue { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
