using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// HTTP proxy live stream that fetches from CDN URL with proper headers.
    /// Supports range requests and custom headers (e.g. StremThru auth).
    /// </summary>
    public sealed class InfiniteDriveLiveStream : ILiveStream
    {
        private readonly ILogger _logger;
        private readonly string _cdnUrl;
        private readonly Dictionary<string, string>? _headers;
        private readonly long? _fileSize;
        private HttpResponseMessage? _response;
        private Stream? _responseStream;
        private bool _disposed;

        private static readonly HttpClientHandler _handler = new()
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        private static readonly HttpClient _http = new(_handler)
        {
            Timeout = TimeSpan.FromMinutes(30),
        };

        public MediaSourceInfo MediaSource { get; set; }
        public string UniqueId { get; } = Guid.NewGuid().ToString("N");
        public string TunerHostId => string.Empty;
        public bool EnableStreamSharing => false;
        public int ConsumerCount { get; set; }
        public string OriginalStreamId { get; set; } = string.Empty;
        public DateTimeOffset DateOpened { get; set; }
        public bool SupportsCopyTo => true;

        public InfiniteDriveLiveStream(MediaSourceInfo source, ILogger logger)
        {
            MediaSource = source;
            _logger = logger;
            _cdnUrl = source.Path;
            _fileSize = source.Size > 0 ? source.Size : null;

            if (source.RequiredHttpHeaders?.Count > 0)
                _headers = new Dictionary<string, string>(source.RequiredHttpHeaders);
        }

        public async Task Open(CancellationToken cancellationToken)
        {
            if (_response != null) return;

            using var req = new HttpRequestMessage(HttpMethod.Get, _cdnUrl);

            if (_headers != null)
            {
                foreach (var h in _headers)
                    req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            _response = await _http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            _response.EnsureSuccessStatusCode();
            _responseStream = await _response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            DateOpened = DateTimeOffset.UtcNow;

            _logger.LogDebug("[InfiniteDriveLiveStream] Opened {Url} (HTTP {Status})",
                TruncateUrl(_cdnUrl), (int)_response.StatusCode);
        }

        public async Task CopyToAsync(PipeWriter destination, CancellationToken cancellationToken)
        {
            if (_responseStream == null) throw new InvalidOperationException("Stream not opened");
            await _responseStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        public async Task CopyToAsync(Stream destination, DateTimeOffset? startTime,
            Action<SegmentedStreamSegmentInfo> segmentCallback, CancellationToken cancellationToken)
        {
            if (_responseStream == null) throw new InvalidOperationException("Stream not opened");
            await _responseStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        public Task Close()
        {
            _responseStream?.Dispose();
            _responseStream = null;
            _response?.Dispose();
            _response = null;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _responseStream?.Dispose();
            _response?.Dispose();
        }

        private static string TruncateUrl(string url) =>
            url.Length > 80 ? url[..80] + "..." : url;
    }
}
