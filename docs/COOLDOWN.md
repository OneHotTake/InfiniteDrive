# COOLDOWN — AIOStreams Throttling & Good-Citizen Design

**Status:** Active · **Owner:** InfiniteDrive

---

## Why This Exists

AIOStreams is the single upstream that makes the Emby experience possible. If we
hammer it, three things happen, in this order:

1. Public/shared instances return `429 Too Many Requests` and users see empty catalogs.
2. Instance operators (ElfHosted, self-hosters who shared their URL) quietly block us.
3. The AIOStreams project notices and the community starts treating InfiniteDrive as
   the plugin that ruined the free tier for everyone.

We can avoid all of that without any new user-facing complexity. The rules are
simple, the implementation is a thin helper class, and the user sees one checkbox.

> **Design goal:** the user picks nothing. The plugin detects what kind of
> AIOStreams instance they're pointed at and does the right thing forever after.
> Advanced users can override via XML. No wizard page. No 5-slider settings panel.

---

## The Two Realities

Every operation InfiniteDrive performs falls into exactly one of two buckets:

```
LOCAL  -> disk I/O, SQLite, Emby ILibraryManager, .strm/.nfo writes, cache hits
HTTP   -> anything that touches the AIOStreams manifest, catalog, or stream API
         (and Cinemeta, which is a public Stremio endpoint)
```

**Rule:** LOCAL is aggressive. HTTP is polite. There is no third bucket.

Everything else — batch caps, jitter, backoff — is a consequence of that rule.

---

## Instance Types (the only knob that matters)

```
SHARED   Public AIOStreams (ElfHosted free tier, random user's URL).
         Default assumption. Respects all published rate limits + safety margin.

PRIVATE  Self-hosted or paid AIOStreams the user controls.
         Aggressive but still civilised (we don't want to DDoS our own VPS either).
```

**Detection is automatic:**

- If `PrimaryManifestUrl` host matches a known shared-instance allowlist
  (`elfhosted.com`, `aiostreams.elfhosted.com`, a small maintained list in code),
  treat it as `SHARED`.
- If the host is `localhost`, `127.0.0.1`, or a private RFC1918 range, treat it as `PRIVATE`.
- Everything else defaults to `SHARED` (safer default — mistakes are cheap,
  getting banned is expensive).

The detection happens once at config save and is stored as
`ResolvedInstanceType` in the config XML. No UI.

---

## The Cooldown Profiles

These are constants compiled into the plugin. They are **not** user-editable from
the UI. Advanced users who really need to tune can edit `InfiniteDrive.xml` directly.

```
                              SHARED     PRIVATE
HTTP base delay (ms)                0           0
SeriesMeta delay (ms)             200           0
Jitter (+/- ms)                    50           0
HTTP timeout (s)                    10          15
Global 429 cooldown (s)             60          30
```

Why these numbers:

- **0ms base delay + 50ms jitter** on Shared gives minimal overhead while
  still breaking synchronization across parallel tasks. The real throttling
  comes from the 429 global cooldown, not per-request delays.
- **200ms SeriesMeta delay** on Shared prevents hammering the API during
  series episode expansion — the most burst-heavy operation. Private instances
  skip this entirely.
- **60s global 429 cooldown** on Shared keeps us off every operator's "block
  this plugin" list. Private gets 30s — the operator is only hurting themselves.

---

## CooldownKind — Two Profiles

The `CooldownKind` enum has exactly two values:

```csharp
public enum CooldownKind { Default, SeriesMeta }
```

| Kind | Purpose | Delay (Shared) | Delay (Private) |
|------|---------|----------------|-----------------|
| `Default` | General API calls: catalog fetch, stream resolve, enrichment | 0ms | 0ms |
| `SeriesMeta` | Series metadata expansion, episode lookups | 200ms + jitter | 0ms |

The 4-value enum from earlier designs (`CatalogFetch`, `StreamResolve`, `Enrichment`, `Cinemeta`) was collapsed to two because per-operation delays proved unnecessary — the global 429 cooldown is sufficient for rate-limit protection, and only series metadata expansion needed proactive throttling on shared instances.

---

## The Helper Class

One file. One class. No ceremony.

```csharp
// Services/CooldownGate.cs
public sealed class CooldownGate
{
    private readonly Func<PluginConfiguration> _configAccessor;
    private readonly ILogger _logger;
    private DateTimeOffset _globalCooldownUntil = DateTimeOffset.MinValue;

    public InstanceType Instance => _configAccessor().ResolvedInstanceType;
    public CooldownProfile Profile => CooldownProfile.For(Instance);

    // Call before every HTTP request to AIOStreams / Cinemeta.
    public async Task WaitAsync(CooldownKind kind, CancellationToken ct)
    {
        // If we're in a global 429 cooldown, sleep until it expires
        lock (_lock)
        {
            if (DateTimeOffset.UtcNow < _globalCooldownUntil)
            {
                var remaining = _globalCooldownUntil - DateTimeOffset.UtcNow;
                _logger.LogDebug("[cooldown] Global cooldown active — waiting {Remaining:F1}s",
                    remaining.TotalSeconds);
                await Task.Delay(remaining, ct);
                return;
            }
        }

        var baseDelay = Profile.DelayFor(kind);
        var jitter = _jitterSource(-Profile.JitterMs, Profile.JitterMs);
        var totalDelay = Math.Max(0, baseDelay + jitter);

        if (totalDelay > 0)
            await Task.Delay(totalDelay, ct);
    }

    // Call on any 429 response.
    public void Tripped(TimeSpan? retryAfter = null)
    {
        lock (_lock)
        {
            var wait = retryAfter ?? TimeSpan.FromSeconds(Profile.GlobalCooldownSeconds);
            _globalCooldownUntil = DateTimeOffset.UtcNow + wait;

            // Three-strikes tracking: if 3+ 429s in 1h on Shared, suggest private instance
            _tripHistory.Enqueue(DateTimeOffset.UtcNow);
            while (_tripHistory.Count > 0 && _tripHistory.Peek() < DateTimeOffset.UtcNow.AddHours(-1))
                _tripHistory.Dequeue();

            if (_tripHistory.Count >= 3 && Instance == InstanceType.Shared)
            {
                _suggestPrivateInstance = true;
                _logger.LogInformation("[cooldown] 3+ rate limits in 1h — suggesting private instance");
            }

            _logger.LogWarning("[cooldown] AIOStreams 429 — pausing all HTTP for {Seconds:F0}s", wait.TotalSeconds);
        }
    }

    public static TimeSpan? ParseRetryAfter(string? retryAfterValue) { ... }
}

public enum InstanceType { Shared, Private }
public enum CooldownKind { Default, SeriesMeta }
```

Every HTTP call site follows this pattern:

```csharp
await _cooldown.WaitAsync(CooldownKind.Default, ct);
var result = await _client.GetAsync(url, ct);
if (result.StatusCode == 429) _cooldown.Tripped(CooldownGate.ParseRetryAfter(result));
```

Series metadata expansion uses:

```csharp
await _cooldown.WaitAsync(CooldownKind.SeriesMeta, ct);
```

Local disk / DB / Emby calls do not get a `CooldownGate`. They go as fast as they
can. That's the point.

---

## Where the Gate Is Wired

The gate wraps every HTTP call to AIOStreams and Cinemeta, and nothing else.

**Nowhere else.** No gate on `.strm` writes, no gate on DB upserts, no gate on
`ILibraryManager.CreateItem`. The file I/O and Emby calls stay aggressive.

---

## Observability (without scaring the user)

On any `429` we:

1. Log `[cooldown] AIOStreams rate limit hit (status 429). Backing off {N}s.`
2. Set `_globalCooldownUntil` for the profile's cooldown.
3. Emit a single structured event to the progress streamer so the admin
   dashboard shows a quiet badge:
   > *"Upstream busy — pausing briefly to stay a good neighbour."*

No popups. No error toasts. No "CONFIGURE RATE LIMITING" wizard. The plugin
degrades gracefully to cached mode and recovers on its own.

If 429s happen **three times in a rolling hour**, and only then, we surface a
one-line suggestion in the admin dashboard:

> *"Want faster syncs? Consider a private AIOStreams instance. [Learn more]"*

That's the only time the user ever sees the word "rate limit."

---

## What We Are Explicitly Not Building

These were on the table and got cut in service of elegance:

- No "Instance Type" dropdown in the wizard. (Auto-detected.)
- No individual sliders for each cooldown kind. (Profile constants.)
- No per-source throttle overrides. (Profile applies to the instance, not sources.)
- No user-visible "Advanced Throttling" tab. (Doesn't exist. XML only.)
- No separate `ApiCallDelayMs` / `JitterMs` config fields. (All collapsed into `CooldownKind.Default`.)
- No retry-with-exponential-backoff on individual failed items during a 429.
  (We go dark globally — simpler, safer, and closer to what operators want.)

The old `ApiCallDelayMs` field has been **removed** — any value in user XML is
ignored. This is the one piece of config we actively reduced.

---

## Appendix — Published AIOStreams Rate Limits (from `.env.sample`)

For future reference when tuning profile constants:

```
CATALOG_API_RATE_LIMIT        5  / 5s
STREMIO_CATALOG_RATE_LIMIT   30  / 5s
STREAM_API_RATE_LIMIT        10  / 5s
USER_API_RATE_LIMIT           5  / 5s
DEFAULT_TIMEOUT               7s
```

The global 429 cooldown (60s shared, 30s private) is the primary throttle mechanism.
Per-request delays are minimal — the system relies on detecting 429s and backing
off globally rather than preemptively throttling every request.
