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
    public class FilterBuilder
    {
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
            double tier1Threshold, // e.g. 100 Chaos
            double tier2Threshold, // e.g. 10 Chaos
            FilterTheme? activeTheme = null
        )
        {
            try
            {
                activeTheme ??= new FilterTheme();
                var sb = new StringBuilder();

                // Add header info
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

                // 1. Build Item Highlight Section (highest priority - Top of the file)
                if (activeBuild != null)
                {
                    sb.AppendLine("# --------------------------------------------------");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"# BUILD HIGHLIGHTS: {activeBuild.Name} (Level {playerLevel}, Zone {zoneLevel})");
                    sb.AppendLine("# --------------------------------------------------");

                    // Highlight specific unique items active at this level
                    var buildUniqueItems = activeBuild.InventorySlots
                        .Where(slot => {
                            if (string.IsNullOrWhiteSpace(slot.ItemName)) return false;
                            if (!string.IsNullOrEmpty(slot.UniqueName)) return true;
                            return false;
                        })
                        .Where(slot => {
                            if (slot.LevelInterval == null || slot.LevelInterval.Count < 2) return true;
                            return playerLevel >= slot.LevelInterval[0] && playerLevel <= slot.LevelInterval[1];
                        })
                        .Select(slot => slot.ItemName)
                        .Distinct()
                        .ToList();

                    if (buildUniqueItems.Count > 0)
                    {
                        sb.AppendLine("# Active Build Unique Items");
                        sb.AppendLine("Show");
                        sb.AppendLine("    Rarity Unique");
                        sb.Append("    BaseType");
                        foreach (var item in buildUniqueItems)
                        {
                            sb.Append(CultureInfo.InvariantCulture, $" \"{item}\"");
                        }
                        sb.AppendLine();
                        AppendStyleBlock(sb, activeTheme.Uniques);
                        sb.AppendLine();
                    }

                    // Highlight progression base items (non-uniques) recommended by the build
                    var buildBases = activeBuild.InventorySlots
                        .Where(slot => string.IsNullOrEmpty(slot.UniqueName) && !string.IsNullOrEmpty(slot.ItemName))
                        .Where(slot => {
                            if (slot.LevelInterval == null || slot.LevelInterval.Count < 2) return true;
                            return playerLevel >= slot.LevelInterval[0] && playerLevel <= slot.LevelInterval[1];
                        })
                        .ToList();

                    var progressionBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var slot in buildBases)
                    {
                        var matchedBase = FindMatchedBase(slot.ItemName);
                        if (matchedBase != null)
                        {
                            progressionBases.Add(matchedBase.Name);
                            var list = FindArchetypeList(matchedBase.Name);
                            if (list != null)
                            {
                                // 1. Highest required level base item <= playerLevel
                                var playerBase = list.Where(b => b.RequiredLevel <= playerLevel)
                                                     .OrderByDescending(b => b.RequiredLevel)
                                                     .FirstOrDefault();
                                if (playerBase != null)
                                {
                                    progressionBases.Add(playerBase.Name);
                                }

                                // 2. Highest required level base item <= zoneLevel
                                var zoneBase = list.Where(b => b.RequiredLevel <= zoneLevel)
                                                   .OrderByDescending(b => b.RequiredLevel)
                                                   .FirstOrDefault();
                                if (zoneBase != null)
                                {
                                    progressionBases.Add(zoneBase.Name);
                                }
                            }
                            else
                            {
                                progressionBases.Add(matchedBase.Name);
                            }
                        }
                        else
                        {
                            var cleaned = CleanItemBaseName(slot.ItemName);
                            if (!string.IsNullOrEmpty(cleaned))
                            {
                                progressionBases.Add(cleaned);
                            }
                        }
                    }

                    if (progressionBases.Count > 0)
                    {
                        sb.AppendLine("# Active Build Progression Base Items (Leveling & Zone)");
                        sb.AppendLine("Show");
                        sb.AppendLine("    Rarity <= Rare");
                        sb.Append("    BaseType");
                        foreach (var baseName in progressionBases)
                        {
                            sb.Append(CultureInfo.InvariantCulture, $" \"{baseName}\"");
                        }
                        sb.AppendLine();
                        AppendStyleBlock(sb, activeTheme.ProgressionBases);
                        sb.AppendLine();
                    }

                    // Highlight active skill gems at this level
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
                        sb.Append("    BaseType");
                        foreach (var gem in buildGems)
                        {
                            sb.Append(CultureInfo.InvariantCulture, $" \"{gem}\"");
                        }
                        sb.AppendLine();
                        AppendStyleBlock(sb, activeTheme.Gems);
                        sb.AppendLine();
                    }
                }

                // 2. Dynamic Economy Highlight Section
                sb.AppendLine("# --------------------------------------------------");
                sb.AppendLine("# DYNAMIC ECONOMY HIGHLIGHTS (poe.ninja)");
                sb.AppendLine("# --------------------------------------------------");

                // Tier 1 Items (Very Valuable: e.g. Divine Orb, Mirror, high-tier uniques)
                var tier1Items = marketItems.Where(i => i.ChaosValue >= tier1Threshold).ToList();
                if (tier1Items.Count > 0)
                {
                    sb.AppendLine("# Tier 1 Economy Items");
                    sb.AppendLine("Show");
                    sb.Append("    BaseType");
                    foreach (var item in tier1Items)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $" \"{item.Name}\"");
                    }
                    sb.AppendLine();
                    AppendStyleBlock(sb, activeTheme.EconomyTier1);
                    sb.AppendLine();
                }

                // Tier 2 Items (Valuable: e.g. Exalted Orb, medium items)
                var tier2Items = marketItems.Where(i => i.ChaosValue >= tier2Threshold && i.ChaosValue < tier1Threshold).ToList();
                if (tier2Items.Count > 0)
                {
                    sb.AppendLine("# Tier 2 Economy Items");
                    sb.AppendLine("Show");
                    sb.Append("    BaseType");
                    foreach (var item in tier2Items)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $" \"{item.Name}\"");
                    }
                    sb.AppendLine();
                    AppendStyleBlock(sb, activeTheme.EconomyTier2);
                    sb.AppendLine();
                }

                // 3. Append the user's existing filter rules if they provided a base file
                if (!string.IsNullOrEmpty(baseFilterPath) && File.Exists(baseFilterPath))
                {
                    sb.AppendLine("# --------------------------------------------------");
                    sb.AppendLine("# APPENDED BASE FILTER RULES");
                    sb.AppendLine("# --------------------------------------------------");
                    var baseFilterContent = File.ReadAllText(baseFilterPath);
                    sb.AppendLine(baseFilterContent);
                }
                else
                {
                    // Fallback to a basic template if no base filter is provided
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

                    bool insertSpace = false;

                    // Transition from lower case to upper case: e.g. 'r' to 'D'
                    if (char.IsLower(prev) && char.IsUpper(curr))
                    {
                        insertSpace = true;
                    }
                    // Transition from upper case to upper case followed by lower: e.g. 'H' to 'M' in "1HMace" where next is 'a'
                    else if (char.IsUpper(prev) && char.IsUpper(curr) && next.HasValue && char.IsLower(next.Value))
                    {
                        insertSpace = true;
                    }
                    // Transition from letter to digit: e.g. 't' to '1'
                    else if (char.IsLetter(prev) && char.IsDigit(curr))
                    {
                        insertSpace = true;
                    }
                    // Transition from digit to letter: e.g. '1' to 'H'
                    else if (char.IsDigit(prev) && char.IsLetter(curr))
                    {
                        insertSpace = true;
                    }

                    if (insertSpace)
                    {
                        sb.Append(' ');
                    }
                }
                sb.Append(clean[i]);
            }
            return sb.ToString().Trim();
        }

        private sealed class BaseItemInfo
        {
            public string Name { get; }
            public int RequiredLevel { get; }

            public BaseItemInfo(string name, int level)
            {
                Name = name;
                RequiredLevel = level;
            }
        }

        private static readonly Dictionary<string, List<BaseItemInfo>> Archetypes = new()
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

        private static BaseItemInfo? FindMatchedBase(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return null;

            foreach (var kvp in Archetypes)
            {
                foreach (var baseItem in kvp.Value)
                {
                    if (itemName.Contains(baseItem.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return baseItem;
                    }
                }
            }
            return null;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Comparing with lowercase string literals is more readable and matches the established casing pattern for game items.")]
        private static List<BaseItemInfo>? FindArchetypeList(string baseItemName)
        {
            if (string.IsNullOrWhiteSpace(baseItemName)) return null;

            foreach (var kvp in Archetypes)
            {
                if (kvp.Value.Any(b => b.Name.Equals(baseItemName, StringComparison.OrdinalIgnoreCase)))
                {
                    return kvp.Value;
                }
            }

            var cleanName = baseItemName.ToLowerInvariant();

            if (cleanName.Contains("maul", StringComparison.Ordinal) || cleanName.Contains("mace", StringComparison.Ordinal))
                return Archetypes["Mace"];
            if (cleanName.Contains("quarterstaff", StringComparison.Ordinal) || cleanName.Contains("staff", StringComparison.Ordinal))
                return Archetypes["Staff"];
            if (cleanName.Contains("crossbow", StringComparison.Ordinal))
                return Archetypes["Crossbow"];
            if (cleanName.Contains("bow", StringComparison.Ordinal))
                return Archetypes["Bow"];
            if (cleanName.Contains("sword", StringComparison.Ordinal) || cleanName.Contains("sabre", StringComparison.Ordinal) || cleanName.Contains("falchion", StringComparison.Ordinal) || cleanName.Contains("cutlass", StringComparison.Ordinal) || cleanName.Contains("greatsword", StringComparison.Ordinal))
                return Archetypes["Sword"];
            if (cleanName.Contains("wand", StringComparison.Ordinal))
                return Archetypes["Wand"];
            if (cleanName.Contains("jacket", StringComparison.Ordinal) || cleanName.Contains("jerkin", StringComparison.Ordinal))
                return Archetypes["Body Armour Evasion"];
            if (cleanName.Contains("plate", StringComparison.Ordinal) || cleanName.Contains("chainmail", StringComparison.Ordinal) || cleanName.Contains("ringmail", StringComparison.Ordinal) || cleanName.Contains("scale vest", StringComparison.Ordinal))
                return Archetypes["Body Armour Armour"];
            if (cleanName.Contains("robe", StringComparison.Ordinal) || cleanName.Contains("vestment", StringComparison.Ordinal) || cleanName.Contains("regalia", StringComparison.Ordinal) || cleanName.Contains("wrap", StringComparison.Ordinal))
                return Archetypes["Body Armour ES"];
            if (cleanName.Contains("boots", StringComparison.Ordinal) || cleanName.Contains("greaves", StringComparison.Ordinal) || cleanName.Contains("slippers", StringComparison.Ordinal) || cleanName.Contains("shoes", StringComparison.Ordinal))
            {
                if (cleanName.Contains("greaves", StringComparison.Ordinal)) return Archetypes["Boots Armour"];
                if (cleanName.Contains("slippers", StringComparison.Ordinal) || cleanName.Contains("shoes", StringComparison.Ordinal)) return Archetypes["Boots ES"];
                return Archetypes["Boots Evasion"];
            }
            if (cleanName.Contains("gauntlets", StringComparison.Ordinal) || cleanName.Contains("gloves", StringComparison.Ordinal) || cleanName.Contains("mitts", StringComparison.Ordinal))
            {
                if (cleanName.Contains("gauntlets", StringComparison.Ordinal)) return Archetypes["Gloves Armour"];
                if (cleanName.Contains("gloves", StringComparison.Ordinal) && (cleanName.Contains("wool", StringComparison.Ordinal) || cleanName.Contains("silk", StringComparison.Ordinal) || cleanName.Contains("sage", StringComparison.Ordinal) || cleanName.Contains("cabalist", StringComparison.Ordinal))) return Archetypes["Gloves ES"];
                return Archetypes["Gloves Evasion"];
            }
            if (cleanName.Contains("helm", StringComparison.Ordinal) || cleanName.Contains("helmet", StringComparison.Ordinal) || cleanName.Contains("cap", StringComparison.Ordinal) || cleanName.Contains("mask", StringComparison.Ordinal) || cleanName.Contains("hood", StringComparison.Ordinal) || cleanName.Contains("coif", StringComparison.Ordinal) || cleanName.Contains("circlet", StringComparison.Ordinal))
            {
                if (cleanName.Contains("hat", StringComparison.Ordinal) || cleanName.Contains("helm", StringComparison.Ordinal) || cleanName.Contains("coif", StringComparison.Ordinal)) return Archetypes["Helm Armour"];
                if (cleanName.Contains("hood", StringComparison.Ordinal) || cleanName.Contains("circlet", StringComparison.Ordinal)) return Archetypes["Helm ES"];
                return Archetypes["Helm Evasion"];
            }
            if (cleanName.Contains("belt", StringComparison.Ordinal) || cleanName.Contains("sash", StringComparison.Ordinal))
                return Archetypes["Belt"];
            if (cleanName.Contains("ring", StringComparison.Ordinal))
                return Archetypes["Ring"];
            if (cleanName.Contains("charm", StringComparison.Ordinal))
                return Archetypes["Charm"];

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

            byte r = 255, g = 255, b = 255, a = 255;

            try
            {
                if (hex.Length == 6)
                {
                    // RRGGBB
                    r = Convert.ToByte(hex.Substring(0, 2), 16);
                    g = Convert.ToByte(hex.Substring(2, 2), 16);
                    b = Convert.ToByte(hex.Substring(4, 2), 16);
                    a = 255;
                }
                else if (hex.Length == 8)
                {
                    // WPF standard uses AARRGGBB.
                    a = Convert.ToByte(hex.Substring(0, 2), 16);
                    r = Convert.ToByte(hex.Substring(2, 2), 16);
                    g = Convert.ToByte(hex.Substring(4, 2), 16);
                    b = Convert.ToByte(hex.Substring(6, 2), 16);
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
    }
}
