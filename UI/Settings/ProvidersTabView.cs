using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI.Settings
{
    public class ProvidersTabView : PluginPageView
    {
        private static readonly JsonSerializerOptions _jsonOpts =
            new() { PropertyNameCaseInsensitive = true };

        public ProvidersTabView(string pluginId, ProvidersUI ui) : base(pluginId)
        {
            ContentData = ui;

            // Smart initial status — reflect whether URLs are actually configured
            if (!string.IsNullOrWhiteSpace(ui.PrimaryManifestUrl))
            {
                ui.PrimaryStatus.StatusText = "Not tested";
                ui.PrimaryStatus.Status = ItemStatus.None;
            }
            if (!string.IsNullOrWhiteSpace(ui.SecondaryManifestUrl))
            {
                ui.SecondaryStatus.StatusText = "Not tested";
                ui.SecondaryStatus.Status = ItemStatus.None;
            }
            else
            {
                ui.SecondaryStatus.StatusText = "Not configured";
                ui.SecondaryStatus.Status = ItemStatus.Unavailable;
            }
        }

        private ProvidersUI UI => (ProvidersUI)ContentData;

        public override bool IsCommandAllowed(string commandKey)
        {
            return true;
        }

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            // The framework may pass the button Data1 via commandId OR data
            var cmd = commandId;
            if (string.IsNullOrEmpty(cmd) && !string.IsNullOrEmpty(data))
                cmd = data.Split(':')[0];

            switch (cmd)
            {
                case ProvidersUI.TestPrimaryCommand:
                    await TestConnectionAsync(UI.PrimaryManifestUrl, UI, isPrimary: true);
                    return this;

                case ProvidersUI.TestSecondaryCommand:
                    await TestConnectionAsync(UI.SecondaryManifestUrl, UI, isPrimary: false);
                    return this;
            }

            return await base.RunCommand(itemId, commandId, data);
        }

        private async Task TestConnectionAsync(string url, ProvidersUI ui, bool isPrimary)
        {
            var status = isPrimary ? ui.PrimaryStatus : ui.SecondaryStatus;

            if (string.IsNullOrWhiteSpace(url))
            {
                status.StatusText = "No URL configured";
                status.Status = ItemStatus.Unavailable;
                RaiseUIViewInfoChanged();
                return;
            }

            status.StatusText = "Testing...";
            status.Status = ItemStatus.InProgress;
            RaiseUIViewInfoChanged();

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
                {
                    status.StatusText = $"Connected: {name ?? "AIOStreams"} v{ver ?? "?"}";
                    status.Status = ItemStatus.Succeeded;
                }
                else
                {
                    status.StatusText = "Response received but no plugin ID — check URL";
                    status.Status = ItemStatus.Warning;
                }
            }
            catch (Exception ex)
            {
                status.StatusText = $"Failed: {ex.Message}";
                status.Status = ItemStatus.Failed;
            }

            RaiseUIViewInfoChanged();
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            SettingsController.SaveProviders(UI, cfg);
            Plugin.Instance.SaveConfiguration();
            Plugin.Instance.TriggerBackgroundSync();
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
