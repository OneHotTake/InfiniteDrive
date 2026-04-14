# Sprint 219 — IChannel SDK Reality Check (Research)

**Status:** Draft | **Risk:** LOW | **Depends:** none | **Target:** v0.52

## Why (2 sentences max)
Multiple AI agents have produced conflicting answers about whether `IChannel` search can be wired
in Emby SDK 4.9.1.90 — the channel was built and deleted twice (Sprint 55, Sprint 211) without
definitive proof of what actually works vs. what compiles but silently does nothing. This sprint
produces ground-truth findings from the actual DLLs and a live server before any implementation
work begins.

## Non-Goals
- No production code written — findings only
- Do not fix or improve anything discovered; log it and stop
- Do not modify any existing service, task, or schema

## Tasks

### FIX-219-01: Inspect IChannel interface in the compile-time SDK DLL
**Files:** none (read-only shell commands)
**Effort:** S
**What:** Dump every method signature on `IChannel` and `ISearchableChannel` from the NuGet
reference DLL to confirm what the compiler sees. Run these commands and save output to
`.ai/research/sprint-219-dll-inspection.txt`:

```bash
# Find the compile-time DLL (NuGet cache or local ref)
find ~/.nuget -name "MediaBrowser.Controller.dll" | head -5
find ~/Projects/emby/InfiniteDrive -name "MediaBrowser.Controller.dll" | head -5

# Dump IChannel members (requires dotnet-ildasm or strings fallback)
# Option A — if dotnet-ildasm available:
dotnet ildasm ~/.nuget/packages/mediabrowser.server.core/4.9.1.90/lib/netstandard2.0/MediaBrowser.Controller.dll \
  | grep -A 40 "interface IChannel"

# Option B — strings fallback (always works):
strings ~/.nuget/packages/mediabrowser.server.core/4.9.1.90/lib/netstandard2.0/MediaBrowser.Controller.dll \
  | grep -iE "IChannel|ISearchable|SearchTerm|SearchQuery|ChannelItem|GetChannelItems" \
  | sort -u
```

**Record:** Does `ISearchableChannel` exist as a type? What are the exact method signatures on
`IChannel`? Is there any overload of `GetChannelItems` that takes anything other than
`InternalChannelItemQuery`?

---

### FIX-219-02: Inspect InternalChannelItemQuery properties in the compile-time DLL
**Files:** none (read-only)
**Effort:** S
**What:** Dump all properties on `InternalChannelItemQuery` to determine definitively whether
`SearchTerm`, `SearchQuery`, `Query`, `Id`, or `FolderId` exist at compile time.

```bash
strings ~/.nuget/packages/mediabrowser.server.core/4.9.1.90/lib/netstandard2.0/MediaBrowser.Controller.dll \
  | grep -iE "InternalChannelItemQuery|SearchTerm|SearchQuery|FolderId" \
  | sort -u

# Also check the Model DLL — some types live there
strings ~/.nuget/packages/mediabrowser.common/4.9.1.90/lib/netstandard2.0/MediaBrowser.Model.dll \
  | grep -iE "InternalChannelItemQuery|ChannelItemQuery|SearchTerm" \
  | sort -u
```

**Record:** Exact property names present. Note which DLL each came from. This is the definitive
answer to the Sprint 209 "impossible" finding vs. the Sprint 54 "it worked" claim.

---

### FIX-219-03: Inspect the runtime DLL (what Emby actually loads)
**Files:** none (read-only)
**Effort:** S
**What:** The compile-time NuGet stub and the runtime DLL Emby ships can differ. Inspect the
DLL that the running Emby server actually loads — this is the source of truth for reflection
results at runtime.

```bash
# Find the runtime DLL shipped with the Emby beta installation
find ~/Projects/emby/emby-beta -name "MediaBrowser.Controller.dll" 2>/dev/null
find ~/Projects/emby/emby-beta -name "MediaBrowser.Model.dll" 2>/dev/null

# Dump channel-related types from the RUNTIME DLL
RUNTIME_DLL=$(find ~/Projects/emby/emby-beta -name "MediaBrowser.Controller.dll" | head -1)
echo "Runtime DLL: $RUNTIME_DLL"
strings "$RUNTIME_DLL" \
  | grep -iE "IChannel|ISearchable|InternalChannelItemQuery|SearchTerm|SearchQuery|FolderId|ChannelItemQuery" \
  | sort -u
```

**Record:** Does the runtime DLL expose more or fewer members than the compile-time stub?
Any `SearchTerm` / `SearchQuery` properties present in the runtime type that are absent at
compile time would explain why reflection works at runtime but the property isn't visible to
the compiler.

---

### FIX-219-04: Live runtime probe via throwaway diagnostic endpoint
**Files:** `Services/StatusService.cs` (modify — temporary, revert after sprint)
**Effort:** S
**What:** Add a temporary GET endpoint that spins up a minimal `InternalChannelItemQuery`
instance, dumps all its property names and types to the response, and returns them as JSON.
This confirms what reflection actually sees in the live process — not what `strings` guesses
from the binary.

Add inside `StatusService.cs` (or any existing `IService` file — pick the smallest one):

```csharp
[Route("/InfiniteDrive/Debug/ChannelQueryProps", "GET",
    Summary = "Sprint 219 research: dumps InternalChannelItemQuery properties via reflection")]
public class ChannelQueryPropsRequest : IReturn<object> { }

public object Get(ChannelQueryPropsRequest _)
{
    var deny = AdminGuard.RequireAdmin(_authCtx, Request);
    if (deny != null) return deny;

    // Instantiate via reflection to avoid compile-time dependency on the concrete type
    var typeName = "MediaBrowser.Controller.Channels.InternalChannelItemQuery";
    var assembly = AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => a.GetType(typeName) != null);

    if (assembly == null)
        return new { error = "Type not found", typeName };

    var type = assembly.GetType(typeName);
    var props = type!.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => new { p.Name, Type = p.PropertyType.FullName })
        .OrderBy(p => p.Name)
        .ToList();

    // Also check for ISearchableChannel
    var searchableType = AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("MediaBrowser.Controller.Channels.ISearchableChannel"))
        .FirstOrDefault(t => t != null);

    return new
    {
        InternalChannelItemQuery_Properties = props,
        ISearchableChannel_Exists = searchableType != null,
        ISearchableChannel_FullName = searchableType?.FullName
    };
}
```

Deploy with `./emby-start.sh`, then:

```bash
curl -s "http://localhost:8096/InfiniteDrive/Debug/ChannelQueryProps" \
  -H "X-Emby-Token: $(grep -o 'ApiKey>[^<]*' ~/emby-dev-data/config/system.xml | head -1 | cut -d'>' -f2)" \
  | python3 -m json.tool | tee .ai/research/sprint-219-live-props.json
```

**Record:** Full JSON output saved to `.ai/research/sprint-219-live-props.json`. This is the
definitive ground truth.

> ⚠️ **Revert this endpoint after the sprint.** It is admin-only but should not ship.

---

### FIX-219-05: Check IChannel interface methods at runtime
**Files:** `Services/StatusService.cs` (same temporary block as FIX-219-04)
**Effort:** S
**What:** Extend the diagnostic endpoint (or add a second one) to dump all methods on the
`IChannel` interface as seen by the runtime — specifically looking for any overload of
`GetChannelItems` beyond `(InternalChannelItemQuery, CancellationToken)`.

```csharp
[Route("/InfiniteDrive/Debug/IChannelMethods", "GET",
    Summary = "Sprint 219 research: dumps IChannel interface methods")]
public class IChannelMethodsRequest : IReturn<object> { }

public object Get(IChannelMethodsRequest _)
{
    var deny = AdminGuard.RequireAdmin(_authCtx, Request);
    if (deny != null) return deny;

    var typeName = "MediaBrowser.Controller.Channels.IChannel";
    var type = AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType(typeName))
        .FirstOrDefault(t => t != null);

    if (type == null)
        return new { error = "IChannel not found" };

    var methods = type.GetMethods()
        .Select(m => new
        {
            m.Name,
            Parameters = m.GetParameters()
                .Select(p => new { p.Name, Type = p.ParameterType.FullName })
                .ToList(),
            ReturnType = m.ReturnType.FullName
        })
        .OrderBy(m => m.Name)
        .ToList();

    return new { IChannel_Methods = methods };
}
```

```bash
curl -s "http://localhost:8096/InfiniteDrive/Debug/IChannelMethods" \
  -H "X-Emby-Token: $(grep -o 'ApiKey>[^<]*' ~/emby-dev-data/config/system.xml | head -1 | cut -d'>' -f2)" \
  | python3 -m json.tool | tee .ai/research/sprint-219-ichannel-methods.json
```

**Record:** Is there a second `GetChannelItems` overload? What are its parameter types?
Does `ChannelItemSearchRequest` appear anywhere?

---

### FIX-219-06: Document findings and make the go/no-go call
**Files:** `.ai/research/sprint-219-findings.md` (create)
**Effort:** S
**What:** Write a findings document using this exact template:

```markdown
# Sprint 219 Findings — IChannel SDK Reality Check

## SDK Version
- Compile-time NuGet: MediaBrowser.Server.Core X.X.X.XX
- Runtime DLL version: [from binary]

## Question 1: Does ISearchableChannel exist in the runtime?
**Answer:** YES / NO
**Evidence:** [paste relevant output line]

## Question 2: Does InternalChannelItemQuery have a SearchTerm/SearchQuery property?
**Answer:** YES (property name: ___) / NO
**Evidence:** [paste relevant output lines]

## Question 3: Is there a second GetChannelItems overload on IChannel?
**Answer:** YES (signature: ___) / NO
**Evidence:** [paste relevant output lines]

## Question 4: What is the exact reflection property name for folder/item ID routing?
**Answer:** Property name is ___ (Id / FolderId / ChannelId / other)
**Evidence:** [paste relevant output line]

## Decision
Based on findings:

**Search wiring verdict:** POSSIBLE via [mechanism] / IMPOSSIBLE — SDK wall confirmed

**Recommended next sprint:**
- If search possible: Sprint 220 — Build InfiniteDriveChannel with search wired via [mechanism]
- If search impossible: Sprint 220 — Build browse-only InfiniteDriveChannel +
  Sprint 221 — HomeSectionManager deeplink to Discover web UI for search
```

---

## Verification (run these or it fails)
- [ ] `.ai/research/sprint-219-dll-inspection.txt` exists and is non-empty
- [ ] `.ai/research/sprint-219-live-props.json` exists and contains `InternalChannelItemQuery_Properties`
- [ ] `.ai/research/sprint-219-ichannel-methods.json` exists and contains `IChannel_Methods`
- [ ] `.ai/research/sprint-219-findings.md` exists with all 4 questions answered
- [ ] Diagnostic endpoints reverted from `StatusService.cs`
- [ ] `dotnet build -c Release` (0 errors/warnings) after revert

## Completion
- [ ] All tasks done
- [ ] BACKLOG.md updated (note Sprint 220 path is now unblocked with evidence)
- [ ] REPO_MAP.md updated (note diagnostic files added to `.ai/research/`)
- [ ] git commit -m "chore: end sprint 219 — IChannel search viability confirmed/denied"
```

---

