namespace InfiniteDrive.Models
{
    /// <summary>
    /// Reason why an item was saved by the user.
    /// Used to distinguish between different save scenarios.
    /// </summary>
    public enum SaveReason
    {
        /// <summary>User explicitly saved the item via UI.</summary>
        Explicit,

        /// <summary>Item automatically saved after watching an episode.</summary>
        WatchedEpisode,

        /// <summary>Admin manually saved the item (override).</summary>
        AdminOverride
    }

    /// <summary>
    /// Extension methods for SaveReason.
    /// </summary>
    public static class SaveReasonExtensions
    {
        /// <summary>
        /// Returns a user-friendly display string for this save reason.
        /// </summary>
        public static string ToDisplayString(this SaveReason reason)
        {
            return reason switch
            {
                SaveReason.Explicit        => "Explicit",
                SaveReason.WatchedEpisode  => "Watched Episode",
                SaveReason.AdminOverride   => "Admin Override",
                _                        => "Unknown"
            };
        }

        /// <summary>
        /// Returns a detailed description suitable for logging.
        /// </summary>
        public static string ToDescription(this SaveReason reason)
        {
            return reason switch
            {
                SaveReason.Explicit        => "User explicitly saved the item",
                SaveReason.WatchedEpisode  => "Item automatically saved after user watched an episode",
                SaveReason.AdminOverride   => "Admin manually saved the item",
                _                        => "Unknown save reason"
            };
        }

        /// <summary>
        /// Checks if this save was automatic (not user-initiated).
        /// </summary>
        public static bool IsAutomatic(this SaveReason reason)
        {
            return reason == SaveReason.WatchedEpisode;
        }
    }
}
