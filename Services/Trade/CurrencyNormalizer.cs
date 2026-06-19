using System.Globalization;

namespace LootPulse.Services.Trade
{
    /// <summary>
    /// Converts trade listing prices (which come back in whatever currency the seller chose) into a
    /// common value so the Trade Market tab can compare and re-sort "cheapest" across mixed currencies.
    ///
    /// Per the PoE2 economy rules (poe2_validator §3.A), the player-to-player trade base is
    /// <b>Exalted Orbs, then Divine Orbs</b> — Chaos Orbs are NOT a trade base. So everything is
    /// normalized to an Exalted-equivalent value for ranking; a Chaos-listed price is valued via its
    /// worth in Divines/Exalts rather than treated as a base. The default rates are shared with
    /// <see cref="FilterBuilder"/> so the constants live in one place.
    /// </summary>
    public sealed class CurrencyRates
    {
        // poe.ninja quotes orbs in chaos; we keep those as the raw inputs and derive exalted-relative
        // factors from them. (Chaos is an internal conversion unit only, never the displayed base.)
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

        /// <summary>How many Exalted Orbs one Divine Orb is worth (the secondary→primary base rate).</summary>
        public double DivineInExalted => DivineInChaos / ExaltedInChaos;

        /// <summary>How many Exalted Orbs one Chaos Orb is worth (chaos valued against the base, not as one).</summary>
        public double ChaosInExalted => 1.0 / ExaltedInChaos;

        /// <summary>
        /// Convert <paramref name="amount"/> of <paramref name="currency"/> to its Exalted-equivalent
        /// value (the primary trade base), or null when the currency isn't one we value — so the caller
        /// can sort those listings last.
        /// </summary>
        public double? ToExalted(double amount, string? currency)
        {
            if (string.IsNullOrWhiteSpace(currency))
            {
                return null;
            }

            return currency.Trim().ToUpperInvariant() switch
            {
                "EXALTED" or "EXALT" or "EX" => amount,
                "DIVINE" or "DIV" => amount * DivineInExalted,
                "CHAOS" => amount * ChaosInExalted,
                _ => null,
            };
        }

        /// <summary>
        /// Display label for an Exalted-equivalent value: shown in Exalts, switching to Divines once it
        /// is worth at least one Divine (Exalts then Divines). Empty when unknown.
        /// </summary>
        public string Format(double? exalted)
        {
            if (exalted is not { } ex)
            {
                return string.Empty;
            }

            if (DivineInExalted > 0 && ex >= DivineInExalted)
            {
                double div = ex / DivineInExalted;
                return "≈ " + div.ToString("0.#", CultureInfo.InvariantCulture) + " div";
            }
            return "≈ " + ex.ToString("0.#", CultureInfo.InvariantCulture) + " ex";
        }
    }
}
