#!/bin/bash
set -euo pipefail

echo "🚀 Token Annihilation v3 — executing full cleanup + file replacement now"
cd "$(git rev-parse --show-toplevel)"

# =============================================================================
# 1. FINAL ARCHIVE SWEEP — nuke everything except the 3 keepers in .ai/
# =============================================================================
mkdir -p .ai/archive/old-sprints .ai/archive/reviews .ai/archive/2026-04

# Move every known bloat file (|| true so missing files don't stop us)
mv .ai/CODE_REVIEW_*          .ai/archive/reviews/      2>/dev/null || true
mv .ai/GAP_ANALYSIS.md        .ai/archive/              2>/dev/null || true
mv .ai/INVENTORY.md           .ai/archive/              2>/dev/null || true
mv .ai/MAINTENANCE.md         .ai/archive/              2>/dev/null || true
mv .ai/RECOVERY_SUMMARY.md    .ai/archive/              2>/dev/null || true
mv .ai/E2E_TEST_PLAN.md       .ai/archive/              2>/dev/null || true
mv .ai/SCHEMA.md              .ai/archive/              2>/dev/null || true
mv .ai/sprint-*.md            .ai/archive/old-sprints/  2>/dev/null || true
mv .ai/SPRINT_*.md            .ai/archive/old-sprints/  2>/dev/null || true
mv .ai/FINDINGS_*.md          .ai/archive/              2>/dev/null || true

# Delete the old REPO_MAP that used to live inside .ai/
rm -f .ai/REPO_MAP.md

# Keep the latest sprint ONLY if it exists (uncomment the line below if you want to preserve sprint-211.md)
# mv .ai/archive/old-sprints/sprint-211.md .ai/ 2>/dev/null || true

echo "✅ .ai/ archive complete — only CURRENT_TASK.md, SESSION_SUMMARY.md and SPRINT_TEMPLATE.md remain active"

# =============================================================================
# 2. OVERWRITE CLAUDE.md with the Token Slayer Edition (exact content)
# =============================================================================
cat << 'CLAUDE_EOF' > CLAUDE.md
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
CLAUDE_EOF

# =============================================================================
# 3. CREATE root REPO_MAP.md (ultra-lean 150-token version)
# =============================================================================
cat << 'REPO_EOF' > REPO_MAP.md
# REPO_MAP.md (ultra-lean — updated only at sprint end)

Root
- Plugin.cs                  : entry point + registration
- PluginConfiguration.cs     : all settings + validation
- CLAUDE.md                  : token rules (this file)

Configuration/               : UI HTML/JS only (never read in backend)
Data/                        : Schema + DatabaseManager (SQLite)
Services/                    : all business logic
Tasks/                       : background tasks
Models/                      : POCO only

.ai/
- CURRENT_TASK.md            : active task only (read first)
- SESSION_SUMMARY.md         : token ledger
- SPRINT_TEMPLATE.md         : one-page only

Everything else archived. Max 3 files per subtask. Never re-read.
REPO_EOF

# =============================================================================
# 4. CREATE root TOKEN_BUDGET.md
# =============================================================================
cat << 'BUDGET_EOF' > TOKEN_BUDGET.md
# TOKEN_BUDGET.md — Monthly Opus Burn Tracker

Monthly budget: $[YOUR NUMBER HERE]
Current spend this month: $0.00 (reset 1st)
Last task: Haiku (est Xk tokens)

Rule: If spend > 70% → force Haiku-only for all tasks. No exceptions.
Every session MUST read and update this file.
BUDGET_EOF

# =============================================================================
# 5. ENFORCE SCOPE_CEILING in .ai/CURRENT_TASK.md (prepend if file exists)
# =============================================================================
if [ -f .ai/CURRENT_TASK.md ]; then
  echo 'SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution

' > /tmp/scope_header.txt
  cat /tmp/scope_header.txt .ai/CURRENT_TASK.md > /tmp/current_new.txt
  mv /tmp/current_new.txt .ai/CURRENT_TASK.md
  rm -f /tmp/scope_header.txt /tmp/current_new.txt
else
  echo "SCOPE_CEILING: Max 3 files | Deliverable: diff only | Stop after first working solution" > .ai/CURRENT_TASK.md
fi

echo "✅ All files replaced and CURRENT_TASK.md updated with mandatory scope ceiling"

# =============================================================================
# 6. Stage everything for commit
# =============================================================================
git add CLAUDE.md REPO_MAP.md TOKEN_BUDGET.md .ai/

echo ""
echo "✅ All done. Files are staged."
echo "Now run this exact command to commit:"
echo 'git commit -m "chore(token-annihilation-v3): nuke 25+ files from .ai/, replace CLAUDE.md + REPO_MAP.md with token slayer rules, enforce scope ceilings" && git push'
echo ""
echo "After that, paste the output of these two commands back here:"
echo "ls -la .ai/"
echo "wc -l CLAUDE.md REPO_MAP.md .ai/CURRENT_TASK.md"
echo ""
echo "We are now officially out of the token-burning dark ages."
