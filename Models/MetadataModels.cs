using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LootPulse.Models
{
    public record BaseItemsConfig(
        [property: JsonPropertyName("archetypes")] Dictionary<string, List<BaseItemInfoRecord>> Archetypes,
        [property: JsonPropertyName("keywordArchetypeMappings")] List<KeywordMappingRecord> KeywordArchetypeMappings,
        [property: JsonPropertyName("knownUniqueBaseTypes")] Dictionary<string, string> KnownUniqueBaseTypes
    );

    public record BaseItemInfoRecord(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("requiredLevel")] int RequiredLevel
    );

    public record KeywordMappingRecord(
        [property: JsonPropertyName("keywords")] List<string> Keywords,
        [property: JsonPropertyName("archetype")] string Archetype
    );

    public record EconomyCategoryRecord(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("isExchange")] bool IsExchange
    );
}
