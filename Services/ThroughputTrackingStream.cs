using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// A read-through <see cref="Stream"/> wrapper that measures sustained
    /// download throughput and updates the <c>client_compat</c> table when
    /// a client consistently cannot keep up with the stream's expected bitrate.
    ///
    /// Algorithm (mirrors the Night 4 sprint spec):
    /// <list type="number">
    ///   <item>Accumulate bytes read over a 5-second window.</item>
    ///   <item>At the end of each window, compute kbps = bytes × 8 / 1024 / 5.</item>
    ///   <item>If measured kbps &lt; 70% of expected kbps, increment a consecutive-low counter.</item>
    ///   <item>After 3 consecutive low windows (≥15 s), write to <c>client_compat</c> once.</item>
    /// </list>
    ///
    /// Once the DB update fires, no further writes happen for this stream session.
    /// </summary>
    public class ThroughputTrackingStream : Stream
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const double WindowSeconds     = 5.0;
        private const double ThresholdFraction = 0.70;
        private const int    LowWindowsNeeded  = 3; // 15+ consecutive seconds

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly Stream  _inner;
        private readonly string  _clientType;
        private readonly int     _expectedKbps;
        private readonly ILogger _logger;

        private long     _windowBytes;
        private DateTime _windowStart        = DateTime.UtcNow;
        private int      _lowWindowCount;
        private bool     _compatUpdated;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Wraps <paramref name="inner"/> with throughput tracking.
        /// </summary>
        /// <param name="inner">The upstream HTTP response stream.</param>
        /// <param name="clientType">Normalised Emby client type string.</param>
        /// <param name="expectedKbps">
        /// Expected sustained bitrate in kbps.  Pass 0 to disable tracking.
        /// </param>
        /// <param name="logger">Logger for debug messages.</param>
        public ThroughputTrackingStream(
            Stream  inner,
            string  clientType,
            int     expectedKbps,
            ILogger logger)
        {
            _inner        = inner;
            _clientType   = clientType;
            _expectedKbps = expectedKbps;
            _logger       = logger;
        }

        // ── Stream overrides ─────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override bool CanRead  => _inner.CanRead;
        /// <inheritdoc/>
        public override bool CanSeek  => false;
        /// <inheritdoc/>
        public override bool CanWrite => false;
        /// <inheritdoc/>
        public override long Length   => _inner.Length;
        /// <inheritdoc/>
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            if (n > 0) AccountForBytes(n);
            return n;
        }

        /// <inheritdoc/>
        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var n = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
            if (n > 0) AccountForBytes(n);
            return n;
        }

        /// <inheritdoc/>
        public override void Flush()  => _inner.Flush();
        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        // ── Private: measurement ─────────────────────────────────────────────────

        private void AccountForBytes(int n)
        {
            if (_compatUpdated || _expectedKbps <= 0) return;

            _windowBytes += n;

            var elapsed = (DateTime.UtcNow - _windowStart).TotalSeconds;
            if (elapsed < WindowSeconds) return;

            // End of window — compute kbps
            var measuredKbps = (int)(_windowBytes * 8L / 1024 / elapsed);
            var threshold    = (int)(_expectedKbps * ThresholdFraction);

            _logger.LogDebug(
                "[EmbyStreams] Throughput window: {Client} measured={Measured} kbps expected={Expected} kbps",
                _clientType, measuredKbps, _expectedKbps);

            if (measuredKbps < threshold)
            {
                _lowWindowCount++;
                if (_lowWindowCount >= LowWindowsNeeded)
                {
                    _compatUpdated = true;
                    _ = RecordLowThroughputAsync(measuredKbps);
                }
            }
            else
            {
                _lowWindowCount = 0; // reset on a good window
            }

            _windowBytes  = 0;
            _windowStart  = DateTime.UtcNow;
        }

        private async Task RecordLowThroughputAsync(int measuredKbps)
        {
            try
            {
                var db = Plugin.Instance?.DatabaseManager;
                if (db == null) return;

                _logger.LogInformation(
                    "[EmbyStreams] Client {Client} sustained {Kbps} kbps (< 70% of {Expected} kbps) — " +
                    "updating client_compat: max_safe_bitrate={Kbps}",
                    _clientType, measuredKbps, _expectedKbps, measuredKbps);

                // Mark as not reliably able to handle redirects at this bitrate.
                // supports_redirect=0 causes PlaybackService to route to proxy next time,
                // where the quality-gate in place can pick a lower-bitrate fallback.
                await db.UpdateClientCompatAsync(_clientType, supportsRedirect: false, maxBitrate: measuredKbps);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[EmbyStreams] Failed to update client compat after low throughput");
            }
        }
    }
}
