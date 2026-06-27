using System.Collections.Generic;
using System.Text.Json.Serialization;
using LootPulse.Models;

namespace LootPulse.Services
{
    [JsonSerializable(typeof(NinjaExchangeResponse))]
    [JsonSerializable(typeof(NinjaCurrencyRoot))]
    [JsonSerializable(typeof(NinjaItemRoot))]
    [JsonSerializable(typeof(NinjaStashResponse))]
    [JsonSerializable(typeof(BaseItemsConfig))]
    [JsonSerializable(typeof(List<EconomyCategoryRecord>))]
    [JsonSerializable(typeof(GggStaticDataResponse))]
    internal sealed partial class PoeNinjaJsonContext : JsonSerializerContext;


    // New PoE2 Economy Models
    public record NinjaExchangeResponse(
        [property: JsonPropertyName("core")] NinjaExchangeCore? Core,
        [property: JsonPropertyName("lines")] List<NinjaExchangeLine>? Lines,
        [property: JsonPropertyName("items")] List<NinjaExchangeCoreItem>? Items
    );

    public record NinjaStashResponse(
        [property: JsonPropertyName("core")] NinjaExchangeCore? Core,
        [property: JsonPropertyName("lines")] List<NinjaStashLine>? Lines
    );

    public record NinjaStashLine(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("detailsId")] string DetailsId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("baseType")] string BaseType,
        [property: JsonPropertyName("primaryValue")] double PrimaryValue
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

    // GGG Official Trade Static Data Models
    public record GggStaticDataResponse([property: JsonPropertyName("result")] List<GggStaticCategory> Result);
    public record GggStaticCategory(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("entries")] List<GggStaticEntry> Entries
    );
    public record GggStaticEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("type")] string? Type
    );
}
