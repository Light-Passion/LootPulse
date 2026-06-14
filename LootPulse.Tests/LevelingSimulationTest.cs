using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using LootPulse.Models;
using LootPulse.Services;

namespace LootPulse.Tests
{
    public class LevelingSimulationTest
    {
        [Fact]
        public void Run_LevelingSimulation_From_1_To_50()
        {
            // Arrange
            var parser = new BuildProfileParser();
            var builder = new FilterBuilder();

            var buildPath = @"d:\Gemini\PoE2_MarketFilter\testfiles\Early - [0.5] Unlimited Hammers - Gemlin.build";
            var baseFilterPath = @"d:\Gemini\PoE2_MarketFilter\testfiles\Balor League Start Filter.filter";
            var outputPath = @"d:\Gemini\PoE2_MarketFilter\testfiles\DynamicLootFilter.filter";

            Assert.True(File.Exists(buildPath), "Example build file must exist");
            Assert.True(File.Exists(baseFilterPath), "Example base filter file must exist");

            // 1. Load the build planner file
            var build = parser.ParseBuildFile(buildPath);
            Assert.NotNull(build);
            Assert.Equal("Early - [0.5] Unlimited Hammers - Gemlin", build.Name);

            // Debug print parsed skills
            Console.WriteLine($"DEBUG: Total skills parsed: {build.Skills.Count}");
            foreach (var skill in build.Skills)
            {
                Console.WriteLine($"DEBUG: Skill ID='{skill.Id}', Name='{skill.Name}', LevelInterval='{(skill.LevelInterval != null ? string.Join(",", skill.LevelInterval) : "null")}'");
            }

            // Mock some market prices
            var marketItems = new List<MarketItem>
            {
                new MarketItem { Name = "Divine Orb", Category = "Currency", ChaosValue = 125.0 },
                new MarketItem { Name = "Exalted Orb", Category = "Currency", ChaosValue = 15.0 },
                new MarketItem { Name = "Chaos Orb", Category = "Currency", ChaosValue = 1.0 }
            };

            // Define progression steps: (Level, Zone, ExpectedGems, UnexpectedGems, ExpectedUniques, UnexpectedUniques)
            var steps = new[]
            {
                // Level 1: Act 1 start
                new { 
                    Level = 1, 
                    Zone = "Riverwoods", 
                    ExpectedGems = new List<string> { "Player Default 1 H Mace" }, // Uncut Gem level 1
                    UnexpectedGems = new List<string> { "Forge Hammer", "Hammer Of The Gods" }, // Level 32, 53 requirements
                    ExpectedUniques = new List<string> { "Chernobog's Pillar" }, 
                    UnexpectedUniques = new List<string> { "Stone Charm", "Prismatic Ring" } 
                },
                // Level 10: Act 1 mid
                new { 
                    Level = 10, 
                    Zone = "Osgath", 
                    ExpectedGems = new List<string> { "Player Default 1 H Mace", "Infernal Cry" }, // Infernal Cry level 7
                    UnexpectedGems = new List<string> { "Forge Hammer", "Hammer Of The Gods" }, 
                    ExpectedUniques = new List<string> { "Stone Charm", "Chernobog's Pillar" }, // level 8 requirement
                    UnexpectedUniques = new List<string> { "Prismatic Ring", "Belt" } 
                },
                // Level 32: Act 3 start
                new { 
                    Level = 32, 
                    Zone = "The Imperial Fields", 
                    ExpectedGems = new List<string> { "Forge Hammer" }, // level 32 requirement
                    UnexpectedGems = new List<string> { "Hammer Of The Gods" }, // level 53 requirement
                    ExpectedUniques = new List<string> { "Stone Charm", "Chernobog's Pillar" }, 
                    UnexpectedUniques = new List<string> { "Prismatic Ring" } // level 35 requirements
                },
                // Level 50: Act 5 start
                new { 
                    Level = 50, 
                    Zone = "The Vastiri Desert", 
                    ExpectedGems = new List<string> { "Forge Hammer", "Stampede" }, // level 32, 42 requirements
                    UnexpectedGems = new List<string> { "Hammer Of The Gods" }, // level 53 requirement
                    ExpectedUniques = new List<string> { "Stone Charm", "Prismatic Ring", "Chernobog's Pillar" }, // level 8, 35 requirements
                    UnexpectedUniques = new List<string> { "Body Armour" } // level 75 requirements
                }
            };

            try
            {
                // Run the simulation steps
                foreach (var step in steps)
                {
                    System.Diagnostics.Debug.WriteLine($"Simulating: Level {step.Level} in {step.Zone}...");

                    // Act - Generate the filter at this leveling step
                    bool success = builder.GenerateFilterFile(
                        outputPath, 
                        baseFilterPath, 
                        marketItems, 
                        build, 
                        step.Level, 
                        100.0, 
                        10.0
                    );

                    // Assert
                    Assert.True(success, $"Failed to generate filter at Level {step.Level}");
                    Assert.True(File.Exists(outputPath));

                    var content = File.ReadAllText(outputPath);
                    var dynamicContent = content;
                    var appendIndex = content.IndexOf("# APPENDED BASE FILTER RULES");
                    if (appendIndex >= 0)
                    {
                        dynamicContent = content.Substring(0, appendIndex);
                    }

                    // Verify expected gems are highlighted
                    foreach (var gem in step.ExpectedGems)
                    {
                        Assert.True(dynamicContent.Contains(gem), $"Expected gem '{gem}' was not found in dynamic filter content. Dynamic content:\n{dynamicContent}");
                    }

                    // Verify unexpected gems are NOT highlighted
                    foreach (var gem in step.UnexpectedGems)
                    {
                        Assert.DoesNotContain($"\"{gem}\"", dynamicContent);
                    }

                    // Verify expected unique items are highlighted
                    foreach (var item in step.ExpectedUniques)
                    {
                        Assert.Contains(item, dynamicContent);
                    }

                    // Verify unexpected unique items are NOT highlighted
                    foreach (var item in step.UnexpectedUniques)
                    {
                        Assert.DoesNotContain($"\"{item}\"", dynamicContent);
                    }
                }
            }
            finally
            {
                // Clean up output filter
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
        }
    }
}
