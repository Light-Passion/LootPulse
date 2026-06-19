using System;
using System.Globalization;

namespace LootPulse.Services.Trade
{
    /// <summary>
    /// Converts trade listing prices (which come back in whatever currency the seller chose —
    /// chaos, exalted, divine, …) into a common chaos value so the Trade Market tab can compare and
    /// re-sort "cheapest" across mixed currencies. Per the PoE2 economy rules, only the three base
    /// currencies (Chaos/Exalted/Divine) are valued; anything else is left unconverted.
    /// The default rates are shared with <see cref="FilterBuilder"/> so the constants live in one place.
    /// </summary>
    public sealed class CurrencyRates
    {
        public const double DefaultDivineInChaos = 120.0;
        public const double DefaultExaltedInChaos = 15.0;

        public double DivineInChaos { get; }
        public double ExaltedInChaos { get; }

        public CurrencyRates(double divineInChaos, double exaltedInChaos)
        {
            DivineInChaos = divineInChaos > 0 ? divineInChaos : DefaultDivineInChaos;
            ExaltedInChaos = exaltedInChaos > 0 ? exaltedInChaos : DefaultExaltedInChaos;
        }

        public static CurrencyRates Default { get; } = new(DefaultDivineInChaos, DefaultExaltedInChaos);

        /// <summary>
        /// Convert <paramref name="amount"/> of <paramref name="currency"/> to chaos, or null when the
        /// currency isn't one we value (so the caller can sort those listings last).
        /// </summary>
        public double? ToChaos(double amount, string? currency)
        {
            if (string.IsNullOrWhiteSpace(currency))
            {
                return null;
            }

            return currency.Trim().ToUpperInvariant() switch
            {
                "CHAOS" => amount,
                "DIVINE" or "DIV" => amount * DivineInChaos,
                "EXALTED" or "EXALT" or "EX" => amount * ExaltedInChaos,
                _ => null,
            };
        }

        /// <summary>"≈ 600c" style label for a chaos value, or empty when unknown.</summary>
        public static string FormatChaos(double? chaos)
        {
            if (chaos is not { } c)
            {
                return string.Empty;
            }
            return "≈ " + c.ToString("N0", CultureInfo.InvariantCulture) + "c";
        }
    }
}
