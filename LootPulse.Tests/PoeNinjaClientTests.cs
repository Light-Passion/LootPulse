using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LootPulse.Models;
using LootPulse.Services;
using Xunit;

namespace LootPulse.Tests;

public class PoeNinjaClientTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> SendAsyncFunc { get; set; } = default!;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return SendAsyncFunc(request);
        }
    }

    [Fact]
    public async Task FetchCurrencyPricesAsync_Success_ReturnsMappedItems()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var client = new PoeNinjaClient(httpClient);

        var responseData = new
        {
            core = new
            {
                rates = new Dictionary<string, double>
                {
                    { "exalted", 15.5 },
                    { "chaos", 120.0 }
                }
            },
            lines = new[]
            {
                new { id = "divine-orb", primaryValue = 1.0 },
                new { id = "chaos-orb", primaryValue = 0.00833 }
            },
            items = new[]
            {
                new { id = "divine-orb", name = "Divine Orb", category = "Currency" },
                new { id = "chaos-orb", name = "Chaos Orb", category = "Currency" }
            }
        };

        mockHandler.SendAsyncFunc = (request) =>
        {
            var json = JsonSerializer.Serialize(responseData);
            return Task.FromResult(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json)
            });
        };

        // Act
        var result = await client.FetchCurrencyPricesAsync("Standard");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);

        var divine = result.Find(i => i.Name == "Divine Orb");
        Assert.NotNull(divine);
        Assert.Equal(1.0, divine.DivineValue);
        Assert.Equal(15.5, divine.ExaltedValue);
        Assert.Equal(120.0, divine.ChaosValue);

        var chaos = result.Find(i => i.Name == "Chaos Orb");
        Assert.NotNull(chaos);
        Assert.Equal(0.00833, chaos.DivineValue);
        Assert.Equal(0.00833 * 15.5, chaos.ExaltedValue, 5);
        Assert.Equal(0.00833 * 120.0, chaos.ChaosValue, 5);
    }

    [Fact]
    public async Task FetchCurrencyPricesAsync_Fallback_TriesStandardLeague()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var client = new PoeNinjaClient(httpClient);

        int callCount = 0;
        string? lastLeague = null;

        mockHandler.SendAsyncFunc = (request) =>
        {
            callCount++;
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("league=Temporary"))
            {
                lastLeague = "Temporary";
                // Return empty response for initial league
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("{\"lines\": []}")
                });
            }
            else if (uri.Contains("league=Standard"))
            {
                lastLeague = "Standard";
                var responseData = new
                {
                    lines = new[] { new { id = "divine-orb", primaryValue = 1.0 } },
                    items = new[] { new { id = "divine-orb", name = "Divine Orb", category = "Currency" } }
                };
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(JsonSerializer.Serialize(responseData))
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        // Act
        var result = await client.FetchCurrencyPricesAsync("Temporary");

        // Assert
        Assert.Equal(2, callCount);
        Assert.Equal("Standard", lastLeague);
        Assert.Single(result);
        Assert.Equal("Divine Orb", result[0].Name);
    }

    [Fact]
    public async Task FetchCurrencyPricesAsync_EmptyResponse_ReturnsEmptyList()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var client = new PoeNinjaClient(httpClient);

        mockHandler.SendAsyncFunc = (request) =>
        {
            return Task.FromResult(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"lines\": null}")
            });
        };

        // Act
        var result = await client.FetchCurrencyPricesAsync("Standard");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task FetchCurrencyPricesAsync_HttpRequestException_ReturnsEmptyList()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var client = new PoeNinjaClient(httpClient);

        mockHandler.SendAsyncFunc = (request) =>
        {
            throw new HttpRequestException("Network error");
        };

        // Act
        var result = await client.FetchCurrencyPricesAsync("Standard");

        // Assert
        Assert.Empty(result);
    }
}
