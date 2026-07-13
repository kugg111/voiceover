namespace Voiceover.Server.Models;

public enum CallOutcome { Completed, Missed, Declined, Cancelled }

// Unencrypted, cross-conversation call history metadata - deliberately
// separate from the E2EE call-event chat messages (see
// Client/Services/CallEventMessage.cs), which only tell each participant's
// own DM thread what happened. A "Recent Calls" view needs to list calls
// regardless of which conversation is open, which the encrypted-DM approach
// can't cheaply provide - so this table stores only caller/callee ids and
// timing/outcome, never call content (there is none - calls carry no
// server-visible media either).
public class CallRecord
{
    public int Id { get; set; }
    public int CallerId { get; set; }
    public int CalleeId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public CallOutcome Outcome { get; set; }
}
