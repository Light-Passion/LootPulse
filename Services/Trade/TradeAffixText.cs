using System.Text.RegularExpressions;

namespace LootPulse.Services.Trade
{
    /// <summary>
    /// Shared text helpers for matching mod strings. Both the recommended affixes from a build and the
    /// mods on a trade listing are reduced to a numeric-agnostic template (digits → "#") so they can be
    /// compared regardless of the rolled values. Used by <see cref="TradeStatResolver"/> (text → stat id)
    /// and the Best-in-slot scoring (listing mods → matched recommended affixes).
    /// </summary>
    public static partial class TradeAffixText
    {
        [GeneratedRegex(@"\d+(\.\d+)?")]
        private static partial Regex NumberRegex();

        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();

        // GGG inline markup in trade2 mod descriptions: "[Physical]" or "[Reference|Displayed]".
        // The displayed text is the segment after the pipe, or the whole token when there's no pipe.
        [GeneratedRegex(@"\[(?:[^\[\]\|]*\|)?([^\[\]\|]+)\]")]
        private static partial Regex MarkupRegex();

        [GeneratedRegex(@"\blevel\s+off\b", RegexOptions.IgnoreCase)]
        private static partial Regex LevelOffRegex();

        [GeneratedRegex(@"\+\s*#")]
        private static partial Regex PlusHashRegex();

        /// <summary>Reduce mod text to a comparable template, e.g. "253% increased Physical Damage" →
        /// "#% INCREASED PHYSICAL DAMAGE". Strips GGG markup tags so a listing's
        /// "69% increased [Physical] Damage" matches a build's plain "…Physical Damage". Empty for blank.</summary>
        public static string Templatize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }
            string unmarked = MarkupRegex().Replace(text, "$1");
            // Normalize typos like "level off" -> "level of"
            unmarked = LevelOffRegex().Replace(unmarked, "level of");
            string withHashes = NumberRegex().Replace(unmarked, "#");
            // Normalize leading plus prefix, e.g. "+#" -> "#"
            withHashes = PlusHashRegex().Replace(withHashes, "#");
            // Upper-case so matching is case-insensitive (analyzers prefer ToUpperInvariant).
            return WhitespaceRegex().Replace(withHashes, " ").Trim().ToUpperInvariant();
        }

        [GeneratedRegex(@"\d+(?:\.\d+)?|#")]
        private static partial Regex GggTokenRegex();

        [GeneratedRegex(@"\d+(?:\.\d+)?")]
        private static partial Regex NumericTokenRegex();

        [GeneratedRegex(@"(\d+(?:\.\d+)?)\s+to\s+(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
        private static partial Regex RangeRegex();

        /// <summary>
        /// Extract a minimum constraint value from a recommended affix by matching it with GGG's catalog stat text.
        /// Extracts averages for ranges (Adds X to Y) and single numeric/percentage variables directly, while ignoring
        /// static numbers (e.g. 20 in "per 20 Spirit").
        /// </summary>
        public static double? ExtractMinConstraint(string? buildText, string? gggText)
        {
            if (string.IsNullOrWhiteSpace(buildText))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(gggText))
            {
                return ExtractMinConstraintSimple(buildText);
            }

            var gggMatches = GggTokenRegex().Matches(gggText);
            var buildMatches = NumericTokenRegex().Matches(buildText);

            if (gggMatches.Count != buildMatches.Count || gggMatches.Count == 0)
            {
                return ExtractMinConstraintSimple(buildText);
            }

            var variables = new List<double>();
            for (int i = 0; i < gggMatches.Count; i++)
            {
                if (gggMatches[i].Value == "#")
                {
                    if (double.TryParse(buildMatches[i].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
                    {
                        variables.Add(val);
                    }
                }
            }

            if (variables.Count == 1)
            {
                return variables[0];
            }
            if (variables.Count == 2)
            {
                return (variables[0] + variables[1]) / 2.0;
            }

            return null;
        }

        private static double? ExtractMinConstraintSimple(string buildText)
        {
            var rangeMatch = RangeRegex().Match(buildText);
            if (rangeMatch.Success &&
                double.TryParse(rangeMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double minVal) &&
                double.TryParse(rangeMatch.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double maxVal))
            {
                return (minVal + maxVal) / 2.0;
            }

            var matches = NumericTokenRegex().Matches(buildText);
            if (matches.Count == 1 &&
                double.TryParse(matches[0].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double singleVal))
            {
                return singleVal;
            }

            return null;
        }
    }
}
