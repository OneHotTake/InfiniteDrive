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

namespace InfiniteDrive.Tests;

public sealed class ImportReconciliationTests
{
    static ImportReconciliationTests() => SQLitePCLEx.raw.SetProvider(new SQLitePCLEx.SQLite3Provider_sqlite3());
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T18:00:00Z");
    private static ImportEpisode Ep(int n = 1) => new() { Key = $"aired:1:{n}", Season = 1, Episode = n, Released = Now.AddDays(-1), Eligible = true };

    [Theory]
    [InlineData(false, false, false, false, "missing")]
    [InlineData(true, false, false, false, "awaiting_indexing")]
    [InlineData(true, true, false, false, "indexed")]
    [InlineData(true, true, true, false, "index_mismatch")]
    [InlineData(false, false, false, true, "review_removal")]
    public void FilesAndNativeChildrenAreSeparate(bool file, bool indexed, bool conflict, bool legacy, string state)
        => Assert.Equal(state, ImportCoveragePolicy.Classify(Ep(), file, indexed, conflict, legacy, Now));

    [Fact] public void DeletionNeedsExplicitRestore()
    {
        var ep = Ep(); ep.EverPublished = true;
        Assert.Equal("review_removal", ImportCoveragePolicy.Classify(ep, false, false, false, false, Now));
        ep.RestoreRequested = true;
        Assert.Equal("missing", ImportCoveragePolicy.Classify(ep, false, false, false, true, Now));
        ep.Suppressed = true;
        Assert.Equal("suppressed", ImportCoveragePolicy.Classify(ep, true, true, false, false, Now));
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
    [Fact] public void UnchangedSnapshotRetainsFailureAndSuppression()
    {
        var ep = Ep(); ep.Attempts = 3; ep.Suppressed = true; ep.NextAttempt = Now.AddHours(1);
        var state = new ImportCoverage { Items = new() { ep }, SnapshotAt = Now };
        ImportReconciliationService.MergeSnapshot(state, new() { Ep(), Ep(2) });
        Assert.Same(ep, state.Items[0]); Assert.True(ep.Suppressed); Assert.Equal(3, ep.Attempts);
        Assert.True(state.Items[1].BaselineKnown);
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

    [Fact] public async Task ObserveNeverResolvesOrPublishes()
    {
        using var h = new Harness(); await h.Seed(); await h.Run(ImportMode.Observe);
        Assert.Equal(0, h.Inventory.Resolutions); Assert.Equal(0, h.Inventory.Published);
        Assert.Equal("success", h.Db.GetMetadata("import_observation_baseline"));
        Assert.All((await h.State()).Items, e => Assert.Equal("missing", e.State));
    }
    [Fact] public async Task PartialFailureRetriesAfterRestartWithUnchangedMetadata()
    {
        using var h = new Harness(); await h.Seed(); h.Inventory.FailEpisode = 2;
        await h.Run(); Assert.Equal(1, h.Inventory.Published);
        var state = await h.State(); Assert.Equal("retrying", state.Items[1].State);
        Assert.Equal(1, state.Items[1].Attempts); Assert.False(state.Complete);
        h.Db = new DatabaseManager(h.Directory.Path, NullLogger.Instance); h.Db.Initialise();
        h.Inventory.FailEpisode = 0; h.Now = h.Now.AddHours(7); await h.Run();
        Assert.Contains(2, h.Inventory.Files);
        await h.Run(); Assert.True((await h.State()).Complete);
    }
    [Fact] public async Task MissingPublishedFileDoesNotResurrect()
    {
        using var h = new Harness(); await h.Seed(); await h.Run(); await h.Run();
        var before = h.Inventory.Resolutions; h.Inventory.Files.Remove(1); await h.Run();
        Assert.Equal(before, h.Inventory.Resolutions); Assert.True((await h.State()).Items[0].Suppressed);
    }
    [Fact] public async Task LegacyHolesNeedRestoreButNewInventorySlotsDoNot()
    {
        using var h = new Harness(); h.Item.StrmPath = "/fake/series"; await h.Seed();
        await h.Run(); Assert.Equal(0, h.Inventory.Published); Assert.All((await h.State()).Items, e => Assert.True(e.Suppressed));
        h.Inventory.Count = 3; h.Now = h.Now.AddHours(7); await h.Run();
        Assert.Contains(3, h.Inventory.Files); Assert.DoesNotContain(1, h.Inventory.Files);
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
    [Fact] public async Task SuppressionDuringResolutionDefeatsLatePublication()
    {
        using var h = new Harness(); await h.Seed();
        h.Inventory.OnResolve = async () => { var state = await h.State(); state.Generation++; state.Suppressed = true; await h.Db.SaveImportCoverageAsync(state); };
        await h.Run(); Assert.Equal(0, h.Inventory.Published);
    }
    [Fact] public async Task MetadataFailureKeepsLastInventoryAndStopsNewWrites()
    {
        using var h = new Harness(); await h.Seed(); await h.Run(ImportMode.Observe);
        h.Now = h.Now.AddHours(7); h.Inventory.BadSnapshot = true; await h.Run();
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
    public class NullLogProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.ReturnType == typeof(MediaBrowser.Model.Logging.ILogger))
                return Create<MediaBrowser.Model.Logging.ILogger, NullLogProxy>();
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    private sealed class Harness : IDisposable
    {
        public TempDir Directory = new(); public DatabaseManager Db; public FakeInventory Inventory = new();
        public DateTimeOffset Now = ImportReconciliationTests.Now, Cooldown = DateTimeOffset.MinValue;
        public CatalogItem Item = new() { AioId = "tt999999991", MediaType = "series", Source = "test", Title = "Import QA" };
        public Harness() { Db = new(Directory.Path, NullLogger.Instance); Db.Initialise(); }
        public Task Seed() => Db.UpsertCatalogItemAsync(Item);
        public Task Run(ImportMode mode = ImportMode.Repair) => new ImportReconciliationService(Db, Inventory, () => mode, TimeZoneInfo.Utc, () => Now, () => Cooldown).RunAsync(default, new[] { Item });
        public async Task<ImportCoverage> State() => (await Db.GetImportCoverageAsync("series:imdb:tt999999991"))!;
        public void Dispose() => Directory.Dispose();
    }
    private sealed class FakeInventory : IImportInventory
    {
        public int Resolutions, Published, Notifications, FailEpisode, Count = 2;
        public bool Owned, BadSnapshot, Paused, Indexed = true; public bool ProviderPaused => Paused; public HashSet<int> Files = new(); public Func<Task>? OnResolve;
        public Task<ImportSnapshot> FetchAsync(CatalogItem item, CancellationToken ct) => Task.FromResult(new ImportSnapshot(BadSnapshot ? new() : Enumerable.Range(1, Count).Select(Ep).ToList(), "success"));
        public Task<ImportObservation> ObserveAsync(CatalogItem item, ImportEpisode ep, CancellationToken ct) => Task.FromResult(new ImportObservation(Files.Contains(ep.Episode!.Value) ? new() { $"/fake/series/Season 01/e{ep.Episode}.strm" } : new(), Files.Contains(ep.Episode.Value) && Indexed ? new() { ep.Key } : new(), false));
        public bool IsOwned(CatalogItem item) => Owned;
        public async Task<List<SelectedVersion>> ResolveAsync(CatalogItem item, ImportEpisode ep, CancellationToken ct)
        { Resolutions++; if (OnResolve != null) await OnResolve(); return ep.Episode == FailEpisode ? new() : new() { new() { Stream = new() { Url = "https://example.invalid/test" } } }; }
        public Task<List<string>> PublishAsync(CatalogItem item, ImportEpisode ep, List<SelectedVersion> versions, CancellationToken ct)
        { Published++; Files.Add(ep.Episode!.Value); return Task.FromResult(new List<string> { $"/fake/series/Season 01/e{ep.Episode}.strm" }); }
        public void Notify(CatalogItem item) => Notifications++;
    }
    private sealed class TempDir : IDisposable
    {
        public string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "import-qa-" + Guid.NewGuid().ToString("N"));
        public TempDir() => System.IO.Directory.CreateDirectory(Path);
        public void Dispose() { try { System.IO.Directory.Delete(Path, true); } catch { } }
    }
}
