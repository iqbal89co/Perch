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
        var all = new[]
        {
            new ProcessUsage("chrome", 0.12, 100),
            new ProcessUsage("code", 0.30, 200),
            new ProcessUsage("explorer", 0.02, 300),
            new ProcessUsage("zed", 0.12, 400),
            new ProcessUsage("svchost", 0.05, 500),
        };
        var top = SystemMath.TopProcesses(all, 4);
        Assert.Equal(4, top.Count);
        Assert.Equal("code", top[0].Name);
        Assert.Equal("chrome", top[1].Name); // ties go by name
        Assert.Equal("zed", top[2].Name);
        Assert.Equal("svchost", top[3].Name);
        Assert.Equal(200, top[0].MemoryBytes); // memory travels with the row
    }

    [Fact]
    public void Top_processes_with_fewer_entries_than_the_limit_return_them_all()
    {
        var top = SystemMath.TopProcesses(new[] { new ProcessUsage("only", 0.5, 1) }, 4);
        Assert.Single(top);
        Assert.Empty(SystemMath.TopProcesses(Array.Empty<ProcessUsage>(), 4));
    }
}

public class GpuMathTests
{
    [Fact]
    public void Engines_of_one_adapter_are_summed()
    {
        var engines = new[]
        {
            ("pid_100_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 20.0),
            ("pid_200_luid_0x0_0x1_phys_0_eng_1_engtype_3D", 15.5),
        };
        Assert.Equal(35.5, SystemMath.GpuUtilization(engines), 6);
    }

    [Fact]
    public void The_busiest_adapter_wins_not_the_sum_across_adapters()
    {
        var engines = new[]
        {
            ("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 30.0),
            ("pid_2_luid_0x0_0x1_phys_0_eng_1_engtype_3D", 20.0), // adapter 0: 50
            ("pid_3_luid_0x0_0x2_phys_1_eng_0_engtype_3D", 60.0), // adapter 1: 60
        };
        Assert.Equal(60.0, SystemMath.GpuUtilization(engines), 6);
    }

    [Fact]
    public void Utilisation_is_clamped_to_100()
    {
        var engines = new[]
        {
            ("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 60.0),
            ("pid_2_luid_0x0_0x1_phys_0_eng_1_engtype_3D", 70.0), // adapter 0 sums to 130
        };
        Assert.Equal(100, SystemMath.GpuUtilization(engines), 6);
    }

    [Fact]
    public void Adapter_key_drops_the_process_and_engine_parts()
    {
        Assert.Equal("luid_0x0_0x1_phys_0",
            SystemMath.AdapterKey("pid_4312_luid_0x0_0x1_phys_0_eng_3_engtype_3D"));
        Assert.Equal("luid_0x0_0x1_phys_0", SystemMath.AdapterKey("luid_0x0_0x1_phys_0"));
    }

    [Fact]
    public void No_instances_reads_zero()
    {
        Assert.Equal(0, SystemMath.GpuUtilization(Array.Empty<(string, double)>()));
        Assert.Equal(0, SystemMath.LargestAdapterTotal(Array.Empty<(string, double)>()));
    }

    [Fact]
    public void Dedicated_memory_is_the_largest_adapter_total()
    {
        var memory = new[]
        {
            ("luid_0x0_0x1_phys_0", 1_288_490_188.0),
            ("luid_0x0_0x2_phys_0", 512.0 * 1024 * 1024),
        };
        Assert.Equal(1_288_490_188.0, SystemMath.LargestAdapterTotal(memory), 6);
    }

    [Theory]
    [InlineData(10, 2)]
    [InlineData(30, 2)]
    [InlineData(31, 5)]
    [InlineData(120, 5)]
    public void Slow_gpu_samples_back_off_to_five_seconds(double sampleMs, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SystemMath.GpuSampleInterval(sampleMs));

    [Fact]
    public void Vram_text_uses_one_decimal_or_nothing()
    {
        Assert.Equal("1.2 GB VRAM", SystemMath.VramText((long)(1.2 * 1024 * 1024 * 1024)));
        Assert.Equal("", SystemMath.VramText(null));
    }
}

public class DeviceTextTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(3400, "3.4 GHz")]
    [InlineData(2995.2, "3.0 GHz")]
    [InlineData(0, "0.0 GHz")]
    public void Clock_reads_in_gigahertz(double mhz, string expected) =>
        Assert.Equal(expected, SystemMath.FormatGHz(mhz));

    [Fact]
    public void Current_clock_scales_the_base_by_processor_performance()
    {
        Assert.Equal(3300, SystemMath.CurrentMhz(3000, 110), 6);
        Assert.Equal(1500, SystemMath.CurrentMhz(3000, 50), 6);
    }

    [Fact]
    public void Without_a_performance_counter_the_clock_is_the_base()
    {
        Assert.Equal(2900, SystemMath.CurrentMhz(2900, null), 6);
        Assert.Equal(2900, SystemMath.CurrentMhz(2900, 0), 6);
    }

    [Fact]
    public void Cpu_detail_shows_clock_and_threads_or_threads_alone()
    {
        Assert.Equal("3.4 GHz · 12 threads", SystemMath.CpuDetail(3400, 12));
        Assert.Equal("12 threads", SystemMath.CpuDetail(0, 12));
    }

    [Theory]
    [InlineData(2, 10, "2 h 10 m left")]
    [InlineData(0, 45, "45 m left")]
    [InlineData(0, 0, "<1 m left")]
    public void Battery_time_left_in_hours_and_minutes(int hours, int minutes, string expected) =>
        Assert.Equal(expected, SystemMath.FormatBatteryLeft(new TimeSpan(hours, minutes, 0)));

    [Theory]
    [InlineData(true, false, null, "Charging")]
    [InlineData(true, true, null, "Charging")]
    [InlineData(false, true, null, "Plugged in")]
    [InlineData(false, false, "2h10", "On battery · 2 h 10 m left")]
    [InlineData(false, false, null, "On battery")]
    public void Power_status_text(bool charging, bool pluggedIn, string? time, string expected) =>
        Assert.Equal(expected, SystemMath.PowerStatusText(charging, pluggedIn, time is null ? null : new TimeSpan(2, 10, 0)));

    [Fact]
    public void Memory_detail_reads_used_total_and_free()
    {
        var text = SystemMath.MemoryDetail((long)(16.1 * GiB), 20 * GiB, (long)(3.9 * GiB));
        Assert.Equal("16.1 / 20 GB · 3.9 GB free", text);
    }

    [Fact]
    public void Disk_activity_reads_read_and_write_rates_or_dashes()
    {
        Assert.Equal("R 12 MB/s · W 3 MB/s", SystemMath.DiskActivity(12 * 1024 * 1024, 3 * 1024 * 1024));
        Assert.Equal("R — · W —", SystemMath.DiskActivity(null, null));
    }

    [Fact]
    public void Network_totals_read_received_and_sent()
    {
        Assert.Equal("Total ↓ 1.2 GB · ↑ 300 MB",
            SystemMath.NetworkTotals((long)(1.2 * GiB), 300L * 1024 * 1024));
    }

    [Theory]
    [InlineData(26200, "Windows 11 26200")]
    [InlineData(22000, "Windows 11 22000")]
    [InlineData(19045, "Windows 10 19045")]
    [InlineData(0, "Windows")]
    public void Windows_label_from_the_build_number(int build, string expected) =>
        Assert.Equal(expected, SystemMath.WindowsLabel(build));

    [Fact]
    public void Cpu_name_is_trimmed_and_collapsed()
    {
        Assert.Equal("Intel(R) Core(TM) i7-13700H", SystemMath.CleanCpuName("  Intel(R)   Core(TM)  i7-13700H  "));
        Assert.Equal("", SystemMath.CleanCpuName(null));
    }

    [Fact]
    public void Footer_joins_the_known_parts()
    {
        Assert.Equal("Intel CPU · Windows 11 26200 · Up 2 d 18 h",
            SystemMath.Footer("Intel CPU", "Windows 11 26200", new TimeSpan(2, 18, 0, 0)));
        Assert.Equal("Windows 11 26200 · Up 7 m", SystemMath.Footer("", "Windows 11 26200", TimeSpan.FromMinutes(7)));
    }

    [Fact]
    public void Busiest_interface_is_the_one_with_most_traffic_and_an_address()
    {
        var picked = SystemMath.BusiestInterface(new[]
        {
            new InterfaceLoad("Ethernet", "10.0.0.5", 900),
            new InterfaceLoad("Wi-Fi", "192.168.1.20", 5_000),
            new InterfaceLoad("vEthernet", null, 99_999), // no IPv4: not a candidate
        });
        Assert.NotNull(picked);
        Assert.Equal("Wi-Fi", picked!.Name);
        Assert.Equal("192.168.1.20", picked.Ipv4);
    }

    [Fact]
    public void Busiest_interface_ties_go_by_name_and_none_gives_null()
    {
        var picked = SystemMath.BusiestInterface(new[]
        {
            new InterfaceLoad("Wi-Fi", "192.168.1.20", 0),
            new InterfaceLoad("Ethernet", "10.0.0.5", 0),
        });
        Assert.Equal("Ethernet", picked!.Name);
        Assert.Null(SystemMath.BusiestInterface(new[] { new InterfaceLoad("Loopback", null, 5) }));
        Assert.Null(SystemMath.BusiestInterface(Array.Empty<InterfaceLoad>()));
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

}
