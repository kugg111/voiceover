namespace Voiceover.Client.Services;

// Call outcomes (missed/declined/ended) show up as a real line in the DM
// thread, not just an ephemeral toast - reuses the existing E2EE
// SendDirectMessage pipeline wholesale instead of a parallel schema/table,
// since the server can't construct a properly encrypted DirectMessage row
// itself (it never has either party's key material - see E2eeService).
// Whichever client's local action ends the call sends one of these to the
// other party; both sides see it since it's the same DM thread either way.
public static class CallEventMessage
{
    // Bracket characters most keyboards/IMEs have no direct way to type,
    // so a real message can never collide with this the way a bare prefix
    // like "CALL:" on its own could.
    private const string Prefix = "⟦VOICEOVER_CALL⟧";

    public static string Format(string outcome) => Prefix + outcome;

    public static bool IsCallEvent(string content) => content.StartsWith(Prefix);

    // Returns the original content unchanged for anything that isn't a call
    // event - safe to call on every message/preview string unconditionally.
    public static string Prettify(string content)
    {
        if (!content.StartsWith(Prefix)) return content;

        return content[Prefix.Length..] switch
        {
            "Missed" => "📞 Missed call",
            "Declined" => "📞 Call declined",
            "Ended" => "📞 Call ended",
            _ => "📞 Call"
        };
    }
}
