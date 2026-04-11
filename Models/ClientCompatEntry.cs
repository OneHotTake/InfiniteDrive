namespace InfiniteDrive.Models
{
    /// <summary>
    /// Learned per-client streaming capabilities stored in the
    /// <c>client_compat</c> table.  Updated automatically during proxy sessions.
    /// </summary>
    public class ClientCompatEntry
    {
        /// <summary>
        /// Normalised client identifier (primary key).
        /// Values: <c>emby_atv</c>, <c>emby_web</c>, <c>emby_android</c>,
        /// <c>emby_ios</c>, <c>infuse</c>, <c>other</c>.
        /// </summary>
        public string ClientType { get; set; } = string.Empty;

        /// <summary>
        /// 1 = redirect mode works for this client;
        /// 0 = must use proxy (e.g. some Samsung/LG TV apps).
        /// </summary>
        public int SupportsRedirect { get; set; } = 1;

        /// <summary>Learned maximum safe bitrate in kbps.  Null = no limit known yet.</summary>
        public int? MaxSafeBitrate { get; set; }

        /// <summary>Best quality tier confirmed playable on this client.</summary>
        public string? PreferredQuality { get; set; }

        /// <summary>Total number of observations used to build this profile.</summary>
        public int TestCount { get; set; } = 0;

        /// <summary>UTC timestamp of the most recent compatibility update.</summary>
        public string? LastTestedAt { get; set; }
    }
}
