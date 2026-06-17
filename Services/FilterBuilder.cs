using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LootPulse.Models;

namespace LootPulse.Services
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Exposing List<T> is standard for WPF databinding and local collection models.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Hex parsing and exception handling catch general errors to return fallback colors.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S107:Methods should not have too many parameters", Justification = "Parameters represent the essential config context (path, items, build, levels, thresholds, themes) needed for the single-pass filter generation.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S2325:Methods and properties that don't access instance data should be static", Justification = "Kept as instance methods to support future dependency injection, mockability, and extension.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept as instance methods to support future dependency injection, mockability, and extension.")]
    public class FilterBuilder
    {
        private const string _sectionSeparator = "# --------------------------------------------------";
        private const string _baseTypePrefix = "    BaseType";

        /// <summary>
        /// Generates a complete .filter file by prepending dynamic market and build rule blocks.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Top-level generation method must catch all exceptions to prevent overlay UI crash and return false indicating failure.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept as instance method to support future dependency injection, mockability, and extension.")]
        public bool GenerateFilterFile(
            string outputPath,
            string? baseFilterPath,
            List<MarketItem> marketItems,
            PoeBuild? activeBuild,
            int playerLevel,
            int zoneLevel,
            double tier1Threshold, // In Divine Orbs, e.g. 1.0
            double tier2Threshold, // In Exalted Orbs, e.g. 1.0
            FilterTheme? activeTheme = null
        )
        {
            try
            {
                activeTheme ??= new FilterTheme();
                marketItems ??= [];

                EnsureMarketValuesNormalized(marketItems);

                var sb = new StringBuilder();

                AppendHeader(sb, playerLevel, zoneLevel, baseFilterPath);

                AppendUniqueHighlights(sb, activeBuild, playerLevel, marketItems, activeTheme);

                AppendProgressionBaseHighlights(sb, activeBuild, playerLevel, zoneLevel, activeTheme);

                AppendSkillGemHighlights(sb, activeBuild, playerLevel, activeTheme);

                AppendDynamicEconomyHighlights(sb, marketItems, tier1Threshold, tier2Threshold, activeTheme);

                AppendFallbackOrBaseRules(sb, baseFilterPath);

                // Write out the filter file
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error generating filter file: {ex.Message}");
                return false;
            }
        }

        private static void EnsureMarketValuesNormalized(List<MarketItem> marketItems)
        {
            if (marketItems == null || marketItems.Count == 0) return;

            double divinePriceInChaos = 120.0;
            double exaltedPriceInChaos = 15.0;

            var divOrb = marketItems.FirstOrDefault(i => i.Name == "Divine Orb" && i.Category == "Currency");
            if (divOrb?.ChaosValue > 0)
            {
                divinePriceInChaos = divOrb.ChaosValue;
            }

            var exOrb = marketItems.FirstOrDefault(i => i.Name == "Exalted Orb" && i.Category == "Currency");
            if (exOrb?.ChaosValue > 0)
            {
                exaltedPriceInChaos = exOrb.ChaosValue;
            }

            foreach (var item in marketItems)
            {
                NormalizeItemValues(item, divinePriceInChaos, exaltedPriceInChaos);
            }
        }

        private static void NormalizeItemValues(MarketItem item, double divinePriceInChaos, double exaltedPriceInChaos)
        {
            if (item.DivineValue <= 0 && item.ChaosValue > 0)
            {
                item.DivineValue = item.ChaosValue / divinePriceInChaos;
            }
            if (item.ExaltedValue <= 0 && item.ChaosValue > 0)
            {
                item.ExaltedValue = item.ChaosValue / exaltedPriceInChaos;
            }

            if (item.ChaosValue <= 0 && item.DivineValue > 0)
            {
                item.ChaosValue = item.DivineValue * divinePriceInChaos;
            }
            if (item.ExaltedValue <= 0 && item.DivineValue > 0)
            {
                item.ExaltedValue = item.DivineValue * (divinePriceInChaos / exaltedPriceInChaos);
            }
        }

        private static void AppendHeader(StringBuilder sb, int playerLevel, int zoneLevel, string? baseFilterPath)
        {
            sb.AppendLine("# ==========================================================================");
            sb.AppendLine("# PATH OF EXILE 2 DYNAMIC ECONOMY & BUILD FILTER");
            sb.AppendLine(CultureInfo.InvariantCulture, $"# Generated on: {DateTime.Now}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"# Current Player Level: {playerLevel}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"# Current Zone Level: {zoneLevel}");
            if (!string.IsNullOrEmpty(baseFilterPath))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"# Merged with Base Filter: {Path.GetFileName(baseFilterPath)}");
            }
            sb.AppendLine("# ==========================================================================\n");
        }

        private static void AppendUniqueHighlights(StringBuilder sb, PoeBuild? activeBuild, int playerLevel, List<MarketItem> marketItems, FilterTheme activeTheme)
        {
            if (activeBuild == null) return;

            var buildUniqueItems = activeBuild.InventorySlots
                .Where(slot => !string.IsNullOrWhiteSpace(slot.ItemName) && !string.IsNullOrEmpty(slot.UniqueName))
                .Where(slot => {
                    if (slot.LevelInterval == null || slot.LevelInterval.Count < 2) return true;
                    return playerLevel >= slot.LevelInterval[0] && playerLevel <= slot.LevelInterval[1];
                })
                .Select(slot => GetUniqueBaseType(slot.ItemName, marketItems))
                .Where(baseType => !string.IsNullOrEmpty(baseType))
                .Distinct()
                .ToList();

            if (buildUniqueItems.Count > 0)
            {
                sb.AppendLine(_sectionSeparator);
                sb.AppendLine(CultureInfo.InvariantCulture, $"# BUILD HIGHLIGHTS: {activeBuild.Name} (Level {playerLevel})");
                sb.AppendLine(_sectionSeparator);
                sb.AppendLine("# Active Build Unique Items");
                sb.AppendLine("Show");
                sb.AppendLine("    Rarity Unique");
                sb.Append(_baseTypePrefix);
                foreach (var baseType in buildUniqueItems)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" \"{baseType}\"");
                }
                sb.AppendLine();
                AppendStyleBlock(sb, activeTheme.Uniques);
                sb.AppendLine();
            }
        }

        private static void GetProgressionBasesForSlot(
            string itemName,
            int playerLevel,
            int zoneLevel,
            HashSet<string> progressionBases)
        {
            var matchedBase = FindMatchedBase(itemName);
            if (matchedBase == null)
            {
                var cleaned = CleanItemBaseName(itemName);
                if (!string.IsNullOrEmpty(cleaned))
                {
                    progressionBases.Add(cleaned);
                }
                return;
            }

            progressionBases.Add(matchedBase.Name);
            var list = FindArchetypeList(matchedBase.Name);
            if (list == null)
            {
                progressionBases.Add(matchedBase.Name);
                return;
            }

            var playerBase = list.Where(b => b.RequiredLevel <= playerLevel)
                                 .OrderByDescending(b => b.RequiredLevel)
                                 .FirstOrDefault();
            if (playerBase != null)
            {
                progressionBases.Add(playerBase.Name);
            }

            var zoneBase = list.Where(b => b.RequiredLevel <= zoneLevel)
                               .OrderByDescending(b => b.RequiredLevel)
                               .FirstOrDefault();
            if (zoneBase != null)
            {
                progressionBases.Add(zoneBase.Name);
            }
        }

        private static void AppendProgressionBaseHighlights(StringBuilder sb, PoeBuild? activeBuild, int playerLevel, int zoneLevel, FilterTheme activeTheme)
        {
            if (activeBuild == null) return;

            var buildBases = activeBuild.InventorySlots
                .Where(slot => string.IsNullOrEmpty(slot.UniqueName) && !string.IsNullOrEmpty(slot.ItemName))
                .Where(slot => {
                    if (slot.LevelInterval == null || slot.LevelInterval.Count < 2) return true;
                    return playerLevel >= slot.LevelInterval[0] && playerLevel <= slot.LevelInterval[1];
                })
                .Select(slot => slot.ItemName)
                .ToList();

            var progressionBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var itemName in buildBases)
            {
                GetProgressionBasesForSlot(itemName, playerLevel, zoneLevel, progressionBases);
            }

            if (progressionBases.Count > 0)
            {
                sb.AppendLine("# Active Build Progression Base Items (Leveling & Zone)");
                sb.AppendLine("Show");
                sb.AppendLine("    Rarity <= Rare");
                sb.Append(_baseTypePrefix);
                foreach (var baseName in progressionBases)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" \"{baseName}\"");
                }
                sb.AppendLine();
                AppendStyleBlock(sb, activeTheme.ProgressionBases);
                sb.AppendLine();
            }
        }

        private static void AppendSkillGemHighlights(StringBuilder sb, PoeBuild? activeBuild, int playerLevel, FilterTheme activeTheme)
        {
            if (activeBuild == null) return;

            var buildGems = activeBuild.Skills
                .Where(skill => {
                    var name = !string.IsNullOrWhiteSpace(skill.Name) ? skill.Name : GetGemNameFromId(skill.Id);
                    if (string.IsNullOrWhiteSpace(name)) return false;
                    if (skill.LevelInterval == null || skill.LevelInterval.Count < 2) return true;
                    return playerLevel >= skill.LevelInterval[0] && playerLevel <= skill.LevelInterval[1];
                })
                .Select(skill => !string.IsNullOrWhiteSpace(skill.Name) ? skill.Name : GetGemNameFromId(skill.Id))
                .Distinct()
                .ToList();

            if (buildGems.Count > 0)
            {
                sb.AppendLine("# Active Build Skill Gems");
                sb.AppendLine("Show");
                sb.AppendLine("    Class \"Skill Gems\"");
                sb.Append(_baseTypePrefix);
                foreach (var gem in buildGems)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" \"{gem}\"");
                }
                sb.AppendLine();
                AppendStyleBlock(sb, activeTheme.Gems);
                sb.AppendLine();
            }
        }

        private static void AppendDynamicEconomyHighlights(StringBuilder sb, List<MarketItem> marketItems, double tier1Threshold, double tier2Threshold, FilterTheme activeTheme)
        {
            sb.AppendLine(_sectionSeparator);
            sb.AppendLine("# DYNAMIC ECONOMY HIGHLIGHTS (poe.ninja)");
            sb.AppendLine(_sectionSeparator);

            var tier1Items = marketItems.Where(i => i.DivineValue >= tier1Threshold && i.Category != "Exchange Rate").ToList();
            if (tier1Items.Count > 0)
            {
                sb.AppendLine("# Tier 1 Economy Items");
                sb.AppendLine("Show");
                sb.Append(_baseTypePrefix);
                foreach (var item in tier1Items)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" \"{item.Name}\"");
                }
                sb.AppendLine();
                AppendStyleBlock(sb, activeTheme.EconomyTier1);
                sb.AppendLine();
            }

            var tier2Items = marketItems.Where(i => i.ExaltedValue >= tier2Threshold && i.DivineValue < tier1Threshold && i.Category != "Exchange Rate").ToList();
            if (tier2Items.Count > 0)
            {
                sb.AppendLine("# Tier 2 Economy Items");
                sb.AppendLine("Show");
                sb.Append(_baseTypePrefix);
                foreach (var item in tier2Items)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" \"{item.Name}\"");
                }
                sb.AppendLine();
                AppendStyleBlock(sb, activeTheme.EconomyTier2);
                sb.AppendLine();
            }
        }

        private static void AppendFallbackOrBaseRules(StringBuilder sb, string? baseFilterPath)
        {
            if (!string.IsNullOrEmpty(baseFilterPath) && File.Exists(baseFilterPath))
            {
                sb.AppendLine(_sectionSeparator);
                sb.AppendLine("# APPENDED BASE FILTER RULES");
                sb.AppendLine(_sectionSeparator);
                var baseFilterContent = File.ReadAllText(baseFilterPath);
                sb.AppendLine(baseFilterContent);
            }
            else
            {
                sb.AppendLine("# Fallback basic catch-all rules");
                sb.AppendLine("Show");
                sb.AppendLine("    Class \"Currency\"");
                sb.AppendLine("    SetFontSize 35");
                sb.AppendLine("Show");
                sb.AppendLine("    Class \"Waystones\"");
                sb.AppendLine("    SetFontSize 35");
                sb.AppendLine("Show");
                sb.AppendLine("    Rarity >= Rare");
                sb.AppendLine("    SetFontSize 35");
                sb.AppendLine("Show");
                sb.AppendLine("    SetFontSize 30");
            }
        }


        private static string GetGemNameFromId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return string.Empty;

            // Remove typical path prefixes
            var clean = id.Replace("Metadata/Items/Gem/SkillGem", "", StringComparison.Ordinal)
                          .Replace("Metadata/Items/Gems/SkillGem", "", StringComparison.Ordinal)
                          .Replace("Metadata/Items/Gems/SupportGem", "", StringComparison.Ordinal)
                          .Replace("Metadata/Items/Gem/SupportGem", "", StringComparison.Ordinal)
                          .Replace("Metadata/Items/Gem/", "", StringComparison.Ordinal)
                          .Replace("Metadata/Items/Gems/", "", StringComparison.Ordinal);

            // Split camel case & numbers, e.g. "PlayerDefault1HMace" -> "Player Default 1 H Mace"
            var sb = new StringBuilder();
            for (int i = 0; i < clean.Length; i++)
            {
                if (i > 0)
                {
                    char prev = clean[i - 1];
                    char curr = clean[i];
                    char? next = (i + 1 < clean.Length) ? clean[i + 1] : (char?)null;

                    bool insertSpace = (char.IsLower(prev) && char.IsUpper(curr))
                        || (char.IsUpper(prev) && char.IsUpper(curr) && next.HasValue && char.IsLower(next.Value))
                        || (char.IsLetter(prev) && char.IsDigit(curr))
                        || (char.IsDigit(prev) && char.IsLetter(curr));

                    if (insertSpace)
                    {
                        sb.Append(' ');
                    }
                }
                sb.Append(clean[i]);
            }
            return sb.ToString().Trim();
        }

        private sealed class BaseItemInfo(string name, int requiredLevel)
        {
            public string Name { get; } = name;
            public int RequiredLevel { get; } = requiredLevel;
        }

        private static readonly Dictionary<string, List<BaseItemInfo>> _archetypes = new()
        {
            { "Mace", new() {
                new("Iron Mace", 1),
                new("Bronze Mace", 10),
                new("Steel Mace", 20),
                new("Flanged Mace", 32),
                new("Ornate Mace", 44),
                new("Jagged Mace", 56),
                new("Petrified Mace", 68),
                new("Sacred Maul", 72)
            }},
            { "Staff", new() {
                new("Wrapped Quarterstaff", 1),
                new("Long Quarterstaff", 10),
                new("Iron-Point Quarterstaff", 20),
                new("Laminated Quarterstaff", 32),
                new("Spliced Quarterstaff", 44),
                new("Steel-Point Quarterstaff", 56),
                new("Crescent Quarterstaff", 68),
                new("Gothic Quarterstaff", 72)
            }},
            { "Bow", new() {
                new("Crude Bow", 1),
                new("Short Bow", 10),
                new("Longbow", 20),
                new("Recurve Bow", 32),
                new("Composite Bow", 44),
                new("Dual-Metal Bow", 56),
                new("Warden's Bow", 68),
                new("Greatbow", 72)
            }},
            { "Sword", new() {
                new("Rusted Sword", 1),
                new("Copper Sword", 10),
                new("Sabre", 20),
                new("Broadsword", 32),
                new("Falchion", 44),
                new("Cutlass", 56),
                new("Estoc", 68),
                new("Greatsword", 72)
            }},
            { "Crossbow", new() {
                new("Makeshift Crossbow", 1),
                new("Light Crossbow", 10),
                new("Arbalest", 20),
                new("Recurve Crossbow", 32),
                new("Heavy Crossbow", 44),
                new("Steel Crossbow", 56),
                new("Grand Crossbow", 68),
                new("Siege Crossbow", 72)
            }},
            { "Wand", new() {
                new("Twig Wand", 1),
                new("Carved Wand", 10),
                new("Engraved Wand", 20),
                new("Ash Wand", 32),
                new("Driftwood Wand", 44),
                new("Sage Wand", 56),
                new("Omen Wand", 68),
                new("Prophecy Wand", 72)
            }},
            { "Body Armour Armour", new() {
                new("Plate Vest", 1),
                new("Chainmail Vest", 10),
                new("Ringmail Coat", 20),
                new("Scale Vest", 30),
                new("Knightly Plate", 60),
                new("Majestic Plate", 75)
            }},
            { "Body Armour Evasion", new() {
                new("Laced Jacket", 1),
                new("Rawhide Jacket", 10),
                new("Leather Jacket", 20),
                new("Buckskin Jerkin", 30),
                new("Wildspire Jerkin", 40),
                new("Hunter's Jacket", 50),
                new("Corsair Jerkin", 60),
                new("Falconer's Jacket", 70)
            }},
            { "Body Armour ES", new() {
                new("Robe", 1),
                new("Velvet Robe", 10),
                new("Silk Robe", 20),
                new("Scholar's Robe", 30),
                new("Mage's Vestment", 40),
                new("Sage's Robe", 50),
                new("Cabalist Regalia", 60),
                new("Silken Wrap", 70),
                new("Grand Regalia", 75)
            }},
            { "Boots Armour", new() {
                new("Iron Greaves", 1),
                new("Chainmail Boots", 12),
                new("Ringmail Boots", 22),
                new("Knightly Greaves", 62)
            }},
            { "Boots Evasion", new() {
                new("Wrapped Boots", 1),
                new("Rawhide Boots", 12),
                new("Leather Boots", 22),
                new("Goatskin Boots", 32),
                new("Wildspire Boots", 42),
                new("Hunter's Boots", 52),
                new("Corsair Boots", 62),
                new("Falconer's Boots", 72)
            }},
            { "Boots ES", new() {
                new("Wool Shoes", 1),
                new("Velvet Slippers", 12),
                new("Silk Slippers", 22),
                new("Scholar's Boots", 32),
                new("Mage's Shoes", 42),
                new("Sage's Shoes", 52),
                new("Cabalist Slippers", 62),
                new("Silken Slippers", 72)
            }},
            { "Gloves Armour", new() {
                new("Iron Gauntlets", 1),
                new("Chainmail Gloves", 11),
                new("Ringmail Gauntlets", 21),
                new("Knightly Gauntlets", 61)
            }},
            { "Gloves Evasion", new() {
                new("Wrapped Mitts", 1),
                new("Rawhide Gloves", 11),
                new("Leather Gloves", 21),
                new("Goatskin Gloves", 31),
                new("Wildspire Gauntlets", 41),
                new("Hunter's Gloves", 51),
                new("Corsair Gloves", 61),
                new("Falconer's Gloves", 71)
            }},
            { "Gloves ES", new() {
                new("Wool Gloves", 1),
                new("Velvet Gloves", 11),
                new("Silk Gloves", 21),
                new("Scholar's Gloves", 31),
                new("Mage's Gloves", 41),
                new("Sage's Gloves", 51),
                new("Cabalist Gloves", 61),
                new("Silken Gloves", 71)
            }},
            { "Helm Armour", new() {
                new("Iron Hat", 1),
                new("Chainmail Coif", 10),
                new("Ringmail Helm", 20),
                new("Knightly Helm", 60)
            }},
            { "Helm Evasion", new() {
                new("Wrapped Cap", 1),
                new("Rawhide Mask", 10),
                new("Leather Hood", 20),
                new("Goatskin Mask", 30),
                new("Wildspire Mask", 40),
                new("Hunter's Hood", 50),
                new("Corsair Helmet", 60),
                new("Falconer's Helmet", 70)
            }},
            { "Helm ES", new() {
                new("Wool Hood", 1),
                new("Velvet Hood", 10),
                new("Silk Hood", 20),
                new("Scholar's Hood", 30),
                new("Mage's Hood", 40),
                new("Sage's Hood", 50),
                new("Cabalist Hood", 60),
                new("Silken Hood", 70)
            }},
            { "Belt", new() {
                new("Rustic Sash", 1),
                new("Chain Belt", 1),
                new("Leather Belt", 15),
                new("Heavy Belt", 15),
                new("Cloth Belt", 30),
                new("Studded Belt", 30),
                new("Crystal Belt", 45),
                new("Vanguard Belt", 45),
                new("Utility Belt", 60)
            }},
            { "Ring", new() {
                new("Iron Ring", 1),
                new("Coral Ring", 1),
                new("Paua Ring", 12),
                new("Gold Ring", 12),
                new("Sapphire Ring", 24),
                new("Topaz Ring", 24),
                new("Ruby Ring", 24),
                new("Diamond Ring", 36),
                new("Prismatic Ring", 36),
                new("Unset Ring", 48),
                new("Moonstone Ring", 48)
            }},
            { "Charm", new() {
                new("Stone Charm", 8),
                new("Golden Charm", 12)
            }}
        };

        private static readonly (string[] Keywords, string Archetype)[] _keywordArchetypeMappings =
        [
            (["maul", "mace"], "Mace"),
            (["quarterstaff", "staff"], "Staff"),
            (["crossbow"], "Crossbow"),
            (["bow"], "Bow"),
            (["sword", "sabre", "falchion", "cutlass", "greatsword"], "Sword"),
            (["wand"], "Wand"),
            (["jacket", "jerkin"], "Body Armour Evasion"),
            (["plate", "chainmail", "ringmail", "scale vest"], "Body Armour Armour"),
            (["robe", "vestment", "regalia", "wrap"], "Body Armour ES"),
            (["belt", "sash"], "Belt"),
            (["ring"], "Ring"),
            (["charm"], "Charm")
        ];

        private static BaseItemInfo? FindMatchedBase(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return null;

            return _archetypes.Values
                .SelectMany(list => list)
                .FirstOrDefault(baseItem => itemName.Contains(baseItem.Name, StringComparison.OrdinalIgnoreCase));
        }

        private static List<BaseItemInfo> FindBootsArchetype(string cleanName)
        {
            if (cleanName.Contains("greaves", StringComparison.Ordinal)) return _archetypes["Boots Armour"];
            if (cleanName.Contains("slippers", StringComparison.Ordinal) || cleanName.Contains("shoes", StringComparison.Ordinal)) return _archetypes["Boots ES"];
            return _archetypes["Boots Evasion"];
        }

        private static List<BaseItemInfo> FindGlovesArchetype(string cleanName)
        {
            if (cleanName.Contains("gauntlets", StringComparison.Ordinal)) return _archetypes["Gloves Armour"];
            if (cleanName.Contains("gloves", StringComparison.Ordinal) && (cleanName.Contains("wool", StringComparison.Ordinal) || cleanName.Contains("silk", StringComparison.Ordinal) || cleanName.Contains("sage", StringComparison.Ordinal) || cleanName.Contains("cabalist", StringComparison.Ordinal))) return _archetypes["Gloves ES"];
            return _archetypes["Gloves Evasion"];
        }

        private static List<BaseItemInfo> FindHelmArchetype(string cleanName)
        {
            if (cleanName.Contains("hat", StringComparison.Ordinal) || cleanName.Contains("helm", StringComparison.Ordinal) || cleanName.Contains("coif", StringComparison.Ordinal)) return _archetypes["Helm Armour"];
            if (cleanName.Contains("hood", StringComparison.Ordinal) || cleanName.Contains("circlet", StringComparison.Ordinal)) return _archetypes["Helm ES"];
            return _archetypes["Helm Evasion"];
        }

        private static bool IsBoots(string name) =>
            name.Contains("boots", StringComparison.Ordinal) ||
            name.Contains("greaves", StringComparison.Ordinal) ||
            name.Contains("slippers", StringComparison.Ordinal) ||
            name.Contains("shoes", StringComparison.Ordinal);

        private static bool IsGloves(string name) =>
            name.Contains("gauntlets", StringComparison.Ordinal) ||
            name.Contains("gloves", StringComparison.Ordinal) ||
            name.Contains("mitts", StringComparison.Ordinal);

        private static bool IsHelm(string name) =>
            name.Contains("helm", StringComparison.Ordinal) ||
            name.Contains("helmet", StringComparison.Ordinal) ||
            name.Contains("cap", StringComparison.Ordinal) ||
            name.Contains("mask", StringComparison.Ordinal) ||
            name.Contains("hood", StringComparison.Ordinal) ||
            name.Contains("coif", StringComparison.Ordinal) ||
            name.Contains("circlet", StringComparison.Ordinal);

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Comparing with lowercase string literals is more readable and matches the established casing pattern for game items.")]
        private static List<BaseItemInfo>? FindArchetypeList(string baseItemName)
        {
            if (string.IsNullOrWhiteSpace(baseItemName)) return null;

            var matchedList = _archetypes.Values.FirstOrDefault(list => list.Any(b => b.Name.Equals(baseItemName, StringComparison.OrdinalIgnoreCase)));
            if (matchedList != null)
            {
                return matchedList;
            }

            var cleanName = baseItemName.ToLowerInvariant();

            if (IsBoots(cleanName)) return FindBootsArchetype(cleanName);
            if (IsGloves(cleanName)) return FindGlovesArchetype(cleanName);
            if (IsHelm(cleanName)) return FindHelmArchetype(cleanName);

            var (_, archetype) = _keywordArchetypeMappings.FirstOrDefault(m => m.Keywords.Any(k => cleanName.Contains(k, StringComparison.Ordinal)));
            if (archetype != null)
            {
                return _archetypes[archetype];
            }

            return null;
        }

        private static string CleanItemBaseName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                var candidate = string.Join(" ", parts.Skip(1));
                return candidate;
            }
            return name;
        }

        private static void AppendStyleBlock(StringBuilder sb, CategoryStyle style)
        {
            if (!style.Enabled) return;

            // 1. Text Color
            var textColor = ParseHexToRgbaString(style.TextColor);
            if (!string.IsNullOrEmpty(textColor))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    SetTextColor {textColor}");
            }

            // 2. Border Color
            var borderColor = ParseHexToRgbaString(style.BorderColor);
            if (!string.IsNullOrEmpty(borderColor))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    SetBorderColor {borderColor}");
            }

            // 3. Background Color
            if (!string.IsNullOrEmpty(style.BackgroundColor) && style.BackgroundColor != "#00000000" && style.BackgroundColor != "#000000" && style.BackgroundColor != "#0")
            {
                var backgroundColor = ParseHexToRgbaString(style.BackgroundColor);
                if (!string.IsNullOrEmpty(backgroundColor))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    SetBackgroundColor {backgroundColor}");
                }
            }

            // 4. Font Size
            int clampedFontSize = Math.Max(18, Math.Min(45, style.FontSize));
            sb.AppendLine(CultureInfo.InvariantCulture, $"    SetFontSize {clampedFontSize}");

            // 5. Audio Settings
            if (style.SoundType == "BuiltIn")
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    PlayAlertSound {style.AlertSoundId} 300");
            }
            else if (style.SoundType == "Custom" && !string.IsNullOrWhiteSpace(style.CustomSoundPath))
            {
                var cleanPath = style.CustomSoundPath.Replace('\\', '/');
                sb.AppendLine(CultureInfo.InvariantCulture, $"    CustomAlertSound \"{cleanPath}\"");
            }

            // 6. Mute default drop sound
            if (style.MuteDefaultDropSound)
            {
                sb.AppendLine("    DisableDropSound");
            }

            // 7. Minimap Icon
            if (!string.IsNullOrWhiteSpace(style.MinimapIcon))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    MinimapIcon {style.MinimapIcon}");
            }

            // 8. Play Effect
            if (!string.IsNullOrWhiteSpace(style.PlayEffect))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    PlayEffect {style.PlayEffect}");
            }
        }

        public static string ParseHexToRgbaString(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return "";

            // Trim leading '#' if present
            hex = hex.Trim().TrimStart('#');

            // Handle shorthand hex like "FFF" or "FFFF"
            if (hex.Length == 3)
            {
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            }

            byte r, g, b, a;

            try
            {
                if (hex.Length == 6)
                {
                    // RRGGBB
                    r = Convert.ToByte(hex[0..2], 16);
                    g = Convert.ToByte(hex[2..4], 16);
                    b = Convert.ToByte(hex[4..6], 16);
                    a = 255;
                }
                else if (hex.Length == 8)
                {
                    // WPF standard uses AARRGGBB.
                    a = Convert.ToByte(hex[0..2], 16);
                    r = Convert.ToByte(hex[2..4], 16);
                    g = Convert.ToByte(hex[4..6], 16);
                    b = Convert.ToByte(hex[6..8], 16);
                }
                else
                {
                    return "";
                }

                return $"{r} {g} {b} {a}";
            }
            catch
            {
                return "";
            }
        }

        private static readonly Dictionary<string, string> _knownUniqueBaseTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Myris Uxor", "Covert Hood" },
            { "Morior Invictus", "Grand Regalia" },
            { "Horror's Flight", "Engraved Bracers" },
            { "Lavianga's Spirits", "Gargantuan Mana Flask" },
            { "The Fall of the Axe", "Silver Charm" },
            { "Nascent Hope", "Thawing Charm" },
            { "Astramentis", "Stellar Amulet" },
            { "Polcirkeln", "Sapphire Ring" },
            { "Chernobog's Pillar", "Blacksteel Tower Shield" },
            { "Constricting Command", "Viper Cap" },
            { "Time-Lost Diamond", "Time-Lost Diamond" }
        };

        private static string GetUniqueBaseType(string uniqueName, List<MarketItem> marketItems)
        {
            if (string.IsNullOrWhiteSpace(uniqueName)) return uniqueName;

            // 1. Check if the name itself is already a known base type
            if (_archetypes.Values.Any(list => list.Any(b => b.Name.Equals(uniqueName, StringComparison.OrdinalIgnoreCase))))
            {
                return uniqueName;
            }

            // 2. Try static dictionary lookup
            if (_knownUniqueBaseTypes.TryGetValue(uniqueName, out var staticBase))
            {
                return staticBase;
            }

            // 3. Try dynamic lookup from synced marketItems
            if (marketItems != null)
            {
                var matched = marketItems.Find(i => i.Name.Equals(uniqueName, StringComparison.OrdinalIgnoreCase));
                if (matched != null && !string.IsNullOrEmpty(matched.BaseType))
                {
                    return matched.BaseType;
                }
            }

            return string.Empty;
        }
    }
}
