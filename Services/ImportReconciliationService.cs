using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;

namespace InfiniteDrive.Services;

/// <summary>One checkpointed reconciliation slice; never derives success from a parent flag.</summary>
public sealed class ImportReconciliationService
{
    private static readonly SemaphoreSlim RunGate = new(1, 1);
    public static readonly SemaphoreSlim MutationGate = new(1, 1);
    private readonly DatabaseManager _db;
    private readonly IImportInventory _inventory;
    private readonly Func<ImportMode> _mode;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<DateTimeOffset> _cooldown;
    private readonly TimeZoneInfo _timezone;

    public ImportReconciliationService(DatabaseManager db, IImportInventory inventory,
        Func<ImportMode> mode, TimeZoneInfo timezone, Func<DateTimeOffset>? clock = null,
        Func<DateTimeOffset>? cooldown = null)
    { _db = db; _inventory = inventory; _mode = mode; _timezone = timezone;
        _clock = clock ?? (() => DateTimeOffset.UtcNow); _cooldown = cooldown ?? (() => DateTimeOffset.MinValue); }

    public static ImportReconciliationService Create()
    {
        var p = Plugin.Instance;
        return new(p.DatabaseManager, new ImportInventory(p.LibraryManager!, p.Logger,
            p.Configuration, p.ProviderManager, p.StrmFileManager!), () => p.Configuration.ImportRecoveryMode,
            TimeZoneInfo.FindSystemTimeZoneById(p.Configuration.ImportHouseholdTimezone),
            cooldown: () => p.CooldownGate?.GlobalCooldownUntil ?? DateTimeOffset.MinValue);
    }

    public async Task RunAsync(CancellationToken ct, IReadOnlyList<CatalogItem>? selected = null)
    {
        if (_mode() == ImportMode.Off || !await RunGate.WaitAsync(0, ct)) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(120));
        var token = budget.Token;
        var attempts = 0;
        var metadata = 0;
        var upgrades = 0;
        var scanned = 0;
        var notified = false;
        var runId = Guid.NewGuid().ToString("N");
        var status = "success";
        try
        {
            await _db.EnsureImportCoverageAsync(token);
            var after = selected == null ? _db.GetMetadata("import_scan_cursor") ?? "" : "";
            var page = selected ?? await _db.GetImportCatalogPageAsync(after, 40);
            if (page.Count == 0 && selected == null)
            { after = ""; page = await _db.GetImportCatalogPageAsync(after, 40); }
            var cursorRows = page.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            if (selected == null)
                page = (await _db.GetDueImportCatalogAsync(_clock())).Concat(page).DistinctBy(x => x.Id).ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sourceItem in page)
            {
                var item = sourceItem;
                token.ThrowIfCancellationRequested();
                if (_mode() == ImportMode.Off) break;
                try
                {
                    var aliases = ImportInventory.Aliases(item);
                    if (aliases.Count == 0) continue;
                    var identity = await _db.FindImportIdentityAsync(aliases, token) ?? aliases[0];
                    var coverage = await _db.GetImportCoverageAsync(identity) ?? new ImportCoverage
                    { Identity = identity, Title = item.Title, LegacyBaseline = !string.IsNullOrEmpty(item.StrmPath) };
                    if (!coverage.CatalogIds.Contains(item.Id)) coverage.CatalogIds.Add(item.Id);
                    var aliasRows = await _db.GetImportCatalogAliasesAsync(item);
                    var established = coverage.CatalogIds.Count == 0 ? null : await _db.GetImportCatalogByIdAsync(coverage.CatalogIds[0]);
                    if (aliasRows.Any(x => !ImportInventory.CompatibleIdentity(item, x)) ||
                        established != null && !ImportInventory.CompatibleIdentity(established, item))
                    {
                        coverage.SnapshotStatus = "identity_conflict"; coverage.Exclusion = "identity_conflict";
                        coverage.CheckedAt = _clock(); await SaveObservedAsync(coverage, token); continue;
                    }
                    foreach (var alias in aliases) await _db.SaveImportAliasAsync(alias, identity, token);
                    if (!seen.Add(identity)) continue;
                    foreach (var aliasRow in aliasRows)
                        if (!coverage.CatalogIds.Contains(aliasRow.Id)) coverage.CatalogIds.Add(aliasRow.Id);
                    // Resolve all aliases through the established primary row and destination.
                    var primary = await _db.GetImportCatalogByIdAsync(coverage.CatalogIds[0]);
                    if (primary != null) item = primary;
                    coverage.Title = item.Title;
                    var now = _clock();
                    var authorized = await IsAuthorizedAsync(coverage, item, token);
                    coverage.Exclusion = !authorized ? "not_authorized" : coverage.Suppressed ? "suppressed" :
                        item.ItemState == ItemState.Retired || _inventory.IsOwned(item) ? "owned_series_or_movie" : "";
                    if (coverage.Exclusion.Length > 0)
                    {
                        coverage.CheckedAt = now;
                        await SaveObservedAsync(coverage, token);
                        continue;
                    }
                    if (metadata < 5 && (coverage.MetadataRetryAt <= now ||
                        !coverage.MetadataRetryAt.HasValue && (!coverage.SnapshotAt.HasValue || coverage.SnapshotAt <= now.AddHours(-6))))
                    {
                        metadata++;
                        try
                        {
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                            deadline.CancelAfter(TimeSpan.FromSeconds(60));
                            var snapshot = await _inventory.FetchAsync(item, deadline.Token);
                            if (!ImportCoveragePolicy.AcceptSnapshot(coverage.Items.Where(x => x.Expected).ToList(), snapshot.Items))
                                throw new InvalidOperationException("suspect_inventory");
                            MergeSnapshot(coverage, snapshot.Items);
                            coverage.ProviderStatus = snapshot.ProviderStatus;
                            coverage.SnapshotStatus = snapshot.ProviderStatus == "numbering_conflict" ? "identity_conflict" : "success";
                            coverage.SnapshotAt = now;
                            coverage.MetadataRetryAt = now.AddHours(6);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch
                        {
                            coverage.SnapshotStatus = "stale_or_unavailable";
                            coverage.MetadataRetryAt = now.AddMinutes(15);
                        }
                    }
                    // Existing source inventories are an explicitly stale migration baseline,
                    // useful for observing files, never sufficient for new automatic imports.
                    if (coverage.Items.Count == 0 && !string.IsNullOrEmpty(item.VideosJson))
                        coverage.Items = EpisodeDiffService.ParseVideoKeys(item.VideosJson).Select(x => new ImportEpisode
                        { Key = $"aired:{x.Season}:{x.Episode}", Season = x.Season, Episode = x.Episode }).ToList();
                    if (coverage.Items.Count == 0 && !ImportInventory.IsSeries(item))
                        coverage.Items.Add(new ImportEpisode { Key = "movie" });

                    var episodes = coverage.Items.OrderBy(x => x.Key, StringComparer.Ordinal)
                        .Where(x => string.CompareOrdinal(x.Key, coverage.Cursor) > 0).Take(200).ToList();
                    if (episodes.Count == 0) { coverage.Cursor = ""; episodes = coverage.Items.OrderBy(x => x.Key, StringComparer.Ordinal).Take(200).ToList(); }
                    foreach (var episode in episodes)
                    {
                        token.ThrowIfCancellationRequested();
                        now = _clock();
                        ImportObservation observation;
                        try { observation = await _inventory.ObserveAsync(item, episode, token); }
                        catch (OperationCanceledException) { throw; }
                        catch { episode.State = "observation_unavailable"; episode.ObservedAt = now; coverage.Cursor = episode.Key;
                            await SaveObservedAsync(coverage, token); continue; }
                        var file = observation.Paths.Count > 0;
                        if (file) episode.EverPublished = true;
                        episode.Paths = observation.Paths;
                        episode.NativeIds = observation.NativeIds;
                        episode.Eligibility = ImportCoveragePolicy.Eligibility(episode, coverage.IncludeSpecials, now, _timezone);
                        episode.Eligible = episode.Eligibility == "eligible";
                        episode.State = ImportCoveragePolicy.Classify(episode, file, observation.NativeIds.Count > 0,
                            observation.Conflict, coverage.LegacyBaseline, now);
                        if (episode.State == "review_removal") episode.Suppressed = true;
                        episode.BaselineKnown = true;
                        episode.ObservedAt = now;
                        if (episode.State == "indexed")
                        { episode.LastSuccess = now; episode.Failure = ""; episode.RestoreRequested = false; episode.Lease = null; episode.LeaseUntil = null; }
                        coverage.Cursor = episode.Key;
                        coverage.CheckedAt = now;
                        await SaveObservedAsync(coverage, token);

                        if (_mode() != ImportMode.Repair) continue;
                        if (episode.State == "awaiting_indexing" && episode.Notifications < 3 &&
                            (!episode.LastNotification.HasValue || episode.LastNotification <= now.AddMinutes(-30)))
                        {
                            // One fallback scan per slice; the native queue coalesces with pending scans.
                            if (!notified) { _inventory.Notify(item); notified = true; }
                            episode.Notifications++;
                            episode.FirstNotification ??= now;
                            episode.LastNotification = now;
                            await SaveObservedAsync(coverage, token);
                        }
                        var upgrade = episode.State == "indexed" && upgrades < 5 &&
                            (!episode.LastVersionRefresh.HasValue || episode.LastVersionRefresh <= now.AddHours(-1)) &&
                            (!episode.NextAttempt.HasValue || episode.NextAttempt <= now) && !coverage.Items.Any(x => x.Eligible && x.State != "indexed");
                        if (episode.State == "indexed" && !episode.LastVersionRefresh.HasValue)
                        { episode.LastVersionRefresh = now; await SaveObservedAsync(coverage, token); upgrade = false; }
                        if ((episode.State != "missing" && !upgrade) || coverage.SnapshotStatus != "success" ||
                            _inventory.ProviderPaused || attempts >= 20 || _cooldown() > now || await _db.GetRecentImportAttemptsAsync(now) >= 200) continue;
                        attempts++;
                        if (upgrade) upgrades++;
                        var lease = Guid.NewGuid().ToString("N");
                        episode.Lease = lease; episode.LeaseUntil = now.AddMinutes(10);
                        if (!upgrade) episode.InitialFailure = true; episode.Attempts++;
                        await _db.RecordImportAttemptAsync(lease, now, token);
                        await SaveObservedAsync(coverage, token);
                        List<SelectedVersion>? versions = null;
                        var failure = "transport_failure";
                        try
                        {
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                            deadline.CancelAfter(TimeSpan.FromSeconds(60));
                            versions = await _inventory.ResolveAsync(item, episode, deadline.Token);
                            if (versions.Count == 0) failure = "source_unavailable";
                            else
                            {
                                await MutationGate.WaitAsync(token);
                                try
                                {
                                    var live = await _db.GetImportCoverageAsync(identity);
                                    var liveEpisode = live?.Items.FirstOrDefault(x => x.Key == episode.Key);
                                    if (_mode() != ImportMode.Repair || live == null || live.Suppressed || liveEpisode?.Suppressed != false ||
                                        live.Generation != coverage.Generation || liveEpisode.Lease != lease ||
                                        !await IsAuthorizedAsync(live, item, token) || _inventory.IsOwned(item))
                                        throw new InvalidOperationException("authorization_changed");
                                    episode.Paths = await _inventory.PublishAsync(item, episode, versions, token);
                                    if (episode.Paths.Count == 0) throw new IOException("publication_failed");
                                    episode.EverPublished = true;
                                    episode.RestoreRequested = false;
                                    episode.State = "awaiting_indexing";
                                    episode.LastVersionRefresh = now;
                                    episode.NextAttempt = null; episode.Attempts = 0;
                                    episode.Failure = "";
                                    episode.Lease = null; episode.LeaseUntil = null;
                                    await _db.SaveImportCoverageAsync(coverage, token);
                                    await RegisterPathsAsync(coverage, item, episode, versions, token);
                                }
                                finally { MutationGate.Release(); }
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (ImportProviderConfigurationException) { failure = "provider_configuration"; versions = null; }
                        catch { versions = null; }
                        if (versions == null || versions.Count == 0)
                        {
                            episode.State = upgrade ? "indexed" : "retrying"; episode.Failure = failure;
                            episode.NextAttempt = failure == "provider_configuration" ? null : ImportCoveragePolicy.RetryAt(episode.Attempts, failure == "source_unavailable",
                                now, Random.Shared.NextDouble() * .2, _cooldown());
                        }
                        episode.Lease = null; episode.LeaseUntil = null;
                        await SaveObservedAsync(coverage, token);
                    }
                    scanned++;
                    if (coverage.SnapshotStatus == "success" && coverage.Items.Any(x => x.ObservedAt.HasValue))
                        await _db.PersistMetadataAsync("import_observation_baseline", "success", token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // No upstream URL/exception message in persistent status.
                    status = "partial";
                    await _db.PersistMetadataAsync("import_last_failure", ex is InvalidOperationException ? "identity_or_inventory_conflict" : "observation_failed", token);
                }
                finally
                {
                    if (selected == null && cursorRows.Contains(sourceItem.Id) && !token.IsCancellationRequested)
                        await _db.PersistMetadataAsync("import_scan_cursor", sourceItem.Id, token);
                }
            }
        }
        catch (OperationCanceledException) { status = ct.IsCancellationRequested ? "cancelled" : "budget_deferred"; }
        finally
        {
            try { await _db.PersistMetadataAsync("import_last_run", System.Text.Json.JsonSerializer.Serialize(new
                { Id = runId, Status = status, FinishedAt = _clock(), Scanned = scanned, Attempts = attempts, Metadata = metadata }), CancellationToken.None); }
            finally { RunGate.Release(); }
        }
    }

    internal static async Task<bool> LegacySuppressedAsync(DatabaseManager db, CatalogItem item, CancellationToken ct)
    {
        if (db.GetMetadata("import_schema") != "1") return false;
        try
        {
            var identity = await db.FindImportIdentityAsync(ImportInventory.Aliases(item), ct);
            var state = identity == null ? null : await db.GetImportCoverageAsync(identity);
            return state?.Suppressed == true || state?.Items.Any(x => x.Suppressed) == true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return true; } // An unreadable safety journal is not authorization to recreate media.
    }

    private async Task<bool> IsAuthorizedAsync(ImportCoverage coverage, CatalogItem fallback, CancellationToken ct)
    {
        // A block on any known alias wins; a removed membership alone does not defeat
        // another still-authorized source. Never trust the stale in-flight CatalogItem.
        var rows = new List<CatalogItem>();
        foreach (var id in coverage.CatalogIds)
        { ct.ThrowIfCancellationRequested(); var row = await _db.GetImportCatalogByIdAsync(id); if (row != null) rows.Add(row); }
        rows.AddRange(await _db.GetImportCatalogAliasesAsync(fallback));
        foreach (var row in rows)
            if (await _db.IsBlockedAsync(row.AioId, row.TmdbId, null)) return false;
        return !rows.Any(x => x.Blocked || !ImportInventory.CompatibleIdentity(fallback, x)) && rows.Any(x => x.RemovedAt == null && x.ItemState != ItemState.Retired);
    }

    private async Task SaveObservedAsync(ImportCoverage state, CancellationToken ct)
    {
        await MutationGate.WaitAsync(ct);
        try
        {
            var live = await _db.GetImportCoverageAsync(state.Identity);
            if (live != null && live.Generation != state.Generation)
            {
                state.Generation = live.Generation;
                state.Suppressed = live.Suppressed;
                state.IncludeSpecials = live.IncludeSpecials;
                foreach (var episode in state.Items)
                {
                    var prior = live.Items.FirstOrDefault(x => x.Key == episode.Key);
                    if (prior == null) continue;
                    episode.Suppressed = prior.Suppressed;
                    episode.RestoreRequested = prior.RestoreRequested;
                }
            }
            await _db.SaveImportCoverageAsync(state, ct);
        }
        finally { MutationGate.Release(); }
    }

    private async Task RegisterPathsAsync(ImportCoverage state, CatalogItem item, ImportEpisode episode, List<SelectedVersion> versions, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(episode.Paths[0])!;
        var titleRoot = episode.Season.HasValue ? Path.GetDirectoryName(directory)! : directory;
        foreach (var id in state.CatalogIds)
        {
            var current = await _db.GetImportCatalogByIdAsync(id);
            if (current == null || current.RemovedAt != null || current.Blocked || current.ItemState == ItemState.Retired) continue;
            current.StrmPath = titleRoot;
            current.LocalSource = "strm"; current.LocalPath = titleRoot;
            current.ItemState = ItemState.Written;
            current.SelectedVersionsJson = StrmFileManager.SerializeVersions(versions);
            current.LastVersionRefreshAt = _clock().ToString("o");
            current.UpdatedAt = _clock().ToString("o");
            if (ImportInventory.IsSeries(item))
                current.VideosJson = System.Text.Json.JsonSerializer.Serialize(state.Items.Where(x => x.Expected && x.Season.HasValue)
                    .Select(x => new { season = x.Season, episode = x.Episode }));
            await _db.UpsertCatalogItemAsync(current, ct);
        }
        item.StrmPath = titleRoot;
    }

    public static void MergeSnapshot(ImportCoverage coverage, List<ImportEpisode> fresh)
    {
        foreach (var old in coverage.Items) old.Expected = false;
        foreach (var current in fresh)
        {
            var old = coverage.Items.FirstOrDefault(x => x.Key == current.Key);
            if (old == null) { current.BaselineKnown = coverage.SnapshotAt.HasValue; current.LegacyAmbiguous = coverage.LegacyBaseline && !coverage.SnapshotAt.HasValue; coverage.Items.Add(current); }
            else { old.Expected = true; old.Released = current.Released; old.DateOnly = current.DateOnly; old.Numbering = current.Numbering; }
        }
    }
}
