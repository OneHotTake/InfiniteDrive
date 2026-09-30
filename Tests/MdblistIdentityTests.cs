using System.Text.Json;
using InfiniteDrive.Services;
using Xunit;

namespace InfiniteDrive.Tests;

public sealed class MdblistIdentityTests
{
    [Theory]
    [InlineData("show", "series")]
    [InlineData("series", "series")]
    [InlineData("movie", "movie")]
    public void PublicArrayPreservesMediaTypeAndReleaseYear(string sourceType, string expected)
    {
        using var doc = JsonDocument.Parse($$"""{"title":"Sugar","imdb_id":"tt16418808","mediatype":"{{sourceType}}","release_year":2024}""");
        var item = ListFetcher.ParseMdblistItem(doc.RootElement);
        Assert.NotNull(item);
        Assert.Equal(expected, item.MediaType);
        Assert.Equal(2024, item.Year);
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("series")]
    public void GroupedResponseSuppliesTypeWhenIndividualItemOmitsIt(string bucket)
    {
        using var doc = JsonDocument.Parse("""{"title":"Example","imdb_id":"tt1234567","year":"2024"}""");
        var item = ListFetcher.ParseMdblistItem(doc.RootElement, bucket);
        Assert.NotNull(item);
        Assert.Equal(bucket, item.MediaType);
        Assert.Equal(2024, item.Year);
    }

    [Fact]
    public void UntypedArrayEntryNeverDefaultsToMovie()
    {
        using var doc = JsonDocument.Parse("""{"title":"Loki","imdb_id":"tt9140554"}""");
        Assert.Null(ListFetcher.ParseMdblistItem(doc.RootElement));
    }

    [Fact]
    public void ContradictoryBucketAndItemTypeAreRejected()
    {
        using var doc = JsonDocument.Parse("""{"title":"Loki","imdb_id":"tt9140554","mediatype":"show"}""");
        Assert.Null(ListFetcher.ParseMdblistItem(doc.RootElement, "movie"));
    }
}
