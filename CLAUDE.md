# InfiniteDrive — CLAUDE.md (Token Slayer Edition — v2026-04-12)

## Core Rules (Violate = Session Terminated)
- Simplicity Over Complexity. Always.
- NO version numbers in code, classes, or methods. Ever.
- Read **ONLY** these two files at session start, then STOP:
  1. .ai/CURRENT_TASK.md
  2. REPO_MAP.md (root)
- Never read any other file unless the task explicitly names it.
- Never read a file >200 lines in full. Use targeted grep/line-range.
- Never re-read any file in the same session.
- Never read any file under .ai/archive/ (or its subdirectories) unless CURRENT_TASK.md explicitly names the exact file for diagnosis/troubleshooting.
- **Max 3 files touched per subtask. Max 1 working solution delivered.**
- After every subtask: append ONE LINE status to .ai/CURRENT_TASK.md and stop.

## Model Policy (This Is Where We Save Real Money)
| Task Type                  | Model     |
|----------------------------|-----------|
| Grep, search, summarize, nav | Haiku    |
| Code gen, review, refactor | Sonnet   |
| Architecture / novel       | Sonnet   |
(Opus banned unless human explicitly says "use Opus" + TOKEN_BUDGET.md <70% used)

Research tasks start on Haiku. Only escalate if Haiku explicitly says "Sonnet required for correctness".

## Output Rules
- No preambles. No explanations. Diffs only (changed lines + 3 context).
- One-sentence summary only when task complete.
- Update .ai/SESSION_SUMMARY.md with exact model + estimated tokens **before** finishing.

## Mandatory: Task Scope Ceiling
Every CURRENT_TASK.md must start with:
SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution
text## State Is King
All state in .ai/. Never trust chat history.
At sprint end: update REPO_MAP.md (one-liner only), archive old sprint, commit, push.

## Quick Ref
dotnet build -c Release
./emby-reset.sh (canonical reset)

High-risk files (require human review): Plugin.cs, DatabaseManager.cs, any auth/resolution code.

## Database Layer Architecture (Enforced)

### JSON-First Pattern
- `raw_meta_json` column is the source of truth for rich metadata (images, cast, genres, etc.)
- Prefer targeted queries (`SELECT raw_meta_json FROM ...`) over full-row mappers when only JSON data is needed
- `GetRawMetaJsonByProviderIdAsync()` is the canonical lookup — bypasses the row mapper entirely

### Schema Policy (Alpha)
- This is alpha code → no schema evolution / migrations allowed.
- Only a single `CREATE TABLE IF NOT EXISTS` with bare-minimum columns + JSON fields.
- All schema changes must be manual and destructive (drop & recreate is fine).
- Never re-introduce `MigrateSchema`, `ALTER TABLE`, version tables, or positional column handling.

### Row Mapper Rules
- **`SELECT *` queries**: MUST use name-based column lookup via `ColMap(table)` + `GetStr/GetReqStr/GetInt/...` helpers. Column order depends on DDL and is not stable.
- **Explicit column list queries** (`SELECT id, imdb_id, ...`): Positional indexing (`r.GetString(0)`) is safe because the column order is controlled by the query text.
- **Never add a new column** to a `SELECT *` query's table without verifying the name-based mapper picks it up automatically.
- **Never share a mapper** between queries with different column lists.

### SQL Provider Matching
- Always use `lower()` on BOTH sides of provider/id comparisons:
  ```sql
  WHERE lower(json_extract(value, '$.provider')) = lower(@provider)
  ```

## Sprint Planning (Token-Optimized)

ALWAYS start from .ai/SPRINT_TEMPLATE.md (the new 38-line version).

1. Copy to .ai/sprint-XXX.md
2. Fill ONLY: Why (2 sentences), Tasks (FIX-XXX blocks), Verification checklist.
3. **No research, no phases, no tables, no prose.** Research lives elsewhere.
4. Commit the empty sprint file **before** any code work.
5. Update .ai/SESSION_SUMMARY.md with tokens burned this sprint (estimate is fine).

## Sprint Completion Ritual

1. Update REPO_MAP.md and BACKLOG.md only.
2. git add .ai/ && git commit -m "chore: end-of-sprint XXX"
3. Push immediately. No accumulated work.
4. Log tokens saved in .ai/SESSION_SUMMARY.md (one line).
