using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI.Settings
{
    public class SetupUI : EditableOptionsBase
    {
        public const string TestPrimaryCommand = nameof(TestPrimaryCommand);
        public const string TestSecondaryCommand = nameof(TestSecondaryCommand);
        public const string RunSetupTestCommand = nameof(RunSetupTestCommand);

        public override string EditorTitle => "Setup";
        public override string EditorDescription =>
            "Configure the essentials — providers, library paths, metadata defaults, and quality. " +
            "*Don't Panic* — Marvin is on the case.";

        // ── Status Banner ─────────────────────────────────────────────────────

        public StatusItem SetupStatus { get; set; } = new StatusItem("Setup", "Not configured — enter Primary Manifest URL to get started", ItemStatus.Unavailable);

        public ButtonItem RunSetupTestButton { get; set; } = new ButtonItem("Run Full Setup Test")
        {
            Icon = IconNames.network_check,
            Data1 = RunSetupTestCommand,
        };

        // ── Section 1: AIOStreams Providers ───────────────────────────────────

        public SpacerItem Spacer0 { get; set; } = new SpacerItem();
        public CaptionItem CaptionProviders { get; set; } = new CaptionItem("AIOStreams Providers");

        [DisplayName("Primary Manifest URL")]
        [Description("Full manifest URL from your AIOStreams web UI (include auth token if required).")]
        public string PrimaryManifestUrl { get; set; } = string.Empty;

        public StatusItem PrimaryStatus { get; set; } = new StatusItem("Primary", "Not tested", ItemStatus.Unknown);

        public ButtonItem TestPrimaryButton { get; set; } = new ButtonItem("Test Primary")
        {
            Icon = IconNames.network_check,
            Data1 = TestPrimaryCommand,
        };

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        [DisplayName("Secondary Manifest URL")]
        [Description("If set, used automatically when the primary is unreachable.")]
        public string SecondaryManifestUrl { get; set; } = string.Empty;

        public StatusItem SecondaryStatus { get; set; } = new StatusItem("Secondary", "Not configured", ItemStatus.Unavailable);

        public ButtonItem TestSecondaryButton { get; set; } = new ButtonItem("Test Secondary")
        {
            Icon = IconNames.network_check,
            Data1 = TestSecondaryCommand,
        };

        // ── Section 2: Emby Server ────────────────────────────────────────────

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionEmby { get; set; } = new CaptionItem("Emby Server");

        [DisplayName("Emby External URL")]
        [Description(
            "Must be externally reachable by all clients (not localhost). " +
            "This is written into every .strm file. Example: http://192.168.1.100:8096")]
        public string EmbyBaseUrl { get; set; } = "http://192.168.1.100:8096";

        // ── Section 3: Library Mappings ───────────────────────────────────────

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionLibraries { get; set; } = new CaptionItem("Library Mappings");

        [DisplayName("Movies Library Name")]
        [Description("Display name shown in Emby sidebar for the movies library.")]
        public string MoviesLibraryName { get; set; } = "InfiniteDrive Movies";

        [DisplayName("Movies Library Path")]
        [Description("Filesystem path where movie .strm files are written.")]
        [EditFolderPicker]
        public string MoviesLibraryPath { get; set; } = string.Empty;

        public SpacerItem Spacer4 { get; set; } = new SpacerItem();

        [DisplayName("Series Library Name")]
        [Description("Display name shown in Emby sidebar for the TV series library.")]
        public string SeriesLibraryName { get; set; } = "InfiniteDrive Series";

        [DisplayName("Series Library Path")]
        [Description("Filesystem path where TV show .strm files are written.")]
        [EditFolderPicker]
        public string SeriesLibraryPath { get; set; } = string.Empty;

        public SpacerItem Spacer5 { get; set; } = new SpacerItem();

        [DisplayName("Anime Library Name")]
        [Description("Display name shown in Emby sidebar for the anime library.")]
        public string AnimeLibraryName { get; set; } = "InfiniteDrive Anime";

        [DisplayName("Anime Library Path")]
        [Description("Filesystem path where anime .strm files are written.")]
        [EditFolderPicker]
        public string AnimeLibraryPath { get; set; } = string.Empty;

        // ── Section 4: Metadata Defaults ──────────────────────────────────────

        public SpacerItem Spacer6 { get; set; } = new SpacerItem();
        public CaptionItem CaptionMetadata { get; set; } = new CaptionItem("Metadata Defaults");

        [DisplayName("Metadata Language")]
        [Description("Language code for titles and overviews (e.g. en, fr, de). Default: en.")]
        public string MetadataLanguage { get; set; } = "en";

        [DisplayName("Certification Country")]
        [Description("Country code for content ratings (US, GB, etc). Default: US.")]
        public string CertificationCountry { get; set; } = "US";

        [DisplayName("Default Subtitle Language")]
        [Description("Preferred subtitle language code. Default: en.")]
        public string DefaultSubtitleLanguage { get; set; } = "en";

        // ── Section 5: Default Quality ────────────────────────────────────────

        public SpacerItem Spacer7 { get; set; } = new SpacerItem();
        public CaptionItem CaptionQuality { get; set; } = new CaptionItem("Default Quality");

        [DisplayName("Default Quality Tier")]
        [Description("Quality tier that plays automatically when multiple streams are available.")]
        public string DefaultQualityTier { get; set; } = "1080p (any)";

        public LabelItem QualityOptions { get; set; } = new LabelItem(
            "Available tiers (highest to lowest):\n" +
            "  4K REMUX / HDR / Atmos\n" +
            "  4K 5.1 / DTS\n" +
            "  4K (any)\n" +
            "  1080p Atmos / TrueHD\n" +
            "  1080p 5.1\n" +
            "  1080p (any)\n" +
            "  720p\n" +
            "  SD / Unknown / Low-bandwidth");

        // ── Setup Test Result ─────────────────────────────────────────────────

        public SpacerItem Spacer8 { get; set; } = new SpacerItem();
        public StatusItem SetupTestResult { get; set; } = new StatusItem("Test Result", "", ItemStatus.None);

        // ── Footer ────────────────────────────────────────────────────────────

        public SpacerItem FooterSpacer { get; set; } = new SpacerItem();
        public LabelItem Footer { get; set; } = new LabelItem("*Don't Panic* — Marvin is on the case.");
    }
}
