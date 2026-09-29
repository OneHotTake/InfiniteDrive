# Import reconciliation QA — September 29, 2026

Verified against Emby Server 4.10.0.40 on an isolated development instance.
Production was not deployed or restarted. The prior development plugin artifact
was restored after testing; temporary libraries, collections, media, database and
the password-protected QA account were removed. Backups and test evidence remain
in the private development build area.

## Automated checks

82 tests passed, zero failed or skipped. Release compilation and publication are
warning-free against the exact Emby SDK. The suite includes the existing 52 tests
and 30 new checks, using Emby's SQLite provider with real temporary databases.
Coverage includes durable unchanged-inventory retry, parent-state independence,
due-work selection, alias deduplication/conflicts, daily budgets, provider pause,
legacy ambiguity, explicit removal, concurrent suppression, indexing limits,
unsafe paths, and failed/empty/unchanged multi-version publication.

## Native Emby checks

| Check | Result |
|---|---|
| Observe two synthetic episodes | Zero resolutions and files; both gaps visible |
| Simulated episode-2 failure | Episode 1 published; episode 2 retained in backoff |
| Server restart with pending gap | Retry state and publication observations retained |
| Native child discovery | Episode 1 acquired matching native identity, numbering and path |
| Retry with identical expected inventory | Exactly one resolution and publication, for episode 2 |
| Subsequent native read-back | Two episode identities indexed across four version files; complete |
| Delete episode 1 | Zero resolutions; review-removal then suppression |
| Explicit restore | Exactly one resolution/publication; native indexing re-established |
| Existing native identities | Episode-1 native IDs retained through retry/restoration |
| Provider API | `GetAllEpisodes` executed successfully; zero provider episodes in the offline fixture |
| Collection API | Seeded QA collection and membership confirmed by native read-back |
| Access controls | Anonymous and non-admin requests denied; admin accepted |
| Repair admission | Missing observation baseline rejected |
| Status reads | Repeated GET did not launch observation or repair |

The beta's broad library scan was too slow to isolate this fixture promptly.
The test used Emby's `Folder.ValidateChildren` on the fixed fixture root with
`MetadataRefreshOptions`, then the production inventory reader checked actual
native records. The production worker uses library-monitor notifications plus a
bounded native scan fallback. Neither elapsed time nor a successful notification
was treated as indexed evidence.

These tests used controlled metadata and synthetic stream URLs. They verify
publication and native indexing, not debrid playback. The offline provider result
does not independently establish the completeness of a real upstream TV catalog.
The existing broader catalog pagination/truncation behavior is outside this change.
Franchise expansion and partial owned-series filling remain out of scope.
