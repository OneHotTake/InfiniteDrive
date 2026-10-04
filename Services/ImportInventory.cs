using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services;

public sealed record ImportSnapshot(List<ImportEpisode> Items, string ProviderStatus);
public sealed record ImportObservation(List<string> Paths, List<string> NativeIds, bool Conflict);

/// <summary>Production and QA use the same coordinator; only metadata/stream transport varies.</summary>
public interface IImportInventory
{
    bool ProviderPaused => false;
    string ProfileFingerprint => "";
    bool CanAddSource(CatalogItem item, ImportEpisode episode) => HasSavedFiles(episode);
    bool HasSavedFiles(ImportEpisode episode) => episode.Paths.Any(File.Exists);
    Task<ImportSnapshot> FetchAsync(CatalogItem item, CancellationToken ct);
    Task<ImportObservation> ObserveAsync(CatalogItem item, ImportEpisode episode, CancellationToken ct);
    bool IsOwned(CatalogItem item);
    Task<List<SelectedVersion>> ResolveAsync(CatalogItem item, ImportEpisode episode, CancellationToken ct);
    Task<List<SelectedVersion>> ResolveCatchUpAsync(CatalogItem item, ImportEpisode episode, CancellationToken ct)
        => ResolveAsync(item, episode, ct);
    Task<List<string>> PublishAsync(CatalogItem item, ImportEpisode episode, List<SelectedVersion> versions, CancellationToken ct);
    Task<List<SelectedVersion>> ResolveDiversityAsync(CatalogItem item, ImportEpisode episode, CancellationToken ct)
        => ResolveAsync(item, episode, ct);
    Task<List<string>> PublishAdditionAsync(CatalogItem item, ImportEpisode episode, List<SelectedVersion> versions, CancellationToken ct)
        => throw new NotSupportedException("additive_publication_unavailable");
    void Notify(CatalogItem item);
}

public sealed class ImportInventory : IImportInventory
{
    private readonly ILibraryManager _library;
    private readonly ILogger _logger;
    private readonly PluginConfiguration _config;
    private readonly IProviderManager? _providers;
    private readonly StrmFileManager _writer;
    // One inventory adapter lives for one slice; native identity/range observations
    // are a dated snapshot. File existence and final ownership remain live checks.
    private readonly Dictionary<string, BaseItem[]> _rootObservations = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Episode[]> _rangeObservations = new();

    public ImportInventory(ILibraryManager library, ILogger logger, PluginConfiguration config,
        IProviderManager? providers, StrmFileManager writer)
    { _library = library; _logger = logger; _config = config; _providers = providers; _writer = writer; }

    public static bool IsSeries(CatalogItem item) => item.MediaType is "series" or "anime";

    public static List<string> Aliases(CatalogItem item) => OwnedMediaPreferenceService.BuildProviderIds(item)
        .Select(x => (IsSeries(item) ? "series:" : "movie:") + x.Key.ToLowerInvariant() + ":" + x.Value.ToLowerInvariant())
        .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

    public static bool CompatibleIdentity(CatalogItem left, CatalogItem right)
    {
        if (IsSeries(left) != IsSeries(right)) return false;
        var a = OwnedMediaPreferenceService.BuildProviderIds(left);
        var b = OwnedMediaPreferenceService.BuildProviderIds(right);
        return a.All(x => !b.Any(y => string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase)) ||
            b.Any(y => string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase)));
    }

    public bool ProviderPaused
    {
        get
        {
            using var client = AioStreamsClientFactory.Create(_logger);
            var paused = Plugin.Instance.DatabaseManager.GetMetadata("import_provider_pause_" + client.ConfigurationFingerprint);
            var resumed = Plugin.Instance.DatabaseManager.GetMetadata("import_provider_resume");
            return paused != null && string.CompareOrdinal(paused, resumed ?? "") > 0;
        }
    }

    public bool IsOwned(CatalogItem item) => new OwnedMediaPreferenceService(_library, _logger).FindOwnedMedia(item) != null;
    public string ProfileFingerprint
    { get { using var client = AioStreamsClientFactory.Create(_logger); return client.ConfigurationFingerprint; } }

    public async Task<ImportSnapshot> FetchAsync(CatalogItem item, CancellationToken ct)
    {
        using var client = AioStreamsClientFactory.TryCreateForManifest(item.SourceManifestUrl ?? "", _logger)
            ?? AioStreamsClientFactory.Create(_logger);
        client.Cooldown = Plugin.Instance?.CooldownGate;
        client.ActiveCooldownKind = CooldownKind.SeriesMeta;
        var meta = (await client.GetMetaAsyncTyped(item.MediaType, item.AioId, ct))?.Meta;
        if (meta == null) throw new InvalidOperationException("metadata_unavailable");
        if (!string.IsNullOrWhiteSpace(meta.Id) && !string.Equals(meta.Id, item.AioId, StringComparison.OrdinalIgnoreCase) &&
            !OwnedMediaPreferenceService.BuildProviderIds(item).Any(x => string.Equals(x.Value, meta.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("identity_conflict");
        if (!string.IsNullOrEmpty(meta.ImdbId) && item.AioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(meta.ImdbId, item.AioId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("identity_conflict");
        List<ImportEpisode> expected;
        if (!IsSeries(item))
        {
            var release = ParseRelease(meta.Released);
            expected = new() { new ImportEpisode { Key = "movie", Released = release,
                DateOnly = meta.Released?.Length == 10 } };
        }
        else
        {
            if (meta.Videos == null || meta.Videos.Count == 0) throw new InvalidOperationException("metadata_empty");
            if (meta.Videos.Any(x => !x.Season.HasValue || !x.Episode.HasValue))
                throw new InvalidOperationException("numbering_conflict");
            expected = meta.Videos.Select(x => new ImportEpisode
            {
                Key = $"aired:{x.Season}:{x.Episode}", Season = x.Season, Episode = x.Episode,
                Released = ParseRelease(x.Released), DateOnly = x.Released?.Length == 10,
                Numbering = item.MediaType == "anime" && !item.AioId.StartsWith("tt", StringComparison.Ordinal)
                    && string.IsNullOrEmpty(item.TvdbId) && string.IsNullOrEmpty(item.TmdbId) ? "unverified" : "aired"
            }).ToList();
        }
        expected = NormalizeSourceNumbering(expected);
        var providerStatus = "not_applicable";
        if (IsSeries(item))
        {
            providerStatus = "provider_check_unavailable";
            var series = FindRoots(item).OfType<Series>().FirstOrDefault();
            if (series != null && _providers != null)
            {
                try
                {
                    // Exact Emby 4.10 API; no REST loopback and no virtual-item query.
                    var results = await _providers.GetAllEpisodes(series, _library.GetLibraryOptions(series), ct);
                    if (results.Length > 0)
                    {
                        var providerEpisodes = results.Where(x => x.ParentIndexNumber.HasValue && x.IndexNumber.HasValue)
                            .Select(x => new ImportEpisode
                            { Key = $"aired:{x.ParentIndexNumber}:{x.IndexNumber}", Season = x.ParentIndexNumber,
                                Episode = x.IndexNumber, Released = x.PremiereDate }).ToList();
                        providerStatus = ReconcileNumbering(expected, providerEpisodes);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { providerStatus = "provider_check_unavailable"; }
            }
        }
        return new(expected, providerStatus);
    }

    public static List<ImportEpisode> NormalizeSourceNumbering(IEnumerable<ImportEpisode> episodes) =>
        episodes.GroupBy(x => x.Key, StringComparer.Ordinal).Select(group =>
        {
            var episode = group.First();
            if (group.Count() > 1) episode.Numbering = "conflict";
            return episode;
        }).ToList();

    // Provider coverage is checked per key. A future/special/missing provider row
    // does not invalidate siblings whose aired numbering agrees. No episode is
    // mapped to another season or an absolute number to manufacture agreement.
    public static string ReconcileNumbering(List<ImportEpisode> source, IReadOnlyList<ImportEpisode> provider)
    {
        var keys = provider.GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        foreach (var episode in source.Where(x => x.Numbering == "aired"))
            episode.Numbering = !keys.TryGetValue(episode.Key, out var rows) ? "unconfirmed"
                : rows.Count != 1 ? "conflict" : "aired";
        var completeAgreement = source.All(x => x.Numbering == "aired");
        // Provider-only additions retain the earlier strict agreement requirement.
        if (completeAgreement)
            foreach (var rows in keys.Values.Where(x => x.Count == 1))
                if (source.All(x => x.Key != rows[0].Key)) source.Add(rows[0]);
        return completeAgreement && keys.Values.All(x => x.Count == 1) ? "success" : "partial_numbering";
    }

    public static DateTimeOffset? ParseRelease(string? value) => DateTimeOffset.TryParse(value,
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;

    private BaseItem[] FindRoots(CatalogItem item)
    {
        var key = item.MediaType + ":" + item.AioId;
        if (!_rootObservations.TryGetValue(key, out var roots))
            _rootObservations[key] = roots = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { IsSeries(item) ? "Series" : "Movie" },
                AnyProviderIdEquals = OwnedMediaPreferenceService.BuildProviderIds(item), Recursive = true
            });
        return roots;
    }

    private Episode[] FindEpisodeRanges(Series series)
    {
        if (!_rangeObservations.TryGetValue(series.InternalId, out var ranges))
            _rangeObservations[series.InternalId] = ranges = _library.GetItemList(new InternalItemsQuery
            { AncestorIds = new[] { series.InternalId }, Recursive = true, IncludeItemTypes = new[] { "Episode" } })
                .OfType<Episode>().Where(x => x.IndexNumber.HasValue && x.IndexNumberEnd > x.IndexNumber).ToArray();
        return ranges;
    }

    public Task<ImportObservation> ObserveAsync(CatalogItem item, ImportEpisode episode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (folder, name) = Destination(item, episode);
        var paths = FindFiles(folder, name);
        if (IsSeries(item) && episode.Season.HasValue)
        {
            foreach (var series in FindRoots(item).OfType<Series>())
            foreach (var child in FindEpisodeRanges(series))
            {
                if (child.ParentIndexNumber != episode.Season || !child.IndexNumber.HasValue ||
                    !child.IndexNumberEnd.HasValue || child.IndexNumber > episode.Episode || child.IndexNumberEnd < episode.Episode) continue;
                var path = child.Path;
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) ||
                    !SafeManagedPath(Path.GetDirectoryName(folder)!, path)) continue;
                if (FindFiles(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path)).Contains(path)) paths.Add(path);
            }
        }
        var ids = new List<string>();
        var conflict = false;
        foreach (var path in paths)
        {
            var native = _library.FindByPath(path, false);
            if (native == null) continue;
            if (!IsSeries(item))
            {
                var matches = FindRoots(item);
                if (matches.Any(x => x.InternalId == native.InternalId)) ids.Add(native.InternalId.ToString(CultureInfo.InvariantCulture));
                else conflict = true;
            }
            else if (native is Episode ep)
            {
                var parent = ep.Series;
                var seriesMatch = parent != null && FindRoots(item).Any(x => x.InternalId == parent.InternalId);
                if (seriesMatch && ep.ParentIndexNumber == episode.Season && ep.IndexNumber <= episode.Episode
                    && (ep.IndexNumberEnd ?? ep.IndexNumber) >= episode.Episode)
                    ids.Add(ep.InternalId.ToString(CultureInfo.InvariantCulture));
                else conflict = true;
            }
            else conflict = true;
        }
        return Task.FromResult(new ImportObservation(paths, ids.Distinct().ToList(), conflict));
    }

    public (string Folder, string Name) Destination(CatalogItem item, ImportEpisode episode)
    {
        var root = item.MediaType switch { "movie" => _config.SyncPathMovies, "anime" => _config.SyncPathAnime, _ => _config.SyncPathShows };
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("managed_root_unconfigured");
        var title = string.IsNullOrEmpty(item.StrmPath) ? Path.Combine(root, NamingPolicyService.BuildFolderName(item)) : item.StrmPath;
        if (title.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            title = Path.GetDirectoryName(title)!;
            if (IsSeries(item)) title = Path.GetDirectoryName(title)!;
        }
        if (!SafeManagedPath(root, title)) throw new InvalidOperationException("unsafe_path");
        var folder = episode.Season.HasValue ? Path.Combine(title, $"Season {episode.Season:D2}") : title;
        if (!SafeManagedPath(root, folder)) throw new InvalidOperationException("unsafe_path");
        var name = episode.Season.HasValue
            ? Path.GetFileNameWithoutExtension(NamingPolicyService.BuildStrmFileName(item, episode.Season, episode.Episode))
            : Path.GetFileName(NamingPolicyService.BuildFolderName(item));
        return (folder, name);
    }

    public static bool SafeManagedPath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
        for (var current = new DirectoryInfo(full); current != null; current = current.Parent)
        {
            if (current.LinkTarget != null) return false;
            if (current.FullName == fullRoot) return true;
        }
        return false;
    }

    public static List<string> FindFiles(string folder, string name)
    {
        if (!Directory.Exists(folder)) return new();
        return Directory.EnumerateFiles(folder, "*.strm").Where(path =>
        {
            var file = new FileInfo(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            return (stem == name || stem.StartsWith(name + " - ", StringComparison.Ordinal)) &&
                file.LinkTarget == null && file.Length > 0 && file.Length < 65536 &&
                Uri.TryCreate(File.ReadAllText(path).Trim(), UriKind.Absolute, out var uri) &&
                (uri.Scheme == "https" || uri.Scheme == "http");
        }).ToList();
    }

    public async Task<List<SelectedVersion>> ResolveAsync(CatalogItem item, ImportEpisode episode, CancellationToken ct)
        => await ResolveCoreAsync(item, episode, false, ct);

    public Task<List<SelectedVersion>> ResolveCatchUpAsync(CatalogItem item, ImportEpisode episode, CancellationToken ct)
        => ResolveCoreAsync(item, episode, true, ct);

    public Task<List<SelectedVersion>> ResolveDiversityAsync(CatalogItem item, ImportEpisode episode, CancellationToken ct)
        => ResolveCoreAsync(item, episode, true, ct, true);

    private async Task<List<SelectedVersion>> ResolveCoreAsync(CatalogItem item, ImportEpisode episode, bool maintenance, CancellationToken ct, bool diversity = false)
    {
        using var client = AioStreamsClientFactory.Create(_logger);
        // Every Repair lookup uses one HTTP submission and the coordinator's
        // deadline. Catch-up changes the worker budget, not transport retries.
        client.MaintenanceResolution = true;
        client.Cooldown = Plugin.Instance?.CooldownGate;
        var response = episode.Season.HasValue
            ? await client.GetSeriesStreamsAsync(item.AioId, episode.Season.Value, episode.Episode!.Value, ct)
            : await client.GetMovieStreamsAsync(item.AioId, ct);
        if (client.LastHttpStatus is 401 or 403)
        {
            await Plugin.Instance!.DatabaseManager.PersistMetadataAsync("import_provider_pause_" + client.ConfigurationFingerprint, DateTimeOffset.UtcNow.ToString("o"), ct);
            throw new ImportProviderConfigurationException();
        }
        if (client.LastHttpStatus == 429) throw new ImportHttpRateLimitException();
        if (response == null) throw new IOException("stream_transport_unavailable");
        var streams = StreamParser.ParseAll(response.Streams);
        if (diversity)
        {
            var existing = ImportDiversityPolicy.Providers(episode);
            var additions = streams.Where(x => ImportDiversityPolicy.Provider(x.ServiceLabel) is { } label &&
                existing != null && !existing.Contains(label) && x.SizeBytes is > 0 and <= 40_000_000_000).ToList();
            if (additions.Count > 0) streams = additions;
        }
        return VersionSelectorService.SelectBestVersions(streams,
            _config.DesiredVersions, RuntimePolicy.EmbyVersionLimit, _config);
    }

    public async Task<List<string>> PublishAsync(CatalogItem item, ImportEpisode episode,
        List<SelectedVersion> versions, CancellationToken ct)
    {
        var (folder, name) = Destination(item, episode);
        await _writer.WriteOrReplaceStrmFilesAsync(folder, name, versions, ct);
        var paths = FindFiles(folder, name);
        episode.Versions = BuildVersionEvidence(paths, versions, DateTimeOffset.UtcNow);
        return paths;
    }

    public static List<ImportVersionEvidence> BuildVersionEvidence(List<string> paths,
        List<SelectedVersion> versions, DateTimeOffset now)
    {
        var evidence = new List<ImportVersionEvidence>();
        foreach (var path in paths)
        {
            var url = File.ReadAllText(path).Trim();
            var version = versions.FirstOrDefault(x => x.Stream.Url == url);
            if (version == null) continue;
            evidence.Add(new(path, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant(),
                version.Stream.ServiceLabel, version.Stream.Resolution, version.Stream.Encode,
                version.Stream.SizeBytes, now));
        }
        return evidence;
    }

    public async Task<List<string>> PublishAdditionAsync(CatalogItem item, ImportEpisode episode,
        List<SelectedVersion> versions, CancellationToken ct)
    {
        var (folder, name) = Destination(item, episode);
        // Recheck all retained files against their own evidence before an additive write.
        foreach (var path in episode.Paths)
        {
            if (!SafeManagedPath(folder, path) || !File.Exists(path) || new FileInfo(path).LinkTarget != null)
                throw new IOException("retained_file_changed");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim()))).ToLowerInvariant();
            if (!episode.Versions.Any(x => x.Path == path && x.UrlSha256 == hash))
                throw new IOException("retained_file_changed");
        }
        var before = FindFiles(folder, name);
        if (!before.ToHashSet(StringComparer.Ordinal).SetEquals(episode.Paths)) throw new IOException("retained_set_changed");
        var label = ImportDiversityPolicy.Providers(episode)?.SingleOrDefault();
        var addition = versions.FirstOrDefault(x => ImportDiversityPolicy.Provider(x.Stream.ServiceLabel) is { } provider &&
            provider != label && x.Stream.SizeBytes is > 0 and <= 40_000_000_000);
        if (addition == null || before.Count >= RuntimePolicy.EmbyVersionLimit) return new();
        var added = await _writer.WriteAdditionalStrmAsync(folder, name, addition, ct);
        // Keep old evidence verbatim; never bind new metadata to an old URL.
        episode.Versions.AddRange(BuildVersionEvidence(new() { added }, new() { addition }, DateTimeOffset.UtcNow)
            .Where(x => !episode.Versions.Any(old => old.Path == x.Path)));
        return before.Append(added).Distinct(StringComparer.Ordinal).ToList();
    }

    public bool CanAddSource(CatalogItem item, ImportEpisode episode)
    {
        try
        {
            var (folder, name) = Destination(item, episode);
            if (!FindFiles(folder, name).ToHashSet(StringComparer.Ordinal).SetEquals(episode.Paths)) return false;
            return episode.Paths.All(path => SafeManagedPath(folder, path) && File.Exists(path) &&
                new FileInfo(path).LinkTarget == null && episode.Versions.Any(x => x.Path == path &&
                x.UrlSha256 == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim()))).ToLowerInvariant()));
        }
        catch { return false; }
    }

    public void Notify(CatalogItem item)
    {
        // ReportFileSystemChanged in the writer discovers new children. The coordinator
        // coalesces this fallback to one scan per run, including pre-existing unindexed files.
        _library.QueueLibraryScan();
    }
}

public sealed class ImportProviderConfigurationException : Exception { }

public sealed class ImportHttpRateLimitException : IOException { }
