using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;

namespace InfiniteDrive.UI
{
    public class LibrariesUI : EditableOptionsBase
    {
        public override string EditorTitle => "Libraries";

        // ── Paths ──

        [DisplayName("Movies Path")]
        [Description("Absolute path where movie .strm files are written. Emby should have a Movies library pointed here.")]
        public string SyncPathMovies { get; set; } = "/media/infinitedrive/movies";

        [DisplayName("Series Path")]
        [Description("Absolute path where TV show .strm files are written. Emby should have a TV Shows library pointed here.")]
        public string SyncPathShows { get; set; } = "/media/infinitedrive/shows";

        [DisplayName("Anime Path")]
        [Description("Absolute path for anime .strm files. Only used when Enable Anime Library is on.")]
        public string SyncPathAnime { get; set; } = "/media/infinitedrive/anime";

        // ── Library Names ──

        [DisplayName("Movies Library Name")]
        public string LibraryNameMovies { get; set; } = "Streamed Movies";

        [DisplayName("Series Library Name")]
        public string LibraryNameSeries { get; set; } = "Streamed Series";

        [DisplayName("Anime Library Name")]
        public string LibraryNameAnime { get; set; } = "Streamed Anime";

        // ── Toggles ──

        [DisplayName("Enable Anime Library")]
        [Description("Route anime items to a dedicated library. Requires Emby Anime Plugin.")]
        public bool EnableAnimeLibrary { get; set; }

        [DisplayName("Write .nfo Hints")]
        [Description("Write minimal .nfo files with IMDB/TMDB IDs alongside .strm files for better metadata matching.")]
        public bool EnableNfoHints { get; set; } = true;

        [DisplayName("Delete .strm on Re-adoption")]
        [Description("Remove .strm files automatically when real media files are detected for the same item.")]
        public bool DeleteStrmOnReadoption { get; set; } = true;

        // ── Metadata ──

        [DisplayName("Metadata Language")]
        [Description("Preferred metadata language. Default: en.")]
        public string MetadataLanguage { get; set; } = "en";

        [DisplayName("Metadata Country")]
        [Description("Country code for certifications. Default: US.")]
        public string MetadataCountryCode { get; set; } = "US";

        [DisplayName("Image Language")]
        [Description("Preferred artwork language. Default: en.")]
        public string ImageLanguage { get; set; } = "en";

        [DisplayName("Subtitle Languages")]
        [Description("Comma-separated subtitle language codes. Default: en.")]
        public string SubtitleDownloadLanguages { get; set; } = "en";

        // ── Emby Connection ──

        [DisplayName("Emby Server URL")]
        [Description("The URL Emby listens on. Written into .strm files. Default: http://127.0.0.1:8096.")]
        public string EmbyBaseUrl { get; set; } = "http://127.0.0.1:8096";

        [DisplayName("Emby API Key")]
        [Description("API key for .strm authentication. Get from Emby Dashboard → API Keys.")]
        public string EmbyApiKey { get; set; } = string.Empty;

        [DisplayName("Provision Libraries")]
        public ButtonItem ProvisionButton => new ButtonItem
        {
            Caption = "Create Libraries & Directories",
            CommandId = "provision-libraries"
        };

        [DisplayName("Sync Schedule")]
        [Description("Hour of day (UTC) for daily catalog sync. Set -1 to disable auto-sync.")]
        public int SyncScheduleHour { get; set; } = 3;

        public LibrariesUI() { }

        public LibrariesUI(PluginConfiguration cfg)
        {
            SyncPathMovies = cfg.SyncPathMovies;
            SyncPathShows = cfg.SyncPathShows;
            SyncPathAnime = cfg.SyncPathAnime;
            LibraryNameMovies = cfg.LibraryNameMovies;
            LibraryNameSeries = cfg.LibraryNameSeries;
            LibraryNameAnime = cfg.LibraryNameAnime;
            EnableAnimeLibrary = cfg.EnableAnimeLibrary;
            EnableNfoHints = cfg.EnableNfoHints;
            DeleteStrmOnReadoption = cfg.DeleteStrmOnReadoption;
            MetadataLanguage = cfg.MetadataLanguage;
            MetadataCountryCode = cfg.MetadataCountryCode;
            ImageLanguage = cfg.ImageLanguage;
            SubtitleDownloadLanguages = cfg.SubtitleDownloadLanguages;
            EmbyBaseUrl = cfg.EmbyBaseUrl;
            EmbyApiKey = cfg.EmbyApiKey;
            SyncScheduleHour = cfg.SyncScheduleHour;
        }

        public void ApplyTo(PluginConfiguration cfg)
        {
            cfg.SyncPathMovies = SyncPathMovies;
            cfg.SyncPathShows = SyncPathShows;
            cfg.SyncPathAnime = SyncPathAnime;
            cfg.LibraryNameMovies = LibraryNameMovies;
            cfg.LibraryNameSeries = LibraryNameSeries;
            cfg.LibraryNameAnime = LibraryNameAnime;
            cfg.EnableAnimeLibrary = EnableAnimeLibrary;
            cfg.EnableNfoHints = EnableNfoHints;
            cfg.DeleteStrmOnReadoption = DeleteStrmOnReadoption;
            cfg.MetadataLanguage = MetadataLanguage;
            cfg.MetadataCountryCode = MetadataCountryCode;
            cfg.ImageLanguage = ImageLanguage;
            cfg.SubtitleDownloadLanguages = SubtitleDownloadLanguages;
            cfg.EmbyBaseUrl = EmbyBaseUrl;
            cfg.EmbyApiKey = EmbyApiKey;
            cfg.SyncScheduleHour = SyncScheduleHour;
        }
    }
}
