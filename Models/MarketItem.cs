using System;

namespace LootPulse.Models
{
    public class MarketItem
    {
        public string Name { get; set; } = string.Empty;
        public string BaseType { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty; // Currency, Map, Waystone, Unique, Rune, etc.
        public double ChaosValue { get; set; }
        public double ExaltedValue { get; set; }
        public double DivineValue { get; set; }
        public DateTime LastUpdated { get; set; }
        public bool IsHudSelected { get; set; }

        public string DisplayValue
        {
            get
            {
                if (Category == "Exchange Rate" || Name.Contains("in Exalted Orbs", StringComparison.OrdinalIgnoreCase))
                {
                    return $"{ExaltedValue:F2} ex";
                }
                if (Name == "Mirror of Kalandra" || DivineValue >= 100.0)
                {
                    return $"{DivineValue:F2} div";
                }
                if (DivineValue >= 1.0 || Name == "Divine Orb")
                {
                    return $"{DivineValue:F2} div";
                }
                return $"{ExaltedValue:F2} ex";
            }
        }
    }
}
