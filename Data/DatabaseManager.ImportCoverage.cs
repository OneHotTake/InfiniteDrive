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
