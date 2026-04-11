namespace InfiniteDrive.Models
{
    /// <summary>
    /// Events or actions that trigger the item pipeline.
    /// Used for logging and determining which operations should run.
    /// </summary>
    public enum PipelineTrigger
    {
        /// <summary>Periodic catalog sync from source manifest.</summary>
        Sync,

        /// <summary>User initiated playback.</summary>
        Play,

        /// <summary>User watched an episode (triggers series save).</summary>
        WatchEpisode,

        /// <summary>User explicitly saved an item.</summary>
        UserSave,

        /// <summary>User explicitly blocked an item.</summary>
        UserBlock,

        /// <summary>User removed an item from saved list.</summary>
        UserRemove,

        /// <summary>Grace period for removed items expired.</summary>
        GraceExpiry,

        /// <summary>Detected user owns the file (Your Files).</summary>
        YourFiles,

        /// <summary>Admin-triggered manual operation.</summary>
        Admin,

        /// <summary>Automatic retry after failure.</summary>
        Retry
    }

    /// <summary>
    /// Extension methods for PipelineTrigger.
    /// </summary>
    public static class PipelineTriggerExtensions
    {
        /// <summary>
        /// Returns a user-friendly display string for this trigger.
        /// </summary>
        public static string ToDisplayString(this PipelineTrigger trigger)
        {
            return trigger switch
            {
                PipelineTrigger.Sync           => "Sync",
                PipelineTrigger.Play           => "Play",
                PipelineTrigger.WatchEpisode   => "Watch Episode",
                PipelineTrigger.UserSave       => "User Save",
                PipelineTrigger.UserBlock      => "User Block",
                PipelineTrigger.UserRemove     => "User Remove",
                PipelineTrigger.GraceExpiry    => "Grace Expiry",
                PipelineTrigger.YourFiles      => "Your Files",
                PipelineTrigger.Admin          => "Admin",
                PipelineTrigger.Retry          => "Retry",
                _                             => "Unknown"
            };
        }

        /// <summary>
        /// Returns a detailed description suitable for logging.
        /// </summary>
        public static string ToDescription(this PipelineTrigger trigger)
        {
            return trigger switch
            {
                PipelineTrigger.Sync           => "Periodic catalog sync from source manifest",
                PipelineTrigger.Play           => "User initiated playback",
                PipelineTrigger.WatchEpisode   => "User watched an episode (series save trigger)",
                PipelineTrigger.UserSave       => "User explicitly saved the item",
                PipelineTrigger.UserBlock      => "User explicitly blocked the item",
                PipelineTrigger.UserRemove     => "User removed the item from saved list",
                PipelineTrigger.GraceExpiry    => "Grace period for removed items expired",
                PipelineTrigger.YourFiles      => "Detected user owns the file (Your Files detection)",
                PipelineTrigger.Admin          => "Admin-triggered manual operation",
                PipelineTrigger.Retry          => "Automatic retry after failure",
                _                             => "Unknown trigger"
            };
        }

        /// <summary>
        /// Checks if this trigger was initiated by a user action.
        /// </summary>
        public static bool IsUserTriggered(this PipelineTrigger trigger)
        {
            return trigger switch
            {
                PipelineTrigger.Play          => true,
                PipelineTrigger.WatchEpisode  => true,
                PipelineTrigger.UserSave      => true,
                PipelineTrigger.UserBlock     => true,
                PipelineTrigger.UserRemove    => true,
                _                            => false
            };
        }

        /// <summary>
        /// Checks if this trigger should be logged as high-priority.
        /// </summary>
        public static bool IsHighPriority(this PipelineTrigger trigger)
        {
            return trigger switch
            {
                PipelineTrigger.Play          => true,
                PipelineTrigger.UserSave      => true,
                PipelineTrigger.UserBlock     => true,
                PipelineTrigger.Admin         => true,
                _                            => false
            };
        }
    }
}
