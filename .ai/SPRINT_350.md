# Sprint 350 — Critical Audit Remediation (One-Shot Fix)

**Status:** Draft | **Risk:** HIGH | **Depends:** none | **Target:** v0.62

## Why (2 sentences max)
Combined Claude/GLM audit identified 12 critical bugs including a showstopper inverted-logic bug in ManifestFetcher that breaks all catalog sync, a circuit breaker that never closes, and multiple security vulnerabilities. These must be fixed before any production deployment.

## Non-Goals
- Refactoring HA architecture (separate sprint)
- Adding provider scoring/latency tracking
- Implementing full CSRF token system (medium-term)

---

## Tasks

### FIX-350-01: ManifestFetcher inverted logic (SHOWSTOPPER)
**Files:** Services/ManifestFetcher.cs (modify)
**Effort:** S
**What:** Line ~87: Change `if (!string.IsNullOrEmpty(source.Url))` to `if (string.IsNullOrEmpty(source.Url))` — currently skips sources WITH valid URLs.

```csharp
// BEFORE (broken):
if (!string.IsNullOrEmpty(source.Url))
{
    _logger.LogWarning("[ManifestFetcher] Source {Name} has no URL, skipping", source.Name);
    continue;
}

// AFTER (fixed):
if (string.IsNullOrEmpty(source.Url))
{
    _logger.LogWarning("[ManifestFetcher] Source {Name} has no URL, skipping", source.Name);
    continue;
}
```

---

### FIX-350-02: Circuit breaker RecordSuccess doesn't close circuit
**Files:** Services/ResolverHealthTracker.cs (modify)
**Effort:** S
**What:** Add `state.CircuitState = CircuitState.Closed;` and `state.BackoffIndex = 0;` in `RecordSuccess()` — currently logs "closing circuit" but never actually closes it.

```csharp
public void RecordSuccess(string resolverName)
{
    lock (_lock)
    {
        if (_states.TryGetValue(resolverName, out var state))
        {
            state.ConsecutiveFailures = 0;
            state.CircuitState = CircuitState.Closed;  // ADD THIS
            state.BackoffIndex = 0;                     // ADD THIS - reset backoff
            _logger.LogDebug(
                "[CircuitBreaker] Resolver {Resolver} recovered, circuit closed",
                resolverName);
        }
    }
}
```

---

### FIX-350-03: PluginSecret empty bypass — fail closed
**Files:** Services/PlaybackTokenService.cs (modify)
**Effort:** S
**What:** Change `Sign()` and `Verify()` to return failure when secret is empty instead of bypassing auth entirely.

```csharp
// In Sign() - line ~37:
// BEFORE:
if (string.IsNullOrEmpty(pluginSecret))
    return url; // Return unsigned if no secret configured

// AFTER:
if (string.IsNullOrEmpty(pluginSecret))
    throw new InvalidOperationException("PluginSecret not configured - cannot sign URLs");

// In Verify() - line ~58:
// BEFORE:
if (string.IsNullOrEmpty(pluginSecret))
    return true; // Allow unsigned if no secret configured

// AFTER:
if (string.IsNullOrEmpty(pluginSecret))
    return false; // Fail closed - no secret = no valid signatures
```

---

### FIX-350-04: Probe timeout breaks fallback loop
**Files:** Services/ResolverService.cs (modify)
**Effort:** S
**What:** In `ProbeAndReorderAsync()` (~line 245), change `break;` to `continue;` on `OperationCanceledException` — currently stops probing all remaining candidates when budget expires on one.

```csharp
// BEFORE:
catch (OperationCanceledException)
{
    // Budget exhausted — give benefit of the doubt to remaining
    dead.Add(stream);
    break;  // ← BUG: stops probing remaining candidates
}

// AFTER:
catch (OperationCanceledException)
{
    // Budget exhausted for this probe — move to next, don't kill remaining
    dead.Add(stream);
    continue;  // ← FIX: continue to next candidate
}
```

---

### FIX-350-05: SeriesGapRepairService verification returns true on exception
**Files:** Services/SeriesGapRepairService.cs (modify)
**Effort:** S
**What:** In `VerifyUpstreamHasStreamsAsync()` (~line 272), change exception handler to return `false` instead of `true` — currently writes ghost .strm files when AIOStreams is down.

```csharp
// BEFORE:
catch (Exception ex)
{
    _logger.LogDebug(ex, "[GapRepair] Verification failed for {ImdbId}", imdbId);
    return true;  // ← BUG: assumes streams exist on error
}

// AFTER:
catch (Exception ex)
{
    _logger.LogWarning(ex, "[GapRepair] Verification failed for {ImdbId} — failing closed", imdbId);
    return false;  // ← FIX: fail closed, don't write unverified .strm
}
```

---

### FIX-350-06: Path traversal in SetupService
**Files:** Services/SetupService.cs (modify)
**Effort:** S
**What:** Add path validation before `Directory.CreateDirectory()` (~line 159-204) to reject paths containing `..` or absolute paths outside expected roots.

```csharp
// Add helper method:
private static bool IsPathSafe(string path, string[] allowedRoots)
{
    if (string.IsNullOrWhiteSpace(path))
        return false;
    
    // Reject path traversal attempts
    var normalized = Path.GetFullPath(path);
    if (path.Contains("..") || !normalized.Equals(Path.GetFullPath(normalized), StringComparison.OrdinalIgnoreCase))
        return false;
    
    // Must be under an allowed root (if specified)
    if (allowedRoots?.Length > 0)
    {
        return allowedRoots.Any(root => 
            normalized.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
    }
    
    return true;
}

// In CreateDirectories or similar method, before Directory.CreateDirectory:
if (!IsPathSafe(moviesPath, null) || !IsPathSafe(showsPath, null))
{
    _logger.LogError("[Setup] Invalid path detected — possible path traversal attempt");
    return new SetupResponse { Success = false, Error = "Invalid directory path" };
}
```

---

### FIX-350-07: WriteStrmFileAsync is placeholder (ItemPipelineService)
**Files:** Services/ItemPipelineService.cs (modify)
**Effort:** M
**What:** Replace placeholder with actual implementation using `StrmWriterService`. The stub currently logs "Would write" but never writes anything.

```csharp
// BEFORE (placeholder):
private Task WriteStrmFileAsync(MediaItem item, CancellationToken ct)
{
    _logger.LogInformation("[ItemPipeline] Would write .strm file for {MediaId}",
        item.PrimaryId.ToString());
    return Task.CompletedTask;
}

// AFTER (implemented):
private async Task WriteStrmFileAsync(MediaItem item, CancellationToken ct)
{
    var config = Plugin.Instance?.Configuration;
    if (config == null)
    {
        _logger.LogError("[ItemPipeline] Configuration not available for .strm write");
        throw new InvalidOperationException("Plugin configuration not initialized");
    }

    var strmWriter = new StrmWriterService(
        new EmbyLoggerAdapter<StrmWriterService>(Plugin.Instance!.LogManager.GetLogger("StrmWriter")),
        Plugin.Instance.LogManager,
        _db);

    // Convert MediaItem to CatalogItem for StrmWriterService
    var catalogItem = await _db.GetCatalogItemByImdbIdAsync(item.PrimaryId.Value);
    if (catalogItem == null)
    {
        _logger.LogWarning("[ItemPipeline] No catalog item found for {MediaId}", item.PrimaryId);
        return;
    }

    var strmPath = await strmWriter.WriteAsync(
        catalogItem,
        SourceType.Aio,
        null,  // userId - not user-specific
        ct);

    if (strmPath != null)
    {
        item.StrmPath = strmPath;
        _logger.LogInformation("[ItemPipeline] Wrote .strm file for {MediaId}: {Path}",
            item.PrimaryId, strmPath);
    }
    else
    {
        _logger.LogWarning("[ItemPipeline] Failed to write .strm file for {MediaId}",
            item.PrimaryId);
    }
}
```

---

### FIX-350-08: StreamResolver series returns empty unconditionally
**Files:** Services/StreamResolver.cs (modify)
**Effort:** M
**What:** Implement series stream resolution using `AioStreamsClient.GetSeriesStreamsAsync()` instead of returning empty list.

```csharp
// BEFORE:
else if (item.MediaType.Equals("series", StringComparison.OrdinalIgnoreCase))
{
    // For series, we need to resolve each episode
    // For now, just return empty list - will be implemented in sprint 112
    _logger.LogDebug("[StreamResolver] Series resolution not yet implemented");
    return new List<StreamCandidate>();
}

// AFTER:
else if (item.MediaType.Equals("series", StringComparison.OrdinalIgnoreCase))
{
    // Series requires season/episode - check if available
    if (!item.Season.HasValue || !item.Episode.HasValue)
    {
        _logger.LogWarning("[StreamResolver] Series {MediaId} missing season/episode", 
            item.PrimaryId);
        return new List<StreamCandidate>();
    }
    
    var response = await _client.GetSeriesStreamsAsync(
        item.PrimaryId.Value, 
        item.Season.Value, 
        item.Episode.Value, 
        ct);
    if (response?.Streams != null)
        streams.AddRange(response.Streams);
}
```

---

### FIX-350-09: Provider state persistence across restarts
**Files:** Data/DatabaseManager.cs (modify), Services/StreamResolutionHelper.cs (modify), Plugin.cs (modify)
**Effort:** M
**What:** Add `active_provider` column to `plugin_state` table, persist `ActiveProviderState` on change, restore on startup.

```csharp
// In DatabaseManager.cs - add new methods:
public async Task SetActiveProviderAsync(string provider, CancellationToken ct = default)
{
    await PersistMetadataAsync("active_provider", provider, ct);
}

public async Task<string?> GetActiveProviderAsync(CancellationToken ct = default)
{
    return await GetMetadataAsync("active_provider", ct);
}

// In StreamResolutionHelper.cs - after failover switch (~line 107):
if (providerKey == "Secondary")
{
    var state = Plugin.Instance?.ActiveProviderState;
    if (state?.Current != Models.ActiveProvider.Secondary)
    {
        state!.Current = Models.ActiveProvider.Secondary;
        logger.LogWarning("[Failover] Primary unavailable, switched to Secondary");
        
        // Persist the failover state
        _ = Task.Run(async () => {
            try { await Plugin.Instance!.DatabaseManager.SetActiveProviderAsync("Secondary"); }
            catch { /* best effort */ }
        });
    }
}

// In Plugin.cs - InfiniteDriveInitializationService.Run() or InitialiseDatabaseManager():
// After database init, restore provider state:
try
{
    var savedProvider = await DatabaseManager.GetActiveProviderAsync();
    if (savedProvider == "Secondary")
    {
        ActiveProviderState.Current = Models.ActiveProvider.Secondary;
        _logger.LogInformation("[InfiniteDrive] Restored ActiveProvider=Secondary from database");
    }
}
catch (Exception ex)
{
    _logger.LogDebug(ex, "[InfiniteDrive] Could not restore active provider state");
}
```

---

### FIX-350-10: Circuit breaker state persistence across restarts
**Files:** Data/DatabaseManager.cs (modify), Services/ResolverHealthTracker.cs (modify)
**Effort:** M
**What:** Persist circuit states to `plugin_state` as JSON on open/close, restore on startup.

```csharp
// In ResolverHealthTracker.cs - add persistence:
private readonly DatabaseManager? _db;

public ResolverHealthTracker(ILogger logger, DatabaseManager? db = null)
{
    _logger = logger;
    _db = db;
}

// Add method to persist state:
private async Task PersistStateAsync()
{
    if (_db == null) return;
    try
    {
        var snapshot = new Dictionary<string, object>();
        lock (_lock)
        {
            foreach (var kvp in _states)
            {
                snapshot[kvp.Key] = new
                {
                    kvp.Value.CircuitState,
                    kvp.Value.CircuitOpenUntil,
                    kvp.Value.ConsecutiveFailures,
                    kvp.Value.BackoffIndex
                };
            }
        }
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        await _db.PersistMetadataAsync("circuit_breaker_state", json);
    }
    catch { /* best effort */ }
}

// Add method to restore state (call from constructor or Init):
public async Task RestoreStateAsync()
{
    if (_db == null) return;
    try
    {
        var json = await _db.GetMetadataAsync("circuit_breaker_state");
        if (string.IsNullOrEmpty(json)) return;
        
        // Parse and restore... (deserialize and populate _states)
        _logger.LogInformation("[CircuitBreaker] Restored circuit state from database");
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "[CircuitBreaker] Could not restore circuit state");
    }
}

// Call PersistStateAsync() at end of RecordFailure() when circuit opens,
// and at end of RecordSuccess() when circuit closes
```

---

### FIX-350-11: IMDB ID format validation
**Files:** Services/IdResolverService.cs (modify)
**Effort:** S
**What:** Add regex validation for IMDB IDs - must be `tt` followed by 7-8 digits.

```csharp
// Add validation helper:
private static readonly Regex ImdbIdPattern = new Regex(
    @"^tt\d{7,8}$", 
    RegexOptions.Compiled | RegexOptions.IgnoreCase);

private static bool IsValidImdbId(string? id)
{
    return !string.IsNullOrEmpty(id) && ImdbIdPattern.IsMatch(id);
}

// In ResolveAsync() - line ~75, after fast path check:
if (lower.StartsWith("tt", StringComparison.Ordinal))
{
    if (!IsValidImdbId(manifestId))
    {
        _logger.LogWarning("[IdResolver] Invalid IMDB ID format: {Id}", manifestId);
        return new ResolvedIds(manifestId, null, null, null, null, null);
    }
    imdbId = manifestId;
    _logger.LogDebug("[IdResolver] Fast path: {Id} is already a tt ID", manifestId);
    return new ResolvedIds(manifestId, imdbId, tmdbId, tvdbId, aniDbId, null);
}
```

---

### FIX-350-12: Rate limiter X-Forwarded-For spoofing prevention
**Files:** Services/RateLimiter.cs (modify)
**Effort:** S
**What:** Only trust X-Forwarded-For when configured trusted proxy is present, otherwise use direct connection IP.

```csharp
// Add configuration property (or use existing trusted IPs):
private readonly bool _trustForwardedHeaders;

public RateLimiter(ILogger<RateLimiter> logger, string[] trustedIps, bool trustForwardedHeaders = false)
{
    _logger = logger;
    _trustedIps = trustedIps ?? Array.Empty<string>();
    _trustForwardedHeaders = trustForwardedHeaders;
}

// Modify GetClientIp():
public static string? GetClientIp(IRequest request, bool trustForwardedHeaders = false)
{
    // Only trust forwarded headers if explicitly configured
    if (trustForwardedHeaders)
    {
        var forwarded = request.Headers["X-Forwarded-For"];
        if (!string.IsNullOrEmpty(forwarded))
        {
            var firstIp = forwarded.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(firstIp))
                return firstIp;
        }

        var realIp = request.Headers["X-Real-IP"];
        if (!string.IsNullOrEmpty(realIp))
            return realIp;
    }

    // Fall back to direct connection IP
    return request.RemoteIp;
}
```

---

### FIX-350-13: M3U8 upstream fetch needs fallback on 5xx/timeout
**Files:** Services/StreamEndpointService.cs (modify)
**Effort:** M
**What:** When upstream M3U8 fetch fails, try next candidate from stream_candidates table instead of returning 502.

```csharp
// In HandleAsync(), after M3U8 fetch attempt (~line 141-145):
// BEFORE:
if (isM3U8)
{
    // 4a. Fetch upstream M3U8
    // ... fetch code that returns 502 on failure
}

// AFTER:
if (isM3U8)
{
    var fetchResult = await TryFetchM3U8WithFallbackAsync(upstreamUrl, req);
    if (fetchResult.Success)
    {
        return new
        {
            ContentType = "application/vnd.apple.mpegurl",
            Content = fetchResult.Content,
            StatusCode = 200
        };
    }
    
    // All candidates exhausted
    _logger.LogWarning("[Stream] All M3U8 fetch attempts failed for {Url}", upstreamUrl);
    return Error(502, "upstream_unavailable", "Stream temporarily unavailable - try again");
}

// Add helper method:
private async Task<(bool Success, string? Content)> TryFetchM3U8WithFallbackAsync(
    string primaryUrl, StreamEndpointRequest req)
{
    // Try primary URL first
    var result = await TryFetchM3U8Async(primaryUrl);
    if (result.Success)
        return result;
    
    // Extract IMDB ID from signed URL to look up fallback candidates
    // ... implementation to get fallback URLs from stream_candidates
    // ... try each fallback in order
    
    return (false, null);
}
```

---

### FIX-350-14: DB write failure in StreamResolutionHelper needs try-catch
**Files:** Services/StreamResolutionHelper.cs (modify)
**Effort:** S
**What:** Wrap `UpsertResolutionResultAsync` in try-catch to prevent resolution loss on transient DB errors.

```csharp
// In SyncResolveViaProvidersAsync(), around line 93-94:
// BEFORE:
await db.UpsertResolutionResultAsync(entry, candidates);
await db.IncrementApiCallCountAsync();

// AFTER:
try
{
    await db.UpsertResolutionResultAsync(entry, candidates);
    await db.IncrementApiCallCountAsync();
}
catch (Exception ex)
{
    logger.LogError(ex, "[InfiniteDrive] Failed to cache resolution for {Imdb} — resolution succeeded but not cached", req.Imdb);
    // Continue - we still have the resolution, just won't be cached
}
```

---

## Verification (run these or it fails)

- [ ] `dotnet build -c Release` (0 errors/warnings)
- [ ] `./emby-reset.sh` succeeds + Discover UI loads
- [ ] **FIX-01 Test:** CatalogSync runs → items appear in catalog_items table (was empty before)
- [ ] **FIX-02 Test:** Kill AIOStreams → 3 failures → circuit opens → restore AIOStreams → next success → `ShouldSkip()` returns `false`
- [ ] **FIX-03 Test:** Clear PluginSecret → try to play → get explicit error (not silent pass-through)
- [ ] **FIX-05 Test:** During GapRepair, disconnect network → no new .strm files written
- [ ] **FIX-06 Test:** SetupService with `moviesPath: "/media/../etc"` → returns error, no directory created
- [ ] **FIX-09 Test:** Trigger failover → restart Emby → check ActiveProviderState is still Secondary
- [ ] **Manual:** Play movie → plays successfully with all fixes in place

## Completion
- [ ] All tasks done
- [ ] BACKLOG.md updated
- [ ] REPO_MAP.md updated
- [ ] git commit -m "chore: end sprint 350 — critical audit remediation"

---

## Risk Notes

| Fix | Risk | Mitigation |
|-----|------|------------|
| FIX-03 (PluginSecret fail closed) | Could break existing deployments with empty secret | Plugin already auto-generates secret on first run; only affects misconfigured installs |
| FIX-07 (WriteStrmFileAsync) | Untested integration with StrmWriterService | Verify StrmWriterService is initialized before ItemPipelineService runs |
| FIX-09/10 (Persistence) | Schema change if plugin_state doesn't exist | Use existing `PersistMetadataAsync` which handles table creation |
