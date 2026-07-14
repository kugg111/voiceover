namespace Voiceover.Server.Services;

// Shared clamp for the various optional take/skip list endpoints (servers,
// members, channels, invites, friends). Only clamps a caller-supplied take
// that's too large - omitting take at all still returns everything, since
// several clients (member panel, channel sidebar, server rail) fully
// materialize these lists today without ever passing take, and defaulting
// omitted-take to a small page size would silently truncate that.
public static class PaginationLimits
{
    public const int MaxPageSize = 200;

    public static int? Clamp(int? take) => take.HasValue ? Math.Min(take.Value, MaxPageSize) : null;
}
