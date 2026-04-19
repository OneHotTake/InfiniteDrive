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
    /// Persists state snapshots to DatabaseManager metadata store.
    /// </summary>
    public class SystemStateService
    {
        private const string CacheKey = "system_state_snapshot";
        private const int CacheTtlMinutes = 30;

        private readonly DatabaseManager _database;

        public SystemStateService(DatabaseManager database)
        {
            _database = database;
        }

        public async Task<SystemSnapshot> EvaluateStateAsync(CancellationToken ct = default)
        {
            var config = Plugin.Instance?.Configuration;
            var snapshot = new SystemSnapshot { EvaluatedAt = DateTime.UtcNow.ToString("o") };

            // Provider health
            snapshot.PrimaryProvider = EvaluateProvider("primary", config?.PrimaryManifestUrl);
            snapshot.SecondaryProvider = EvaluateProvider("secondary", config?.SecondaryManifestUrl);

            // Library health
            snapshot.Library = await EvaluateLibrariesAsync(config, ct);

            // Overall state
            DetermineSystemState(snapshot);

            // Persist
            await PersistStateAsync(snapshot, ct);
            return snapshot;
        }

        public async Task<SystemSnapshot> GetStateAsync(CancellationToken ct = default)
        {
            var json = _database.GetMetadata(CacheKey);
            if (!string.IsNullOrEmpty(json))
            {
                try { return JsonSerializer.Deserialize<SystemSnapshot>(json) ?? new SystemSnapshot(); }
                catch { /* corrupt cache, re-evaluate */ }
            }
            return await EvaluateStateAsync(ct);
        }

        public async Task<SystemSnapshot> UpdateProviderTestAsync(
            string providerId, bool isReachable, int latencyMs, string message, CancellationToken ct = default)
        {
            var snapshot = await GetStateAsync(ct);
            var provider = providerId.Equals("primary", StringComparison.OrdinalIgnoreCase)
                ? snapshot.PrimaryProvider : snapshot.SecondaryProvider;

            provider.IsReachable = isReachable;
            provider.LastTestAt = DateTime.UtcNow.ToString("o");
            provider.LatencyMs = latencyMs;
            provider.Message = message;
            provider.ExpiresAt = DateTime.UtcNow.AddMinutes(CacheTtlMinutes).ToString("o");

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
            if (!s.AnyProviderReachable && (s.PrimaryProvider.IsConfigured || s.SecondaryProvider.IsConfigured))
            {
                // Configured but never tested — treat as Ready (trust config)
                s.State = SystemStateEnum.Ready;
                s.Description = "System ready (providers not yet tested)";
                return;
            }
            s.State = SystemStateEnum.Ready;
            s.Description = "System healthy";
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
