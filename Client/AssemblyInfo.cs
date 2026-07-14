using System.Runtime.CompilerServices;

// Lets Client.Tests exercise E2eeService's internal AES-GCM pack/unpack
// helpers directly (see WrapBytes/DecryptPacked) without needing to mock
// the full ECDH handshake that normally derives the key in production.
[assembly: InternalsVisibleTo("Client.Tests")]
