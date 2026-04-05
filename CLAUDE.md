# EmbyStreams — Claude Code Instructions

## Startup Ritual (Every Session)

Read these two files **only**, then stop and wait for the task:
1. `.ai/CURRENT_TASK.md` — active task and status
2. `.ai/REPO_MAP.md` — where to find code

Do NOT read README.md, BACKLOG.md, or any source file unless the task requires it.

## Sprint Completion Ritual (Every Sprint)

At the end of every sprint, before committing:
1. Update `.ai/REPO_MAP.md` — add/modify/remove entries for any .cs files that were created, deleted, or significantly refactored this sprint
2. Update `BACKLOG.md` — mark completed tasks `[x]`, add Findings and Guidance
3. Update `.ai/SESSION_SUMMARY.md` — log tokens saved, delegation outcomes
4. `git add .ai/REPO_MAP.md BACKLOG.md && git commit -m "chore: end-of-sprint update"`

---

## Hard Context Limits

- **Max 3 files per subtask** without explicit user approval
- **Never read a file >300 lines in full** — use `grep`, `sed`, or line-range reads
- **Never re-read a file already read this session** — use `.ai/CURRENT_TASK.md` notes
- **No broad grep sweeps** — always scope to a specific path
- **Summarize before continuing** — write 3-line summary to `.ai/CURRENT_TASK.md` after each subtask

---

## Output Discipline

- Prefer diffs over full file rewrites — changed lines + 3 lines of context only
- No preamble ("I'll now...", "Let me...") — execute immediately, then report in one sentence
- Prefer JSON/checklists over prose

---

## Delegation (Cost-Optimized Mode)

Route class → task type:
- `code-fast` — file analysis, code explanation
- `code-accurate` — code review, test generation, refactoring
- `docs-fast` — boilerplate, documentation, summaries
- `longctx-analysis` — multi-file audits, repo-wide analysis

Route source of truth: `.ai/MODEL_STATE.json` — resolve at runtime, never assume.
Never silently switch to a paid route without explicit approval.

**Reserve Claude for:** architecture decisions, tradeoff analysis, integration planning, final patch review, security changes, incident triage.

**High-risk changes (require Claude review):**
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

    dotnet build -c Release
    tail -f ~/emby-dev-data/logs/embyserver.txt
    # Config UI: http://localhost:8096/web/configurationpage?name=EmbyStreams

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
