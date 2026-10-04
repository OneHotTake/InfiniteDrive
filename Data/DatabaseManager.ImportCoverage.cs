using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;

namespace InfiniteDrive.Data;

public partial class DatabaseManager
{
    // JSON-first additive tables leave existing catalog schema, enum values and retries intact.
    public async Task EnsureImportCoverageAsync(CancellationToken ct = default)
    {
        await ExecuteWriteAsync("CREATE TABLE IF NOT EXISTS import_coverage (identity TEXT PRIMARY KEY, checked_at TEXT NOT NULL, payload TEXT NOT NULL);", _ => { }, ct);
        await ExecuteWriteAsync("CREATE TABLE IF NOT EXISTS import_aliases (alias TEXT PRIMARY KEY, identity TEXT NOT NULL);", _ => { }, ct);
        await ExecuteWriteAsync("CREATE TABLE IF NOT EXISTS import_attempts (id TEXT PRIMARY KEY, attempted_at TEXT NOT NULL);", _ => { }, ct);
        await ExecuteWriteAsync("CREATE INDEX IF NOT EXISTS ix_import_attempts_time ON import_attempts(attempted_at);", _ => { }, ct);
        await ExecuteWriteAsync("CREATE TABLE IF NOT EXISTS import_run_history (id TEXT PRIMARY KEY, finished_at TEXT NOT NULL, payload TEXT NOT NULL);", _ => { }, ct);
        await ExecuteWriteAsync("CREATE TABLE IF NOT EXISTS import_reprocess (request_id TEXT NOT NULL, identity TEXT NOT NULL, episode_key TEXT NOT NULL, status TEXT NOT NULL, requested_at TEXT NOT NULL, updated_at TEXT NOT NULL, PRIMARY KEY(request_id,identity,episode_key));", _ => { }, ct);
        await ExecuteWriteAsync("CREATE INDEX IF NOT EXISTS ix_import_reprocess_status ON import_reprocess(status,identity);", _ => { }, ct);
        if (GetMetadata("import_schema") != "1") await PersistMetadataAsync("import_schema", "1", ct);
    }

    public Task<ImportCoverage?> GetImportCoverageAsync(string identity) => QuerySingleAsync(
        "SELECT payload FROM import_coverage WHERE identity=@id;", c => BindText(c, "@id", identity),
        r => JsonSerializer.Deserialize<ImportCoverage>(r.GetString(0))!);

    public Task<List<ImportCoverage>> GetImportCoveragePageAsync(int offset = 0, int limit = 50) => QueryListAsync(
        "SELECT payload FROM import_coverage ORDER BY identity LIMIT @limit OFFSET @offset;",
        c => { BindInt(c, "@limit", Math.Clamp(limit, 1, 100)); BindInt(c, "@offset", Math.Max(0, offset)); },
        r => JsonSerializer.Deserialize<ImportCoverage>(r.GetString(0))!);

    public Task SaveImportCoverageAsync(ImportCoverage state, CancellationToken ct = default) => ExecuteWriteAsync(
        "INSERT INTO import_coverage(identity,checked_at,payload) VALUES(@id,@at,@json) ON CONFLICT(identity) DO UPDATE SET checked_at=excluded.checked_at,payload=excluded.payload;",
        c => { BindText(c, "@id", state.Identity); BindText(c, "@at", state.CheckedAt?.ToString("o") ?? "");
            BindText(c, "@json", JsonSerializer.Serialize(state)); }, ct);

    public ImportSourceHealth GetImportSourceHealth() =>
        JsonSerializer.Deserialize<ImportSourceHealth>(GetMetadata("import_source_health") ?? "{}") ?? new();

    public Task SaveImportSourceHealthAsync(ImportSourceHealth state, CancellationToken ct = default) =>
        PersistMetadataAsync("import_source_health", JsonSerializer.Serialize(state), ct);

    public async Task<ImportReprocessSummary> GetImportReprocessSummaryAsync()
    {
        if (await QueryScalarIntAsync("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='import_reprocess';", _ => { }) == 0)
            return new(null, 0, 0, 0, 0, 0);
        var id = GetMetadata("import_reprocess_request");
        return await QuerySingleAsync(@"SELECT
            COALESCE(SUM(status='pending'),0),COALESCE(SUM(status='in_flight'),0),
            COALESCE(SUM(status='published'),0),COALESCE(SUM(status='failed'),0),
            COALESCE(SUM(status='cancelled'),0) FROM import_reprocess WHERE request_id=@id;",
            c => BindText(c, "@id", id ?? ""), r => new ImportReprocessSummary(id,
                r.GetInt(0), r.GetInt(1), r.GetInt(2), r.GetInt(3), r.GetInt(4))) ?? new(null, 0, 0, 0, 0, 0);
    }

    // Called under MutationGate: snapshot only; no failure, backoff, generation,
    // attempt ledger or saved-file evidence is rewritten by the admin action.
    public async Task<string> QueueImportReprocessAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var previous = await GetImportReprocessSummaryAsync();
        if (previous.Pending + previous.InFlight > 0) return "already_queued";
        var id = Guid.NewGuid().ToString("N");
        await ExecuteWriteAsync(@"INSERT INTO import_reprocess(request_id,identity,episode_key,status,requested_at,updated_at)
            SELECT @id,s.identity,json_extract(e.value,'$.Key'),'pending',@now,@now
            FROM import_coverage s,json_each(s.payload,'$.Items') e
            WHERE json_extract(s.payload,'$.Exclusion')='' AND json_extract(s.payload,'$.SnapshotStatus')='success'
            AND json_extract(e.value,'$.Expected')=1 AND json_extract(e.value,'$.Eligible')=1
            AND json_extract(e.value,'$.State') NOT IN ('excluded','index_mismatch')
            AND json_extract(e.value,'$.Failure') IN ('source_unavailable','transport_failure','lookup_deadline','http_429','publication_failed','slice_cancelled');",
            c => { BindText(c, "@id", id); BindText(c, "@now", now.ToString("o")); }, ct);
        await PersistMetadataAsync("import_reprocess_request", id, ct);
        return "queued_or_deferred";
    }

    public async Task<bool> IsImportReprocessPendingAsync(string identity, string key) => await QueryScalarIntAsync(
        "SELECT COUNT(*) FROM import_reprocess WHERE request_id=@id AND identity=@identity AND episode_key=@key AND status='pending';",
        c => { BindText(c, "@id", GetMetadata("import_reprocess_request") ?? ""); BindText(c, "@identity", identity); BindText(c, "@key", key); }) > 0;

    public Task SetImportReprocessStatusAsync(string identity, string key, string status, DateTimeOffset now,
        CancellationToken ct = default) => ExecuteWriteAsync(
        "UPDATE import_reprocess SET status=@status,updated_at=@now WHERE request_id=@id AND identity=@identity AND episode_key=@key AND status IN ('pending','in_flight');",
        c => { BindText(c, "@id", GetMetadata("import_reprocess_request") ?? ""); BindText(c, "@identity", identity);
            BindText(c, "@key", key); BindText(c, "@status", status); BindText(c, "@now", now.ToString("o")); }, ct);

    public Task CancelImportReprocessAsync(DateTimeOffset now, CancellationToken ct = default) => ExecuteWriteAsync(
        "UPDATE import_reprocess SET status='cancelled',updated_at=@now WHERE request_id=@id AND status='pending';",
        c => { BindText(c, "@id", GetMetadata("import_reprocess_request") ?? ""); BindText(c, "@now", now.ToString("o")); }, ct);

    public Task<List<CatalogItem>> GetReprocessImportCatalogAsync() => QueryListAsync(@"SELECT DISTINCT c.*
        FROM import_reprocess q JOIN import_coverage s ON s.identity=q.identity
        JOIN catalog_items c ON c.id=json_extract(s.payload,'$.CatalogIds[0]')
        WHERE q.request_id=@id AND q.status='pending'
        AND json_extract(s.payload,'$.SnapshotStatus')='success' AND json_extract(s.payload,'$.Exclusion')=''
        ORDER BY q.requested_at,c.id LIMIT 100;",
        c => BindText(c, "@id", GetMetadata("import_reprocess_request") ?? ""), ReadCatalogItem);

    public Task ReconcileInterruptedReprocessAsync(DateTimeOffset now, CancellationToken ct = default) => ExecuteWriteAsync(@"UPDATE import_reprocess
        SET status='cancelled',updated_at=@now WHERE status='in_flight' AND NOT EXISTS
        (SELECT 1 FROM import_coverage s,json_each(s.payload,'$.Items') e WHERE s.identity=import_reprocess.identity
         AND json_extract(e.value,'$.Key')=import_reprocess.episode_key AND julianday(json_extract(e.value,'$.LeaseUntil'))>julianday(@now));",
        c => BindText(c, "@now", now.ToString("o")), ct);

    // A normal repair or a later identity/removal decision can supersede queued
    // work. Retire that request without changing coverage, backoff or attempts.
    public Task RetireSupersededReprocessAsync(DateTimeOffset now, CancellationToken ct = default) => ExecuteWriteAsync(@"UPDATE import_reprocess
        SET status='cancelled',updated_at=@now WHERE status='pending'
        AND (NOT EXISTS (SELECT 1 FROM import_coverage s WHERE s.identity=import_reprocess.identity)
        OR EXISTS (SELECT 1 FROM import_coverage s WHERE s.identity=import_reprocess.identity
         AND (json_extract(s.payload,'$.Exclusion')<>'' OR json_extract(s.payload,'$.SnapshotStatus')='success')))
        AND NOT EXISTS
        (SELECT 1 FROM import_coverage s,json_each(s.payload,'$.Items') e
         WHERE s.identity=import_reprocess.identity AND json_extract(e.value,'$.Key')=import_reprocess.episode_key
         AND json_extract(s.payload,'$.Exclusion')='' AND json_extract(s.payload,'$.SnapshotStatus')='success'
         AND json_extract(e.value,'$.Expected')=1 AND json_extract(e.value,'$.Eligible')=1
         AND json_extract(e.value,'$.State') NOT IN ('excluded','index_mismatch')
         AND json_extract(e.value,'$.Failure') IN ('source_unavailable','transport_failure','lookup_deadline','http_429','publication_failed','slice_cancelled'));",
        c => BindText(c, "@now", now.ToString("o")), ct);

    // Emby SQLite binds empty strings as NULL; the initial/wrapped cursor must still scan.
    public Task<List<CatalogItem>> GetImportCatalogPageAsync(string after, int limit = 50) => QueryListAsync(
        "SELECT * FROM catalog_items WHERE id > COALESCE(@after, '') ORDER BY id LIMIT @limit;",
        c => { BindText(c, "@after", after); BindInt(c, "@limit", limit); }, ReadCatalogItem);

    public Task<List<CatalogItem>> GetExistingImportCatalogPageAsync(string after, int limit = 1000) => QueryListAsync(
        "SELECT * FROM catalog_items WHERE id > COALESCE(@after, '') AND strm_path IS NOT NULL AND strm_path<>'' ORDER BY id LIMIT @limit;",
        c => { BindText(c, "@after", after); BindInt(c, "@limit", limit); }, ReadCatalogItem);

    public Task<List<CatalogItem>> GetDueImportCatalogAsync(DateTimeOffset now) => QueryListAsync(
        @"SELECT c.* FROM import_coverage s JOIN catalog_items c ON c.id=json_extract(s.payload,'$.CatalogIds[0]')
          WHERE json_extract(s.payload,'$.Exclusion')=''
          AND EXISTS (SELECT 1 FROM json_each(s.payload,'$.Items') e
            WHERE json_extract(e.value,'$.Eligible')=1
            AND json_extract(e.value,'$.State') IN ('missing','retrying','awaiting_indexing','resolving')
            AND (json_extract(e.value,'$.NextAttempt') IS NULL OR json_extract(e.value,'$.NextAttempt')<=@now)
            AND (json_extract(e.value,'$.LeaseUntil') IS NULL OR json_extract(e.value,'$.LeaseUntil')<=@now))
          ORDER BY s.checked_at,c.id LIMIT 10;", c => BindText(c, "@now", now.ToString("o")), ReadCatalogItem);

    public Task<List<CatalogItem>> GetDiversityImportCatalogAsync(DateTimeOffset now) => QueryListAsync(
        @"SELECT c.* FROM import_coverage s JOIN catalog_items c ON c.id=json_extract(s.payload,'$.CatalogIds[0]')
          WHERE json_extract(s.payload,'$.Exclusion')=''
          AND EXISTS (SELECT 1 FROM json_each(s.payload,'$.Items') e
            WHERE json_extract(e.value,'$.Diversity.Status') IN ('waiting','in_flight')
            AND julianday(json_extract(e.value,'$.Diversity.NextAttempt'))<=julianday(@now)
            AND (json_extract(e.value,'$.LeaseUntil') IS NULL OR julianday(json_extract(e.value,'$.LeaseUntil'))<=julianday(@now)))
          ORDER BY s.checked_at,c.id LIMIT 10;", c => BindText(c, "@now", now.ToString("o")), ReadCatalogItem);

    // Revisit unfinished existing versions before advancing the catalog cursor. Failed
    // episodes retain their own backoff; successfully refreshed siblings leave this queue.
    public Task<List<CatalogItem>> GetCatchUpImportCatalogAsync(DateTimeOffset started, DateTimeOffset now) => QueryListAsync(
        @"SELECT c.* FROM import_coverage s JOIN catalog_items c ON c.id=json_extract(s.payload,'$.CatalogIds[0]')
          WHERE json_extract(s.payload,'$.Exclusion')=''
          AND json_extract(s.payload,'$.SnapshotStatus')='success'
          AND (EXISTS (SELECT 1 FROM json_each(s.payload,'$.Items') e
            WHERE json_extract(e.value,'$.Expected')=1 AND json_extract(e.value,'$.ObservedAt') IS NULL
            AND json_extract(e.value,'$.Key')>json_extract(s.payload,'$.Cursor'))
          OR EXISTS (SELECT 1 FROM json_each(s.payload,'$.Items') e
            WHERE json_extract(e.value,'$.Eligible')=1 AND json_extract(e.value,'$.State') IN ('indexed','awaiting_indexing','indexing_attention')
            AND (json_extract(e.value,'$.LastVersionRefresh') IS NULL OR julianday(json_extract(e.value,'$.LastVersionRefresh'))<julianday(@started))
            AND (json_extract(e.value,'$.NextAttempt') IS NULL OR json_extract(e.value,'$.NextAttempt')<=@now)))
          ORDER BY s.checked_at,c.id LIMIT 10;",
        c => { BindText(c, "@started", started.ToString("o")); BindText(c, "@now", now.ToString("o")); }, ReadCatalogItem);

    public Task<CatalogItem?> GetImportCatalogByIdAsync(string id) => QuerySingleAsync(
        "SELECT * FROM catalog_items WHERE id=@id;", c => BindText(c, "@id", id), ReadCatalogItem);

    public Task<List<CatalogItem>> GetImportCatalogAliasesAsync(CatalogItem item) => QueryListAsync(
        "SELECT * FROM catalog_items WHERE (lower(aio_id)=lower(@id) OR (tmdb_id IS NOT NULL AND tmdb_id<>'' AND tmdb_id=@tmdb)) AND ((media_type IN ('series','anime'))=@series);",
        c => { BindText(c, "@id", item.AioId); BindText(c, "@tmdb", item.TmdbId ?? ""); BindInt(c, "@series", Services.ImportInventory.IsSeries(item) ? 1 : 0); }, ReadCatalogItem);

    public async Task<string?> FindImportIdentityAsync(IEnumerable<string> aliases, CancellationToken ct)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alias in aliases)
        {
            ct.ThrowIfCancellationRequested();
            var row = await QuerySingleAsync("SELECT identity FROM import_aliases WHERE alias=@a;",
                c => BindText(c, "@a", alias), r => new IdentityRow(r.GetString(0)));
            if (row != null) identities.Add(row.Value);
        }
        if (identities.Count > 1) throw new InvalidOperationException("identity_conflict");
        return identities.FirstOrDefault();
    }

    public Task SaveImportAliasAsync(string alias, string identity, CancellationToken ct) => ExecuteWriteAsync(
        "INSERT INTO import_aliases(alias,identity) VALUES(@a,@id) ON CONFLICT(alias) DO NOTHING;",
        c => { BindText(c, "@a", alias); BindText(c, "@id", identity); }, ct);

    public Task<int> GetRecentImportAttemptsAsync(DateTimeOffset now) => QueryScalarIntAsync(
        "SELECT COUNT(*) FROM import_attempts WHERE attempted_at >= @since;",
        c => BindText(c, "@since", now.AddDays(-1).ToString("o")));

    public async Task<DateTimeOffset?> GetNextImportCreditAsync(DateTimeOffset now, int allowance)
    {
        var value = await QuerySingleAsync(@"SELECT attempted_at FROM import_attempts
            WHERE attempted_at>=@since AND (SELECT COUNT(*) FROM import_attempts WHERE attempted_at>=@since)>=@limit
            ORDER BY attempted_at LIMIT 1 OFFSET MAX(0,(SELECT COUNT(*) FROM import_attempts WHERE attempted_at>=@since)-@limit);",
            c => { BindText(c, "@since", now.AddDays(-1).ToString("o")); BindInt(c, "@limit", Math.Max(1, allowance)); }, r => r.GetString(0));
        return DateTimeOffset.TryParse(value, out var at) ? at.AddDays(1) : null;
    }

    public async Task SaveImportRunReportAsync(string id, DateTimeOffset finished, string report, CancellationToken ct)
    {
        await ExecuteWriteAsync("INSERT OR REPLACE INTO import_run_history(id,finished_at,payload) VALUES(@id,@at,@report);",
            c => { BindText(c, "@id", id); BindText(c, "@at", finished.ToString("o")); BindText(c, "@report", report); }, ct);
        // Analytics-only retention. The independent attempt ledger is never reset.
        await ExecuteWriteAsync("DELETE FROM import_run_history WHERE finished_at<@at;",
            c => BindText(c, "@at", finished.AddDays(-7).ToString("o")), ct);
    }
    public async Task<List<string>> GetImportRunHistoryAsync(int limit = 12)
    {
        if (await QueryScalarIntAsync("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='import_run_history';", _ => { }) == 0)
            return new();
        return await QueryListAsync("SELECT payload FROM import_run_history ORDER BY finished_at DESC,id LIMIT @limit;",
            c => BindInt(c, "@limit", Math.Clamp(limit, 1, 48)), r => r.GetString(0));
    }

    public async Task RecordImportAttemptAsync(string id, DateTimeOffset now, CancellationToken ct)
    {
        await ExecuteWriteAsync("INSERT INTO import_attempts(id,attempted_at) VALUES(@id,@at);",
            c => { BindText(c, "@id", id); BindText(c, "@at", now.ToString("o")); }, ct);
        await ExecuteWriteAsync("DELETE FROM import_attempts WHERE attempted_at < @at;",
            c => BindText(c, "@at", now.AddDays(-2).ToString("o")), ct);
    }

    public Task<List<(int Id, string CollectionName, string AioId, string Source, string? UserId)>> GetImportCollectionPageAsync(int after) => QueryListAsync(
        "SELECT id,collection_name,aio_id,source,user_id FROM collection_membership WHERE id>@after AND (user_id IS NULL OR emby_item_id IS NULL) ORDER BY id LIMIT 50;",
        c => BindInt(c, "@after", after), r => (r.GetInt(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));

    public Task<string?> GetImportCollectionIdAsync(string name) => QuerySingleAsync(
        "SELECT emby_collection_id FROM collections WHERE name=@name AND emby_collection_id IS NOT NULL GROUP BY name HAVING COUNT(*)=1;",
        c => BindText(c, "@name", name), r => r.GetString(0));

    private sealed record IdentityRow(string Value);
}
