using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using InfiniteDrive.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging.Abstractions;
using SQLitePCL.pretty;
using Xunit;

namespace InfiniteDrive.Tests;

public sealed class CatalogPruningTests
{
    [Fact]
    public async Task FullUnionCountsOneAbsenceAcrossMoreThanTwoSqlBatches()
    {
        using var h = new Harness(); var absent = await h.Seed("ttabsent"); var present = await h.Seed("tt1000");
        h.Sql("UPDATE catalog_items SET global_absent_syncs=2;");
        var ids = Enumerable.Range(0, 1001).Select(x => "tt" + x).ToHashSet();
        await h.Db.IncrementGlobalAbsentSyncsAsync(ids);
        Assert.Equal(3, (await h.Db.GetImportCatalogByIdAsync(absent.Id))!.GlobalAbsentSyncs);
        Assert.Equal(0, (await h.Db.GetImportCatalogByIdAsync(present.Id))!.GlobalAbsentSyncs);
        await h.Db.IncrementGlobalAbsentSyncsAsync(new());
        Assert.Equal(3, (await h.Db.GetImportCatalogByIdAsync(absent.Id))!.GlobalAbsentSyncs);
    }

    [Fact]
    public async Task InvalidHistoricalCounterIsResetOnlyOnceWithoutChangingUserOrRetryState()
    {
        using var h = new Harness(); var item = await h.Seed("ttlegacy");
        h.Sql("DELETE FROM catalog_pruning_policy; UPDATE catalog_items SET global_absent_syncs=100,retry_count=4,next_retry_at=1900000000,blocked_at='protected';");
        h.Db.Initialise(); var actual = (await h.Db.GetImportCatalogByIdAsync(item.Id))!;
        Assert.Equal(0, actual.GlobalAbsentSyncs); Assert.Equal(4, actual.RetryCount);
        Assert.Equal(1900000000, actual.NextRetryAt); Assert.Equal("protected", actual.BlockedAt);
        Assert.Equal(item.StrmPath, actual.StrmPath); Assert.Null(actual.RemovedAt);
        h.Sql("UPDATE catalog_items SET global_absent_syncs=2;"); h.Db.Initialise();
        Assert.Equal(2, (await h.Db.GetImportCatalogByIdAsync(item.Id))!.GlobalAbsentSyncs);
    }

    [Theory]
    [InlineData("external")]
    [InlineData("owned")]
    [InlineData("blocked")]
    [InlineData("pinned")]
    [InlineData("membership")]
    [InlineData("played")]
    [InlineData("saved")]
    [InlineData("unknown-owner")]
    public async Task ProtectedRowsNeverBecomeAbsentPruneCandidates(string protection)
    {
        using var h = new Harness(); var item = await h.Seed("ttprotected");
        h.Sql("UPDATE catalog_items SET global_absent_syncs=3;");
        h.Sql(protection switch {
            "external" => "UPDATE catalog_items SET source='external_list';",
            "owned" => "UPDATE catalog_items SET local_source='library',local_path='/owned/file.mkv',item_state=3;",
            "blocked" => "UPDATE catalog_items SET blocked_at='protected';",
            "pinned" => "UPDATE catalog_items SET item_state=5;",
            "membership" => "INSERT INTO collection_membership(collection_name,aio_id,source,last_seen,created_at,updated_at) VALUES('Keep','ttprotected','aiostreams','now','now','now');",
            "played" => "INSERT INTO playback_log(id,aio_id,resolution_mode,played_at) VALUES('played','ttprotected','cached','now');",
            "saved" => "INSERT INTO media_items(id,primary_id_type,primary_id,media_type,title,status,saved,created_at,updated_at) VALUES('saved','imdb','ttprotected','movie','Keep','known',1,'now','now');",
            "no-path" => "UPDATE catalog_items SET strm_path=NULL;",
            _ => "UPDATE catalog_items SET local_source='unknown';"
        });
        Assert.Empty(await h.Db.GetAbsentPruneCandidatesAsync());
        Assert.Equal(0, await h.Db.RetireAbsentCatalogItemsAsync(new[] { (item.Id, item.StrmPath, item.AioId) }));
        Assert.Null((await h.Db.GetImportCatalogByIdAsync(item.Id))!.RemovedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task UnpublishedAbsentMetadataRetiresAfterThreeCompleteObservations(string? path)
    {
        using var h = new Harness(); var item=await h.Seed("ttunpublished");
        h.Sql("UPDATE catalog_items SET strm_path="+(path==null ? "NULL" : "''")+",local_path=NULL,local_source=NULL,item_state=6;");
        for (var i=0;i<3;i++) await h.Db.IncrementGlobalAbsentSyncsAsync(new() {"ttpresent"});
        var candidates=await h.Db.GetAbsentPruneCandidatesAsync(); Assert.Single(candidates);
        Assert.Equal(path,candidates[0].StrmPath); Assert.Equal(item.Id,candidates[0].Id);
        Assert.Equal(1,await h.Db.RetireAbsentCatalogItemsAsync(candidates));
        Assert.NotNull((await h.Db.GetImportCatalogByIdAsync(item.Id))!.RemovedAt);
    }

    [Theory]
    [InlineData("external_list", null, 6)]
    [InlineData("aiostreams", "library", 3)]
    [InlineData("aiostreams", null, 5)]
    [InlineData("aiostreams", "unknown", 6)]
    public async Task MetadataOnlyRetirementStillRespectsSourceOwnershipAndIntent(string source,string? owner,int state)
    {
        using var h = new Harness(); var item=await h.Seed("ttprotectedmetadata");
        item.Source=source; item.LocalSource=owner; item.StrmPath=null; item.ItemState=(ItemState)state;
        h.Sql("DELETE FROM catalog_items;"); await h.Db.UpsertCatalogItemAsync(item);
        h.Sql("UPDATE catalog_items SET global_absent_syncs=3;");
        Assert.Empty(await h.Db.GetAbsentPruneCandidatesAsync());
        Assert.Equal(0,await h.Db.RetireAbsentCatalogItemsAsync(new[] {(item.Id,item.StrmPath,item.AioId)}));
    }

    [Fact]
    public async Task MetadataOnlyRowsRetainWatchedAndActiveExternalTmdbAlias()
    {
        using var h=new Harness(); var item=await h.Seed("ttmetadata"); item.TmdbId="456";
        item.LocalSource=null; item.StrmPath=null; await h.Db.UpsertCatalogItemAsync(item);
        // The normal sparse upsert preserves a materialized path; make the fixture truly unpublished.
        h.Sql("UPDATE catalog_items SET strm_path=NULL,local_source=NULL,global_absent_syncs=3;");
        await h.Db.LogPlaybackAsync(new PlaybackEntry { AioId=item.AioId, ResolutionMode="cached" });
        Assert.Empty(await h.Db.GetAbsentPruneCandidatesAsync());
        h.Sql("DELETE FROM playback_log;");
        await h.Db.UpsertCatalogItemAsync(new CatalogItem { AioId="tmdb:456", TmdbId="456", Source="external_list", MediaType="movie",Title="Keep" });
        Assert.Empty(await h.Db.GetAbsentPruneCandidatesAsync());
    }

    [Theory]
    [InlineData("same-imdb", "movie", true)]
    [InlineData("different-imdb", "movie", true)]
    [InlineData("different-imdb", "series", false)]
    public async Task ActiveExternalAliasProtectsMatchingIdentityWithoutTmdbMovieTvCollision(string id, string type, bool protectedTitle)
    {
        using var h = new Harness(); var item = await h.Seed("same-imdb");
        item.TmdbId = "123"; await h.Db.UpsertCatalogItemAsync(item);
        await h.Db.UpsertCatalogItemAsync(new CatalogItem { AioId = id, Source = "external_list", MediaType = type, Title = "List title", TmdbId = "123" });
        h.Sql("UPDATE catalog_items SET global_absent_syncs=3;");
        Assert.Equal(protectedTitle ? 0 : 1, (await h.Db.GetAbsentPruneCandidatesAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatchedOrCollectedTmdbAliasKeepsBothBroadRows(bool collection)
    {
        using var h = new Harness(); var first=await h.Seed("ttfirst"); first.TmdbId="321"; await h.Db.UpsertCatalogItemAsync(first);
        var alias=await h.Seed("tmdb:321"); alias.TmdbId="321"; await h.Db.UpsertCatalogItemAsync(alias);
        h.Sql("UPDATE catalog_items SET global_absent_syncs=3;");
        h.Sql(collection ? "INSERT INTO collection_membership(collection_name,aio_id,source,last_seen,created_at,updated_at) VALUES('Keep','tmdb:321','aiostreams','now','now','now');"
            : "INSERT INTO playback_log(id,aio_id,resolution_mode,played_at) VALUES('played','tmdb:321','cached','now');");
        Assert.Empty(await h.Db.GetAbsentPruneCandidatesAsync());
    }

    [Fact]
    public async Task RetirementRechecksNewProtectionAndNeverDeletesAnotherSource()
    {
        using var h = new Harness(); var item = await h.Seed("ttstale");
        for (int i=0;i<2;i++) { await h.Db.IncrementGlobalAbsentSyncsAsync(new() { "ttother" }); Assert.Empty(await h.Db.GetAbsentPruneCandidatesAsync()); }
        await h.Db.IncrementGlobalAbsentSyncsAsync(new() { "ttother" });
        var candidates = await h.Db.GetAbsentPruneCandidatesAsync(); Assert.Single(candidates);
        var external = new CatalogItem { AioId=item.AioId, Source="external_list", MediaType="movie", Title="Keep" };
        await h.Db.UpsertCatalogItemAsync(external);
        Assert.Equal(0, await h.Db.RetireAbsentCatalogItemsAsync(candidates));
        Assert.Null((await h.Db.GetImportCatalogByIdAsync(item.Id))!.RemovedAt);
        h.Sql("UPDATE catalog_items SET removed_at='old' WHERE source='external_list';");
        Assert.Equal(1, await h.Db.RetireAbsentCatalogItemsAsync(candidates));
        Assert.NotNull((await h.Db.GetImportCatalogByIdAsync(item.Id))!.RemovedAt);
        Assert.Equal("old", (await h.Db.GetImportCatalogByIdAsync(external.Id))!.RemovedAt);
    }

    [Fact]
    public void SkippedProvidersAndPartialCatalogFailuresCannotAuthorizeAbsence()
    {
        Assert.False(CatalogPruningPolicy.CanObserveAbsence(1,2)); Assert.False(CatalogPruningPolicy.CanObserveAbsence(0,0));
        Assert.True(CatalogPruningPolicy.CanObserveAbsence(2,2));
        var result = new CatalogFetchResult { Items = new() { new CatalogItem() } };
        result.CatalogOutcomes["success"] = new() { Succeeded=true };
        Assert.True(CatalogPruningPolicy.IsCompleteProviderSnapshot(result));
        result.CatalogOutcomes["failed"] = new() { Succeeded=false };
        Assert.False(CatalogPruningPolicy.IsCompleteProviderSnapshot(result));
        result.CatalogOutcomes.Remove("failed"); result.ProviderReachable=false;
        Assert.False(CatalogPruningPolicy.IsCompleteProviderSnapshot(result));
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("missing-metas", false)]
    [InlineData("repeat", false)]
    [InlineData("empty", true)]
    [InlineData("cap", true)]
    public async Task PartialPageFailuresRetainFetchedDataWithoutAuthorizingPruning(string ending, bool complete)
    {
        int calls=0; var page = new AioStreamsCatalogResponse { Metas=new() { new AioStreamsMeta { Id="tt1234567", Type="movie", Name="Fixture" } } };
        var result = await AioStreamsCatalogProvider.FetchCatalogPagesAsync(new() { Id="popular", Type="movie" }, NullLogger.Instance,
            ending=="cap" ? 1:2, CancellationToken.None, _ => Task.FromResult(++calls==1 ? page : ending switch {
                "null" => null, "missing-metas" => new AioStreamsCatalogResponse { Metas=null! },
                "repeat" => page, _ => new AioStreamsCatalogResponse { Metas=new() }
            }));
        Assert.Single(result.Items); Assert.Equal(complete, result.Outcome.Succeeded);
    }

    [Theory]
    [InlineData("played")]
    [InlineData("resume")]
    [InlineData("favorite")]
    [InlineData("history")]
    public void LaterNativeVersionAndAnySeriesEpisodeProtectUserState(string state)
    {
        var first = new Movie(); var second = new Movie(); var series = new Series(); var episode = new Episode();
        var user=new User(); var data = state switch { "played"=>new UserItemData { PlayCount=1 }, "resume"=>new UserItemData { PlaybackPositionTicks=1 },
            "favorite"=>new UserItemData { IsFavorite=true }, _=>new UserItemData { LastPlayedDate=DateTimeOffset.UtcNow } };
        Assert.True(CatalogPruningPolicy.HasProtectedNativeItems(new BaseItem[] {first,second}, new[] {user}, _=>Array.Empty<BaseItem>(),
            (_, item)=>ReferenceEquals(item,second) ? data:new(), _=>false));
        Assert.True(CatalogPruningPolicy.HasProtectedNativeItems(new BaseItem[] {series}, new[] {user}, _=>new BaseItem[] {episode},
            (_, item)=>ReferenceEquals(item,episode) ? data:new(), _=>false));
    }

    [Fact]
    public void UnknownNativeEvidenceAndOwnedMatchesRetainButUnwatchedManagedMatchesAreEligible()
    {
        var items=new BaseItem[] {new Movie()}; var users=new[] {new User()};
        Assert.True(CatalogPruningPolicy.HasProtectedNativeItems(null,users,_=>Array.Empty<BaseItem>(),(_,_)=>new(),_=>false));
        Assert.True(CatalogPruningPolicy.HasProtectedNativeItems(items,users,_=>Array.Empty<BaseItem>(),(_,_)=>throw new IOException(),_=>false));
        Assert.True(CatalogPruningPolicy.HasProtectedNativeItems(items,users,_=>Array.Empty<BaseItem>(),(_,_)=>new(),_=>true));
        Assert.False(CatalogPruningPolicy.HasProtectedNativeItems(items,users,_=>Array.Empty<BaseItem>(),(_,_)=>new(),_=>false));
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _path=Path.Combine(Path.GetTempPath(),"pruning-"+Guid.NewGuid().ToString("N"));
        public DatabaseManager Db {get;}
        public Harness() { SqliteTestRuntime.EnsureInitialized(); Db=new(_path,NullLogger.Instance); Db.Initialise(); }
        public async Task<CatalogItem> Seed(string id) { var item=new CatalogItem { AioId=id, Source="aiostreams", Title="Fixture", MediaType="movie",
            LocalSource="strm", StrmPath="/managed/movies/"+id, ItemState=ItemState.Ready }; await Db.UpsertCatalogItemAsync(item); return item; }
        public void Sql(string sql) { using var c=SQLite3.Open(Path.Combine(_path,"infinitedrive.db"),ConnectionFlags.ReadWrite,null,true); foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries)) c.Execute(statement+";"); }
        public void Dispose() { try { Directory.Delete(_path,true); } catch {} }
    }
}
