using System.Runtime.CompilerServices;

// The test suite exercises internals directly — notably the archive's path-safety helper,
// which guards against a hostile disc writing outside the target library. That check deserves
// tests that do not have to construct a malicious archive first.
[assembly: InternalsVisibleTo("SteamDisc.Tests")]
