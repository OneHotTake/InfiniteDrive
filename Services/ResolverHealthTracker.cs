using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Tracks resolver health and implements circuit breaker pattern.
    /// Prevents repeated calls to failing resolvers.
    /// </summary>
    public class ResolverHealthTracker
    {
        private readonly ILogger _logger;
        private readonly DatabaseManager? _db;
        private readonly object _lock = new();
        private readonly Dictionary<string, ResolverState> _states = new();

        private const int FailureThreshold = 3;
        private static readonly TimeSpan[] BackoffIntervals = new[]
        {
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5) // Cap at 5 minutes
        };

        public ResolverHealthTracker(ILogger logger, DatabaseManager? db = null)
        {
            _logger = logger;
            _db = db;
        }

        /// <summary>
        /// Records a successful resolution for the given resolver.
        /// </summary>
        public void RecordSuccess(string resolverName)
        {
            lock (_lock)
            {
                if (_states.TryGetValue(resolverName, out var state))
                {
                    state.ConsecutiveFailures = 0;
                    state.CircuitState = CircuitState.Closed;
                    state.BackoffIndex = 0;
                    _logger.LogDebug(
                        "[CircuitBreaker] Resolver {Resolver} recovered, circuit closed",
                        resolverName);
                }
            }
            _ = PersistStateAsync();
        }

        /// <summary>
        /// Records a failed resolution for the given resolver.
        /// Returns true if the circuit should be opened.
        /// </summary>
        public bool RecordFailure(string resolverName)
        {
            bool opened;
            lock (_lock)
            {
                if (!_states.TryGetValue(resolverName, out var state))
                {
                    state = new ResolverState();
                    _states[resolverName] = state;
                }

                state.ConsecutiveFailures++;
                state.LastFailureTime = DateTime.UtcNow;

                // Open circuit if threshold reached
                if (state.ConsecutiveFailures >= FailureThreshold && state.CircuitState != CircuitState.Open)
                {
                    var backoffIndex = Math.Min(
                        state.BackoffIndex,
                        BackoffIntervals.Length - 1);

                    var backoff = BackoffIntervals[backoffIndex];
                    var jitter = (int)(backoff.TotalMilliseconds * Random.Shared.Next(-10, 10) / 100);

                    state.CircuitState = CircuitState.Open;
                    state.CircuitOpenUntil = DateTime.UtcNow.Add(backoff).Add(TimeSpan.FromMilliseconds(jitter));
                    state.BackoffIndex = Math.Min(backoffIndex + 1, BackoffIntervals.Length - 1);

                    _logger.LogWarning(
                        "[CircuitBreaker] Circuit opened for {Resolver} (failures={Failures}, backoff={Backoff}s + {Jitter}ms)",
                        resolverName, state.ConsecutiveFailures, backoff.TotalSeconds, jitter);

                    opened = true;
                }
                else
                {
                    opened = false;
                }
            }
            _ = PersistStateAsync();
            return opened;
        }

        /// <summary>
        /// Checks if a resolver should be skipped due to open circuit.
        /// </summary>
        public bool ShouldSkip(string resolverName)
        {
            lock (_lock)
            {
                if (_states.TryGetValue(resolverName, out var state))
                {
                    // Circuit is open, check if backoff has elapsed
                    if (state.CircuitState == CircuitState.Open)
                    {
                        if (DateTime.UtcNow >= state.CircuitOpenUntil)
                        {
                            // Half-open: allow one request to test
                            _logger.LogDebug(
                                "[CircuitBreaker] Circuit half-opening for {Resolver}",
                                resolverName);
                            state.CircuitState = CircuitState.HalfOpen;
                            return false;
                        }

                        // Circuit is still open, skip this resolver
                        _logger.LogDebug(
                            "[CircuitBreaker] Skipping {Resolver} - circuit open until {When}",
                            resolverName, state.CircuitOpenUntil);
                        return true;
                    }

                    // Circuit is closed or half-open, allow requests
                    if (state.CircuitState == CircuitState.HalfOpen)
                    {
                        _logger.LogDebug(
                            "[CircuitBreaker] {Resolver} in half-open state",
                            resolverName);
                    }
                    return false;
                }
            }

            return false;
        }

        private async Task PersistStateAsync()
        {
            if (_db == null) return;
            try
            {
                var snapshot = new Dictionary<string, object>();
                lock (_lock)
                {
                    foreach (var kvp in _states)
                    {
                        snapshot[kvp.Key] = new
                        {
                            CircuitState = (int)kvp.Value.CircuitState,
                            CircuitOpenUntil = kvp.Value.CircuitOpenUntil.ToString("o"),
                            kvp.Value.ConsecutiveFailures,
                            kvp.Value.BackoffIndex
                        };
                    }
                }
                var json = JsonSerializer.Serialize(snapshot);
                await _db.SetCircuitBreakerStateAsync(json);
            }
            catch { /* best effort */ }
        }

        public void RestoreState()
        {
            if (_db == null) return;
            try
            {
                var json = _db.GetCircuitBreakerState();
                if (string.IsNullOrEmpty(json)) return;

                var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var state = new ResolverState();
                    if (prop.Value.TryGetProperty("CircuitState", out var csEl))
                        state.CircuitState = (CircuitState)csEl.GetInt32();
                    if (prop.Value.TryGetProperty("CircuitOpenUntil", out var coEl))
                        state.CircuitOpenUntil = DateTime.Parse(coEl.GetString()!);
                    if (prop.Value.TryGetProperty("ConsecutiveFailures", out var cfEl))
                        state.ConsecutiveFailures = cfEl.GetInt32();
                    if (prop.Value.TryGetProperty("BackoffIndex", out var biEl))
                        state.BackoffIndex = biEl.GetInt32();

                    lock (_lock) { _states[prop.Name] = state; }
                }
                _logger.LogInformation("[CircuitBreaker] Restored circuit state from database ({Count} entries)", _states.Count);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[CircuitBreaker] Could not restore circuit state");
            }
        }

        private enum CircuitState
        {
            Closed,
            HalfOpen,
            Open
        }

        private class ResolverState
        {
            public int ConsecutiveFailures { get; set; }
            public DateTime LastFailureTime { get; set; }
            public CircuitState CircuitState { get; set; }
            public DateTime CircuitOpenUntil { get; set; }
            public int BackoffIndex { get; set; }
        }
    }
}
