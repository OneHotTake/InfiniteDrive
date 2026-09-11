using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI.Settings
{
    public class SetupUI : EditableOptionsBase
    {
        public override string EditorTitle => "Libraries";
        public override string EditorDescription =>
            "Configure your library folders. Language, artwork and certification preferences follow Emby. " +
            "The paths below are pre-filled with sensible defaults.";

        // ── Section 1: Library Mappings ───────────────────────────────────────

        public SpacerItem Spacer0 { get; set; } = new SpacerItem();
        public CaptionItem CaptionLibraries { get; set; } = new CaptionItem("Library Folders");

        public LabelItem LibrariesHelp { get; set; } = new LabelItem(
            "Map each content type to a library name (shown in Emby) and the folder where .strm files will be written.");

        public CaptionItem CaptionMovies { get; set; } = new CaptionItem("Movies");

        [DisplayName("Library Name")]
        [Description("What this library will be called in your Emby sidebar.")]
        public string MoviesLibraryName { get; set; } = "Streamed Movies";

        [DisplayName("Folder Path")]
        [Description("Where movie streaming files are stored on your server.")]
        [EditFolderPicker]
        public string MoviesLibraryPath { get; set; } = "/media/infinitedrive/movies";

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionSeries { get; set; } = new CaptionItem("Series");

        [DisplayName("Library Name")]
        [Description("What this library will be called in your Emby sidebar.")]
        public string SeriesLibraryName { get; set; } = "Streamed Series";

        [DisplayName("Folder Path")]
        [Description("Where TV series streaming files are stored on your server.")]
        [EditFolderPicker]
        public string SeriesLibraryPath { get; set; } = "/media/infinitedrive/shows";

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionAnime { get; set; } = new CaptionItem("Anime");

        [DisplayName("Library Name")]
        [Description("What this library will be called in your Emby sidebar.")]
        public string AnimeLibraryName { get; set; } = "Streamed Anime";

        [DisplayName("Folder Path")]
        [Description("Where anime streaming files are stored on your server.")]
        [EditFolderPicker]
        public string AnimeLibraryPath { get; set; } = "/media/infinitedrive/anime";

    }
}
