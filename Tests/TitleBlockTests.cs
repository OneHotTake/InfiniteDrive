using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfiniteDrive.Tests;
public sealed class TitleBlockTests
{
    [Theory]
    [InlineData("series:imdb:tt0123338", "tt0123338")]
    [InlineData("movie:imdb:tt0000001", "tt0000001")]
    [InlineData("series:tmdb:12345", null)]
    [InlineData("series:imdb:tt123", null)]
    [InlineData("series:imdb:../../etc", null)]
    public void OnlyStableExplicitImdbIdentitiesQualify(string identity, string? expected)
        => Assert.Equal(expected, TitleBlockPolicy.ImdbFromIdentity(identity));

    [Fact] public void ArchiveKeepsExactRecoveryCopyAndOnlyRemovesVerifiedStrm()
    {
        using var h = new Files(); var version = h.Version(100);
        Assert.Equal("archived", TitleBlockPolicy.Archive(version,new[]{h.Managed},h.Archive));
        Assert.False(File.Exists(version.Path));
        Assert.Equal(h.Content, File.ReadAllText(Directory.GetFiles(h.Archive,"*.strm").Single()));
        Assert.Contains(version.Path, File.ReadAllText(Path.Combine(h.Archive,"restore.jsonl")));
        Assert.True(File.Exists(Path.Combine(h.Managed,"owned.mkv")));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,
            File.GetUnixFileMode(Directory.GetFiles(h.Archive,"*.strm").Single()));
    }
    [Theory]
    [InlineData(null, false)]
    [InlineData(0L, false)]
    [InlineData(100L, true)]
    public void UnknownSizeOrChangedEvidenceIsNeverDeleted(long? size, bool changed)
    {
        using var h = new Files(); var v = h.Version(size);
        if (changed) File.AppendAllText(v.Path,"changed");
        Assert.NotEqual("archived",TitleBlockPolicy.Archive(v,new[]{h.Managed},h.Archive));
        Assert.True(File.Exists(v.Path)); Assert.False(Directory.Exists(h.Archive));
    }
    [Fact] public void OutsideRootsAndSymlinkParentsArePreserved()
    {
        using var h = new Files(); var v = h.Version(100);
        Assert.Equal("outside_managed_library",TitleBlockPolicy.Archive(v,new[]{h.Root+"/other"},h.Archive));
        var link=Path.Combine(h.Root,"link"); Directory.CreateSymbolicLink(link,h.Managed);
        v=v with { Path=Path.Combine(link,"episode.strm") };
        Assert.Equal("symlink",TitleBlockPolicy.Archive(v,new[]{link},h.Archive));
        Assert.True(File.Exists(v.Path));
    }
    [Fact] public void FailedArchiveNeverDeletesSource()
    {
        using var h = new Files(); var v=h.Version(100); File.WriteAllText(h.Archive,"not a directory");
        Assert.Equal("preserved_on_error",TitleBlockPolicy.Archive(v,new[]{h.Managed},h.Archive));
        Assert.True(File.Exists(v.Path));
    }
    [Fact] public async Task RepeatedAndConcurrentBlocksKeepOneActiveRowAndUnblockHistory()
    {
        using var h=new Files(); SqliteTestRuntime.EnsureInitialized(); var db=new DatabaseManager(h.Root,NullLogger.Instance); db.Initialise();
        await Task.WhenAll(Enumerable.Range(0,8).Select(_=>db.UpsertBlockedItemAsync("tt0123338",null,null,"60 Minutes","series","admin")));
        Assert.True(await db.IsBlockedAsync("TT0123338",null,null));
        var blocks=await db.GetBlockedItemsAsync(0, 100); Assert.Single(blocks);
        await db.UnblockItemAsync(blocks[0].Id,"admin"); Assert.False(await db.IsBlockedAsync("tt0123338",null,null));
        await db.UpsertBlockedItemAsync("tt0123338",null,null,"60 Minutes","series","admin");
        Assert.Single(await db.GetBlockedItemsAsync(0, 100));
    }
    private sealed class Files : IDisposable
    {
        public string Root=Path.Combine(Path.GetTempPath(),"title-block-"+Guid.NewGuid().ToString("N"));
        public string Managed=>Path.Combine(Root,"managed"); public string Archive=>Path.Combine(Root,"archive");
        public string Content="https://example.invalid/test-fixture\n";
        public Files(){Directory.CreateDirectory(Managed);File.WriteAllText(Path.Combine(Managed,"owned.mkv"),"owned");}
        public ImportVersionEvidence Version(long? size) { var path=Path.Combine(Managed,"episode.strm");File.WriteAllText(path,Content);
            return new ImportVersionEvidence(path,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Content.Trim()))).ToLowerInvariant(),"fixture","1080p",null,size,DateTimeOffset.UtcNow); }
        public void Dispose(){Directory.Delete(Root,true);}
    }
}
