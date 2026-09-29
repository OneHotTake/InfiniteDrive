using System;
using System.Linq;
using System.Text.Json;
using InfiniteDrive.Services;
using InfiniteDrive.Models;
using ItemStatus = Emby.Web.GenericEdit.Elements.ItemStatus;
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
            ShowSave = false;
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
                    case "ImportCatchUp": result = await ImportHealthService.ApplyAsync(new() { Action = "start_catch_up" }); break;
                    case "ImportNormalSpeed": result = await ImportHealthService.ApplyAsync(new() { Action = "stop_catch_up" }); break;
                    case "ImportOff": result = await ImportHealthService.ApplyAsync(new() { Action = "mode", Mode = "Off" }); break;
                    case "ImportResumeProvider": result = await ImportHealthService.ApplyAsync(new() { Action = "resume_provider" }); break;
                    case "ImportCheck": result = await ImportHealthService.ApplyAsync(new() { Action = "check" }); break;
                    case "ImportIncludeSpecials":
                    case "ImportExcludeSpecials":
                        result = await ImportHealthService.ApplyAsync(new() { Action = cmd == "ImportIncludeSpecials" ? "include_specials" : "exclude_specials", Identity = itemId }); break;
                    case "ImportNext": _importOffset += 25; break;

                }
                await LoadImportsAsync();
                if (result != null)
                {
                    using var response = JsonDocument.Parse(JsonSerializer.Serialize(result));
                    var outcome = response.RootElement.GetProperty("Status").GetString();
                    UI.ImportStatus.StatusText += " · " + (outcome switch
                    {
                        "observe_first" => "Choose Check only and let Marvin finish a pass first.",
                        "enable_recovery_first" => "Turn on Repair & refresh first.",
                        "already_running" => "Already engaged. The original timer still applies.",
                        "queued_or_deferred" => "Request saved. Marvin will pick it up.",
                        _ => "Couldn't apply that change. Refresh status and try again.",
                    });
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
                var now = DateTimeOffset.UtcNow;
                var mode = Plugin.Instance.Configuration.ImportRecoveryMode;
                var speed = ImportWorkBudget.For(Plugin.Instance.Configuration, now);
                UI.ImportStatus.StatusText = mode switch
                {
                    ImportMode.Repair => "Repair & refresh",
                    ImportMode.Observe => "Check only",
                    _ => "Classic importer",
                };
                UI.ImprobabilityDrive.Clear();
                UI.ImprobabilityDrive.Add(new GenericListItem
                {
                    PrimaryText = "Infinite Improbability Drive",
                    SecondaryText = speed.IsCatchUp
                        ? $"Engaged. Normality returns {When(speed.CatchUpUntil, now)}. Switch off whenever you like."
                        : "Normality has been restored. Engage for a faster refresh. Switches itself off after seven days.",
                    Icon = IconNames.play_arrow,
                    IconMode = ItemListIconMode.SmallRegular,
                    Toggle = new ToggleButtonItem
                    {
                        Caption = "Engaged",
                        IsChecked = speed.IsCatchUp,
                        CommandId = speed.IsCatchUp ? "ImportNormalSpeed" : "ImportCatchUp",
                    },
                });
                var lastRun = db.GetMetadata("import_last_run");
                if (lastRun == null)
                {
                    UI.MarvinStatus.StatusText = "No report yet. Run Marvin to check the library.";
                    UI.MarvinStatus.Status = ItemStatus.None;
                    return;
                }
                UI.ImportStatus.StatusText += $" · {await db.GetRecentImportAttemptsAsync(now):N0} of {speed.AttemptsPerDay:N0} stream checks used in the last 24 hours";
                using (var report = JsonDocument.Parse(lastRun))
                {
                    var run = report.RootElement;
                    var finished = run.GetProperty("FinishedAt").GetDateTimeOffset();
                    var summary = $"{When(finished, now)} · {run.GetProperty("Attempts").GetInt32()} stream checks";
                    if (run.TryGetProperty("Published", out var published)) summary += $" · {published.GetInt32()} items refreshed";
                    var status = run.GetProperty("Status").GetString();
                    UI.MarvinStatus.StatusText = summary + (status switch
                    {
                        "budget_deferred" => " · More next pass",
                        "cancelled" => " · Stopped early",
                        "partial" => " · Some items need another check",
                        _ => "",
                    });
                    UI.MarvinStatus.Status = status == "partial" ? ItemStatus.Warning : ItemStatus.None;
                }
                var page = await db.GetImportCoveragePageAsync(_importOffset, 25);
                if (page.Count == 0 && _importOffset > 0)
                { _importOffset = 0; page = await db.GetImportCoveragePageAsync(0, 25); }
                foreach (var title in page)
                {
                    var expected = title.Items.Count(x => x.Eligible);
                    var indexed = title.Items.Count(x => x.Eligible && x.State == "indexed");
                    var detail = (expected > 0 ? $"{indexed}/{expected} released items in Emby" : "No released items confirmed") + $" · checked {When(title.CheckedAt, now)}";
                    if (title.Exclusion.Length > 0) detail += " · " + Explain(title.Exclusion);
                    else if (title.SnapshotStatus != "success") detail += " · " + Explain(title.SnapshotStatus);
                    if (title.ProviderStatus is "provider_check_unavailable" or "numbering_conflict")
                        detail += " · " + Explain(title.ProviderStatus);
                    UI.ImportItems.Add(new GenericListItem { PrimaryText = title.Title,
                        SecondaryText = detail,
                        Icon = IconNames.info, IconMode = ItemListIconMode.SmallRegular,
                        Button1 = new ButtonItem(title.IncludeSpecials ? "Skip specials" : "Include released specials")
                        { Data1 = title.Identity, CommandId = title.IncludeSpecials ? "ImportExcludeSpecials" : "ImportIncludeSpecials" } });
                    foreach (var episode in title.Items.Where(x => x.State != "indexed").Take(50))
                    {
                        var reason = episode.State == "excluded" ? Explain(episode.Eligibility) : Explain(episode.State);
                        var more = episode.Failure.Length > 0 ? Explain(episode.Failure) : "";
                        if (episode.NextAttempt.HasValue) more += (more.Length > 0 ? " · " : "") + (episode.NextAttempt > now ? "Retry " + When(episode.NextAttempt, now) : "Retry due");
                        UI.ImportItems.Add(new GenericListItem
                        {
                            PrimaryText = (episode.Season.HasValue ? $"S{episode.Season:D2}E{episode.Episode:D2}" : "Movie") + " · " + reason,
                            SecondaryText = more,
                        });
                    }
                }
            }
            catch
            {
                UI.ImportStatus.StatusText = "Couldn't load the library checks. Try Refresh status.";
                UI.ImportStatus.Status = ItemStatus.Warning;
            }
            finally { RaiseUIViewInfoChanged(); }
        }

        private static string When(DateTimeOffset? value, DateTimeOffset now)
        {
            if (!value.HasValue) return "not yet";
            var future = value > now;
            var span = (value.Value - now).Duration();
            if (span.TotalMinutes < 1) return future ? "in less than a minute" : "just now";
            var count = span.TotalDays >= 1 ? (int)span.TotalDays : span.TotalHours >= 1 ? (int)span.TotalHours : (int)span.TotalMinutes;
            var unit = span.TotalDays >= 1 ? "day" : span.TotalHours >= 1 ? "hour" : "minute";
            var duration = $"{count} {unit}{(count == 1 ? "" : "s")}";
            return future ? "in " + duration : duration + " ago";
        }

        private static string Explain(string value) => value switch
        {
            "missing" => "Missing from the library",
            "retrying" => "Waiting to retry",
            "awaiting_indexing" => "Waiting for Emby to scan",
            "resolving" => "Looking for streams",
            "indexing_attention" => "Emby hasn't picked this up. Check the library scan.",
            "index_mismatch" or "identity_conflict" or "numbering_conflict" => "Title or episode numbers don't match. Needs a check.",
            "observation_unavailable" => "Couldn't check this item",
            "unknown_date" => "Release date unknown",
            "future" => "Not released yet",
            "special_excluded" => "Specials skipped",
            "upstream_removed" => "No longer in the source's episode list",
            "owned_series_or_movie" => "Using your local copy",
            "not_authorized" => "No longer scheduled for repair",
            "stale_or_unavailable" => "Couldn't refresh the episode list",
            "provider_check_unavailable" => "Emby's episode metadata is unavailable",
            "source_unavailable" => "No streams matched your settings",
            "transport_failure" => "Couldn't reach the source",
            "provider_configuration" => "Check your provider settings, then retry",
            _ => "Needs another check",
        };

        // ═══════════════════════════════════════════════════════════════
        // Commands
        // ═══════════════════════════════════════════════════════════════

        private void RunMarvinNow()
        {
            UI.MarvinStatus.StatusText = "Asking Marvin to run…";
            UI.MarvinStatus.Status = ItemStatus.InProgress;
            RaiseUIViewInfoChanged();

            try
            {
                Plugin.Instance.TriggerBackgroundSync();
                UI.MarvinStatus.StatusText = "Request sent. Refresh status to see his next report.";
                UI.MarvinStatus.Status = ItemStatus.None;
            }
            catch (Exception)
            {
                UI.MarvinStatus.StatusText = "Couldn't start Marvin. Try again or check the server log.";
                UI.MarvinStatus.Status = ItemStatus.Failed;
            }

            RaiseUIViewInfoChanged();
        }

    }
}
