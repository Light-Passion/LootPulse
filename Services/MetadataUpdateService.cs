using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using LootPulse.Models;
using LootPulse.Services.Trade;

namespace LootPulse.Services
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Prevent external scrape/network errors from crashing the WPF overlay.")]
    [SuppressMessage("Globalization", "CA1305:Specify IFormatProvider", Justification = "String interpolation and locale-aware formatting is controlled at output.")]
    [SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "Checked internally by UI controller.")]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept as instance methods to support future dependency injection, mockability, and extension.")]
    public class MetadataUpdateService
    {
        private static readonly SemaphoreSlim _fileLock = new(1, 1);

        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LootPulse"
        );

        private static readonly string BaseItemsPath = Path.Combine(AppDataFolder, "base_items.json");
        private static readonly string EconomyCategoriesPath = Path.Combine(AppDataFolder, "economy_categories.json");

        public BaseItemsConfig LoadBaseItemsConfig()
        {
            _fileLock.Wait();
            try
            {
                if (File.Exists(BaseItemsPath))
                {
                    string json = File.ReadAllText(BaseItemsPath);
                    var config = JsonSerializer.Deserialize(json, PoeNinjaJsonContext.Default.BaseItemsConfig);
                    if (config != null)
                    {
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load base items configuration: {ex.Message}");
            }
            finally
            {
                _fileLock.Release();
            }

            // Fallback & write defaults. Needs to happen outside the lock to avoid double lock since SaveBaseItemsConfig gets its own lock.
            var defaults = GetDefaultBaseItems();
            _fileLock.Wait();
            try
            {
                SaveBaseItemsConfigInternal(defaults);
            }
            finally
            {
                _fileLock.Release();
            }
            return defaults;
        }

        public List<EconomyCategoryRecord> LoadEconomyCategories()
        {
            _fileLock.Wait();
            try
            {
                if (File.Exists(EconomyCategoriesPath))
                {
                    string json = File.ReadAllText(EconomyCategoriesPath);
                    var categories = JsonSerializer.Deserialize(json, PoeNinjaJsonContext.Default.ListEconomyCategoryRecord);
                    if (categories != null)
                    {
                        return categories;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load economy categories: {ex.Message}");
            }
            finally
            {
                _fileLock.Release();
            }

            // Fallback & write defaults. SaveEconomyCategories handles its own lock.
            var defaults = GetDefaultEconomyCategories();
            _fileLock.Wait();
            try
            {
                SaveEconomyCategoriesInternal(defaults);
            }
            finally
            {
                _fileLock.Release();
            }
            return defaults;
        }

        public void SaveBaseItemsConfig(BaseItemsConfig config)
        {
            _fileLock.Wait();
            try
            {
                SaveBaseItemsConfigInternal(config);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private void SaveBaseItemsConfigInternal(BaseItemsConfig config)
        {
            try
            {
                Directory.CreateDirectory(AppDataFolder);
                string json = JsonSerializer.Serialize(config, PoeNinjaJsonContext.Default.BaseItemsConfig);
                
                // Safe Atomic Write-and-Replace
                string tempPath = BaseItemsPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, BaseItemsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save base items configuration: {ex.Message}");
            }
        }

        public void SaveEconomyCategories(List<EconomyCategoryRecord> categories)
        {
            _fileLock.Wait();
            try
            {
                SaveEconomyCategoriesInternal(categories);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private void SaveEconomyCategoriesInternal(List<EconomyCategoryRecord> categories)
        {
            try
            {
                Directory.CreateDirectory(AppDataFolder);
                string json = JsonSerializer.Serialize(categories, PoeNinjaJsonContext.Default.ListEconomyCategoryRecord);
                
                // Safe Atomic Write-and-Replace
                string tempPath = EconomyCategoriesPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, EconomyCategoriesPath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save economy categories: {ex.Message}");
            }
        }

        public async Task<(int NewCurrencies, int NewBases, string Log)> UpdateMetadataAsync(ITradeTransport tradeTransport, CancellationToken ct = default)
        {
            var log = new System.Text.StringBuilder();
            int newCurrenciesCount = 0;
            int newBasesCount = 0;

            log.AppendLine("Starting dynamic metadata harvesting...");

            await _fileLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 1. Fetch GGG Static Data
                log.AppendLine("Fetching GGG trade static metadata...");
                var response = await tradeTransport.SendAsync(HttpMethod.Get, "https://www.pathofexile.com/api/trade2/data/static", null, ct).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    log.AppendLine($"Failed to fetch static metadata from GGG Trade: {response.StatusCode}");
                    return (0, 0, log.ToString());
                }

                var gggData = JsonSerializer.Deserialize(response.Body, PoeNinjaJsonContext.Default.GggStaticDataResponse);
                if (gggData?.Result == null)
                {
                    log.AppendLine("GGG trade static metadata response was empty or invalid.");
                    return (0, 0, log.ToString());
                }

                var currentBasesConfig = LoadBaseItemsConfig();
                var currentCategories = LoadEconomyCategories();

                // Build lookup sets for existing items to easily find what is new
                var existingCurrencies = new HashSet<string>(currentCategories.Select(c => c.Type), StringComparer.OrdinalIgnoreCase);
                var existingBaseNames = new HashSet<string>(currentBasesConfig.Archetypes.Values.SelectMany(l => l).Select(b => b.Name), StringComparer.OrdinalIgnoreCase);

                // We will collect newly discovered base items grouped by GGG category label (e.g. "Staves", "Maces")
                // so we can scrape their required levels and classifications category-by-category.
                var newBasesByCategory = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (var category in gggData.Result)
                {
                    string label = category.Label ?? string.Empty;
                    string id = category.Id ?? string.Empty;

                    // If it is a currency or commodity category, check if we need to track it
                    if (id.Equals("currency", StringComparison.OrdinalIgnoreCase) ||
                        id.Equals("cards", StringComparison.OrdinalIgnoreCase) ||
                        id.Equals("monsters", StringComparison.OrdinalIgnoreCase) ||
                        id.Equals("delirium", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var entry in category.Entries)
                        {
                            string entryName = entry.Text ?? entry.Type ?? string.Empty;
                            if (string.IsNullOrEmpty(entryName)) continue;

                            // We map this to our categories list. The 'type' in poe.ninja is often different,
                            // but if there are new ones, we log them. GGG static data is mostly useful for item bases,
                            // since poe.ninja categories are mostly hardcoded in PoeNinjaClient. Resolving a new category
                            // token requires community updates (Option A).
                        }
                    }
                    else
                    {
                        // Equipment category
                        foreach (var entry in category.Entries)
                        {
                            string baseName = entry.Text ?? entry.Type ?? string.Empty;
                            if (string.IsNullOrEmpty(baseName)) continue;

                            // If we don't have it, queue it for scraping/level lookup
                            if (!existingBaseNames.Contains(baseName))
                            {
                                if (!newBasesByCategory.TryGetValue(label, out var list))
                                {
                                    list = new List<string>();
                                    newBasesByCategory[label] = list;
                                }
                                list.Add(baseName);
                            }
                        }
                    }
                }

                // 2. Scrape poe2db.tw for level requirements of new items
                if (newBasesByCategory.Count > 0)
                {
                    log.AppendLine($"Discovered {newBasesByCategory.Values.Sum(l => l.Count)} new item bases in GGG data. Querying poe2db.tw for level requirements...");

                    // Map GGG category labels to poe2db page slugs & archetypes
                    var categoryMappings = new[]
                    {
                        new { GggLabel = "One Hand Maces", Poe2DbSlug = "Mace", DefaultArchetype = "Mace" },
                        new { GggLabel = "Two Hand Maces", Poe2DbSlug = "Mace", DefaultArchetype = "Mace" },
                        new { GggLabel = "Quarterstaves", Poe2DbSlug = "Staff", DefaultArchetype = "Staff" },
                        new { GggLabel = "Bows", Poe2DbSlug = "Bow", DefaultArchetype = "Bow" },
                        new { GggLabel = "Crossbows", Poe2DbSlug = "Crossbow", DefaultArchetype = "Crossbow" },
                        new { GggLabel = "Wands", Poe2DbSlug = "Wand", DefaultArchetype = "Wand" },
                        new { GggLabel = "Body Armours", Poe2DbSlug = "Body_Armour", DefaultArchetype = "Body Armour" },
                        new { GggLabel = "Boots", Poe2DbSlug = "Boots", DefaultArchetype = "Boots" },
                        new { GggLabel = "Gloves", Poe2DbSlug = "Gloves", DefaultArchetype = "Gloves" },
                        new { GggLabel = "Helmets", Poe2DbSlug = "Helmets", DefaultArchetype = "Helm" },
                        new { GggLabel = "Belts", Poe2DbSlug = "Belt", DefaultArchetype = "Belt" },
                        new { GggLabel = "Rings", Poe2DbSlug = "Ring", DefaultArchetype = "Ring" },
                        new { GggLabel = "Charms", Poe2DbSlug = "Charms", DefaultArchetype = "Charm" }
                    };

                    string scrapeScript = @"(function() {
  var results = [];
  var rows = document.querySelectorAll('table tr, .table tr');
  rows.forEach(row => {
    var cells = row.querySelectorAll('td');
    if (cells.length < 2) return;
    var nameLink = cells[0].querySelector('a');
    var name = nameLink ? nameLink.textContent.trim() : cells[0].textContent.trim();
    if (!name || name.length > 50) return;
    var reqLevel = -1;
    for (var i = 1; i < cells.length; i++) {
      var txt = cells[i].textContent.trim();
      var levelMatch = txt.match(/(?:Level|Lvl)?\s*(\d+)/i);
      if (levelMatch) {
        var val = parseInt(levelMatch[1], 10);
        if (val >= 1 && val <= 100) {
          reqLevel = val;
          break;
        }
      }
    }
    if (reqLevel !== -1) {
      results.push({ name: name, level: reqLevel });
    }
  });
  return JSON.stringify(results);
})()";

                    // To avoid spamming, we scrape only pages for which we found new bases
                    var pagesToScrape = categoryMappings
                        .Where(m => newBasesByCategory.Keys.Any(k => k.Contains(m.GggLabel, StringComparison.OrdinalIgnoreCase)))
                        .GroupBy(m => m.Poe2DbSlug)
                        .Select(g => g.First())
                        .ToList();

                    foreach (var page in pagesToScrape)
                    {
                        string url = $"https://poe2db.tw/us/{page.Poe2DbSlug}";
                        log.AppendLine($"Scraping {url}...");

                        try
                        {
                            string jsonResult = await tradeTransport.ScrapePageAsync(url, scrapeScript, ct).ConfigureAwait(false);
                            // ExecuteScriptAsync returns raw JSON string wrapped in quotes if it's a string, or double-encoded.
                            // System.Text.Json can deserialize it. If it is wrapped in quotes as a JSON string, we parse it first.
                            if (jsonResult.StartsWith('\"') && jsonResult.EndsWith('\"'))
                            {
                                jsonResult = JsonSerializer.Deserialize<string>(jsonResult) ?? "[]";
                            }

                            var scrapedItems = JsonSerializer.Deserialize<List<ScrapedItem>>(jsonResult);
                            if (scrapedItems != null && scrapedItems.Count > 0)
                            {
                                log.AppendLine($"Found {scrapedItems.Count} items on poe2db.tw page {page.Poe2DbSlug}.");

                                foreach (var scraped in scrapedItems)
                                {
                                    if (existingBaseNames.Contains(scraped.Name)) continue;

                                    // Classify the item into the correct archetype list
                                    string archetype = page.DefaultArchetype;
                                    if (archetype == "Body Armour")
                                    {
                                        archetype = ClassifyBodyArmour(scraped.Name);
                                    }
                                    else if (archetype == "Boots")
                                    {
                                        archetype = ClassifyBoots(scraped.Name);
                                    }
                                    else if (archetype == "Gloves")
                                    {
                                        archetype = ClassifyGloves(scraped.Name);
                                    }
                                    else if (archetype == "Helm")
                                    {
                                        archetype = ClassifyHelm(scraped.Name);
                                    }

                                    if (currentBasesConfig.Archetypes.TryGetValue(archetype, out var list))
                                    {
                                        // Add to the list
                                        list.Add(new BaseItemInfoRecord(scraped.Name, scraped.Level));
                                        existingBaseNames.Add(scraped.Name);
                                        newBasesCount++;
                                        log.AppendLine($"  Added new base item: {scraped.Name} (Level {scraped.Level}) to archetype '{archetype}'");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            log.AppendLine($"Error scraping page {page.Poe2DbSlug}: {ex.Message}");
                        }

                        // Delay to be respectful of rate limits
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                    }
                }

                // 3. Sort archetypes by level requirement and save
                if (newBasesCount > 0)
                {
                    foreach (var key in currentBasesConfig.Archetypes.Keys.ToList())
                    {
                        currentBasesConfig.Archetypes[key] = currentBasesConfig.Archetypes[key]
                            .OrderBy(b => b.RequiredLevel)
                            .ThenBy(b => b.Name)
                            .ToList();
                    }

                    SaveBaseItemsConfigInternal(currentBasesConfig);
                    log.AppendLine($"Success! Updated base items configuration. Added {newBasesCount} new base items.");
                }
                else
                {
                    log.AppendLine("No new base items were added to the configuration.");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"Fatal error during metadata update: {ex.Message}");
            }
            finally
            {
                _fileLock.Release();
            }

            return (newCurrenciesCount, newBasesCount, log.ToString());
        }

        private static string ClassifyBodyArmour(string name)
        {
            if (name.Contains("vest", StringComparison.OrdinalIgnoreCase) || name.Contains("coat", StringComparison.OrdinalIgnoreCase)) return "Body Armour Evasion";
            if (name.Contains("plate", StringComparison.OrdinalIgnoreCase) || name.Contains("cuirass", StringComparison.OrdinalIgnoreCase)) return "Body Armour Armour";
            return "Body Armour ES";
        }

        private static string ClassifyBoots(string name)
        {
            if (name.Contains("greaves", StringComparison.OrdinalIgnoreCase)) return "Boots Armour";
            if (name.Contains("sandals", StringComparison.OrdinalIgnoreCase) || name.Contains("slippers", StringComparison.OrdinalIgnoreCase)) return "Boots ES";
            return "Boots Evasion";
        }

        private static string ClassifyGloves(string name)
        {
            if (name.Contains("mitts", StringComparison.OrdinalIgnoreCase) || name.Contains("gauntlets", StringComparison.OrdinalIgnoreCase)) return "Gloves Armour";
            if (name.Contains("bracers", StringComparison.OrdinalIgnoreCase) || name.Contains("cuffs", StringComparison.OrdinalIgnoreCase)) return "Gloves Evasion";
            return "Gloves ES";
        }

        private static string ClassifyHelm(string name)
        {
            if (name.Contains("greathelm", StringComparison.OrdinalIgnoreCase) || name.Contains("helm", StringComparison.OrdinalIgnoreCase)) return "Helm Armour";
            if (name.Contains("circlet", StringComparison.OrdinalIgnoreCase) || name.Contains("tiara", StringComparison.OrdinalIgnoreCase)) return "Helm ES";
            return "Helm Evasion";
        }

        private class ScrapedItem
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("level")]
            public int Level { get; set; }
        }

        internal static List<EconomyCategoryRecord> GetDefaultEconomyCategories()
        {
            return new List<EconomyCategoryRecord>
            {
                new("Fragments", "Fragments", true),
                new("Runes", "Runes", true),
                new("SoulCores", "Soul Cores", true),
                new("Essences", "Essences", true),
                new("Ritual", "Omens", true),
                new("Abyss", "Abyssal Bones", true),
                new("UncutGems", "Gems", true),
                new("LineageSupportGems", "Lineage Support Gems", true),
                new("Idols", "Idols", true),
                new("Expedition", "Expedition", true),
                new("Delirium", "Liquid Emotions", true),
                new("Breach", "Breach Catalysts", true),
                new("Verisium", "Verisium", true),
                new("UniqueWeapons", "Unique Weapons", false),
                new("UniqueArmours", "Unique Armour", false),
                new("UniqueAccessories", "Unique Accessories", false),
                new("UniqueFlasks", "Unique Flasks", false),
                new("UniqueCharms", "Unique Charms", false),
                new("UniqueJewels", "Unique Jewels", false),
                new("UniqueSanctumRelics", "Unique Relics", false),
                new("UniqueTablets", "Unique Tablets", false),
                new("PrecursorTablets", "Precursor Tablets", false)
            };
        }

        internal static BaseItemsConfig GetDefaultBaseItems()
        {
            var archetypes = new Dictionary<string, List<BaseItemInfoRecord>>
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

            var keywordArchetypeMappings = new List<KeywordMappingRecord>
            {
                new(new() { "maul", "mace", "club", "hammer" }, "Mace"),
                new(new() { "quarterstaff", "staff" }, "Staff"),
                new(new() { "crossbow" }, "Crossbow"),
                new(new() { "bow" }, "Bow"),
                new(new() { "wand" }, "Wand"),
                new(new() { "vest", "coat" }, "Body Armour Evasion"),
                new(new() { "plate", "cuirass" }, "Body Armour Armour"),
                new(new() { "robe", "raiment", "regalia" }, "Body Armour ES"),
                new(new() { "belt", "sash" }, "Belt"),
                new(new() { "ring" }, "Ring"),
                new(new() { "charm" }, "Charm")
            };

            var knownUniqueBaseTypes = new Dictionary<string, string>
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

            return new BaseItemsConfig(archetypes, keywordArchetypeMappings, knownUniqueBaseTypes);
        }
    }
}
