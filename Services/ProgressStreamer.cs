using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Manages server-sent event (SSE) streams for real-time progress updates.
    /// </summary>
    public class ProgressStreamer
    {
        private readonly ConcurrentDictionary<string, Queue<ProgressEvent>> _streams =
            new();

        /// <summary>
        /// Subscribes a new client session to progress events.
        /// </summary>
        /// <param name="sessionId">Unique session identifier.</param>
        public void Subscribe(string sessionId)
        {
            _streams.TryAdd(sessionId, new Queue<ProgressEvent>());
        }

        /// <summary>
        /// Unsubscribes a client session from progress events.
        /// </summary>
        /// <param name="sessionId">Unique session identifier.</param>
        public void Unsubscribe(string sessionId)
        {
            _streams.TryRemove(sessionId, out _);
        }

        /// <summary>
        /// Publishes a progress event to all subscribed clients.
        /// </summary>
        /// <param name="evt">The progress event to publish.</param>
        public void Publish(ProgressEvent evt)
        {
            foreach (var stream in _streams.Values)
            {
                stream.Enqueue(evt);
            }
        }

        /// <summary>
        /// Reads progress events for a specific session as an async enumerable.
        /// </summary>
        /// <param name="sessionId">Unique session identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Async enumerable of progress events.</returns>
        public async IAsyncEnumerable<ProgressEvent> ReadEventsAsync(
            string sessionId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            // Wait for subscription to be registered
            Queue<ProgressEvent>? queue = null;
            while (!_streams.TryGetValue(sessionId, out queue))
            {
                await Task.Delay(100, ct);
            }

            // Yield events as they arrive
            while (!ct.IsCancellationRequested && queue != null)
            {
                while (queue.TryDequeue(out var evt))
                {
                    yield return evt!;
                }
                await Task.Delay(100, ct);
            }
        }
    }

    /// <summary>
    /// Represents a progress event for SSE streaming.
    /// </summary>
    /// <param name="Type">Event type: "progress", "complete", "error".</param>
    /// <param name="Message">Human-readable message.</param>
    /// <param name="Progress">Progress value from 0.0 to 1.0.</param>
    /// <param name="Details">Optional JSON payload with additional data.</param>
    public record ProgressEvent(
        string Type,
        string Message,
        double Progress,
        string? Details = null
    );
}
