namespace InfiniteDrive.Models
{
    /// <summary>
    /// Result from item pipeline processing.
    /// </summary>
    public class ItemPipelineResult
    {
        /// <summary>Whether the pipeline completed successfully.</summary>
        public bool Success { get; set; }

        /// <summary>The resulting item status after processing.</summary>
        public ItemStatus Status { get; set; }

        /// <summary>Error message if processing failed.</summary>
        public string? Error { get; set; }

        /// <summary>The processed item.</summary>
        public MediaItem? Item { get; set; }

        /// <summary>Details about what happened.</summary>
        public string? Details { get; set; }
    }
}
