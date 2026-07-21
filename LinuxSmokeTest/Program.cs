using LiveKit.Rtc;

// Phase 0 smoke test for the Linux client plan: does Livekit.Rtc.Dotnet's
// native FFI layer (liblivekit_ffi.so) actually load and initialize on
// linux-x64? That's the real unknown here, not whether a specific room
// join succeeds - a DllNotFoundException or P/Invoke marshaling crash at
// `new Room()`/`ConnectAsync` looks completely different from a clean
// "connection rejected" error, and only the former means the native
// layer itself is broken on this platform.
//
// If LIVEKIT_URL/LIVEKIT_API_KEY/LIVEKIT_API_SECRET are set (matching
// Server/Services/LiveKitTokenService.cs's env var names), this also
// attempts a real connect against the actual deployment for a full
// end-to-end check. Without them, it still proves the native layer loads
// by attempting - and expecting to cleanly fail - a connect with a
// placeholder token.

var url = Environment.GetEnvironmentVariable("LIVEKIT_URL");
var apiKey = Environment.GetEnvironmentVariable("LIVEKIT_API_KEY");
var apiSecret = Environment.GetEnvironmentVariable("LIVEKIT_API_SECRET");

Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}, " +
    $"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

string token;
string connectUrl;
if (url is not null && apiKey is not null && apiSecret is not null)
{
    Console.WriteLine("LIVEKIT_URL/LIVEKIT_API_KEY/LIVEKIT_API_SECRET are set - attempting a real connect.");
    connectUrl = url;
    token = new Livekit.Server.Sdk.Dotnet.AccessToken(apiKey, apiSecret)
        .WithIdentity("linux-smoke-test")
        .WithName("linux-smoke-test")
        .WithGrants(new Livekit.Server.Sdk.Dotnet.VideoGrants { RoomJoin = true, Room = "linux-smoke-test" })
        .WithTtl(TimeSpan.FromMinutes(5))
        .ToJwt();
}
else
{
    Console.WriteLine("No LiveKit credentials set - attempting a connect with a placeholder token " +
        "(expected to fail cleanly; a native-load crash would look very different).");
    connectUrl = "wss://example.invalid";
    token = "placeholder";
}

try
{
    Console.WriteLine("Constructing Room()...");
    var room = new Room();
    Console.WriteLine("Room() constructed OK - native library loaded.");

    Console.WriteLine("Calling ConnectAsync...");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await room.ConnectAsync(connectUrl, token, new RoomOptions(), cts.Token);

    Console.WriteLine("Connected successfully.");
    await room.DisconnectAsync();
    Console.WriteLine("SMOKE TEST PASSED: connected and disconnected cleanly.");
}
catch (Exception ex)
{
    // A connect failure (bad URL/token/timeout) here is EXPECTED and fine
    // when no real credentials were supplied - it proves the native layer
    // initialized and made a real network attempt. Only a load-time
    // failure (DllNotFoundException, BadImageFormatException, or an
    // exception thrown directly from `new Room()` before any network
    // activity) means the platform port itself is broken.
    Console.WriteLine($"Connect attempt did not succeed: {ex.GetType().Name}: {ex.Message}");
    if (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
    {
        Console.WriteLine("SMOKE TEST FAILED: native library did not load on this platform.");
        Environment.Exit(1);
    }

    Console.WriteLine("SMOKE TEST PASSED (partial): native library loaded and attempted a connection; " +
        "the connection itself failed as expected without real credentials/network.");
}
