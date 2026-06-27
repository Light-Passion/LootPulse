using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LootPulse.Models;
using LootPulse.Services;
using Xunit;

namespace LootPulse.Tests
{
    public class FilterBuilderTests
    {
        [Theory]
        [InlineData(null, "255 255 255 255")]
        [InlineData("", "255 255 255 255")]
        [InlineData("   ", "255 255 255 255")]
        public void ParseHexToRgbaString_NullOrEmpty_ReturnsDefault(string input, string expected)
        {
            var result = FilterBuilder.ParseHexToRgbaString(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("#FFF", "255 255 255 255")]
        [InlineData("000", "0 0 0 255")]
        [InlineData("#123", "17 34 51 255")]
        public void ParseHexToRgbaString_3DigitHex_ReturnsCorrectRgba(string input, string expected)
        {
            var result = FilterBuilder.ParseHexToRgbaString(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("#F00F", "0 0 255 255")] // ARGB: A=F, R=0, G=0, B=F -> 255 0 0 255? No, AARRGGBB.
                                              // If #F00F is ARGB shorthand: A=F, R=0, G=0, B=F.
                                              // AARRGGBB: FF 00 00 FF.
                                              // RGB A: 0 0 255 255.
        [InlineData("FABC", "170 187 204 255")] // ARGB: A=F, R=A, G=B, B=C -> FF AA BB CC
                                                // R:170 G:187 B:204 A:255
        public void ParseHexToRgbaString_4DigitHex_ReturnsCorrectRgba(string input, string expected)
        {
            var result = FilterBuilder.ParseHexToRgbaString(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("#FF0000", "255 0 0 255")]
        [InlineData("00FF00", "0 255 0 255")]
        [InlineData("#0000FF", "0 0 255 255")]
        [InlineData("123456", "18 52 86 255")]
        public void ParseHexToRgbaString_6DigitHex_ReturnsCorrectRgba(string input, string expected)
        {
            var result = FilterBuilder.ParseHexToRgbaString(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("#FFFF0000", "255 0 0 255")] // AARRGGBB -> A:FF R:FF G:00 B:00
        [InlineData("8000FF00", "0 255 0 128")] // AARRGGBB -> A:80 R:00 G:FF B:00
        [InlineData("#000000FF", "0 0 255 0")]   // AARRGGBB -> A:00 R:00 G:00 B:FF
        public void ParseHexToRgbaString_8DigitHex_ReturnsCorrectRgba(string input, string expected)
        {
            var result = FilterBuilder.ParseHexToRgbaString(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("invalid")]
        [InlineData("#12")]
        [InlineData("12345")]
        [InlineData("1234567")]
        [InlineData("GHIJKL")]
        public void ParseHexToRgbaString_InvalidInput_ReturnsEmpty(string input)
        {
            var result = FilterBuilder.ParseHexToRgbaString(input);
            Assert.Equal("", result);
        }

        [Fact]
        public void ParseHexToRgbaString_MixedCasing_Works()
        {
            var result = FilterBuilder.ParseHexToRgbaString("#ffAa00");
            Assert.Equal("255 170 0 255", result);
        }

        [Fact]
        public async Task GenerateFilterFileAsync_ValidInput_GeneratesFile()
        {
            // Arrange
            var builder = new FilterBuilder();
            var outputPath = GetTempFilePath();
            var build = CreateTestBuild();
            var items = CreateTestMarketItems();

            try
            {
                // Act
                bool result = await builder.GenerateFilterFileAsync(
                    outputPath,
                    null,
                    items,
                    build,
                    20, 20, 1.0, 1.0);

                // Assert
                Assert.True(result);
                Assert.True(File.Exists(outputPath));

                var content = await File.ReadAllTextAsync(outputPath);
                Assert.Contains("# Active Build Unique Items", content);
                Assert.Contains("\"Stellar Amulet\"", content);
                Assert.Contains("\"Uncut Skill Gem\"", content);
            }
            finally
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
            }
        }

        [Fact]
        public async Task GenerateFilterFileAsync_NullBuild_GeneratesFallback()
        {
            // Arrange
            var builder = new FilterBuilder();
            var outputPath = GetTempFilePath();

            try
            {
                // Act
                bool result = await builder.GenerateFilterFileAsync(
                    outputPath,
                    null,
                    new List<MarketItem>(),
                    null,
                    1, 1, 1.0, 1.0);

                // Assert
                Assert.True(result);
                Assert.True(File.Exists(outputPath));

                var content = await File.ReadAllTextAsync(outputPath);
                Assert.Contains("# Fallback basic catch-all rules", content);
                Assert.Contains("Class \"Currency\"", content);
            }
            finally
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
            }
        }

        [Fact]
        public async Task GenerateFilterFileAsync_WithBaseFilter_Deduplicates()
        {
            // Arrange
            var builder = new FilterBuilder();
            var outputPath = GetTempFilePath();
            var baseFilterPath = GetTempFilePath();
            var build = CreateTestBuild(); // Contains Astramentis -> Stellar Amulet

            string baseFilterContent = "Show\n    BaseType \"Stellar Amulet\"\n    SetFontSize 42";
            await File.WriteAllTextAsync(baseFilterPath, baseFilterContent);

            try
            {
                // Act
                bool result = await builder.GenerateFilterFileAsync(
                    outputPath,
                    baseFilterPath,
                    CreateTestMarketItems(),
                    build,
                    20, 20, 1.0, 1.0);

                // Assert
                Assert.True(result);
                var content = await File.ReadAllTextAsync(outputPath);

                Assert.Contains("# APPENDED BASE FILTER RULES", content);
                // The deduplication should strip the "Stellar Amulet" block from the base filter portion
                // because it's already highlighted in the build section.
                // We check that the specific block from base filter (with SetFontSize 42) is NOT there.
                // Note: StripDuplicateBaseTypeRules drops the whole block if no conditions remain.
                Assert.DoesNotContain("SetFontSize 42", content);
            }
            finally
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
                if (File.Exists(baseFilterPath)) File.Delete(baseFilterPath);
            }
        }

        [Fact]
        public async Task GenerateFilterFileAsync_InvalidPath_ReturnsFalse()
        {
            // Arrange
            var builder = new FilterBuilder();
            // Using a path that contains invalid characters to force failure on all platforms
            var invalidPath = "C:\\invalid\\path\\?:|*\\filter.filter";

            // Act
            bool result = await builder.GenerateFilterFileAsync(
                invalidPath,
                null,
                new List<MarketItem>(),
                null,
                1, 1, 1.0, 1.0);

            // Assert
            Assert.False(result);
        }

        private static PoeBuild CreateTestBuild()
        {
            return new PoeBuild
            {
                Name = "Test Spark Build",
                InventorySlots = new List<BuildInventorySlot>
                {
                    new BuildInventorySlot
                    {
                        InventoryId = "Amulet",
                        UniqueName = "Astramentis"
                    }
                },
                Skills = new List<BuildSkill>
                {
                    new BuildSkill
                    {
                        Name = "Spark"
                    }
                }
            };
        }

        private static List<MarketItem> CreateTestMarketItems()
        {
            return new List<MarketItem>
            {
                new MarketItem { Name = "Divine Orb", Category = "Currency", ChaosValue = 200 },
                new MarketItem { Name = "Exalted Orb", Category = "Currency", ChaosValue = 20 }
            };
        }

        private static string GetTempFilePath()
        {
            return Path.Combine(Path.GetTempPath(), "LootPulse_Test_" + Guid.NewGuid().ToString("N") + ".filter");
        }
    }
}
