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
    private readonly Func<ImportWorkBudget> _workBudget;
    private ImportRunTelemetry? _activeTelemetry;

    public ImportReconciliationService(DatabaseManager db, IImportInventory inventory,
        Func<ImportMode> mode, TimeZoneInfo timezone, Func<DateTimeOffset>? clock = null,
        Func<DateTimeOffset>? cooldown = null, Func<ImportWorkBudget>? workBudget = null)
    { _db = db; _inventory = inventory; _mode = mode; _timezone = timezone;
        _workBudget = workBudget ?? (() => ImportWorkBudget.Normal);
        _clock = clock ?? (() => DateTimeOffset.UtcNow); _cooldown = cooldown ?? (() => DateTimeOffset.MinValue); }

    public static ImportReconciliationService Create()
    {
        var p = Plugin.Instance;
        return new(p.DatabaseManager, new ImportInventory(p.LibraryManager!, p.Logger,
            p.Configuration, p.ProviderManager, p.StrmFileManager!), () => p.Configuration.ImportRecoveryMode,
            TimeZoneInfo.FindSystemTimeZoneById(p.Configuration.ImportHouseholdTimezone),
            cooldown: () => p.CooldownGate?.GlobalCooldownUntil ?? DateTimeOffset.MinValue,
            workBudget: () => ImportWorkBudget.For(p.Configuration, DateTimeOffset.UtcNow));
    }

    public async Task RunAsync(CancellationToken ct, IReadOnlyList<CatalogItem>? selected = null)
    {
        if (_mode() == ImportMode.Off || !await RunGate.WaitAsync(0, ct)) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var allowance = _workBudget();
        var elapsed = Stopwatch.StartNew();
        budget.CancelAfter(TimeSpan.FromSeconds(allowance.SliceSeconds));
        var token = budget.Token;
        var attempts = 0;
        var metadata = 0;
        var upgrades = 0;
        var published = 0;
        var scanned = 0;
        var notified = false;
        var runId = Guid.NewGuid().ToString("N");
        var status = "success";
        var telemetry = ImportRunTelemetry.Start(runId);
        _activeTelemetry = telemetry;
        var pending = new List<ResolutionWork>();
        var deferred = new List<ResolutionCandidate>();
        // Rotate the first claim on scarce credits across native slices. The other
        // lane can borrow unused credits after inventory discovery; no new quota.
        var priority = allowance.IsCatchUp && _db.GetMetadata("import_catch_up_last_priority") == "refresh"
            ? "missing" : "refresh";
        var sourceFailures = 0;
        var transportFailures = 0;
        var refreshed = 0;

        async Task<ResolutionResult> ResolveAsync(CatalogItem item, ImportEpisode episode)
        {
            telemetry.LookupStarted();
            var timer = Stopwatch.StartNew();
            var failure = "";
            List<SelectedVersion>? versions = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(allowance.IsCatchUp ? 120 : 60));
            try
            {
                versions = allowance.IsCatchUp
                    ? await _inventory.ResolveCatchUpAsync(item, episode, deadline.Token)
                    : await _inventory.ResolveAsync(item, episode, deadline.Token);
                failure = versions.Count == 0 ? "source_unavailable" : "";
            }
            catch (ImportProviderConfigurationException) { failure = "provider_configuration"; }
            catch (ImportHttpRateLimitException) { failure = "http_429"; }
            catch (OperationCanceledException) { failure = token.IsCancellationRequested ? "cancelled" : deadline.IsCancellationRequested ? "lookup_deadline" : "transport_failure"; }
            catch { failure = "transport_failure"; }
            finally
            {
                telemetry.RecordTiming("resolution", timer.Elapsed.TotalSeconds);
                telemetry.LookupFinished(failure);
            }
            return new(versions, failure);
        }

        async Task CompleteAsync(ResolutionWork work)
        {
            var result = await work.Resolution;
            token.ThrowIfCancellationRequested();
            if (allowance.IsCatchUp && _workBudget().CatchUpStartedAt != allowance.CatchUpStartedAt)
            { budget.Cancel(); token.ThrowIfCancellationRequested(); }
            await MutationGate.WaitAsync(token);
            try
            {
                var live = await _db.GetImportCoverageAsync(work.Coverage.Identity);
                var episode = live?.Items.FirstOrDefault(x => x.Key == work.Episode.Key);
                if (_mode() != ImportMode.Repair || live == null || episode == null ||
                    live.Generation != work.Generation || episode.Lease != work.Lease ||
                    !episode.Eligible || !episode.Expected ||
                    !await IsAuthorizedAsync(live, work.Item, token) || _inventory.IsOwned(work.Item)) return;
                var now = _clock();
                var failure = result.Failure;
                if (result.Versions is { Count: > 0 })
                {
                    telemetry.Work("publication", work.Item.Title, episode.Key);
                    var publishTimer = Stopwatch.StartNew();
                    try
                    {
                        episode.Paths = await _inventory.PublishAsync(work.Item, episode, result.Versions, token);
                        if (episode.Paths.Count == 0) throw new IOException("publication_failed");
                        episode.EverPublished = true;
                        episode.State = "awaiting_indexing";
                        episode.LastVersionRefresh = now;
                        episode.NextAttempt = null; episode.Attempts = 0; episode.Failure = "";
                        episode.Lease = null; episode.LeaseUntil = null;
                        await _db.SaveImportCoverageAsync(live, token);
                        await RegisterPathsAsync(live, work.Item, episode, result.Versions, token);
                        published++;
                        if (work.Upgrade) refreshed++;
                        telemetry.Published(work.Upgrade);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch { failure = "publication_failed"; telemetry.PublicationFailed(); }
                    finally { telemetry.RecordTiming("publication", publishTimer.Elapsed.TotalSeconds); }
                }
                if (failure.Length > 0)
                {
                    if (failure == "source_unavailable") sourceFailures++;
                    if (failure == "transport_failure") transportFailures++;
                    episode.State = work.Upgrade ? work.PreviousState : "retrying";
                    episode.Failure = failure;
                    episode.NextAttempt = failure == "provider_configuration" ? null :
                        ImportCoveragePolicy.RetryAt(episode.Attempts, failure == "source_unavailable", now,
                            Random.Shared.NextDouble() * .2, _cooldown());
                }
                episode.Lease = null; episode.LeaseUntil = null;
                await _db.SaveImportCoverageAsync(live, token);
                // Keep the observation document current for subsequent serial saves of siblings.
                var index = work.Coverage.Items.FindIndex(x => x.Key == episode.Key);
                if (index >= 0) work.Coverage.Items[index] = episode;
            }
            finally { MutationGate.Release(); }
        }

        async Task DrainOneAsync()
        {
            var done = await Task.WhenAny(pending.Select(x => x.Resolution));
            var work = pending.First(x => x.Resolution == done);
            pending.Remove(work);
            await CompleteAsync(work);
        }

        async Task DrainCompletedAsync()
        {
            // Publish ready results before more inventory work. Waiting for 64
            // queued requests (or the end of the page) strands small batches.
            while (pending.FirstOrDefault(x => x.Resolution.IsCompleted) is { } work)
            {
                pending.Remove(work);
                await CompleteAsync(work);
            }
        }
        async Task DispatchAsync(ResolutionCandidate candidate)
        {
            token.ThrowIfCancellationRequested();
            var now = _clock();
            if (_mode() != ImportMode.Repair || attempts >= allowance.AttemptsPerSlice ||
                candidate.Upgrade && upgrades >= allowance.UpgradesPerSlice ||
                _inventory.ProviderPaused || _cooldown() > now ||
                await _db.GetRecentImportAttemptsAsync(now) >= allowance.AttemptsPerDay) return;
            if (allowance.IsCatchUp && _workBudget().CatchUpStartedAt != allowance.CatchUpStartedAt)
            { budget.Cancel(); token.ThrowIfCancellationRequested(); }
            // Deferred work may have waited behind earlier results. Recheck its
            // durable authorization, generation, lease and backoff before a call.
            Func<ResolutionWork> start;
            await MutationGate.WaitAsync(token);
            try
            {
                var live = await _db.GetImportCoverageAsync(candidate.Coverage.Identity);
                var episode = live?.Items.FirstOrDefault(x => x.Key == candidate.Episode.Key);
                if (live == null || episode == null || live.Generation != candidate.Generation ||
                    live.SnapshotStatus != "success" || !episode.Expected || !episode.Eligible ||
                    episode.NextAttempt > now || episode.LeaseUntil > now ||
                    !await IsAuthorizedAsync(live, candidate.Item, token) || _inventory.IsOwned(candidate.Item)) return;
                if (candidate.Upgrade && !allowance.NeedsRefresh(episode, now)) return;
                attempts++;
                if (candidate.Upgrade) upgrades++;
                var lease = Guid.NewGuid().ToString("N");
                episode.Lease = lease; episode.LeaseUntil = now.AddMinutes(10);
                if (!candidate.Upgrade) episode.InitialFailure = true;
                episode.Attempts++;
                await _db.RecordImportAttemptAsync(lease, now, token);
                telemetry.Attempt(candidate.Upgrade);
                await _db.SaveImportCoverageAsync(live, token);
                var index = candidate.Coverage.Items.FindIndex(x => x.Key == episode.Key);
                if (index >= 0) candidate.Coverage.Items[index] = episode;
                start = () => new(candidate.Coverage, candidate.Item, episode, live.Generation, lease,
                    candidate.Upgrade, episode.State, ResolveAsync(candidate.Item, episode));
            }
            finally { MutationGate.Release(); }
            pending.Add(start());
            await DrainCompletedAsync();
            if (pending.Count >= allowance.Parallelism) await DrainOneAsync();
        }
        try
        {
            await _db.EnsureImportCoverageAsync(token);
            if (allowance.IsCatchUp && _mode() == ImportMode.Repair)
                await _db.PersistMetadataAsync("import_catch_up_last_priority", priority, token);
            var cursorKey = allowance.IsCatchUp ? "import_catch_up_scan_cursor" : "import_scan_cursor";
            if (selected == null && allowance.IsCatchUp &&
                _db.GetMetadata("import_catch_up_scan_window") != allowance.CatchUpStartedAt!.Value.ToString("o"))
            {
                await _db.PersistMetadataAsync(cursorKey, "", token);
                await _db.PersistMetadataAsync("import_catch_up_scan_window", allowance.CatchUpStartedAt.Value.ToString("o"), token);
            }
            var after = selected == null ? _db.GetMetadata(cursorKey) ?? "" : "";
            var page = selected ?? (allowance.IsCatchUp
                ? await _db.GetExistingImportCatalogPageAsync(after)
                : await _db.GetImportCatalogPageAsync(after, 40));
            if (page.Count == 0 && selected == null)
            {
                after = "";
                page = allowance.IsCatchUp ? await _db.GetExistingImportCatalogPageAsync(after)
                    : await _db.GetImportCatalogPageAsync(after, 40);
            }
            var cursorRows = page.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            if (selected == null)
            {
                var catchUp = allowance.CatchUpStartedAt.HasValue
                    ? await _db.GetCatchUpImportCatalogAsync(allowance.CatchUpStartedAt.Value, _clock())
                    : new List<CatalogItem>();
                page = (await _db.GetDueImportCatalogAsync(_clock())).Concat(catchUp).Concat(page)
                    .DistinctBy(x => x.Id).ToList();
            }
            var mayHaveRefresh = page.Any(x => !string.IsNullOrEmpty(x.StrmPath));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sourceItem in page)
            {
                await DrainCompletedAsync();
                var item = sourceItem;
                token.ThrowIfCancellationRequested();
                if (_mode() == ImportMode.Off) break;
                try
                {
                    telemetry.Work("inventory", item.Title);
                    var aliases = ImportInventory.Aliases(item);
                    if (aliases.Count == 0) continue;
                    var identity = await _db.FindImportIdentityAsync(aliases, token) ?? aliases[0];
                    var coverage = await _db.GetImportCoverageAsync(identity) ?? new ImportCoverage
                    { Identity = identity, Title = item.Title };
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
                    coverage.Exclusion = !authorized ? "not_authorized" :
                        item.ItemState == ItemState.Retired || _inventory.IsOwned(item) ? "owned_series_or_movie" : "";
                    if (coverage.Exclusion.Length > 0)
                    {
                        coverage.CheckedAt = now;
                        await SaveObservedAsync(coverage, token);
                        continue;
                    }
                    var legacyNumberingGate = coverage.InventoryPolicyVersion == 0 &&
                        coverage.SnapshotStatus == "identity_conflict" && coverage.ProviderStatus == "numbering_conflict";
                    if (metadata < allowance.MetadataPerSlice && (legacyNumberingGate || coverage.MetadataRetryAt <= now ||
                        !coverage.MetadataRetryAt.HasValue && (!coverage.SnapshotAt.HasValue || coverage.SnapshotAt <= now.AddHours(-6))))
                    {
                        metadata++;
                        coverage.InventoryPolicyVersion = 1;
                        telemetry.Work("metadata", item.Title);
                        var metadataTimer = Stopwatch.StartNew();
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
                        finally { telemetry.RecordTiming("metadata", metadataTimer.Elapsed.TotalSeconds); }
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
                        await DrainCompletedAsync();
                        token.ThrowIfCancellationRequested();
                        now = _clock();
                        telemetry.Work("observation", item.Title, episode.Key);
                        ImportObservation observation;
                        var observationTimer = Stopwatch.StartNew();
                        try { observation = await _inventory.ObserveAsync(item, episode, token); }
                        catch (OperationCanceledException) { throw; }
                        catch { episode.State = "observation_unavailable"; episode.ObservedAt = now; coverage.Cursor = episode.Key;
                            await SaveObservedAsync(coverage, token); continue; }
                        finally { telemetry.Checked(); telemetry.RecordTiming("observation", observationTimer.Elapsed.TotalSeconds); }
                        var file = observation.Paths.Count > 0;
                        if (file) episode.EverPublished = true;
                        episode.Paths = observation.Paths;
                        episode.NativeIds = observation.NativeIds;
                        episode.Eligibility = ImportCoveragePolicy.Eligibility(episode, coverage.IncludeSpecials, now, _timezone);
                        episode.Eligible = episode.Eligibility == "eligible";
                        episode.State = ImportCoveragePolicy.Classify(episode, file, observation.NativeIds.Count > 0,
                            observation.Conflict, now);
                        episode.ObservedAt = now;
                        if (episode.State == "indexed")
                        {
                            episode.LastSuccess = now;
                            // Native indexing establishes observation success, not
                            // success of a later source refresh. Preserve its failure.
                            if (episode.LeaseUntil <= now) { episode.Lease = null; episode.LeaseUntil = null; }
                        }
                        coverage.Cursor = episode.Key;
                        coverage.CheckedAt = now;
                        await SaveObservedAsync(coverage, token);

                        if (_mode() != ImportMode.Repair) continue;
                        if (allowance.IsCatchUp && _workBudget().CatchUpStartedAt != allowance.CatchUpStartedAt)
                        { budget.Cancel(); token.ThrowIfCancellationRequested(); }
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
                        var refreshable = episode.State == "indexed" || allowance.IsCatchUp && file &&
                            episode.State is "awaiting_indexing" or "indexing_attention";
                        var upgrade = refreshable && upgrades < allowance.UpgradesPerSlice &&
                            allowance.NeedsRefresh(episode, now) &&
                            (!episode.NextAttempt.HasValue || episode.NextAttempt <= now) && (allowance.IsCatchUp || !coverage.Items.Any(x => x.Eligible && x.State != "indexed"));
                        // Adopting an existing file is not a successful refresh against the current profile.
                        if ((episode.State != "missing" && !upgrade) || coverage.SnapshotStatus != "success" ||
                            _inventory.ProviderPaused || attempts >= allowance.AttemptsPerSlice ||
                            _cooldown() > now || await _db.GetRecentImportAttemptsAsync(now) >= allowance.AttemptsPerDay) continue;
                        var candidate = new ResolutionCandidate(coverage, item, episode, coverage.Generation, upgrade);
                        if (allowance.IsCatchUp && (upgrade ? "refresh" : "missing") != priority &&
                            (priority != "refresh" || mayHaveRefresh))
                            deferred.Add(candidate);
                        else await DispatchAsync(candidate);
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
                        await _db.PersistMetadataAsync(cursorKey, sourceItem.Id, token);
                }
            }
            telemetry.Work("borrowed_capacity");
            foreach (var candidate in deferred)
            {
                await DrainCompletedAsync();
                await DispatchAsync(candidate);
            }
            telemetry.Work("waiting_for_sources");
            while (pending.Count > 0) await DrainOneAsync();
        }
        catch (OperationCanceledException) { status = ct.IsCancellationRequested ? "cancelled" : "budget_deferred"; }
        catch { status = "failed"; throw; }
        finally
        {
            // Cancellation ends all lookups before another run can acquire RunGate.
            budget.Cancel();
            await Task.WhenAll(pending.Select(x => x.Resolution));
            telemetry.Finish(status);
            try { var report = System.Text.Json.JsonSerializer.Serialize(new
                { Id = runId, Status = status, FinishedAt = _clock(), Scanned = scanned, Attempts = attempts, Metadata = metadata, Published = published,
                    Upgrades = upgrades, Refreshed = refreshed, SourceFailures = sourceFailures,
                    TransportFailures = transportFailures, Priority = allowance.IsCatchUp ? priority : "normal", ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    Speed = allowance.IsCatchUp ? "catch_up" : "normal", allowance.AttemptsPerDay, allowance.Parallelism,
                    allowance.CatchUpStartedAt, allowance.CatchUpUntil, Analytics = telemetry.Snapshot() });
                await _db.PersistMetadataAsync("import_last_run", report, CancellationToken.None);
                await _db.SaveImportRunReportAsync(runId, DateTimeOffset.UtcNow, report, CancellationToken.None); }
            finally { RunGate.Release(); }
        }
    }

    private sealed record ResolutionCandidate(ImportCoverage Coverage, CatalogItem Item, ImportEpisode Episode, long Generation, bool Upgrade);
    private sealed record ResolutionResult(List<SelectedVersion>? Versions, string Failure);
    private sealed record ResolutionWork(ImportCoverage Coverage, CatalogItem Item, ImportEpisode Episode,
        long Generation, string Lease, bool Upgrade, string PreviousState, Task<ResolutionResult> Resolution);

    internal static async Task<bool> IsBlockedAsync(DatabaseManager db, CatalogItem item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rows = await db.GetImportCatalogAliasesAsync(item);
        foreach (var row in rows.Append(item))
            if (row.Blocked || await db.IsBlockedAsync(row.AioId, row.TmdbId, null)) return true;
        return false;
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
        var timer = Stopwatch.StartNew();
        await MutationGate.WaitAsync(ct);
        try
        {
            var live = await _db.GetImportCoverageAsync(state.Identity);
            if (live != null && live.Generation != state.Generation)
            {
                state.Generation = live.Generation;
                state.IncludeSpecials = live.IncludeSpecials;
            }
            await _db.SaveImportCoverageAsync(state, ct);
        }
        finally { MutationGate.Release(); _activeTelemetry?.RecordTiming("checkpoint", timer.Elapsed.TotalSeconds); }
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
            if (old == null) { coverage.Items.Add(current); }
            else { old.Expected = true; old.Released = current.Released; old.DateOnly = current.DateOnly; old.Numbering = current.Numbering; }
        }
    }
}
