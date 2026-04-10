# COOLDOWN — AIOStreams Throttling & Good-Citizen Design

**Status:** Design Spec | **Owner:** embyStreams | **Target Sprint:** 155

---

## Why This Exists

AIOStreams is the single upstream that makes the Emby experience possible. If we
hammer it, three things happen, in this order:

1. Public/shared instances return `429 Too Many Requests` and users see empty catalogs.
2. Instance operators (ElfHosted, self-hosters who shared their URL) quietly block us.
3. The AIOStreams project notices and the community starts treating embyStreams as
   the plugin that ruined the free tier for everyone.

We can avoid all of that without any new user-facing complexity. The rules are
simple, the implementation is a thin helper class, and the user sees one checkbox.

> **Design goal:** the user picks nothing. The plugin detects what kind of
> AIOStreams instance they're pointed at and does the right thing forever after.
> Advanced users can override via XML. No wizard page. No 5-slider settings panel.

---

## The Two Realities

Every operation embyStreams performs falls into exactly one of two buckets:

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
         Default assumption. Respects all published rate limits + 30% safety margin.

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
the UI. Advanced users who really need to tune can edit `EmbyStreams.xml` directly.

```
                              SHARED     PRIVATE
HTTP base delay (ms)             1000         200
HTTP jitter (+/- ms)              300          80
HTTP timeout (s)                    8          12
Catalog sources per run             2           6
Enrichment items per run           42         150
Rehydration items per run         500        2000
Cinemeta base delay (ms)          700         200
Global 429 cooldown (s)           900         120
```

Why these numbers:

- **1000 ms + 300 ms jitter** gives ~2.5 req/s, well under the published
  `STREAM_API_RATE_LIMIT` of 10/5s, with headroom for burst retries.
- **Jitter** prevents thundering-herd when multiple tasks wake on the same hour.
- **2 catalog sources per run** respects `CATALOG_API_RATE_LIMIT` (5/5s) even when
  sources have multiple pages. Private can do 6 because the user owns the pipe.
- **Enrichment 42** is already what we do today. Keep it for SHARED. PRIVATE gets
  more because 150 items × 1s delay × 0 = instant when there's no delay.
- **Global 429 cooldown** — if we see a 429 we go dark for 15 minutes on SHARED.
  That single rule keeps us off every operator's "block this plugin" list.

---

## The Helper Class

One file. One class. No ceremony.

```csharp
// Services/CooldownGate.cs
public sealed class CooldownGate
{
    private readonly PluginConfiguration _cfg;
    private readonly ILogger _logger;
    private DateTimeOffset _globalCooldownUntil = DateTimeOffset.MinValue;

    public InstanceType Instance => _cfg.ResolvedInstanceType;
    public CooldownProfile Profile => CooldownProfile.For(Instance);

    // Call before every HTTP request to AIOStreams / Cinemeta.
    public async Task WaitAsync(CooldownKind kind, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _globalCooldownUntil)
        {
            var remaining = _globalCooldownUntil - DateTimeOffset.UtcNow;
            await Task.Delay(remaining, ct);
        }

        var delay = Profile.DelayFor(kind);
        var jitter = Random.Shared.Next(-Profile.JitterMs, Profile.JitterMs);
        await Task.Delay(delay + jitter, ct);
    }

    // Call on any 429 response.
    public void Tripped(TimeSpan? retryAfter = null)
    {
        var wait = retryAfter ?? TimeSpan.FromSeconds(Profile.GlobalCooldownSeconds);
        _globalCooldownUntil = DateTimeOffset.UtcNow + wait;
        _logger.Warn($"[cooldown] AIOStreams 429 — pausing all HTTP for {wait.TotalSeconds:F0}s");
    }
}

public enum InstanceType { Shared, Private }
public enum CooldownKind { CatalogFetch, StreamResolve, Enrichment, Cinemeta }
```

That's the whole abstraction. Every HTTP call site changes from:

```csharp
await Task.Delay(config.ApiCallDelayMs, ct);
var result = await _client.GetAsync(url, ct);
```

to:

```csharp
await _cooldown.WaitAsync(CooldownKind.StreamResolve, ct);
var result = await _client.GetAsync(url, ct);
if (result.StatusCode == 429) _cooldown.Tripped(ParseRetryAfter(result));
```

Local disk / DB / Emby calls do not get a `CooldownGate`. They go as fast as they
can. That's the point.

---

## Where the Gate Is Wired

| Call site | Kind | Notes |
|---|---|---|
| `AioStreamsClient.GetCatalogAsync` | `CatalogFetch` | One gate per source page |
| `AioStreamsClient.GetStreamsAsync` | `StreamResolve` | Used by LinkResolverTask, RehydrationService, ResolverService |
| `AioMetadataClient.FetchAsync` | `Enrichment` | DeepCleanTask enrichment trickle |
| `CinemetaClient.GetAsync` | `Cinemeta` | MetadataFallbackTask |

**Nowhere else.** No gate on `.strm` writes, no gate on DB upserts, no gate on
`ILibraryManager.CreateItem`. The file I/O and Emby calls stay aggressive.

---

## Batch Caps (enforced by callers, read from profile)

Each task reads its cap from `CooldownProfile`:

```csharp
var cap = _cooldown.Profile.EnrichmentPerRun; // 42 or 150
foreach (var item in candidates.Take(cap)) { ... }
```

The tasks themselves stay dumb. The profile is the single source of truth for
"how much is too much for this instance type."

---

## Observability (without scaring the user)

On any `429` we:

1. Log `[cooldown] AIOStreams rate limit hit (status 429). Backing off 900s.`
2. Set `_globalCooldownUntil` for the profile's cooldown.
3. Emit a single structured event to the existing progress streamer so the admin
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

- ❌ "Instance Type" dropdown in the wizard. (Auto-detected.)
- ❌ Individual sliders for each cooldown kind. (Profile constants.)
- ❌ Per-source throttle overrides. (Profile applies to the instance, not sources.)
- ❌ User-visible "Advanced Throttling" tab. (Doesn't exist. XML only.)
- ❌ Separate `ApiCallDelayMs` / `JitterMs` / `MaxCatalogSourcesPerRun` config fields.
  (All collapse into `ResolvedInstanceType`.)
- ❌ Retry-with-exponential-backoff on individual failed items during a 429.
  (We go dark globally — simpler, safer, and closer to what operators want.)

The existing `ApiCallDelayMs` field is **retired** — any value in user XML is
ignored after migration, and the field is removed from the configuration page.
This is the one piece of config we're actively reducing.

---

## Success Criteria

1. Zero new fields on the configuration UI.
2. `CooldownGate` wraps every HTTP call to AIOStreams/Cinemeta and nothing else.
3. A synthetic 429 (injectable in debug build) causes all HTTP to pause for
   900s on SHARED, 120s on PRIVATE, and then resume.
4. Local disk/DB/Emby operations measurably do **not** slow down after the gate
   is introduced (benchmark: 10k `.strm` writes before/after within ±5%).
5. Full catalog sync against a public AIOStreams instance completes without
   a single 429 over a 24-hour soak test.

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

SHARED profile stays at ≤ 70% of the lowest applicable limit with jitter.
PRIVATE profile is tuned for the operator, not the published limits.
