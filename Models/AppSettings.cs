using System.Collections.Generic;

namespace LootPulse.Models
{
    public class AppSettings
    {
        public string LogPath { get; set; } = string.Empty;
        public string FilterOutputPath { get; set; } = string.Empty;
        public string SelectedBaseFilterPath { get; set; } = string.Empty;
        public string BuildFilePath { get; set; } = string.Empty;
        public double Tier1Threshold { get; set; } = 1.0;
        public double Tier2Threshold { get; set; } = 1.0;
        public double HudWidth { get; set; } = 250;
        public double HudHeight { get; set; } = 120;
        public double HudXPercent { get; set; } = 0.80;
        public double HudYPercent { get; set; } = 0.05;
        public double EditModeOpacity { get; set; } = 0.85;
        public double HudModeOpacity { get; set; } = 0.30;
        public bool IsHudVisible { get; set; } = true;
        public bool ShowEconomyHighlights { get; set; } = true;
        public string League { get; set; } = "Runes of Aldur";

        public Dictionary<string, Dictionary<string, AffixImportance>> BuildCustomWeights { get; set; } = [];
    }
}
