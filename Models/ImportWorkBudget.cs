using System;
using System.Globalization;

namespace InfiniteDrive.Models;

/// <summary>Finite, opt-in maintenance allowance. Provider backoff and the shared ledger still apply.</summary>
public sealed record ImportWorkBudget(int SliceSeconds, int AttemptsPerSlice, int UpgradesPerSlice,
    int MetadataPerSlice, int AttemptsPerDay, DateTimeOffset? CatchUpStartedAt = null,
    DateTimeOffset? CatchUpUntil = null, int Parallelism = 1)
{
    public static ImportWorkBudget Normal { get; } = new(120, 20, 5, 5, 200);
    public bool IsCatchUp => CatchUpStartedAt.HasValue;

    public static ImportWorkBudget For(PluginConfiguration config, DateTimeOffset now)
    {
        if (config.ImportRecoveryMode != ImportMode.Repair ||
            !DateTimeOffset.TryParse(config.ImportCatchUpStartedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var start) ||
            !DateTimeOffset.TryParse(config.ImportCatchUpUntil, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var until) ||
            start > now || until <= now || until <= start || until > start.AddDays(7))
            return Normal;
        return new(480, 4096, 4096, 1000, 40000, start, until, 64);
    }

    public bool NeedsRefresh(ImportEpisode episode, DateTimeOffset now) =>
        !episode.LastVersionRefresh.HasValue ||
        (IsCatchUp ? episode.LastVersionRefresh < CatchUpStartedAt : episode.LastVersionRefresh <= now.AddHours(-1));
}
