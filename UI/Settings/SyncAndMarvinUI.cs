using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements.List;
using Emby.Web.GenericEdit.Elements;

namespace InfiniteDrive.UI.Settings
{
    public class SyncAndMarvinUI : EditableOptionsBase
    {
        public const string RunMarvinNowCommand = nameof(RunMarvinNowCommand);

        public override string EditorTitle => "Marvin";
        public override string EditorDescription => "Brain the size of a planet. Still on library duty.";

        public CaptionItem DashboardCaption { get; set; } = new CaptionItem("Marvin dashboard");
        public LabelItem DashboardHelp { get; set; } = new LabelItem("Current activity and results from the repair pass. Refresh to update. A saved stream still needs a playback test.");
        public ButtonItem RefreshDashboard { get; set; } = new ButtonItem("Refresh dashboard") { Data1 = "ImportRefresh" };
        public GenericItemList DashboardItems { get; set; } = new GenericItemList();
        public CaptionItem TimingCaption { get; set; } = new CaptionItem("Repair timings");
        public GenericItemList TimingItems { get; set; } = new GenericItemList();
        public LabelItem TimingHelp { get; set; } = new LabelItem("Resolution includes pacing and provider wait. Parallel totals overlap. p95 uses the latest 512 calls; averages and totals cover the pass. Checkpoint covers observation saves.");
        public CaptionItem HistoryCaption { get; set; } = new CaptionItem("Recent repair passes");
        public GenericItemList RecentRuns { get; set; } = new GenericItemList();
        public StatusItem MarvinStatus { get; set; } = new StatusItem("Last library pass", "No report yet", ItemStatus.None);
        public ButtonItem RunMarvinNowButton { get; set; } = new ButtonItem("Run Marvin now")
        {
            Icon = IconNames.play_arrow,
            Data1 = RunMarvinNowCommand,
        };

        public SpacerItem SpacerStatus { get; set; } = new SpacerItem();
        public CaptionItem ImportCaption { get; set; } = new CaptionItem("Library upkeep");
        public LabelItem ImportHelp { get; set; } = new LabelItem(
            "Check only finds gaps. Repair & refresh fills them and updates stream choices. Classic importer uses the older import routine.");
        public StatusItem ImportStatus { get; set; } = new StatusItem("Mode", "Checking…", ItemStatus.None);
        public ButtonItem ObserveImports { get; set; } = new ButtonItem("Check only") { Data1 = "ImportObserve" };
        public ButtonItem RepairImports { get; set; } = new ButtonItem("Repair & refresh") { Data1 = "ImportRepair" };
        public ButtonItem StopImports { get; set; } = new ButtonItem("Classic importer") { Data1 = "ImportOff" };

        public SpacerItem SpacerDrive { get; set; } = new SpacerItem();
        public GenericItemList ImprobabilityDrive { get; set; } = new GenericItemList();
        public LabelItem DriveHelp { get; set; } = new LabelItem(
            "For a big library refresh: up to 40,000 stream checks a day instead of 200. Actual speed depends on your sources. Repair & refresh must be on.");

        public SpacerItem SpacerLibrary { get; set; } = new SpacerItem();
        public CaptionItem LibraryCaption { get; set; } = new CaptionItem("Library checks");
        public LabelItem LibraryHelp { get; set; } = new LabelItem("These checks track what's in Emby. Press Play to test a stream.");
        public ButtonItem RefreshImports { get; set; } = new ButtonItem("Refresh status") { Data1 = "ImportRefresh" };
        public ButtonItem CheckImports { get; set; } = new ButtonItem("Retry due items") { Data1 = "ImportCheck" };
        public ButtonItem ResumeProvider { get; set; } = new ButtonItem("Retry after fixing provider settings") { Data1 = "ImportResumeProvider" };
        public LabelItem ReprocessHelp { get; set; } = new LabelItem("After an outage, queue one retry of the deferred items. Marvin waits through provider cooldowns, tests a small sample, then drains the queue as AIO responds. Existing files and retry history stay intact.");
        public ButtonItem ReprocessDeferred { get; set; } = new ButtonItem("Reprocess deferred items") { Data1 = "ImportReprocess" };
        public ButtonItem CancelReprocess { get; set; } = new ButtonItem("Cancel pending reprocessing") { Data1 = "ImportCancelReprocess" };
        public GenericItemList ImportItems { get; set; } = new GenericItemList();
        public ButtonItem NextImports { get; set; } = new ButtonItem("Next 25 titles") { Data1 = "ImportNext" };
    }
}
