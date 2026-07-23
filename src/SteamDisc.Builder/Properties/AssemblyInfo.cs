using System.Runtime.CompilerServices;

// The argument parser has enough edge cases (flags adjacent to valued options, "=" syntax,
// human-readable sizes) to be worth testing directly rather than through process invocation.
[assembly: InternalsVisibleTo("SteamDisc.Tests")]
