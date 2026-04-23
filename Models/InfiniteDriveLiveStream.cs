using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Controller.Library;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// ILiveStream wrapper returned from AioMediaSourceProvider.OpenMediaSource().
    /// Carries the resolved CDN MediaSourceInfo for Emby to play directly.
    /// </summary>
    public sealed class InfiniteDriveLiveStream : ILiveStream
    {
        public MediaSourceInfo MediaSource { get; set; }
        public string UniqueId { get; } = Guid.NewGuid().ToString("N");
        public string TunerHostId => string.Empty;
        public bool EnableStreamSharing => false;
        public int ConsumerCount { get; set; }
        public string OriginalStreamId { get; set; } = string.Empty;
        public DateTimeOffset DateOpened { get; set; }
        public bool SupportsCopyTo => false;

        public InfiniteDriveLiveStream(MediaSourceInfo source)
        {
            MediaSource = source;
        }

        public Task Open(CancellationToken cancellationToken)
        {
            DateOpened = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }

        public Task Close()
        {
            return Task.CompletedTask;
        }

        public Task CopyToAsync(PipeWriter destination, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("HTTP streaming handled by Emby via MediaSource.Path");
        }

        public Task CopyToAsync(Stream destination, DateTimeOffset? startTime, Action<SegmentedStreamSegmentInfo> segmentCallback, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("HTTP streaming handled by Emby via MediaSource.Path");
        }

        public void Dispose()
        {
            Close().GetAwaiter().GetResult();
        }
    }
}
