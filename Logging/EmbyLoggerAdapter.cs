using System;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;
using EmbyILogger = MediaBrowser.Model.Logging.ILogger;

namespace InfiniteDrive.Logging
{
    /// <summary>
    /// Adapts Emby's <see cref="EmbyILogger"/> to MEL's <see cref="ILogger{T}"/>
    /// so all existing <c>_logger.LogInformation / LogWarning / LogError</c> call
    /// sites work without modification, while the output goes through Emby's own
    /// log manager (and therefore appears in the Emby server log file).
    /// </summary>
    internal sealed class EmbyLoggerAdapter<T> : ILogger<T>
    {
        private readonly EmbyILogger _inner;

        internal EmbyLoggerAdapter(EmbyILogger inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        // ── ILogger<T> ──────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public bool IsEnabled(MelLogLevel logLevel) => logLevel != MelLogLevel.None;

        /// <inheritdoc/>
        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

        /// <inheritdoc/>
        public void Log<TState>(
            MelLogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);

            switch (logLevel)
            {
                case MelLogLevel.Trace:
                case MelLogLevel.Debug:
                    if (exception != null)
                        _inner.Debug("{0} | {1}: {2}", message, exception.GetType().Name, exception.Message);
                    else
                        _inner.Debug(message);
                    break;

                case MelLogLevel.Information:
                    _inner.Info(message);
                    break;

                case MelLogLevel.Warning:
                    // Emby's ILogger has no WarnException; include exception summary in message.
                    if (exception != null)
                        _inner.Warn("{0} | {1}: {2}", message, exception.GetType().Name, exception.Message);
                    else
                        _inner.Warn(message);
                    break;

                case MelLogLevel.Error:
                    if (exception != null)
                        _inner.ErrorException(message, exception);
                    else
                        _inner.Error(message);
                    break;

                case MelLogLevel.Critical:
                    if (exception != null)
                        _inner.FatalException(message, exception);
                    else
                        _inner.Fatal(message);
                    break;
            }
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            private NullScope() { }
            public void Dispose() { }
        }
    }
}
