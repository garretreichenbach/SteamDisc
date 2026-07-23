namespace SteamDisc.Core.Vdf;

/// <summary>Thrown when a KeyValues document cannot be parsed.</summary>
public sealed class VdfSyntaxException : Exception
{
    public VdfSyntaxException(string message, int line, int column)
        : base($"{message} (line {line}, column {column})")
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }

    public int Column { get; }
}
