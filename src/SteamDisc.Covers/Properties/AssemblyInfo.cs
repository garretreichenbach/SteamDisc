using System.Runtime.CompilerServices;

// Internal helpers with non-obvious behaviour — argument splitting, media inference, image
// placement maths — are unit tested directly rather than only through the public surface.
[assembly: InternalsVisibleTo("SteamDisc.Tests")]
