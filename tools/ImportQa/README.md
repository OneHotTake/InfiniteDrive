# Isolated Emby import QA

`ImportLabService.cs` is excluded from the plugin build by the existing `tools/**`
compile exclusion. Copy it to `Services/ImportLabService.cs` only in a separate lab
build. Preserve the deployment's existing freeze and integrations. Never put this
service in a production artifact.

Requires an administrator user session and `INFINITEDRIVE_LAB_FREEZE=1`.
Fixed fixture paths are `/media/import-reconciliation-qa` and
`/config/data/import-reconciliation-qa`. The helper refuses a second prepare over
an existing fixture and accepts no arbitrary path, ID, URL, or SQL input.

POST `/InfiniteDrive/ImportQa` with a `Step`:

1. `prepare`: create a private fixture database and local NFO metadata; observe
   two released episodes without stream resolution or publication.
2. Register `/media/import-reconciliation-qa/TV` as a temporary TV library with
   remote metadata/image fetchers disabled.
3. `fail-second`: publish two versions of episode 1; simulate no sources for
   episode 2. Confirm awaiting-indexing versus retrying, and incomplete coverage.
4. Restart beta with its freeze preserved. `status` must retain those outcomes.
5. `discover`: validate only the fixed fixture folder using Emby's native child
   discovery API, then read back native identity/numbering/path observations.
6. `retry`: advance the injected clock by eight hours, leaving inventory unchanged;
   the failed episode is retried. `discover` then verifies both native episodes.
7. `delete-first`: delete only the fixture episode's managed STRMs and verify that
   one resolution/publication automatically refills it. `discover` verifies indexing.
   `prune-delete-first` marks the catalog title removed and deletes the fixture
   episode: no refill. `rejoin` restores catalog eligibility and refills it.
   `block-delete-first` persists an explicit block and deletes the episode: no refill,
   including after restart.
8. `provider-probe`: exercise `GetAllEpisodes` on the native fixture series. An
   empty/unavailable response in an offline lab is reported, not completeness.
9. `collection`: create the named QA-only collection and verify native membership.

Inspect API responses without publishing credentials. The returned targets are
synthetic `example.invalid` URLs, not playback tests. Clean up only the named QA
collection/library, fixture media/database, and temporary QA account/session. Keep
pre-test snapshots and the prior plugin artifact for rollback. Do not reset whole
beta datasets or restart production.

Numbering regression: on a freshly prepared fixture, `partial-numbering` passes
only episode 1 through the real per-key numbering policy. Its STRMs publish;
episode 2 stays excluded. `discover` observes native indexing.
`recheck-numbering` advances the fixture clock eight hours and restores the
complete source inventory; episode 2 becomes eligible without a database reset.
These are synthetic targets and do not establish playback.
