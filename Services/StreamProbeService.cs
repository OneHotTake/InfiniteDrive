using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Result of a stream URL availability probe.
    /// </summary>
    public sealed record ProbeResult(
        bool Ok,
        int? StatusCode,
        string Reason); // "ok" | "timeout" | "http_{code}" | "error"

    /// <summary>
    /// Lightweight HTTP probe service for stream availability checking.
    /// Used by StreamResolutionService to verify that a candidate stream URL
    /// actually responds before serving it to the user.
    ///
    /// <para>Probes use HEAD requests with a 500ms timeout, falling back to
    /// GET with Range: bytes=0-1023 if the server returns 405 Method Not Allowed.</para>
    /// </summary>
    public sealed class StreamProbeService
    {
        private readonly ILogger<StreamProbeService> _logger;

        // Shared HttpClient instance — thread-safe and designed for reuse
        private static readonly HttpClient _sharedHttp = new HttpClient();

        /// <summary>
        /// Production constructor.
        /// </summary>
        public StreamProbeService(ILogger<StreamProbeService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Probes a stream URL to check if it responds.
        /// Uses HEAD with 500ms timeout, falls back to GET with Range if HEAD returns 405.
        /// </summary>
        /// <param name="url">The stream URL to probe.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>ProbeResult indicating success or failure reason.</returns>
        public async Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return new ProbeResult(Ok: false, StatusCode: null, Reason: "error");
            }

            try
            {
                _logger.LogDebug("[StreamProbe] Probing {Url}", url);

                // Try HEAD first with 500ms timeout
                using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headCts.CancelAfter(500);

                using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
                using var headResponse = await _sharedHttp.SendAsync(headRequest, headCts.Token);

                // 2xx or 206 is acceptable
                if (IsSuccess(headResponse.StatusCode))
                {
                    _logger.LogDebug("[StreamProbe] HEAD OK for {Url} — {StatusCode}",
                        url, headResponse.StatusCode);
                    return new ProbeResult(Ok: true, (int)headResponse.StatusCode, "ok");
                }

                // 405 Method Not Allowed — fall back to GET with Range
                if (headResponse.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                {
                    _logger.LogDebug("[StreamProbe] HEAD returned 405, trying GET with Range for {Url}", url);
                    return await GetWithRangeAsync(url, ct);
                }

                // Any other non-success status
                _logger.LogDebug("[StreamProbe] HEAD failed for {Url} — {StatusCode}",
                    url, headResponse.StatusCode);
                return new ProbeResult(Ok: false, (int)headResponse.StatusCode,
                    $"http_{(int)headResponse.StatusCode}");
            }
            catch (TaskCanceledException)
            {
                // External cancellation or our timeout expired
                _logger.LogDebug("[StreamProbe] Probe canceled/timeout for {Url}", url);
                return new ProbeResult(Ok: false, null, "timeout");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "[StreamProbe] HTTP error probing {Url}", url);
                return new ProbeResult(Ok: false, null, "error");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StreamProbe] Unexpected error probing {Url}", url);
                return new ProbeResult(Ok: false, null, "error");
            }
        }

        /// <summary>
        /// GET with Range: bytes=0-1023 fallback for servers that reject HEAD.
        /// </summary>
        private async Task<ProbeResult> GetWithRangeAsync(string url, CancellationToken ct)
        {
            try
            {
                using var rangeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                rangeCts.CancelAfter(500);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);

                using var response = await _sharedHttp.SendAsync(request, rangeCts.Token);

                if (IsSuccess(response.StatusCode) || response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                {
                    _logger.LogDebug("[StreamProbe] GET Range OK for {Url} — {StatusCode}",
                        url, response.StatusCode);
                    return new ProbeResult(Ok: true, (int)response.StatusCode, "ok");
                }

                _logger.LogDebug("[StreamProbe] GET Range failed for {Url} — {StatusCode}",
                    url, response.StatusCode);
                return new ProbeResult(Ok: false, (int)response.StatusCode,
                    $"http_{(int)response.StatusCode}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("[StreamProbe] GET Range timeout for {Url}", url);
                return new ProbeResult(Ok: false, null, "timeout");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StreamProbe] GET Range error for {Url}", url);
                return new ProbeResult(Ok: false, null, "error");
            }
        }

        /// <summary>
        /// Checks if HTTP status code indicates success (2xx or 206).
        /// </summary>
        private static bool IsSuccess(System.Net.HttpStatusCode statusCode) =>
            (int)statusCode >= 200 && (int)statusCode < 300 ||
            statusCode == System.Net.HttpStatusCode.PartialContent;
    }
}
