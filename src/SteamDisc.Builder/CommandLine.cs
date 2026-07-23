using System.Globalization;

namespace SteamDisc.Builder;

/// <summary>
/// A tiny argument parser: verbs, positional values, and <c>--name value</c> / <c>--flag</c>
/// options.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled from a package, because Core deliberately carries no
/// third-party dependencies and there is no reason for the Builder to be the exception. The
/// surface is small enough that a parser is cheaper than an argument about one.
/// </remarks>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = new();

    private CommandLine()
    {
    }

    public IReadOnlyList<string> Positional => _positional;

    public static CommandLine Parse(IEnumerable<string> args)
    {
        var result = new CommandLine();
        var tokens = args.ToList();

        for (var i = 0; i < tokens.Count; i++)
        {
            var argument = tokens[i];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (argument is "-h" or "-?")
                {
                    result._options["help"] = null;
                    continue;
                }

                result._positional.Add(argument);
                continue;
            }

            var name = argument[2..];
            var separator = name.IndexOf('=', StringComparison.Ordinal);

            if (separator > 0)
            {
                result._options[name[..separator]] = name[(separator + 1)..];
                continue;
            }

            // Take the next token as this option's value only if it is not itself an option.
            // Peeking rather than consuming matters: "--no-art --out path" must leave --out
            // with its value intact rather than treating it as a bare flag.
            if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result._options[name] = tokens[i + 1];
                i++;
            }
            else
            {
                result._options[name] = null;
            }
        }

        return result;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Value(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public string Value(string name, string fallback) => Value(name) ?? fallback;

    public string Require(string name)
        => Value(name) ?? throw new ArgumentException($"--{name} is required.");

    public long? Size(string name)
    {
        var raw = Value(name);
        return raw is null ? null : ParseSize(raw);
    }

    public string? PositionalAt(int index) => index < _positional.Count ? _positional[index] : null;

    /// <summary>Parses "512m", "2g", "1500000" into a byte count.</summary>
    internal static long ParseSize(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        long multiplier = 1;

        if (text.EndsWith("kb", StringComparison.Ordinal) || text.EndsWith('k'))
        {
            multiplier = 1024;
            text = text.TrimEnd('b', 'k');
        }
        else if (text.EndsWith("mb", StringComparison.Ordinal) || text.EndsWith('m'))
        {
            multiplier = 1024 * 1024;
            text = text.TrimEnd('b', 'm');
        }
        else if (text.EndsWith("gb", StringComparison.Ordinal) || text.EndsWith('g'))
        {
            multiplier = 1024L * 1024 * 1024;
            text = text.TrimEnd('b', 'g');
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            throw new ArgumentException($"'{value}' is not a size. Try 2g, 512m or a plain byte count.");
        }

        return (long)(number * multiplier);
    }
}
