using System;
using EmbyStreams.Models;

namespace EmbyStreams.Configuration
{
    /// <summary>
    /// Data row for Sources grid.
    /// </summary>
    public class SourceRow
    {
        public string Name { get; set; } = string.Empty;
        public int ItemCount { get; set; }
        public DateTimeOffset? LastSyncedAt { get; set; }
        public bool Enabled { get; set; }
        public bool ShowAsCollection { get; set; }
    }

    /// <summary>
    /// Data row for Collections grid.
    /// </summary>
    public class CollectionRow
    {
        public string CollectionName { get; set; } = string.Empty;
        public string SourceName { get; set; } = string.Empty;
        public DateTimeOffset? LastSyncedAt { get; set; }
    }

    /// <summary>
    /// Data row for Items grid.
    /// </summary>
    public class ItemRow
    {
        public string Title { get; set; } = string.Empty;
        public int Year { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public ItemStatus Status { get; set; }
        public string? SaveReason { get; set; }
        public bool Superseded { get; set; }
        public bool SupersededConflict { get; set; }
    }

    /// <summary>
    /// Watch status enum for Watch History.
    /// </summary>
    public enum WatchStatus
    {
        Watched,
        PartiallyWatched,
        NotStarted
    }

    /// <summary>
    /// Data row for Watch History grid.
    /// </summary>
    public class WatchHistoryRow
    {
        public string Title { get; set; } = string.Empty;
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public WatchStatus Status { get; set; }
        public DateTimeOffset? LastWatchedAt { get; set; }
    }
}
