using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Generic keyed concurrency guard.  When multiple callers request the same
    /// key simultaneously, only the first executes the factory; all others await
    /// the same result.  Prevents duplicate AIOStreams API calls when two Emby
    /// clients play the same episode at the same time.
    /// </summary>
    public static class SingleFlight<T> where T : class?
    {
        private static readonly ConcurrentDictionary<string, Lazy<Task<T>>> Flights
            = new ConcurrentDictionary<string, Lazy<Task<T>>>(StringComparer.Ordinal);

        public static async Task<T> RunAsync(string key, Func<Task<T>> factory)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lazy = new Lazy<Task<T>>(() => tcs.Task);

            if (Flights.TryAdd(key, lazy))
            {
                // We are the first caller — run the factory.
                try
                {
                    var result = await factory().ConfigureAwait(false);
                    tcs.SetResult(result);
                    return result;
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                    throw;
                }
                finally
                {
                    Flights.TryRemove(key, out _);
                }
            }

            // A flight is already in-progress — await it.
            var existingLazy = Flights[key];
            return await existingLazy.Value.ConfigureAwait(false);
        }
    }
}
