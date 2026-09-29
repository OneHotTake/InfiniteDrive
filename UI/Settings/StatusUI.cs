using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;

namespace InfiniteDrive.UI.Settings
{
    public class StatusUI : EditableOptionsBase
    {
        public override string EditorTitle => "Overview";
        public override string EditorDescription =>
            "I keep your streaming library in order. Try to contain your excitement.";

        // ── Setup guidance ───────────────────────────────────────────────────

        public LabelItem SetupGuide { get; set; } = new LabelItem(
            "Start with Libraries, then Quality, then Providers. Add your catalogs or lists on Sources. Marvin takes it from there.");

        // ── Content readiness (headline traffic light) ───────────────────────

        public StatusItem ContentReadiness { get; set; } = new StatusItem(
            "Content", "Checking…", ItemStatus.None);

        // ── Libraries ────────────────────────────────────────────────────────

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionLibraries { get; set; } = new CaptionItem("Libraries");

        public StatusItem LibraryStatus { get; set; } = new StatusItem(
            "Libraries", "Not configured", ItemStatus.None);

        // ── Quality ──────────────────────────────────────────────────────────

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionQuality { get; set; } = new CaptionItem("Quality");

        public StatusItem QualityStatus { get; set; } = new StatusItem(
            "Quality Buckets", "Not configured", ItemStatus.None);

        // ── Provider ─────────────────────────────────────────────────────────

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionProvider { get; set; } = new CaptionItem("Stream Source");

        public StatusItem ProviderStatus { get; set; } = new StatusItem(
            "AIOStreams", "Not configured", ItemStatus.None);
    }
}
