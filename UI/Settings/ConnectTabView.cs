using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI.Settings
{
    public class ConnectTabView : PluginPageView
    {
        public ConnectTabView(string pluginId, ConnectUI ui) : base(pluginId)
        {
            ContentData = ui;
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
                    {
                        UpdateResult("Secondary manifest: Not configured", ItemStatus.None);
                    }
                    else
                    {
                        await TestSingleAsync("Secondary manifest", UI.SecondaryManifestUrl).ConfigureAwait(false);
                    }
                    return this;

                case ConnectUI.RunSetupTestCommand:
                    await RunFullSetupTestAsync().ConfigureAwait(false);
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

        // ── Full test → single result ──────────────────────────────────────

        private async Task RunFullSetupTestAsync()
        {
            UpdateResult("Running tests...", ItemStatus.InProgress);

            var sb = new StringBuilder();
            var allOk = true;

            // Primary
            if (string.IsNullOrWhiteSpace(UI.PrimaryManifestUrl))
            {
                sb.AppendLine("Primary manifest: Not configured");
                allOk = false;
            }
            else
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var resp = await http.GetAsync(UI.PrimaryManifestUrl, cts.Token).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    sb.AppendLine("Primary manifest: OK");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Primary manifest: FAILED — {ex.Message}");
                    allOk = false;
                }
            }

            // Secondary (only if set)
            if (!string.IsNullOrWhiteSpace(UI.SecondaryManifestUrl))
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var resp = await http.GetAsync(UI.SecondaryManifestUrl, cts.Token).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    sb.AppendLine("Secondary manifest: OK");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Secondary manifest: FAILED — {ex.Message}");
                    allOk = false;
                }
            }

            UpdateResult(sb.ToString().TrimEnd(), allOk ? ItemStatus.Succeeded : ItemStatus.Warning);
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
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
