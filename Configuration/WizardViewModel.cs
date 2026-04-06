using System.ComponentModel.DataAnnotations;

namespace EmbyStreams.Configuration
{
    /// <summary>
    /// Wizard ViewModel for initial setup.
    /// Re-entrant — users can return to update API keys and library paths.
    /// </summary>
    public class WizardViewModel : BasePluginViewModel
    {
        [Display(Name = "API Key", Description = "Your EmbyStreams API key for accessing the catalog")]
        [Required]
        public string ApiKey { get; set; } = string.Empty;

        [Display(Name = "Movies Library Path", Description = "Path to the EmbyStreams movies library")]
        [Required]
        public string MoviesLibraryPath { get; set; } = "/embystreams/library/movies/";

        [Display(Name = "Series Library Path", Description = "Path to the EmbyStreams series library")]
        [Required]
        public string SeriesLibraryPath { get; set; } = "/embystreams/library/series/";

        [Display(Name = "Anime Library Path", Description = "Path to the EmbyStreams anime library (AniList/AniDB)")]
        [Required]
        public string AnimeLibraryPath { get; set; } = "/embystreams/library/anime/";

        [Display(Name = "Enable Auto-Sync", Description = "Automatically sync catalog items every 6 hours")]
        public bool EnableAutoSync { get; set; } = true;

        [Display(Name = "Sync Interval (hours)", Description = "How often to auto-sync (minimum: 1 hour)")]
        [Range(1, 24)]
        public int SyncIntervalHours { get; set; } = 6;
    }
}
