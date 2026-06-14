using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using LootPulse.Models;

namespace LootPulse.Services
{
    public class BuildProfileParser
    {
        /// <summary>
        /// Parses a native Path of Exile 2 in-game build planner JSON file (.build).
        /// </summary>
        public PoeBuild? ParseBuildFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                var json = File.ReadAllText(filePath);
                var build = JsonSerializer.Deserialize<PoeBuild>(json, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    PropertyNameCaseInsensitive = true
                });

                return build;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing build file: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Decodes a Path of Building (PoB) URL-safe Base64 string and returns the raw XML content.
        /// </summary>
        public string? DecodePobShareCode(string shareCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(shareCode))
                    return null;

                // 1. Normalize URL-safe Base64 padding and characters
                string normalized = shareCode.Replace('-', '+').Replace('_', '/');
                int mod = normalized.Length % 4;
                if (mod > 0)
                {
                    normalized += new string('=', 4 - mod);
                }

                // 2. Decode Base64 to byte array
                byte[] data = Convert.FromBase64String(normalized);
                if (data.Length < 2)
                    return null;

                // 3. Decompress zlib format.
                // Note: The first two bytes of zlib are header bytes (often 0x78 0x9C).
                // DeflateStream expects raw deflate stream without the zlib header.
                using var ms = new MemoryStream(data, 2, data.Length - 2);
                using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                using var reader = new StreamReader(deflate, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error decoding PoB share code: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Converts decoded PoB XML into a native PoeBuild model.
        /// </summary>
        public PoeBuild? ConvertPobXmlToPoeBuild(string xmlContent)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xmlContent);

                var buildNode = doc.SelectSingleNode("/PathOfBuilding/Build");
                var nameAttribute = buildNode?.Attributes?.GetNamedItem("className");
                
                var build = new PoeBuild
                {
                    Name = $"PoB Import - {nameAttribute?.Value ?? "Build"}",
                    Author = "Imported",
                    Description = "Converted from Path of Building 2 XML"
                };

                // Parse Items
                var itemNodes = doc.SelectNodes("/PathOfBuilding/Items/Item");
                if (itemNodes != null)
                {
                    foreach (XmlNode itemNode in itemNodes)
                    {
                        var rawText = itemNode.InnerText;
                        var lines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 0)
                        {
                            var nameLine = lines[0].Trim();
                            // In PoB, uniques/rares usually have name on line 2 if line 1 has rarity
                            if (lines.Length > 1 && (nameLine.StartsWith("Rarity:") || nameLine.Contains("Rarity")))
                            {
                                nameLine = lines[1].Trim();
                            }
                            
                             build.InventorySlots.Add(new BuildInventorySlot
                             {
                                 UniqueName = nameLine,
                                 InventoryId = "Imported"
                             });
                         }
                     }
                 }
 
                 // Parse Skills
                 var skillNodes = doc.SelectNodes("/PathOfBuilding/Skills/Skill");
                 if (skillNodes != null)
                 {
                     foreach (XmlNode skillNode in skillNodes)
                     {
                         var gemNodes = skillNode.SelectNodes("Gem");
                         if (gemNodes != null)
                         {
                             foreach (XmlNode gemNode in gemNodes)
                             {
                                 var nameAttr = gemNode.Attributes?.GetNamedItem("nameSpec");
                                 if (nameAttr != null)
                                 {
                                     build.Skills.Add(new BuildSkill
                                     {
                                         Id = nameAttr.Value ?? string.Empty,
                                         Name = nameAttr.Value ?? string.Empty
                                     });
                                 }
                            }
                        }
                    }
                }

                return build;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error converting PoB XML: {ex.Message}");
                return null;
            }
        }
    }
}
