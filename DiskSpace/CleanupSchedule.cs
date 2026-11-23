namespace DiskSpace;

internal sealed record CleanupSchedule(DateTimeOffset Last, int Found, long Removed, string Result, DateTimeOffset? RetryAfter = null)
{
    internal static readonly CleanupSchedule Empty = new(DateTimeOffset.MinValue, 0, 0, "Ещё не запускалась");
    internal bool Due(DateTimeOffset now) => RetryAfter is { } next ? now >= next : now - Last >= TimeSpan.FromDays(1);
}
