# Sprint 120 — Logging (v3.3)

**Version:** v3.3 | **Status:** Planning | **Risk:** LOW | **Depends:** Sprint 119

---

## Overview

Sprint 120 implements comprehensive logging for v3.3. Logging covers item pipeline, stream resolution, and general events for debugging and auditing.

**Key Components:**
- PipelineLogger - Logs item lifecycle events
- ResolutionLogger - Logs stream resolution events
- LogRepository - Persist log entries
- LogRetention - Manages log retention and cleanup

---

## Phase 120A — PipelineLogger

### FIX-120A-01: Create PipelineLogger

**File:** `Logging/PipelineLogger.cs`

```csharp
public class PipelineLogger
{
    private readonly ILogRepository _repo;
    private readonly ILogger _logger;

    // Primary ID types for log tables (no FK to media_items)
    public enum PrimaryIdType
    {
        MediaItem,
        Source,
        Collection,
        User,
        Unknown
    }

    public async Task LogAsync(
        string primaryId, // TEXT UUID for MediaItem, string IDs for other types
        PrimaryIdType primaryIdType,
        string mediaType, // "movie" or "series"
        string phase,
        PipelineTrigger trigger,
        bool success,
        string? details = null,
        CancellationToken ct = default)
    {
        var entry = new PipelineLogEntry
        {
            PrimaryId = primaryId,
            PrimaryIdType = primaryIdType.ToString().ToLower(),
            MediaType = mediaType,
            Phase = phase,
            Trigger = trigger.ToString(),
            Success = success,
            Details = details,
            Timestamp = DateTimeOffset.UtcNow
        };

        await _repo.InsertPipelineLogAsync(entry, ct);

        // Also log to Emby logger
        if (success)
        {
            _logger.Info("[{Phase}] Item {PrimaryId} - {Trigger}", phase, primaryId, trigger);
        }
        else
        {
            _logger.Warn("[{Phase}] Item {PrimaryId} - {Trigger} - {Details}", phase, primaryId, trigger, details);
        }
    }

    public async Task LogResolutionAsync(
        string primaryId,
        PrimaryIdType primaryIdType,
        string mediaType,
        string mediaId,
        int streamCount,
        string? selectedStream,
        TimeSpan duration,
        CancellationToken ct = default)
    {
        var entry = new ResolutionLogEntry
        {
            PrimaryId = primaryId,
            PrimaryIdType = primaryIdType.ToString().ToLower(),
            MediaType = mediaType,
            MediaId = mediaId,
            StreamCount = streamCount,
            SelectedStream = selectedStream,
            DurationMs = (long)duration.TotalMilliseconds,
            Timestamp = DateTimeOffset.UtcNow
        };

        await _repo.InsertResolutionLogAsync(entry, ct);

        _logger.Info("Resolved {MediaId}: {Count} streams, selected: {Stream}, took {Duration}ms",
            mediaId, streamCount, selectedStream, (long)duration.TotalMilliseconds);
    }

    public async Task LogFailureAsync(
        string primaryId,
        PrimaryIdType primaryIdType,
        string mediaType,
        string phase,
        FailureReason reason,
        string? details = null,
        Exception? exception = null,
        CancellationToken ct = default)
    {
        var errorMessage = details;
        if (exception != null)
        {
            errorMessage += $" | {exception.GetType().Name}: {exception.Message}";
        }

        await LogAsync(primaryId, primaryIdType, mediaType, phase, PipelineTrigger.ManualSync, false, errorMessage, ct);

        _logger.Error(exception, "[{Phase}] Item {PrimaryId} failed: {Reason}", phase, primaryId, reason);
    }
}
```

**Acceptance Criteria:**
- [ ] Logs pipeline events
- [ ] Logs resolution events
- [ ] Logs failures with exceptions
- [ ] Writes to repository
- [ ] Also logs to Emby logger
- [ ] Uses Emby ILogger (not MEL ILogger<T>)

---

## Phase 120B — LogRepository

### FIX-120B-01: Create ILogRepository Interface

**File:** `Repositories/Interfaces/ILogRepository.cs`

```csharp
public interface ILogRepository
{
    Task InsertPipelineLogAsync(PipelineLogEntry entry, CancellationToken ct = default);
    Task InsertResolutionLogAsync(ResolutionLogEntry entry, CancellationToken ct = default);
    Task<List<PipelineLogEntry>> GetPipelineLogsAsync(string? primaryId, string? primaryIdType, string? mediaType, PipelineTrigger? trigger, int limit, CancellationToken ct = default);
    Task<List<ResolutionLogEntry>> GetResolutionLogsAsync(string? primaryId, string? primaryIdType, string? mediaType, int limit, CancellationToken ct = default);
    Task<List<RecentLogEntry>> GetRecentLogsAsync(string? level, int limit, CancellationToken ct = default);
    Task<int> GetPipelineLogCountAsync(CancellationToken ct = default);
    Task<int> GetResolutionLogCountAsync(CancellationToken ct = default);
    Task<int> PrunePipelineLogsAsync(DateTimeOffset before, CancellationToken ct = default);
    Task<int> PruneResolutionLogsAsync(DateTimeOffset before, CancellationToken ct = default);
}
```

**Acceptance Criteria:**
- [ ] Insert pipeline log
- [ ] Insert resolution log
- [ ] Query pipeline logs with filters
- [ ] Query resolution logs with filters
- [ ] Query recent logs with level filter
- [ ] Get pipeline log count
- [ ] Get resolution log count
- [ ] Prune pipeline logs before timestamp
- [ ] Prune resolution logs before timestamp

### FIX-120B-02: Implement LogRepository in DatabaseManager

**File:** `Data/DatabaseManager.cs`

Add ILogRepository implementation:

```csharp
public async Task InsertPipelineLogAsync(PipelineLogEntry entry, CancellationToken ct = default)
{
    const string sql = @"
        INSERT INTO item_pipeline_log (primary_id, primary_id_type, media_type, phase, trigger, success, details, timestamp)
        VALUES (@PrimaryId, @PrimaryIdType, @MediaType, @Phase, @Trigger, @Success, @Details, @Timestamp)";

    await ExecuteAsync(sql, entry, ct);
}

public async Task InsertResolutionLogAsync(ResolutionLogEntry entry, CancellationToken ct = default)
{
    const string sql = @"
        INSERT INTO stream_resolution_log (primary_id, primary_id_type, media_type, media_id, stream_count, selected_stream, duration_ms, timestamp)
        VALUES (@PrimaryId, @PrimaryIdType, @MediaType, @MediaId, @StreamCount, @SelectedStream, @DurationMs, @Timestamp)";

    await ExecuteAsync(sql, entry, ct);
}

public async Task<List<PipelineLogEntry>> GetPipelineLogsAsync(
    string? primaryId, string? primaryIdType, string? mediaType, PipelineTrigger? trigger, int limit, CancellationToken ct = default)
{
    var sql = @"
        SELECT * FROM item_pipeline_log
        WHERE 1=1";

    var parameters = new Dictionary<string, object>();

    if (!string.IsNullOrEmpty(primaryId))
    {
        sql += " AND primary_id = @PrimaryId";
        parameters["PrimaryId"] = primaryId;
    }

    if (!string.IsNullOrEmpty(primaryIdType))
    {
        sql += " AND primary_id_type = @PrimaryIdType";
        parameters["PrimaryIdType"] = primaryIdType;
    }

    if (!string.IsNullOrEmpty(mediaType))
    {
        sql += " AND media_type = @MediaType";
        parameters["MediaType"] = mediaType;
    }

    if (trigger.HasValue)
    {
        sql += " AND trigger = @Trigger";
        parameters["Trigger"] = trigger.Value.ToString();
    }

    sql += " ORDER BY timestamp DESC LIMIT @Limit";
    parameters["Limit"] = limit;

    return await QueryAsync<PipelineLogEntry>(sql, parameters, ct);
}

public async Task<List<ResolutionLogEntry>> GetResolutionLogsAsync(string? primaryId, string? primaryIdType, string? mediaType, int limit, CancellationToken ct = default)
{
    var sql = @"
        SELECT * FROM stream_resolution_log
        WHERE 1=1";

    var parameters = new Dictionary<string, object>();

    if (!string.IsNullOrEmpty(primaryId))
    {
        sql += " AND primary_id = @PrimaryId";
        parameters["PrimaryId"] = primaryId;
    }

    if (!string.IsNullOrEmpty(primaryIdType))
    {
        sql += " AND primary_id_type = @PrimaryIdType";
        parameters["PrimaryIdType"] = primaryIdType;
    }

    if (!string.IsNullOrEmpty(mediaType))
    {
        sql += " AND media_type = @MediaType";
        parameters["MediaType"] = mediaType;
    }

    sql += " ORDER BY timestamp DESC LIMIT @Limit";
    parameters["Limit"] = limit;

    return await QueryAsync<ResolutionLogEntry>(sql, parameters, ct);
}

public async Task<List<RecentLogEntry>> GetRecentLogsAsync(string? level, int limit, CancellationToken ct = default)
{
    var sql = @"
        SELECT * FROM item_pipeline_log
        WHERE 1=1";

    var parameters = new Dictionary<string, object>();

    if (!string.IsNullOrEmpty(level))
    {
        if (level == "error")
        {
            sql += " AND success = 0";
        }
    }

    sql += " ORDER BY timestamp DESC LIMIT @Limit";
    parameters["Limit"] = limit;

    var entries = await QueryAsync<PipelineLogEntry>(sql, parameters, ct);

    return entries.Select(e => new RecentLogEntry(
        e.Timestamp,
        "pipeline",
        e.Success ? "info" : "error",
        $"{e.Phase}: Item {e.PrimaryId} ({e.MediaType}) - {e.Trigger}",
        e.Details
    )).ToList();
}

public async Task<int> GetPipelineLogCountAsync(CancellationToken ct = default)
{
    const string sql = @"
        SELECT COUNT(*) FROM item_pipeline_log";

    return await ExecuteScalarAsync<int>(sql, ct);
}

public async Task<int> GetResolutionLogCountAsync(CancellationToken ct = default)
{
    const string sql = @"
        SELECT COUNT(*) FROM stream_resolution_log";

    return await ExecuteScalarAsync<int>(sql, ct);
}

public async Task<int> PrunePipelineLogsAsync(DateTimeOffset before, CancellationToken ct = default)
{
    const string sql = @"
        DELETE FROM item_pipeline_log
        WHERE timestamp < @Before";

    return await ExecuteAsync(sql, new { Before = before }, ct);
}

public async Task<int> PruneResolutionLogsAsync(DateTimeOffset before, CancellationToken ct = default)
{
    const string sql = @"
        DELETE FROM stream_resolution_log
        WHERE timestamp < @Before";

    return await ExecuteAsync(sql, new { Before = before }, ct);
}
```

**Acceptance Criteria:**
- [ ] Inserts pipeline logs
- [ ] Inserts resolution logs
- [ ] Queries with filters
- [ ] Prunes pipeline logs
- [ ] Prunes resolution logs
- [ ] Returns count of pruned logs
- [ ] Returns log counts

---

## Phase 120C — LogRetention

### FIX-120C-01: Create LogRetentionService

**File:** `Services/LogRetentionService.cs`

```csharp
public class LogRetentionService
{
    private readonly ILogRepository _repo;
    private readonly ILogger _logger;

    // Retention periods
    private static readonly TimeSpan PipelineLogRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan ResolutionLogRetention = TimeSpan.FromDays(7);

    public async Task RetentionCleanupAsync(CancellationToken ct = default)
    {
        var pipelineCutoff = DateTimeOffset.UtcNow - PipelineLogRetention;
        var resolutionCutoff = DateTimeOffset.UtcNow - ResolutionLogRetention;

        _logger.Info("Starting log retention cleanup...");

        var pipelineCount = await _repo.PrunePipelineLogsAsync(pipelineCutoff, ct);
        _logger.Info("Pruned {Count} pipeline logs older than {Days} days",
            pipelineCount, PipelineLogRetention.Days);

        var resolutionCount = await _repo.PruneResolutionLogsAsync(resolutionCutoff, ct);
        _logger.Info("Pruned {Count} resolution logs older than {Days} days",
            resolutionCount, ResolutionLogRetention.Days);

        var totalCount = pipelineCount + resolutionCount;
        _logger.Info("Log retention cleanup complete: {Total} logs removed", totalCount);
    }

    public async Task<LogRetentionStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var pipelineCount = await _repo.GetPipelineLogCountAsync(ct);
        var resolutionCount = await _repo.GetResolutionLogCountAsync(ct);

        return new LogRetentionStatus
        {
            PipelineLogCount = pipelineCount,
            ResolutionLogCount = resolutionCount,
            PipelineRetentionDays = PipelineLogRetention.Days,
            ResolutionRetentionDays = ResolutionLogRetention.Days,
            LastCleanupAt = GetLastCleanupTime()
        };
    }
}

public record LogRetentionStatus(
    int PipelineLogCount,
    int ResolutionLogCount,
    int PipelineRetentionDays,
    int ResolutionRetentionDays,
    DateTimeOffset? LastCleanupAt
);
```

**Acceptance Criteria:**
- [ ] Prunes pipeline logs (> 30 days)
- [ ] Prunes resolution logs (> 7 days)
- [ ] Uses separate prune methods
- [ ] Returns retention status
- [ ] Logs cleanup results
- [ ] Uses Emby ILogger (not MEL ILogger<T>)

---

## Phase 120D — LogRetentionTask

### FIX-120D-01: Create LogRetentionTask

**File:** `Tasks/LogRetentionTask.cs`

```csharp
public class LogRetentionTask : IScheduledTask
{
    private readonly LogRetentionService _service;
    private readonly ILogger _logger;

    public string Name => "EmbyStreams Log Retention";
    public string Key => "embystreams_log_retention";
    public string Description => "Cleans up old pipeline and resolution logs";
    public string Category => "EmbyStreams";

    public async Task ExecuteAsync(CancellationToken ct, IProgress<double> progress)
    {
        progress?.Report(0);

        _logger.Info("Starting log retention task...");

        await _service.RetentionCleanupAsync(ct);

        progress?.Report(100);

        _logger.Info("Log retention task complete");
    }
}
```

**Acceptance Criteria:**
- [ ] Runs retention cleanup
- [ ] Reports progress
- [ ] Logs completion
- [ ] Uses Emby ILogger (not MEL ILogger<T>)

---

## Sprint 120 Dependencies

- **Previous Sprint:** 119 (API Endpoints)
- **Blocked By:** Sprint 119
- **Blocks:** Sprint 121 (E2E Validation)

---

## Sprint 120 Completion Criteria

- [ ] PipelineLogger logs all pipeline events
- [ ] ResolutionLogger logs all resolution events
- [ ] LogRepository persists and queries logs
- [ ] LogRepository has separate prune methods for each log type
- [ ] LogRetentionService prunes old logs with correct retention periods
- [ ] LogRetentionTask runs periodic cleanup
- [ ] All logging uses Emby ILogger (not MEL ILogger<T>)
- [ ] Build succeeds
- [ ] E2E: Logs appear in UI

---

## Sprint 120 Notes

**Log Schema (v3.3 Spec §13):**

Per v3.3 spec §13, log tables use this exact schema:

**item_pipeline_log:**
- primary_id: TEXT (MediaItem UUID or other ID)
- primary_id_type: TEXT (mediaitem, source, collection, user, unknown)
- media_type: TEXT ("movie" or "series")
- phase: TEXT
- trigger: TEXT
- success: INTEGER (0 or 1)
- details: TEXT
- timestamp: TEXT (ISO 8601)

**stream_resolution_log:**
- primary_id: TEXT (MediaItem UUID or other ID)
- primary_id_type: TEXT (mediaitem, source, collection, user, unknown)
- media_type: TEXT ("movie" or "series")
- media_id: TEXT
- stream_count: INTEGER
- selected_stream: TEXT
- duration_ms: INTEGER
- timestamp: TEXT (ISO 8601)

**Log Entry Models:**

```csharp
public record PipelineLogEntry(
    string PrimaryId,
    string PrimaryIdType,
    string MediaType,
    string Phase,
    string Trigger,
    bool Success,
    string? Details,
    DateTimeOffset Timestamp
);

public record ResolutionLogEntry(
    string PrimaryId,
    string PrimaryIdType,
    string MediaType,
    string MediaId,
    int StreamCount,
    string? SelectedStream,
    long DurationMs,
    DateTimeOffset Timestamp
);

public record RecentLogEntry(
    DateTimeOffset Timestamp,
    string LogType,
    string Level,
    string Message,
    string? Details
);
```

**Key Design Points:**
- NO foreign key to media_items table
- Uses composite key: (primary_id, primary_id_type, media_type)
- primary_id: TEXT (MediaItem UUID or other ID)
- primary_id_type: enum (mediaitem, source, collection, user, unknown)
- This design allows logging for any entity type, not just media_items
- Prevents orphaned log entries when items are deleted

**Retention Periods:**
- Pipeline logs: 30 days
- Resolution logs: 7 days
- Separate prune methods for each log type (not combined)

**Emby ILogger Pattern (CRITICAL):**

Use Emby's ILogger, not MEL's ILogger<T>:

```csharp
// CORRECT:
private readonly ILogger _logger;
_logger.Info("Message");
_logger.Warn("Message");
_logger.Error(ex, "Message");
_logger.Debug("Message");

// WRONG (do NOT use):
private readonly ILogger<MyClass> _logger;
_logger.LogInformation("Message");
```

Emby's ILogger interface has these methods:
- Info(string message, params object[] args)
- Warn(string message, params object[] args)
- Error(Exception exception, string message, params object[] args)
- Debug(string message, params object[] args)

**Log Levels:**
- Info: Successful events
- Warning: Non-fatal failures
- Error: Fatal failures

**Scheduled Task:**
- Runs daily at 2:00 AM
- No user interaction needed
- Automatic cleanup
