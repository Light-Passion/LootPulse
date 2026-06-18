using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace PoE2.MarketFilter.Probing;

public class PoeNinjaProbingTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ITestOutputHelper _output;

    // Verified PoE2 economy endpoints (see .agents/skills/poe2-ninja-api/SKILL.md §1).
    // The bare https://poe.ninja host returns the website HTML, NOT JSON.
    //  - EXCHANGE: bulk commodities / base-type consumables (currency, runes, soul cores, ...).
    //  - ITEM (stash): unique & named items, richer shape with name/baseType on each line.
    private const string ExchangeUrl = "https://poe.ninja/poe2/api/economy/exchange/current/overview";
    private const string ItemUrl = "https://poe.ninja/poe2/api/economy/stash/current/item/overview";

    // Active indexed PoE2 league (from /poe2/api/data/index-state).
    private const string League = "Runes of Aldur";

    // Captured "verified data maps" land here, one file per type, regardless of console verbosity.
    private static readonly string OutputDir =
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "probe-output");

    public PoeNinjaProbingTests(ITestOutputHelper output)
    {
        _output = output;
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        _httpClient = new HttpClient(handler);

        // Standard Desktop User-Agent Bypass Rule (Cloudflare mitigation - SKILL.md §2.A).
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    // Verified (endpoint, type) catalog - SKILL.md §1.A/§1.B (captured live 2026-06-18).
    // bool isItemEndpoint = true -> stash/item endpoint (rich shape, name+baseType on the line).
    public static IEnumerable<object[]> EconomyTypes()
    {
        // Exchange endpoint - bulk commodities & base-type consumables.
        foreach (var t in new[]
        {
            "Currency", "Fragments", "Runes", "SoulCores", "Essences", "Ritual", "Abyss",
            "UncutGems", "LineageSupportGems", "Idols", "Expedition", "Delirium", "Breach", "Verisium",
        })
        {
            yield return new object[] { ExchangeUrl, t, false };
        }

        // Item (stash) endpoint - unique & named items.
        foreach (var t in new[]
        {
            "UniqueWeapons", "UniqueArmours", "UniqueAccessories", "UniqueFlasks", "UniqueCharms",
            "UniqueJewels", "UniqueSanctumRelics", "UniqueTablets", "PrecursorTablets",
        })
        {
            yield return new object[] { ItemUrl, t, true };
        }
    }

    [Theory]
    [MemberData(nameof(EconomyTypes))]
    public async Task ProbeEndpoint_ValidatesSchemaAndCaptures(string endpoint, string type, bool isItemEndpoint)
    {
        var targetUri = $"{endpoint}?league={Uri.EscapeDataString(League)}&type={type}";
        _output.WriteLine($"Probing: {targetUri}");

        HttpResponseMessage response = await _httpClient.GetAsync(new Uri(targetUri));
        _output.WriteLine($"HTTP Return Code: {response.StatusCode} ({(int)response.StatusCode})");

        // Fail loudly on a non-200: a 404 means a wrong/renamed type token or wrong endpoint,
        // a 403 means the Cloudflare UA bypass regressed. The harness should go red, not silent.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string rawJson = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(rawJson);
        JsonElement root = doc.RootElement;

        // Capture first (before asserts) so a schema drift still leaves a report to inspect.
        bool hasLines = root.TryGetProperty("lines", out JsonElement lines) && lines.ValueKind == JsonValueKind.Array;
        bool hasItems = root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array;
        WriteReport(type, BuildReport(targetUri, (int)response.StatusCode, root,
            hasLines ? lines : default, hasItems ? items : default));

        // Both endpoints expose the core/lines envelope. The root-level `items` name table is REQUIRED
        // on the exchange endpoint (needed to resolve slug ids, SKILL.md §3.A) but is only an optional
        // currency-rate table on the item endpoint, where lines carry name/baseType directly (§3.E).
        Assert.True(hasLines, "Expected a root-level 'lines' array.");
        if (!isItemEndpoint)
        {
            Assert.True(hasItems, "Exchange endpoint must expose a root-level 'items' name table (SKILL.md §3.A).");
        }

        _output.WriteLine($"[OK] {type}: lines={lines.GetArrayLength()}, items={(hasItems ? items.GetArrayLength() : 0)}");

        // Endpoint-specific invariant: the item endpoint must expose name+baseType directly on each
        // line (no slug lookup). Only assert when there's data to inspect.
        if (isItemEndpoint && lines.GetArrayLength() > 0)
        {
            JsonElement first = FirstElement(lines);
            Assert.True(first.TryGetProperty("name", out JsonElement nm) && nm.ValueKind == JsonValueKind.String,
                "Item endpoint lines[] must carry a direct 'name' (SKILL.md §3.E).");
            Assert.True(first.TryGetProperty("baseType", out JsonElement bt) && bt.ValueKind == JsonValueKind.String,
                "Item endpoint lines[] must carry a direct 'baseType' (SKILL.md §3.E).");
            _output.WriteLine($"     sample: name='{nm.GetString()}', baseType='{bt.GetString()}'");
        }

        WriteReport(type, BuildReport(targetUri, (int)response.StatusCode, root, lines, items));
    }

    private static string BuildReport(string uri, int status, JsonElement root, JsonElement lines, JsonElement items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"targetUri\": {JsonSerializer.Serialize(uri)},");
        sb.AppendLine($"  \"httpStatus\": {status},");

        sb.Append("  \"rootKeys\": [");
        bool firstKey = true;
        foreach (JsonProperty p in root.EnumerateObject())
        {
            if (!firstKey) sb.Append(", ");
            firstKey = false;
            sb.Append($"{{ \"name\": {JsonSerializer.Serialize(p.Name)}, \"kind\": \"{p.Value.ValueKind}\" }}");
        }
        sb.AppendLine("],");

        sb.AppendLine($"  \"linesLength\": {ArrayLen(lines)},");
        sb.AppendLine($"  \"itemsLength\": {ArrayLen(items)},");
        sb.AppendLine($"  \"sampleLine\": {SampleFirst(lines)},");
        sb.AppendLine($"  \"sampleItem\": {SampleFirst(items)}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static int ArrayLen(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.GetArrayLength() : 0;

    private static JsonElement FirstElement(JsonElement array)
    {
        foreach (JsonElement e in array.EnumerateArray())
        {
            return e;
        }
        return default;
    }

    private static string SampleFirst(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            return "null";
        }
        return JsonSerializer.Serialize(FirstElement(array));
    }

    private void WriteReport(string type, string content)
    {
        try
        {
            Directory.CreateDirectory(OutputDir);
            string path = Path.Combine(OutputDir, $"{type}.json");
            File.WriteAllText(path, content);
            _output.WriteLine($"[CAPTURED] {Path.GetFullPath(path)}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[WARN] Could not write probe report for '{type}': {ex.Message}");
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
