# Provider health and backoff

Every configured manifest is an active peer. InfiniteDrive does not model one as
primary and another as a disabled backup.

## Observed states

- **Healthy:** requests and candidate resolution proceed normally.
- **Throttled:** HTTP 429 and Retry-After state pause background work.
- **Unavailable:** connection or 5xx failures leave that peer temporarily
  unavailable while another configured peer may still answer.
- **Recovering:** a later successful request clears transient failure state.

Playback remains higher priority than background cache warming. Background work
uses bounded concurrency and respects provider backoff. The database call count
is telemetry; there is no configurable daily API-budget rule.

An item is not pruned because one provider is down, throttled, or temporarily
missing. Absence must be observed across active catalog state and repeated safe
reconciliation, with playlist/watched protection and managed-path ownership.

See [COOLDOWN.md](COOLDOWN.md) and [stream resolution](STREAM_RESOLUTION.md).
