using System;
using System.Collections.Generic;
using System.Linq;

namespace InfiniteDrive.Models;

/// <summary>One durable queue entry per coverage identity/episode, never a playback claim.</summary>
public sealed class ImportDiversityEntry
{
    public string Status { get; set; } = "waiting";
    public string Profile { get; set; } = "";
    public long Generation { get; set; }
    public string Reason { get; set; } = "single_source";
    public int Streak { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public DateTimeOffset? NextAttempt { get; set; }
}

public static class ImportDiversityPolicy
{
    private static readonly string[] Labels = { "TorBox", "Usenet", "Real-Debrid", "AllDebrid", "Premiumize",
        "Debrid-Link", "Offcloud", "EasyDebrid", "Debrider", "Easynews", "NZBDav", "AltMount", "StremThru" };
    public static string? Provider(string? label) => label?.ToLowerInvariant() is "nntp" or "stremionntp"
        ? "Usenet" : Labels.FirstOrDefault(x => string.Equals(x, label, StringComparison.OrdinalIgnoreCase));

    // Every retained path must have one unambiguous known label. Historical paths don't count.
    public static List<string>? Providers(ImportEpisode ep)
    {
        if (ep.Paths.Count == 0) return null;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in ep.Paths.Distinct(StringComparer.Ordinal))
        {
            var labels = ep.Versions.Where(x => x.Path == path).Select(x => Provider(x.ServiceLabel)).Distinct().ToList();
            if (labels.Count != 1 || labels[0] == null) return null;
            result.Add(labels[0]!);
        }
        return result.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public static bool Eligible(ImportCoverage coverage, ImportEpisode ep) =>
        coverage.Exclusion.Length == 0 && coverage.SnapshotStatus == "success" && ep.Expected && ep.Eligible &&
        ep.State == "indexed" && ep.Failure.Length == 0 && Providers(ep)?.Count == 1;

    public static void Reconcile(ImportCoverage coverage, ImportEpisode ep, string profile, DateTimeOffset now)
    {
        var providers = Providers(ep);
        if (!Eligible(coverage, ep))
        {
            if (ep.Diversity != null)
            {
                // A successful ordinary refresh waits for Emby indexing. It is not a
                // new diversity generation and must not keep restarting the six-hour wait.
                var awaitingIndex = coverage.Exclusion.Length == 0 && coverage.SnapshotStatus == "success" &&
                    ep.Expected && ep.Eligible && ep.Failure.Length == 0 && ep.State == "awaiting_indexing" &&
                    providers?.Count == 1 && ep.Diversity.Profile == profile &&
                    ep.Diversity.Generation == coverage.Generation;
                // Resolved/retired entries are entering a new single-source wait. A
                // resolved entry may have no deadline; do not strand it by preserving null.
                if (awaitingIndex && (ep.Diversity.Status is "resolved" or "retired" ||
                    ep.Diversity.NextAttempt == null))
                    ep.Diversity.NextAttempt = now.AddHours(6);
                ep.Diversity.Status = awaitingIndex ? "waiting" : providers?.Count > 1 ? "resolved" : "retired";
                ep.Diversity.Reason = awaitingIndex ? "awaiting_indexing" : providers?.Count > 1 ? "multiple_sources" : "ineligible";
            }
            return;
        }
        if (ep.Diversity == null || ep.Diversity.Profile != profile || ep.Diversity.Generation != coverage.Generation)
            ep.Diversity = new() { Profile = profile, Generation = coverage.Generation, CreatedAt = now,
                NextAttempt = now.AddHours(6) };
        else if (ep.Diversity.Status is "retired" or "resolved")
        { ep.Diversity.Status = "waiting"; ep.Diversity.Reason = "single_source";
          ep.Diversity.NextAttempt = now.AddHours(6); }
        else if (ep.Diversity.Reason == "awaiting_indexing") ep.Diversity.Reason = "single_source";
        if (ep.Diversity.Status == "in_flight" && (ep.LeaseUntil == null || ep.LeaseUntil <= now))
        { ep.Diversity.Status = "waiting"; ep.Diversity.Reason = "interrupted";
          ep.Diversity.NextAttempt = now.AddMinutes(5); }
        if (ep.Paths.Distinct().Count() >= 8)
        { ep.Diversity.Status = "capacity"; ep.Diversity.Reason = "version_limit"; }
        else if (ep.Diversity.Status == "capacity")
        { ep.Diversity.Status = "waiting"; ep.Diversity.Reason = "single_source"; }
    }

    public static void Defer(ImportDiversityEntry entry, string reason, DateTimeOffset now,
        double jitter, DateTimeOffset cooldown)
    {
        entry.Streak++; entry.Status = "waiting"; entry.Reason = reason; entry.CheckedAt = now;
        entry.NextAttempt = ImportCoveragePolicy.RetryAt(entry.Streak,
            reason is "same_source" or "source_unavailable" or "no_valid_addition", now, jitter, cooldown);
    }
}
