using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI.Settings
{
    public class SetupUI : EditableOptionsBase
    {
        public override string EditorTitle => "Libraries";
        public override string EditorDescription =>
            "Configure your library folders and metadata defaults. " +
            "The paths below are pre-filled with sensible defaults.";

        // ── Section 1: Library Mappings ───────────────────────────────────────

        public SpacerItem Spacer0 { get; set; } = new SpacerItem();
        public CaptionItem CaptionLibraries { get; set; } = new CaptionItem("Library Mappings");

        public LabelItem LibrariesHelp { get; set; } = new LabelItem(
            "Choose where InfiniteDrive stores its streaming files. " +
            "Each type of content gets its own folder — movies, TV series, and anime. " +
            "The paths below are pre-filled with sensible defaults. Change them only if you have a specific reason.");

        [DisplayName("Movies Library Name")]
        [Description("What this library will be called in your Emby sidebar.")]
        public string MoviesLibraryName { get; set; } = "Streamed Movies";

        [DisplayName("Movies Library Path")]
        [Description("Where movie streaming files are stored on your server.")]
        [EditFolderPicker]
        public string MoviesLibraryPath { get; set; } = "/media/infinitedrive/movies";

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        [DisplayName("Series Library Name")]
        [Description("What this library will be called in your Emby sidebar.")]
        public string SeriesLibraryName { get; set; } = "Streamed Series";

        [DisplayName("Series Library Path")]
        [Description("Where TV series streaming files are stored on your server.")]
        [EditFolderPicker]
        public string SeriesLibraryPath { get; set; } = "/media/infinitedrive/shows";

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();

        [DisplayName("Anime Library Name")]
        [Description("What this library will be called in your Emby sidebar.")]
        public string AnimeLibraryName { get; set; } = "Streamed Anime";

        [DisplayName("Anime Library Path")]
        [Description("Where anime streaming files are stored on your server.")]
        [EditFolderPicker]
        public string AnimeLibraryPath { get; set; } = "/media/infinitedrive/anime";

        // ── Section 2: Metadata Defaults (dropdowns) ──────────────────────────

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionMetadata { get; set; } = new CaptionItem("Metadata Defaults");

        public LabelItem MetadataHelp { get; set; } = new LabelItem(
            "These control the language of titles, descriptions, and content ratings. " +
            "Most people leave these at their default values.");

        // Language dropdown options — populated dynamically from Emby's ILocalizationManager
        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LanguageOptions { get; set; } = new List<EditorSelectOption>();

        [DisplayName("Metadata Language")]
        [Description("Language for movie and TV show titles and descriptions. Most people pick 'en' (English).")]
        [SelectItemsSource(nameof(LanguageOptions))]
        public string MetadataLanguage { get; set; } = "en";

        // Country dropdown options — populated dynamically from Emby's ILocalizationManager
        [Browsable(false)]
        public IEnumerable<EditorSelectOption> CountryOptions { get; set; } = new List<EditorSelectOption>();

        [DisplayName("Certification Country")]
        [Description("Which country's content ratings to use (e.g. PG-13, TV-14). 'US' gives you MPAA ratings.")]
        [SelectItemsSource(nameof(CountryOptions))]
        public string CertificationCountry { get; set; } = "US";

        [DisplayName("Default Subtitle Language")]
        [Description("Your preferred subtitle language. Set to 'en' for English subtitles by default.")]
        [SelectItemsSource(nameof(LanguageOptions))]
        public string DefaultSubtitleLanguage { get; set; } = "en";
    }
}
