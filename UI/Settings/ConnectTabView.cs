using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
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
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var resp = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
                var ver = root.TryGetProperty("version", out var v) ? v.GetString() : null;
                var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;

                if (id != null)
                    UpdateResult($"{label}: OK — {name ?? "AIOStreams"} v{ver ?? "?"}", ItemStatus.Succeeded);
                else
                    UpdateResult($"{label}: Response received but no plugin ID — check URL", ItemStatus.Warning);
            }
            catch (Exception ex)
            {
                UpdateResult($"{label}: FAILED — {ex.Message}", ItemStatus.Failed);
            }
        }

        // ── URL info ───────────────────────────────────────────────────────

        private void LoadUrlInfo()
        {
            var cfg = Plugin.Instance.Configuration;
            PopulateUrlInfo(cfg.PrimaryManifestUrl, UI.PrimaryServerUrl, UI.PrimaryUserId);
            SetDashboardLink(UI.PrimaryDashboardLink, UI.PrimaryServerUrl);

            PopulateUrlInfo(cfg.SecondaryManifestUrl, UI.SecondaryServerUrl, UI.SecondaryUserId);
            SetDashboardLink(UI.SecondaryDashboardLink, UI.SecondaryServerUrl);
        }

        private static void SetDashboardLink(Emby.Web.GenericEdit.Elements.LabelItem label, StatusItem serverItem)
        {
            if (serverItem.Status == ItemStatus.Succeeded && !string.IsNullOrWhiteSpace(serverItem.StatusText))
            {
                label.Text = "Open dashboard";
                label.HyperLink = serverItem.StatusText;
            }
            else
            {
                label.Text = string.Empty;
                label.HyperLink = null;
            }
        }

        private void PopulateUrlInfo(string url, StatusItem serverItem, StatusItem userIdItem)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                serverItem.StatusText = "Not configured";
                serverItem.Status = ItemStatus.None;
                userIdItem.StatusText = "—";
                userIdItem.Status = ItemStatus.None;
                return;
            }

            try
            {
                var uri = new Uri(url);
                serverItem.StatusText = $"{uri.Scheme}://{uri.Host}/";
                serverItem.Status = ItemStatus.Succeeded;

                // Find the UUID-shaped path segment regardless of position (some servers
                // prefix with /stremio/ or similar before the user ID)
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
                serverItem.StatusText = "Invalid URL";
                serverItem.Status = ItemStatus.Warning;
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
            cfg.EnableBackupAioStreams = !string.IsNullOrWhiteSpace(UI.SecondaryManifestUrl);
            Plugin.Instance.SaveConfiguration();
            Plugin.Instance.TriggerBackgroundSync();
            LoadUrlInfo();
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
