using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using LootPulse.Models;

namespace LootPulse.Services
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept as instance methods to support future dependency injection, mockability, and extension.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S2325:Methods and properties that don't access instance data should be static", Justification = "Kept as instance methods to support future dependency injection, mockability, and extension.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Catching generic Exception in zlib and XML parsing prevents crashes when user inputs malformed share codes.")]
    public class BuildProfileParser
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };

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
                var build = JsonSerializer.Deserialize<PoeBuild>(json, _jsonOptions);

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
                XmlDocument doc = new() { XmlResolver = null };
                doc.LoadXml(xmlContent);

                var buildNode = doc.SelectSingleNode("/PathOfBuilding/Build");
                var nameAttribute = buildNode?.Attributes?.GetNamedItem("className");

                var build = new PoeBuild
                {
                    Name = $"PoB Import - {nameAttribute?.Value ?? "Build"}",
                    Author = "Imported",
                    Description = "Converted from Path of Building 2 XML"
                };

                ParsePobItems(doc, build);
                ParsePobSkills(doc, build);

                return build;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error converting PoB XML: {ex.Message}");
                return null;
            }
        }

        private void ParsePobItems(XmlDocument doc, PoeBuild build)
        {
            var itemNodes = doc.SelectNodes("/PathOfBuilding/Items/Item");
            if (itemNodes == null) return;

            foreach (XmlNode itemNode in itemNodes)
            {
                var rawText = itemNode.InnerText;
                var lines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) continue;

                var nameLine = lines[0].Trim();
                if (lines.Length > 1 && (nameLine.StartsWith("Rarity:", StringComparison.Ordinal) || nameLine.Contains("Rarity", StringComparison.Ordinal)))
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

        private void ParsePobSkills(XmlDocument doc, PoeBuild build)
        {
            var skillNodes = doc.SelectNodes("/PathOfBuilding/Skills/Skill");
            if (skillNodes == null) return;

            foreach (XmlNode skillNode in skillNodes)
            {
                var gemNodes = skillNode.SelectNodes("Gem");
                if (gemNodes == null) continue;

                foreach (XmlNode gemNode in gemNodes)
                {
                    var nameAttr = gemNode.Attributes?.GetNamedItem("nameSpec");
                    if (nameAttr?.Value != null)
                    {
                        build.Skills.Add(new BuildSkill
                        {
                            Id = nameAttr.Value,
                            Name = nameAttr.Value
                        });
                    }
                }
            }
        }
    }
}
