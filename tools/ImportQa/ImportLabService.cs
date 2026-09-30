// QA-only: copied into Services in an isolated lab build. Excluded from normal compilation.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace InfiniteDrive.Services;

[Route("/InfiniteDrive/ImportQa", "POST")]
public sealed class ImportQaRequest : IReturn<object> { public string Step { get; set; } = ""; }
public sealed class ImportLabService : IService, IRequiresRequest
{
    private readonly IAuthorizationContext _authorization;
    public IRequest Request { get; set; } = null!;
    private const string Root = "/media/import-reconciliation-qa";
    private const string Data = "/config/data/import-reconciliation-qa";
    private const string Identity = "series:imdb:tt999999991";
    public ImportLabService(IAuthorizationContext authorization) { _authorization = authorization; }
    public async Task<object> Post(ImportQaRequest request)
    {
        var deny = AdminGuard.RequireAdmin(_authorization, Request);
        if (deny != null) return deny;
        if (Environment.GetEnvironmentVariable("INFINITEDRIVE_LAB_FREEZE") != "1")
            throw new InvalidOperationException("Isolated frozen lab only");
        var p = Plugin.Instance;
        var db = new DatabaseManager(Data, p.Logger); db.Initialise(); await db.EnsureImportCoverageAsync();
        var item = new CatalogItem { Id = "import-qa-series", AioId = "tt999999991", Title = "InfiniteDrive Reconciliation QA", Year = 2020, MediaType = "series", Source = "import-qa" };
        var existing = await db.GetImportCatalogByIdAsync(item.Id); if (existing != null) item = existing;
        var cfg = new PluginConfiguration { SyncPathShows = Root + "/TV", SyncPathMovies = Root + "/Movies", SyncPathAnime = Root + "/Anime" };
        var real = new ImportInventory(p.LibraryManager!, p.Logger, cfg, p.ProviderManager, p.StrmFileManager!);
        var fake = new LabInventory(real);
        var now = DateTimeOffset.UtcNow;
        var mode = ImportMode.Repair;
        switch (request.Step)
        {
            case "prepare":
                if (existing != null) throw new InvalidOperationException("Fixture already exists");
                await db.UpsertCatalogItemAsync(item);
                var (folder, _) = real.Destination(item, fake.Episodes[0]);
                var titleRoot = Path.GetDirectoryName(folder)!; Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(titleRoot, "tvshow.nfo"), "<tvshow><title>InfiniteDrive Reconciliation QA</title><year>2020</year><id>tt999999991</id><uniqueid type=\"imdb\" default=\"true\">tt999999991</uniqueid><lockdata>true</lockdata></tvshow>");
                foreach (var ep in fake.Episodes)
                {
                    var (epFolder, name) = real.Destination(item, ep);
                    File.WriteAllText(Path.Combine(epFolder, name + ".nfo"), $"<episodedetails><title>Fixture {ep.Episode}</title><season>1</season><episode>{ep.Episode}</episode><aired>2020-01-01</aired><lockdata>true</lockdata></episodedetails>");
                }
                mode = ImportMode.Observe;
                break;
            case "partial-numbering":
                fake.PartialNumbering = true; now = now.AddHours(8);
                break;
            case "recheck-numbering": now = now.AddHours(16); break;
            case "fail-second": fake.FailSecond = true; break;
            case "retry": now = now.AddHours(8); break;
            case "observe": mode = ImportMode.Observe; break;
            case "delete-first":
                foreach (var path in real.FindEpisodeFiles(item, fake.Episodes[0])) File.Delete(path);
                break;
            case "prune-delete-first":
                await db.SoftDeleteCatalogItemsAsync(new[] { item.AioId });
                foreach (var path in real.FindEpisodeFiles(item, fake.Episodes[0])) File.Delete(path);
                break;
            case "rejoin":
                item.RemovedAt = null; await db.UpsertCatalogItemAsync(item);
                break;
            case "block-delete-first":
                await db.UpsertBlockedItemAsync(item.AioId, null, null, item.Title, item.MediaType, "qa");
                foreach (var path in real.FindEpisodeFiles(item, fake.Episodes[0])) File.Delete(path);
                break;
            case "discover":
                var directory = p.LibraryManager!.FindByPath(Root + "/TV", true) as MediaBrowser.Controller.Entities.Folder;
                if (directory == null) throw new InvalidOperationException("Fixture root not registered");
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                    await directory.ValidateChildren(new Progress<double>(), deadline.Token, new MediaBrowser.Controller.Providers.MetadataRefreshOptions(p.FileSystem!), true);
                mode = ImportMode.Observe;
                break;
            case "provider-probe":
                var native = p.LibraryManager!.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                    { IncludeItemTypes = new[] { "Series" }, AnyProviderIdEquals = new[] { new KeyValuePair<string,string>("Imdb", item.AioId) }, Recursive = true })
                    .OfType<MediaBrowser.Controller.Entities.TV.Series>().Single(x => x.Path.StartsWith(Root + "/", StringComparison.Ordinal));
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var remote = await p.ProviderManager!.GetAllEpisodes(native, p.LibraryManager.GetLibraryOptions(native), deadline.Token);
                    await db.PersistMetadataAsync("qa_provider_probe", "runtime_call_success; returned=" + remote.Length);
                }
                catch { await db.PersistMetadataAsync("qa_provider_probe", "provider_unavailable_in_isolated_lab"); }
                mode = ImportMode.Observe;
                break;
            case "collection":
                var seriesItem = p.LibraryManager!.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                    { IncludeItemTypes = new[] { "Series" }, AnyProviderIdEquals = new[] { new KeyValuePair<string,string>("Imdb", item.AioId) }, Recursive = true })
                    .Single(x => x.Path.StartsWith(Root + "/", StringComparison.Ordinal));
                var boxes = p.BoxSetService!;
                var box = boxes.FindBoxSet("Import Reconciliation QA Collection") ?? await boxes.CreateBoxSetAsync("Import Reconciliation QA Collection", seriesItem.Id);
                if (box == null) throw new InvalidOperationException("QA collection not created");
                await boxes.AddItemToBoxSetAsync(box.Id, seriesItem.Id);
                await db.PersistMetadataAsync("qa_collection", boxes.Contains(box, seriesItem.Id) ? "confirmed" : "failed");
                mode = ImportMode.Observe;
                break;
            case "status": return await Status(db, fake);
            default: throw new ArgumentException("Unknown QA step");
        }
        await new ImportReconciliationService(db, fake, () => mode, TimeZoneInfo.Utc, () => now)
            .RunAsync(CancellationToken.None, new[] { item });
        return await Status(db, fake);
    }
    private static async Task<object> Status(DatabaseManager db, LabInventory fake)
    {
        var state = await db.GetImportCoverageAsync(Identity);
        return new { State = state, fake.Resolutions, fake.Published,
            LastRun = db.GetMetadata("import_last_run"), ProviderProbe = db.GetMetadata("qa_provider_probe"), Collection = db.GetMetadata("qa_collection"), Files = Directory.Exists(Root) ? Directory.GetFiles(Root, "*.strm", SearchOption.AllDirectories).Length : 0 };
    }
    private sealed class LabInventory : IImportInventory
    {
        private readonly ImportInventory _real;
        public bool FailSecond, PartialNumbering; public int Resolutions, Published;
        public List<ImportEpisode> Episodes => Enumerable.Range(1, 2).Select(n => new ImportEpisode
            { Key = $"aired:1:{n}", Season = 1, Episode = n, Released = DateTimeOffset.Parse("2020-01-01T00:00:00Z") }).ToList();
        public LabInventory(ImportInventory real) { _real = real; }
        public Task<ImportSnapshot> FetchAsync(CatalogItem item, CancellationToken ct)
        {
            var episodes = Episodes;
            var status = PartialNumbering ? ImportInventory.ReconcileNumbering(episodes, episodes.Take(1).ToList()) : "fixture";
            return Task.FromResult(new ImportSnapshot(episodes, status));
        }
        public Task<ImportObservation> ObserveAsync(CatalogItem item, ImportEpisode ep, CancellationToken ct) => _real.ObserveAsync(item, ep, ct);
        public bool IsOwned(CatalogItem item) => false;
        public Task<List<SelectedVersion>> ResolveAsync(CatalogItem item, ImportEpisode ep, CancellationToken ct)
        {
            Resolutions++;
            return Task.FromResult(FailSecond && ep.Episode == 2 ? new List<SelectedVersion>() : new List<SelectedVersion>
                { new() { Stream = new() { Url = "https://example.invalid/qa/" + ep.Episode } },
                  new() { Stream = new() { Url = "https://example.invalid/qa/" + ep.Episode + "/hd" }, VersionLabel = "1080p" } });
        }
        public async Task<List<string>> PublishAsync(CatalogItem item, ImportEpisode ep, List<SelectedVersion> versions, CancellationToken ct)
        { Published++; return await _real.PublishAsync(item, ep, versions, ct); }
        public void Notify(CatalogItem item) => _real.Notify(item);
    }
}
internal static class LabFileExtensions
{
    internal static List<string> FindEpisodeFiles(this ImportInventory inventory, CatalogItem item, ImportEpisode ep)
    { var (folder, name) = inventory.Destination(item, ep); return ImportInventory.FindFiles(folder, name); }
}
