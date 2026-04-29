using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;

namespace InfiniteDrive.UI.Settings
{
    public class HealthUI : EditableOptionsBase
    {
        public const string SummonMarvinCommand = nameof(SummonMarvinCommand);
        public const string RunCatalogSyncCommand = nameof(RunCatalogSyncCommand);
        public const string RunPreCacheCommand = nameof(RunPreCacheCommand);
        public const string UnblockCommand = nameof(UnblockCommand);

        public override string EditorTitle => "Health";
        public override string EditorDescription =>
            "System status and maintenance actions.";

        // ── At-a-glance Stats ────────────────────────────────────────────────

        public CaptionItem CaptionStats { get; set; } = new CaptionItem("At a Glance");

        public StatusItem OverallStatus { get; set; } = new StatusItem("System", "Loading...", ItemStatus.Unknown);

        public GenericItemList HealthDetails { get; set; } = new GenericItemList();

        // ── Maintenance Actions ──────────────────────────────────────────────

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionMarvin { get; set; } = new CaptionItem("Maintenance");

        public LabelItem MarvinDescription { get; set; } = new LabelItem(
            "Marvin runs the full pipeline: sync → populate → resolve → repair. " +
            "Use this after changing library paths or when content is missing.");

        public ButtonItem SummonMarvinButton { get; set; } = new ButtonItem("Run Full Sync")
        {
            Icon = IconNames.smart_toy,
            Data1 = SummonMarvinCommand,
            ConfirmationPrompt = "Run the full sync pipeline now? This may take several minutes."
        };

        public StatusItem MarvinStatus { get; set; } = new StatusItem("Full Sync", "Idle", ItemStatus.None);

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionTasks { get; set; } = new CaptionItem("Quick Actions");

        public ButtonItem CatalogSyncButton { get; set; } = new ButtonItem("Sync Catalogs")
        {
            Icon = IconNames.sync,
            Data1 = RunCatalogSyncCommand,
        };

        public StatusItem CatalogSyncStatus { get; set; } = new StatusItem("Catalog Sync", "Idle", ItemStatus.None);

        public ButtonItem PreCacheButton { get; set; } = new ButtonItem("Run Stream Pre-Selection")
        {
            Icon = IconNames.cached,
            Data1 = RunPreCacheCommand,
        };

        public StatusItem PreCacheStatus { get; set; } = new StatusItem("Pre-Selection", "Idle", ItemStatus.None);

        // ── Blocked Items ────────────────────────────────────────────────────

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionBlocked { get; set; } = new CaptionItem("Blocked Items");

        public LabelItem BlockedDescription { get; set; } = new LabelItem(
            "Items blocked from syncing. Use Unblock to restore.");

        public GenericItemList BlockedItems { get; set; } = new GenericItemList();
    }
}
