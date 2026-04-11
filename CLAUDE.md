# InfiniteDrive — Claude Code Instructions

## Version Naming Policy

**DO NOT use version numbers (V1, V2, V3, V20, V33, etc.) in code, class names, or method names.**

InfiniteDrive is beta software. Version numbers are arbitrary during development and should only be used for:
- Documentation and release notes
- Database schema version tracking (stored in schema_version table)
- External communication (not internal code)

Use descriptive names instead:
- ❌ `V33DatabaseManager`, `V20Schema`
- ✅ `DatabaseManager`, `MediaItem`, `Source`

**Rationale:** Version numbers create unnecessary coupling and become misleading as schemas evolve. Use descriptive names that reflect what the code does, not what version it "is".

---

## Design Principle

> **Simplicity Over Complexity**
>
> Users want simplicity, administrators want flexibility, nobody wants complexity. Fortunately for us, the debrid and usenet streaming world is inherently complex.
>
> When making architectural decisions: prefer the simple approach that works over the sophisticated one that handles every edge case.

---

## Startup Ritual (Every Session)

Read these two files **only**, then stop and wait for the task:

1. `.ai/CURRENT_TASK.md` — active task and status
2. `.ai/REPO_MAP.md` — where to find code

Do NOT read README.md, BACKLOG.md, or any source file unless the task requires it.

---

## Sprint Completion Ritual (Every Sprint)

At the end of every sprint, **commit and push immediately before starting the next sprint**. No accumulated uncommitted work.

Before committing:
1. Update `.ai/REPO_MAP.md` — add/modify/remove entries for any .cs files created, deleted, or significantly refactored this sprint
2. Update `BACKLOG.md` — mark completed tasks `[x]`, add Findings and Guidance
3. Update `.ai/SESSION_SUMMARY.md` — log tokens saved, delegation outcomes
4. `git add .ai/REPO_MAP.md BACKLOG.md && git commit -m "chore: end-of-sprint update"`

Sprint-specific schema changes, feature specs, and behaviour decisions belong in `BACKLOG.md` or `.ai/CURRENT_TASK.md` — not in this file.

---

## Hard Context Limits

- **Max 3 files per subtask** without explicit user approval
- **Never read a file >300 lines in full** — use `grep`, `sed`, or line-range reads
- **Never re-read a file already read this session** — use `.ai/CURRENT_TASK.md` notes instead
- **No broad grep sweeps** — always scope to a specific path
- **Summarize before continuing** — write a 3-line summary to `.ai/CURRENT_TASK.md` after each subtask

---

## Output Discipline

- Prefer diffs over full file rewrites — changed lines + 3 lines of context only
- No preamble ("I'll now...", "Let me...") — execute immediately, then report in one sentence
- Prefer JSON/checklists over prose

---

## Delegation (Cost-Optimized Mode)

Route source of truth: `.ai/MODEL_STATE.json` — resolve at runtime, never assume.
**If `.ai/MODEL_STATE.json` is missing or unreadable, default to direct Claude and flag the issue.**

Route class → task type:
- `code-fast` — file analysis, code explanation
- `code-accurate` — code review, test generation, refactoring
- `docs-fast` — boilerplate, documentation, summaries
- `longctx-analysis` — multi-file audits, repo-wide analysis

Never silently switch to a paid route without explicit approval.

**Reserve Claude for:** architecture decisions, tradeoff analysis, integration planning, final patch review, security changes, incident triage.

**High-risk changes (require Claude review before committing):**
- Plugin entrypoints / service registration
- Database schema or migrations
- Auth / API key handling
- Playback resolution / caching / network paths
- Public API endpoints

---

## State Files

- `.ai/CURRENT_TASK.md` — active task, route, status, next action
- `.ai/MODEL_STATE.json` — route classes, providers, fallback chains
- `.ai/TASK_QUEUE.json` — pending subtasks
- `.ai/FAILURES.ndjson` — append-only failure log
- `.ai/SESSION_SUMMARY.md` — session metrics
- `.ai/REPO_MAP.md` — codebase index

Never depend on chat history for state. Always read `.ai/CURRENT_TASK.md`.

---

## Sprint Planning

**Always start from the template:** `.ai/SPRINT_TEMPLATE.md`

When defining a new sprint:
1. Copy `.ai/SPRINT_TEMPLATE.md` to a new sprint file (e.g., `.ai/sprints/sprint-162.md`)
2. Fill in the header (Version, Status, Risk, Depends, Owner, Target, PR)
3. Write the Overview section with problem statement, why now, and approach
4. Research findings go into "What the Research Found" — document API/library capabilities, existing patterns, constraints
5. List Breaking Changes and Non-Goals explicitly
6. Break work into phases (A: Database, B: Service Layer, C: Wiring, D: Registration, E: Verification)
7. Each task gets a FIX-XXX format ID with file path, estimated effort, and detailed "What"
8. Build & Verification phase includes build check, grep checklist, and manual tests
9. Completion Criteria is a checklist of all deliverables
10. Open Questions / Blockers table for items requiring research or decision
11. Notes section summarizes files changed and risk assessment

---

## Session Start (New Task)

1. Read `.ai/CURRENT_TASK.md`
2. Read `.ai/REPO_MAP.md`
3. Break complex tasks into subtasks in `.ai/TASK_QUEUE.json` (~30 min each)
4. Delegate bounded subtasks to the cheapest free-cloud-first route
5. Update `.ai/SESSION_SUMMARY.md` on completion

---

## Failure Handling

1. Log to `.ai/FAILURES.ndjson`
2. Retry once (transient errors)
3. Switch to fallback route
4. After 2 retries + 3 route switches → stop, log blocker to `.ai/CURRENT_TASK.md` with exact next action

---

## Quick Reference

```
dotnet build -c Release
tail -f ~/emby-dev-data/logs/embyserver.txt
# Config UI: http://localhost:8096/web/configurationpage?name=InfiniteDrive
```

### Beta Software Locations

- **Emby Server:** `../emby-beta/` (extracted from emby-server-deb_4.10.0.8_amd64.deb)
- **Emby SDK:** `../emby.SDK-beta/` (Emby Plugin SDK documentation and samples)
- **SQLite DLLs:** Referenced from `../emby-beta/opt/emby-server/system/`

### Dev Server Scripts (all in project root)

| Script | What it does |
|--------|--------------|
| `./emby-reset.sh` | **CANONICAL RESET**: kill → wipe data + media .strm files → build → deploy DLL → start on :8096 |
| `./emby-start.sh` | Build + deploy DLL + start on :8096 (no data wipe — use when state is clean) |
| `./emby-stop.sh` | Stop Emby only |
| `./test-signed-stream.sh` | Test HMAC signed-stream endpoint against :8096 |

**If the server fails to start or loops on startup errors → always run `./emby-reset.sh` first.**

Retired scripts are in `_retired/` — do not use them.

Full dev guide: `docs/dev-guide.md`
