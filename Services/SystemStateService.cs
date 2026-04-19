using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Centralized state engine for InfiniteDrive system health.
    /// Always evaluates live — config fields and DB counts are cheap.
    /// </summary>
    public class SystemStateService
    {
        private const string CacheKey = "system_state_snapshot";

        private readonly DatabaseManager _database;

        public SystemStateService(DatabaseManager database)
        {
            _database = database;
        }

        public async Task<SystemSnapshot> EvaluateStateAsync(CancellationToken ct = default)
        {
            var config = Plugin.Instance?.Configuration;
            var snapshot = new SystemSnapshot { EvaluatedAt = DateTime.UtcNow.ToString("o") };

            // Provider health — derive reachability from StatusService health cache
            snapshot.PrimaryProvider = EvaluateProvider("primary", config?.PrimaryManifestUrl);
            snapshot.SecondaryProvider = EvaluateProvider("secondary", config?.SecondaryManifestUrl);

            // Library health
            snapshot.Library = await EvaluateLibrariesAsync(config, ct);

            // Overall state
            DetermineSystemState(snapshot);

            // Persist for diagnostics
            await PersistStateAsync(snapshot, ct);
            return snapshot;
        }

        public async Task<SystemSnapshot> GetStateAsync(CancellationToken ct = default)
        {
            // Always re-evaluate — config changes must be reflected immediately
            return await EvaluateStateAsync(ct);
        }

        public async Task<SystemSnapshot> UpdateProviderTestAsync(
            string providerId, bool isReachable, int latencyMs, string message, CancellationToken ct = default)
        {
            // Re-evaluate then override the specific provider's reachability
            var snapshot = await EvaluateStateAsync(ct);
            var provider = providerId.Equals("primary", StringComparison.OrdinalIgnoreCase)
                ? snapshot.PrimaryProvider : snapshot.SecondaryProvider;

            provider.IsReachable = isReachable;
            provider.LastTestAt = DateTime.UtcNow.ToString("o");
            provider.LatencyMs = latencyMs;
            provider.Message = message;

            DetermineSystemState(snapshot);
            await PersistStateAsync(snapshot, ct);
            return snapshot;
        }

        public async Task<string[]> GetReachableProvidersAsync(CancellationToken ct = default)
        {
            var s = await GetStateAsync(ct);
            var list = new System.Collections.Generic.List<string>();
            if (s.PrimaryProvider.IsReachable) list.Add("primary");
            if (s.SecondaryProvider.IsReachable) list.Add("secondary");
            return list.ToArray();
        }

        // ── Private ──────────────────────────────────────────────────────

        private ProviderHealth EvaluateProvider(string id, string? url)
        {
            var configured = !string.IsNullOrWhiteSpace(url);
            return new ProviderHealth
            {
                ProviderId = id,
                IsConfigured = configured,
                IsReachable = false,
                Message = configured ? "Not yet tested" : "Not configured"
            };
        }

        private async Task<LibraryHealth> EvaluateLibrariesAsync(PluginConfiguration? config, CancellationToken ct)
        {
            if (config == null) return new LibraryHealth();

            var libConfigured =
                !string.IsNullOrWhiteSpace(config.SyncPathMovies) &&
                !string.IsNullOrWhiteSpace(config.LibraryNameMovies) &&
                !string.IsNullOrWhiteSpace(config.LibraryNameSeries);

            int catalogCount = 0;
            try { catalogCount = await _database.GetCatalogItemCountAsync(); } catch { }

            int strmCount = 0;
            try
            {
                strmCount += CountStrm(config.SyncPathMovies);
                strmCount += CountStrm(config.SyncPathShows);
                strmCount += CountStrm(config.SyncPathAnime);
            }
            catch { }

            bool accessible = false;
            if (libConfigured)
            {
                try { accessible = Directory.Exists(config.SyncPathMovies); } catch { }
            }

            return new LibraryHealth
            {
                IsConfigured = libConfigured,
                IsAccessible = accessible,
                CatalogItemCount = catalogCount,
                StrmFileCount = strmCount
            };
        }

        private void DetermineSystemState(SystemSnapshot s)
        {
            if (s.Library.IsConfigured && !s.Library.IsAccessible)
            {
                s.State = SystemStateEnum.Error;
                s.Description = "Library paths not accessible";
                return;
            }
            if (!s.PrimaryProvider.IsConfigured && !s.SecondaryProvider.IsConfigured)
            {
                s.State = SystemStateEnum.Unconfigured;
                s.Description = "No providers configured";
                return;
            }
            if (!s.Library.IsConfigured)
            {
                s.State = SystemStateEnum.Unconfigured;
                s.Description = "Libraries not configured";
                return;
            }
            if (!s.AllProvidersReachable && s.AnyProviderReachable)
            {
                s.State = SystemStateEnum.Degraded;
                s.Description = "Some providers unreachable";
                return;
            }
            // Configured (and libraries configured) = Ready
            // Provider reachability comes from explicit tests — absence = trust config
            s.State = SystemStateEnum.Ready;
            s.Description = s.AnyProviderReachable ? "System healthy" : "System ready (providers not yet tested)";
        }

        private async Task PersistStateAsync(SystemSnapshot snapshot, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(snapshot);
            await _database.PersistMetadataAsync(CacheKey, json, ct);
        }

        private static int CountStrm(string? path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return 0;
            return Directory.GetFiles(path, "*.strm", SearchOption.AllDirectories).Length;
        }
    }
}
