using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LootPulse.Services.Trade
{
    // ---- Source-generated JSON context (same zero-reflection pattern as PoeNinjaJsonContext) ----
    // Null request properties are omitted so we never send `"name": null` to the trade API.
    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(TradeSearchRequest))]
    [JsonSerializable(typeof(TradeSearchResponse))]
    [JsonSerializable(typeof(TradeFetchResponse))]
    [JsonSerializable(typeof(TradeStatsDataResponse))]
    internal sealed partial class Poe2TradeJsonContext : JsonSerializerContext
    {
    }

    // ---- Request shape: POST /api/trade2/search/<league> ----
    // { "query": { "status": {"option":"online"}, "name"?, "type"?, "stats":[{"type":"and","filters":[]}],
    //   "filters": { "req_filters": { "filters": { "lvl": { "max": <charLevel> } } } } },
    //   "sort": { "price": "asc" } }
    public record TradeSearchRequest(
        [property: JsonPropertyName("query")] TradeQuery Query,
        [property: JsonPropertyName("sort")] TradeSort Sort
    );

    public record TradeQuery(
        [property: JsonPropertyName("status")] TradeStatus Status,
        [property: JsonPropertyName("stats")] IReadOnlyList<TradeStatGroup> Stats,
        [property: JsonPropertyName("filters")] TradeFilters Filters,
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("type")] string? Type = null
    );

    public record TradeStatus(
        [property: JsonPropertyName("option")] string Option
    );

    // A stats group. type "and" = match all; type "count" = match at least Value.Min of the Filters
    // (used for the build's recommended affixes — the user picks the minimum number that must match).
    public record TradeStatGroup(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("filters")] IReadOnlyList<TradeStatFilter> Filters,
        [property: JsonPropertyName("value")] TradeMinMax? Value = null
    );

    // One stat in a group. We match on presence (id only) — a listing "matches" the affix if it has
    // that mod at all; the count group's Value.Min then controls how many must be present.
    public record TradeStatFilter(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("value")] TradeMinMax? Value = null
    );

    public record TradeFilters(
        [property: JsonPropertyName("type_filters")] TradeTypeFilters? TypeFilters = null,
        [property: JsonPropertyName("req_filters")] TradeReqFilters? ReqFilters = null
    );

    public record TradeTypeFilters(
        [property: JsonPropertyName("filters")] TradeTypeFilterValues Filters
    );

    public record TradeTypeFilterValues(
        [property: JsonPropertyName("rarity")] TradeOption? Rarity = null
    );

    public record TradeOption(
        [property: JsonPropertyName("option")] string Option
    );

    public record TradeReqFilters(
        [property: JsonPropertyName("filters")] TradeReqFilterValues Filters
    );

    public record TradeReqFilterValues(
        [property: JsonPropertyName("lvl")] TradeMinMax Lvl
    );

    public record TradeMinMax(
        [property: JsonPropertyName("min")] int? Min = null,
        [property: JsonPropertyName("max")] int? Max = null
    );

    public record TradeSort(
        [property: JsonPropertyName("price")] string Price
    );

    // ---- Search response: { "id": "...", "total": N, "result": ["hash", ...] } ----
    public record TradeSearchResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("result")] List<string>? Result
    );

    // ---- Fetch response: GET /api/trade2/fetch/<hashes>?query=<id> ----
    public record TradeFetchResponse(
        [property: JsonPropertyName("result")] List<TradeFetchEntry?>? Result
    );

    public record TradeFetchEntry(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("listing")] TradeFetchListing? Listing,
        [property: JsonPropertyName("item")] TradeFetchItem? Item
    );

    public record TradeFetchListing(
        [property: JsonPropertyName("price")] TradePrice? Price,
        [property: JsonPropertyName("account")] TradeAccount? Account
    );

    public record TradePrice(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("amount")] double Amount,
        [property: JsonPropertyName("currency")] string? Currency
    );

    public record TradeAccount(
        [property: JsonPropertyName("name")] string? Name
    );

    public record TradeFetchItem(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("baseType")] string? BaseType,
        [property: JsonPropertyName("typeLine")] string? TypeLine
    );

    // ---- Stats lookup table: GET /api/trade2/data/stats ----
    // { "result": [ { "id":"explicit", "label":"Explicit", "entries":[
    //     { "id":"explicit.stat_3299347043", "text":"#% increased Physical Damage", "type":"explicit" } ] } ] }
    // Maps the build's human-readable affix text to the stat IDs the search "stats" filter requires.
    public record TradeStatsDataResponse(
        [property: JsonPropertyName("result")] List<TradeStatsCategory>? Result
    );

    public record TradeStatsCategory(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("entries")] List<TradeStatEntry>? Entries
    );

    public record TradeStatEntry(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("type")] string? Type
    );
}
