using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using Xunit;

namespace InfiniteDrive.Tests;

public sealed class ServiceLabelTests
{
    [Theory]
    [InlineData("torbox", "TorBox")]
    [InlineData("real-debrid", "Real-Debrid")]
    [InlineData("alldebrid", "AllDebrid")]
    [InlineData("premiumize", "Premiumize")]
    [InlineData("debrid_link", "Debrid-Link")]
    [InlineData("easynews", "Easynews")]
    [InlineData("stremio_nntp", "Usenet")]
    [InlineData("Future Service", "Future Service")]
    public void StructuredServicesAreNotLimitedToOneProvider(string id, string expected)
    {
        var raw = Raw(); raw.Service = new() { Id = id };
        raw.Description = "12 GB · Usenet";
        Assert.Equal(expected, StreamParser.ParseSingle(raw)!.ServiceLabel);
    }

    [Fact]
    public void StructuredDisplayNameIsPreserved()
    {
        var raw = Raw(); raw.Service = new() { Id = "future-service", Name = "Future Streaming" };
        Assert.Equal("Future Streaming", StreamServiceLabel.Parse(raw));
    }

    [Theory]
    [InlineData("13.1 GB · Dolby Digital Plus 5.1 · TorBox", "TorBox")]
    [InlineData("28 GB • Dolby Atmos 7.1 • Usenet", "Usenet")]
    [InlineData("12 GB | Real Debrid", "Real-Debrid")]
    [InlineData("4K\nService: New Provider", "New Provider")]
    [InlineData("Provider: Example Debrid", "Example Debrid")]
    [InlineData("13 GB · ReleaseGroup", "")]
    [InlineData("The TorBox Story (2024)", "")]
    [InlineData("https://example.invalid/TorBox?token=private", "")]
    [InlineData("Service: https://example.invalid/private", "")]
    public void StandardResponsesUseExplicitServiceTextOnly(string text, string expected)
    {
        var raw = Raw(); raw.Description = text;
        Assert.Equal(expected, StreamServiceLabel.Parse(raw));
    }

    [Fact]
    public void UnknownSourcesAreNotInferredFromUrlsOrFilenames()
    {
        var raw = Raw(); raw.BehaviorHints = new() { Filename = "Movie.TorBox.2160p.mkv" };
        raw.Url = "https://torbox.example.invalid/private";
        raw.Addon = "torbox";
        raw.Service = new() { Name = "../../secret", Id = "https://example.invalid/token" };
        Assert.Equal("", StreamServiceLabel.Parse(raw));
        raw.StreamType = "usenet";
        Assert.Equal("Usenet", StreamServiceLabel.Parse(raw));
    }

    [Fact]
    public void SameStreamsNeedRefreshWhenTheirLabelsChange()
    {
        var versions = Select("TorBox");
        var stored = StrmFileManager.DeserializeVersions(StrmFileManager.SerializeVersions(versions));
        Assert.Equal("TorBox", stored[0].ServiceLabel);
        Assert.False(VersionSelectorService.ShouldReplace(stored, versions));
        stored[0].VersionLabel = "4K - WEB-DL - 10.0GiB";
        Assert.True(VersionSelectorService.ShouldReplace(stored, versions));
        Assert.Empty(StrmFileManager.DeserializeVersions("[{\"Resolution\":\"4K\"}]")[0].ServiceLabel);
    }

    [Fact]
    public async Task EveryKnownServiceVersionIsLabeledWithoutAddingAnExtraDefault()
    {
        var folder = Path.Combine(Path.GetTempPath(), "service-label-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var log = DispatchProxy.Create<MediaBrowser.Model.Logging.ILogManager, ImportReconciliationTests.NullLogProxy>();
            var writer = new StrmFileManager(log);
            File.WriteAllText(Path.Combine(folder, "E1.strm"), "https://example.invalid/old");
            File.WriteAllText(Path.Combine(folder, "E10.strm"), "https://example.invalid/neighbor");
            var versions = Select("TorBox", "Usenet");
            await writer.WriteOrReplaceStrmFilesAsync(folder, "E1", versions, default);
            var files = ImportInventory.FindFiles(folder, "E1");
            Assert.Equal(2, files.Count);
            Assert.Contains(files, p => p.EndsWith("TorBox.strm"));
            Assert.Contains(files, p => p.EndsWith("Usenet.strm"));
            Assert.False(File.Exists(Path.Combine(folder, "E1.strm")));
            Assert.True(File.Exists(Path.Combine(folder, "E10.strm")));
            Assert.Equal(0, await writer.WriteOrReplaceStrmFilesAsync(folder, "E1", versions, default));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task FailedLabeledReplacementRetainsTheOldPrimary()
    {
        var folder = Path.Combine(Path.GetTempPath(), "service-label-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var log = DispatchProxy.Create<MediaBrowser.Model.Logging.ILogManager, ImportReconciliationTests.NullLogProxy>();
            var writer = new StrmFileManager(log);
            var versions = Select("TorBox");
            File.WriteAllText(Path.Combine(folder, "E1.strm"), "https://example.invalid/old");
            Directory.CreateDirectory(Path.Combine(folder, "E1 - " + versions[0].VersionLabel + ".strm"));
            await Assert.ThrowsAsync<IOException>(() => writer.WriteOrReplaceStrmFilesAsync(folder, "E1", versions, default));
            Assert.Equal("https://example.invalid/old", File.ReadAllText(Path.Combine(folder, "E1.strm")));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static AioStreamsStream Raw() => new() { Url = "https://example.invalid/video", Name = "4K" };
    private static List<SelectedVersion> Select(params string[] services) => VersionSelectorService.SelectBestVersions(
        services.Select((service, i) => new ParsedStream { Url = "https://example.invalid/" + i,
            StreamKey = "test-" + i, Resolution = "4K", SourceTag = "WEB-DL", ServiceLabel = service,
            SizeGiB = 10, RankScore = 100 }).ToList(), new());
}
