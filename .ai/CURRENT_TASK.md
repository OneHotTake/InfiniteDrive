---
    status: in_progress
    task: Sprint 132 — Stream Endpoint + Token Methods
    phase: Sprint 132 spec written
    last_updated: 2026-04-08

## Sprint 132 Spec Created

**Files:**
- `.ai/SPRINT_132.md` — Full spec for Stream Endpoint + Token Methods

### Implementation Scope
1. Create `Services/StreamEndpointService.cs` with HLS manifest rewriting
2. Add `GenerateResolveToken` and `ValidateStreamToken` to `StreamUrlSigner`
3. Create `Models/StreamEndpointRequest.cs`
4. Update schema to v23
5. Register endpoint in `Plugin.cs`

### Build Status
- 0 errors, 0 warnings

### Next Action
Implement Sprint 132 FIX-132A-01 (Create StreamEndpointService)
