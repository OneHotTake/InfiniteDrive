using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfiniteDrive.Tests;
public sealed class ImportAnalyticsTests
{
    static ImportAnalyticsTests() => SqliteTestRuntime.EnsureInitialized();
    [Fact] public async Task ConcurrentLookupsKeepAccurateCountersAndBoundedTimingSamples()
    {
        var telemetry = new ImportRunTelemetry("test");
        await Task.WhenAll(Enumerable.Range(0, 1000).Select(i => Task.Run(() =>
        {
            telemetry.Attempt(i % 2 == 0); telemetry.LookupStarted();
            telemetry.RecordTiming("resolution", i); telemetry.LookupFinished(i % 2 == 0 ? "" : "source_unavailable");
        })));
        var run = telemetry.Snapshot(); var timing = run.Timings["resolution"];
        Assert.Equal(0, run.ActiveLookups); Assert.Equal(500, run.MissingAttempts);
        Assert.Equal(500, run.RefreshAttempts); Assert.Equal(500, run.Matched); Assert.Equal(500, run.EmptyResults);
        Assert.Equal(1000, timing.Count); Assert.Equal(512, timing.SampleCount);
        Assert.Equal(499500, timing.TotalSeconds); Assert.Equal(499.5, timing.MeanSeconds); Assert.Equal(999, timing.MaxSeconds);
    }
    [Fact] public void LookupMatchesAndPublishedGroupsAreDifferentUnits()
    {
        var t = new ImportRunTelemetry("test"); t.Work("metadata", "A title", "aired:1:1");
        for (var i = 1; i <= 20; i++) t.RecordTiming("metadata", i);
        t.LookupStarted(); t.LookupFinished(""); t.PublicationFailed();
        var before = t.Snapshot(); Assert.Equal(1, before.Matched); Assert.Equal(0, before.Published);
        Assert.Equal(1, before.PublicationFailures); Assert.Equal(19, before.Timings["metadata"].P95Seconds);
        t.Published(true); t.Finish("budget_deferred");
        var done = t.Snapshot(); Assert.NotNull(done.FinishedAt); Assert.Equal("budget_deferred", done.Status);
        Assert.Equal(1, done.Published); Assert.Equal(1, done.Refreshed); Assert.Equal("finished", done.Phase);
        Assert.DoesNotContain("https://", JsonSerializer.Serialize(done));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.RecordTiming("https://private.invalid/token", 1));
    }
    [Fact] public async Task ReadingOldHistoryCreatesNoSchemaAndNewReportsSurviveReopenWithoutResettingLedger()
    {
        var root = Path.Combine(Path.GetTempPath(), "analytics-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var db = new DatabaseManager(root, NullLogger.Instance); db.Initialise();
            Assert.Empty(await db.GetImportRunHistoryAsync());
            await db.EnsureImportCoverageAsync(); var now = DateTimeOffset.UtcNow;
            await db.RecordImportAttemptAsync("keep", now, default);
            await db.SaveImportRunReportAsync("old", now.AddDays(-8), "old", default);
            await db.SaveImportRunReportAsync("current", now, "current", default);
            db = new DatabaseManager(root, NullLogger.Instance); db.Initialise();
            Assert.Equal(new[] { "current" }, await db.GetImportRunHistoryAsync());
            Assert.Equal(1, await db.GetRecentImportAttemptsAsync(now));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task NextCreditUsesTheCorrectExpiringAttemptWhenReturningToALowerCeiling()
    {
        var root = Path.Combine(Path.GetTempPath(), "credit-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var db = new DatabaseManager(root, NullLogger.Instance); db.Initialise(); await db.EnsureImportCoverageAsync();
            var now = DateTimeOffset.Parse("2026-09-30T20:00:00Z");
            await db.RecordImportAttemptAsync("first", now.AddHours(-2), default);
            await db.RecordImportAttemptAsync("second", now.AddHours(-1), default);
            await db.RecordImportAttemptAsync("third", now, default);
            Assert.Equal(now.AddHours(23), await db.GetNextImportCreditAsync(now, 2));
            Assert.Equal(now.AddHours(22), await db.GetNextImportCreditAsync(now, 3));
            Assert.Null(await db.GetNextImportCreditAsync(now, 4));
        }
        finally { Directory.Delete(root, true); }
    }
}
