using Xunit;
using YuNotes.Models;

namespace YuNotes.Tests;

public class ToolbarMetricsTests
{
    [Theory]
    [InlineData(ToolbarSize.Small, 83)]
    [InlineData(ToolbarSize.Normal, 100)]
    [InlineData(ToolbarSize.Large, 128)]
    public void OldThreeStepSetting_CarriesOverAsAPercentage(ToolbarSize size, int percent) =>
        Assert.Equal(percent, ToolbarMetrics.PercentOf(ToolbarMetrics.ScaleOf(new AppSettings { ToolbarSize = size })));

    [Fact]
    public void SliderValue_WinsOverTheOldSetting()
    {
        var s = new AppSettings { ToolbarSize = ToolbarSize.Large, ToolbarScale = ToolbarMetrics.FromPercent(70) };
        Assert.Equal(70, ToolbarMetrics.PercentOf(ToolbarMetrics.ScaleOf(s)));
    }

    [Fact]
    public void Scale_IsClampedToTheSliderRange()
    {
        Assert.Equal(ToolbarMetrics.MaxPercent, ToolbarMetrics.PercentOf(ToolbarMetrics.ScaleOf(new AppSettings { ToolbarScale = 9 })));
        Assert.Equal(ToolbarMetrics.MinPercent, ToolbarMetrics.PercentOf(ToolbarMetrics.ScaleOf(new AppSettings { ToolbarScale = 0.01 })));
    }

    [Fact]
    public void Sizes_MatchTheOldNormalAtOneHundredPercent()
    {
        // Wide window, old Normal: 44 × 0.78 ≈ 34 px buttons, 24 × 0.78 ≈ 19 px icons.
        var m = ToolbarMetrics.For(1400, ToolbarMetrics.FromPercent(100));
        Assert.Equal(34, m.Button);
        Assert.Equal(19, m.Icon);
    }

    [Fact]
    public void Sizes_GrowWithTheSliderAndShrinkWithTheWindow()
    {
        var small = ToolbarMetrics.For(1400, ToolbarMetrics.FromPercent(60));
        var big = ToolbarMetrics.For(1400, ToolbarMetrics.FromPercent(160));
        var narrow = ToolbarMetrics.For(700, ToolbarMetrics.FromPercent(160));
        Assert.True(small.Button < big.Button);
        Assert.True(narrow.Button < big.Button);
    }
}
