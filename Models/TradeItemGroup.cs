using System.Collections.Generic;

namespace LootPulse.Models
{
    /// <summary>What to search the trade site for, derived from one build inventory slot.</summary>
    public sealed class TradeItemQuery
    {
        /// <summary>Display label for the group (the build item name).</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>Unique item name to match (set for unique gear), or null for a plain base search.</summary>
        public string? Name { get; set; }

        /// <summary>Base type to match (e.g. "Sapphire Ring"), or null.</summary>
        public string? BaseType { get; set; }
    }

    /// <summary>One trade listing row shown under a group (cheapest-5).</summary>
    public sealed class TradeListingRow
    {
        public string PriceText { get; set; } = string.Empty;   // e.g. "5 exalted"
        public string Seller { get; set; } = string.Empty;
        public string ItemLabel { get; set; } = string.Empty;   // item name / typeLine
    }

    /// <summary>A build item and the cheapest live listings found for it, bound to the Trade Market tab.</summary>
    public sealed class TradeItemGroup
    {
        public string ItemLabel { get; set; } = string.Empty;
        public int MaxLevel { get; set; }
        public string LevelNote => $"≤ Lv {MaxLevel}";

        /// <summary>Deep link to the same search on the trade site (null until a search ran).</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Bound to a Button.Tag in XAML and opened via ProcessStartInfo.FileName, both of which take a string.")]
        public string? BrowserUrl { get; set; }

        public string StatusText { get; set; } = string.Empty;  // e.g. "no listings ≤ Lv 42"
        public bool HasListings => Listings.Count > 0;

        public List<TradeListingRow> Listings { get; } = new();
    }
}
