namespace SteamDisc.Runtime.App;

/// <summary>
/// The subset of the console runtime's options the skinned front-end honours.
/// </summary>
/// <remarks>
/// The graphical installer is driven by clicks, not flags, so most of the console's switches make
/// no sense here. What remains is the disc root (the folder to install from) and the escape
/// hatches an operator scripting a machine might still pass.
/// </remarks>
public sealed class RuntimeArgs
{
    /// <summary>The folder holding <c>payload.json</c> — the disc, or a staging folder.</summary>
    public string DiscRoot { get; private set; } = AppContext.BaseDirectory;

    public string? LibraryPath { get; private set; }

    public string? SteamPath { get; private set; }

    public string? LogPath { get; private set; }

    public static RuntimeArgs Parse(string[] args)
    {
        var result = new RuntimeArgs();
        var expectingDiscRoot = true;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            if (!argument.StartsWith('-'))
            {
                if (expectingDiscRoot)
                {
                    result.DiscRoot = Path.GetFullPath(argument);
                    expectingDiscRoot = false;
                }

                continue;
            }

            switch (argument.ToLowerInvariant())
            {
                case "--library":
                    result.LibraryPath = Next(args, ref i);
                    break;
                case "--steam-path":
                    result.SteamPath = Next(args, ref i);
                    break;
                case "--log":
                    result.LogPath = Next(args, ref i);
                    break;
            }
        }

        return result;
    }

    private static string? Next(string[] args, ref int index)
        => index + 1 < args.Length ? args[++index] : null;
}
