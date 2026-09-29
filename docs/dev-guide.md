# Developer guide

## Build and test

Use a Linux amd64 Docker host, Git and Python 3. Other host architectures need
working Docker amd64 emulation. The default Emby image is pinned to the 4.10.0.40
ABI declared in `plugin.json`; the .NET SDK is 8.0.

```sh
python3 scripts/check-repository.py
./scripts/build-container.sh
```

The build extracts Emby's managed references into ignored `libs/`, builds a small
SDK image with native SQLite, restores dependencies, runs xUnit and publishes
`artifacts/InfiniteDrive.dll` plus `plugin.json`. It prints the DLL checksum.
It does not start Emby or deploy the plugin.

`EMBY_IMAGE` and `DOTNET_SDK_IMAGE` override the defaults for explicit compatibility
experiments. Changing the target ABI requires matching references, manifest updates
and native validation; compilation alone is insufficient.

With the exact references, .NET 8 and native SQLite already installed:

```sh
dotnet test Tests/InfiniteDrive.Tests.csproj -c Release
dotnet publish InfiniteDrive.csproj -c Release -o artifacts
```

The repository does not redistribute Emby DLLs. Keep `libs/`, `bin/`, `obj/` and
`artifacts/` untracked. Automated tests require no service credentials.

## Source map

| Path | Responsibility |
|---|---|
| `Plugin.cs`, `PluginConfiguration.cs` | Registration and persisted settings |
| `UI/Settings/` | Native administrative pages |
| `Configuration/` | Embedded web Discover interface |
| `Tasks/` | Marvin orchestration and catalog/import stages |
| `Services/`, `Services/Api/` | Resolution, publication, discovery, recovery and endpoints |
| `Data/`, `Repositories/` | SQLite persistence and repository contracts |
| `Models/` | Shared state and transfer types |
| `Tests/` | Automated xUnit regression suite |
| `tools/ImportQa/` | Excluded, isolated native QA harness |
| `docs/archive/` | Historical evidence; not setup instructions |

## Native QA and release

Use an isolated Emby instance with its own configuration and managed media roots.
Back up its prior plugin/configuration/database. Keep production media read-only
and control the instance through its actual service manager.

The [import QA harness](../tools/ImportQa/README.md) is optional and never part of
a release build. [Recorded native QA](import-reconciliation-qa.md) distinguishes
synthetic indexing tests from playback acceptance. Test playback separately when
changing resolution or media-source handling.

For a release, keep project and manifest versions consistent, run the full build,
and attach the DLL, manifest and checksum to the matching GitHub release. Deploy
only in a separate authorized maintenance step after checking active use.
Rollback restores reviewed prior artifacts while preserving newer user data and
additive schema; do not blindly overwrite a live database.
