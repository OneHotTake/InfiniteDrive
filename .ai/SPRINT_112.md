# Sprint 112 — Stream Resolution and Playback (v3.3)

**Version:** v3.3 | **Status:** Planning | **Risk:** MEDIUM | **Depends:** Sprint 111

---

## Overview

Sprint 112 implements stream resolution for playback, including cache management, URL signing, and SSE progress streaming. This sprint connects resolved streams to actual playback.

**Key Components:**
- PlaybackService - Stream resolution for .strm playback with ranked fallback hierarchy
- StreamCache - Caches resolved URLs with TTL (dual storage: primary + secondary)
- StreamUrlSigner - Signs stream URLs with HMAC
- ProgressStreamer - SSE progress events

---

## Phase 112A — PlaybackService

### FIX-112A-01: Create PlaybackService

**File:** `Services/PlaybackService.cs`

```csharp
public class PlaybackService : IStreamService
{
    private readonly StreamResolver _resolver;
    private readonly StreamCache _cache;
    private readonly StreamUrlSigner _signer;
    private readonly ILogger _logger;

    public async Task<string?> GetStreamUrlAsync(
        string mediaId,
        CancellationToken ct = default)
    {
        // RANKED FALLBACK HIERARCHY (try in order):

        // 1. Try primary cached URL
        var cachedUrl = await _cache.GetPrimaryAsync(mediaId, ct);
        if (cachedUrl != null)
        {
            _logger.Debug("Cache primary hit for {MediaId}", mediaId);
            return _signer.Sign(cachedUrl);
        }

        // 2. Try secondary cached URL
        var cachedUrl2 = await _cache.GetSecondaryAsync(mediaId, ct);
        if (cachedUrl2 != null)
        {
            _logger.Debug("Cache secondary hit for {MediaId}", mediaId);
            return _signer.Sign(cachedUrl2);
        }

        // 3. Live resolution from AIOStreams
        _logger.Info("Cache miss for {MediaId}, resolving live...", mediaId);
        var streams = await _resolver.ResolveStreamsAsync(mediaId, ct);

        if (streams == null || streams.Count == 0)
        {
            _logger.Warn("No streams found for {MediaId}", mediaId);
            return null;
        }

        // 4. Pick best stream by quality ranking
        var bestStream = streams
            .OrderByDescending(s => GetQualityRank(s.Quality))
            .ThenByDescending(s => s.Score)
            .First();

        _logger.Info("Selected stream: {Quality} from {Provider}",
            bestStream.Quality, bestStream.Provider);

        // 5. Return signed URL (DO NOT cache here - ItemPipelineService caches)
        return _signer.Sign(bestStream.Url);
    }

    private int GetQualityRank(StreamQuality quality)
    {
        return quality switch
        {
            StreamQuality.FHD_4K => 5,
            StreamQuality.FHD => 4,
            StreamQuality.HD => 3,
            StreamQuality.SD => 2,
            StreamQuality.LD => 1,
            _ => 0
        };
    }
}
```

**Acceptance Criteria:**
- [ ] Implements ranked fallback: cache primary → cache secondary → live resolution
- [ ] Checks both cache entries before live resolution
- [ ] DOES NOT cache in GetStreamUrlAsync (ItemPipelineService handles caching)
- [ ] Picks highest quality stream with score tiebreaker
- [ ] Signs URL with HMAC

**CRITICAL: NO CACHE WRITING IN GetStreamUrlAsync**

PlaybackService MUST NOT call `_cache.SetAsync()` or `_cache.SetPrimaryAsync()` or `_cache.SetSecondaryAsync()`. Cache writing is exclusively handled by ItemPipelineService during the item lifecycle (Resolved → Hydrated phases).

---

## Phase 112B — StreamCache

### FIX-112B-01: Create StreamCache

**File:** `Services/StreamCache.cs`

```csharp
public class StreamCache
{
    private readonly IDatabaseManager _db;
    private readonly ILogger _logger;
    private readonly TimeSpan _defaultTtl = TimeSpan.FromHours(24);

    public async Task<string?> GetPrimaryAsync(string mediaId, CancellationToken ct = default)
    {
        var entry = await _db.GetCachedStreamAsync(mediaId, ct);
        if (entry == null) return null;

        // Check if expired (24-hour TTL)
        if (DateTimeOffset.UtcNow > entry.ExpiresAt)
        {
            _logger.Debug("Cache entry for {MediaId} expired", mediaId);
            await InvalidateAsync(mediaId, ct);
            return null;
        }

        return entry.Url;
    }

    public async Task<string?> GetSecondaryAsync(string mediaId, CancellationToken ct = default)
    {
        var entry = await _db.GetCachedStreamAsync(mediaId, ct);
        if (entry == null || string.IsNullOrEmpty(entry.UrlSecondary)) return null;

        // Check if expired (24-hour TTL)
        if (DateTimeOffset.UtcNow > entry.ExpiresAt)
        {
            _logger.Debug("Cache entry for {MediaId} expired", mediaId);
            await InvalidateAsync(mediaId, ct);
            return null;
        }

        return entry.UrlSecondary;
    }

    public async Task SetPrimaryAsync(
        string mediaId,
        string url,
        CancellationToken ct = default,
        TimeSpan? ttl = null)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl ?? _defaultTtl);
        await _db.SetCachedStreamPrimaryAsync(mediaId, url, expiresAt, ct);
        _logger.Debug("Cached primary URL for {MediaId}, expires at {ExpiresAt}",
            mediaId, expiresAt);
    }

    public async Task SetSecondaryAsync(
        string mediaId,
        string url,
        CancellationToken ct = default,
        TimeSpan? ttl = null)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl ?? _defaultTtl);
        await _db.SetCachedStreamSecondaryAsync(mediaId, url, expiresAt, ct);
        _logger.Debug("Cached secondary URL for {MediaId}, expires at {ExpiresAt}",
            mediaId, expiresAt);
    }

    public async Task InvalidateAsync(string mediaId, CancellationToken ct = default)
    {
        await _db.DeleteCachedStreamAsync(mediaId, ct);
        _logger.Debug("Invalidated cache for {MediaId}", mediaId);
    }

    public async Task PurgeExpiredAsync(CancellationToken ct = default)
    {
        var count = await _db.PurgeExpiredCacheAsync(ct);
        _logger.Info("Purged {Count} expired cache entries", count);
    }
}
```

**Acceptance Criteria:**
- [ ] Retrieves primary cached URL (Url field)
- [ ] Retrieves secondary cached URL (UrlSecondary field)
- [ ] Respects 24-hour TTL
- [ ] Invalidates expired entries
- [ ] Sets primary URL independently
- [ ] Sets secondary URL independently
- [ ] Purges all expired entries

---

## Phase 112C — StreamUrlSigner

### FIX-112C-01: Create StreamUrlSigner

**File:** `Services/StreamUrlSigner.cs`

```csharp
public class StreamUrlSigner
{
    private readonly PluginConfiguration _config;

    public string Sign(string url)
    {
        if (string.IsNullOrEmpty(_config.PluginSecret))
        {
            // Return unsigned if no secret configured
            return url;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var message = $"{url}|{timestamp}";
        var signature = ComputeHmac(message, _config.PluginSecret);

        return $"{url}|{timestamp}|{signature}";
    }

    public bool Verify(string signedUrl)
    {
        if (string.IsNullOrEmpty(_config.PluginSecret))
            return true; // Allow unsigned if no secret configured

        var parts = signedUrl.Split('|');
        if (parts.Length != 3) return false;

        var url = parts[0];
        var timestamp = long.Parse(parts[1]);
        var signature = parts[2];

        // Check timestamp (reject if older than 1 hour)
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (age > TimeSpan.FromHours(1))
        {
            return false;
        }

        // Verify signature
        var message = $"{url}|{timestamp}";
        var expectedSignature = ComputeHmac(message, _config.PluginSecret);

        return signature == expectedSignature;
    }

    private string ComputeHmac(string message, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string GenerateSecret()
    {
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[32];
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
```

**Acceptance Criteria:**
- [ ] Signs URLs with HMAC-SHA256
- [ ] Includes timestamp for expiration
- [ ] Verifies signatures
- [ ] Rejects expired signatures (> 1 hour)
- [ ] Generates random secrets

---

## Phase 112D — ProgressStreamer

### FIX-112D-01: Create ProgressStreamer

**File:** `Services/ProgressStreamer.cs`

```csharp
public class ProgressStreamer
{
    private readonly ConcurrentDictionary<string, Queue<ProgressEvent>> _streams =
        new();

    public void Subscribe(string sessionId)
    {
        _streams.TryAdd(sessionId, new Queue<ProgressEvent>());
    }

    public void Unsubscribe(string sessionId)
    {
        _streams.TryRemove(sessionId, out _);
    }

    public void Publish(ProgressEvent evt)
    {
        foreach (var stream in _streams.Values)
        {
            stream.Enqueue(evt);
        }
    }

    public async IAsyncEnumerable<ProgressEvent> ReadEventsAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!_streams.TryGetValue(sessionId, out var queue))
        {
            await Task.Delay(100, ct);
        }

        while (!ct.IsCancellationRequested)
        {
            while (queue.TryDequeue(out var evt))
            {
                yield return evt;
            }
            await Task.Delay(100, ct);
        }
    }
}

public record ProgressEvent(
    string Type,
    string Message,
    double Progress,
    string? Details = null
);
```

**Acceptance Criteria:**
- [ ] Manages subscriber sessions
- [ ] Publishes events to all subscribers
- [ ] Reads events async enumerable
- [ ] Handles cancellation

### FIX-112D-02: Create SSE Endpoint

**File:** `Services/ProgressEndpoint.cs`

```csharp
[Route("embystreams/progress")]
public class ProgressEndpoint : IReturnVoid
{
    public string SessionId { get; set; }
}

public class ProgressService : Service
{
    public async Task Any(ProgressEndpoint request)
    {
        var streamer = Plugin.Instance.ProgressStreamer;
        Response.ContentType = "text/event-stream";
        Response.NoCache = true;

        await foreach (var evt in streamer.ReadEventsAsync(request.SessionId, Request.AbortToken))
        {
            var json = JsonSerializer.Serialize(evt);
            await Response.WriteAsync($"data: {json}\n\n", Request.AbortToken);
            await Response.FlushAsync(Request.AbortToken);
        }
    }
}
```

**Acceptance Criteria:**
- [ ] SSE endpoint returns text/event-stream
- [ ] No-cache header set
- [ ] Flushes each event
- [ ] Respects cancellation

---

## Sprint 112 Dependencies

- **Previous Sprint:** 111 (Sync Pipeline)
- **Blocked By:** Sprint 111
- **Blocks:** Sprint 113 (Saved/Blocked User Actions)

---

## Sprint 112 Completion Criteria

- [ ] PlaybackService implements ranked fallback: cache primary → cache secondary → live
- [ ] PlaybackService DOES NOT call cache.SetAsync in GetStreamUrlAsync
- [ ] StreamCache manages dual URL storage (primary + secondary)
- [ ] StreamCache respects 24-hour TTL
- [ ] StreamUrlSigner signs and verifies URLs
- [ ] ProgressStreamer streams events via SSE
- [ ] Build succeeds
- [ ] E2E: Playback works with signed URLs and cache fallback

---

## Sprint 112 Notes

**Ranked Fallback Hierarchy (CRITICAL):**

PlaybackService MUST try URLs in this exact order:
1. Primary cached URL (Url field) → If valid and not expired, return signed
2. Secondary cached URL (UrlSecondary field) → If valid and not expired, return signed
3. Live resolution from AIOStreams → Resolve, pick best quality, return signed (DO NOT cache)

Cache writing is handled exclusively by ItemPipelineService during Resolved and Hydrated phases. PlaybackService is READ-ONLY with respect to cache.

**Cache TTL:**
- Default: 24 hours
- Expiration checked on read (no HEAD requests at playback time)
- Expired entries invalidated on access

**Dual URL Storage:**

The cache stores two URLs per media_id for redundancy:
- `url` (primary) - First choice from highest quality stream
- `url_secondary` (secondary) - Fallback from second-best quality stream

This allows playback to succeed even if the primary URL becomes unavailable.

**URL Signing:**
- HMAC-SHA256
- Includes timestamp for expiration
- 1-hour max age
- PluginSecret from config

**SSE Events:**
- Type: "progress", "complete", "error"
- Progress: 0.0 to 1.0
- Details: Optional JSON payload

**No URL Validation at Playback Time:**

Cached URLs are NOT validated with HEAD requests during playback. This adds latency and is unnecessary with proper TTL management. Cache validity is determined solely by expiration timestamp.

**ItemPipelineService Cache Writing:**

ItemPipelineService is responsible for cache writing:
- Resolved phase: Set primary URL (url = best stream.Url)
- Hydrated phase: Set secondary URL (url_secondary = second-best stream.Url)
- DO NOT write cache from PlaybackService.GetStreamUrlAsync
