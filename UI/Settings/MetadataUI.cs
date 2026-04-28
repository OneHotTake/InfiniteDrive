using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;

namespace InfiniteDrive.UI.Settings
{
    public class MetadataUI : EditableOptionsBase
    {
        public override string EditorTitle => "Metadata";
        public override string EditorDescription =>
            "Language and episode handling settings for library metadata.";

        public CaptionItem CaptionLang { get; set; } = new CaptionItem("Language & Ratings");

        [DisplayName("Metadata Language")]
        [Description("Preferred metadata language code. Default: en.")]
        public string MetadataLanguage { get; set; } = "en";

        [DisplayName("Certification Country")]
        [Description("Country code for content ratings lookup (US, GB, etc). Default: US.")]
        public string MetadataCertificationCountry { get; set; } = "US";

        [DisplayName("Subtitle Languages")]
        [Description("Preferred subtitle languages, comma-separated. Default: en.")]
        public string SubtitleDownloadLanguages { get; set; } = "en";

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionEpisodes { get; set; } = new CaptionItem("Episode Handling");

        [DisplayName("Skip Future Episodes")]
        [Description("Skip episodes that have not aired yet.")]
        public bool SkipFutureEpisodes { get; set; } = true;

        [DisplayName("Future Episode Buffer (days)")]
        [Description("Grace period in days before considering an episode aired.")]
        public int FutureEpisodeBufferDays { get; set; } = 2;
    }
}
