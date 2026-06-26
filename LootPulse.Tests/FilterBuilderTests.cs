using LootPulse.Services;
using Xunit;

namespace LootPulse.Tests
{
    public class FilterBuilderTests
    {
        [Theory]
        [InlineData(null, "255 255 255 255")]
        [InlineData("", "255 255 255 255")]
        [InlineData("   ", "255 255 255 255")]
        public void ParseHexToRgbaString_NullOrEmpty_ReturnsDefault(string input, string expected)
        {
            var result = FilterBuilder.ParseHexToRgbaString(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("#FFF", "255 255 255 255")]
        [InlineData("000", "0 0 0 255")]
        [InlineData("#123", "17 34 51 255")]
        public void ParseHexToRgbaString_3DigitHex_ReturnsCorrectRgba(string input, string expected)
        {
            var result = FilterBuilder.ParseHexToRgbaString(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("#F00F", "0 0 255 255")] // ARGB: A=F, R=0, G=0, B=F -> 255 0 0 255? No, AARRGGBB.
                                              // If #F00F is ARGB shorthand: A=F, R=0, G=0, B=F.
                                              // AARRGGBB: FF 00 00 FF.
                                              // RGB A: 0 0 255 255.
        [InlineData("FABC", "170 187 204 255")] // ARGB: A=F, R=A, G=B, B=C -> FF AA BB CC
                                                // R:170 G:187 B:204 A:255
        public void ParseHexToRgbaString_4DigitHex_ReturnsCorrectRgba(string input, string expected)
        {
            var result = FilterBuilder.ParseHexToRgbaString(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("#FF0000", "255 0 0 255")]
        [InlineData("00FF00", "0 255 0 255")]
        [InlineData("#0000FF", "0 0 255 255")]
        [InlineData("123456", "18 52 86 255")]
        public void ParseHexToRgbaString_6DigitHex_ReturnsCorrectRgba(string input, string expected)
        {
            var result = FilterBuilder.ParseHexToRgbaString(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("#FFFF0000", "255 0 0 255")] // AARRGGBB -> A:FF R:FF G:00 B:00
        [InlineData("8000FF00", "0 255 0 128")] // AARRGGBB -> A:80 R:00 G:FF B:00
        [InlineData("#000000FF", "0 0 255 0")]   // AARRGGBB -> A:00 R:00 G:00 B:FF
        public void ParseHexToRgbaString_8DigitHex_ReturnsCorrectRgba(string input, string expected)
        {
            var result = FilterBuilder.ParseHexToRgbaString(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("invalid")]
        [InlineData("#12")]
        [InlineData("12345")]
        [InlineData("1234567")]
        [InlineData("GHIJKL")]
        public void ParseHexToRgbaString_InvalidInput_ReturnsEmpty(string input)
        {
            var result = FilterBuilder.ParseHexToRgbaString(input);
            Assert.Equal("", result);
        }

        [Fact]
        public void ParseHexToRgbaString_MixedCasing_Works()
        {
            var result = FilterBuilder.ParseHexToRgbaString("#ffAa00");
            Assert.Equal("255 170 0 255", result);
        }
    }
}
