using Perch.Services.Hud;

namespace Perch.Tests;

public class VolumeHudTests
{
    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.9, true)]
    [InlineData(0.5, true)]
    public void Muted_always_shows_the_mute_glyph(double level, bool muted) =>
        Assert.Equal(VolumeHud.GlyphMute, VolumeHud.VolumeGlyph(level, muted));

    [Fact]
    public void Silent_level_shows_the_mute_glyph()
    {
        Assert.Equal(VolumeHud.GlyphMute, VolumeHud.VolumeGlyph(0, false));
        Assert.Equal(VolumeHud.GlyphMute, VolumeHud.VolumeGlyph(double.NaN, false));
    }

    [Theory]
    [InlineData(0.01, "low")]
    [InlineData(0.339, "low")]
    [InlineData(0.34, "medium")]
    [InlineData(0.669, "medium")]
    [InlineData(0.67, "high")]
    [InlineData(1.0, "high")]
    public void Level_thresholds_pick_the_volume_glyph(double level, string band)
    {
        var expected = band switch
        {
            "low" => VolumeHud.GlyphLow,
            "medium" => VolumeHud.GlyphMedium,
            _ => VolumeHud.GlyphHigh,
        };
        Assert.Equal(expected, VolumeHud.VolumeGlyph(level, false));
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.125, 13)]
    [InlineData(0.42, 42)]
    [InlineData(1.0, 100)]
    [InlineData(1.7, 100)]
    [InlineData(-0.2, 0)]
    [InlineData(double.NaN, 0)]
    public void Percent_is_rounded_and_clamped(double level, int expected) =>
        Assert.Equal(expected, VolumeHud.Percent(level));
}

public class PowerNoticeLogicTests
{
    [Fact]
    public void First_observation_only_sets_the_baseline()
    {
        var logic = new PowerNoticeLogic();
        Assert.Null(logic.Observe(new PowerSample(false, 15)));
        Assert.Null(logic.Observe(new PowerSample(false, 15)));
    }

    [Fact]
    public void Plugging_in_and_unplugging_produce_notices()
    {
        var logic = new PowerNoticeLogic();
        logic.Observe(new PowerSample(false, 64));

        var plugged = logic.Observe(new PowerSample(true, 64));
        Assert.NotNull(plugged);
        Assert.Equal(PowerNoticeLogic.GlyphCharging, plugged!.Glyph);
        Assert.Equal("Charging · 64%", plugged.Text);

        var unplugged = logic.Observe(new PowerSample(false, 63));
        Assert.NotNull(unplugged);
        Assert.Equal("On battery · 63%", unplugged!.Text);

        Assert.Null(logic.Observe(new PowerSample(false, 62)));
    }

    [Fact]
    public void Low_battery_fires_once_at_20_and_once_at_10()
    {
        var logic = new PowerNoticeLogic();
        logic.Observe(new PowerSample(false, 25));

        Assert.Equal("Battery low · 20%", logic.Observe(new PowerSample(false, 20))!.Text);
        Assert.Null(logic.Observe(new PowerSample(false, 19)));
        Assert.Null(logic.Observe(new PowerSample(false, 18)));

        Assert.Equal("Battery low · 10%", logic.Observe(new PowerSample(false, 10))!.Text);
        Assert.Null(logic.Observe(new PowerSample(false, 9)));
        Assert.Null(logic.Observe(new PowerSample(false, 8)));
    }

    [Fact]
    public void Low_battery_does_not_repeat_until_charging_again()
    {
        var logic = new PowerNoticeLogic();
        logic.Observe(new PowerSample(false, 25));
        Assert.NotNull(logic.Observe(new PowerSample(false, 20)));

        // Still low, plugged in later, unplugged at the same level: a new discharge cycle begins.
        Assert.NotNull(logic.Observe(new PowerSample(true, 19)));
        Assert.Null(logic.Observe(new PowerSample(true, 19)));
        Assert.Equal("Battery low · 19%", logic.Observe(new PowerSample(false, 19))!.Text);
        Assert.Null(logic.Observe(new PowerSample(false, 18)));
    }

    [Fact]
    public void Starting_below_a_threshold_does_not_announce_it_later()
    {
        var logic = new PowerNoticeLogic();
        logic.Observe(new PowerSample(false, 15)); // baseline: already past 20%
        Assert.Null(logic.Observe(new PowerSample(false, 14)));
        Assert.Equal("Battery low · 9%", logic.Observe(new PowerSample(false, 9))!.Text);
    }

    [Fact]
    public void Dropping_past_both_thresholds_at_once_gives_one_notice()
    {
        var logic = new PowerNoticeLogic();
        logic.Observe(new PowerSample(false, 40));
        Assert.Equal("Battery low · 8%", logic.Observe(new PowerSample(false, 8))!.Text);
        Assert.Null(logic.Observe(new PowerSample(false, 7)));
    }

    [Fact]
    public void Reaching_full_while_charging_announces_once()
    {
        var logic = new PowerNoticeLogic();
        logic.Observe(new PowerSample(true, 99));

        var full = logic.Observe(new PowerSample(true, 100));
        Assert.NotNull(full);
        Assert.Equal("Fully charged", full!.Text);
        Assert.Null(logic.Observe(new PowerSample(true, 100)));
    }

    [Fact]
    public void Being_full_when_plugged_in_is_not_announced_as_full()
    {
        var logic = new PowerNoticeLogic();
        logic.Observe(new PowerSample(false, 100));
        var plugged = logic.Observe(new PowerSample(true, 100));
        Assert.Equal("Charging · 100%", plugged!.Text);
        Assert.Null(logic.Observe(new PowerSample(true, 100)));
    }
}
