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

        /// <summary>Reduce mod text to a comparable template, e.g. "253% increased Physical Damage" →
        /// "#% INCREASED PHYSICAL DAMAGE". Empty for null/blank input.</summary>
        public static string Templatize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }
            string withHashes = NumberRegex.Replace(text, "#");
            // Upper-case so matching is case-insensitive (analyzers prefer ToUpperInvariant).
            return WhitespaceRegex.Replace(withHashes, " ").Trim().ToUpperInvariant();
        }
    }
}
