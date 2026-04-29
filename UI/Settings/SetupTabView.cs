using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI.Settings
{
    public class SetupTabView : PluginPageView
    {
        private static readonly JsonSerializerOptions _jsonOpts =
            new() { PropertyNameCaseInsensitive = true };

        public SetupTabView(string pluginId, SetupUI ui) : base(pluginId)
        {
            ContentData = ui;

            // Smart initial status — reflect config completeness
            if (!string.IsNullOrWhiteSpace(ui.PrimaryManifestUrl))
            {
                ui.SetupStatus.StatusText = "Ready — Primary manifest configured";
                ui.SetupStatus.Status = ItemStatus.Succeeded;
                ui.PrimaryStatus.StatusText = "Not tested";
                ui.PrimaryStatus.Status = ItemStatus.None;
            }
            if (!string.IsNullOrWhiteSpace(ui.SecondaryManifestUrl))
            {
                ui.SecondaryStatus.StatusText = "Not tested";
                ui.SecondaryStatus.Status = ItemStatus.None;
            }
        }

        private SetupUI UI => (SetupUI)ContentData;

        public override bool IsCommandAllowed(string commandKey) => true;

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var cmd = commandId;
            if (string.IsNullOrEmpty(cmd) && !string.IsNullOrEmpty(data))
                cmd = data.Split(':')[0];

            switch (cmd)
            {
                case SetupUI.TestPrimaryCommand:
                    await TestConnectionAsync(UI.PrimaryManifestUrl, isPrimary: true).ConfigureAwait(false);
                    return this;

                case SetupUI.TestSecondaryCommand:
                    await TestConnectionAsync(UI.SecondaryManifestUrl, isPrimary: false).ConfigureAwait(false);
                    return this;

                case SetupUI.RunSetupTestCommand:
                    await RunFullSetupTestAsync().ConfigureAwait(false);
                    return this;
            }

            return await base.RunCommand(itemId, commandId, data);
        }

        private async Task TestConnectionAsync(string url, bool isPrimary)
        {
            var status = isPrimary ? UI.PrimaryStatus : UI.SecondaryStatus;

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

        private async Task RunFullSetupTestAsync()
        {
            UI.SetupTestResult.StatusText = "Running tests...";
            UI.SetupTestResult.Status = ItemStatus.InProgress;
            RaiseUIViewInfoChanged();

            var sb = new StringBuilder();
            var allOk = true;

            // Test primary manifest
            if (string.IsNullOrWhiteSpace(UI.PrimaryManifestUrl))
            {
                sb.AppendLine("Primary manifest URL: Not configured");
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

            // Test secondary manifest if configured
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
                }
            }

            // Check Emby URL
            if (string.IsNullOrWhiteSpace(UI.EmbyBaseUrl) ||
                UI.EmbyBaseUrl.StartsWith("http://127.0.0.1") ||
                UI.EmbyBaseUrl.StartsWith("http://localhost"))
            {
                sb.AppendLine("Emby URL: Not externally reachable — update External URL");
                allOk = false;
            }
            else
            {
                sb.AppendLine("Emby URL: Configured");
            }

            // Check library paths
            if (string.IsNullOrWhiteSpace(UI.MoviesLibraryPath))
            {
                sb.AppendLine("Movies path: Not set");
                allOk = false;
            }
            else
            {
                sb.AppendLine("Movies path: Configured");
            }

            if (string.IsNullOrWhiteSpace(UI.SeriesLibraryPath))
            {
                sb.AppendLine("Series path: Not set");
                allOk = false;
            }
            else
            {
                sb.AppendLine("Series path: Configured");
            }

            UI.SetupTestResult.StatusText = sb.ToString().TrimEnd();
            UI.SetupTestResult.Status = allOk ? ItemStatus.Succeeded : ItemStatus.Warning;
            RaiseUIViewInfoChanged();
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            cfg.PrimaryManifestUrl = UI.PrimaryManifestUrl ?? string.Empty;
            cfg.SecondaryManifestUrl = UI.SecondaryManifestUrl ?? string.Empty;
            cfg.EnableBackupAioStreams = !string.IsNullOrWhiteSpace(UI.SecondaryManifestUrl);
            cfg.EmbyBaseUrl = UI.EmbyBaseUrl ?? string.Empty;
            cfg.MoviesLibraryName = UI.MoviesLibraryName ?? string.Empty;
            cfg.MoviesLibraryPath = UI.MoviesLibraryPath ?? string.Empty;
            cfg.SeriesLibraryName = UI.SeriesLibraryName ?? string.Empty;
            cfg.SeriesLibraryPath = UI.SeriesLibraryPath ?? string.Empty;
            cfg.AnimeLibraryName = UI.AnimeLibraryName ?? string.Empty;
            cfg.AnimeLibraryPath = UI.AnimeLibraryPath ?? string.Empty;
            cfg.MetadataLanguage = UI.MetadataLanguage ?? "en";
            cfg.CertificationCountry = UI.CertificationCountry ?? "US";
            cfg.DefaultSubtitleLanguage = UI.DefaultSubtitleLanguage ?? "en";
            cfg.DefaultQualityTier = UI.DefaultQualityTier ?? "1080p (any)";
            Plugin.Instance.SaveConfiguration();
            Plugin.Instance.TriggerBackgroundSync();
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
