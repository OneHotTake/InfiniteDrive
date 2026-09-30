using System.Linq;
using System.Text.Json;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using Xunit;

namespace InfiniteDrive.Tests;

public sealed class ExternalListIdentityTests
{
    [Theory]
    [InlineData("movie", "movie", false)]
    [InlineData("show", "series", true)]
    public void TraktWrappersKeepIdentityYearAndImportFamily(string wrapper, string type, bool episodic)
    {
        var result = ListFetcher.ParseTraktResponse("""[{"type":"__WRAPPER__","__WRAPPER__":{"title":"Example","year":2024,"ids":{"imdb":"tt1234567"}}}]""".Replace("__WRAPPER__", wrapper));
        var item = Assert.Single(result.Items);
        Assert.Equal("tt1234567", item.AioId);
        Assert.Equal(2024, item.Year);
        Assert.Equal(type, item.MediaType);
        Assert.Equal(episodic, ImportInventory.IsSeries(new CatalogItem { MediaType = item.MediaType! }));
    }

    [Theory]
    [InlineData("movie", "movie")]
    [InlineData("show", "series")]
    public void TraktFlatEntriesRequireAnExplicitType(string rawType, string expected)
    {
        var result = ListFetcher.ParseTraktResponse("""[{"title":"Example","year":2024,"type":"__RAWTYPE__","ids":{"imdb":"tt1234567"}}]""".Replace("__RAWTYPE__", rawType));
        Assert.Equal(expected, Assert.Single(result.Items).MediaType);
    }

    [Theory]
    [InlineData("""[{"title":"Unknown type","ids":{"imdb":"tt1234567"}}]""")]
    [InlineData("""[{"type":"episode","show":{"title":"Parent","ids":{"imdb":"tt1234567"}},"episode":{"season":1,"number":2}}]""")]
    [InlineData("""[{"type":"show","movie":{"title":"Contradiction","ids":{"imdb":"tt1234567"}}}]""")]
    public void TraktDoesNotGuessOrPromoteUnsupportedItems(string json)
    {
        Assert.False(ListFetcher.ParseTraktResponse(json).Ok);
    }

    [Fact]
    public void TraktMixedListDoesNotRouteShowsThroughMovies()
    {
        var result = ListFetcher.ParseTraktResponse("""[{"movie":{"title":"Film","year":2023,"ids":{"imdb":"tt1234567"}}},{"show":{"title":"Show","year":2024,"ids":{"imdb":"tt1234568"}}}]""");
        Assert.Equal(new[] { "movie", "series" }, result.Items.Select(x => x.MediaType));
    }

    [Fact]
    public void TmdbTvKeepsTvYearAndUsesEpisodeImporter()
    {
        var result = ListFetcher.ParseTmdbResponse("""{"name":"Mixed list","items":[{"id":203744,"name":"Sugar","media_type":"tv","release_date":"","first_air_date":"2024-04-05"}]}""");
        var item = Assert.Single(result.Items);
        Assert.Equal("tmdb_203744", item.AioId);
        Assert.Equal("series", item.MediaType);
        Assert.Equal(2024, item.Year);
        Assert.True(ImportInventory.IsSeries(new CatalogItem { MediaType = item.MediaType! }));
        Assert.Equal("Mixed list", result.DisplayName);
    }

    [Fact]
    public void TmdbV3MovieRetainsUntypedMovieCompatibility()
    {
        var result = ListFetcher.ParseTmdbResponse("""{"items":[{"id":157336,"title":"Interstellar","release_date":"2014-11-05"}]}""");
        var item = Assert.Single(result.Items);
        Assert.Equal("tmdb_157336", item.AioId);
        Assert.Equal("movie", item.MediaType);
        Assert.Equal(2014, item.Year);
    }

    [Theory]
    [InlineData("""{"items":[{"id":42,"name":"Ambiguous"}]}""")]
    [InlineData("""{"items":[{"id":42,"name":"A person","media_type":"person"}]}""")]
    public void TmdbRejectsAmbiguousOrUnsupportedTypes(string json)
    {
        Assert.False(ListFetcher.ParseTmdbResponse(json).Ok);
    }

    [Theory]
    [InlineData("MOVIE", "movie", false)]
    [InlineData("TV", "series", true)]
    [InlineData("TV_SHORT", "series", true)]
    [InlineData("OVA", "series", true)]
    [InlineData("ONA", "series", true)]
    [InlineData("SPECIAL", "series", true)]
    public void AnilistFormatDistinguishesFilmsFromEpisodes(string format, string type, bool episodic)
    {
        var result = ListFetcher.ParseAnilistResponse("""{"data":{"MediaListCollection":{"lists":[{"entries":[{"media":{"id":21519,"type":"ANIME","format":"__FORMAT__","title":{"english":"Example","romaji":"Rei"},"startDate":{"year":2016}}}]}]}}}""".Replace("__FORMAT__", format), "example-user");
        var item = Assert.Single(result.Items);
        Assert.Equal("anilist:21519", item.AioId);
        Assert.Equal(2016, item.Year);
        Assert.Equal(type, item.MediaType);
        Assert.Equal(episodic, ImportInventory.IsSeries(new CatalogItem { MediaType = item.MediaType! }));
    }

    [Theory]
    [InlineData("ANIME", "UNKNOWN")]
    [InlineData("MANGA", "TV")]
    [InlineData("ANIME", "MUSIC")]
    public void AnilistRejectsUnsupportedFormatOrNonAnime(string type, string format)
    {
        var result = ListFetcher.ParseAnilistResponse("""{"data":{"MediaListCollection":{"lists":[{"entries":[{"media":{"id":1,"type":"__TYPE__","format":"__FORMAT__","title":{"english":"Unsupported"}}}]}]}}}""".Replace("__TYPE__", type).Replace("__FORMAT__", format), "example-user");
        Assert.False(result.Ok);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":{\"MediaListCollection\":null}}")]
    public void AnilistMalformedCollectionIsReportedAsAFailure(string json)
    {
        Assert.False(ListFetcher.ParseAnilistResponse(json, "example-user").Ok);
    }

    [Fact]
    public void AnilistGraphqlErrorDoesNotBecomeAnEmptySuccessfulList()
    {
        Assert.False(ListFetcher.ParseAnilistResponse("""{"errors":[{"message":"Not found"}]}""", "example-user").Ok);
    }
}
