# Contributing

Start with the [developer guide](docs/dev-guide.md), [architecture](docs/architecture.md)
and [repository instructions](AGENTS.md).

Create a focused branch from `main`. Describe the problem, resulting behavior,
tests and rollback in a pull request. Update affected documentation in the same
change. Avoid mixing runtime redesign with file organization.

Run `python3 scripts/check-repository.py` before submitting. Build, reference or
runtime changes also require `./scripts/build-container.sh`. CI runs both checks.
Use a disposable isolated Emby instance for native tests; never use production
as a fixture or include credentials in an issue, artifact or source file.

Release tags and GitHub release assets preserve published versions. Build artifacts
stay ignored locally. Merge cleanup without changing plugin versions; make versioned
releases only for intentional plugin changes.
