using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
            FilterTheme? activeTheme = null,
            bool showEconomyHighlights = true
        )
        {
            try
            {
                activeTheme ??= new FilterTheme();
                marketItems ??= [];

                EnsureMarketValuesNormalized(marketItems);

                var sb = new StringBuilder();

                AppendHeader(sb, playerLevel, zoneLevel, baseFilterPath);

                var buildUniqueItems = GetBuildUniqueItemNames(activeBuild, playerLevel, marketItems);
                AppendUniqueHighlights(sb, activeBuild, playerLevel, buildUniqueItems, activeTheme);

                var progressionBases = GetBuildProgressionBaseNames(activeBuild, playerLevel, zoneLevel);
                AppendProgressionBaseHighlights(sb, progressionBases, activeTheme);

                var buildGems = GetBuildSkillGemNames(activeBuild, playerLevel);
                AppendSkillGemHighlights(sb, buildGems, activeTheme);

                List<MarketItem> tier1Items = [];
                List<MarketItem> tier2Items = [];
                if (showEconomyHighlights)
                {
                    tier1Items = GetTier1EconomyItems(marketItems, tier1Threshold);
                    tier2Items = GetTier2EconomyItems(marketItems, tier1Threshold, tier2Threshold);
                    AppendDynamicEconomyHighlights(sb, tier1Items, tier2Items, activeTheme);
                }

                var highlightedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                highlightedNames.UnionWith(buildUniqueItems);
                highlightedNames.UnionWith(progressionBases);
                highlightedNames.UnionWith(buildGems);
                highlightedNames.UnionWith(tier1Items.Select(i => i.Name));
                highlightedNames.UnionWith(tier2Items.Select(i => i.Name));

                AppendFallbackOrBaseRules(sb, baseFilterPath, highlightedNames);

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

            double divinePriceInChaos = Trade.CurrencyRates.DefaultDivineInChaos;
            double exaltedPriceInChaos = Trade.CurrencyRates.DefaultExaltedInChaos;

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

        private static List<string> GetBuildUniqueItemNames(PoeBuild? activeBuild, int playerLevel, List<MarketItem> marketItems)
        {
            if (activeBuild == null) return [];

            return activeBuild.InventorySlots
                .Where(slot => !string.IsNullOrWhiteSpace(slot.ItemName) && !string.IsNullOrEmpty(slot.UniqueName))
                .Where(slot => {
                    if (slot.LevelInterval == null || slot.LevelInterval.Count < 2) return true;
                    return playerLevel >= slot.LevelInterval[0] && playerLevel <= slot.LevelInterval[1];
                })
                .Select(slot => GetUniqueBaseType(slot.ItemName, marketItems))
                .Where(baseType => !string.IsNullOrEmpty(baseType))
                .Distinct()
                .ToList();
        }

        private static void AppendUniqueHighlights(StringBuilder sb, PoeBuild? activeBuild, int playerLevel, List<string> buildUniqueItems, FilterTheme activeTheme)
        {
            if (activeBuild == null || buildUniqueItems.Count == 0) return;

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

        private static HashSet<string> GetBuildProgressionBaseNames(PoeBuild? activeBuild, int playerLevel, int zoneLevel)
        {
            var progressionBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (activeBuild == null) return progressionBases;

            var buildBases = activeBuild.InventorySlots
                .Where(slot => string.IsNullOrEmpty(slot.UniqueName) && !string.IsNullOrEmpty(slot.ItemName))
                .Where(slot => {
                    if (slot.LevelInterval == null || slot.LevelInterval.Count < 2) return true;
                    return playerLevel >= slot.LevelInterval[0] && playerLevel <= slot.LevelInterval[1];
                })
                .Select(slot => slot.ItemName)
                .ToList();

            foreach (var itemName in buildBases)
            {
                GetProgressionBasesForSlot(itemName, playerLevel, zoneLevel, progressionBases);
            }

            return progressionBases;
        }

        private static void AppendProgressionBaseHighlights(StringBuilder sb, HashSet<string> progressionBases, FilterTheme activeTheme)
        {
            if (progressionBases.Count == 0) return;

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

        private static List<string> GetBuildSkillGemNames(PoeBuild? activeBuild, int playerLevel)
        {
            if (activeBuild == null) return [];

            return activeBuild.Skills
                .Where(skill => {
                    var name = !string.IsNullOrWhiteSpace(skill.Name) ? skill.Name : GetGemNameFromId(skill.Id);
                    if (string.IsNullOrWhiteSpace(name)) return false;
                    if (skill.LevelInterval == null || skill.LevelInterval.Count < 2) return true;
                    return playerLevel >= skill.LevelInterval[0] && playerLevel <= skill.LevelInterval[1];
                })
                .Select(skill => !string.IsNullOrWhiteSpace(skill.Name) ? skill.Name : GetGemNameFromId(skill.Id))
                .Distinct()
                .ToList();
        }

        // PoE2 does not drop pre-named skill/support gems on the ground - only the generic
        // "Uncut Skill Gem" / "Uncut Support Gem" / "Uncut Spirit Gem" BaseTypes exist, and
        // the player allocates a specific skill when using one (the "Uncut Gem" framework -
        // see poe2_validator.md). Writing a named skill like "Wolf Pounce" as a BaseType rule
        // breaks the whole filter on load, since that string is never a real item BaseType.
        private static void AppendSkillGemHighlights(StringBuilder sb, List<string> buildGems, FilterTheme activeTheme)
        {
            if (buildGems.Count == 0) return;

            sb.AppendLine(CultureInfo.InvariantCulture, $"# Active Build Skills (Uncut Gem framework): {string.Join(", ", buildGems)}");
            sb.AppendLine("Show");
            sb.AppendLine("    BaseType \"Uncut Skill Gem\" \"Uncut Support Gem\" \"Uncut Spirit Gem\"");
            AppendStyleBlock(sb, activeTheme.Gems);
            sb.AppendLine();
        }

        private static List<MarketItem> GetTier1EconomyItems(List<MarketItem> marketItems, double tier1Threshold)
        {
            return marketItems.Where(i => i.DivineValue >= tier1Threshold && i.Category != "Exchange Rate").ToList();
        }

        private static List<MarketItem> GetTier2EconomyItems(List<MarketItem> marketItems, double tier1Threshold, double tier2Threshold)
        {
            return marketItems.Where(i => i.ExaltedValue >= tier2Threshold && i.DivineValue < tier1Threshold && i.Category != "Exchange Rate").ToList();
        }

        private static void AppendDynamicEconomyHighlights(StringBuilder sb, List<MarketItem> tier1Items, List<MarketItem> tier2Items, FilterTheme activeTheme)
        {
            sb.AppendLine(_sectionSeparator);
            sb.AppendLine("# DYNAMIC ECONOMY HIGHLIGHTS (poe.ninja)");
            sb.AppendLine(_sectionSeparator);

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

        private static void AppendFallbackOrBaseRules(StringBuilder sb, string? baseFilterPath, HashSet<string> highlightedNames)
        {
            if (!string.IsNullOrEmpty(baseFilterPath) && File.Exists(baseFilterPath))
            {
                sb.AppendLine(_sectionSeparator);
                sb.AppendLine("# APPENDED BASE FILTER RULES");
                sb.AppendLine(_sectionSeparator);
                var baseFilterContent = File.ReadAllText(baseFilterPath);
                var cleanedContent = StripDuplicateBaseTypeRules(baseFilterContent, highlightedNames);
                sb.AppendLine(cleanedContent);
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

        private static readonly Regex _quotedTokenRegex = new("\"([^\"]*)\"", RegexOptions.Compiled);

        private static readonly HashSet<string> _styleDirectiveKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "SetTextColor", "SetBorderColor", "SetBackgroundColor", "SetFontSize",
            "PlayAlertSound", "PlayEffect", "MinimapIcon", "DisableDropSound", "CustomAlertSound", "Continue"
        };

        /// <summary>
        /// Removes BaseType references to items that are already covered by a dynamically
        /// generated highlight block, so the appended base filter doesn't define a conflicting
        /// duplicate rule for the same item further down the file.
        /// </summary>
        internal static string StripDuplicateBaseTypeRules(string filterContent, HashSet<string> highlightedNames)
        {
            if (string.IsNullOrEmpty(filterContent) || highlightedNames == null || highlightedNames.Count == 0)
            {
                return filterContent;
            }

            var lines = filterContent.Split('\n');
            var blocks = new List<List<string>>();
            List<string>? currentBlock = null;

            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                bool isBlockStart = trimmed.StartsWith("Show", StringComparison.Ordinal) || trimmed.StartsWith("Hide", StringComparison.Ordinal);

                if (isBlockStart)
                {
                    currentBlock = [];
                    blocks.Add(currentBlock);
                }

                if (currentBlock == null)
                {
                    blocks.Add([line]);
                    continue;
                }

                currentBlock.Add(line);
            }

            var resultLines = new List<string>();
            foreach (var block in blocks)
            {
                var cleaned = StripBaseTypeTokensFromBlock(block, highlightedNames);
                if (cleaned != null)
                {
                    resultLines.AddRange(cleaned);
                }
            }

            return string.Join("\n", resultLines);
        }

        private static List<string>? StripBaseTypeTokensFromBlock(List<string> block, HashSet<string> highlightedNames)
        {
            bool isRuleBlock = block.Count > 0 &&
                (block[0].TrimStart().StartsWith("Show", StringComparison.Ordinal) || block[0].TrimStart().StartsWith("Hide", StringComparison.Ordinal));
            if (!isRuleBlock) return block;

            bool strippedAnyToken = false;
            bool hasRemainingCondition = false;
            var resultLines = new List<string>();

            foreach (var line in block)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("BaseType", StringComparison.Ordinal))
                {
                    var (remainingLine, removedAny, hasTokens) = RemoveQuotedTokens(line, highlightedNames);
                    strippedAnyToken |= removedAny;
                    if (hasTokens)
                    {
                        resultLines.Add(remainingLine);
                        hasRemainingCondition = true;
                    }
                    continue;
                }

                resultLines.Add(line);
                if (!hasRemainingCondition && IsConditionLine(trimmed))
                {
                    hasRemainingCondition = true;
                }
            }

            if (!strippedAnyToken) return block;

            // Dropping all BaseType tokens with no other condition left would turn this
            // into an unconditional catch-all rule, which is worse than the duplicate it
            // was meant to fix - drop the whole block instead.
            return hasRemainingCondition ? resultLines : null;
        }

        private static (string Line, bool RemovedAny, bool HasTokens) RemoveQuotedTokens(string line, HashSet<string> highlightedNames)
        {
            var matches = _quotedTokenRegex.Matches(line);
            if (matches.Count == 0) return (line, false, false);

            var keptTokens = new List<string>();
            bool removedAny = false;

            foreach (Match match in matches)
            {
                var token = match.Groups[1].Value;
                if (highlightedNames.Contains(token))
                {
                    removedAny = true;
                }
                else
                {
                    keptTokens.Add(token);
                }
            }

            if (!removedAny) return (line, false, keptTokens.Count > 0);

            var firstQuoteIndex = line.IndexOf('"', StringComparison.Ordinal);
            var prefix = firstQuoteIndex >= 0 ? line[..firstQuoteIndex].TrimEnd() : line.TrimEnd();
            if (keptTokens.Count == 0)
            {
                return (prefix, true, false);
            }

            var rebuilt = prefix + " " + string.Join(' ', keptTokens.Select(t => $"\"{t}\""));
            return (rebuilt, true, true);
        }

        private static bool IsConditionLine(string trimmedLine)
        {
            if (trimmedLine.Length == 0) return false;
            if (trimmedLine.StartsWith("Show", StringComparison.Ordinal) || trimmedLine.StartsWith("Hide", StringComparison.Ordinal)) return false;

            var keyword = trimmedLine.Split(' ', 2)[0];
            return !_styleDirectiveKeywords.Contains(keyword);
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

        // Base item names and required levels verified against poe2db.tw (2026-06-17).
        // An earlier version of this table was copied from Path of Exile 1 base names
        // (e.g. "Paua Ring", "Iron Mace", "Falconer's Jacket") that do not exist in PoE2 -
        // the game rejects the whole filter on load if a BaseType rule references one.
        // Do not add entries here without verifying them against poe2db.tw first.
        private static readonly Dictionary<string, List<BaseItemInfo>> _archetypes = new()
        {
            { "Mace", new() {
                new("Wooden Club", 1),
                new("Smithing Hammer", 4),
                new("Forge Maul", 11),
                new("Spiked Club", 16),
                new("Cultist Greathammer", 22),
                new("Brigand Mace", 33),
                new("Crumbling Maul", 38),
                new("Morning Star", 45),
                new("Totemic Greatclub", 50),
                new("Giant Maul", 65),
                new("Sacred Maul", 72),
                new("Massive Greathammer", 77)
            }},
            { "Staff", new() {
                new("Wrapped Quarterstaff", 1),
                new("Long Quarterstaff", 4),
                new("Gothic Quarterstaff", 11),
                new("Crackling Quarterstaff", 16),
                new("Crescent Quarterstaff", 20),
                new("Steelpoint Quarterstaff", 28),
                new("Barrier Quarterstaff", 37),
                new("Guardian Quarterstaff", 62),
                new("Lunar Quarterstaff", 72),
                new("Aegis Quarterstaff", 79)
            }},
            { "Bow", new() {
                new("Crude Bow", 1),
                new("Shortbow", 5),
                new("Warden Bow", 11),
                new("Recurve Bow", 16),
                new("Composite Bow", 22),
                new("Dualstring Bow", 28),
                new("Cultist Bow", 33),
                new("Artillery Bow", 45),
                new("Heavy Bow", 65),
                new("Cavalry Bow", 72),
                new("Fanatic Bow", 79)
            }},
            { "Crossbow", new() {
                new("Makeshift Crossbow", 1),
                new("Tense Crossbow", 4),
                new("Sturdy Crossbow", 10),
                new("Varnished Crossbow", 16),
                new("Alloy Crossbow", 26),
                new("Bombard Crossbow", 33),
                new("Blackfire Crossbow", 45),
                new("Cannonade Crossbow", 59),
                new("Stout Crossbow", 67),
                new("Siege Crossbow", 79)
            }},
            { "Wand", new() {
                new("Withered Wand", 1),
                new("Bone Wand", 2),
                new("Siphoning Wand", 11),
                new("Volatile Wand", 16),
                new("Galvanic Wand", 25),
                new("Acrid Wand", 33),
                new("Frigid Wand", 45),
                new("Primordial Wand", 56),
                new("Runic Fork", 65)
            }},
            { "Body Armour Armour", new() {
                new("Rusted Cuirass", 1),
                new("Fur Plate", 4),
                new("Iron Cuirass", 11),
                new("Raider Plate", 16),
                new("Maraketh Cuirass", 20),
                new("Steel Plate", 27),
                new("Full Plate", 33),
                new("Vaal Cuirass", 37),
                new("Juggernaut Plate", 45),
                new("Chieftain Cuirass", 50),
                new("Glorious Plate", 65),
                new("Abyssal Cuirass", 73)
            }},
            { "Body Armour Evasion", new() {
                new("Leather Vest", 1),
                new("Quilted Vest", 4),
                new("Pathfinder Coat", 11),
                new("Shrouded Vest", 16),
                new("Rhoahide Coat", 22),
                new("Studded Vest", 26),
                new("Scout's Vest", 33),
                new("Serpentscale Coat", 36),
                new("Corsair Vest", 45),
                new("Exquisite Vest", 65),
                new("Armoured Vest", 73)
            }},
            { "Body Armour ES", new() {
                new("Tattered Robe", 1),
                new("Feathered Robe", 5),
                new("Hexer's Robe", 11),
                new("Bone Raiment", 16),
                new("Silk Robe", 22),
                new("Votive Raiment", 33),
                new("Altar Robe", 40),
                new("Elementalist Robe", 45),
                new("Imperial Robe", 52),
                new("Havoc Raiment", 65),
                new("Arcane Raiment", 73)
            }},
            { "Boots Armour", new() {
                new("Rough Greaves", 1),
                new("Iron Greaves", 11),
                new("Bronze Greaves", 16),
                new("Trimmed Greaves", 27),
                new("Stone Greaves", 33),
                new("Reefsteel Greaves", 45),
                new("Bulwark Greaves", 65),
                new("Vaal Greaves", 75),
                new("Tasalian Greaves", 80)
            }},
            { "Boots Evasion", new() {
                new("Rawhide Boots", 1),
                new("Laced Boots", 11),
                new("Embossed Boots", 16),
                new("Steeltoe Boots", 28),
                new("Lizardscale Boots", 33),
                new("Flared Boots", 45),
                new("Cinched Boots", 65),
                new("Dragonscale Boots", 75),
                new("Drakeskin Boots", 80)
            }},
            { "Boots ES", new() {
                new("Straw Sandals", 1),
                new("Wrapped Sandals", 11),
                new("Lattice Sandals", 16),
                new("Silk Slippers", 27),
                new("Feathered Sandals", 33),
                new("Bound Sandals", 65),
                new("Sandsworn Sandals", 75),
                new("Sekhema Sandals", 80)
            }},
            { "Gloves Armour", new() {
                new("Stocky Mitts", 1),
                new("Riveted Mitts", 11),
                new("Tempered Mitts", 16),
                new("Bolstered Mitts", 27),
                new("Moulded Mitts", 33),
                new("Detailed Mitts", 45),
                new("Titan Mitts", 52),
                new("Grand Mitts", 65)
            }},
            { "Gloves Evasion", new() {
                new("Suede Bracers", 1),
                new("Firm Bracers", 11),
                new("Bound Bracers", 16),
                new("Sectioned Bracers", 28),
                new("Spined Bracers", 33),
                new("Fine Bracers", 45),
                new("Hardened Bracers", 52),
                new("Engraved Bracers", 65)
            }},
            { "Gloves ES", new() {
                new("Torn Gloves", 1),
                new("Sombre Gloves", 12),
                new("Stitched Gloves", 16),
                new("Jewelled Gloves", 26),
                new("Intricate Gloves", 33),
                new("Embroidered Gloves", 52),
                new("Adorned Gloves", 65)
            }},
            { "Helm Armour", new() {
                new("Rusted Greathelm", 1),
                new("Soldier Greathelm", 12),
                new("Wrapped Greathelm", 16),
                new("Spired Greathelm", 27),
                new("Elite Greathelm", 33),
                new("Commander Greathelm", 45),
                new("Sentinel Greathelm", 52),
                new("Guardian Greathelm", 65)
            }},
            { "Helm Evasion", new() {
                new("Shabby Hood", 1),
                new("Felt Cap", 10),
                new("Lace Hood", 16),
                new("Swathed Cap", 26),
                new("Hunter Hood", 33),
                new("Viper Cap", 38),
                new("Corsair Cap", 45),
                new("Covert Hood", 56),
                new("Armoured Cap", 65)
            }},
            { "Helm ES", new() {
                new("Twig Circlet", 1),
                new("Wicker Tiara", 10),
                new("Beaded Circlet", 16),
                new("Chain Tiara", 26),
                new("Feathered Tiara", 33),
                new("Gold Circlet", 40),
                new("Noble Circlet", 52),
                new("Magus Tiara", 65)
            }},
            { "Belt", new() {
                new("Rawhide Belt", 1),
                new("Linen Belt", 1),
                new("Wide Belt", 14),
                new("Long Belt", 20),
                new("Plate Belt", 25),
                new("Ornate Belt", 31),
                new("Mail Belt", 40),
                new("Double Belt", 44),
                new("Heavy Belt", 50),
                new("Utility Belt", 55),
                new("Fine Belt", 62)
            }},
            { "Ring", new() {
                new("Iron Ring", 1),
                new("Ruby Ring", 8),
                new("Golden Hoop", 12),
                new("Sapphire Ring", 12),
                new("Topaz Ring", 16),
                new("Amethyst Ring", 20),
                new("Emerald Ring", 26),
                new("Pearl Ring", 32),
                new("Prismatic Ring", 35),
                new("Gold Ring", 40),
                new("Unset Ring", 44)
            }},
            { "Charm", new() {
                new("Ruby Charm", 5),
                new("Sapphire Charm", 5),
                new("Topaz Charm", 5),
                new("Stone Charm", 8),
                new("Silver Charm", 10),
                new("Thawing Charm", 12),
                new("Staunching Charm", 18),
                new("Antidote Charm", 24),
                new("Dousing Charm", 32),
                new("Grounding Charm", 32),
                new("Amethyst Charm", 40),
                new("Cleansing Charm", 40),
                new("Golden Charm", 50)
            }}
        };

        // Note: PoE2's currently obtainable weapon classes do not include a "Sword" class
        // (confirmed against the live, FilterBlade-authored base filter's Class list, which
        // enumerates every weapon class and never references Swords). No "Sword" archetype
        // is defined - guessing base names here risks reintroducing the same kind of bug.
        private static readonly (string[] Keywords, string Archetype)[] _keywordArchetypeMappings =
        [
            (["maul", "mace", "club", "hammer"], "Mace"),
            (["quarterstaff", "staff"], "Staff"),
            (["crossbow"], "Crossbow"),
            (["bow"], "Bow"),
            (["wand"], "Wand"),
            (["vest", "coat"], "Body Armour Evasion"),
            (["plate", "cuirass"], "Body Armour Armour"),
            (["robe", "raiment", "regalia"], "Body Armour ES"),
            (["belt", "sash"], "Belt"),
            (["ring"], "Ring"),
            (["charm"], "Charm")
        ];

        private static BaseItemInfo? FindMatchedBase(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return null;

            // Prefer the longest (most specific) matching base name so a generic entry
            // (e.g. "Robe") can't preempt a more specific one (e.g. "Velvet Robe") based
            // on dictionary/list enumeration order alone.
            return _archetypes.Values
                .SelectMany(list => list)
                .Where(baseItem => itemName.Contains(baseItem.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(baseItem => baseItem.Name.Length)
                .FirstOrDefault();
        }

        // Sub-archetype routing below reflects PoE2's actual naming convention (verified
        // against poe2db.tw): armour-tier gloves are "Mitts" (not "Gauntlets"), ES boots are
        // "Sandals" (not just "Slippers"), and "Hood"/"Cap" are Evasion helms while
        // "Circlet"/"Tiara" are ES helms - the inverse of what this previously assumed.
        private static List<BaseItemInfo> FindBootsArchetype(string cleanName)
        {
            if (cleanName.Contains("greaves", StringComparison.Ordinal)) return _archetypes["Boots Armour"];
            if (cleanName.Contains("sandals", StringComparison.Ordinal) || cleanName.Contains("slippers", StringComparison.Ordinal)) return _archetypes["Boots ES"];
            return _archetypes["Boots Evasion"];
        }

        private static List<BaseItemInfo> FindGlovesArchetype(string cleanName)
        {
            if (cleanName.Contains("mitts", StringComparison.Ordinal) || cleanName.Contains("gauntlets", StringComparison.Ordinal)) return _archetypes["Gloves Armour"];
            if (cleanName.Contains("bracers", StringComparison.Ordinal) || cleanName.Contains("cuffs", StringComparison.Ordinal)) return _archetypes["Gloves Evasion"];
            return _archetypes["Gloves ES"];
        }

        private static List<BaseItemInfo> FindHelmArchetype(string cleanName)
        {
            if (cleanName.Contains("greathelm", StringComparison.Ordinal) || cleanName.Contains("helm", StringComparison.Ordinal)) return _archetypes["Helm Armour"];
            if (cleanName.Contains("circlet", StringComparison.Ordinal) || cleanName.Contains("tiara", StringComparison.Ordinal)) return _archetypes["Helm ES"];
            return _archetypes["Helm Evasion"];
        }

        private static bool IsBoots(string name) =>
            name.Contains("boots", StringComparison.Ordinal) ||
            name.Contains("greaves", StringComparison.Ordinal) ||
            name.Contains("sandals", StringComparison.Ordinal) ||
            name.Contains("slippers", StringComparison.Ordinal);

        private static bool IsGloves(string name) =>
            name.Contains("gauntlets", StringComparison.Ordinal) ||
            name.Contains("gloves", StringComparison.Ordinal) ||
            name.Contains("mitts", StringComparison.Ordinal) ||
            name.Contains("bracers", StringComparison.Ordinal) ||
            name.Contains("cuffs", StringComparison.Ordinal);

        private static bool IsHelm(string name) =>
            name.Contains("greathelm", StringComparison.Ordinal) ||
            name.Contains("helm", StringComparison.Ordinal) ||
            name.Contains("cap", StringComparison.Ordinal) ||
            name.Contains("hood", StringComparison.Ordinal) ||
            name.Contains("circlet", StringComparison.Ordinal) ||
            name.Contains("tiara", StringComparison.Ordinal);

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

        // Build-planner exports prefix rune-socketed items with the word "Runeforged"
        // (e.g. "Runeforged Commander Gauntlets"), which is not part of the real BaseType.
        // Only that literal, known prefix is stripped - earlier code stripped *any* first
        // word, which silently mangled names like "Ultimate Life Flask" into the
        // non-existent "Life Flask" (the tier word there is part of the real BaseType).
        private const string _runeforgedPrefix = "Runeforged ";

        private static string CleanItemBaseName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            return name.StartsWith(_runeforgedPrefix, StringComparison.OrdinalIgnoreCase)
                ? name[_runeforgedPrefix.Length..]
                : name;
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
