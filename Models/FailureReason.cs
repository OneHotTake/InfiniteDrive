namespace InfiniteDrive.Models
{
    /// <summary>
    /// Reason codes for why an item failed to process.
    /// Used to categorize and diagnose failures in the item pipeline.
    /// </summary>
    public enum FailureReason
    {
        /// <summary>No failure (default value).</summary>
        None,

        /// <summary>No streams found for this item from upstream providers.</summary>
        NoStreamsFound,

        /// <summary>Failed to fetch metadata from Cinemeta/AIOMetadata.</summary>
        MetadataFetchFailed,

        /// <summary>Error writing .strm or .nfo file to filesystem.</summary>
        FileWriteError,

        /// <summary>Timeout waiting for Emby to index the item.</summary>
        EmbyIndexTimeout,

        /// <summary>Item blocked by digital release gate (not yet available).</summary>
        DigitalReleaseGate,

        /// <summary>Item blocked by user (or admin).</summary>
        Blocked
    }

    /// <summary>
    /// Extension methods for FailureReason.
    /// </summary>
    public static class FailureReasonExtensions
    {
        /// <summary>
        /// Returns a user-friendly display string for this failure reason.
        /// </summary>
        public static string ToDisplayString(this FailureReason reason)
        {
            return reason switch
            {
                FailureReason.None                  => "None",
                FailureReason.NoStreamsFound         => "No streams found",
                FailureReason.MetadataFetchFailed    => "Metadata fetch failed",
                FailureReason.FileWriteError         => "File write error",
                FailureReason.EmbyIndexTimeout      => "Emby index timeout",
                FailureReason.DigitalReleaseGate     => "Digital release gate",
                FailureReason.Blocked                => "Blocked",
                _                                 => "Unknown"
            };
        }

        /// <summary>
        /// Returns a detailed description suitable for logging or UI tooltips.
        /// </summary>
        public static string ToDescription(this FailureReason reason)
        {
            return reason switch
            {
                FailureReason.None                  => "No failure occurred.",
                FailureReason.NoStreamsFound         => "No stream URLs were found from the upstream provider.",
                FailureReason.MetadataFetchFailed    => "Failed to fetch metadata from the metadata provider.",
                FailureReason.FileWriteError         => "An error occurred while writing the .strm or .nfo file.",
                FailureReason.EmbyIndexTimeout      => "Timed out waiting for Emby to index the item.",
                FailureReason.DigitalReleaseGate     => "The item is not yet available (digital release gate).",
                FailureReason.Blocked                => "The item has been blocked by a user or admin.",
                _                                 => "Unknown failure reason."
            };
        }

        /// <summary>
        /// Checks if this failure is recoverable (can be retried).
        /// </summary>
        public static bool IsRecoverable(this FailureReason reason)
        {
            return reason switch
            {
                FailureReason.NoStreamsFound         => true,
                FailureReason.MetadataFetchFailed    => true,
                FailureReason.FileWriteError         => true,
                FailureReason.EmbyIndexTimeout      => true,
                FailureReason.DigitalReleaseGate     => true,
                FailureReason.None                  => false,
                FailureReason.Blocked                => false,
                _                                 => false
            };
        }
    }
}
