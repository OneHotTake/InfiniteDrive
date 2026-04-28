using System.ComponentModel;
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

        public override string EditorTitle => "Health";
        public override string EditorDescription =>
            "System health overview. Run maintenance tasks or summon Marvin to execute the full pipeline.";

        // ── System State ─────────────────────────────────────────────────────

        public CaptionItem CaptionSystem { get; set; } = new CaptionItem("System State");

        public StatusItem OverallStatus { get; set; } = new StatusItem("Overall", "Evaluating...", ItemStatus.Unknown);

        public GenericItemList HealthDetails { get; set; } = new GenericItemList();

        // ── Marvin ───────────────────────────────────────────────────────────

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionMarvin { get; set; } = new CaptionItem("Marvin — Pipeline Orchestrator");

        public LabelItem MarvinDescription { get; set; } = new LabelItem(
            "Marvin runs the full 4-phase pipeline:\n" +
            "  1. Sync — fetch manifests, deduplicate, upsert to DB\n" +
            "  2. Populate — write .strm files\n" +
            "  3. Resolve — enrich metadata, notify Emby\n" +
            "  4. Repair — validate state, cleanup orphans");

        public ButtonItem SummonMarvinButton { get; set; } = new ButtonItem("Summon Marvin")
        {
            Icon = IconNames.smart_toy,
            Data1 = SummonMarvinCommand,
            ConfirmationPrompt = "Run the full Marvin pipeline now? This may take several minutes."
        };

        public StatusItem MarvinStatus { get; set; } = new StatusItem("Marvin", "Idle", ItemStatus.None);

        // ── Quick Tasks ──────────────────────────────────────────────────────

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionTasks { get; set; } = new CaptionItem("Quick Tasks");

        public ButtonItem CatalogSyncButton { get; set; } = new ButtonItem("Run Catalog Sync")
        {
            Icon = IconNames.sync,
            Data1 = RunCatalogSyncCommand,
        };

        public StatusItem CatalogSyncStatus { get; set; } = new StatusItem("Sync", "Idle", ItemStatus.None);

        public ButtonItem PreCacheButton { get; set; } = new ButtonItem("Run Pre-Cache")
        {
            Icon = IconNames.cached,
            Data1 = RunPreCacheCommand,
        };

        public StatusItem PreCacheStatus { get; set; } = new StatusItem("Pre-Cache", "Idle", ItemStatus.None);
    }
}
