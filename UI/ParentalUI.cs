using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI
{
    public class ParentalUI : EditableOptionsBase
    {
        public override string EditorTitle => "Parental Controls";

        [DisplayName("TMDB API Key")]
        [Description("Required for content ratings (MPAA/TV). Free from themoviedb.org → Settings → API. Same service Emby uses.")]
        [IsPassword]
        public string TmdbApiKey { get; set; } = string.Empty;

        [DisplayName("Block Unrated for Restricted Users")]
        [Description("When enabled, users with parental restrictions will NOT see content without known ratings. Unrestricted users are never affected.")]
        public bool BlockUnratedForRestricted { get; set; } = true;

        [DisplayName("Test TMDB Key")]
        public ButtonItem TestTmdbButton => new ButtonItem
        {
            Caption = "Test TMDB Key",
            CommandId = "test-tmdb"
        };

        [DisplayName("Filter Status")]
        public StatusItem FilterStatus => new StatusItem
        {
            Caption = "Parental Filtering",
            StatusText = string.IsNullOrEmpty(TmdbApiKey) ? "Disabled — no TMDB key configured" : "Active"
        };

        public ParentalUI() { }

        public ParentalUI(PluginConfiguration cfg)
        {
            TmdbApiKey = cfg.TmdbApiKey;
            BlockUnratedForRestricted = cfg.BlockUnratedForRestricted;
        }

        public void ApplyTo(PluginConfiguration cfg)
        {
            cfg.TmdbApiKey = TmdbApiKey;
            cfg.BlockUnratedForRestricted = BlockUnratedForRestricted;
        }
    }
}
