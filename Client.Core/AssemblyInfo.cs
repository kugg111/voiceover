using System.Runtime.CompilerServices;

// Lets Client.Tests exercise E2eeService's internal AES-GCM pack/unpack
// helpers directly (see WrapBytes/DecryptPacked) without needing to mock
// the full ECDH handshake that normally derives the key in production.
// Client.Tests only has a ProjectReference to Client.csproj, not this
// project directly, but that's fine - project references are transitive
// for compilation, and InternalsVisibleTo only cares which assembly is
// asking.
[assembly: InternalsVisibleTo("Client.Tests")]
