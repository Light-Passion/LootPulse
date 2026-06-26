using System.Text.Json;
using LootPulse.Models;
using Xunit;

namespace LootPulse.Tests;

public class AppSettingsTests
{
    [Fact]
    public void AppSettings_Defaults_AreCorrect()
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
    public void AppSettings_RoundTrip_Serialization_Works()
    {
        // Arrange
        var settings = new AppSettings
        {
            LogPath = "C:\\Path\\To\\Log.txt",
            FilterOutputPath = "C:\\Path\\To\\Filter.filter",
            SelectedBaseFilterPath = "Base.filter",
            BuildFilePath = "Build.build",
            Tier1Threshold = 5.5,
            Tier2Threshold = 2.0,
            HudWidth = 300,
            HudHeight = 150,
            HudXPercent = 0.5,
            HudYPercent = 0.1,
            EditModeOpacity = 0.9,
            HudModeOpacity = 0.5,
            IsHudVisible = false,
            ShowEconomyHighlights = false,
            League = "Standard"
        };

        var options = new JsonSerializerOptions { WriteIndented = true };

        // Act
        string json = JsonSerializer.Serialize(settings, options);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json, options);

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
    public void AppSettings_Deserialize_PartialJson_UsesDefaultsForMissingProperties()
    {
        // Arrange
        var json = "{\"LogPath\": \"custom/path\"}";

        // Act
        var settings = JsonSerializer.Deserialize<AppSettings>(json);

        // Assert
        Assert.NotNull(settings);
        Assert.Equal("custom/path", settings.LogPath);
        // Should have default values for others
        Assert.Equal(1.0, settings.Tier1Threshold);
        Assert.True(settings.IsHudVisible);
        Assert.Equal("Runes of Aldur", settings.League);
    }
}
