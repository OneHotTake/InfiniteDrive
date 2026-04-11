using System.ComponentModel.DataAnnotations;

namespace InfiniteDrive.Configuration
{
    /// <summary>
    /// Wizard ViewModel for initial setup.
    /// Re-entrant — users can return to update API keys and library paths.
    /// </summary>
    public class WizardViewModel : BasePluginViewModel
    {
        [Display(Name = "API Key", Description = "Your InfiniteDrive API key for accessing the catalog")]
        [Required]
        public string ApiKey { get; set; } = string.Empty;

        [Display(Name = "Movies Library Path", Description = "Path to the InfiniteDrive movies library")]
        [Required]
        public string MoviesLibraryPath { get; set; } = "/embystreams/library/movies/";

        [Display(Name = "Series Library Path", Description = "Path to the InfiniteDrive series library")]
        [Required]
        public string SeriesLibraryPath { get; set; } = "/embystreams/library/series/";

        [Display(Name = "Anime Library Path", Description = "Path to the InfiniteDrive anime library (AniList/AniDB)")]
        [Required]
        public string AnimeLibraryPath { get; set; } = "/embystreams/library/anime/";

        [Display(Name = "Enable Auto-Sync", Description = "Automatically sync catalog items every 6 hours")]
        public bool EnableAutoSync { get; set; } = true;

        [Display(Name = "Sync Interval (hours)", Description = "How often to auto-sync (minimum: 1 hour)")]
        [Range(1, 24)]
        public int SyncIntervalHours { get; set; } = 6;

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  STREAM QUALITY (WIZARD STEP 3)                                      ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        [Display(Name = "Quality Mode", Description = "\"simple\" shows basic toggles, \"advanced\" shows full slot config")]
        public string QualityMode { get; set; } = "simple"; // "simple" or "advanced"

        [Display(Name = "HD Broad", Description = "Locked, always true — the baseline quality slot")]
        public bool SlotHdBroad { get; set; } = true; // Locked, always true

        [Display(Name = "Best Available", Description = "Automatically pick the highest quality stream")]
        public bool SlotBestAvailable { get; set; } = false;

        [Display(Name = "4K Dolby Vision", Description = "Enable 4K Dolby Vision quality slot")]
        public bool Slot4kDv { get; set; } = false;

        [Display(Name = "4K HDR", Description = "Enable 4K HDR quality slot")]
        public bool Slot4kHdr { get; set; } = false;

        [Display(Name = "4K SDR", Description = "Enable 4K SDR quality slot")]
        public bool Slot4kSdr { get; set; } = false;

        [Display(Name = "HD Efficient", Description = "Smaller file sizes, lower bandwidth")]
        public bool SlotHdEfficient { get; set; } = false;

        [Display(Name = "Compact", Description = "Minimal bandwidth, smallest file sizes")]
        public bool SlotCompact { get; set; } = false;

        [Display(Name = "Default Version", Description = "Which slot is used for auto-play")]
        public string DefaultVersion { get; set; } = "hd_broad";
    }
}
