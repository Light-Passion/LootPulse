using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LootPulse.Services
{
    [JsonSerializable(typeof(NinjaExchangeResponse))]
    [JsonSerializable(typeof(NinjaCurrencyRoot))]
    [JsonSerializable(typeof(NinjaItemRoot))]
    internal sealed partial class PoeNinjaJsonContext : JsonSerializerContext
    {
    }

    // New PoE2 Economy Models
    public record NinjaExchangeResponse(
        [property: JsonPropertyName("core")] NinjaExchangeCore? Core,
        [property: JsonPropertyName("lines")] List<NinjaExchangeLine>? Lines
    );

    public record NinjaExchangeCore(
        [property: JsonPropertyName("items")] List<NinjaExchangeCoreItem>? Items,
        [property: JsonPropertyName("rates")] Dictionary<string, double>? Rates
    );

    public record NinjaExchangeCoreItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("image")] string Image,
        [property: JsonPropertyName("category")] string Category
    );

    public record NinjaExchangeLine(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("primaryValue")] double PrimaryValue
    );

    // Old models kept for compatibility / reference
    public record NinjaCurrencyRoot(
        [property: JsonPropertyName("lines")] List<NinjaCurrencyLine>? Lines
    );

    public record NinjaCurrencyLine(
        [property: JsonPropertyName("currencyTypeName")] string CurrencyTypeName,
        [property: JsonPropertyName("chaosEquivalent")] double ChaosEquivalent
    );

    public record NinjaItemRoot(
        [property: JsonPropertyName("lines")] List<NinjaItemLine>? Lines
    );

    public record NinjaItemLine(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("baseType")] string BaseType,
        [property: JsonPropertyName("chaosValue")] double ChaosValue,
        [property: JsonPropertyName("exaltedValue")] double ExaltedValue,
        [property: JsonPropertyName("divineValue")] double DivineValue
    );
}
