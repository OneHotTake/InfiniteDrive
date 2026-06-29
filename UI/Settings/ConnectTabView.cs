using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using InfiniteDrive.Services;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI.Settings
{
    public class ConnectTabView : PluginPageView
    {
        private static readonly Regex UuidPattern = new Regex(
            @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public ConnectTabView(string pluginId, ConnectUI ui) : base(pluginId)
        {
            ContentData = ui;
            LoadUrlInfo();
        }

        private ConnectUI UI => (ConnectUI)ContentData;

        public override bool IsCommandAllowed(string commandKey) => true;

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var cmd = commandId;
            if (string.IsNullOrEmpty(cmd) && !string.IsNullOrEmpty(data))
                cmd = data.Split(':')[0];

            switch (cmd)
            {
                case ConnectUI.TestPrimaryCommand:
                    await TestSingleAsync("Primary manifest", UI.PrimaryManifestUrl).ConfigureAwait(false);
                    return this;

                case ConnectUI.TestSecondaryCommand:
                    if (string.IsNullOrWhiteSpace(UI.SecondaryManifestUrl))
                        UpdateResult("Secondary manifest: Not configured", ItemStatus.None);
                    else
                        await TestSingleAsync("Secondary manifest", UI.SecondaryManifestUrl).ConfigureAwait(false);
                    return this;

                case ConnectUI.PreviewRecommendedCommand:
                    await ApplyRecommendedAsync(dryRun: true).ConfigureAwait(false);
                    return this;

                case ConnectUI.ApplyRecommendedCommand:
                    await ApplyRecommendedAsync(dryRun: false).ConfigureAwait(false);
                    return this;

            }

            return await base.RunCommand(itemId, commandId, data);
        }

        // ── Single test → single result ────────────────────────────────────

        private async Task TestSingleAsync(string label, string url)
        {
            UpdateResult($"{label}: Testing...", ItemStatus.InProgress);

            if (string.IsNullOrWhiteSpace(url))
            {
                UpdateResult($"{label}: Not configured", ItemStatus.Unavailable);
                return;
            }

            try
            {
                // Read-only diagnostic probe: manifest census + stream-signal tiers.
                var v = await ManifestUrlParser.ValidateManifestUrlAsync(url).ConfigureAwait(false);
                if (!v.IsValid)
                {
                    UpdateResult($"{label}: FAILED — {v.ErrorMessage}", ItemStatus.Failed);
                    return;
                }

                var parts = new List<string>
                {
                    $"OK — {v.AddonName ?? "AIOStreams"} v{v.AddonVersion ?? "?"}",
                    v.HasStreamResource ? "streams ✓" : "no stream resource ✗",
                };

                // Catalog census — catch the "advertises catalog resource but empty" trap.
                parts.Add(v.CatalogCount == 0
                    ? "0 catalogs (browse via lists, or enable catalogs in AIOStreams)"
                    : $"{v.BrowsableCatalogCount} browsable / {v.SearchOnlyCatalogCount} search-only catalogs");

                // Stream-signal probe (read-only) — what quality detection to expect.
                var probe = await ManifestUrlParser.ProbeStreamSignalsAsync(url).ConfigureAwait(false);
                if (probe.Ok && probe.StreamCount > 0)
                    parts.Add($"quality: filename {(probe.HasFilename ? "✓" : "✗")}, size {(probe.HasVideoSize ? "✓" : "✗")}");

                // Warn (not fail) when something will degrade quality/browse, but streaming still works.
                var status = (!v.HasStreamResource || (probe.Ok && probe.StreamCount > 0 && !probe.HasFilename))
                    ? ItemStatus.Warning : ItemStatus.Succeeded;

                UpdateResult($"{label}: " + string.Join(" · ", parts), status);
            }
            catch (Exception ex)
            {
                UpdateResult($"{label}: FAILED — {ex.Message}", ItemStatus.Failed);
            }
        }

        // ── Recommended formatter & sort (opt-in, formatter+sort ONLY) ──────

        private async Task ApplyRecommendedAsync(bool dryRun)
        {
            UI.RecommendedResult.StatusText = dryRun ? "Previewing…" : "Applying…";
            UI.RecommendedResult.Status = ItemStatus.InProgress;
            RaiseUIViewInfoChanged();

            if (string.IsNullOrWhiteSpace(UI.PrimaryManifestUrl))
            {
                SetRecommended("Add a primary manifest URL first.", ItemStatus.Unavailable);
                return;
            }
            if (string.IsNullOrWhiteSpace(UI.PrimaryManifestPassword)
                && string.IsNullOrWhiteSpace(UI.SecondaryManifestPassword))
            {
                SetRecommended("Enter the password for at least one instance, then Preview.", ItemStatus.Unavailable);
                return;
            }

            // Each instance authenticates independently, so run them as separate targets
            // and aggregate the results. A configured-but-no-password target is skipped
            // with a clear note rather than failing the whole run.
            var targets = new List<(string Label, string Url, string Password)>
            {
                ("Primary", UI.PrimaryManifestUrl, UI.PrimaryManifestPassword),
            };
            if (!string.IsNullOrWhiteSpace(UI.SecondaryManifestUrl))
                targets.Add(("Backup", UI.SecondaryManifestUrl, UI.SecondaryManifestPassword));

            var blocks = new List<string>();
            var anyFailed = false;
            var anyChanged = false;

            foreach (var (label, url, password) in targets)
            {
                if (string.IsNullOrWhiteSpace(password))
                {
                    blocks.Add($"{label}: skipped — no password entered.");
                    continue;
                }

                try
                {
                    var r = await AioStreamsConfigClient.ApplyRecommendedAsync(
                        url, password, dryRun).ConfigureAwait(false);

                    if (!r.Ok) anyFailed = true;
                    if (r.Changed) anyChanged = true;

                    var block = $"{label}: {r.Message}";
                    if (r.Ok && !string.IsNullOrEmpty(r.Diff) && (dryRun || r.Changed))
                        block += "\n" + r.Diff;
                    blocks.Add(block);
                }
                catch (Exception ex)
                {
                    anyFailed = true;
                    blocks.Add($"{label}: Failed — {ex.Message}");
                }
            }

            var status = anyFailed ? ItemStatus.Failed
                : (dryRun || anyChanged) ? ItemStatus.Warning : ItemStatus.Succeeded;
            SetRecommended(string.Join("\n\n", blocks), status);
        }

        private void SetRecommended(string text, ItemStatus status)
        {
            UI.RecommendedResult.StatusText = text;
            UI.RecommendedResult.Status = status;
            RaiseUIViewInfoChanged();
        }

        // ── URL info ───────────────────────────────────────────────────────

        private void LoadUrlInfo()
        {
            var cfg = Plugin.Instance.Configuration;
            PopulateUrlInfo(cfg.PrimaryManifestUrl, UI.PrimaryServerUrl, UI.PrimaryUserId);
            PopulateUrlInfo(cfg.SecondaryManifestUrl, UI.SecondaryServerUrl, UI.SecondaryUserId);
        }

        private void PopulateUrlInfo(string url, Emby.Web.GenericEdit.Elements.LabelItem serverLabel, StatusItem userIdItem)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                serverLabel.Text = "Not configured";
                serverLabel.HyperLink = null;
                userIdItem.StatusText = "—";
                userIdItem.Status = ItemStatus.None;
                return;
            }

            try
            {
                var uri = new Uri(url);
                var baseUrl = $"{uri.Scheme}://{uri.Host}/";
                serverLabel.Text = baseUrl;
                serverLabel.HyperLink = baseUrl;

                var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                var userId = segments.FirstOrDefault(s => UuidPattern.IsMatch(s));
                if (!string.IsNullOrEmpty(userId))
                {
                    userIdItem.StatusText = userId;
                    userIdItem.Status = ItemStatus.Succeeded;
                }
                else
                {
                    userIdItem.StatusText = "Not found in URL";
                    userIdItem.Status = ItemStatus.Warning;
                }
            }
            catch
            {
                serverLabel.Text = "Invalid URL";
                serverLabel.HyperLink = null;
                userIdItem.StatusText = "—";
                userIdItem.Status = ItemStatus.None;
            }
        }

        // ── One writer ─────────────────────────────────────────────────────

        private void UpdateResult(string text, ItemStatus status)
        {
            UI.SetupTestResult.StatusText = text;
            UI.SetupTestResult.Status = status;
            RaiseUIViewInfoChanged();
        }

        // ── Save ───────────────────────────────────────────────────────────

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            cfg.PrimaryManifestUrl = UI.PrimaryManifestUrl ?? string.Empty;
            cfg.SecondaryManifestUrl = UI.SecondaryManifestUrl ?? string.Empty;
            // The AIOStreams password is deliberately NOT persisted — it's a one-time,
            // per-environment secret used only for the Preview/Apply action in-memory.
            cfg.EnableBackupAioStreams = !string.IsNullOrWhiteSpace(UI.SecondaryManifestUrl);
            Plugin.Instance.SaveConfiguration();
            Plugin.Instance.TriggerBackgroundSync();
            LoadUrlInfo();
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
