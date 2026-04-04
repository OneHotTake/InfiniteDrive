using System;
using System.Net.Http;
using Polly;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Resilience
{
    /// <summary>
    /// Resilience policy for AIOStreams HTTP calls.
    /// Combines retry, circuit breaker, and timeout to handle transient failures.
    /// (Sprint 104C-02)
    /// </summary>
    public static class AIOStreamsResiliencePolicy
    {
        // Retry settings
        private const int MaxRetryAttempts = 3;
        private static readonly TimeSpan[] RetryDelays = new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4)
        };

        // Circuit breaker settings
        private const int CircuitBreakerExceptionThreshold = 5;
        private static readonly TimeSpan CircuitBreakerDurationOfBreak = TimeSpan.FromSeconds(30);

        // Timeout settings
        private static readonly TimeSpan TimeoutDuration = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Gets the combined resilience pipeline for AIOStreams calls.
        /// Order: Circuit Breaker → Retry → Timeout
        /// </summary>
        public static AsyncPolicy<HttpResponseMessage> CreatePolicy(ILogger logger)
        {
            // Circuit breaker: open circuit after 5 failures, close after 30 seconds
            var circuitBreakerPolicy = Policy<HttpResponseMessage>
                .Handle<HttpRequestException>()
                .Or<TimeoutException>()
                .OrResult(r => !r.IsSuccessStatusCode && (int)r.StatusCode >= 500)
                .CircuitBreakerAsync(
                    CircuitBreakerExceptionThreshold,
                    CircuitBreakerDurationOfBreak,
                    onBreak: (exception, duration, context) =>
                    {
                        logger?.LogError(
                            exception.Exception,
                            "[AIOStreamsResilience] Circuit breaker opened for {Duration}s due to {ExceptionType}",
                            duration.TotalSeconds,
                            exception.Exception?.GetType().Name ?? $"HTTP {exception.Result?.StatusCode}");
                    },
                    onReset: context =>
                    {
                        logger?.LogInformation("[AIOStreamsResilience] Circuit breaker reset");
                    },
                    onHalfOpen: () =>
                    {
                        logger?.LogInformation("[AIOStreamsResilience] Circuit breaker half-open - testing connection");
                    });

            // Retry policy: retry on transient failures with exponential backoff
            var retryPolicy = Policy<HttpResponseMessage>
                .Handle<HttpRequestException>()
                .Or<TimeoutException>()
                .OrResult(r => !r.IsSuccessStatusCode && (int)r.StatusCode >= 500)
                .WaitAndRetryAsync(
                    MaxRetryAttempts,
                    retryAttempt => RetryDelays[retryAttempt - 1],
                    onRetry: (outcome, delay, retryCount, context) =>
                    {
                        logger?.LogWarning(
                            "[AIOStreamsResilience] Retry {RetryCount}/{MaxRetries} after {Delay}s due to {ExceptionType}",
                            retryCount,
                            MaxRetryAttempts,
                            delay.TotalSeconds,
                            outcome.Exception?.GetType().Name ?? $"HTTP {outcome.Result?.StatusCode}");
                    });

            // Timeout policy: fail fast if AIOStreams doesn't respond within 15 seconds
            var timeoutPolicy = Policy<HttpResponseMessage>
                .Handle<TimeoutException>()
                .FallbackAsync(FallbackMessage(logger));

            var timeoutWithDelegate = Policy
                .TimeoutAsync(TimeoutDuration)
                .WrapAsync(timeoutPolicy);

            // Combine policies: outermost is timeout, then retry, then circuit breaker
            return Policy.WrapAsync(timeoutWithDelegate, retryPolicy, circuitBreakerPolicy);
        }

        /// <summary>
        /// Fallback message when timeout policy fails.
        /// Returns a 504 Gateway Timeout response.
        /// </summary>
        private static HttpResponseMessage FallbackMessage(ILogger logger)
        {
            logger?.LogError(
                "[AIOStreamsResilience] Timeout after {Timeout}s - returning fallback",
                TimeoutDuration.TotalSeconds);

            return new HttpResponseMessage(System.Net.HttpStatusCode.GatewayTimeout)
            {
                ReasonPhrase = "AIOStreams timeout"
            };
        }
    }
}
