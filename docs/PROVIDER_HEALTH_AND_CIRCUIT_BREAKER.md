# Provider Health & Circuit Breakers

## 1. The Health Tracker
`ResolverHealthTracker` is a singleton that monitors every outgoing request to AIOStreams.

## 2. Circuit Breaker States
- **Closed (Healthy):** Requests flow normally.
- **Open (Failing):** If 5 consecutive timeouts or 500-errors occur, the circuit opens. All Primary requests are immediately diverted to the Backup Manifest without hitting the network.
- **Half-Open (Testing):** After 5 minutes, the system allows ONE "probe" request. If successful, the circuit closes.

## 3. Rate Limit Management (The 429 Guard)
Throttling is NOT a failure; it is a signal.
- When a 429 is detected, the provider is marked `Throttled` for the duration of the `Retry-After` header.
- All background tasks (Marvin, Expansion) must yield to Playback requests during a throttle window.
