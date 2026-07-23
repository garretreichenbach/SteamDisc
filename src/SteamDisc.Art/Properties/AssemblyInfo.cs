using System.Runtime.CompilerServices;

// Candidate ranking decides which artwork ends up on a printed cover, so its scoring is unit
// tested directly rather than only through a live provider.
[assembly: InternalsVisibleTo("SteamDisc.Tests")]
