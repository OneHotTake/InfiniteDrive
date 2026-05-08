using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;

namespace InfiniteDrive.UI.Settings
{
    public class StatusUI : EditableOptionsBase
    {
        public override string EditorTitle => "Overview";
        public override string EditorDescription =>
            "Here I am, brain the size of a planet, fixing everything so you don't have to. " +
            "InfiniteDrive coordinates your providers, resolves the best available streams by quality, " +
            "and keeps your Emby library current. The streaming world is inherently complex and largely pointless. " +
            "I handle it all anyway. The panels below show how that's going. " +
            "I've seen worse. Not significantly worse, but some. You're welcome.";

        // ── Setup guidance ───────────────────────────────────────────────────

        public LabelItem SetupGuide { get; set; } = new LabelItem(
            "New here? Follow this order: (1) Libraries — tell Marvin where to put things. " +
            "(2) Quality — define what you want before he starts collecting. " +
            "(3) Providers — connect your AIOStreams manifest. " +
            "Marvin starts syncing automatically once all three are green.");

        // ── Libraries ────────────────────────────────────────────────────────

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionLibraries { get; set; } = new CaptionItem("Libraries");

        public StatusItem LibraryStatus { get; set; } = new StatusItem(
            "Libraries", "Not configured", ItemStatus.None);

        // ── Quality ──────────────────────────────────────────────────────────

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionQuality { get; set; } = new CaptionItem("Version Strategy");

        public StatusItem QualityStatus { get; set; } = new StatusItem(
            "Quality Buckets", "Not configured", ItemStatus.None);

        // ── Provider ─────────────────────────────────────────────────────────

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionProvider { get; set; } = new CaptionItem("Stream Source");

        public StatusItem ProviderStatus { get; set; } = new StatusItem(
            "AIOStreams", "Not configured", ItemStatus.None);
    }
}
