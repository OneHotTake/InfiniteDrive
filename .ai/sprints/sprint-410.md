# Sprint 410 — Secure playback via RequiresOpening + OpenMediaSource

**Status:** In Progress | **Risk:** MED | **Depends:** Sprint 407 | **Target:** v2.x

## Why (2 sentences max)
Every .strm file currently contains a signed 365-day resolve URL exposed to Emby playback without auth gating. Setting RequiresOpening=true forces all playback through OpenMediaSource(), gating it behind Emby's auth layer and removing direct .strm URL exposure.

## Non-Goals
- Removing old ResolveService/StreamEndpointService endpoints (follow-up sprint)
- Removing PlaybackTokenService token methods (follow-up sprint)

## Tasks

### FIX-410-01: Add InfiniteDriveLiveStream model
**Files:** Models/InfiniteDriveLiveStream.cs (create)
**Effort:** S
**What:** ILiveStream wrapper that carries a resolved MediaSourceInfo for OpenMediaSource() return

### FIX-410-02: Rewrite AioMediaSourceProvider OpenMediaSource
**Files:** Services/AioMediaSourceProvider.cs (modify)
**Effort:** M
**What:** Set RequiresOpening=true + OpenToken=cdnUrl in GetMediaSources(); implement OpenMediaSource() to return InfiniteDriveLiveStream

### FIX-410-03: Notify Emby library after .strm write
**Files:** Services/StrmWriterService.cs (modify)
**Effort:** S
**What:** Inject ILibraryMonitor, call ReportFileSystemChanged(path) after every successful .strm write

## Verification (run these or it fails)
- [ ] `dotnet build -c Release` (0 errors/warnings)
- [ ] `./emby-reset.sh` succeeds
- [ ] Manual test: play AIO item → log shows "OpenMediaSource called" not "/InfiniteDrive/Resolve"
- [ ] Multiple quality versions visible in picker
- [ ] Non-AIO library items unaffected

## Completion
- [ ] All tasks done
- [ ] BACKLOG.md updated
- [ ] REPO_MAP.md updated
- [ ] git commit -m "chore: end sprint 410"
