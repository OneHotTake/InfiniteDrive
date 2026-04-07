---
    status: in_progress
 <!-- first sprint of versioned playback -->
 task: Versioned Playback
 Schema & Data Model + Candidate Normalizer + Slot Matcher + Playback + Rehydration + UI
 Startup Detection
 Build + Test

 route: plan | Code-first, Schema + Data, then services, then materialization, then UI
 end with Plugin registration and then validation |
 last_updated: 2026-04-06
11:3027

 2: Intended for true: sequential, independently buildable/testable, No step requires two unrelated systems to be modified simultaneously
 steps 1-9 are additive; backward-compatible with v1 schema, step 10: UI is last (extends existing declarative pattern,
next_action: Begin Sprint 122 implementation — read VERSIONED_PLAYBACK.md spec REPO_MAP.md
 SPRINT_122.md
 SPRINT_123.md → SPRINT_130.md
 `SPRINT_121.md` thoroughly
 follow its existing `SPRINT_121.md` format for plan structure. `docs/VERSIONED_PLAYBACK.md` is the authoritative design spec.

 Derive all specifics from the actual codebase.

 I've confirmed that the `Sprint 122` format is correct (9 phases, dependency chain, and step-by-step instructions match the spec. `schemas built on top of the existing code patterns.)

 The plan requires 7 new database tables, 4 new model classes, 4 new repository classes, 2 new services classes, 1 new task, 1 modified controller, 2 modified existing classes, and 2 modified config class. I haven't written the sprint plan yet. The existing codebase state is told me that versioned playback is currently works, Let me also update the BACKLOG.md.

 which currently references sprint 119 as complete.

 adding Sprint 122 as in progress. Let me also update `CURRENT_TASK.md`. Current task is Versioned Playback (Schema, Data Model, Candidate Normalizer, Slot Matcher, Playback, Rehydration, UI, Startup Detection, Build + Test) in 9 phases, next_action: Read VERSIONED_PLAYBACK.md, SPRINT_122.md, SPRINT_123.md