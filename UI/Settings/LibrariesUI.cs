using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI.Settings
{
    public class LibrariesUI : EditableOptionsBase
    {
        public override string EditorTitle => "Libraries";
        public override string EditorDescription =>
            "Set up library paths and names. Each library needs a folder (where .strm files go) " +
            "and a display name (what Emby shows in the sidebar). " +
            "Create the folders first, then add matching libraries in Emby Dashboard.";

        // ── Movies ────────────────────────────────────────────────────────────

        public CaptionItem CaptionMovies { get; set; } = new CaptionItem("Movies");

        [DisplayName("Movies Folder")]
        [Description("Filesystem path where movie .strm files are written.")]
        [EditFolderPicker]
        public string SyncPathMovies { get; set; } = "/media/infinitedrive/movies";

        [DisplayName("Movies Library Name")]
        [Description("Display name shown in Emby sidebar.")]
        public string LibraryNameMovies { get; set; } = "Streamed Movies";

        // ── TV Shows ──────────────────────────────────────────────────────────

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionShows { get; set; } = new CaptionItem("TV Shows");

        [DisplayName("TV Shows Folder")]
        [Description("Filesystem path where TV show .strm files are written.")]
        [EditFolderPicker]
        public string SyncPathShows { get; set; } = "/media/infinitedrive/shows";

        [DisplayName("TV Shows Library Name")]
        [Description("Display name shown in Emby sidebar.")]
        public string LibraryNameSeries { get; set; } = "Streamed Series";

        // ── Anime ─────────────────────────────────────────────────────────────

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionAnime { get; set; } = new CaptionItem("Anime");

        [DisplayName("Anime Folder")]
        [Description("Filesystem path where anime .strm files are written.")]
        [EditFolderPicker]
        public string SyncPathAnime { get; set; } = "/media/infinitedrive/anime";

        [DisplayName("Anime Library Name")]
        [Description("Display name shown in Emby sidebar.")]
        public string LibraryNameAnime { get; set; } = "Streamed Anime";

        // ── Emby Server ───────────────────────────────────────────────────────

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionEmby { get; set; } = new CaptionItem("Emby Server");

        [DisplayName("Emby External URL")]
        [Description(
            "The URL your Emby server is reachable at from client devices (browsers, TVs, apps). " +
            "This is written into every .strm file so clients can reach the plugin. " +
            "Use the external/reachable URL — NOT localhost or 127.0.0.1. " +
            "Example: http://192.168.1.100:8096 or https://emby.mydomain.com")]
        public string EmbyBaseUrl { get; set; } = "http://192.168.1.100:8096";
    }
}
