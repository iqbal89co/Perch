using Perch.Pages.Media;

namespace Perch.Tests;

public class LrcParserTests
{
    [Fact]
    public void Parses_tenths_hundredths_and_milliseconds()
    {
        var lines = LrcParser.Parse("[00:01.5]tenths\n[00:02.25]hundredths\n[00:03.125]millis");
        Assert.Equal(3, lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), lines[0].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(2250), lines[1].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(3125), lines[2].Time);
        Assert.Equal("millis", lines[2].Text);
    }

    [Fact]
    public void Parses_minutes_without_fraction()
    {
        var lines = LrcParser.Parse("[01:05]one minute five");
        Assert.Single(lines);
        Assert.Equal(TimeSpan.FromSeconds(65), lines[0].Time);
    }

    [Fact]
    public void One_line_with_several_timestamps_yields_one_entry_per_timestamp()
    {
        var lines = LrcParser.Parse("[00:10.00][01:20.00]chorus");
        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(80), lines[1].Time);
        Assert.All(lines, l => Assert.Equal("chorus", l.Text));
    }

    [Fact]
    public void Positive_offset_makes_lyrics_earlier()
    {
        var lines = LrcParser.Parse("[offset:+500]\n[00:10.00]line");
        Assert.Equal(TimeSpan.FromMilliseconds(9500), lines[0].Time);
    }

    [Fact]
    public void Negative_offset_makes_lyrics_later()
    {
        var lines = LrcParser.Parse("[00:10.00]line\n[offset:-250]");
        Assert.Equal(TimeSpan.FromMilliseconds(10250), lines[0].Time);
    }

    [Fact]
    public void Offset_past_the_start_clamps_to_zero()
    {
        var lines = LrcParser.Parse("[offset:+5000]\n[00:01.00]early");
        Assert.Equal(TimeSpan.Zero, lines[0].Time);
    }

    [Fact]
    public void Metadata_tags_are_ignored()
    {
        var lines = LrcParser.Parse("[ar:Some Artist]\n[ti:Some Title]\n[by:someone]\n[al:Album]\n[00:01.00]the line");
        Assert.Single(lines);
        Assert.Equal("the line", lines[0].Text);
    }

    [Fact]
    public void Unsorted_input_comes_out_sorted()
    {
        var lines = LrcParser.Parse("[00:30.00]third\n[00:05.00]first\n[00:12.00]second");
        Assert.Equal(new[] { "first", "second", "third" }, lines.Select(l => l.Text));
        Assert.True(lines.SequenceEqual(lines.OrderBy(l => l.Time)));
    }

    [Fact]
    public void Garbage_lines_are_skipped()
    {
        var lrc = "just some text\n[bad timestamp]no time\n[00:02.00\n[xx:yy]nope\n\n[00:04.00]kept";
        var lines = LrcParser.Parse(lrc);
        Assert.Single(lines);
        Assert.Equal("kept", lines[0].Text);
    }

    [Fact]
    public void Empty_text_lines_are_kept_as_breaks()
    {
        var lines = LrcParser.Parse("[00:05.00]\n[00:09.00]after the break");
        Assert.Equal(2, lines.Count);
        Assert.Equal("", lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(5), lines[0].Time);
    }

    [Fact]
    public void Bracketed_lyric_text_is_not_mistaken_for_a_tag()
    {
        var lines = LrcParser.Parse("[00:01.00][Chorus] la la");
        Assert.Single(lines);
        Assert.Equal("[Chorus] la la", lines[0].Text);
    }

    [Fact]
    public void Null_or_blank_input_gives_no_lines()
    {
        Assert.Empty(LrcParser.Parse(null));
        Assert.Empty(LrcParser.Parse("   \n  "));
    }

    [Fact]
    public void Handles_crlf_line_endings()
    {
        var lines = LrcParser.Parse("[00:01.00]one\r\n[00:02.00]two\r\n");
        Assert.Equal(new[] { "one", "two" }, lines.Select(l => l.Text));
    }
}

public class LrcIndexTests
{
    private static readonly IReadOnlyList<LyricLine> Lines = new List<LyricLine>
    {
        new(TimeSpan.FromSeconds(5), "a"),
        new(TimeSpan.FromSeconds(10), "b"),
        new(TimeSpan.FromSeconds(20), "c"),
    };

    [Fact]
    public void Before_the_first_line_is_minus_one()
    {
        Assert.Equal(-1, LrcParser.IndexAt(Lines, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Exactly_on_a_line_selects_it()
    {
        Assert.Equal(1, LrcParser.IndexAt(Lines, TimeSpan.FromSeconds(10)));
        Assert.Equal(0, LrcParser.IndexAt(Lines, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Between_lines_selects_the_earlier_one()
    {
        Assert.Equal(1, LrcParser.IndexAt(Lines, TimeSpan.FromSeconds(14)));
        Assert.Equal(1, LrcParser.IndexAt(Lines, TimeSpan.FromSeconds(19.9)));
    }

    [Fact]
    public void After_the_last_line_selects_the_last()
    {
        Assert.Equal(2, LrcParser.IndexAt(Lines, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Empty_list_is_minus_one()
    {
        Assert.Equal(-1, LrcParser.IndexAt(new List<LyricLine>(), TimeSpan.FromSeconds(3)));
    }
}

public class LyricsCacheKeyTests
{
    [Fact]
    public void Key_is_16_lowercase_hex_chars()
    {
        var key = LyricsCacheKey.For("Artist", "Title", "Album");
        Assert.Matches("^[0-9a-f]{16}$", key);
    }

    [Fact]
    public void Key_is_stable_across_calls()
    {
        Assert.Equal(LyricsCacheKey.For("Artist", "Title", "Album"), LyricsCacheKey.For("Artist", "Title", "Album"));
    }

    [Fact]
    public void Key_ignores_case_and_surrounding_spaces()
    {
        Assert.Equal(
            LyricsCacheKey.For("Artist", "Title", "Album"),
            LyricsCacheKey.For("  ARTIST ", "title", "album"));
    }

    [Fact]
    public void Different_tracks_get_different_keys()
    {
        Assert.NotEqual(
            LyricsCacheKey.For("Artist", "Title", "Album"),
            LyricsCacheKey.For("Artist", "Other Title", "Album"));
    }
}
