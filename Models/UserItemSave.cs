using System;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Per-user save record linking a user to a media item.
    /// Replaces the global saved_by/save_reason/saved_season columns on media_items.
    /// </summary>
    public class UserItemSave
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string UserId { get; set; } = "";
        public string MediaItemId { get; set; } = "";
        public string? SaveReason { get; set; }
        public int? SavedSeason { get; set; }
        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
