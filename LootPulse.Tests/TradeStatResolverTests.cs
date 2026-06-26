using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LootPulse.Models;
using LootPulse.Services.Trade;
using Xunit;

namespace LootPulse.Tests;

public class TradeStatResolverTests
{
    private class MockTradeTransport : ITradeTransport
    {
        public bool IsConnected { get; set; } = true;
        public int SendCount { get; private set; }
        public TradeHttpResponse Response { get; set; } = new() { StatusCode = HttpStatusCode.OK, Body = "{}" };

        public Task<bool> ConnectAsync(CancellationToken ct = default) => Task.FromResult(IsConnected);

        public Task<TradeHttpResponse> SendAsync(HttpMethod method, string url, string? jsonBody, CancellationToken ct = default)
        {
            SendCount++;
            return Task.FromResult(Response);
        }
    }

    private readonly MockTradeTransport _transport = new();
    private readonly TradeRateLimiter _rateLimiter = new();
    private readonly TradeStatResolver _resolver;

    public TradeStatResolverTests()
    {
        _resolver = new TradeStatResolver(_transport, _rateLimiter);
        // Speed up the rate limiter for tests to avoid delays during retries
        _rateLimiter.Observe(new TradeHttpResponse
        {
            StatusCode = HttpStatusCode.OK,
            Headers = new Dictionary<string, string>
            {
                { "x-rate-limit-rules", "ip" },
                { "x-rate-limit-ip", "1000:1:0" }
            }
        });
    }

    [Fact]
    public async Task ResolveDetailedAsync_EmptyAffixes_ReturnsEmpty()
    {
        var result = await _resolver.ResolveDetailedAsync(Array.Empty<BuildAffix>());
        Assert.Empty(result);
        Assert.Equal(0, _transport.SendCount);
    }

    [Fact]
    public async Task ResolveDetailedAsync_NullAffixes_ReturnsEmpty()
    {
        var result = await _resolver.ResolveDetailedAsync(null!);
        Assert.Empty(result);
        Assert.Equal(0, _transport.SendCount);
    }

    [Fact]
    public async Task ResolveDetailedAsync_NotConnected_ReturnsEmpty()
    {
        _transport.IsConnected = false;
        var affixes = new[] { new BuildAffix { Text = "Some Mod" } };

        var result = await _resolver.ResolveDetailedAsync(affixes);

        Assert.Empty(result);
        Assert.Equal(0, _transport.SendCount);
    }

    [Fact]
    public async Task ResolveDetailedAsync_Success_MapsAndCaches()
    {
        var statsJson = @"
        {
            ""result"": [
                {
                    ""id"": ""explicit"",
                    ""entries"": [
                        { ""id"": ""stat_1"", ""text"": ""#% increased Physical Damage"", ""type"": ""explicit"" },
                        { ""id"": ""stat_2"", ""text"": ""+# to maximum Life"", ""type"": ""explicit"" }
                    ]
                }
            ]
        }";
        _transport.Response = new TradeHttpResponse { StatusCode = HttpStatusCode.OK, Body = statsJson };

        var affixes = new[]
        {
            new BuildAffix { Text = "25% increased Physical Damage" },
            new BuildAffix { Text = "+10 to maximum Life" }
        };

        // First call
        var result = await _resolver.ResolveDetailedAsync(affixes);

        Assert.Equal(2, result.Count);
        Assert.Equal("stat_1", result[0].StatId);
        Assert.Equal("stat_2", result[1].StatId);
        Assert.Equal(1, _transport.SendCount);

        // Second call - should use cache
        var result2 = await _resolver.ResolveDetailedAsync(affixes);
        Assert.Equal(2, result2.Count);
        Assert.Equal(1, _transport.SendCount);
    }

    [Fact]
    public async Task ResolveDetailedAsync_DeduplicatesByStatId()
    {
         var statsJson = @"
        {
            ""result"": [
                {
                    ""id"": ""explicit"",
                    ""entries"": [
                        { ""id"": ""stat_1"", ""text"": ""#% increased Physical Damage"", ""type"": ""explicit"" }
                    ]
                }
            ]
        }";
        _transport.Response = new TradeHttpResponse { StatusCode = HttpStatusCode.OK, Body = statsJson };

        var affixes = new[]
        {
            new BuildAffix { Text = "25% increased Physical Damage" },
            new BuildAffix { Text = "10% increased Physical Damage" }
        };

        var result = await _resolver.ResolveDetailedAsync(affixes);

        Assert.Single(result);
        Assert.Equal("stat_1", result[0].StatId);
    }

    [Fact]
    public async Task ResolveDetailedAsync_ExplicitTakesPriority()
    {
        // If two stats have same templatized text, explicit should win because of OrderBy in FetchTableAsync
        var statsJson = @"
        {
            ""result"": [
                {
                    ""id"": ""implicit"",
                    ""entries"": [
                        { ""id"": ""stat_implicit"", ""text"": ""#% increased Physical Damage"", ""type"": ""implicit"" }
                    ]
                },
                {
                    ""id"": ""explicit"",
                    ""entries"": [
                        { ""id"": ""stat_explicit"", ""text"": ""#% increased Physical Damage"", ""type"": ""explicit"" }
                    ]
                }
            ]
        }";
        _transport.Response = new TradeHttpResponse { StatusCode = HttpStatusCode.OK, Body = statsJson };

        var affixes = new[] { new BuildAffix { Text = "25% increased Physical Damage" } };

        var result = await _resolver.ResolveDetailedAsync(affixes);

        Assert.Single(result);
        Assert.Equal("stat_explicit", result[0].StatId);
    }

    [Fact]
    public async Task ResolveDetailedAsync_FetchFailure_RetriesNextTime()
    {
        _transport.Response = new TradeHttpResponse { StatusCode = HttpStatusCode.InternalServerError, Body = "Error" };
        var affixes = new[] { new BuildAffix { Text = "Some Mod" } };

        // First call - fails
        var result1 = await _resolver.ResolveDetailedAsync(affixes);
        Assert.Empty(result1);
        Assert.Equal(1, _transport.SendCount);

        // Second call - retries
        _transport.Response = new TradeHttpResponse {
            StatusCode = HttpStatusCode.OK,
            Body = "{\"result\": [{\"entries\": [{\"id\": \"s1\", \"text\": \"Some Mod\", \"type\": \"explicit\"}]}]}"
        };
        var result2 = await _resolver.ResolveDetailedAsync(affixes);
        Assert.Single(result2);
        Assert.Equal("s1", result2[0].StatId);
        Assert.Equal(2, _transport.SendCount);
    }

    [Fact]
    public async Task ResolveDetailedAsync_InvalidJson_HandlesGracefully()
    {
        _transport.Response = new TradeHttpResponse { StatusCode = HttpStatusCode.OK, Body = "{ invalid json" };
        var affixes = new[] { new BuildAffix { Text = "Some Mod" } };

        var result = await _resolver.ResolveDetailedAsync(affixes);

        Assert.Empty(result);
        Assert.Equal(1, _transport.SendCount);
    }

    [Fact]
    public async Task ResolveDetailedAsync_EmptyResponse_ReturnsEmpty()
    {
        _transport.Response = new TradeHttpResponse { StatusCode = HttpStatusCode.OK, Body = "{\"result\": []}" };
        var affixes = new[] { new BuildAffix { Text = "Some Mod" } };

        var result = await _resolver.ResolveDetailedAsync(affixes);

        Assert.Empty(result);
        Assert.Equal(1, _transport.SendCount);
    }
}
