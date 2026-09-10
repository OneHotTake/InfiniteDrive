using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Services;
using InfiniteDrive.Models;
using InfiniteDrive.Tasks;
using Xunit;

namespace InfiniteDrive.Tests;

public sealed class HardeningRegressionTests
{
    [Fact]
    public async Task ManifestValidationAcceptsObjectResourcesAndClassifiesCatalogs()
    {
        const string json = """
            {"id":"test.addon","name":"Fixture","version":"1.0.0",
             "resources":[{"name":"stream","types":["movie"]},"catalog"],
             "catalogs":[
               {"id":"browse","type":"movie"},
               {"id":"search","type":"series","extra":[{"name":"search","isRequired":true}]}
             ]}
            """;
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.OK, json));

        var result = await ManifestUrlParser.ValidateManifestUrlAsync(
            "https://example.invalid/stremio/user/secret/manifest.json", http);

        Assert.True(result.IsValid);
        Assert.True(result.HasStreamResource);
        Assert.Equal(2, result.CatalogCount);
        Assert.Equal(1, result.BrowsableCatalogCount);
        Assert.Equal(1, result.SearchOnlyCatalogCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{}", "403")]
    [InlineData(HttpStatusCode.NotFound, "{}", "404")]
    [InlineData(HttpStatusCode.OK, "not-json", "invalid_json")]
    [InlineData(HttpStatusCode.OK, "{}", "missing_id")]
    public async Task ManifestValidationFailsClosed(
        HttpStatusCode status, string body, string expectedError)
    {
        using var http = new HttpClient(new StaticHandler(status, body));
        var result = await ManifestUrlParser.ValidateManifestUrlAsync(
            "https://example.invalid/stremio/user/secret/manifest.json", http);

        Assert.False(result.IsValid);
        Assert.Equal(expectedError, result.ErrorType);
        Assert.DoesNotContain("secret", result.ErrorMessage);
    }

    [Fact]
    public void AuthenticatedManifestTokenMayContainSlashes()
    {
        const string url = "https://example.invalid/stremio/11111111-2222-3333-4444-555555555555/part-one/part-two/manifest.json";

        var (baseUrl, uuid, token) = AioStreamsClient.TryParseManifestUrl(url);

        Assert.Equal("https://example.invalid", baseUrl);
        Assert.Equal("11111111-2222-3333-4444-555555555555", uuid);
        Assert.Equal("part-one/part-two", token);
    }

    [Fact]
    public void BackupProviderRequiresExplicitEnableFlag()
    {
        var config = new PluginConfiguration
        {
            PrimaryManifestUrl = "https://primary.invalid/stremio/manifest.json",
            SecondaryManifestUrl = "https://backup.invalid/stremio/manifest.json",
            EnableBackupAioStreams = false,
        };

        Assert.Single(ProviderHelper.GetProviders(config));

        config.EnableBackupAioStreams = true;
        Assert.Equal(2, ProviderHelper.GetProviders(config).Count);
    }

    [Theory]
    [InlineData(
        "https://aio.example/stremio/user/token/manifest.json?api_key=secret",
        "https://aio.example/stremio/[redacted]/manifest.json")]
    [InlineData(
        "https://aio.example/stremio/user/token/with/slashes/stream/movie/tt123.json",
        "https://aio.example/stremio/[redacted]/stream/movie/tt123.json")]
    [InlineData(
        "https://cdn.example/signed/path/movie.mkv?token=secret",
        "https://cdn.example/[redacted]")]
    public void UrlRedactionRemovesCredentials(string input, string expected)
    {
        Assert.Equal(expected, SensitiveUrlRedactor.Redact(input));
        Assert.DoesNotContain("secret", SensitiveUrlRedactor.Redact(input));
        Assert.DoesNotContain("token/", SensitiveUrlRedactor.Redact(input));
    }

    [Fact]
    public void DiagnosticTextRedactionRemovesSignedUrls()
    {
        const string text = "ffprobe failed for https://cdn.example/a/b/file.mkv?token=secret: 403";
        var safe = SensitiveUrlRedactor.RedactText(text);

        Assert.Equal("ffprobe failed for https://cdn.example/[redacted] 403", safe);
        Assert.DoesNotContain("secret", safe);
        Assert.DoesNotContain("file.mkv", safe);
    }

    [Fact]
    public void EveryPublicConfigurationPropertyIsPersisted()
    {
        var missing = typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.DeclaringType == typeof(PluginConfiguration))
            .Where(p => p.GetCustomAttribute<DataMemberAttribute>() == null)
            .Select(p => p.Name)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void PopulateConsumesOnlyQueuedOrDueExpansionWork()
    {
        const long now = 100_000;
        var items = new List<CatalogItem>
        {
            new() { Id = "new", AioId = "tt1", MediaType = "movie", ItemState = ItemState.Queued },
            new() { Id = "written", AioId = "tt2", MediaType = "movie", ItemState = ItemState.Queued, StrmPath = "/movies/tt2.strm" },
            new() { Id = "series-due", AioId = "tt3", MediaType = "series", ItemState = ItemState.Ready, StrmPath = "/tv/tt3.strm", EpisodesExpanded = true, LastExpandedAt = now - (6 * 3600) },
            new() { Id = "series-fresh", AioId = "tt4", MediaType = "series", ItemState = ItemState.Ready, StrmPath = "/tv/tt4.strm", EpisodesExpanded = true, LastExpandedAt = now - 60 },
        };

        var selected = RefreshTask.SelectPopulateWork(items, now);

        Assert.Equal(new[] { "new", "series-due" }, selected.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void AutoSelectionRejectsCamAndRemuxIncludingFallbacksByDefault()
    {
        var safe = new ParsedStream { Url = "https://cdn.invalid/safe", SourceTag = "WEB-DL", Resolution = "1080p" };
        var cam = new ParsedStream { Url = "https://cdn.invalid/cam", SourceTag = "CAM/TS", Resolution = "1080p" };
        var remux = new ParsedStream { Url = "https://cdn.invalid/remux", SourceTag = "BluRay Remux", Resolution = "4K" };
        var config = new PluginConfiguration
        {
            UseRemuxForAutoSelection = false,
            DesiredVersions = new List<DesiredVersionBucket>
            {
                new() { Resolution = "1080p", Audio = "Any Audio", Count = 1 }
            }
        };

        var selected = VersionSelectorService.SelectBestVersions(
            new List<ParsedStream> { cam, remux, safe }, config.DesiredVersions, 3, config);
        VersionSelectorService.AssignSecondaryUrls(selected, new List<ParsedStream> { cam, remux, safe }, config);

        Assert.Single(selected);
        Assert.Same(safe, selected[0].Stream);
        Assert.Null(selected[0].SecondaryUrl);
    }

    [Theory]
    [InlineData("Movie.2026.720p.CAM.x264.mkv", true)]
    [InlineData("Movie.2026.HDTS.x264.mkv", true)]
    [InlineData("Movie.2026.TELESYNC.x264.mkv", true)]
    [InlineData("Movie.2026.1080p.WEB-DL.DTS.mkv", false)]
    public void LowQualityCaptureDetectionIsTokenAware(string name, bool expected)
    {
        Assert.Equal(expected, StreamHelpers.IsLowQualityCapture(name));
    }

    [Fact]
    public void ResolverCandidateFilteringCannotReintroduceRejectedReleases()
    {
        var response = new AioStreamsStreamResponse
        {
            Streams = new List<AioStreamsStream>
            {
                new() { Url = "https://cdn.invalid/cam", StreamType = "debrid", BehaviorHints = new AioStreamsBehaviorHints { Filename = "Movie.1080p.CAM.mkv" } },
                new() { Url = "https://cdn.invalid/remux", StreamType = "debrid", BehaviorHints = new AioStreamsBehaviorHints { Filename = "Movie.2160p.REMUX.mkv" } },
                new() { Url = "https://cdn.invalid/web", StreamType = "debrid", BehaviorHints = new AioStreamsBehaviorHints { Filename = "Movie.1080p.WEB-DL.mkv" } },
            }
        };

        var candidates = StreamHelpers.RankAndFilterStreams(
            response, "tt1234567", null, null, "torbox", 0, 360, includeRemux: false);

        Assert.Single(candidates);
        Assert.Equal("https://cdn.invalid/web", candidates[0].Url);
    }

    [Fact]
    public async Task LegacyIdentityAndUrlContractsRemainValid()
    {
        Assert.True(StreamUrlTests.TestAbsoluteEpisodeCalculation());
        Assert.True(await UniqueIdTests.VerifyUniqueIdTypesAsync());

        var movie = StreamUrlTests.TestMovieStreamUrl("https://example.invalid", "tt1234567");
        var episode = StreamUrlTests.TestSeriesStreamUrl("https://example.invalid", "tt1234567", 2, 3);
        Assert.True(StreamUrlTests.ValidateStreamUrlFormat(movie, "/stream/movie/tt1234567"));
        Assert.True(StreamUrlTests.ValidateStreamUrlFormat(episode, "/stream/series/tt1234567:2:3"));
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StaticHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }
}
