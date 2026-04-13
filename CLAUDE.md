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
