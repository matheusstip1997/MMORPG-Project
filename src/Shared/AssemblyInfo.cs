using System.Runtime.CompilerServices;

// Test-only constructors (for example SessionCrypto starting near the end of its sequence space).
// The assembly is "Shared" for .NET and "Game.Shared" in Unity; these names cover the test assemblies of both.
[assembly: InternalsVisibleTo("Shared.Tests")]
[assembly: InternalsVisibleTo("GameServer.Tests")]
[assembly: InternalsVisibleTo("Game.Tests.EditMode")]
