using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using LootPulse.Models;
using LootPulse.Services;
using Xunit;

namespace LootPulse.Tests;

public class BuildProfileParserTests : IDisposable
{
    private readonly BuildProfileParser _parser = new();
    private readonly string _testTempDir;

    public BuildProfileParserTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testTempDir))
        {
            Directory.Delete(_testTempDir, true);
        }
    }

    [Fact]
    public async Task ParseBuildFile_FileDoesNotExist_ReturnsNull()
    {
        // Act
        var result = await _parser.ParseBuildFileAsync("non_existent_file.build");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ParseBuildFile_ValidJson_ReturnsPoeBuild()
    {
        // Arrange
        var filePath = Path.Combine(_testTempDir, "valid.build");
        var json = "{\"name\": \"Test Build\", \"author\": \"Tester\"}";
        File.WriteAllText(filePath, json);

        // Act
        var result = await _parser.ParseBuildFileAsync(filePath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Build", result.Name);
        Assert.Equal("Tester", result.Author);
    }

    [Fact]
    public async Task ParseBuildFile_InvalidJson_ReturnsNull()
    {
        // Arrange
        var filePath = Path.Combine(_testTempDir, "invalid.build");
        var json = "{ \"name\": "; // Malformed JSON
        File.WriteAllText(filePath, json);

        // Act
        var result = await _parser.ParseBuildFileAsync(filePath);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ParseBuildFile_EmptyFile_ReturnsNull()
    {
        // Arrange
        var filePath = Path.Combine(_testTempDir, "empty.build");
        File.WriteAllText(filePath, "");

        // Act
        var result = await _parser.ParseBuildFileAsync(filePath);

        // Assert
        Assert.Null(result);
    }
}
