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

## Sprint 215 Complete (2026-04-13)
- Settings Redesign: wizard-based UI → flat 7-tab Apple-style layout
- New tabs: providers, libraries, sources, security, parental, health, repair
- All wizard code removed from configurationpage.js
