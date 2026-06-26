using System;
using System.Text.RegularExpressions;

namespace LootPulse.Services
{
    public static partial class ZoneLookup
    {
        [GeneratedRegex(@"\bLevel\s+(\d+)")]
        private static partial Regex LevelInNameRegex();

        private static readonly (string Substring, int Level)[] _zoneLevels = [
            ("oakhaven", 1),
            ("lioneye's watch", 1),
            ("the coast", 2),
            ("mud flats", 3),
            ("the grotto", 4),
            ("the ridge", 5),
            ("great forest", 6),
            ("hooded copse", 7),
            ("riverways", 8),
            ("oasis", 10),
            ("forest encampment", 16),
            ("fields", 16),
            ("ruins", 18),
            ("sarn encampment", 33),
            ("slums", 33)
        ];

        public static int GetZoneLevelFromName(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName)) return 1;

            foreach (var (sub, zoneLvl) in _zoneLevels)
            {
                if (zoneName.Contains(sub, StringComparison.OrdinalIgnoreCase))
                {
                    return zoneLvl;
                }
            }

            var levelInNameMatch = LevelInNameRegex().Match(zoneName);
            if (levelInNameMatch.Success && int.TryParse(levelInNameMatch.Groups[1].Value, out int lvl))
            {
                return lvl;
            }

            return 1;
        }
    }
}
