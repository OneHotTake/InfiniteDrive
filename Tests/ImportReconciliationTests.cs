using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SQLitePCL.pretty;

namespace InfiniteDrive.Tests;

public sealed class ImportReconciliationTests
{
    static ImportReconciliationTests() => SqliteTestRuntime.EnsureInitialized();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T18:00:00Z");
    private static ImportEpisode Ep(int n = 1) => new() { Key = $"aired:1:{n}", Season = 1, Episode = n, Released = Now.AddDays(-1), Eligible = true };

    [Theory]
    [InlineData(false, false, false, "missing")]
    [InlineData(true, false, false, "awaiting_indexing")]
    [InlineData(true, true, false, "indexed")]
    [InlineData(true, true, true, "index_mismatch")]
    public void FilesAndNativeChildrenAreSeparate(bool file, bool indexed, bool conflict, string state)
        => Assert.Equal(state, ImportCoveragePolicy.Classify(Ep(), file, indexed, conflict, Now));

    [Fact] public void DeletedPublishedFileIsMissing()
    {
        var ep = Ep(); ep.EverPublished = true;
        Assert.Equal("missing", ImportCoveragePolicy.Classify(ep, false, false, false, Now));
    }
    [Fact] public void DatesAndSpecialsAreConservative()
    {
        var ep = Ep(); ep.Season = 0;
        Assert.Equal("special_excluded", ImportCoveragePolicy.Eligibility(ep, false, Now, TimeZoneInfo.Utc));
        ep.Released = null;
        Assert.Equal("unknown_date", ImportCoveragePolicy.Eligibility(ep, true, Now, TimeZoneInfo.Utc));
        ep.Released = Now.Date; ep.DateOnly = true;
        Assert.Equal("future", ImportCoveragePolicy.Eligibility(ep, true, Now, TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")));
        ep.Numbering = "absolute";
        Assert.Equal("numbering_conflict", ImportCoveragePolicy.Eligibility(ep, true, Now, TimeZoneInfo.Utc));
    }
    [Fact] public void RetryBackoffAndCooldownSurvivePolicyEvaluation()
    {
        Assert.Equal(Now.AddMinutes(15), ImportCoveragePolicy.RetryAt(1, false, Now));
        Assert.Equal(Now.AddHours(1), ImportCoveragePolicy.RetryAt(2, false, Now));
        Assert.Equal(Now.AddHours(4), ImportCoveragePolicy.RetryAt(3, false, Now));
        Assert.Equal(Now.AddDays(1), ImportCoveragePolicy.RetryAt(4, false, Now));
        Assert.Equal(Now.AddHours(6), ImportCoveragePolicy.RetryAt(1, true, Now));
        Assert.Equal(Now.AddDays(7), ImportCoveragePolicy.RetryAt(8, true, Now));
        Assert.Equal(Now.AddDays(2), ImportCoveragePolicy.RetryAt(1, true, Now, 0, Now.AddDays(2)));
    }
    [Fact] public void EmptyShrunkOrDuplicateInventoryCannotEraseBaseline()
    {
        var old = Enumerable.Range(1, 10).Select(Ep).ToList();
        Assert.False(ImportCoveragePolicy.AcceptSnapshot(old, new List<ImportEpisode>()));
        Assert.False(ImportCoveragePolicy.AcceptSnapshot(old, old.Take(7).ToList()));
        Assert.False(ImportCoveragePolicy.AcceptSnapshot(old, old.Append(Ep()).ToList()));
        Assert.True(ImportCoveragePolicy.AcceptSnapshot(old, old));
    }
    [Fact] public void UnchangedSnapshotRetainsFailure()
    {
        var ep = Ep(); ep.Attempts = 3; ep.NextAttempt = Now.AddHours(1);
        var state = new ImportCoverage { Items = new() { ep }, SnapshotAt = Now };
        ImportReconciliationService.MergeSnapshot(state, new() { Ep(), Ep(2) });
        Assert.Same(ep, state.Items[0]); Assert.Equal(3, ep.Attempts);
        Assert.Equal("aired:1:2", state.Items[1].Key);
    }
    [Fact] public void PrefixTraversalAndSymlinkRootsAreRejected()
    {
        using var dir = new TempDir();
        Assert.False(ImportInventory.SafeManagedPath(dir.Path, dir.Path + "-other/title"));
        Assert.False(ImportInventory.SafeManagedPath(dir.Path, System.IO.Path.Combine(dir.Path, "..", "escape")));
        var link = System.IO.Path.Combine(dir.Path, "link"); Directory.CreateSymbolicLink(link, "/tmp");
        Assert.False(ImportInventory.SafeManagedPath(dir.Path, System.IO.Path.Combine(link, "title")));
    }
    [Fact] public void MultiVersionCoverageIgnoresAdjacentEpisodesAndCorruptFiles()
    {
        using var dir = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "E1.strm"), "https://example.invalid/one");
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "E1 - HD.strm"), "https://example.invalid/two");
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "E10.strm"), "https://example.invalid/ten");
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "E1 - corrupt.strm"), "partial");
        Assert.Equal(2, ImportInventory.FindFiles(dir.Path, "E1").Count);
    }

    [Fact] public async Task DefaultWorkerScansTheFirstCatalogPage()
    {
        using var h = new Harness(); await h.Seed();
        Assert.Single(await h.Db.GetImportCatalogPageAsync("", 40));
        await new ImportReconciliationService(h.Db, h.Inventory, () => ImportMode.Observe, TimeZoneInfo.Utc, () => h.Now).RunAsync(default);
        Assert.Equal("success", h.Db.GetMetadata("import_observation_baseline"));
        Assert.Equal(2, (await h.State()).Items.Count);
    }
    [Fact] public async Task ObserveNeverResolvesOrPublishes()
    {
        using var h = new Harness(); await h.Seed(); await h.Run(ImportMode.Observe);
        Assert.Equal(0, h.Inventory.Resolutions); Assert.Equal(0, h.Inventory.Published);
        Assert.Equal("success", h.Db.GetMetadata("import_observation_baseline"));
        Assert.All((await h.State()).Items, e => Assert.Equal("missing", e.State));
    }
    [Fact] public async Task ExistingFilesAreRefreshedWithoutPretendingObservationResolvedThem()
    {
        using var h = new Harness(); h.Inventory.Count = 1; h.Inventory.Files.Add(1); await h.Seed();
        await h.Run(ImportMode.Observe);
        Assert.Null((await h.State()).Items[0].LastVersionRefresh);
        Assert.Equal(0, h.Inventory.Resolutions);
        await h.Run(); Assert.Equal(1, h.Inventory.Resolutions); Assert.Contains(1, h.Inventory.Files);
        Assert.Equal(h.Now, (await h.State()).Items[0].LastVersionRefresh);
    }
    [Fact] public async Task PartialFailureRetriesAfterRestartWithUnchangedMetadata()
    {
        using var h = new Harness(); await h.Seed(); h.Inventory.FailEpisode = 2;
        await h.Run(); Assert.Equal(1, h.Inventory.Published);
        var state = await h.State(); Assert.Equal("retrying", state.Items[1].State);
        Assert.Equal(1, state.Items[1].Attempts); Assert.False(state.Complete);
        h.Db = new DatabaseManager(h.Directory.Path, NullLogger.Instance); h.Db.Initialise();
        h.Inventory.FailEpisode = 0; h.Now = h.Now.AddHours(8); await h.Run();
        Assert.Contains(2, h.Inventory.Files);
        await h.Run(); Assert.True((await h.State()).Complete);
    }
    [Fact] public async Task MissingPublishedFileIsAutomaticallyRefilled()
    {
        using var h = new Harness(); await h.Seed(); await h.Run(); await h.Run();
        var before = h.Inventory.Resolutions; h.Inventory.Files.Remove(1); await h.Run();
        Assert.Equal(before + 1, h.Inventory.Resolutions); Assert.Contains(1, h.Inventory.Files);
        await h.Run(); Assert.True((await h.State()).Complete);
    }
    [Fact] public async Task ExistingTitleHolesAndNewInventorySlotsAutoRepair()
    {
        using var h = new Harness(); h.Item.StrmPath = "/fake/series"; await h.Seed();
        await h.Run(); Assert.Equal(2, h.Inventory.Published);
        h.Inventory.Count = 3; h.Now = h.Now.AddHours(8); await h.Run();
        Assert.Contains(3, h.Inventory.Files); Assert.Contains(1, h.Inventory.Files);
    }
    [Theory] [InlineData("owned")] [InlineData("blocked")] [InlineData("removed")] [InlineData("cooldown")]
    public async Task PolicyExclusionsDoNotPublish(string cause)
    {
        using var h = new Harness(); await h.Seed();
        if (cause == "owned") h.Inventory.Owned = true;
        if (cause == "blocked") await h.Db.UpsertBlockedItemAsync(h.Item.AioId, null, null, "fixture", "series", "test");
        if (cause == "removed") await h.Db.MarkCatalogItemRemovedAsync(h.Item.AioId, h.Item.Source);
        if (cause == "cooldown") h.Cooldown = h.Now.AddDays(1);
        await h.Run(); Assert.Equal(0, h.Inventory.Resolutions);
    }
    [Fact] public async Task BlockDuringResolutionDefeatsLatePublication()
    {
        using var h = new Harness(); await h.Seed();
        h.Inventory.OnResolve = async () => await h.Db.UpsertBlockedItemAsync(h.Item.AioId, null, null, "fixture", "series", "test");
        await h.Run(); Assert.Equal(0, h.Inventory.Published);
    }
    [Fact] public async Task BlockSurvivesRestartAndStopsRefill()
    {
        using var h = new Harness(); await h.Seed(); await h.Run(); await h.Run();
        await h.Db.UpsertBlockedItemAsync(h.Item.AioId, null, null, "fixture", "series", "test");
        h.Inventory.Files.Remove(1); var before = h.Inventory.Resolutions;
        h.Db = new DatabaseManager(h.Directory.Path, NullLogger.Instance); h.Db.Initialise();
        await h.Seed(); await h.Run();
        Assert.Equal(before, h.Inventory.Resolutions); Assert.DoesNotContain(1, h.Inventory.Files);
    }
    [Fact] public async Task AnotherCatalogStillAuthorizesRefill()
    {
        using var h = new Harness(); await h.Seed(); await h.Run(); await h.Run();
        await h.Db.UpsertCatalogItemAsync(new CatalogItem { AioId = h.Item.AioId, MediaType = "series", Source = "retained-list", Title = h.Item.Title });
        await h.Db.MarkCatalogItemRemovedAsync(h.Item.AioId, h.Item.Source);
        h.Inventory.Files.Remove(1); var before = h.Inventory.Resolutions;
        await h.Run(); Assert.Equal(before + 1, h.Inventory.Resolutions); Assert.Contains(1, h.Inventory.Files);
    }
    [Fact] public async Task PrunedTitleStaysAbsentUntilCatalogReentry()
    {
        using var h = new Harness(); await h.Seed(); await h.Run(); await h.Run();
        await h.Db.SoftDeleteCatalogItemsAsync(new[] { h.Item.AioId });
        h.Inventory.Files.Clear(); var before = h.Inventory.Resolutions;
        await h.Run(); Assert.Equal(before, h.Inventory.Resolutions);
        h.Item.RemovedAt = null; await h.Seed(); await h.Run();
        Assert.Equal(before + 2, h.Inventory.Resolutions);
    }
    [Fact] public async Task MetadataFailureKeepsLastInventoryAndStopsNewWrites()
    {
        using var h = new Harness(); await h.Seed(); await h.Run(ImportMode.Observe);
        h.Now = h.Now.AddHours(8); h.Inventory.BadSnapshot = true; await h.Run();
        Assert.Equal(2, (await h.State()).Items.Count); Assert.Equal("stale_or_unavailable", (await h.State()).SnapshotStatus);
        Assert.Equal(0, h.Inventory.Published);
    }
    [Fact] public async Task PerRunBudgetIsBoundedAndCursorResumes()
    {
        using var h = new Harness(); h.Inventory.Count = 31; await h.Seed(); await h.Run();
        Assert.Equal(20, h.Inventory.Resolutions); await h.Run(); Assert.Equal(31, h.Inventory.Published);
    }
    [Fact] public async Task IndexingHasBoundedNotificationAttempts()
    {
        using var h = new Harness(); h.Inventory.Indexed = false; await h.Seed(); await h.Run();
        for (var n = 0; n < 5; n++) { h.Now = h.Now.AddMinutes(31); await h.Run(); }
        Assert.Equal(3, h.Inventory.Notifications); Assert.All((await h.State()).Items, e => Assert.Equal("indexing_attention", e.State));
    }

    [Fact] public async Task ConflictingProviderAliasesFailClosed()
    {
        using var h = new Harness(); h.Item.TmdbId = "123"; await h.Seed();
        await h.Db.UpsertCatalogItemAsync(new CatalogItem { AioId = "tt999999992", TmdbId = "123", MediaType = "series", Source = "other", Title = "Conflict" });
        await h.Run(); Assert.Equal(0, h.Inventory.Resolutions);
        Assert.Equal("identity_conflict", (await h.State()).SnapshotStatus);
    }

    [Fact] public async Task DueWorkIsSelectedIndependentlyOfParentStateAndBackoffIsHonored()
    {
        using var h = new Harness(); h.Item.ItemState = ItemState.Notified; await h.Seed(); await h.Run(ImportMode.Observe);
        Assert.Single(await h.Db.GetDueImportCatalogAsync(h.Now));
        var state = await h.State(); foreach (var ep in state.Items) ep.NextAttempt = h.Now.AddHours(1);
        await h.Db.SaveImportCoverageAsync(state); Assert.Empty(await h.Db.GetDueImportCatalogAsync(h.Now));
        Assert.Single(await h.Db.GetDueImportCatalogAsync(h.Now.AddHours(2)));
    }

    [Fact] public async Task AliasesShareOneRepairAndDestination()
    {
        using var h = new Harness(); await h.Seed();
        var alias = new CatalogItem { AioId = h.Item.AioId, MediaType = "series", Source = "another", Title = "Other spelling" };
        await h.Db.UpsertCatalogItemAsync(alias);
        await new ImportReconciliationService(h.Db, h.Inventory, () => ImportMode.Repair, TimeZoneInfo.Utc, () => h.Now)
            .RunAsync(default, new[] { h.Item, alias });
        Assert.Equal(2, h.Inventory.Resolutions);
        Assert.Equal((await h.Db.GetImportCatalogByIdAsync(h.Item.Id))!.StrmPath, (await h.Db.GetImportCatalogByIdAsync(alias.Id))!.StrmPath);
    }
    [Fact] public async Task RollingDailyBudgetDoesNotResetWithNewWorker()
    {
        using var h = new Harness(); await h.Seed(); await h.Db.EnsureImportCoverageAsync();
        for (var i = 0; i < 200; i++) await h.Db.RecordImportAttemptAsync("prior-" + i, h.Now, default);
        await h.Run(); Assert.Equal(0, h.Inventory.Resolutions);
        h.Now = h.Now.AddDays(1).AddSeconds(1); await h.Run(); Assert.Equal(2, h.Inventory.Resolutions);
    }
    [Fact] public async Task OffAndProviderPauseDoNotDispatch()
    {
        using var h = new Harness(); await h.Seed(); await h.Run(ImportMode.Off);
        Assert.Null(h.Db.GetMetadata("import_last_run"));
        h.Inventory.Paused = true; await h.Run(); Assert.Equal(0, h.Inventory.Resolutions);
    }
    [Fact] public async Task AtomicWriterPreservesNeighborsAndOldVersionsOnFailure()
    {
        using var dir = new TempDir();
        var logger = System.Reflection.DispatchProxy.Create<MediaBrowser.Model.Logging.ILogManager, NullLogProxy>();
        var writer = new StrmFileManager(logger);
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "E10.strm"), "https://example.invalid/ten");
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "E1 - old.strm"), "https://example.invalid/old");
        await writer.WriteOrReplaceStrmFilesAsync(dir.Path, "E1", new(), default);
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "E1 - old.strm")));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "E1 - HD.strm"));
        var versions = new List<SelectedVersion> { new() { Stream = new() { Url = "https://example.invalid/one" } }, new() { Stream = new() { Url = "https://example.invalid/hd" }, VersionLabel = "HD" } };
        await Assert.ThrowsAsync<IOException>(() => writer.WriteOrReplaceStrmFilesAsync(dir.Path, "E1", versions, default));
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "E1 - old.strm")));
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "E10.strm")));
        System.IO.Directory.Delete(System.IO.Path.Combine(dir.Path, "E1 - HD.strm"));
        await writer.WriteOrReplaceStrmFilesAsync(dir.Path, "E1", versions, default);
        Assert.False(File.Exists(System.IO.Path.Combine(dir.Path, "E1 - old.strm")));
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "E10.strm")));
        Assert.Equal(0, await writer.WriteOrReplaceStrmFilesAsync(dir.Path, "E1", versions, default));
    }
    [Fact] public void ImprobabilityRequiresRepairAndAFiniteWindow()
    {
        var config = new PluginConfiguration { ImportRecoveryMode = ImportMode.Repair,
            ImportCatchUpStartedAt = Now.ToString("o"), ImportCatchUpUntil = Now.AddDays(7).ToString("o") };
        Assert.Equal(40000, ImportWorkBudget.For(config, Now).AttemptsPerDay);
        Assert.Equal(64, ImportWorkBudget.For(config, Now).Parallelism);
        Assert.False(ImportWorkBudget.For(config, Now.AddDays(7)).IsCatchUp);
        Assert.False(ImportWorkBudget.For(config, Now.AddSeconds(-1)).IsCatchUp);
        config.ImportCatchUpUntil = Now.AddDays(8).ToString("o");
        Assert.False(ImportWorkBudget.For(config, Now).IsCatchUp);
        config.ImportCatchUpUntil = Now.AddDays(7).ToString("o");
        config.ImportRecoveryMode = ImportMode.Observe;
        Assert.False(ImportWorkBudget.For(config, Now).IsCatchUp);
        config.ImportRecoveryMode = ImportMode.Repair;
        config.ImportCatchUpStartedAt = "invalid";
        Assert.Equal(ImportWorkBudget.Normal, ImportWorkBudget.For(config, Now));
    }
    [Fact] public async Task ImprobabilityRefreshesOncePerWindowAndResumesAfterRestart()
    {
        using var h = new Harness(); h.Inventory.Count = 205;
        h.Inventory.Files.UnionWith(Enumerable.Range(1, 205)); await h.Seed(); h.Engage();
        await h.Run(); Assert.Equal(200, h.Inventory.Published);
        Assert.Single(await h.Db.GetCatchUpImportCatalogAsync(Now, h.Now));
        h.Db = new DatabaseManager(h.Directory.Path, NullLogger.Instance); h.Db.Initialise();
        h.Now = h.Now.AddHours(2); await h.Run(); Assert.Equal(205, h.Inventory.Published);
        await h.Run(); Assert.Equal(205, h.Inventory.Published);
        Assert.Empty(await h.Db.GetCatchUpImportCatalogAsync(Now, h.Now));
        h.Now = h.Now.AddMinutes(1); h.Engage(); await h.Run();
        Assert.Equal(405, h.Inventory.Published); // Due work precedes the observation cursor.
        await h.Run(); Assert.Equal(410, h.Inventory.Published);
    }
    [Fact] public async Task ImprobabilityHasABoundedAttemptAllowance()
    {
        using var h = new Harness(); h.Inventory.Count = 80;
        h.Inventory.Files.UnionWith(Enumerable.Range(1, 80)); await h.Seed(); h.Engage();
        var limit = ImportWorkBudget.For(h.Config, h.Now) with { AttemptsPerSlice = 37, UpgradesPerSlice = 37 };
        var worker = new ImportReconciliationService(h.Db, h.Inventory, () => ImportMode.Repair,
            TimeZoneInfo.Utc, () => h.Now, workBudget: () => limit);
        await worker.RunAsync(default, new[] { h.Item }); Assert.Equal(37, h.Inventory.Resolutions);
        await worker.RunAsync(default, new[] { h.Item }); Assert.Equal(74, h.Inventory.Resolutions);
    }
    [Fact] public async Task ImprobabilityRefreshesIndexedSiblingsWithoutBypassingFailedGapBackoff()
    {
        using var h = new Harness(); h.Inventory.Count = 10; h.Inventory.FailEpisode = 1;
        h.Inventory.Files.UnionWith(Enumerable.Range(2, 9)); await h.Seed(); h.Engage();
        await h.Run(); Assert.Equal(9, h.Inventory.Published); Assert.Equal(10, h.Inventory.Resolutions);
        var next = (await h.State()).Items.Single(x => x.Episode == 1).NextAttempt;
        h.Now = h.Now.AddHours(1); await h.Run(); Assert.Equal(10, h.Inventory.Resolutions);
        Assert.Equal(next, (await h.State()).Items.Single(x => x.Episode == 1).NextAttempt);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DisengagingOrExpiringDuringResolutionPreventsPublication(bool expire)
    {
        using var h = new Harness(); await h.Seed(); h.Engage();
        h.Inventory.OnResolve = () => { if (expire) h.Now = h.Now.AddDays(7);
            else h.Config.ImportCatchUpUntil = ""; return Task.CompletedTask; };
        await h.Run(); Assert.Equal(1, h.Inventory.Resolutions); Assert.Equal(0, h.Inventory.Published);
    }
    [Fact] public async Task ImprobabilityUsesSharedRollingLedgerAndDoesNotEraseItOnDisengage()
    {
        using var h = new Harness(); await h.Seed(); await h.Db.EnsureImportCoverageAsync();
        for (var i = 0; i < 200; i++) await h.Db.RecordImportAttemptAsync("prior-" + i, h.Now, default);
        h.Engage(); await h.Run(); Assert.Equal(2, h.Inventory.Resolutions);
        h.Config.ImportCatchUpUntil = ""; h.Now = h.Now.AddHours(2);
        h.Db = new DatabaseManager(h.Directory.Path, NullLogger.Instance); h.Db.Initialise();
        await h.Run(); Assert.Equal(2, h.Inventory.Resolutions);
        Assert.Equal(202, await h.Db.GetRecentImportAttemptsAsync(h.Now));
    }
    [Fact] public async Task ImprobabilityDailyCeilingSurvivesRestart()
    {
        using var h = new Harness(); await h.Seed(); await h.Db.EnsureImportCoverageAsync();
        // Real persisted ledger, populated in one fixture transaction.
        using (var conn = SQLitePCL.pretty.SQLite3.Open(System.IO.Path.Combine(h.Directory.Path, "infinitedrive.db"),
            SQLitePCL.pretty.ConnectionFlags.ReadWrite, null, true))
        {
            using var statement = conn.PrepareStatement("WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<39999) INSERT INTO import_attempts SELECT 'prior-'||x, '2026-09-29T18:00:00.0000000+00:00' FROM n;");
            statement.MoveNext();
        }
        h.Engage(); await h.Run(); Assert.Equal(1, h.Inventory.Resolutions);
        h.Db = new DatabaseManager(h.Directory.Path, NullLogger.Instance); h.Db.Initialise();
        await h.Run(); Assert.Equal(1, h.Inventory.Resolutions);
        Assert.Equal(40000, await h.Db.GetRecentImportAttemptsAsync(h.Now));
    }
    [Fact] public async Task ImprobabilityHonorsProviderPauseAndOwnership()
    {
        using var h = new Harness(); await h.Seed(); h.Engage();
        h.Inventory.Paused = true; await h.Run(); Assert.Equal(0, h.Inventory.Resolutions);
        h.Inventory.Paused = false; h.Inventory.Owned = true; await h.Run();
        Assert.Equal(0, h.Inventory.Published);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CatchUpLookupsOverlapButPublicationIsSerialAndCheckpointsAllSiblings(bool existingFiles)
    {
        using var h = new Harness(); h.Inventory.Count = 80;
        if (existingFiles) h.Inventory.Files.UnionWith(Enumerable.Range(1, 80));
        await h.Seed(); h.Engage();
        var full = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var maximum = 0;
        h.Inventory.OnResolve = async () =>
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            if (current == 64) full.TrySetResult(true);
            await release.Task;
            Interlocked.Decrement(ref active);
        };
        var run = h.Run();
        await full.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, h.Inventory.Published);
        release.SetResult(true); await run;
        Assert.Equal(64, maximum); Assert.Equal(80, h.Inventory.Published);
        Assert.All((await h.State()).Items, e => { Assert.Equal(h.Now, e.LastVersionRefresh); Assert.Null(e.Lease); });
        await h.Run(); Assert.Equal(80, h.Inventory.Published);
    }
    [Fact] public async Task GenerationChangeRejectsEveryQueuedPublication()
    {
        using var h = new Harness(); h.Inventory.Count = 64;
        h.Inventory.Files.UnionWith(Enumerable.Range(1, 64)); await h.Seed(); h.Engage();
        var full = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Inventory.OnResolve = async () => { if (h.Inventory.Resolutions == 64) full.TrySetResult(true); await release.Task; };
        var run = h.Run(); await full.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var state = await h.State(); state.Generation++; await h.Db.SaveImportCoverageAsync(state);
        release.SetResult(true); await run;
        Assert.Equal(0, h.Inventory.Published);
    }
    [Fact] public async Task ExistingFilesRefreshWhileNativeIndexingIsPending()
    {
        using var h = new Harness(); h.Inventory.Indexed = false;
        h.Inventory.Files.UnionWith(new[] { 1, 2 }); await h.Seed(); h.Engage();
        await h.Run(); Assert.Equal(2, h.Inventory.Published);
        Assert.All((await h.State()).Items, e => Assert.Equal("awaiting_indexing", e.State));
        await h.Run(); Assert.Equal(2, h.Inventory.Published);
    }
    [Fact] public async Task CatchUpRepairsMissingEpisodesBeyondTheNormalTwentyAttemptLimit()
    {
        using var h = new Harness(); h.Inventory.Count = 80; await h.Seed(); h.Engage();
        await h.Run(); Assert.Equal(80, h.Inventory.Resolutions);
        Assert.Equal(80, h.Inventory.Published);
        await h.Run(); Assert.Equal(80, h.Inventory.Resolutions);
    }
    [Fact] public async Task CatchUpMissingFillsStillShareTheRunAttemptCeiling()
    {
        using var h = new Harness(); h.Inventory.Count = 80; await h.Seed(); h.Engage();
        var limit = ImportWorkBudget.For(h.Config, h.Now) with { AttemptsPerSlice = 37 };
        var worker = new ImportReconciliationService(h.Db, h.Inventory, () => ImportMode.Repair,
            TimeZoneInfo.Utc, () => h.Now, workBudget: () => limit);
        await worker.RunAsync(default, new[] { h.Item });
        Assert.Equal(37, h.Inventory.Resolutions); Assert.Equal(37, h.Inventory.Published);
        await worker.RunAsync(default, new[] { h.Item });
        Assert.Equal(74, h.Inventory.Resolutions); Assert.Equal(74, h.Inventory.Published);
    }
    [Fact] public async Task ReadyResultsPublishBeforeTheNextInventoryObservationFinishes()
    {
        using var h = new Harness(); await h.Seed(); h.Engage();
        var laterObservation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Inventory.OnObserve = async ep =>
        {
            if (ep.Episode != 2) return;
            laterObservation.TrySetResult(true);
            await release.Task;
        };
        var run = h.Run();
        try
        {
            await laterObservation.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, h.Inventory.Published);
        }
        finally { release.TrySetResult(true); await run; }
        Assert.Equal(2, h.Inventory.Published);
    }
    [Fact] public async Task CancellationJoinsAllLookupsBeforeReturningAndKeepsOldFiles()
    {
        using var h = new Harness(); h.Inventory.Count = 64;
        h.Inventory.Files.UnionWith(Enumerable.Range(1, 64)); await h.Seed(); h.Engage();
        using var cancel = new CancellationTokenSource();
        var full = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Inventory.OnResolveWithToken = async ct => { if (h.Inventory.Resolutions == 64) full.TrySetResult(true); await Task.Delay(Timeout.Infinite, ct); };
        var run = new ImportReconciliationService(h.Db, h.Inventory, () => ImportMode.Repair,
            TimeZoneInfo.Utc, () => h.Now, workBudget: () => ImportWorkBudget.For(h.Config, h.Now))
            .RunAsync(cancel.Token, new[] { h.Item });
        await full.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); await run;
        Assert.Equal(0, h.Inventory.Published); Assert.Equal(64, h.Inventory.Files.Count);
        Assert.Contains("cancelled", h.Db.GetMetadata("import_last_run"));
    }

    [Fact] public void ExactSizeIsKeptSeparateFromRoundedDescription()
    {
        var stream = StreamParser.ParseSingle(new AioStreamsStream { Url = "https://example.invalid/exact",
            Description = "4K 39 GB", BehaviorHints = new() { VideoSize = 40_000_000_001 } });
        Assert.Equal(40_000_000_001, stream!.SizeBytes);
        var rounded = StreamParser.ParseSingle(new AioStreamsStream { Url = "https://example.invalid/rounded", Description = "4K 39 GB" });
        Assert.Null(rounded!.SizeBytes);
    }
    [Fact] public void PerFileEvidenceMatchesActualUrlAndOmitsPrivateTargets()
    {
        using var dir = new TempDir();
        var one = System.IO.Path.Combine(dir.Path, "e1.strm"); var two = System.IO.Path.Combine(dir.Path, "e1 - HD.strm");
        File.WriteAllText(one, "https://example.invalid/one"); File.WriteAllText(two, "https://example.invalid/two");
        var evidence = ImportInventory.BuildVersionEvidence(new() { two, one }, new()
        {
            new() { Stream = new() { Url = "https://example.invalid/one", SizeBytes = 123456789, ServiceLabel = "TorBox" } },
            new() { Stream = new() { Url = "https://example.invalid/two", ServiceLabel = "Usenet" } }
        }, Now);
        Assert.Equal("Usenet", evidence[0].ServiceLabel); Assert.Null(evidence[0].SizeBytes);
        Assert.Equal(123456789, evidence[1].SizeBytes); Assert.Equal(one, evidence[1].Path);
        Assert.Equal(64, evidence[1].UrlSha256.Length);
        Assert.DoesNotContain("https://", System.Text.Json.JsonSerializer.Serialize(evidence));
    }

    [Fact] public async Task EmptyMetadataRoundTripsThroughTheRealEmbySqliteProvider()
    {
        using var h = new Harness();
        await h.Db.PersistMetadataAsync("empty-cursor-test", "");
        Assert.Equal("", h.Db.GetMetadata("empty-cursor-test"));
    }
    [Fact] public async Task ScheduledCatchUpResetsItsCursorAndScansExistingFilesBeforeNewIntent()
    {
        using var h = new Harness(); h.Item.StrmPath = "/fake/series";
        h.Inventory.Files.UnionWith(new[] { 1, 2 }); await h.Seed(); h.Engage();
        await h.Db.UpsertCatalogItemAsync(new CatalogItem { AioId = "tt999999993", MediaType = "series", Source = "new-list", Title = "New intent" });
        await h.Db.PersistMetadataAsync("import_catch_up_scan_cursor", "zzzz-stale-prior-window");
        var worker = new ImportReconciliationService(h.Db, h.Inventory, () => ImportMode.Repair,
            TimeZoneInfo.Utc, () => h.Now, workBudget: () => ImportWorkBudget.For(h.Config, h.Now));
        await worker.RunAsync(default);
        Assert.Equal(2, h.Inventory.Resolutions); Assert.Equal(2, h.Inventory.Published);
        Assert.Equal(h.Now.ToString("o"), h.Db.GetMetadata("import_catch_up_scan_window"));
        Assert.Null(await h.Db.GetImportCoverageAsync("series:imdb:tt999999993"));
        await worker.RunAsync(default); Assert.Equal(2, h.Inventory.Published);
    }

    [Fact] public void MissingFutureAndSpecialProviderRowsDoNotBlockConfirmedSiblings()
    {
        var source = new List<ImportEpisode> { Ep(), Ep(2),
            new() { Key = "aired:2:1", Season = 2, Episode = 1, Released = Now.AddDays(1) },
            new() { Key = "aired:0:1", Season = 0, Episode = 1, Released = Now.AddDays(-1) } };
        Assert.Equal("partial_numbering", ImportInventory.ReconcileNumbering(source, new[] { Ep() }));
        Assert.Equal("eligible", ImportCoveragePolicy.Eligibility(source[0], false, Now, TimeZoneInfo.Utc));
        Assert.All(source.Skip(1), e => Assert.NotEqual("eligible", ImportCoveragePolicy.Eligibility(e, false, Now, TimeZoneInfo.Utc)));
        Assert.Equal(4, source.Count);
    }
    [Fact] public void DuplicateProviderEpisodeBlocksOnlyThatKeyAndDoesNotInventASeason()
    {
        var source = new List<ImportEpisode> { Ep(), Ep(2) };
        ImportInventory.ReconcileNumbering(source, new[] { Ep(), Ep(), Ep(2), Ep(3) });
        Assert.Equal("conflict", source[0].Numbering);
        Assert.Equal("aired", source[1].Numbering);
        Assert.Equal(2, source.Count); // No provider-only expansion on partial agreement.
    }
    [Fact] public void DuplicateSourceKeysRemainExplicitlyConflictedWithConfirmedSiblingsUsable()
    {
        var source = ImportInventory.NormalizeSourceNumbering(new[] { Ep(), Ep(), Ep(2) });
        Assert.True(ImportCoveragePolicy.AcceptSnapshot(new List<ImportEpisode>(), source));
        ImportInventory.ReconcileNumbering(source, new[] { Ep(), Ep(2) });
        Assert.Equal("conflict", source[0].Numbering); Assert.Equal("aired", source[1].Numbering);
    }
    [Fact] public void UnverifiedAnimeAndDisjointInventoriesNeverForceMatches()
    {
        var source = new List<ImportEpisode> { Ep() }; source[0].Numbering = "unverified";
        ImportInventory.ReconcileNumbering(source, new[] { Ep() });
        Assert.Equal("unverified", source[0].Numbering);
        source = new() { Ep() }; ImportInventory.ReconcileNumbering(source, new[] { Ep(2) });
        Assert.Single(source); Assert.Equal("unconfirmed", source[0].Numbering);
    }
    [Fact] public void ProviderOnlyEpisodesRequireCompleteSourceAgreement()
    {
        var source = new List<ImportEpisode> { Ep() };
        Assert.Equal("success", ImportInventory.ReconcileNumbering(source, new[] { Ep(), Ep(2) }));
        Assert.Equal(new[] { 1, 2 }, source.Select(x => x.Episode!.Value));
    }
    [Fact] public async Task PartialNumberingPublishesConfirmedEpisodesAndKeepsDisputedOnesExcluded()
    {
        using var h = new Harness(); await h.Seed(); h.Inventory.DisputedEpisode = 2;
        await h.Run(); Assert.Equal(1, h.Inventory.Published);
        var state = await h.State(); Assert.Equal("success", state.SnapshotStatus);
        Assert.Equal("partial_numbering", state.ProviderStatus);
        Assert.Equal("numbering_conflict", state.Items.Single(x => x.Episode == 2).Eligibility);
    }
    [Fact] public async Task LegacyWholeSeriesGateIsRecheckedWithoutResettingRetriesOrIdentityExclusions()
    {
        using var h = new Harness(); await h.Seed(); await h.Run(ImportMode.Observe);
        var state = await h.State(); state.SnapshotStatus = "identity_conflict";
        state.ProviderStatus = "numbering_conflict"; state.InventoryPolicyVersion = 0;
        state.MetadataRetryAt = h.Now.AddHours(6);
        state.Items[0].NextAttempt = h.Now.AddHours(2); state.Items[0].Attempts = 3;
        await h.Db.SaveImportCoverageAsync(state); h.Inventory.DisputedEpisode = 2;
        await h.Run(); state = await h.State(); Assert.Equal("success", state.SnapshotStatus);
        Assert.Equal(1, state.InventoryPolicyVersion); Assert.Equal(3, state.Items[0].Attempts);
        Assert.Equal(h.Now.AddHours(2), state.Items[0].NextAttempt);
        Assert.Equal(0, h.Inventory.Resolutions);
    }
    [Fact] public async Task GenuineIdentityConflictDoesNotBecomeAOneTimeNumberingRetry()
    {
        using var h = new Harness(); await h.Seed(); await h.Run(ImportMode.Observe);
        var state = await h.State(); state.SnapshotStatus = "identity_conflict";
        state.ProviderStatus = "success"; state.MetadataRetryAt = h.Now.AddHours(6);
        state.InventoryPolicyVersion = 0; await h.Db.SaveImportCoverageAsync(state);
        await h.Run(); Assert.Equal("identity_conflict", (await h.State()).SnapshotStatus);
        Assert.Equal(0, h.Inventory.Resolutions);
    }
    [Fact] public async Task IndexedOldFilePreservesFailedRefreshReasonAndBackoffUntilSuccess()
    {
        using var h = new Harness(); h.Inventory.Count = 1; h.Inventory.Files.Add(1);
        h.Inventory.FailEpisode = 1; await h.Seed(); h.Engage(); await h.Run();
        var before = (await h.State()).Items.Single(); Assert.Equal("source_unavailable", before.Failure);
        h.Db = new DatabaseManager(h.Directory.Path, NullLogger.Instance); h.Db.Initialise();
        h.Now = h.Now.AddHours(1); await h.Run(); var after = (await h.State()).Items.Single();
        Assert.Equal("indexed", after.State); Assert.Equal(before.Failure, after.Failure);
        Assert.Equal(before.NextAttempt, after.NextAttempt); Assert.Equal(1, h.Inventory.Resolutions);
        Assert.Contains(1, h.Inventory.Files); Assert.Null(after.LastVersionRefresh);
        h.Now = h.Now.AddDays(1); h.Inventory.FailEpisode = 0; await h.Run();
        after = (await h.State()).Items.Single(); Assert.Equal("", after.Failure);
        Assert.Null(after.NextAttempt); Assert.Equal(h.Now, after.LastVersionRefresh);
    }
    [Fact] public async Task ScarceSliceCreditsRotateAcrossMissingAndRefreshWorkAfterRestart()
    {
        using var h = new Harness(); h.Inventory.Count = 10; h.Item.StrmPath = "/fake/series";
        h.Inventory.Files.UnionWith(Enumerable.Range(6, 5)); await h.Seed(); h.Engage();
        var limit = ImportWorkBudget.For(h.Config, h.Now) with { AttemptsPerSlice = 2 };
        Task Run() => new ImportReconciliationService(h.Db, h.Inventory, () => ImportMode.Repair,
            TimeZoneInfo.Utc, () => h.Now, workBudget: () => limit).RunAsync(default, new[] { h.Item });
        await Run(); Assert.All(h.Inventory.ResolvedEpisodes, e => Assert.True(e >= 6));
        Assert.Equal(2, h.Inventory.Published);
        h.Db = new DatabaseManager(h.Directory.Path, NullLogger.Instance); h.Db.Initialise();
        h.Inventory.ResolvedEpisodes.Clear(); await Run();
        Assert.Equal(2, h.Inventory.ResolvedEpisodes.Count);
        Assert.All(h.Inventory.ResolvedEpisodes, e => Assert.True(e < 6));
        Assert.Equal(4, await h.Db.GetRecentImportAttemptsAsync(h.Now));
    }
    [Fact] public async Task NonPriorityLaneBorrowsUnusedCreditsAndRechecksALateBlock()
    {
        using var h = new Harness(); h.Item.StrmPath = "/fake/series";
        await h.Seed(); h.Engage(); await h.Run(); Assert.Equal(2, h.Inventory.Published);
        h.Inventory.Files.Clear(); h.Now = h.Now.AddMinutes(1);
        await h.Db.PersistMetadataAsync("import_catch_up_last_priority", "missing");
        h.Inventory.OnObserve = async ep => { if (ep.Episode == 2)
            await h.Db.UpsertBlockedItemAsync(h.Item.AioId, null, null, "fixture", "series", "test"); };
        var previous = h.Inventory.Resolutions; await h.Run();
        Assert.Equal(previous, h.Inventory.Resolutions);
    }

    [Fact] public async Task AnalyticsSeparatesExplicitHttp429FromEmptySourcesAndKeepsBackoff()
    {
        using var h = new Harness(); h.Inventory.Count = 1; h.Inventory.RateLimit = true;
        await h.Seed(); await h.Run(); var run = ImportRunTelemetry.Current!;
        Assert.Equal(1, run.Http429); Assert.Equal(0, run.EmptyResults);
        Assert.Equal(0, run.TransportFailures); Assert.Equal(0, run.Published);
        Assert.Equal(0, run.ActiveLookups); Assert.Equal(1, run.MissingAttempts);
        var episode = (await h.State()).Items.Single(); Assert.Equal("http_429", episode.Failure);
        Assert.True(episode.NextAttempt > h.Now); Assert.Single(await h.Db.GetImportRunHistoryAsync());
    }
    [Fact] public async Task AnalyticsDoesNotCallAnUncancelledHttpOperationTimeoutALookupDeadline()
    {
        using var h = new Harness(); h.Inventory.Count = 1;
        h.Inventory.OnResolve = () => throw new OperationCanceledException();
        await h.Seed(); await h.Run(); var run = ImportRunTelemetry.Current!;
        Assert.Equal(1, run.TransportFailures); Assert.Equal(0, run.LookupDeadlines);
        Assert.Equal(0, run.ActiveLookups); Assert.Equal("transport_failure", (await h.State()).Items.Single().Failure);
    }

    [Fact] public async Task RecoveryQueueIsDurableIdempotentAndDoesNotResetBackoffOrLedger()
    {
        using var h = new Harness(); h.Inventory.Count = 1; h.Inventory.FailEpisode = 1;
        await h.Seed(); await h.Run(); var before = (await h.State()).Items.Single();
        Assert.Equal("queued_or_deferred", await h.Db.QueueImportReprocessAsync(h.Now));
        Assert.Equal("already_queued", await h.Db.QueueImportReprocessAsync(h.Now));
        h.Db = new DatabaseManager(h.Directory.Path, NullLogger.Instance); h.Db.Initialise();
        Assert.Equal(1, (await h.Db.GetImportReprocessSummaryAsync()).Pending);
        Assert.Equal(before.NextAttempt, (await h.State()).Items.Single().NextAttempt);
        Assert.Equal(1, await h.Db.GetRecentImportAttemptsAsync(h.Now));
        h.Inventory.FailEpisode = 0; await h.Run();
        Assert.Equal(2, h.Inventory.Resolutions); Assert.Equal(1, (await h.Db.GetImportReprocessSummaryAsync()).Published);
        Assert.Null((await h.State()).Items.Single().NextAttempt);
        Assert.Equal(2, await h.Db.GetRecentImportAttemptsAsync(h.Now));
        await h.Run(); Assert.Equal(2, h.Inventory.Resolutions);
    }

    [Fact] public async Task RecoveryProbeIsBoundedUntilResponsesAreHealthyAndPreservesFiles()
    {
        using var h = new Harness(); h.Inventory.Count = 5; h.Inventory.Files.UnionWith(Enumerable.Range(1,5));
        await h.Seed(); await h.Run(ImportMode.Observe);
        var state = await h.State(); foreach(var e in state.Items)
        { e.Failure = "source_unavailable"; e.Attempts = 2; e.NextAttempt = h.Now.AddDays(1); }
        await h.Db.SaveImportCoverageAsync(state); await h.Db.QueueImportReprocessAsync(h.Now);
        await h.Run(); Assert.Equal(1, h.Inventory.Resolutions); Assert.Equal(4, (await h.Db.GetImportReprocessSummaryAsync()).Pending);
        Assert.Equal(5, h.Inventory.Files.Count);
        var health = h.Db.GetImportSourceHealth(); health.Record("",h.Now); health.Record("",h.Now);
        await h.Db.SaveImportSourceHealthAsync(health); await h.Run();
        Assert.Equal(0, (await h.Db.GetImportReprocessSummaryAsync()).Pending);
        Assert.Equal(5, (await h.Db.GetImportReprocessSummaryAsync()).Published);
    }

    [Fact] public async Task RecoveryPreservesBlocksOwnershipCooldownAndDoesNotTouchExcludedEpisodes()
    {
        using var h = new Harness(); h.Inventory.Count = 2; await h.Seed(); await h.Run(ImportMode.Observe);
        var state = await h.State(); foreach(var e in state.Items)
        { e.Failure = "transport_failure"; e.NextAttempt = h.Now.AddDays(1); }
        state.Items[1].Numbering = "unconfirmed"; state.Items[1].Eligible = false;
        await h.Db.SaveImportCoverageAsync(state); await h.Db.QueueImportReprocessAsync(h.Now);
        Assert.Equal(1, (await h.Db.GetImportReprocessSummaryAsync()).Pending);
        h.Cooldown = h.Now.AddMinutes(30); await h.Run(); Assert.Equal(0, h.Inventory.Resolutions);
        h.Cooldown = DateTimeOffset.MinValue; h.Inventory.Owned = true; await h.Run();
        Assert.Equal(0, h.Inventory.Resolutions); Assert.Equal(1, (await h.Db.GetImportReprocessSummaryAsync()).Cancelled);
        Assert.Equal(h.Now.AddDays(1), (await h.State()).Items[0].NextAttempt);
        Assert.Equal(0, await h.Db.GetRecentImportAttemptsAsync(h.Now));
    }

    [Fact] public async Task SupersededRecoveryIsRetiredAndCancellationLeavesInFlightAlone()
    {
        using var h = new Harness(); h.Inventory.Count = 1; h.Inventory.FailEpisode = 1;
        await h.Seed(); await h.Run(); await h.Db.QueueImportReprocessAsync(h.Now);
        var state = await h.State(); var episode = state.Items.Single();
        state.SnapshotStatus = "stale_or_unavailable"; await h.Db.SaveImportCoverageAsync(state);
        await h.Db.RetireSupersededReprocessAsync(h.Now);
        Assert.Equal(1, (await h.Db.GetImportReprocessSummaryAsync()).Pending);
        state.SnapshotStatus = "success";
        var originalAttempts = episode.Attempts; episode.Failure = "";
        await h.Db.SaveImportCoverageAsync(state); await h.Db.RetireSupersededReprocessAsync(h.Now);
        Assert.Equal(0, (await h.Db.GetImportReprocessSummaryAsync()).Pending);
        Assert.Equal(1, (await h.Db.GetImportReprocessSummaryAsync()).Cancelled);
        Assert.Equal(originalAttempts, (await h.State()).Items.Single().Attempts);
        episode.Failure = "source_unavailable"; await h.Db.SaveImportCoverageAsync(state);
        await h.Db.QueueImportReprocessAsync(h.Now);
        await h.Db.SetImportReprocessStatusAsync(state.Identity, episode.Key, "in_flight", h.Now);
        await h.Db.CancelImportReprocessAsync(h.Now);
        Assert.Equal(1, (await h.Db.GetImportReprocessSummaryAsync()).InFlight);
    }

    [Fact] public async Task FailedRecoveryEscalatesAndAnOpenCircuitPreventsMoreDispatches()
    {
        using var h = new Harness(); h.Inventory.Count = 1; h.Inventory.FailEpisode = 1;
        await h.Seed(); await h.Run(); await h.Db.QueueImportReprocessAsync(h.Now); await h.Run();
        var ep = (await h.State()).Items.Single();
        Assert.Equal(2, ep.ConsecutiveFailures); Assert.Equal(2, ep.Attempts);
        Assert.True(ep.NextAttempt >= h.Now.AddDays(1));
        Assert.Equal(1, (await h.Db.GetImportReprocessSummaryAsync()).Failed);
        await h.Db.QueueImportReprocessAsync(h.Now);
        var health = h.Db.GetImportSourceHealth(); health.Record("http_429", h.Now);
        await h.Db.SaveImportSourceHealthAsync(health); h.Inventory.FailEpisode = 0;
        await h.Run(); Assert.Equal(2, h.Inventory.Resolutions);
        Assert.Equal(1, (await h.Db.GetImportReprocessSummaryAsync()).Pending);
    }

    [Fact] public async Task CancellationCheckpointsLeasesWithoutEscalatingSourceFailures()
    {
        using var h = new Harness(); h.Inventory.Count = 1; await h.Seed(); h.Engage();
        h.Inventory.OnResolveWithToken = ct => Task.Delay(Timeout.Infinite,ct);
        var allowance = ImportWorkBudget.For(h.Config,h.Now) with { SliceSeconds = 1 };
        await new ImportReconciliationService(h.Db,h.Inventory,()=>ImportMode.Repair,TimeZoneInfo.Utc,()=>h.Now,
            workBudget:()=>allowance).RunAsync(default,new[]{h.Item});
        var ep = (await h.State()).Items.Single(); Assert.Null(ep.Lease); Assert.Null(ep.LeaseUntil);
        Assert.Equal("slice_cancelled",ep.Failure); Assert.Equal(0,ep.ConsecutiveFailures);
        Assert.Equal(h.Now.AddMinutes(5),ep.NextAttempt); Assert.Equal(0,h.Db.GetImportSourceHealth().Escalation);
        Assert.Equal(1,await h.Db.GetRecentImportAttemptsAsync(h.Now));
    }

    public class NullLogProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.ReturnType == typeof(MediaBrowser.Model.Logging.ILogger))
                return Create<MediaBrowser.Model.Logging.ILogger, NullLogProxy>();
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    private static async Task SeedDiversity(Harness h, int count = 1)
    {
        h.Inventory.Count = count; h.Engage();
        foreach (var n in Enumerable.Range(1, count)) h.Inventory.Files.Add(n);
        await h.Seed(); await h.Run(ImportMode.Observe);
        var c = await h.State();
        foreach (var e in c.Items)
        {
            e.LastVersionRefresh = h.Now; e.Versions = new() { new(e.Paths[0], "saved", "TorBox", "1080p", null, null, h.Now) };
            ImportDiversityPolicy.Reconcile(c, e, h.Inventory.ProfileFingerprint, h.Now);
            e.Diversity!.NextAttempt = h.Now;
        }
        await h.Db.SaveImportCoverageAsync(c);
    }
    [Fact] public async Task DiversitySameSourcePersistsWithoutLoopOrRepairFailure()
    {
        using var h = new Harness(); await SeedDiversity(h); h.Inventory.Service = "TorBox";
        await h.Run(); var e = (await h.State()).Items[0];
        Assert.Equal(1, h.Inventory.Resolutions); Assert.Equal(0, h.Inventory.Published);
        Assert.Equal("same_source", e.Diversity!.Reason); Assert.Equal("", e.Failure); Assert.Null(e.NextAttempt);
        h.Db = new DatabaseManager(h.Directory.Path, NullLogger.Instance); h.Db.Initialise();
        await h.Run(); Assert.Equal(1, h.Inventory.Resolutions); Assert.Equal(1, await h.Db.GetRecentImportAttemptsAsync(h.Now));
    }
    [Fact] public async Task DiversityAddsSecondSourceWithoutReplacementAndCompletesOnlyThatEpisode()
    {
        using var h = new Harness(); await SeedDiversity(h, 2); h.Inventory.Service = "Usenet";
        await h.Run(); var items = (await h.State()).Items;
        Assert.Equal(1, h.Inventory.Added); Assert.Equal(0, h.Inventory.Published);
        Assert.Equal(2, items[0].Paths.Count); Assert.Equal("resolved", items[0].Diversity!.Status);
        Assert.Equal("waiting", items[1].Diversity!.Status); Assert.Equal(0, items[0].Attempts);
    }
    [Fact] public async Task DiversityWaitsForCooldownAndDisabledSettingAndExhaustedCredit()
    {
        using var h = new Harness(); await SeedDiversity(h); h.Inventory.Service = "Usenet";
        h.Cooldown = h.Now.AddHours(1); await h.Run(); Assert.Equal(0, h.Inventory.Resolutions);
        h.Cooldown = DateTimeOffset.MinValue; h.Config.ImportProviderDiversityEnabled = false;
        await h.Run(); Assert.Equal(0, h.Inventory.Resolutions);
        h.Config.ImportProviderDiversityEnabled = true;
        await new ImportReconciliationService(h.Db,h.Inventory,()=>ImportMode.Repair,TimeZoneInfo.Utc,()=>h.Now,
            workBudget:()=>new ImportWorkBudget(120,20,5,5,0,h.Now,h.Now.AddDays(7))).RunAsync(default,new[]{h.Item});
        Assert.Equal(0,h.Inventory.Resolutions); Assert.Equal("waiting",(await h.State()).Items[0].Diversity!.Status);
    }
    [Fact] public async Task DiversityProfileChangeAndBlockInFlightPreventPublication()
    {
        using var h = new Harness(); await SeedDiversity(h); h.Inventory.Service = "Usenet";
        h.Inventory.OnResolve = () => { h.Inventory.ProfileFingerprint = "changed"; return Task.CompletedTask; };
        await h.Run(); Assert.Equal(0,h.Inventory.Added);
        h.Inventory.OnResolve = null; var c = await h.State();
        ImportDiversityPolicy.Reconcile(c,c.Items[0],h.Inventory.ProfileFingerprint,h.Now); c.Items[0].Diversity!.NextAttempt=h.Now; await h.Db.SaveImportCoverageAsync(c);
        h.Inventory.OnResolve = async()=>{await h.Db.UpsertBlockedItemAsync(h.Item.AioId,null,null,"fixture","series","test");};
        await h.Run(); Assert.Equal(0,h.Inventory.Added);
    }
    [Fact] public async Task DiversityRateLimitKeepsWorkingSourceAndSeparateBackoff()
    {
        using var h = new Harness(); await SeedDiversity(h); h.Inventory.RateLimit=true;
        await h.Run(); var e=(await h.State()).Items[0];
        Assert.Equal("http_429",e.Diversity!.Reason); Assert.True(e.Diversity.NextAttempt>h.Now);
        Assert.Equal("",e.Failure); Assert.Single(e.Paths); Assert.True(h.Db.GetImportSourceHealth().Paused(h.Now));
    }
    [Fact] public async Task DiversityEssentialAttemptWinsAndStaleHealthAllowsOnlyOneProbe()
    {
        using var h = new Harness(); await SeedDiversity(h,2); h.Inventory.Service="Usenet";
        var c=await h.State(); c.Items[1].Paths.Clear();c.Items[1].State="missing";c.Items[1].LastVersionRefresh=null;
        h.Inventory.Files.Remove(2);await h.Db.SaveImportCoverageAsync(c);
        await h.Run();Assert.Equal(1,h.Inventory.Published);Assert.Equal(0,h.Inventory.Added);
        Assert.Equal(new[]{2},h.Inventory.ResolvedEpisodes);
    }

    private sealed class Harness : IDisposable
    {
        public TempDir Directory = new(); public DatabaseManager Db; public FakeInventory Inventory = new();
        public DateTimeOffset Now = ImportReconciliationTests.Now, Cooldown = DateTimeOffset.MinValue;
        public CatalogItem Item = new() { AioId = "tt999999991", MediaType = "series", Source = "test", Title = "Import QA" };
        public Harness() { Db = new(Directory.Path, NullLogger.Instance); Db.Initialise(); }
        public PluginConfiguration Config = new() { ImportRecoveryMode = ImportMode.Repair };
        public void Engage() { Config.ImportCatchUpStartedAt = Now.ToString("o"); Config.ImportCatchUpUntil = Now.AddDays(7).ToString("o"); }
        public Task Seed() => Db.UpsertCatalogItemAsync(Item);
        public Task Run(ImportMode mode = ImportMode.Repair) => new ImportReconciliationService(Db, Inventory, () => mode, TimeZoneInfo.Utc, () => Now, () => Cooldown, () => ImportWorkBudget.For(Config, Now), () => Config.ImportProviderDiversityEnabled).RunAsync(default, new[] { Item });
        public async Task<ImportCoverage> State() => (await Db.GetImportCoverageAsync("series:imdb:tt999999991"))!;
        public void Dispose() => Directory.Dispose();
    }
    private sealed class FakeInventory : IImportInventory
    {
        public int Resolutions, Published, Notifications, FailEpisode, DisputedEpisode, Count = 2, Added;
        public string Service = ""; public string ProfileFingerprint { get; set; } = "fixture";
        public Dictionary<int,string> Additions = new();
        public bool HasSavedFiles(ImportEpisode episode) => episode.Episode.HasValue && Files.Contains(episode.Episode.Value);
        public List<int> ResolvedEpisodes = new();
        public bool Owned, BadSnapshot, Paused, RateLimit, Indexed = true; public bool ProviderPaused => Paused; public HashSet<int> Files = new(); public Func<Task>? OnResolve; public Func<CancellationToken, Task>? OnResolveWithToken; public Func<ImportEpisode, Task>? OnObserve;
        public Task<ImportSnapshot> FetchAsync(CatalogItem item, CancellationToken ct)
        {
            var episodes = BadSnapshot ? new List<ImportEpisode>() : Enumerable.Range(1, Count).Select(Ep).ToList();
            if (DisputedEpisode > 0) foreach (var ep in episodes.Where(x => x.Episode == DisputedEpisode)) ep.Numbering = "unconfirmed";
            return Task.FromResult(new ImportSnapshot(episodes, DisputedEpisode > 0 ? "partial_numbering" : "success"));
        }
        public async Task<ImportObservation> ObserveAsync(CatalogItem item, ImportEpisode ep, CancellationToken ct)
        {
            if (OnObserve != null) await OnObserve(ep);
            var paths = Files.Contains(ep.Episode!.Value) ? new List<string> { $"/fake/series/Season 01/e{ep.Episode}.strm" } : new();
            if (Additions.TryGetValue(ep.Episode.Value,out var addition)) paths.Add(addition);
            return new ImportObservation(paths, Files.Contains(ep.Episode.Value) && Indexed ? new() { ep.Key } : new(), false);
        }
        public bool IsOwned(CatalogItem item) => Owned;
        public async Task<List<SelectedVersion>> ResolveAsync(CatalogItem item, ImportEpisode ep, CancellationToken ct)
        { Resolutions++; ResolvedEpisodes.Add(ep.Episode!.Value); if (RateLimit) throw new ImportHttpRateLimitException(); if (OnResolve != null) await OnResolve(); if (OnResolveWithToken != null) await OnResolveWithToken(ct); return ep.Episode == FailEpisode ? new() : new() { new() { Stream = new() { Url = "https://example.invalid/test", ServiceLabel=Service, SizeBytes=100 } } }; }
        public Task<List<string>> PublishAsync(CatalogItem item, ImportEpisode ep, List<SelectedVersion> versions, CancellationToken ct)
        { Published++; Files.Add(ep.Episode!.Value); return Task.FromResult(new List<string> { $"/fake/series/Season 01/e{ep.Episode}.strm" }); }
        public Task<List<string>> PublishAdditionAsync(CatalogItem item,ImportEpisode ep,List<SelectedVersion> versions,CancellationToken ct)
        {
            Added++; var path=$"/fake/series/Season 01/e{ep.Episode} - addition.strm";Additions[ep.Episode!.Value]=path;
            ep.Versions.Add(new(path,"additional",versions[0].Stream.ServiceLabel,"1080p",null,100,Now));
            return Task.FromResult(ep.Paths.Append(path).ToList());
        }
        public void Notify(CatalogItem item) => Notifications++;
    }
    private sealed class TempDir : IDisposable
    {
        public string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "import-qa-" + Guid.NewGuid().ToString("N"));
        public TempDir() => System.IO.Directory.CreateDirectory(Path);
        public void Dispose() { try { System.IO.Directory.Delete(Path, true); } catch { } }
    }
}
