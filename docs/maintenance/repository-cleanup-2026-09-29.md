# Repository cleanup — September 29, 2026

## Plan and scope

Baseline: `ed08bc0` on main, after the 0.42.4 release and README update.

1. Keep one documentation index and current setup/developer/architecture guides.
2. Archive engineering investigations and old plans with explicit historical labels.
3. Remove scratch exports, abandoned rewrite copies, tracked AI session state and
   obsolete scripts. Git history retains the originals; do not rewrite history.
4. Repair the container build to extract the references used by the current tests
   and supply native SQLite. Add build/test and repository-hygiene CI.
5. Verify links, release metadata, the complete regression suite and publication.
6. Publish a cleanup PR. Retire only branches proven fully merged, recording their
   tips below; preserve unmerged experiments and all release tags/assets.

Production plugin behavior, databases and service configuration are outside scope.
No C# source change or new release is needed. Revert the cleanup commit to restore
removed files. Restore a retired branch with `git branch <name> <tip>` and push it.

## Removed artifacts

The duplicated `newdocs/` rewrite was never the documentation entry point.
`Export.txt` was a chat transcript, `configurationpage.js.new` an unused UI draft,
and `.ai/` contained session-specific state despite being ignored. The token
ledger and `nuke.sh` regenerated obsolete agent constraints. Repository dump
helpers duplicated ignored exports; Sonar referenced a missing solution. Old
Emby helpers referenced obsolete installations and used broad process kills or
fixed-path data deletion. Use the tested build and isolated QA guide instead.
All originals remain available at the baseline commit.

## Branch audit

| Branch | Tip | Decision |
|---|---|---|
| codex/overnight-hardening-20260910 | 4173a44f18a560beb350a4db42fcd4a1e92aa8fc | Fully merged; removed after checking tip |
| feat/import-reconciliation | 90f43aed18cfd94e0648e1b60d1d2b164965bb26 | Fully merged; removed after checking tip |
| feature/multi-version-strm-prewrite | c680b68abc29c9662674938a08b4e8d9a8630ce7 | Fully merged; removed after checking tip |
| virtual | 743176e5e13d95f1fbf2c0de67bf7d41cbf90586 | Fully merged; removed after checking tip |
| virtual-library | da4f55418c7e0aa72f4ccbededcc21288688bd49 | Unmerged; preserve |
| virtual-library-v2 | 129be9a789445d64bae4157fb6294d0d1c02392f | Unmerged; preserve |

## Verification

Verified in an isolated Linux amd64 build: all 86 tests passed, none skipped;
Release publication had no warnings. The DLL SHA-256 exactly matches v0.42.4:
`885e0257a61bcf19560eacc67458a45f563530293fc68d392dd101069b2ebf1b`.
No C# files or release metadata changed. Repository checks cover current local
Markdown targets/fences, ignored artifacts and matching project/manifest versions.
Deliberate broken-link and unclosed-fence fixtures were rejected. Shell/Ruby syntax
checks and a high-confidence credential-pattern scan passed (not a full security
audit). Native playback is not retested by repository CI.

Documentation review corrected the obsolete signed playback design, claimed
My Picks/My Lists tabs, removed anime settings, universal eight-version ceiling,
recovery controls and blanket database-restore advice. Historical reports retain
their dates and limitations. The settings generator no longer labels hardcoded
properties as freshly live-verified.

GitHub CI repeats repository checks and the exact-ABI test/publish build for PRs
and main. It has read-only repository permissions and no production secrets.

GitHub housekeeping: project description now identifies the plugin; automatic
branch deletion after PR merge is enabled. Four merged branches were removed
with their audited tips checked. Both unmerged experiments and every release
remain. Restore the former blank description or disable automatic branch deletion
in repository settings to roll back those two metadata changes.
