using System.Collections.Generic;

namespace LootPulse.Models
{
    public enum AffixImportance
    {
        Required,
        VeryImportant,
        Important,
        Wanted
    }

    /// <summary>One recommended affix line lifted from a build slot's additional_text (lines 2+).</summary>
    public sealed class BuildAffix
    {
        /// <summary>Human-readable mod text, e.g. "253% increased Physical Damage" (leading "N. " stripped).</summary>
        public string Text { get; set; } = string.Empty;

        public AffixImportance Importance { get; set; } = AffixImportance.Wanted;
    }

    /// <summary>What to search the trade site for, derived from one build inventory slot.</summary>
    public sealed class TradeItemQuery
    {
        /// <summary>Display label for the group (the build item name).</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>Unique item name to match (set for unique gear), or null for a plain base search.</summary>
        public string? Name { get; set; }

        /// <summary>Base type to match (e.g. "Sapphire Ring"), or null.</summary>
        public string? BaseType { get; set; }

        /// <summary>Recommended affixes from the build for this slot (base searches only); may be empty.</summary>
        public List<BuildAffix> Affixes { get; } = [];

        /// <summary>The build's minimum level for this item (level_interval[0]), or null if unknown.</summary>
        public int? MinRequiredLevel { get; set; }

        /// <summary>The build inventory slot id (e.g. "Weapon1", "Boots1") — selects the Best-in-slot
        /// weighting profile. Null/unknown falls back to an even profile.</summary>
        public string? SlotId { get; set; }
    }

    /// <summary>One trade listing row shown under a group (cheapest-5).</summary>
    public sealed class TradeListingRow
    {
        public string PriceText { get; set; } = string.Empty;   // e.g. "5 exalted"
        public string Seller { get; set; } = string.Empty;
        public string ItemLabel { get; set; } = string.Empty;   // item name / typeLine

        /// <summary>Exalted-equivalent value used to rank cheapest across currencies; null if unknown.</summary>
        public double? NormalizedExalted { get; set; }

        /// <summary>Display form of <see cref="NormalizedExalted"/>, e.g. "≈ 12 ex" / "≈ 3 div" (empty when unknown).</summary>
        public string NormalizedText { get; set; } = string.Empty;

        /// <summary>Best-in-slot weighted score from the listing's matched recommended affixes (higher = better).</summary>
        public double Score { get; set; }

        /// <summary>The listing's actual character-level requirement (base + affixes), or null if unknown.</summary>
        public int? RequiredLevel { get; set; }
    }

    /// <summary>A build item and the cheapest live listings found for it, bound to the Trade Market tab.</summary>
    public sealed class TradeItemGroup
    {
        public string ItemLabel { get; set; } = string.Empty;

        /// <summary>The level cap actually used for this item's search (may be raised for buy-ahead items).</summary>
        public int MaxLevel { get; set; }

        /// <summary>True when the build needs a higher level than the character has — a good pre-purchase.</summary>
        public bool IsAboveCharacterLevel { get; set; }

        public string LevelNote => IsAboveCharacterLevel ? $"Lv {MaxLevel}+ • buy ahead" : $"≤ Lv {MaxLevel}";

        /// <summary>Deep link to the same search on the trade site (null until a search ran).</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Bound to a Button.Tag in XAML and opened via ProcessStartInfo.FileName, both of which take a string.")]
        public string? BrowserUrl { get; set; }

        public string StatusText { get; set; } = string.Empty;  // e.g. "no listings ≤ Lv 42"
        public bool HasListings => Listings.Count > 0;

        public List<TradeListingRow> Listings { get; } = [];
    }
}
