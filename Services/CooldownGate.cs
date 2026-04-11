using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Whether the upstream AIOStreams instance is shared (public) or private (self-hosted).
    /// Determines which cooldown profile applies. Auto-detected from manifest URL.
    /// </summary>
    public enum InstanceType { Shared, Private }

    /// <summary>
    /// Category of HTTP operation. Each kind maps to a different base delay in the profile.
    /// </summary>
    public enum CooldownKind { CatalogFetch, StreamResolve, Enrichment, Cinemeta }

    /// <summary>
    /// Compiled-in throttle constants per instance type.
    /// Not user-editable from UI. Advanced users can edit InfiniteDrive.xml directly.
    /// </summary>
    public sealed class CooldownProfile
    {
        public int HttpBaseDelayMs { get; init; }
        public int JitterMs { get; init; }
        public int HttpTimeoutSeconds { get; init; }
        public int CatalogSourcesPerRun { get; init; }
        public int EnrichmentPerRun { get; init; }
        public int RehydrationPerRun { get; init; }
        public int CinemetaDelayMs { get; init; }
        public int GlobalCooldownSeconds { get; init; }

        public static readonly CooldownProfile Shared = new()
        {
            HttpBaseDelayMs       = 1000,
            JitterMs              = 300,
            HttpTimeoutSeconds    = 8,
            CatalogSourcesPerRun  = 2,
            EnrichmentPerRun      = 42,
            RehydrationPerRun     = 500,
            CinemetaDelayMs       = 700,
            GlobalCooldownSeconds = 900,
        };

        public static readonly CooldownProfile Private = new()
        {
            HttpBaseDelayMs       = 200,
            JitterMs              = 80,
            HttpTimeoutSeconds    = 12,
            CatalogSourcesPerRun  = 6,
            EnrichmentPerRun      = 150,
            RehydrationPerRun     = 2000,
            CinemetaDelayMs       = 200,
            GlobalCooldownSeconds = 120,
        };

        public static CooldownProfile For(InstanceType type) =>
            type == InstanceType.Private ? Private : Shared;

        /// <summary>
        /// Returns the base delay in milliseconds for the given operation kind.
        /// </summary>
        public int DelayFor(CooldownKind kind) => kind switch
        {
            CooldownKind.CatalogFetch  => HttpBaseDelayMs,
            CooldownKind.StreamResolve => HttpBaseDelayMs,
            CooldownKind.Enrichment    => HttpBaseDelayMs,
            CooldownKind.Cinemeta      => CinemetaDelayMs,
            _ => HttpBaseDelayMs,
        };
    }

    /// <summary>
    /// Single gate that coordinates HTTP throttling for all AIOStreams / Cinemeta calls.
    /// Replaces scattered <c>Task.Delay(ApiCallDelayMs)</c> with profile-aware delays,
    /// jitter, and a global backoff on 429 responses.
    /// </summary>
    public sealed class CooldownGate
    {
        private readonly Func<PluginConfiguration> _configAccessor;
        private readonly ILogger _logger;
        private readonly Func<int, int, int> _jitterSource;
        private DateTimeOffset _globalCooldownUntil = DateTimeOffset.MinValue;

        // Soft dependency — may be null during tests or before Plugin initialisation
        private ProgressStreamer? _progressStreamer;

        // Three-strikes tracking: rolling queue of Tripped() timestamps
        private readonly Queue<DateTimeOffset> _tripHistory = new();
        private bool _suggestPrivateInstance;

        /// <summary>The resolved instance type (Shared or Private).</summary>
        public InstanceType Instance => _configAccessor().ResolvedInstanceType;

        /// <summary>The active cooldown profile based on instance type.</summary>
        public CooldownProfile Profile => CooldownProfile.For(Instance);

        /// <summary>
        /// True when 3+ 429s occurred in the last hour on a Shared instance.
        /// Dashboard may show a one-shot "consider a private instance" suggestion.
        /// </summary>
        public bool SuggestPrivateInstance => _suggestPrivateInstance;

        /// <summary>
        /// UTC timestamp until which all HTTP is paused, or <see cref="DateTimeOffset.MinValue"/>
        /// if no global cooldown is active.
        /// </summary>
        public DateTimeOffset GlobalCooldownUntil => _globalCooldownUntil;

        /// <summary>
        /// Optional progress streamer for emitting cooldown events to the dashboard.
        /// Set after construction when ProgressStreamer is available.
        /// </summary>
        public ProgressStreamer? ProgressStreamer
        {
            get => _progressStreamer;
            set => _progressStreamer = value;
        }

        /// <summary>
        /// Production constructor. Uses <see cref="Random.Shared"/> for jitter.
        /// </summary>
        public CooldownGate(Func<PluginConfiguration> configAccessor, ILogger logger)
            : this(configAccessor, logger, (min, max) => Random.Shared.Next(min, max))
        {
        }

        /// <summary>
        /// Testable constructor. Inject a deterministic jitter source for unit tests.
        /// </summary>
        internal CooldownGate(Func<PluginConfiguration> configAccessor, ILogger logger, Func<int, int, int> jitterSource)
        {
            _configAccessor = configAccessor;
            _logger = logger;
            _jitterSource = jitterSource;
        }

        /// <summary>
        /// Call before every HTTP request to AIOStreams / Cinemeta.
        /// Sleeps for <c>profile.DelayFor(kind) +/- jitter</c> and respects
        /// the global cooldown window (set by <see cref="Tripped"/>).
        /// </summary>
        public async Task WaitAsync(CooldownKind kind, CancellationToken ct)
        {
            // If we're in a global 429 cooldown, sleep until it expires
            if (DateTimeOffset.UtcNow < _globalCooldownUntil)
            {
                var remaining = _globalCooldownUntil - DateTimeOffset.UtcNow;
                _logger.LogDebug("[cooldown] Global cooldown active — waiting {Remaining:F1}s", remaining.TotalSeconds);
                await Task.Delay(remaining, ct).ConfigureAwait(false);
            }

            var baseDelay = Profile.DelayFor(kind);
            var jitter = _jitterSource(-Profile.JitterMs, Profile.JitterMs);
            var totalDelay = Math.Max(0, baseDelay + jitter);

            if (totalDelay > 0)
                await Task.Delay(totalDelay, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Call on any 429 (Too Many Requests) response. Sets a global cooldown
        /// window that pauses all HTTP until it expires.
        /// </summary>
        /// <param name="retryAfter">
        /// Optional <c>Retry-After</c> header value. Falls back to
        /// <see cref="CooldownProfile.GlobalCooldownSeconds"/> if null.
        /// </param>
        public void Tripped(TimeSpan? retryAfter = null)
        {
            var wait = retryAfter ?? TimeSpan.FromSeconds(Profile.GlobalCooldownSeconds);
            _globalCooldownUntil = DateTimeOffset.UtcNow + wait;

            // Three-strikes tracking
            _tripHistory.Enqueue(DateTimeOffset.UtcNow);
            while (_tripHistory.Count > 0 && _tripHistory.Peek() < DateTimeOffset.UtcNow.AddHours(-1))
                _tripHistory.Dequeue();

            if (_tripHistory.Count >= 3 && Instance == InstanceType.Shared)
            {
                _suggestPrivateInstance = true;
                _logger.LogInformation("[cooldown] 3+ rate limits in 1h on shared instance — suggesting private instance");
            }

            _logger.LogWarning("[cooldown] AIOStreams 429 — pausing all HTTP for {Seconds:F0}s", wait.TotalSeconds);

            // Emit progress event for dashboard badge (Sprint 155E)
            _progressStreamer?.Publish(new ProgressEvent(
                Type: "upstream_cooldown",
                Message: "Upstream busy — pausing briefly to stay a good neighbour.",
                Progress: 0,
                Details: $"{{\"until\":\"{_globalCooldownUntil:O}\",\"reason\":\"shared_instance_rate_limit\",\"suggestPrivate\":{_suggestPrivateInstance.ToString().ToLowerInvariant()}}}"
            ));
        }

        /// <summary>
        /// Parses the <c>Retry-After</c> header from an HTTP response.
        /// Handles both delta-seconds and HTTP-date formats.
        /// Returns null if the header is absent or unparseable.
        /// </summary>
        public static TimeSpan? ParseRetryAfter(string? retryAfterValue)
        {
            if (string.IsNullOrWhiteSpace(retryAfterValue))
                return null;

            // Try delta-seconds first
            if (int.TryParse(retryAfterValue, out var seconds))
                return TimeSpan.FromSeconds(seconds);

            // Try HTTP-date
            if (DateTimeOffset.TryParse(retryAfterValue, out var retryAt))
            {
                var delta = retryAt - DateTimeOffset.UtcNow;
                return delta > TimeSpan.Zero ? delta : null;
            }

            return null;
        }
    }
}
