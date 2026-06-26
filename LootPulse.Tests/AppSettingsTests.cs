using System.Text.Json;
using LootPulse.Models;
using Xunit;

namespace LootPulse.Tests;

public class AppSettingsTests
{
    [Fact]
    public void AppSettings_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var settings = new AppSettings();

        // Assert
        Assert.Equal(string.Empty, settings.LogPath);
        Assert.Equal(string.Empty, settings.FilterOutputPath);
        Assert.Equal(string.Empty, settings.SelectedBaseFilterPath);
        Assert.Equal(string.Empty, settings.BuildFilePath);
        Assert.Equal(1.0, settings.Tier1Threshold);
        Assert.Equal(1.0, settings.Tier2Threshold);
        Assert.Equal(250, settings.HudWidth);
        Assert.Equal(120, settings.HudHeight);
        Assert.Equal(0.80, settings.HudXPercent);
        Assert.Equal(0.05, settings.HudYPercent);
        Assert.Equal(0.85, settings.EditModeOpacity);
        Assert.Equal(0.30, settings.HudModeOpacity);
        Assert.True(settings.IsHudVisible);
        Assert.True(settings.ShowEconomyHighlights);
        Assert.Equal("Runes of Aldur", settings.League);
    }

    [Fact]
    public void AppSettings_ShouldSerializeAndDeserializeCorrectly()
    {
        // Arrange
        var settings = new AppSettings
        {
            LogPath = "C:\\path\\to\\log",
            FilterOutputPath = "C:\\path\\to\\filter",
            SelectedBaseFilterPath = "C:\\path\\to\\base",
            BuildFilePath = "C:\\path\\to\\build",
            Tier1Threshold = 2.5,
            Tier2Threshold = 1.5,
            HudWidth = 300,
            HudHeight = 150,
            HudXPercent = 0.5,
            HudYPercent = 0.5,
            EditModeOpacity = 0.9,
            HudModeOpacity = 0.4,
            IsHudVisible = false,
            ShowEconomyHighlights = false,
            League = "Standard"
        };

        // Act
        var json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(settings.LogPath, deserialized.LogPath);
        Assert.Equal(settings.FilterOutputPath, deserialized.FilterOutputPath);
        Assert.Equal(settings.SelectedBaseFilterPath, deserialized.SelectedBaseFilterPath);
        Assert.Equal(settings.BuildFilePath, deserialized.BuildFilePath);
        Assert.Equal(settings.Tier1Threshold, deserialized.Tier1Threshold);
        Assert.Equal(settings.Tier2Threshold, deserialized.Tier2Threshold);
        Assert.Equal(settings.HudWidth, deserialized.HudWidth);
        Assert.Equal(settings.HudHeight, deserialized.HudHeight);
        Assert.Equal(settings.HudXPercent, deserialized.HudXPercent);
        Assert.Equal(settings.HudYPercent, deserialized.HudYPercent);
        Assert.Equal(settings.EditModeOpacity, deserialized.EditModeOpacity);
        Assert.Equal(settings.HudModeOpacity, deserialized.HudModeOpacity);
        Assert.Equal(settings.IsHudVisible, deserialized.IsHudVisible);
        Assert.Equal(settings.ShowEconomyHighlights, deserialized.ShowEconomyHighlights);
        Assert.Equal(settings.League, deserialized.League);
    }

    [Fact]
    public void AppSettings_DeserializePartialJson_ShouldUseDefaultValues()
    {
        // Arrange
        var json = "{\"League\": \"New League\"}";

        // Act
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("New League", deserialized.League);
        Assert.Equal(string.Empty, deserialized.LogPath); // Default
        Assert.Equal(1.0, deserialized.Tier1Threshold); // Default
        Assert.True(deserialized.IsHudVisible); // Default
    }
}
