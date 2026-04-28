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
        }

        private ProvidersUI UI => (ProvidersUI)ContentData;

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var effectiveCommand = data?.Split(':')[0] ?? commandId;
            switch (effectiveCommand)
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
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
