using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Emby.Web.GenericEdit;
using InfiniteDrive.Services;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI
{
    public class InfiniteDriveController : IHasUIPages, IHasTabbedUIPages
    {
        private IReadOnlyCollection<IPluginUIPageController>? _uiPageControllers;
        private IReadOnlyList<IPluginUIPageController>? _tabPageControllers;
        private static readonly HttpClient _sharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers =>
            _uiPageControllers ??= BuildControllers();

        public IReadOnlyList<IPluginUIPageController> TabPageControllers =>
            _tabPageControllers ??= BuildTabControllers();

        private List<IPluginUIPageController> BuildTabControllers()
        {
            var list = new List<IPluginUIPageController>();

            // ── Health tab (read-only, server-side refresh) ──
            list.Add(new TabPageController("InfiniteDrive_Health", "Health", CreateHealthView));

            // ── Providers tab ──
            list.Add(new TabPageController("InfiniteDrive_Providers", "Providers", () =>
            {
                var cfg = Plugin.Instance.Configuration;
                var model = new ProvidersUI(cfg);
                return new InfiniteDrivePageView(model, content =>
                {
                    ((ProvidersUI)content).ApplyTo(cfg);
                    Plugin.Instance.SaveConfiguration();
                }, async commandId =>
                {
                    if (commandId == "test-connection" && !string.IsNullOrEmpty(cfg.PrimaryManifestUrl))
                    {
                        try
                        {
                            using var http = new HttpClient();
                            http.Timeout = TimeSpan.FromSeconds(15);
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            var resp = await http.GetAsync(cfg.PrimaryManifestUrl);
                            sw.Stop();
                            return resp.IsSuccessStatusCode
                                ? $"Connected ({sw.ElapsedMilliseconds} ms)"
                                : $"HTTP {(int)resp.StatusCode}";
                        }
                        catch (Exception ex)
                        {
                            return $"Error: {ex.Message}";
                        }
                    }
                    return null;
                });
            }));

            // ── Libraries tab ──
            list.Add(new TabPageController("InfiniteDrive_Libraries", "Libraries", () =>
            {
                var cfg = Plugin.Instance.Configuration;
                var model = new LibrariesUI(cfg);
                return new InfiniteDrivePageView(model, content =>
                {
                    ((LibrariesUI)content).ApplyTo(cfg);
                    Plugin.Instance.SaveConfiguration();
                }, async commandId =>
                {
                    if (commandId == "provision-libraries")
                    {
                        return await TriggerInternal("provision_libraries");
                    }
                    return null;
                });
            }));

            // ── Sources tab ──
            list.Add(new TabPageController("InfiniteDrive_Sources", "Sources", () =>
            {
                var cfg = Plugin.Instance.Configuration;
                var model = new SourcesUI(cfg);
                return new InfiniteDrivePageView(model, content =>
                {
                    ((SourcesUI)content).ApplyTo(cfg);
                    Plugin.Instance.SaveConfiguration();
                }, async commandId =>
                {
                    if (commandId == "trigger-sync")
                    {
                        return await TriggerInternal("catalog_sync");
                    }
                    return null;
                });
            }));

            // ── Security tab ──
            list.Add(new TabPageController("InfiniteDrive_Security", "Security", () =>
            {
                var cfg = Plugin.Instance.Configuration;
                var model = new SecurityUI(cfg);
                return new InfiniteDrivePageView(model, content =>
                {
                    ((SecurityUI)content).ApplyTo(cfg);
                    Plugin.Instance.SaveConfiguration();
                }, commandId =>
                {
                    if (commandId == "rotate")
                    {
                        Plugin.Instance.Configuration.PluginSecret = PlaybackTokenService.GenerateSecret();
                        Plugin.Instance.Configuration.PluginSecretRotatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        Plugin.Instance.SaveConfiguration();
                    }
                    return Task.FromResult<string?>(null);
                });
            }));

            // ── Parental tab ──
            list.Add(new TabPageController("InfiniteDrive_Parental", "Parental Controls", () =>
            {
                var cfg = Plugin.Instance.Configuration;
                var model = new ParentalUI(cfg);
                return new InfiniteDrivePageView(model, content =>
                {
                    ((ParentalUI)content).ApplyTo(cfg);
                    Plugin.Instance.SaveConfiguration();
                }, async commandId =>
                {
                    if (commandId == "test-tmdb" && !string.IsNullOrEmpty(cfg.TmdbApiKey))
                    {
                        try
                        {
                            using var http = new HttpClient();
                            http.Timeout = TimeSpan.FromSeconds(10);
                            var resp = await http.GetAsync(
                                $"https://api.themoviedb.org/3/movie/550?api_key={cfg.TmdbApiKey}");
                            return resp.IsSuccessStatusCode ? "TMDB key is valid" : $"TMDB returned {(int)resp.StatusCode}";
                        }
                        catch (Exception ex)
                        {
                            return $"Error: {ex.Message}";
                        }
                    }
                    return null;
                });
            }));

            // ── Repair tab ──
            list.Add(new TabPageController("InfiniteDrive_Repair", "Repair", () =>
            {
                var cfg = Plugin.Instance.Configuration;
                var model = new RepairUI(cfg);
                return new InfiniteDrivePageView(model, content =>
                {
                    ((RepairUI)content).ApplyTo(cfg);
                    Plugin.Instance.SaveConfiguration();
                }, async commandId =>
                {
                    return commandId switch
                    {
                        "trigger-sync"      => await TriggerInternal("catalog_sync"),
                        "summon-marvin"     => await TriggerInternal("marvin"),
                        "provision-libraries" => await TriggerInternal("provision_libraries"),
                        "create-directories" => await TriggerInternal("create_directories"),
                        "purge-catalog"     => await TriggerInternal("purge_catalog"),
                        "nuclear-reset"     => await TriggerInternal("nuclear_reset"),
                        "clear-profiles"    => await TriggerInternal("clear_client_profiles"),
                        _ => null
                    };
                });
            }));

            return list;
        }

        /// <summary>
        /// Creates the Health view with live data from /InfiniteDrive/Status.
        /// Used both for initial load and server-side refresh.
        /// </summary>
        private static InfiniteDrivePageView CreateHealthView()
        {
            var model = new HealthUI();

            // Populate initial data synchronously (best effort)
            try
            {
                var baseUrl = Plugin.Instance.Configuration.EmbyBaseUrl;
                if (string.IsNullOrEmpty(baseUrl)) baseUrl = "http://127.0.0.1:8096";
                var json = _sharedHttp.GetStringAsync($"{baseUrl}/InfiniteDrive/Status").GetAwaiter().GetResult();
                model.PopulateFromJson(json);
            }
            catch
            {
                model.LastUpdatedLabel = new Emby.Web.GenericEdit.Elements.LabelItem
                {
                    Text = $"Unable to fetch status — server may be starting up"
                };
            }

            return new InfiniteDrivePageView(model, _ => { },
                onCommand: null,
                onRefresh: () => Task.FromResult<IPluginUIView>(CreateHealthView()))
            {
                ShowSave = false
            };
        }

        private static async Task<string?> TriggerInternal(string taskKey)
        {
            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(30);
                var baseUrl = Plugin.Instance.Configuration.EmbyBaseUrl;
                if (string.IsNullOrEmpty(baseUrl)) baseUrl = "http://127.0.0.1:8096";
                var resp = await http.PostAsync($"{baseUrl}/InfiniteDrive/Trigger?task={Uri.EscapeDataString(taskKey)}", null);
                return resp.IsSuccessStatusCode ? $"{taskKey} triggered" : $"HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private List<IPluginUIPageController> BuildControllers()
        {
            return BuildTabControllers();
        }
    }
}
