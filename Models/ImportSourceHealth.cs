using System;

namespace InfiniteDrive.Models;

/// <summary>Maintenance-only circuit. Responsive AIO replies do not prove every upstream is healthy.</summary>
public sealed class ImportSourceHealth
{
    public int ConsecutiveErrors { get; set; }
    public int Escalation { get; set; }
    public int ResponsiveReplies { get; set; }
    public int MatchedReplies { get; set; }
    public DateTimeOffset? LastResponseAt { get; set; }
    public DateTimeOffset? LastMatchAt { get; set; }
    public DateTimeOffset? PausedUntil { get; set; }
    public string LastOutcome { get; set; } = "";
    public bool Paused(DateTimeOffset now) => PausedUntil > now;
    public bool RecoveryReady(DateTimeOffset now) => !Paused(now) && ResponsiveReplies >= 3 && MatchedReplies > 0 &&
        LastResponseAt >= now.AddMinutes(-15) && LastMatchAt >= now.AddMinutes(-15) && ConsecutiveErrors == 0;

    public void Record(string outcome, DateTimeOffset now)
    {
        if (outcome is "cancelled" or "slice_cancelled" or "publication_failed") return;
        LastOutcome = outcome;
        if (outcome is "" or "source_unavailable")
        {
            if (LastResponseAt < now.AddMinutes(-15)) { ResponsiveReplies = 0; MatchedReplies = 0; }
            LastResponseAt = now; ResponsiveReplies = Math.Min(3, ResponsiveReplies + 1);
            if (outcome.Length == 0) { MatchedReplies = Math.Min(3, MatchedReplies + 1); LastMatchAt = now; }
            ConsecutiveErrors = 0;
            if (RecoveryReady(now)) { Escalation = 0; PausedUntil = null; }
        }
        else if (outcome is "transport_failure" or "lookup_deadline" or "http_429" or "provider_configuration")
        {
            ResponsiveReplies = 0;
            MatchedReplies = 0;
            // An in-flight batch may finish after the circuit opens. It must not
            // advance the ladder 64 times or move the pause on every completion.
            if (Paused(now)) return;
            if (++ConsecutiveErrors >= 3 || outcome is "http_429" or "provider_configuration")
            {
                Escalation++;
                PausedUntil = now + (Escalation switch { 1 => TimeSpan.FromMinutes(5),
                    2 => TimeSpan.FromMinutes(15), 3 => TimeSpan.FromHours(1),
                    4 => TimeSpan.FromHours(4), _ => TimeSpan.FromDays(1) });
                ConsecutiveErrors = 0;
            }
        }
    }
}

public sealed record ImportReprocessSummary(string? RequestId, int Pending, int InFlight,
    int Published, int Failed, int Cancelled);
