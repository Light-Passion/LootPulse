namespace LootPulse.Models
{
    public class PlayerState
    {
        public string CharacterName { get; set; } = string.Empty;
        public int Level { get; set; } = 1;
        public string CurrentZone { get; set; } = "Lioneye's Watch";
        public int ZoneLevel { get; set; } = 1;
        public string CurrentAct { get; set; } = "Act 1";
    }
}
