Sprint 214 — Settings Redesign Backend Prerequisites

Status: Draft | Risk: MED | Depends: Sprint 213 | Target: v0.54

Why

The v2.3 settings redesign (Sprint 215) requires five targeted backend changes before the UI can be implemented correctly: safe two-phase key rotation, a rotation status endpoint, a local catalog search endpoint, updated block endpoint to use internal IDs, and EnableBackupAioStreams simplification.

Non-Goals

No database schema changes
No changes to playback logic, stream resolution, or task scheduling
No changes to any endpoint not listed below
Tasks

FIX-214-01: Add PluginSecretRotatedAt to PluginConfiguration.cs

Files: PluginConfiguration.cs (modify)
Effort: S
What: Add one property to record when the secret was last rotated. Set it in the rotate handler.

csharp
Copy
/// <summary>
/// Unix timestamp (seconds) of the last PluginSecret rotation.
/// 0 = never rotated (auto-generated on first load).
/// </summary>
public long PluginSecretRotatedAt { get; set; } = 0;
FIX-214-02: Two-phase safe rotation in SetupService.RotateApiKey

Files: Services/SetupService.cs (modify)
Effort: M
What: Change RotateApiKeyRequest handler from synchronous single-step to a safe two-phase flow. The existing RotateApiKey endpoint already rewrites .strm files atomically (tmp → rename). Enhance it to:

Generate new secret (do not save to PluginSecret yet)
Rewrite all .strm files using the new secret (atomic writes — existing pattern)
Only after all files are written successfully: set PluginSecret = newSecret, set PluginSecretRotatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), call Plugin.Instance.SaveConfiguration()
Return { Success: true, RotatedAt: <unix timestamp> }
If any file write fails: abort, do not update PluginSecret. Return { Success: false, Error: "..." }.

Gotcha: The existing handler may already do most of this — audit it first. The key invariant is: PluginSecret must never be updated until all .strm files have been written with the new secret.

FIX-214-03: Add GET /InfiniteDrive/Setup/RotationStatus endpoint

Files: Services/SetupService.cs (modify)
Effort: S
What: Add a lightweight status endpoint the UI polls during rotation.

csharp
Copy
public class RotationStatusRequest : IReturn<RotationStatusResponse> { }

public class RotationStatusResponse
{
    public bool IsRotating { get; set; }
    public int FilesTotal { get; set; }
    public int FilesWritten { get; set; }
    public long LastRotatedAt { get; set; } // unix timestamp, 0 if never
}

public object Get(RotationStatusRequest req)
{
    AdminGuard.RequireAdmin(_authCtx, Request);
    var cfg = Plugin.Instance!.Configuration;
    return new RotationStatusResponse
    {
        IsRotating    = _rotationInProgress, // static bool field on service
        FilesTotal    = _rotationTotal,
        FilesWritten  = _rotationWritten,
        LastRotatedAt = cfg.PluginSecretRotatedAt
    };
}
Use a static (or singleton-scoped) _rotationInProgress / _rotationTotal / _rotationWritten on SetupService — these are only written by RotateApiKey and read by RotationStatus. No persistence needed; in-memory is correct.

FIX-214-04: Add GET /InfiniteDrive/Admin/SearchItems endpoint

Files: Services/AdminService.cs (modify)
Effort: M
What: Local catalog search for the block UI. Searches only InfiniteDrive-managed items in media_items. Does not call any external API.

csharp
Copy
public class SearchItemsRequest : IReturn<SearchItemsResponse>
{
    [ApiMember(Name = "q")] public string Query { get; set; } = "";
    [ApiMember(Name = "limit")] public int Limit { get; set; } = 5;
}

public class SearchItemDto
{
    public string Id { get; set; }        // internal UUID from media_items
    public string Title { get; set; }
    public int? Year { get; set; }
    public string MediaType { get; set; } // "movie" | "series" | "anime"
    // Best available external ID for display (imdb > tvdb > tmdb > kitsu > mal)
    public string? DisplayExternalId { get; set; }
    public string? DisplayExternalIdType { get; set; }
}

public class SearchItemsResponse
{
    public List<SearchItemDto> Items { get; set; } = new();
}
Query: SELECT id, title, year, media_type FROM media_items WHERE title LIKE '%{q}%' AND blocked = 0 LIMIT {limit} — then for each result, join media_item_ids to get the best available external ID for display (priority: imdb > tvdb > tmdb > kitsu > mal — use whatever is available, not required).

Gotcha: Use parameterised query, not string interpolation. Require admin auth via AdminGuard.RequireAdmin.

FIX-214-05: Update POST /InfiniteDrive/Admin/BlockItems to accept internal IDs

Files: Services/AdminService.cs (modify)
Effort: S
What: The existing BlockItems endpoint currently accepts ImdbIds: string[]. Change it to also accept ItemIds: string[] (internal UUIDs from media_items.id). Support both for backwards compatibility — if ItemIds is provided, prefer it; if only ImdbIds is provided, look up the internal ID first via SELECT id FROM media_items WHERE primary_id = @imdbId.

csharp
Copy
public class BlockItemsRequest : IReturn<BlockItemsResponse>
{
    public List<string>? ItemIds { get; set; }   // internal UUIDs (preferred)
    public List<string>? ImdbIds { get; set; }   // legacy, still supported
}
Existing block action side effects (delete .strm, delete .nfo, trigger library scan) remain unchanged.

FIX-214-06: Simplify EnableBackupAioStreams — derive from URL presence

Files: Services/AioStreamsClient.cs (modify)
Effort: S
What: Change the backup fallback condition to use URL presence rather than the explicit flag. This means users no longer need to toggle a checkbox — filling in a backup URL is sufficient.

Find:

csharp
Copy
if (string.IsNullOrWhiteSpace(baseUrl) && config.EnableBackupAioStreams)
    (baseUrl, uuid, token) = TryParseManifestUrl(config.SecondaryManifestUrl);
Replace with:

csharp
Copy
if (string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
    (baseUrl, uuid, token) = TryParseManifestUrl(config.SecondaryManifestUrl);
EnableBackupAioStreams is now unused by the backend. Leave the property in PluginConfiguration.cs to avoid XML deserialization errors on existing configs — just don't read it. The UI in Sprint 215 will stop saving it. It will naturally be false for all users and can be removed in a future cleanup sprint.

Verification

 dotnet build -c Release (0 errors, 0 warnings)
 ./emby-reset.sh succeeds + plugin loads
 Manual test: POST /InfiniteDrive/Setup/RotateApiKey → confirm all .strm files rewritten, PluginSecretRotatedAt updated in XML, old secret no longer active
 Manual test: GET /InfiniteDrive/Setup/RotationStatus returns { IsRotating: false, LastRotatedAt: <recent timestamp> } after rotation
 Manual test: GET /InfiniteDrive/Admin/SearchItems?q=dune returns results from local catalog only, each with Id (UUID) field
 Manual test: POST /InfiniteDrive/Admin/BlockItems with { "ItemIds": ["<uuid-from-search>"] } blocks item and deletes .strm
 Manual test: Populate SecondaryManifestUrl without setting EnableBackupAioStreams = true → confirm backup is used on primary failure
 `grep -r "EnableBackupA
