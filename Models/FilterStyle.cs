using System;

namespace LootPulse.Models
{
    public class CategoryStyle
    {
        public bool Enabled { get; set; } = true;
        public string TextColor { get; set; } = "#FFFFFFFF";       // HEX string (AARRGGBB)
        public string BorderColor { get; set; } = "#FFFF6124";     // HEX string (AARRGGBB)
        public string BackgroundColor { get; set; } = "#00000000"; // HEX string (AARRGGBB)
        public int FontSize { get; set; } = 35;
        
        // Sound Options
        public string SoundType { get; set; } = "BuiltIn";       // "BuiltIn", "Custom", or "None"
        public int AlertSoundId { get; set; } = 1;               // ID 1-16 for BuiltIn
        public string CustomSoundPath { get; set; } = "";        // Path to custom .wav/.mp3 file
        public bool MuteDefaultDropSound { get; set; } = false;  // Maps to DisableDropSound
        
        // Minimap / Effects
        public string MinimapIcon { get; set; } = "0 Orange Star"; // Size Color Shape (e.g., "0 Orange Star")
        public string PlayEffect { get; set; } = "Orange Temp";    // Color Temp (e.g., "Orange Temp")
    }

    public class FilterTheme
    {
        public string ThemeName { get; set; } = "Custom Theme";
        
        public CategoryStyle Uniques { get; set; } = new() 
        { 
            TextColor = "#FFFF6124", 
            BorderColor = "#FFFF6124", 
            BackgroundColor = "#00000000",
            FontSize = 45, 
            SoundType = "BuiltIn", 
            AlertSoundId = 1, 
            MinimapIcon = "0 Orange Star", 
            PlayEffect = "Orange Temp",
            MuteDefaultDropSound = false
        };

        public CategoryStyle Gems { get; set; } = new() 
        { 
            TextColor = "#FFFF6124", 
            BorderColor = "#FF00C8FF", 
            BackgroundColor = "#00000000",
            FontSize = 40, 
            SoundType = "None", 
            AlertSoundId = 0, 
            MinimapIcon = "", 
            PlayEffect = "",
            MuteDefaultDropSound = false
        };

        public CategoryStyle ProgressionBases { get; set; } = new() 
        { 
            TextColor = "#FFFFFFFF", 
            BorderColor = "#FFFF6124", 
            BackgroundColor = "#00000000",
            FontSize = 35, 
            SoundType = "None", 
            AlertSoundId = 0, 
            MinimapIcon = "", 
            PlayEffect = "White Temp",
            MuteDefaultDropSound = false
        };

        public CategoryStyle EconomyTier1 { get; set; } = new() 
        { 
            TextColor = "#FFFF0000", 
            BorderColor = "#FFFF0000", 
            BackgroundColor = "#FFFFFFFF", 
            FontSize = 45, 
            SoundType = "BuiltIn", 
            AlertSoundId = 6, 
            MinimapIcon = "0 Red Star", 
            PlayEffect = "Red",
            MuteDefaultDropSound = false
        };

        public CategoryStyle EconomyTier2 { get; set; } = new() 
        { 
            TextColor = "#FFE5B560", 
            BorderColor = "#FFE5B560", 
            BackgroundColor = "#00000000",
            FontSize = 40, 
            SoundType = "BuiltIn", 
            AlertSoundId = 2, 
            MinimapIcon = "1 Yellow Star", 
            PlayEffect = "Yellow Temp",
            MuteDefaultDropSound = false
        };
    }
}
