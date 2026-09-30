using System;
using System.IO;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfiniteDrive.Tests;

public sealed class CatalogIdentityPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongTypeRefreshCannotResetManagedIdentityOrSafeguards(bool bulk)
    {
        using var h = new Harness();
        var original = h.Series();
        original.RetryCount = 2;
        original.NextRetryAt = 1900000000;
        original.BlockedAt = "2026-09-30T12:00:00Z";
        original.BlockedBy = "test-user";
        await h.Db.UpsertCatalogItemAsync(original);
        var stale = new CatalogItem { AioId = original.AioId, Source = original.Source,
            MediaType = "movie", Title = original.Title, ItemState = ItemState.Queued };
        if (bulk) Assert.Equal(0, await h.Db.BulkUpsertCatalogItemsAsync(new[] { stale }));
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.UpsertCatalogItemAsync(stale));
            Assert.Equal("managed_identity_conflict", error.Message);
        }
        var actual = (await h.Db.GetCatalogItemByAioIdAsync(original.AioId))!;
        Assert.Equal(original.Id, actual.Id);
        Assert.Equal("series", actual.MediaType);
        Assert.Equal(2026, actual.Year);
        Assert.Equal("252105", actual.TmdbId);
        Assert.Equal(original.StrmPath, actual.StrmPath);
        Assert.Equal(2, actual.RetryCount);
        Assert.Equal(original.NextRetryAt, actual.NextRetryAt);
        Assert.Equal(original.BlockedAt, actual.BlockedAt);
        Assert.Equal(original.BlockedBy, actual.BlockedBy);
        Assert.Equal(original.UpdatedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task SameTypeSparseRefreshKeepsKnownYearAndTmdb()
    {
        using var h = new Harness(); var original = h.Series();
        await h.Db.UpsertCatalogItemAsync(original);
        await h.Db.UpsertCatalogItemAsync(new CatalogItem { AioId = original.AioId,
            Source = original.Source, MediaType = "series", Title = "Updated title" });
        var actual = (await h.Db.GetCatalogItemByAioIdAsync(original.AioId))!;
        Assert.Equal("Updated title", actual.Title);
        Assert.Equal(2026, actual.Year); Assert.Equal("252105", actual.TmdbId);
        Assert.Equal(original.StrmPath, actual.StrmPath);
    }

    [Fact]
    public async Task BulkIdentityConflictSkipsOnlyConflictingRow()
    {
        using var h = new Harness(); var original = h.Series();
        await h.Db.UpsertCatalogItemAsync(original);
        var unrelated = new CatalogItem { AioId = "tt0000002", Source = "aiostreams", MediaType = "movie", Title = "Original" };
        await h.Db.UpsertCatalogItemAsync(unrelated); unrelated.Title = "Valid update";
        var wrong = new CatalogItem { AioId = original.AioId, Source = original.Source, MediaType = "movie", Title = "Wrong" };
        Assert.Equal(1, await h.Db.BulkUpsertCatalogItemsAsync(new[] { unrelated, wrong }));
        Assert.Equal("Valid update", (await h.Db.GetCatalogItemByAioIdAsync(unrelated.AioId))!.Title);
        Assert.Equal("series", (await h.Db.GetCatalogItemByAioIdAsync(original.AioId))!.MediaType);
    }

    [Fact]
    public async Task OwnedRetirementRemainsRetiredAndCannotChangeFamily()
    {
        using var h = new Harness(); var original = h.Series();
        await h.Db.UpsertCatalogItemAsync(original);
        await h.Db.RetireCatalogItemForOwnedMediaAsync(original.Id, "/owned/series");
        var wrong = new CatalogItem { AioId = original.AioId, Source = original.Source, MediaType = "movie", Title = "Wrong" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.UpsertCatalogItemAsync(wrong));
        var actual = (await h.Db.GetCatalogItemByAioIdAsync(original.AioId))!;
        Assert.Equal(ItemState.Retired, actual.ItemState); Assert.Null(actual.StrmPath);
        Assert.Equal("library", actual.LocalSource); Assert.Equal("/owned/series", actual.LocalPath);
        Assert.Equal("series", actual.MediaType);
    }

    [Theory]
    [InlineData("anime", "series")]
    [InlineData("series", "anime")]
    public async Task CompatibleEpisodeFamilyRefreshPreservesManagedDestinationType(string existing, string incoming)
    {
        using var h = new Harness(); var original = h.Series(); original.MediaType = existing;
        await h.Db.UpsertCatalogItemAsync(original);
        var refresh = new CatalogItem { AioId = original.AioId, Source = original.Source, MediaType = incoming, Title = "New title" };
        Assert.Equal(1, await h.Db.BulkUpsertCatalogItemsAsync(new[] { refresh }));
        var actual = (await h.Db.GetCatalogItemByAioIdAsync(original.AioId))!;
        Assert.Equal(existing, actual.MediaType); Assert.Equal(original.StrmPath, actual.StrmPath);
        Assert.Equal("New title", actual.Title);
    }

    [Fact]
    public async Task UnpublishedRowsAndSeparateSourcesCanStillAcceptCatalogIntent()
    {
        using var h = new Harness(); var pending = h.Series(); pending.StrmPath = null;
        await h.Db.UpsertCatalogItemAsync(pending);
        pending.MediaType = "movie"; await h.Db.UpsertCatalogItemAsync(pending);
        Assert.Equal("movie", (await h.Db.GetCatalogItemByAioIdAsync(pending.AioId))!.MediaType);
        var separate = h.Series(); separate.Source = "aiostreams";
        await h.Db.UpsertCatalogItemAsync(separate);
        Assert.Equal("series", (await h.Db.GetImportCatalogByIdAsync(separate.Id))!.MediaType);
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "catalog-identity-" + Guid.NewGuid().ToString("N"));
        public DatabaseManager Db { get; }
        public Harness() { SqliteTestRuntime.EnsureInitialized(); Directory.CreateDirectory(_path); Db = new DatabaseManager(_path, NullLogger.Instance); Db.Initialise(); }
        public CatalogItem Series() => new() { AioId = "tt32149571", Source = "external_list", Title = "Golden Axe",
            MediaType = "series", Year = 2026, TmdbId = "252105", StrmPath = "/managed/shows/Golden Axe",
            LocalPath = "/managed/shows/Golden Axe", LocalSource = "strm", ItemState = ItemState.Queued,
            UpdatedAt = "2026-09-30T12:04:46Z" };
        public void Dispose() { try { Directory.Delete(_path, true); } catch { } }
    }
}
