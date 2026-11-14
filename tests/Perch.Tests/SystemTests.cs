using Perch.Pages.SystemMonitor;

namespace Perch.Tests;

public class RingBufferTests
{
    [Fact]
    public void Keeps_insertion_order_until_full()
    {
        var ring = new RingBuffer<int>(3);
        ring.Add(1);
        ring.Add(2);
        Assert.Equal(2, ring.Count);
        Assert.Equal(new[] { 1, 2 }, ring.ToArray());
    }

    [Fact]
    public void Wraps_and_drops_the_oldest()
    {
        var ring = new RingBuffer<int>(3);
        for (var i = 1; i <= 5; i++) ring.Add(i);
        Assert.Equal(3, ring.Count);
        Assert.Equal(3, ring.Capacity);
        Assert.Equal(new[] { 3, 4, 5 }, ring.ToArray());
        Assert.Equal(3, ring[0]);
        Assert.Equal(5, ring[2]);
    }

    [Fact]
    public void Indexer_rejects_positions_outside_the_filled_range()
    {
        var ring = new RingBuffer<int>(4);
        ring.Add(7);
        Assert.Throws<ArgumentOutOfRangeException>(() => ring[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ring[-1]);
    }

    [Fact]
    public void Clear_empties_it_and_it_can_be_reused()
    {
        var ring = new RingBuffer<string>(2);
        ring.Add("a");
        ring.Add("b");
        ring.Clear();
        Assert.Equal(0, ring.Count);
        Assert.Empty(ring);
        ring.Add("c");
        Assert.Equal(new[] { "c" }, ring);
    }

    [Fact]
    public void Zero_capacity_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<double>(0));
    }
}

public class CpuUsageTests
{
    [Fact]
    public void Half_busy_reads_one_half()
    {
        // kernel grew by 100 (of which 50 idle), no user time: busy = 1 - 50 / 100
        var usage = SystemMath.CpuUsage(0, 0, 0, idle: 50, kernel: 100, user: 0);
        Assert.Equal(0.5, usage, 6);
    }

    [Fact]
    public void Busy_fraction_uses_kernel_plus_user_with_kernel_including_idle()
    {
        // kernel grew by 100 (of which 60 idle), user by 40: total 140, idle 60 -> busy = 80/140
        var usage = SystemMath.CpuUsage(prevIdle: 0, prevKernel: 0, prevUser: 0, idle: 60, kernel: 100, user: 40);
        Assert.Equal(80.0 / 140.0, usage, 6);
    }

    [Fact]
    public void Zero_delta_reads_zero()
    {
        var usage = SystemMath.CpuUsage(10, 20, 30, 10, 20, 30);
        Assert.Equal(0, usage);
    }

    [Fact]
    public void Fully_idle_is_zero_and_fully_busy_is_one()
    {
        Assert.Equal(0, SystemMath.CpuUsage(0, 0, 0, idle: 100, kernel: 100, user: 0), 6);
        Assert.Equal(1, SystemMath.CpuUsage(0, 0, 0, idle: 0, kernel: 100, user: 0), 6);
    }

    [Fact]
    public void Idle_growing_faster_than_the_total_clamps_to_zero()
    {
        Assert.Equal(0, SystemMath.CpuUsage(0, 0, 0, idle: 150, kernel: 100, user: 0));
    }

    [Fact]
    public void Counters_that_went_backwards_count_as_no_movement()
    {
        Assert.Equal(0, SystemMath.CpuUsage(500, 500, 500, idle: 10, kernel: 20, user: 30));
    }
}

public class ProcessPercentTests
{
    [Fact]
    public void One_core_second_over_one_second_on_four_cores_is_a_quarter()
    {
        var pct = SystemMath.ProcessPercent(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(1), cores: 4);
        Assert.Equal(0.25, pct, 6);
    }

    [Fact]
    public void Zero_elapsed_or_no_cores_reads_zero()
    {
        Assert.Equal(0, SystemMath.ProcessPercent(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.Zero, 4));
        Assert.Equal(0, SystemMath.ProcessPercent(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0));
    }

    [Fact]
    public void Process_time_that_went_backwards_reads_zero_and_big_values_clamp()
    {
        Assert.Equal(0, SystemMath.ProcessPercent(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1), 4));
        Assert.Equal(1, SystemMath.ProcessPercent(TimeSpan.Zero, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(1), 4));
    }

    [Fact]
    public void Top_processes_are_sorted_descending_and_limited()
    {
        var byName = new Dictionary<string, double>
        {
            ["chrome"] = 0.12,
            ["code"] = 0.30,
            ["explorer"] = 0.02,
            ["zed"] = 0.12,
            ["svchost"] = 0.05,
        };
        var top = SystemMath.TopProcesses(byName, 4);
        Assert.Equal(4, top.Count);
        Assert.Equal("code", top[0].Name);
        Assert.Equal("chrome", top[1].Name); // ties go by name
        Assert.Equal("zed", top[2].Name);
        Assert.Equal("svchost", top[3].Name);
    }
}

public class RateCalculatorTests
{
    [Fact]
    public void Delta_over_seconds_is_bytes_per_second()
    {
        Assert.Equal(500, RateCalculator.BytesPerSecond(prevTotal: 1000, total: 2000, seconds: 2));
    }

    [Fact]
    public void Counter_reset_reads_zero()
    {
        Assert.Equal(0, RateCalculator.BytesPerSecond(prevTotal: 5_000_000, total: 1_000, seconds: 1));
    }

    [Fact]
    public void Zero_or_bad_elapsed_reads_zero()
    {
        Assert.Equal(0, RateCalculator.BytesPerSecond(0, 1000, 0));
        Assert.Equal(0, RateCalculator.BytesPerSecond(0, 1000, -1));
        Assert.Equal(0, RateCalculator.BytesPerSecond(0, 1000, double.NaN));
    }
}

public class SystemFormatTests
{
    [Theory]
    [InlineData(3, 4, 0, "Up 3 d 4 h")]
    [InlineData(0, 5, 12, "Up 5 h 12 m")]
    [InlineData(0, 0, 7, "Up 7 m")]
    [InlineData(0, 0, 0, "Up 0 m")]
    public void Uptime_reads_in_the_largest_sensible_units(int days, int hours, int minutes, string expected) =>
        Assert.Equal(expected, SystemMath.FormatUptime(new TimeSpan(days, hours, minutes, 0)));

    [Fact]
    public void Negative_uptime_clamps_to_zero()
    {
        Assert.Equal("Up 0 m", SystemMath.FormatUptime(TimeSpan.FromMinutes(-5)));
    }

    [Theory]
    [InlineData(9.4, "9.4")]
    [InlineData(16, "16")]
    [InlineData(212, "212")]
    [InlineData(0.5, "0.5")]
    public void Gibibytes_keep_one_decimal_only_when_small(double gib, string expected) =>
        Assert.Equal(expected, SystemMath.FormatGiB((long)(gib * 1024 * 1024 * 1024)));

    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(0.125, "13%")]
    [InlineData(0.5, "50%")]
    [InlineData(1.7, "100%")]
    public void Percent_rounds_and_clamps(double fraction, string expected) =>
        Assert.Equal(expected, SystemMath.Percent(fraction));

    [Theory]
    [InlineData(true, false, "Charging")]
    [InlineData(true, true, "Charging")]
    [InlineData(false, true, "Plugged in")]
    [InlineData(false, false, "On battery")]
    public void Battery_status_text(bool charging, bool pluggedIn, string expected) =>
        Assert.Equal(expected, SystemMath.BatteryStatusText(charging, pluggedIn));
}
