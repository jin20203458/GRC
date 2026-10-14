using System.Globalization;
using GRC.Models;
using Xunit;

namespace GRC.Tests;

public class CustomStatUITests
{
    [Fact]
    public void Create_ValidFraction_SetsGaugePropertiesCorrectly()
    {
        var stat = CustomStatUI.Create("HP", "75/100");

        Assert.Equal("HP", stat.Name);
        Assert.Equal("75/100", stat.Value);
        Assert.True(stat.IsGauge);
        Assert.Equal(75.0, stat.CurrentValue);
        Assert.Equal(100.0, stat.MaxValue);
    }

    [Fact]
    public void Create_PlainText_SetsNonGauge()
    {
        var stat = CustomStatUI.Create("상태", "양호");

        Assert.Equal("상태", stat.Name);
        Assert.Equal("양호", stat.Value);
        Assert.False(stat.IsGauge);
        Assert.Equal(0.0, stat.CurrentValue);
        Assert.Equal(0.0, stat.MaxValue);
    }

    [Theory]
    [InlineData("10/20/30")]
    [InlineData("abc/def")]
    [InlineData("10/")]
    [InlineData("/20")]
    public void Create_InvalidFractionFormat_SetsNonGauge(string invalidValue)
    {
        var stat = CustomStatUI.Create("Stat", invalidValue);

        Assert.Equal("Stat", stat.Name);
        Assert.Equal(invalidValue, stat.Value);
        Assert.False(stat.IsGauge);
    }

    [Theory]
    [InlineData("50/0")]
    [InlineData("50/-10")]
    public void Create_ZeroOrNegativeDenominator_SetsNonGauge(string invalidDenominator)
    {
        var stat = CustomStatUI.Create("Gauge", invalidDenominator);

        Assert.False(stat.IsGauge);
    }

    [Fact]
    public void Create_FloatingPointValuesWithInvariant_SetsGauge()
    {
        var stat = CustomStatUI.Create("Exp", "12.5/25.0");

        Assert.True(stat.IsGauge);
        Assert.Equal(12.5, stat.CurrentValue);
        Assert.Equal(25.0, stat.MaxValue);
    }

    [Fact]
    public void Create_NullOrEmptyValue_GracefulHandling()
    {
        var statNull = CustomStatUI.Create(null, null);
        Assert.Equal("", statNull.Name);
        Assert.Equal("", statNull.Value);
        Assert.False(statNull.IsGauge);

        var statEmpty = CustomStatUI.Create("Stat", "");
        Assert.Equal("Stat", statEmpty.Name);
        Assert.Equal("", statEmpty.Value);
        Assert.False(statEmpty.IsGauge);
    }
}
