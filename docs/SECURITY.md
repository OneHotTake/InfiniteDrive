# Security and private data

Treat provider manifests, configuration identifiers, API keys, resolved stream
URLs and signed playback links as credentials. Do not include them in issues,
screenshots, source files or CI artifacts.

Current STRM files contain resolved URLs. Cached stream/version data and plugin
configuration can also contain sensitive values. Protect the Emby data directory,
managed roots and backups. The historical API-key/signed-resolver design in the
archive is not a guarantee about the current playback path. Native Emby playback
and client behavior determine where resolved URLs are delivered; do not assume
they remain LAN-only.

Administrative import routes require an Emby administrator user. A server API key
without that user is insufficient for recovery activation. Read-only import status
does not contact providers or queue work. These are specific tested boundaries,
not a security audit of every endpoint.

`SensitiveUrlRedactor` handles diagnostics, but review logs before sharing them.
Keep service configuration and credentials in the deployment's protected store.
CI uses synthetic data and needs no production account or provider secret.

Removing an accidentally committed credential does not remove it from history.
Revoke or rotate it through the owning service and coordinate separate history
remediation if needed.
