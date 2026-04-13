using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;

namespace InfiniteDrive.UI
{
    public class HealthUI : EditableOptionsBase
    {
        public override string EditorTitle => "Health";

        // ── Connection ──

        [DisplayName("AIOStreams")]
        public StatusItem AioStreamsStatus { get; set; } = new()
        {
            Caption = "AIOStreams",
            StatusText = "Not tested"
        };

        [DisplayName("Addon")]
        public LabelItem AddonInfo { get; set; } = new()
        {
            Text = "Not connected",
            IsVisible = true
        };

        [DisplayName("Configured")]
        public StatusItem ConfiguredStatus { get; set; } = new()
        {
            Caption = "Configuration",
            StatusText = "Unknown"
        };

        [DisplayName("Manifest")]
        public StatusItem ManifestStatusItem { get; set; } = new()
        {
            Caption = "Manifest",
            StatusText = "Unknown"
        };

        // ── Item Counts ──

        [DisplayName("Catalog Items")]
        public LabelItem CatalogCount { get; set; } = new() { Text = "—" };

        [DisplayName("Library Items")]
        public LabelItem LibraryCount { get; set; } = new() { Text = "—" };

        [DisplayName("STRM Files")]
        public LabelItem StrmCount { get; set; } = new() { Text = "—" };

        // ── Resolution Cache ──

        [DisplayName("Coverage")]
        public ProgressItem CoverageProgress { get; set; } = new()
        {
            CurrentValue = 0,
            MaxValue = 100,
            ProgressText = "0% (0/0 items)"
        };

        [DisplayName("Cache Stats")]
        public LabelItem CacheStatsLabel { get; set; } = new()
        {
            Text = "Valid: — | Stale: — | Failed: —"
        };

        // ── API Budget ──

        [DisplayName("API Budget")]
        public ProgressItem ApiBudgetProgress { get; set; } = new()
        {
            CurrentValue = 0,
            MaxValue = 100,
            ProgressText = "0 / 0 calls today (0%)"
        };

        // ── Item States ──

        [DisplayName("Item States")]
        public LabelItem ItemStatesLabel { get; set; } = new()
        {
            Text = "Catalogued: — | Present: — | Resolved: — | Retired: — | Pinned: — | Orphaned: —"
        };

        [DisplayName("Resurrections")]
        public LabelItem ResurrectionLabel { get; set; } = new() { Text = "—" };

        [DisplayName("Re-adopted")]
        public LabelItem ReadoptedLabel { get; set; } = new() { Text = "—" };

        [DisplayName("Pending Expansion")]
        public LabelItem PendingExpansionLabel { get; set; } = new() { Text = "—" };

        // ── Source Sync ──

        [DisplayName("Source Sync States")]
        public GenericItemList SourceSyncList { get; set; } = new();

        // ── Client Profiles ──

        [DisplayName("Client Profiles")]
        public GenericItemList ClientProfileList { get; set; } = new();

        // ── Recent Plays ──

        [DisplayName("Recent Plays")]
        public GenericItemList RecentPlaysList { get; set; } = new();

        // ── Provider Health ──

        [DisplayName("Providers")]
        public GenericItemList ProviderHealthList { get; set; } = new();

        // ── Cooldown ──

        [DisplayName("Rate Limiting")]
        public StatusItem CooldownStatus { get; set; } = new()
        {
            Caption = "Cooldown",
            StatusText = "None"
        };

        // ── Actions ──

        [DisplayName("Refresh")]
        public ButtonItem RefreshButton => new()
        {
            Caption = "Refresh Status",
            CommandId = "refresh"
        };

        [DisplayName("Last Updated")]
        public LabelItem LastUpdatedLabel { get; set; } = new()
        {
            Text = "Not yet loaded"
        };

        public HealthUI() { }

        /// <summary>
        /// Populates all fields from a StatusResponse JSON payload.
        /// Called by the controller after fetching /InfiniteDrive/Status.
        /// </summary>
        public void PopulateFromJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Connection
                if (root.TryGetProperty("AioStreams", out var aio))
                {
                    var ok = aio.TryGetProperty("Ok", out var okEl) && okEl.GetBoolean();
                    var latency = aio.TryGetProperty("LatencyMs", out var lEl) ? lEl.GetInt32() : -1;
                    AioStreamsStatus = new StatusItem
                    {
                        Caption = "AIOStreams",
                        StatusText = ok
                            ? $"Connected{((latency >= 0) ? $" ({latency} ms)" : "")}"
                            : "Offline"
                    };
                }

                // Addon info
                var addonName = root.TryGetProperty("AioStreamsAddonName", out var an) ? an.GetString() : null;
                var addonVer = root.TryGetProperty("AioStreamsAddonVersion", out var av) ? av.GetString() : null;
                AddonInfo = new LabelItem
                {
                    Text = !string.IsNullOrEmpty(addonName) ? $"{addonName} v{addonVer}" : "Not connected",
                    IsVisible = true
                };

                // Configured
                var configured = root.TryGetProperty("IsConfigured", out var cfgEl) && cfgEl.GetBoolean();
                ConfiguredStatus = new StatusItem
                {
                    Caption = "Configuration",
                    StatusText = configured ? "Complete" : "Not configured"
                };

                // Manifest
                var manifestStatus = root.TryGetProperty("ManifestStatus", out var ms) ? ms.GetString() : null;
                ManifestStatusItem = new StatusItem
                {
                    Caption = "Manifest",
                    StatusText = manifestStatus ?? Plugin.GetManifestStatus()
                };

                // Item counts
                CatalogCount = new LabelItem { Text = GetInt(root, "CatalogItemCount").ToString() };
                LibraryCount = new LabelItem { Text = GetInt(root, "LibraryItemCount").ToString() };
                StrmCount = new LabelItem { Text = GetInt(root, "StrmItemCount").ToString() };

                // Coverage
                if (root.TryGetProperty("Coverage", out var cov))
                {
                    var pct = GetInt(cov, "CoveragePercent");
                    var valid = GetInt(cov, "ValidCached");
                    var total = GetInt(cov, "TotalStrm");
                    CoverageProgress = new ProgressItem
                    {
                        CurrentValue = pct,
                        MaxValue = 100,
                        ProgressText = $"{pct}% ({valid}/{total} items)"
                    };
                    var stale = GetInt(cov, "StaleCached");
                    var uncached = GetInt(cov, "Uncached");
                    CacheStatsLabel = new LabelItem
                    {
                        Text = $"Valid: {valid} | Stale: {stale} | Uncached: {uncached}"
                    };
                }

                // Cache stats (from ResolutionCacheStats)
                if (root.TryGetProperty("Cache", out var cache))
                {
                    var valid = GetInt(cache, "ValidUnexpired");
                    var stale = GetInt(cache, "Stale");
                    var failed = GetInt(cache, "Failed");
                    CacheStatsLabel = new LabelItem
                    {
                        Text = $"Valid: {valid} | Stale: {stale} | Failed: {failed}"
                    };
                }

                // API Budget
                if (root.TryGetProperty("ApiBudget", out var budget))
                {
                    var made = GetInt(budget, "CallsMade");
                    var budge = GetInt(budget, "CallsBudget");
                    var pct = budge > 0 ? made * 100 / budge : 0;
                    ApiBudgetProgress = new ProgressItem
                    {
                        CurrentValue = pct,
                        MaxValue = 100,
                        ProgressText = $"{made} / {budge} calls today ({pct}%)"
                    };
                }

                // Item states
                ItemStatesLabel = new LabelItem
                {
                    Text = $"Catalogued: {GetInt(root, "CataloguedCount")} | " +
                           $"Present: {GetInt(root, "PresentCount")} | " +
                           $"Resolved: {GetInt(root, "ResolvedCount")} | " +
                           $"Retired: {GetInt(root, "RetiredCount")} | " +
                           $"Pinned: {GetInt(root, "PinnedCount")} | " +
                           $"Orphaned: {GetInt(root, "OrphanedCount")}"
                };

                ResurrectionLabel = new LabelItem { Text = GetInt(root, "ResurrectionCount").ToString() };
                ReadoptedLabel = new LabelItem { Text = GetInt(root, "ReadoptedCount").ToString() };
                PendingExpansionLabel = new LabelItem { Text = GetInt(root, "PendingExpansionCount").ToString() };

                // Source sync states
                SourceSyncList = new GenericItemList();
                if (root.TryGetProperty("SyncStates", out var syncs))
                {
                    foreach (var s in syncs.EnumerateArray())
                    {
                        var key = GetString(s, "SourceKey");
                        var status = GetString(s, "Status");
                        var items = GetInt(s, "ItemCount");
                        var failures = GetInt(s, "ConsecutiveFailures");
                        var lastSync = GetString(s, "LastSyncAt");
                        var lastErr = GetString(s, "LastError");

                        SourceSyncList.Add(new GenericListItem
                        {
                            PrimaryText = key,
                            SecondaryText = $"{items} items | Last sync: {FormatTimestamp(lastSync)}{(failures > 0 ? $" | Failures: {failures}" : "")}",
                            Status = status == "ok" ? ItemStatus.Succeeded : status == "warn" ? ItemStatus.Warning : ItemStatus.Failed,
                            Button1 = !string.IsNullOrEmpty(lastErr) ? new ButtonItem
                            {
                                Caption = "Details",
                                Data1 = lastErr.Length > 100 ? lastErr[..100] + "..." : lastErr
                            } : null
                        });
                    }
                }

                // Client profiles
                ClientProfileList = new GenericItemList();
                if (root.TryGetProperty("ClientProfiles", out var clients))
                {
                    foreach (var cl in clients.EnumerateArray())
                    {
                        var ctype = GetString(cl, "ClientType");
                        var mode = cl.TryGetProperty("SupportsRedirect", out var sr) && sr.GetInt32() == 1 ? "redirect" : "proxy";
                        var bitrate = cl.TryGetProperty("MaxSafeBitrate", out var br) && br.ValueKind != JsonValueKind.Null
                            ? br.GetInt32() : 0;
                        var bitrateStr = bitrate >= 1000 ? $"{bitrate / 1000.0:F1} Mbps" : bitrate > 0 ? $"{bitrate} kbps" : "learning";
                        var quality = GetString(cl, "PreferredQuality");
                        var tests = GetInt(cl, "TestCount");

                        ClientProfileList.Add(new GenericListItem
                        {
                            PrimaryText = ctype ?? "Unknown",
                            SecondaryText = $"Mode: {mode} | Bitrate: {bitrateStr} | Quality: {quality} | Tests: {tests}",
                            Status = mode == "redirect" ? ItemStatus.Succeeded : ItemStatus.Warning
                        });
                    }
                }

                // Recent plays
                RecentPlaysList = new GenericItemList();
                if (root.TryGetProperty("RecentPlays", out var plays))
                {
                    foreach (var p in plays.EnumerateArray().Take(10))
                    {
                        var title = GetString(p, "Title") ?? GetString(p, "ImdbId");
                        var season = p.TryGetProperty("Season", out var sn) && sn.ValueKind == JsonValueKind.Number ? sn.GetInt32() : (int?)null;
                        var episode = p.TryGetProperty("Episode", out var ep) && ep.ValueKind == JsonValueKind.Number ? ep.GetInt32() : (int?)null;
                        var epStr = season.HasValue ? $" S{season:D2}E{episode:D2}" : "";
                        var mode = GetString(p, "ResolutionMode");
                        var quality = GetString(p, "QualityServed");
                        var client = GetString(p, "ClientType");
                        var latency = p.TryGetProperty("LatencyMs", out var lm) && lm.ValueKind == JsonValueKind.Number ? lm.GetInt32() : (int?)null;
                        var playedAt = GetString(p, "PlayedAt");

                        RecentPlaysList.Add(new GenericListItem
                        {
                            PrimaryText = $"{title}{epStr}",
                            SecondaryText = $"{mode} | {quality ?? "—"} | {client ?? "—"} | {latency?.ToString() ?? "—"} ms | {FormatTimestamp(playedAt)}",
                            Status = mode == "cached" ? ItemStatus.Succeeded : mode == "failed" ? ItemStatus.Failed : ItemStatus.Warning,
                            HyperLink = $"https://www.imdb.com/title/{GetString(p, "ImdbId")}",
                            HyperLinkTargetExternal = true
                        });
                    }
                }

                // Provider health
                ProviderHealthList = new GenericItemList();
                if (root.TryGetProperty("Providers", out var provs))
                {
                    foreach (var pr in provs.EnumerateArray())
                    {
                        var ok = pr.TryGetProperty("Ok", out var okEl) && okEl.GetBoolean();
                        var displayName = GetString(pr, "DisplayName") ?? "Provider";
                        var latency = pr.TryGetProperty("LatencyMs", out var lEl) && lEl.ValueKind == JsonValueKind.Number ? lEl.GetInt32() : -1;

                        ProviderHealthList.Add(new GenericListItem
                        {
                            PrimaryText = displayName,
                            SecondaryText = ok ? $"Connected{((latency >= 0) ? $" ({latency} ms)" : "")}" : "Offline",
                            Status = ok ? ItemStatus.Succeeded : ItemStatus.Failed
                        });
                    }
                }

                // Cooldown
                var cooldownActive = root.TryGetProperty("CooldownActive", out var ca) && ca.GetBoolean();
                var cooldownUntil = GetString(root, "CooldownUntil");
                CooldownStatus = new StatusItem
                {
                    Caption = "Cooldown",
                    StatusText = cooldownActive ? $"Active until {FormatTimestamp(cooldownUntil)}" : "None"
                };

                // Timestamp
                LastUpdatedLabel = new LabelItem
                {
                    Text = $"Updated {DateTime.Now:HH:mm:ss}"
                };
            }
            catch
            {
                LastUpdatedLabel = new LabelItem { Text = $"Error parsing status at {DateTime.Now:HH:mm:ss}" };
            }
        }

        public void ApplyTo(PluginConfiguration cfg)
        {
            // Read-only — nothing to save
        }

        private static int GetInt(JsonElement parent, string prop)
        {
            return parent.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : 0;
        }

        private static string? GetString(JsonElement parent, string prop)
        {
            return parent.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        }

        private static string FormatTimestamp(string? iso)
        {
            if (string.IsNullOrEmpty(iso)) return "—";
            return DateTimeOffset.TryParse(iso, out var dto) ? dto.ToLocalTime().ToString("g") : iso;
        }
    }
}
