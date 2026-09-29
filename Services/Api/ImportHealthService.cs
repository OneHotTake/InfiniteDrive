using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace InfiniteDrive.Services;

[Route("/InfiniteDrive/Imports", "GET", Summary = "Admin: dated import coverage; reading never queues work")]
public sealed class ImportHealthRequest : IReturn<object>
{
    public int Offset { get; set; }
    public int Limit { get; set; } = 50;
}

[Route("/InfiniteDrive/Imports/Action", "POST", Summary = "Admin: import observation/repair controls")]
public sealed class ImportActionRequest : IReturn<object>
{
    public string Action { get; set; } = "";
    public string Identity { get; set; } = "";
    public string EpisodeKey { get; set; } = "";
    public string Mode { get; set; } = "";
}

public sealed class ImportHealthService : IService, IRequiresRequest
{
    private readonly IAuthorizationContext _authorization;
    public IRequest Request { get; set; } = null!;
    public ImportHealthService(IAuthorizationContext authorization) { _authorization = authorization; }

    public async Task<object> Get(ImportHealthRequest request)
    {
        var deny = AdminGuard.RequireAdmin(_authorization, Request);
        if (deny != null) return deny;
        var db = Plugin.Instance.DatabaseManager;
        // No schema creation, provider calls, notifications, or task launch from GET.
        if (db.GetMetadata("import_last_run") == null) return new { Status = "not_observed", Mode = Plugin.Instance.Configuration.ImportRecoveryMode.ToString() };
        var speed = ImportWorkBudget.For(Plugin.Instance.Configuration, DateTimeOffset.UtcNow);
        var used = await db.GetRecentImportAttemptsAsync(DateTimeOffset.UtcNow);
        var page = await db.GetImportCoveragePageAsync(request.Offset, request.Limit);
        return new { Mode = Plugin.Instance.Configuration.ImportRecoveryMode.ToString(),
            Speed = speed.IsCatchUp ? "catch_up" : "normal", speed.CatchUpUntil, speed.AttemptsPerDay, AttemptsUsed = used,
            LastRun = db.GetMetadata("import_last_run"), CollectionHealth = db.GetMetadata("import_collection_health"), NextOffset = request.Offset + page.Count,
            Items = page.Select(x => new { x.Identity, x.Title, Complete = x.Complete && x.SnapshotAt >= DateTimeOffset.UtcNow.AddHours(-6), x.SnapshotAt, x.CheckedAt,
                x.SnapshotStatus, x.ProviderStatus, x.Exclusion,
                Stale = x.SnapshotAt == null || x.SnapshotAt < DateTimeOffset.UtcNow.AddHours(-6),
                Expected = x.Items.Count(i => i.Eligible), Indexed = x.Items.Count(i => i.Eligible && i.State == "indexed"),
                Episodes = x.Items.Select(i => new { i.Key, i.Season, i.Episode, i.State, i.Eligibility,
                    i.Failure, i.Attempts, i.NextAttempt, i.ObservedAt }) }) };
    }

    public async Task<object> Post(ImportActionRequest request)
    {
        var deny = AdminGuard.RequireAdmin(_authorization, Request);
        if (deny != null) return deny;
        return await ApplyAsync(request);
    }

    // Native plugin settings are administered by Emby's existing settings authorization.
    internal static async Task<object> ApplyAsync(ImportActionRequest request)
    {
        var plugin = Plugin.Instance;
        var db = plugin.DatabaseManager;
        await ImportReconciliationService.MutationGate.WaitAsync();
        try
        {
            await db.EnsureImportCoverageAsync();
            switch (request.Action)
            {
                case "mode":
                    if (!Enum.TryParse<ImportMode>(request.Mode, false, out var mode) || !Enum.IsDefined(mode))
                        return new { Status = "invalid_mode" };
                    if (mode == ImportMode.Repair && db.GetMetadata("import_observation_baseline") != "success")
                        return new { Status = "observe_first" };
                    plugin.Configuration.ImportRecoveryMode = mode;
                    plugin.SaveConfiguration();
                    break;
                case "start_catch_up":
                    if (plugin.Configuration.ImportRecoveryMode != ImportMode.Repair)
                        return new { Status = "enable_recovery_first" };
                    if (ImportWorkBudget.For(plugin.Configuration, DateTimeOffset.UtcNow).IsCatchUp)
                        return new { Status = "already_running" };
                    var started = DateTimeOffset.UtcNow;
                    plugin.Configuration.ImportCatchUpStartedAt = started.ToString("o");
                    plugin.Configuration.ImportCatchUpUntil = started.AddDays(7).ToString("o");
                    plugin.SaveConfiguration();
                    break;
                case "stop_catch_up":
                    plugin.Configuration.ImportCatchUpStartedAt = "";
                    plugin.Configuration.ImportCatchUpUntil = "";
                    plugin.SaveConfiguration();
                    break;
                case "resume_provider":
                    await db.PersistMetadataAsync("import_provider_resume", DateTimeOffset.UtcNow.ToString("o"));
                    break;
                case "check":
                case "retry":
                    // Mark intent; the existing locked Marvin run consumes due work. Backoff stays intact.
                    await db.PersistMetadataAsync("import_check_requested", DateTimeOffset.UtcNow.ToString("o"));
                    break;
                case "include_specials":
                case "exclude_specials":
                    var state = await db.GetImportCoverageAsync(request.Identity);
                    if (state == null) return new { Status = "unknown_identity" };
                    state.IncludeSpecials = request.Action == "include_specials";
                    state.Generation++;
                    await db.SaveImportCoverageAsync(state);
                    break;
                default: return new { Status = "unknown_action" };
            }
        }
        finally { ImportReconciliationService.MutationGate.Release(); }
        var operationId = Guid.NewGuid().ToString("N");
        await db.PersistMetadataAsync("import_requested_operation", operationId);
        if (plugin.Configuration.ImportRecoveryMode != ImportMode.Off) plugin.TriggerBackgroundSync();
        return new { Status = "queued_or_deferred", OperationId = operationId };
    }
}
