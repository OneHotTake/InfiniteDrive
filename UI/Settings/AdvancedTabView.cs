using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.UI.Settings
{
    public class AdvancedTabView : PluginPageView
    {
        public AdvancedTabView(string pluginId, AdvancedUI ui) : base(pluginId)
        {
            ContentData = ui;
        }

        private AdvancedUI UI => (AdvancedUI)ContentData;

        public override bool IsCommandAllowed(string commandKey) => true;

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var cmd = commandId;
            if (string.IsNullOrEmpty(cmd) && !string.IsNullOrEmpty(data))
                cmd = data.Split(':')[0];

            switch (cmd)
            {
                case AdvancedUI.ClearCacheCommand:
                    await ClearCacheAsync();
                    return this;

                case AdvancedUI.RebuildLibrariesCommand:
                    RebuildLibraries();
                    return this;

                case AdvancedUI.ResetAllDataCommand:
                    await ResetAllDataAsync();
                    return this;

                case AdvancedUI.ResetFactoryDefaultsCommand:
                    await ResetFactoryDefaultsAsync();
                    return this;
            }

            return await base.RunCommand(itemId, commandId, data);
        }

        // ═══════════════════════════════════════════════════════════════
        // Commands
        // ═══════════════════════════════════════════════════════════════

        private async Task ClearCacheAsync()
        {
            SetStatus("Clearing stream resolution cache...", ItemStatus.InProgress);
            try
            {
                var db = Plugin.Instance.DatabaseManager;
                await db.ClearResolutionCacheAsync();
                await db.VacuumAsync();
                SetStatus("Cache cleared. Marvin will re-resolve streams on next cycle.", ItemStatus.Succeeded);
                Plugin.Instance.Logger.LogInformation("[Advanced] Resolution cache cleared");
            }
            catch (Exception ex)
            {
                SetStatus($"Failed: {ex.Message}", ItemStatus.Failed);
                Plugin.Instance.Logger.LogWarning(ex, "[Advanced] Cache clear failed");
            }
        }

        private void RebuildLibraries()
        {
            try
            {
                Plugin.Instance.TriggerBackgroundSync();
                SetStatus("Rebuild triggered — Marvin will run a full sync in the background.", ItemStatus.Succeeded);
            }
            catch (Exception ex)
            {
                SetStatus($"Failed: {ex.Message}", ItemStatus.Failed);
            }
        }

        private async Task ResetAllDataAsync()
        {
            SetStatus("Resetting all data...", ItemStatus.InProgress);
            try
            {
                var db = Plugin.Instance.DatabaseManager;
                var paths = await db.ResetAllAsync();

                // ResetAllAsync clears the DB rows and returns the .strm paths — actually
                // delete those files (and their version variants), otherwise "Wipe Library
                // Data" leaves thousands of orphaned .strm files on disk.
                int filesDeleted = 0;
                var strmManager = Plugin.Instance.StrmFileManager;
                foreach (var path in paths)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    try
                    {
                        strmManager?.DeleteWithVersions(path);
                        filesDeleted++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Instance.Logger.LogDebug(ex, "[Advanced] Failed to delete .strm during wipe: {Path}", path);
                    }
                }

                SetStatus($"All data reset — {filesDeleted} .strm file(s) deleted. Settings preserved.", ItemStatus.Succeeded);
                Plugin.Instance.Logger.LogInformation("[Advanced] All data reset — {Count} .strm files deleted", filesDeleted);
            }
            catch (Exception ex)
            {
                SetStatus($"Failed: {ex.Message}", ItemStatus.Failed);
                Plugin.Instance.Logger.LogWarning(ex, "[Advanced] Reset all data failed");
            }
        }

        private Task ResetFactoryDefaultsAsync()
        {
            SetStatus("Applying factory defaults...", ItemStatus.Warning);

            try
            {
                var cfg = Plugin.Instance.Configuration;
                cfg.PrimaryManifestUrl = string.Empty;
                cfg.SecondaryManifestUrl = string.Empty;
                cfg.EnableBackupAioStreams = false;
                cfg.EnableAioStreamsCatalog = true;
                cfg.AioStreamsCatalogIds = string.Empty;
                cfg.AioStreamsAcceptedStreamTypes = "debrid";
                cfg.EmbyApiKey = string.Empty;
                cfg.LibraryRootMovies = "/media/infinitedrive/movies";
                cfg.SyncPathMovies = "/media/infinitedrive/movies";
                cfg.SyncPathShows = "/media/infinitedrive/shows";
                cfg.SyncPathAnime = "/media/infinitedrive/anime";
                cfg.LibraryNameMovies = "Streamed Movies";
                cfg.LibraryNameSeries = "Streamed Series";
                cfg.LibraryNameAnime = "Streamed Anime";
                cfg.MetadataLanguage = "en";
                cfg.MetadataCertificationCountry = "US";
                cfg.DontPanic = false;
                cfg.ImageLanguage = "en";
                cfg.SubtitleDownloadLanguages = "en";
                cfg.SkipFutureEpisodes = true;
                cfg.FutureEpisodeBufferDays = 2;
                cfg.CacheLifetimeMinutes = 360;
                cfg.ApiDailyBudget = 2000;
                cfg.MaxConcurrentResolutions = 3;
                cfg.CatalogItemLimitsJson = string.Empty;
                cfg.CatalogSyncIntervalHours = 1;
                cfg.EnablePreCache = true;
                cfg.PreCacheBatchSize = 42;
                cfg.PreCacheTTLDays = 14;
                cfg.InMemoryCacheTtlMinutes = 360;
                cfg.ProviderPriorityOrder = "realdebrid,torbox,alldebrid,debridlink,premiumize,stremthru,usenet,http";
                cfg.CandidatesPerProvider = 3;
                cfg.MaxCuratedStreams = 7;
                cfg.StreamBucketsJson = @"[{""resTier"":0,""srcMax"":0,""maxCount"":2},{""resTier"":0,""srcMax"":1,""maxCount"":1},{""resTier"":0,""srcMax"":2,""maxCount"":1},{""resTier"":1,""srcMax"":0,""maxCount"":1},{""resTier"":1,""srcMax"":1,""maxCount"":2},{""resTier"":2,""srcMax"":1,""maxCount"":1}]";
                cfg.DirectPlayEnabled = true;
                cfg.VersionLabelPrefix = "InfiniteDrive · ";
                cfg.SyncResolveTimeoutSeconds = 30;
                cfg.AioStreamsDiscoveredTimeoutSeconds = 0;
                cfg.AioStreamsDiscoveredName = string.Empty;
                cfg.AioStreamsDiscoveredVersion = string.Empty;
                cfg.AioStreamsIsStreamOnly = false;
                cfg.AioStreamsStreamIdPrefixes = string.Empty;
                cfg.EnableCinemetaDefault = false;
                cfg.CandidateTtlHours = 6;
                cfg.DefaultSlotKey = "hd_broad";
                cfg.PendingRehydrationOperations = new List<string>();
                cfg.NextUpLookaheadEpisodes = 2;
                cfg.SyncScheduleHour = 3;
                cfg.DeleteStrmOnReadoption = true;
                cfg.AioMetadataBaseUrl = string.Empty;
                cfg.MetadataIdTypeCensus = "{}";
                cfg.MetadataEnabledIdTypes = "[]";
                cfg.SystemRssFeedUrls = string.Empty;
                cfg.DisabledSourceKeysJson = string.Empty;
                cfg.IsFirstRunComplete = false;
                cfg.TmdbApiKey = string.Empty;
                cfg.BlockUnratedForRestricted = true;
                cfg.TraktClientId = string.Empty;
                cfg.UserCatalogLimit = 5;
                cfg.CertificationCountry = "US";
                cfg.DefaultSubtitleLanguage = "en";
                cfg.DefaultQualityTier = "1080p (any)";
                cfg.HideUnratedContent = false;
                cfg.MaxListsPerUser = 10;
                cfg.MarvinProcessIntervalMinutes = 10;
                cfg.StreamResolutionBatchSize = 42;
                cfg.MarvinActionsPerHour = 360;
                cfg.PluginLogLevel = "Info";
                cfg.RespectPlaylistsWhenPruning = true;
                cfg.AutoDeduplicatePhysicalMedia = true;
                Plugin.Instance.SaveConfiguration();
                SetStatus("Factory defaults applied. Please reconfigure the plugin from the Connect tab.", ItemStatus.Succeeded);
                Plugin.Instance.Logger.LogInformation("[Advanced] Factory defaults restored");
            }
            catch (Exception ex)
            {
                SetStatus($"Failed: {ex.Message}", ItemStatus.Failed);
                Plugin.Instance.Logger.LogWarning(ex, "[Advanced] Factory reset failed");
            }

            RaiseUIViewInfoChanged();
            return Task.CompletedTask;
        }

        private void SetStatus(string text, ItemStatus status)
        {
            UI.ActionStatus.StatusText = text;
            UI.ActionStatus.Status = status;
            RaiseUIViewInfoChanged();
        }

        // ═══════════════════════════════════════════════════════════════
        // Save
        // ═══════════════════════════════════════════════════════════════

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            cfg.PluginLogLevel = UI.PluginLogLevel ?? "Info";
            Plugin.Instance.SaveConfiguration();
            Plugin.Instance.TriggerBackgroundSync();
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
