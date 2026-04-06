using System;

namespace EmbyStreams.Models
{
    /// <summary>
    /// Pipeline log entry.
    /// </summary>
    public class PipelineLogEntry
    {
        public string PrimaryId { get; set; } = string.Empty;
        public string PrimaryIdType { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public string Trigger { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? Details { get; set; }
        public DateTimeOffset Timestamp { get; set; }
    }

    /// <summary>
    /// Resolution log entry.
    /// </summary>
    public class ResolutionLogEntry
    {
        public string PrimaryId { get; set; } = string.Empty;
        public string PrimaryIdType { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;
        public string MediaId { get; set; } = string.Empty;
        public int StreamCount { get; set; }
        public string? SelectedStream { get; set; }
        public long DurationMs { get; set; }
        public DateTimeOffset Timestamp { get; set; }
    }

    /// <summary>
    /// Recent log entry (combined from pipeline and resolution logs).
    /// </summary>
    public class RecentLogEntry
    {
        public DateTimeOffset Timestamp { get; set; }
        public string LogType { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Details { get; set; }
    }
}
