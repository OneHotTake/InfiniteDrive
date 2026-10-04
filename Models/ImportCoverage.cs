using System;
using System.Collections.Generic;
using System.Linq;

namespace InfiniteDrive.Models;

public enum ImportMode { Off, Observe, Repair }

/// <summary>Durable observations, separate from catalog lifecycle and playback health.</summary>
public sealed class ImportCoverage
{
    public string Identity { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> CatalogIds { get; set; } = new();
    public string SnapshotStatus { get; set; } = "unknown";
    public string ProviderStatus { get; set; } = "not_checked";
    public int InventoryPolicyVersion { get; set; }
    public DateTimeOffset? SnapshotAt { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public DateTimeOffset? MetadataRetryAt { get; set; }
    public string Exclusion { get; set; } = "";
    public long Generation { get; set; }
    public bool IncludeSpecials { get; set; }
    public string Cursor { get; set; } = "";
    public List<ImportEpisode> Items { get; set; } = new();
    public bool Complete => SnapshotStatus == "success" && Exclusion.Length == 0 &&
        Items.Any(x => x.Eligible) && Items.Where(x => x.Eligible).All(x => x.State == "indexed");
}

public sealed class ImportEpisode
{
    // Movies use movie; TV uses aired:S:E. Unknown numbering is never guessed.
    public string Key { get; set; } = "";
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public DateTimeOffset? Released { get; set; }
    public bool DateOnly { get; set; }
    public string Numbering { get; set; } = "aired";
    public bool Expected { get; set; } = true;
    public bool Eligible { get; set; }
    public string Eligibility { get; set; } = "unknown_date";
    public string State { get; set; } = "unknown";
    public bool EverPublished { get; set; }
    public bool InitialFailure { get; set; }
    public List<string> Paths { get; set; } = new();
    public List<string> NativeIds { get; set; } = new();
    public List<ImportVersionEvidence> Versions { get; set; } = new();
    public int Attempts { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string LastFailureKind { get; set; } = "";
    public DateTimeOffset? LastFailureAt { get; set; }
    public int Notifications { get; set; }
    public DateTimeOffset? FirstNotification { get; set; }
    public DateTimeOffset? LastNotification { get; set; }
    public DateTimeOffset? NextAttempt { get; set; }
    public DateTimeOffset? LastVersionRefresh { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public string Failure { get; set; } = "";
    public string? Lease { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    // Additive JSON migration: old observations deserialize without queue state.
    public ImportDiversityEntry? Diversity { get; set; }
}

/// <summary>Fresh per-file metadata bound to its current URL hash, without storing the URL.</summary>
public sealed record ImportVersionEvidence(string Path, string UrlSha256, string ServiceLabel,
    string Resolution, string? Encode, long? SizeBytes, DateTimeOffset ObservedAt);

public static class ImportCoveragePolicy
{
    public static string Eligibility(ImportEpisode item, bool specials, DateTimeOffset now,
        TimeZoneInfo timezone)
    {
        if (!item.Expected) return "upstream_removed";
        if (item.Numbering != "aired") return "numbering_conflict";
        if (item.Season == 0 && !specials) return "special_excluded";
        if (item.Released is null) return item.EverPublished ? "eligible" : "unknown_date";
        var release = item.Released.Value;
        if (item.DateOnly)
        {
            var end = DateTime.SpecifyKind(release.Date.AddDays(1), DateTimeKind.Unspecified);
            release = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(end, timezone));
        }
        return release <= now ? "eligible" : "future";
    }

    public static string Classify(ImportEpisode item, bool file, bool indexed, bool conflict,
        DateTimeOffset now)
    {
        if (!item.Eligible) return "excluded";
        if (conflict) return "index_mismatch";
        if (file && indexed) return "indexed";
        if (file)
            return item.Notifications >= 3 && item.FirstNotification <= now.AddHours(-2)
                ? "indexing_attention" : "awaiting_indexing";
        if (item.LeaseUntil > now) return "resolving";
        return item.NextAttempt > now ? "retrying" : "missing";
    }

    public static DateTimeOffset RetryAt(int attempt, bool noSource, DateTimeOffset now,
        double jitter = 0, DateTimeOffset? cooldown = null)
    {
        var delay = noSource
            ? attempt switch { <= 1 => TimeSpan.FromHours(6), 2 => TimeSpan.FromDays(1),
                3 => TimeSpan.FromDays(2), 4 => TimeSpan.FromDays(3), _ => TimeSpan.FromDays(7) }
            : attempt switch { 1 => TimeSpan.FromMinutes(15), 2 => TimeSpan.FromHours(1),
                3 => TimeSpan.FromHours(4), 4 => TimeSpan.FromDays(1),
                5 => TimeSpan.FromDays(3), _ => TimeSpan.FromDays(7) };
        var next = now + TimeSpan.FromTicks((long)(delay.Ticks * (1 + Math.Clamp(jitter, 0, .2))));
        return cooldown > next ? cooldown.Value : next;
    }

    public static void Failed(ImportEpisode episode, string failure, DateTimeOffset now,
        double jitter, DateTimeOffset? cooldown)
    {
        // Upgrade old saved records conservatively; attempts/ledger remain untouched.
        episode.ConsecutiveFailures = episode.LastFailureKind == failure
            ? episode.ConsecutiveFailures + 1
            : episode.LastFailureKind.Length == 0 ? Math.Max(1, episode.Attempts) : 1;
        episode.LastFailureKind = failure; episode.LastFailureAt = now;
        episode.Failure = failure;
        episode.NextAttempt = failure == "provider_configuration" ? null :
            RetryAt(episode.ConsecutiveFailures, failure == "source_unavailable", now, jitter, cooldown);
    }

    public static bool Reprocessable(ImportCoverage coverage, ImportEpisode episode) =>
        coverage.Exclusion.Length == 0 && coverage.SnapshotStatus == "success" &&
        episode.Expected && episode.Eligible && episode.State is not ("excluded" or "index_mismatch") &&
        episode.Failure is "source_unavailable" or "transport_failure" or "lookup_deadline" or
            "http_429" or "publication_failed" or "slice_cancelled";

    public static bool AcceptSnapshot(IReadOnlyCollection<ImportEpisode> previous,
        IReadOnlyCollection<ImportEpisode> fresh)
    {
        if (fresh.Count == 0 || fresh.GroupBy(x => x.Key).Any(g => g.Count() > 1)) return false;
        // An unexpectedly reduced inventory is evidence to review, never a deletion instruction.
        return previous.Count == 0 || fresh.Count >= previous.Count * .8;
    }
}
