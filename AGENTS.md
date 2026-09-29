# Working on InfiniteDrive

- Read the README, documentation index and affected implementation before changing
  behavior. `docs/archive/` is historical evidence, not current instructions.
- Preserve user changes; use a focused branch and reviewable pull request.
- Run `python3 scripts/check-repository.py` for repository changes and
  `./scripts/build-container.sh` for build or runtime changes. The latter tests and
  publishes against the pinned Emby ABI; never substitute server-core NuGet packages.
- Never commit Emby assemblies, binaries, databases, logs, session state, credentials,
  private manifests, signed stream URLs or full configuration exports.
- Back up persistent state before migrations. Preserve additive import tables and
  user state; do not reset databases to make a test or upgrade pass.
- Preserve owned-media precedence, explicit blocks, Marvin retention and bounded
  recovery. Missing files alone do not establish a user block.
- Native QA belongs in an isolated Emby instance. Never include `tools/ImportQa`
  in a production DLL. Repository cleanup does not authorize production deployment.
- Keep current docs under `docs/`, link them from its index, and label dated QA.
  Temporary investigation notes belong in ignored `.ai/` or outside the repository.
- Report what passed and any limits. Compilation and indexing do not prove playback.
