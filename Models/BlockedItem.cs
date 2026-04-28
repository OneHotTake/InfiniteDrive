namespace InfiniteDrive.Models
{
    /// <summary>POCO for a blocked_items row.</summary>
    public class BlockedItem
    {
        public long Id { get; set; }
        public string? ImdbId { get; set; }
        public string? TmdbId { get; set; }
        public string? AnilistId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;
        public string BlockedAt { get; set; } = string.Empty;
        public string BlockedBy { get; set; } = string.Empty;
        public string? UnblockedAt { get; set; }
        public string? UnblockedBy { get; set; }
    }
}
