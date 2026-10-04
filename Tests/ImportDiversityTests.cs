using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using Xunit;

namespace InfiniteDrive.Tests;

public sealed class ImportDiversityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-03T18:00:00Z");
    private static ImportEpisode Single() => new() { Key = "movie", Expected = true, Eligible = true,
        State = "indexed", Paths = new() { "/fixture/one.strm", "/fixture/two.strm" }, Versions = new()
        { new("/fixture/one.strm", "a", "TorBox", "4K", null, null, Now),
          new("/fixture/two.strm", "b", "torbox", "1080p", null, 100, Now),
          new("/retired/old.strm", "c", "Usenet", "720p", null, 100, Now) } };
    private static ImportCoverage Coverage(ImportEpisode ep) => new() { Identity = "movie:imdb:tt99999", SnapshotStatus = "success", Items = new() { ep } };

    [Fact] public void QualityVariantsAndHistoricalSourcesDoNotInflateCoverage()
    {
        var ep = Single(); Assert.Equal(new[] { "TorBox" }, ImportDiversityPolicy.Providers(ep));
        ep.Versions[1] = ep.Versions[1] with { ServiceLabel = "Usenet" };
        Assert.Equal(2, ImportDiversityPolicy.Providers(ep)!.Count);
    }
    [Theory] [InlineData("")] [InlineData("unknown")] [InlineData("https://private.invalid/key")]
    public void UnknownLabelsNeverProveOneProvider(string label)
    { var ep = Single(); ep.Versions[1] = ep.Versions[1] with { ServiceLabel = label }; Assert.Null(ImportDiversityPolicy.Providers(ep)); }
    [Fact] public void ConflictingLabelsAndMissingEvidenceStayUnknown()
    {
        var ep = Single(); ep.Versions.Add(ep.Versions[0] with { ServiceLabel = "Usenet" });
        Assert.Null(ImportDiversityPolicy.Providers(ep)); ep.Versions.Clear(); Assert.Null(ImportDiversityPolicy.Providers(ep));
    }
    [Fact] public void QueueIsIdempotentAndProfileGenerationChangesRestartItsOwnWaitingWindow()
    {
        var ep = Single(); var c = Coverage(ep);
        ImportDiversityPolicy.Reconcile(c, ep, "profile1", Now); var first = ep.Diversity;
        ImportDiversityPolicy.Reconcile(c, ep, "profile1", Now.AddHours(1)); Assert.Same(first, ep.Diversity);
        Assert.Equal(Now.AddHours(6), ep.Diversity!.NextAttempt);
        c.Generation++; ImportDiversityPolicy.Reconcile(c, ep, "profile1", Now.AddHours(1));
        Assert.NotSame(first, ep.Diversity); Assert.Equal(c.Generation, ep.Diversity!.Generation);
        var second = ep.Diversity; ImportDiversityPolicy.Reconcile(c, ep, "profile2", Now.AddHours(2));
        Assert.NotSame(second, ep.Diversity); Assert.Equal("profile2", ep.Diversity!.Profile);
    }
    [Theory] [InlineData("excluded")] [InlineData("index_mismatch")] [InlineData("retrying")]
    public void NonIndexedStatesRetireInsteadOfConsumingDiversityCredits(string state)
    {
        var ep = Single(); var c = Coverage(ep); ImportDiversityPolicy.Reconcile(c, ep, "p", Now);
        ep.State = state; ImportDiversityPolicy.Reconcile(c, ep, "p", Now);
        Assert.Equal("retired", ep.Diversity!.Status);
    }
    [Fact] public void TransientRefreshIndexingPreservesPersistedDueTimeAndBackoff()
    {
        var ep = Single(); var c = Coverage(ep); ImportDiversityPolicy.Reconcile(c, ep, "p", Now);
        ImportDiversityPolicy.Defer(ep.Diversity!, "same_source", Now, 0, DateTimeOffset.MinValue);
        var due = ep.Diversity!.NextAttempt;
        for (var hour = 1; hour <= 8; hour++)
        {
            ep.State = "awaiting_indexing";
            ImportDiversityPolicy.Reconcile(c, ep, "p", Now.AddHours(hour));
            Assert.Equal("waiting", ep.Diversity.Status);
            Assert.Equal("awaiting_indexing", ep.Diversity.Reason);
            Assert.False(ImportDiversityPolicy.Eligible(c, ep));
            Assert.Equal(due, ep.Diversity.NextAttempt); Assert.Equal(1, ep.Diversity.Streak);
            ep.State = "indexed";
            ImportDiversityPolicy.Reconcile(c, ep, "p", Now.AddHours(hour));
            Assert.Equal("single_source", ep.Diversity.Reason);
            Assert.Equal(due, ep.Diversity.NextAttempt); Assert.Equal(1, ep.Diversity.Streak);
        }
        ep.State = "awaiting_indexing"; c.Exclusion = "not_authorized";
        ImportDiversityPolicy.Reconcile(c, ep, "p", Now.AddHours(9));
        Assert.Equal("retired", ep.Diversity.Status);
    }
    [Theory] [InlineData("resolved", true)] [InlineData("resolved", false)]
    [InlineData("retired", true)] [InlineData("retired", false)]
    public void ReenteringSingleSourceAfterRefreshGetsANewDeadline(string status, bool noDeadline)
    {
        var ep = Single(); var c = Coverage(ep); ImportDiversityPolicy.Reconcile(c, ep, "p", Now);
        ep.Diversity!.Status = status;
        ep.Diversity.NextAttempt = noDeadline ? null : Now.AddHours(-1);
        ep.Diversity.Attempts = 3; ep.Diversity.Streak = 2;
        ep.State = "awaiting_indexing";
        ImportDiversityPolicy.Reconcile(c, ep, "p", Now.AddHours(1));
        Assert.Equal("waiting", ep.Diversity.Status);
        Assert.Equal("awaiting_indexing", ep.Diversity.Reason);
        Assert.Equal(Now.AddHours(7), ep.Diversity.NextAttempt);
        Assert.False(ImportDiversityPolicy.Eligible(c, ep));
        ep.State = "indexed";
        ImportDiversityPolicy.Reconcile(c, ep, "p", Now.AddHours(2));
        Assert.Equal("single_source", ep.Diversity.Reason);
        Assert.Equal(Now.AddHours(7), ep.Diversity.NextAttempt);
        Assert.Equal(3, ep.Diversity.Attempts); Assert.Equal(2, ep.Diversity.Streak);
    }
    [Fact] public void ActualFailuresAndBlocksWinOverDiversity()
    {
        var ep = Single(); var c = Coverage(ep); ImportDiversityPolicy.Reconcile(c, ep, "p", Now);
        ep.Failure = "source_unavailable"; ep.NextAttempt = Now.AddDays(3);
        ImportDiversityPolicy.Reconcile(c, ep, "p", Now); Assert.Equal("retired", ep.Diversity!.Status);
        Assert.Equal(Now.AddDays(3), ep.NextAttempt); ep.Failure = ""; c.Exclusion = "not_authorized";
        Assert.False(ImportDiversityPolicy.Eligible(c, ep));
    }
    [Fact] public void BackoffEscalatesWithoutTouchingRepairStateAndHonorsCooldown()
    {
        var ep = Single(); var c = Coverage(ep); ImportDiversityPolicy.Reconcile(c, ep, "p", Now);
        var hours = new[] { 6, 24, 48, 72, 168, 168 };
        foreach (var h in hours) { ImportDiversityPolicy.Defer(ep.Diversity!, "same_source", Now, 0, DateTimeOffset.MinValue); Assert.Equal(Now.AddHours(h), ep.Diversity!.NextAttempt); }
        ImportDiversityPolicy.Defer(ep.Diversity!, "http_429", Now, .2, Now.AddDays(20));
        Assert.Equal(Now.AddDays(20), ep.Diversity!.NextAttempt); Assert.Equal("", ep.Failure); Assert.Null(ep.NextAttempt);
    }
    [Fact] public void InterruptedLeaseRecoversButLiveLeaseDoesNot()
    {
        var ep = Single(); var c = Coverage(ep); ImportDiversityPolicy.Reconcile(c, ep, "p", Now);
        ep.Diversity!.Status = "in_flight"; ep.LeaseUntil = Now.AddMinutes(1);
        ImportDiversityPolicy.Reconcile(c, ep, "p", Now); Assert.Equal("in_flight", ep.Diversity.Status);
        ImportDiversityPolicy.Reconcile(c, ep, "p", Now.AddMinutes(2));
        Assert.Equal("waiting", ep.Diversity.Status); Assert.Equal(Now.AddMinutes(7), ep.Diversity.NextAttempt);
    }
    [Fact] public void CapacityDoesNotSpendARequestOrEraseAnUnknownSizeVersion()
    {
        var ep=Single();var c=Coverage(ep);
        for(var i=2;i<8;i++) {var path=$"/fixture/{i}.strm";ep.Paths.Add(path);ep.Versions.Add(new(path,"hash","TorBox","1080p",null,null,Now));}
        ImportDiversityPolicy.Reconcile(c,ep,"p",Now);
        Assert.Equal("capacity",ep.Diversity!.Status);Assert.Equal(0,ep.Diversity.Attempts);Assert.Equal(8,ep.Paths.Count);
        ep.Paths.RemoveAt(7);ImportDiversityPolicy.Reconcile(c,ep,"p",Now.AddHours(7));
        Assert.Equal("waiting",ep.Diversity.Status);Assert.Equal(Now.AddHours(6),ep.Diversity.NextAttempt);
    }
    [Fact] public void MissingLeaseAfterInterruptionDoesNotStrandEntry()
    {
        var ep=Single();var c=Coverage(ep);ImportDiversityPolicy.Reconcile(c,ep,"p",Now);
        ep.Diversity!.Status="in_flight";ImportDiversityPolicy.Reconcile(c,ep,"p",Now);
        Assert.Equal("waiting",ep.Diversity.Status);Assert.Equal(Now.AddMinutes(5),ep.Diversity.NextAttempt);
    }
    [Fact] public void TelemetrySeparatesDiversityFromMissingAndRefreshAttempts()
    {
        var t=new ImportRunTelemetry("fixture");t.Attempt(false,true);t.Published(false,true);
        var s=t.Snapshot();Assert.Equal(1,s.DiversityAttempts);Assert.Equal(0,s.MissingAttempts);
        Assert.Equal(0,s.RefreshAttempts);Assert.Equal(1,s.DiversityPublished);Assert.Equal(1,s.Published);Assert.Equal(0,s.Refreshed);
    }
    [Fact] public async Task AdditiveWriterPreservesUnknownSizeWorkingFilesAndNeighborsAndIsIdempotent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "diversity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var log = DispatchProxy.Create<MediaBrowser.Model.Logging.ILogManager, ImportReconciliationTests.NullLogProxy>();
            var writer = new StrmFileManager(log);
            var old = Path.Combine(folder, "E1 - TorBox.strm"); File.WriteAllText(old, "https://example.invalid/old");
            var neighbor = Path.Combine(folder, "E10.strm"); File.WriteAllText(neighbor, "https://example.invalid/neighbor");
            var v = new SelectedVersion { VersionLabel = "1080p - Usenet", Stream = new() { Url = "https://example.invalid/new", ServiceLabel = "Usenet", SizeBytes = 100 } };
            var added = await writer.WriteAdditionalStrmAsync(folder, "E1", v, default);
            Assert.Equal(added, await writer.WriteAdditionalStrmAsync(folder, "E1", v, default));
            Assert.Equal(2, ImportInventory.FindFiles(folder, "E1").Count);
            Assert.Equal("https://example.invalid/old", File.ReadAllText(old)); Assert.Equal("https://example.invalid/neighbor", File.ReadAllText(neighbor));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
            v.Stream.SizeBytes = null;
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAdditionalStrmAsync(folder, "E1", v, default));
        }
        finally { Directory.Delete(folder, true); }
    }
}
