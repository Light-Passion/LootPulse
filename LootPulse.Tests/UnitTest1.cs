using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Xunit;
using LootPulse.Models;
using LootPulse.Services;

namespace LootPulse.Tests
{
    public class PoE2ServicesTests
    {
        [Fact]
        public void BuildProfileParser_ParseBuildFile_ValidJson_ReturnsPoeBuild()
        {
            // Arrange
            var parser = new BuildProfileParser();
            var tempFile = Path.GetTempFileName();
            var json = @"
            {
                ""name"": ""Fire Elementalist"",
                ""author"": ""TestUser"",
                ""description"": ""A build focusing on fire damage"",
                ""ascendancy"": ""Elementalist"",
                ""passives"": [{ ""id"": ""Node1"" }, { ""id"": ""Node2"" }],
                ""skills"": [
                    { ""gem_id"": ""fireball"", ""name"": ""Fireball"", ""level"": 20 }
                ],
                ""inventory_slots"": [
                    { ""slot_name"": ""Weapon"", ""unique_name"": ""Spire of Stone"", ""min_level"": 45 }
                ]
            }";
            
            File.WriteAllText(tempFile, json);

            try
            {
                // Act
                var build = parser.ParseBuildFile(tempFile);

                // Assert
                Assert.NotNull(build);
                Assert.Equal("Fire Elementalist", build.Name);
                Assert.Equal("Elementalist", build.Ascendancy);
                Assert.Single(build.Skills);
                Assert.Equal("Fireball", build.Skills[0].Name);
                Assert.Single(build.InventorySlots);
                Assert.Equal("Spire of Stone", build.InventorySlots[0].ItemName);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Fact]
        public void BuildProfileParser_DecodePobShareCode_AndConvertXml_ReturnsValidBuild()
        {
            // Arrange
            var parser = new BuildProfileParser();
            // Let's create a compressed XML:
            var xml = "<PathOfBuilding><Build className=\"Witch\"/><Items><Item>Spire of Stone</Item></Items><Skills><Skill><Gem nameSpec=\"Fireball\" level=\"20\"/></Skill></Skills></PathOfBuilding>";
            var xmlBytes = Encoding.UTF8.GetBytes(xml);

            // Compress to deflate format
            byte[] compressedBytes;
            using (var ms = new MemoryStream())
            {
                // Write standard zlib header (0x78, 0x9C)
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, true))
                {
                    deflate.Write(xmlBytes, 0, xmlBytes.Length);
                }
                compressedBytes = ms.ToArray();
            }

            // Encode as URL-safe Base64
            var base64 = Convert.ToBase64String(compressedBytes).Replace('+', '-').Replace('/', '_');

            // Act
            var decodedXml = parser.DecodePobShareCode(base64);
            Assert.NotNull(decodedXml);
            Assert.Contains("PathOfBuilding", decodedXml);

            var build = parser.ConvertPobXmlToPoeBuild(decodedXml);

            // Assert
            Assert.NotNull(build);
            Assert.Contains("Witch", build.Name);
            Assert.Single(build.InventorySlots);
            Assert.Equal("Spire of Stone", build.InventorySlots[0].ItemName);
            Assert.Single(build.Skills);
            Assert.Equal("Fireball", build.Skills[0].Name);
        }

        [Fact]
        public void ClientLogMonitor_DetectsZoneTransition_FiresEvent()
        {
            // Arrange
            var monitor = new ClientLogMonitor();
            var tempLogFile = Path.GetTempFileName();
            string? detectedZone = null;
            var resetEvent = new AutoResetEvent(false);

            monitor.ZoneChanged += (s, e) =>
            {
                detectedZone = e.ZoneName;
                resetEvent.Set();
            };

            // Initialize log file with some history
            File.WriteAllText(tempLogFile, "2026/06/14 02:00:00 123456 [Info Client] : You have entered Lioneye's Watch.\n");

            try
            {
                // Act - Start monitoring
                monitor.StartMonitoring(tempLogFile);

                // Append a new zone entry
                using (var writer = File.AppendText(tempLogFile))
                {
                    writer.WriteLine("2026/06/14 02:30:00 123457 [Info Client] : You have entered The Rogue Harbour.");
                    writer.Flush();
                }

                // Assert
                bool eventFired = resetEvent.WaitOne(3000); // 3-second timeout

                Assert.True(eventFired);
                Assert.Equal("The Rogue Harbour", detectedZone);
            }
            finally
            {
                monitor.StopMonitoring();
                int retries = 5;
                while (retries > 0)
                {
                    try
                    {
                        if (File.Exists(tempLogFile))
                        {
                            File.Delete(tempLogFile);
                        }
                        break;
                    }
                    catch (IOException)
                    {
                        retries--;
                        Thread.Sleep(100);
                    }
                }
            }
        }

        [Fact]
        public void FilterBuilder_GeneratesLootFilter_WritesSuccessfully()
        {
            // Arrange
            var builder = new FilterBuilder();
            var outputPath = Path.Combine(Path.GetTempPath(), "PoE2TestFilter.filter");
            var mockItems = new List<MarketItem>
            {
                new MarketItem { Name = "Divine Orb", Category = "Currency", ChaosValue = 125.0 },
                new MarketItem { Name = "Chaos Orb", Category = "Currency", ChaosValue = 1.0 }
            };
            var mockBuild = new PoeBuild
            {
                Name = "Test Build",
                Skills = new List<BuildSkill>
                {
                    new BuildSkill { Name = "Freezing Pulse" }
                }
            };

            try
            {
                // Act
                bool success = builder.GenerateFilterFile(outputPath, null, mockItems, mockBuild, 1, 100.0, 10.0);

                // Assert
                Assert.True(success);
                Assert.True(File.Exists(outputPath));

                var content = File.ReadAllText(outputPath);
                Assert.Contains("PATH OF EXILE 2 DYNAMIC ECONOMY & BUILD FILTER", content);
                Assert.Contains("Divine Orb", content); // Should be Tier 1
                Assert.Contains("Freezing Pulse", content); // Gem highlight
                Assert.DoesNotContain("Chaos Orb", content); // Chaos is < 10.0 so not tier 1 or 2
            }
            finally
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
        }
    }
}
