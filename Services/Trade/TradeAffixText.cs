using System.Text.RegularExpressions;

namespace LootPulse.Services.Trade
{
    /// <summary>
    /// Shared text helpers for matching mod strings. Both the recommended affixes from a build and the
    /// mods on a trade listing are reduced to a numeric-agnostic template (digits → "#") so they can be
    /// compared regardless of the rolled values. Used by <see cref="TradeStatResolver"/> (text → stat id)
    /// and the Best-in-slot scoring (listing mods → matched recommended affixes).
    /// </summary>
    public static class TradeAffixText
    {
        private static readonly Regex NumberRegex = new(@"\d+(\.\d+)?", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

        // GGG inline markup in trade2 mod descriptions: "[Physical]" or "[Reference|Displayed]".
        // The displayed text is the segment after the pipe, or the whole token when there's no pipe.
        private static readonly Regex MarkupRegex = new(@"\[(?:[^\[\]\|]*\|)?([^\[\]\|]+)\]", RegexOptions.Compiled);

        /// <summary>Reduce mod text to a comparable template, e.g. "253% increased Physical Damage" →
        /// "#% INCREASED PHYSICAL DAMAGE". Strips GGG markup tags so a listing's
        /// "69% increased [Physical] Damage" matches a build's plain "…Physical Damage". Empty for blank.</summary>
        public static string Templatize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }
            string unmarked = MarkupRegex.Replace(text, "$1");
            string withHashes = NumberRegex.Replace(unmarked, "#");
            // Upper-case so matching is case-insensitive (analyzers prefer ToUpperInvariant).
            return WhitespaceRegex.Replace(withHashes, " ").Trim().ToUpperInvariant();
        }
    }
}
