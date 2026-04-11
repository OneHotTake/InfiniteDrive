namespace InfiniteDrive.Models
{
    /// <summary>
    /// Logs structured run information for Health Panel visibility.
    /// </summary>
    public class RefreshRunLog
    {
        /// <summary>Auto-incremented primary key.</summary>
        public long Id { get; set; }

        /// <summary>UTC timestamp of run start (ISO8601).</summary>
        public string RunAt { get; set; } = string.Empty;

        /// <summary>Worker name ("Refresh" or "Deep Clean").</summary>
        public string Worker { get; set; } = string.Empty;

        /// <summary>Step name ("Collect", "Write", "Hint", "Notify", "Verify").</summary>
        public string Step { get; set; } = string.Empty;

        /// <summary>Run status ("started", "completed", "faulted", "skipped").</summary>
        public string Status { get; set; } = "started";

        /// <summary>Count of items affected by this step.</summary>
        public int ItemsAffected { get; set; }

        /// <summary>Optional notes or error details.</summary>
        public string? Notes { get; set; }
    }
}
