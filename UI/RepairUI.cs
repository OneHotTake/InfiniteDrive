using System;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;

namespace InfiniteDrive.UI
{
    public class RepairUI : EditableOptionsBase
    {
        public override string EditorTitle => "Repair";

        // ── Status ──

        [DisplayName("Plugin Status")]
        public StatusItem PluginStatus { get; set; } = new StatusItem
        {
            Caption = "InfiniteDrive",
            StatusText = "Loading..."
        };

        [DisplayName("Manifest Status")]
        public StatusItem ManifestStatus { get; set; } = new StatusItem
        {
            Caption = "Manifest",
            StatusText = "Unknown"
        };

        // ── Quick Actions ──

        [DisplayName("Sync Catalogs")]
        [Description("Trigger an immediate catalog sync.")]
        public ButtonItem SyncNowButton => new ButtonItem
        {
            Caption = "Sync Catalogs Now",
            CommandId = "trigger-sync"
        };

        [DisplayName("Summon Marvin")]
        [Description("Run the Marvin automated diagnostic and repair task.")]
        public ButtonItem MarvinButton => new ButtonItem
        {
            Caption = "Summon Marvin",
            CommandId = "summon-marvin",
            ConfirmationPrompt = "Marvin will run diagnostics and attempt automatic repairs. Continue?"
        };

        [DisplayName("Provision Libraries")]
        [Description("Create library directories and Emby libraries if they don't exist.")]
        public ButtonItem ProvisionButton => new ButtonItem
        {
            Caption = "Provision Libraries",
            CommandId = "provision-libraries"
        };

        [DisplayName("Create Directories")]
        [Description("Create .strm storage directories if they don't exist.")]
        public ButtonItem CreateDirsButton => new ButtonItem
        {
            Caption = "Create Directories",
            CommandId = "create-directories"
        };

        // ── Destructive Actions ──

        [DisplayName("Purge Catalog")]
        [Description("Delete all catalog entries from the database. .strm files remain on disk.")]
        public ButtonItem PurgeButton => new ButtonItem
        {
            Caption = "Purge Catalog",
            CommandId = "purge-catalog",
            ConfirmationPrompt = "This deletes ALL catalog entries from the database. .strm files remain on disk. Continue?"
        };

        [DisplayName("Nuclear Reset")]
        [Description("Delete everything: catalog database, all .strm files, all cached data. Irreversible.")]
        public ButtonItem NuclearButton => new ButtonItem
        {
            Caption = "Nuclear Reset",
            CommandId = "nuclear-reset",
            ConfirmationPrompt = "WARNING: This deletes ALL InfiniteDrive data including .strm files, catalog database, and cached streams. Type 'RESET' to confirm. This cannot be undone."
        };

        [DisplayName("Clear Client Profiles")]
        [Description("Clear all learned client proxy/redirect profiles.")]
        public ButtonItem ClearProfilesButton => new ButtonItem
        {
            Caption = "Clear Client Profiles",
            CommandId = "clear-profiles"
        };

        // ── Misc Settings ──

        [DisplayName("Skip Future Episodes")]
        [Description("Skip episodes that haven't aired yet during catalog sync.")]
        public bool SkipFutureEpisodes { get; set; } = true;

        [DisplayName("Future Episode Buffer (days)")]
        [Description("Days buffer to consider future episodes as aired. Default: 2.")]
        public int FutureEpisodeBufferDays { get; set; } = 2;

        [DisplayName("Default Series Seasons")]
        [Description("Seasons to write when metadata is unavailable. Default: 1.")]
        public int DefaultSeriesSeasons { get; set; } = 1;

        [DisplayName("Default Episodes Per Season")]
        [Description("Episodes per season when metadata is unavailable. Default: 10.")]
        public int DefaultSeriesEpisodesPerSeason { get; set; } = 10;

        [DisplayName("RSS Feed URLs")]
        [Description("Newline-separated system-wide RSS feed URLs. Visible to all users.")]
        public string SystemRssFeedUrls { get; set; } = string.Empty;

        [DisplayName("Last Result")]
        public StatusItem LastResult { get; set; } = new StatusItem
        {
            Caption = "Last Action",
            StatusText = "None"
        };

        public RepairUI() { }

        public RepairUI(PluginConfiguration cfg)
        {
            SkipFutureEpisodes = cfg.SkipFutureEpisodes;
            FutureEpisodeBufferDays = cfg.FutureEpisodeBufferDays;
            DefaultSeriesSeasons = cfg.DefaultSeriesSeasons;
            DefaultSeriesEpisodesPerSeason = cfg.DefaultSeriesEpisodesPerSeason;
            SystemRssFeedUrls = cfg.SystemRssFeedUrls;

            ManifestStatus = new StatusItem
            {
                Caption = "Manifest",
                StatusText = Plugin.GetManifestStatus()
            };
        }

        public void ApplyTo(PluginConfiguration cfg)
        {
            cfg.SkipFutureEpisodes = SkipFutureEpisodes;
            cfg.FutureEpisodeBufferDays = FutureEpisodeBufferDays;
            cfg.DefaultSeriesSeasons = DefaultSeriesSeasons;
            cfg.DefaultSeriesEpisodesPerSeason = DefaultSeriesEpisodesPerSeason;
            cfg.SystemRssFeedUrls = SystemRssFeedUrls;
        }
    }
}
