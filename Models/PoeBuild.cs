using System.Collections.Generic;

namespace LootPulse.Models
{
    public class PoeBuild
    {
        public string Name { get; set; } = string.Empty;
        public string? Author { get; set; }
        public string? Description { get; set; }
        public string? Ascendancy { get; set; }
        public List<BuildPassive> Passives { get; set; } = new();
        public List<BuildSkill> Skills { get; set; } = new();
        public List<BuildInventorySlot> InventorySlots { get; set; } = new();
    }

    public class BuildPassive
    {
        public string Id { get; set; } = string.Empty;
    }

    public class BuildSkill
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<int>? LevelInterval { get; set; }
        public List<BuildSupportSkill> SupportSkills { get; set; } = new();
    }

    public class BuildSupportSkill
    {
        public string Id { get; set; } = string.Empty;
        public List<int>? LevelInterval { get; set; }
    }

    public class BuildInventorySlot
    {
        public string InventoryId { get; set; } = string.Empty; // e.g. Weapon1, Ring1
        public string? UniqueName { get; set; }
        public string? AdditionalText { get; set; }
        public List<int>? LevelInterval { get; set; }

        // Helper property to resolve the display name for the loot filter
        public string ItemName
        {
            get
            {
                if (!string.IsNullOrEmpty(UniqueName))
                    return UniqueName;

                if (!string.IsNullOrEmpty(AdditionalText))
                {
                    // Extract first line of additional text (e.g. Sacred Maul, Falconer's Jacket)
                    var lines = AdditionalText.Split('\n');
                    if (lines.Length > 0)
                        return lines[0].Trim();
                }

                return string.Empty;
            }
        }
    }
}
