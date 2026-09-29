using System;
using System.Linq;
using System.Text.Json;
using InfiniteDrive.Services;
using Emby.Web.GenericEdit.Elements.List;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI.Settings
{
    public class SyncAndMarvinTabView : PluginPageView
    {
        public SyncAndMarvinTabView(string pluginId, SyncAndMarvinUI ui)
            : base(pluginId)
        {
            ContentData = ui;
            LoadMarvinStatus(ui);
            _ = LoadImportsAsync();
        }

        private int _importOffset;
        private SyncAndMarvinUI UI => (SyncAndMarvinUI)ContentData;

        public override bool IsCommandAllowed(string commandKey) => true;

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var cmd = commandId;
            if (string.IsNullOrEmpty(cmd) && !string.IsNullOrEmpty(data))
                cmd = data.Split(':')[0];

            if (cmd.StartsWith("Import", StringComparison.Ordinal))
            {
                object? result = null;
                switch (cmd)
                {
                    case "ImportObserve": result = await ImportHealthService.ApplyAsync(new() { Action = "mode", Mode = "Observe" }); break;
                    case "ImportRepair": result = await ImportHealthService.ApplyAsync(new() { Action = "mode", Mode = "Repair" }); break;
                    case "ImportOff": result = await ImportHealthService.ApplyAsync(new() { Action = "mode", Mode = "Off" }); break;
                    case "ImportResumeProvider": result = await ImportHealthService.ApplyAsync(new() { Action = "resume_provider" }); break;
                    case "ImportCheck": result = await ImportHealthService.ApplyAsync(new() { Action = "check" }); break;
                    case "ImportIncludeSpecials":
                    case "ImportExcludeSpecials":
                        result = await ImportHealthService.ApplyAsync(new() { Action = cmd == "ImportIncludeSpecials" ? "include_specials" : "exclude_specials", Identity = itemId }); break;
                    case "ImportNext": _importOffset += 25; break;
                    case "ImportRestore":
                    case "ImportSuppress":
                        var parts = itemId.Split('|');
                        if (parts.Length == 2) result = await ImportHealthService.ApplyAsync(new()
                            { Action = cmd == "ImportRestore" ? "restore" : "suppress", Identity = parts[0], EpisodeKey = parts[1] });
                        break;
                }
                await LoadImportsAsync();
                if (result != null)
                {
                    using var response = JsonDocument.Parse(JsonSerializer.Serialize(result));
                    var outcome = response.RootElement.GetProperty("Status").GetString();
                    UI.ImportStatus.StatusText += outcome == "observe_first"
                        ? " · Run an observation before enabling recovery."
                        : " · " + (outcome ?? "Request recorded").Replace('_', ' ');
                }
                RaiseUIViewInfoChanged();
                return this;
            }
            switch (cmd)
            {
                case SyncAndMarvinUI.RunMarvinNowCommand:
                    RunMarvinNow();
                    return this;
            }

            return await base.RunCommand(itemId, commandId, data);
        }

        // ═══════════════════════════════════════════════════════════════
        // Load
        // ═══════════════════════════════════════════════════════════════

        private async Task LoadImportsAsync()
        {
            try
            {
                var db = Plugin.Instance.DatabaseManager;
                UI.ImportItems.Clear();
                UI.ImportStatus.StatusText = Plugin.Instance.Configuration.ImportRecoveryMode +
                    " · coverage is a dated observation; indexing does not prove playback";
                if (db.GetMetadata("import_last_run") == null) return;
                var page = await db.GetImportCoveragePageAsync(_importOffset, 25);
                if (page.Count == 0 && _importOffset > 0)
                { _importOffset = 0; page = await db.GetImportCoveragePageAsync(0, 25); }
                foreach (var title in page)
                {
                    UI.ImportItems.Add(new GenericListItem { PrimaryText = title.Title,
                        SecondaryText = $"{title.Items.Count(x => x.State == "indexed")}/{title.Items.Count(x => x.Eligible)} indexed · {title.SnapshotStatus.Replace('_', ' ')} · provider {title.ProviderStatus.Replace('_', ' ')} · checked {title.CheckedAt:u} · {title.Exclusion}",
                        Icon = IconNames.info, IconMode = ItemListIconMode.SmallRegular,
                        Button1 = new ButtonItem(title.IncludeSpecials ? "Exclude specials" : "Include released specials")
                        { Data1 = title.Identity, CommandId = title.IncludeSpecials ? "ImportExcludeSpecials" : "ImportIncludeSpecials" } });
                    foreach (var episode in title.Items.Where(x => x.State != "indexed").Take(50))
                        UI.ImportItems.Add(new GenericListItem { PrimaryText = (episode.Season.HasValue ? $"S{episode.Season:D2}E{episode.Episode:D2}" : "Movie") + " · " + episode.State.Replace('_', ' '),
                            SecondaryText = $"{episode.Eligibility.Replace('_', ' ')} · {episode.Failure.Replace('_', ' ')} · next retry {episode.NextAttempt:u}",
                            Button1 = new ButtonItem(episode.Suppressed ? "Restore" : "Suppress recovery")
                            { Data1 = title.Identity + "|" + episode.Key, CommandId = episode.Suppressed ? "ImportRestore" : "ImportSuppress" } });
                }
            }
            catch { UI.ImportStatus.StatusText = "Coverage unavailable; refresh after the next observation run."; }
            finally { RaiseUIViewInfoChanged(); }
        }

        private void LoadMarvinStatus(SyncAndMarvinUI ui)
        {
            ui.MarvinStatus.StatusText =
                "Automatic · provider backoff respected · playlists and watched items protected";
            ui.MarvinStatus.Status = ItemStatus.Succeeded;
            RaiseUIViewInfoChanged();
        }

        // ═══════════════════════════════════════════════════════════════
        // Commands
        // ═══════════════════════════════════════════════════════════════

        private void RunMarvinNow()
        {
            UI.MarvinStatus.StatusText = "Running Marvin...";
            UI.MarvinStatus.Status = ItemStatus.InProgress;
            RaiseUIViewInfoChanged();

            try
            {
                Plugin.Instance.TriggerBackgroundSync();
                UI.MarvinStatus.StatusText = "Marvin triggered — refresh page to see updates";
                UI.MarvinStatus.Status = ItemStatus.Succeeded;
            }
            catch (Exception ex)
            {
                UI.MarvinStatus.StatusText = $"Failed: {ex.Message}";
                UI.MarvinStatus.Status = ItemStatus.Failed;
            }

            RaiseUIViewInfoChanged();
        }

    }
}
