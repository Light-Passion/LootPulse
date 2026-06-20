using System;
using System.Collections.Generic;
using LootPulse.Models;

namespace LootPulse.Services.Trade
{
    /// <summary>Which priority bucket a recommended affix falls into for Best-in-slot weighting.</summary>
    public enum AffixBucket
    {
        Damage,
        Defence,
        ElementalResistance,
        Movement,
        Other,
    }

    /// <summary>
    /// Best-in-slot weighting: categorize a build's recommended affixes into buckets by their text,
    /// apply a per-slot priority profile, and score a listing by the weighted sum of the recommended
    /// affixes it actually carries. Pure keyword matching on mod text — no poe2db tier tables (so it
    /// can't drift out of date), per poe2_validator §3.C.
    /// </summary>
    public static class BisWeighting
    {
        // Priority weights, widely spaced so a higher-priority match dominates a lower one.
        private const double Primary = 100;
        private const double Secondary = 10;
        private const double Tertiary = 1;

        /// <summary>
        /// Score a single listing: sum the per-slot weight of every recommended affix the listing has.
        /// Presence-based (the value magnitude is left to Stage 2's native weighted-sum) — listing mods
        /// are matched to recommended affixes by their numeric-agnostic template.
        /// </summary>
        public static double ScoreListing(
            string? slotId, IReadOnlyList<BuildAffix> recommended, IEnumerable<string>? listingMods)
        {
            if (recommended == null || recommended.Count == 0 || listingMods == null)
            {
                return 0;
            }

            // Templates present on the listing.
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mod in listingMods)
            {
                string t = TradeAffixText.Templatize(mod);
                if (t.Length > 0)
                {
                    present.Add(t);
                }
            }

            double score = 0;
            foreach (var affix in recommended)
            {
                if (present.Contains(TradeAffixText.Templatize(affix.Text)))
                {
                    score += Weight(slotId, Categorize(affix.Text));
                }
            }
            return score;
        }

        /// <summary>Per-slot priority weight for a bucket. Unspecified slots weight everything evenly.</summary>
        public static double Weight(string? slotId, AffixBucket bucket)
        {
            string slot = (slotId ?? string.Empty).ToUpperInvariant();

            // Weapon / Gloves / Rings: damage → elemental resistance → other.
            if (slot.StartsWith("WEAPON", StringComparison.Ordinal) ||
                slot.StartsWith("GLOVES", StringComparison.Ordinal) ||
                slot.StartsWith("RING", StringComparison.Ordinal))
            {
                return bucket switch
                {
                    AffixBucket.Damage => Primary,
                    AffixBucket.ElementalResistance => Secondary,
                    _ => Tertiary,
                };
            }

            // Helm / Body: defence → elemental resistance → other.
            if (slot.StartsWith("HELM", StringComparison.Ordinal) ||
                slot.StartsWith("BODY", StringComparison.Ordinal))
            {
                return bucket switch
                {
                    AffixBucket.Defence => Primary,
                    AffixBucket.ElementalResistance => Secondary,
                    _ => Tertiary,
                };
            }

            // Boots: movement speed → defence → other.
            if (slot.StartsWith("BOOT", StringComparison.Ordinal))
            {
                return bucket switch
                {
                    AffixBucket.Movement => Primary,
                    AffixBucket.Defence => Secondary,
                    _ => Tertiary,
                };
            }

            // Amulet, Belt, Charm, Flask, Offhand, anything else: weight all listed affixes evenly.
            return Secondary;
        }

        /// <summary>Bucket a recommended affix by keywords in its text. "Accuracy" and "Stun Threshold"
        /// are deliberately NOT promoted into Damage/Defence (commonly-ignored mods) — they land in
        /// Other, so they only count when explicitly listed and never drive priority.</summary>
        public static AffixBucket Categorize(string? affixText)
        {
            string t = (affixText ?? string.Empty).ToUpperInvariant();

            if (t.Contains("MOVEMENT SPEED", StringComparison.Ordinal))
            {
                return AffixBucket.Movement;
            }

            if (t.Contains("FIRE RESISTANCE", StringComparison.Ordinal) ||
                t.Contains("COLD RESISTANCE", StringComparison.Ordinal) ||
                t.Contains("LIGHTNING RESISTANCE", StringComparison.Ordinal) ||
                t.Contains("ELEMENTAL RESISTANCE", StringComparison.Ordinal) ||
                t.Contains("FIRE AND", StringComparison.Ordinal) ||      // "Fire and Chaos Resistances" etc.
                t.Contains("RESISTANCES", StringComparison.Ordinal))
            {
                return AffixBucket.ElementalResistance;
            }

            // Damage (Accuracy intentionally excluded).
            if (t.Contains("DAMAGE", StringComparison.Ordinal) ||
                t.Contains("ATTACK SPEED", StringComparison.Ordinal) ||
                t.Contains("CRITICAL", StringComparison.Ordinal) ||
                t.Contains("PENETRATION", StringComparison.Ordinal))
            {
                return AffixBucket.Damage;
            }

            // Defence (Stun Threshold intentionally excluded).
            if (t.Contains("MAXIMUM LIFE", StringComparison.Ordinal) ||
                t.Contains("ARMOUR", StringComparison.Ordinal) ||
                t.Contains("EVASION", StringComparison.Ordinal) ||
                t.Contains("ENERGY SHIELD", StringComparison.Ordinal))
            {
                return AffixBucket.Defence;
            }

            return AffixBucket.Other;
        }
    }
}
