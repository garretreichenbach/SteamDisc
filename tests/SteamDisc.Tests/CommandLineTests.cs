using SteamDisc.Builder;

namespace SteamDisc.Tests;

public class CommandLineTests
{
    [Fact]
    public void Separates_verbs_and_positional_values_from_options()
    {
        var command = CommandLine.Parse(new[] { "package", "620", "--out", "/tmp/discs" });

        Assert.Equal(new[] { "package", "620" }, command.Positional);
        Assert.Equal("/tmp/discs", command.Value("out"));
    }

    [Fact]
    public void A_flag_followed_by_an_option_does_not_swallow_its_value()
    {
        // The failure this guards against is silent: "--out" would be recorded as a bare flag
        // and the output path would quietly fall back to the working directory.
        var command = CommandLine.Parse(new[] { "covers", "new", "--no-art", "--out", "/tmp/cover.json" });

        Assert.True(command.Has("no-art"));
        Assert.Equal("/tmp/cover.json", command.Value("out"));
    }

    [Fact]
    public void Consecutive_flags_all_register()
    {
        var command = CommandLine.Parse(new[] { "package", "--no-art", "--no-hashes", "--validate" });

        Assert.True(command.Has("no-art"));
        Assert.True(command.Has("no-hashes"));
        Assert.True(command.Has("validate"));
    }

    [Fact]
    public void Supports_equals_syntax()
    {
        var command = CommandLine.Parse(new[] { "package", "--media=bd-r-dl", "--compression=maximum" });

        Assert.Equal("bd-r-dl", command.Value("media"));
        Assert.Equal("maximum", command.Value("compression"));
    }

    [Fact]
    public void A_trailing_flag_is_still_recorded()
    {
        var command = CommandLine.Parse(new[] { "verify", "/tmp/disc", "--verbose" });

        Assert.True(command.Has("verbose"));
        Assert.Equal("/tmp/disc", command.PositionalAt(1));
    }

    [Fact]
    public void Option_names_are_case_insensitive()
    {
        var command = CommandLine.Parse(new[] { "--Out", "x" });

        Assert.Equal("x", command.Value("out"));
    }

    [Theory]
    [InlineData("2g", 2L * 1024 * 1024 * 1024)]
    [InlineData("512m", 512L * 1024 * 1024)]
    [InlineData("1.5g", (long)(1.5 * 1024 * 1024 * 1024))]
    [InlineData("4096", 4096L)]
    [InlineData("700mb", 700L * 1024 * 1024)]
    public void Parses_human_sizes(string input, long expected)
    {
        Assert.Equal(expected, CommandLine.ParseSize(input));
    }

    [Fact]
    public void Rejects_a_size_that_is_not_a_number()
    {
        Assert.Throws<ArgumentException>(() => CommandLine.ParseSize("huge"));
    }

    [Fact]
    public void Missing_required_options_fail_loudly()
    {
        var command = CommandLine.Parse(new[] { "iso" });

        Assert.Throws<ArgumentException>(() => command.Require("out"));
        Assert.Equal("fallback", command.Value("out", "fallback"));
    }
}
