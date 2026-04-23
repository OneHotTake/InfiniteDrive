namespace InfiniteDrive.Configuration.UI.views
{
    using System.ComponentModel;

    using Emby.Web.GenericEdit;
    using Emby.Web.GenericEdit.Elements;

    using MediaBrowser.Model.Attributes;

    /// <summary>
    /// Libraries tab: sync paths, library names, anime toggle.
    /// </summary>
    public class LibrariesUI : EditableOptionsBase
    {
        public override string EditorTitle => "Libraries";

        public override string EditorDescription =>
            "Configure where .strm files are written and how libraries appear in Emby. "
            + "Folder paths must be accessible from the Emby server.";

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        public CaptionItem CaptionMovies { get; set; } = new CaptionItem("Movies");

        [DisplayName("Movie Sync Path")]
        [Description("File system path where movie .strm files are written.")]
        [EditFolderPicker]
        public string SyncPathMovies { get; set; } = "/media/infinitedrive/movies";

        [DisplayName("Movie Library Name")]
        [Description("Display name for the movies library in Emby.")]
        public string LibraryNameMovies { get; set; } = "Streamed Movies";

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();

        public CaptionItem CaptionShows { get; set; } = new CaptionItem("TV Series");

        [DisplayName("Series Sync Path")]
        [Description("File system path where TV series .strm files are written.")]
        [EditFolderPicker]
        public string SyncPathShows { get; set; } = "/media/infinitedrive/shows";

        [DisplayName("Series Library Name")]
        [Description("Display name for the TV series library in Emby.")]
        public string LibraryNameSeries { get; set; } = "Streamed Series";

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();

        public CaptionItem CaptionAnime { get; set; } = new CaptionItem("Anime");

        [DisplayName("Enable Anime Library")]
        [Description("Create a separate anime library with its own sync path.")]
        public bool EnableAnimeLibrary { get; set; } = true;

        [DisplayName("Anime Sync Path")]
        [Description("File system path where anime .strm files are written.")]
        [EditFolderPicker]
        public string SyncPathAnime { get; set; } = "/media/infinitedrive/anime";

        [DisplayName("Anime Library Name")]
        [Description("Display name for the anime library in Emby.")]
        public string LibraryNameAnime { get; set; } = "Streamed Anime";
    }
}
