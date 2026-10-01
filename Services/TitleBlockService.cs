using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Services;

namespace InfiniteDrive.Services;

[Route("/InfiniteDrive/Admin/TitleBlocks", "POST", Summary = "Admin: block stable title identities; optionally archive verified managed STRMs")]
public sealed class TitleBlockRequest : IReturn<object>
{
    public List<string> Identities { get; set; } = new();
    public string ImdbId { get; set; } = "";
    public string Title { get; set; } = "";
    public string MediaType { get; set; } = "";
    public bool ArchiveStreams { get; set; }
}

[Route("/InfiniteDrive/Admin/TitleBlocks", "GET", Summary = "Admin: title blocking capability; starts no work")]
public sealed class TitleBlockCapabilities : IReturn<object> { }

public sealed class TitleBlockService : IService, IRequiresRequest
{
    private readonly IAuthorizationContext _auth;
    private readonly IApplicationPaths _paths;
    private readonly ISessionManager _sessions;
    public IRequest Request { get; set; } = null!;
    public TitleBlockService(IAuthorizationContext auth, IApplicationPaths paths, ISessionManager sessions)
    { _auth = auth; _paths = paths; _sessions = sessions; }

    public object Get(TitleBlockCapabilities request) => AdminGuard.RequireAdmin(_auth, Request)
        ?? new { Available = true, MaxTitles = 25, ArchiveVerifiedStreams = true };

    public async Task<object> Post(TitleBlockRequest request)
    {
        var deny = AdminGuard.RequireAdmin(_auth, Request);
        if (deny != null) return deny;
        var db = Plugin.Instance.DatabaseManager;
        var cfg = Plugin.Instance.Configuration;
        // Clearing files waits for an idle playback lane, never stops a session.
        if (request.ArchiveStreams && _sessions.Sessions.Any(s => s.NowPlayingItem != null))
            return new { Status = "playback_active", Message = "Try again when playback has finished." };
        var targets = (request.Identities ?? new List<string>()).Distinct(StringComparer.Ordinal).ToList();
        if (targets.Count > 25 || (targets.Count == 0 && !TitleBlockPolicy.ValidImdb(request.ImdbId)))
            return new { Status = "invalid_selection" };
        if (targets.Count > 0) await db.EnsureImportCoverageAsync();
        var outcomes = new List<object>();
        foreach (var identity in targets)
        {
            var imdb = TitleBlockPolicy.ImdbFromIdentity(identity);
            if (imdb == null)
            { outcomes.Add(new { Identity = identity, Status = "identity_requires_review" }); continue; }
            var coverage = await db.GetImportCoverageAsync(identity);
            var aliases = new List<CatalogItem>();
            foreach (var id in coverage?.CatalogIds ?? new List<string>())
            { var row = await db.GetImportCatalogByIdAsync(id); if (row != null) aliases.Add(row); }
            if (coverage == null)
            {
                aliases = await db.GetImportCatalogAliasesAsync(new CatalogItem { AioId = imdb,
                    MediaType = identity.StartsWith("series:", StringComparison.Ordinal) ? "series" : "movie" });
                coverage = new ImportCoverage { Identity = identity, Title = aliases.FirstOrDefault()?.Title ?? imdb };
            }
            if (aliases.Count == 0 || !aliases.All(x => TitleBlockPolicy.Matches(identity, x) && ImportInventory.CompatibleIdentity(aliases[0], x)))
            { outcomes.Add(new { Identity = identity, Status = "identity_requires_review" }); continue; }
            if (!await db.IsBlockedAsync(imdb, null, null))
                await db.UpsertBlockedItemAsync(imdb, null, null, coverage.Title,
                    identity.StartsWith("series:", StringComparison.Ordinal) ? "series" : "movie", "admin");
            var archived = 0; var skipped = 0;
            if (request.ArchiveStreams)
            {
                // Files from disputed/owned aliases never qualify for cleanup.
                if (coverage.Exclusion.Length != 0 || coverage.SnapshotStatus != "success" ||
                    aliases.Any(x => x.LocalSource == "library" || x.ItemState == ItemState.Retired))
                    skipped = coverage.Items.Sum(x => x.Versions.Count);
                else
                {
                    await ImportReconciliationService.MutationGate.WaitAsync();
                    try
                    {
                        var liveAliases = await db.GetImportCatalogAliasesAsync(aliases[0]);
                        if (liveAliases.Any(x => x.LocalSource == "library" || x.ItemState == ItemState.Retired ||
                            !TitleBlockPolicy.Matches(identity, x) || !ImportInventory.CompatibleIdentity(aliases[0], x)))
                        {
                            outcomes.Add(new { Identity = identity, Status = "blocked", Archived = 0,
                                Preserved = coverage.Items.Sum(x => x.Versions.Count) });
                            continue;
                        }
                        var archiveRoot = Path.Combine(_paths.DataPath, "InfiniteDrive", "blocked-streams", Guid.NewGuid().ToString("N"));
                        foreach (var version in coverage.Items.SelectMany(x => x.Versions).DistinctBy(v => v.Path))
                        {
                            if (_sessions.Sessions.Any(s => s.NowPlayingItem != null)) { skipped++; continue; }
                            var result = TitleBlockPolicy.Archive(version,
                                new[] { cfg.SyncPathMovies, cfg.SyncPathShows, cfg.SyncPathAnime }, archiveRoot);
                            if (result == "archived") archived++; else if (result != "already_absent") skipped++;
                        }
                    }
                    finally { ImportReconciliationService.MutationGate.Release(); }
                }
            }
            // Keep coverage, retries, saved selections, watch state and attempt rows.
            // An unblock authorizes natural repair; it never resets those records.
            outcomes.Add(new { Identity = identity, Status = "blocked", Archived = archived, Preserved = skipped });
        }
        if (targets.Count == 0)
        {
            if (request.MediaType is not ("movie" or "series") || (request.Title ?? "").Length is < 1 or > 200 || request.ArchiveStreams)
                return new { Status = "invalid_selection" };
            var imdb = request.ImdbId.ToLowerInvariant();
            if (!await db.IsBlockedAsync(imdb, null, null))
                await db.UpsertBlockedItemAsync(imdb, null, null, request.Title, request.MediaType, "admin");
            outcomes.Add(new { Identity = request.MediaType + ":imdb:" + imdb, Status = "blocked", Archived = 0, Preserved = 0 });
        }
        return new { Status = "complete", Outcomes = outcomes };
    }
}

internal static class TitleBlockPolicy
{
    internal static bool ValidImdb(string value) => Regex.IsMatch(value ?? "", "^tt[0-9]{5,12}$", RegexOptions.IgnoreCase);
    internal static string? ImdbFromIdentity(string identity)
    {
        var parts = (identity ?? "").Split(':');
        return parts.Length == 3 && parts[0] is "movie" or "series" && parts[1] == "imdb" && ValidImdb(parts[2])
            ? parts[2].ToLowerInvariant() : null;
    }
    internal static bool Matches(string identity, CatalogItem item) => ImportInventory.Aliases(item).Contains(identity, StringComparer.Ordinal);
    internal static string Archive(ImportVersionEvidence version, IEnumerable<string> roots, string destination)
    {
        try
        {
            if (!Path.IsPathFullyQualified(version.Path) || Path.GetExtension(version.Path) != ".strm" || version.SizeBytes is not > 0)
                return "unverified";
            var file = Path.GetFullPath(version.Path);
            var root = roots.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Path.GetFullPath)
                .FirstOrDefault(x => file.StartsWith(x.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            if (root == null) return "outside_managed_library";
            var archive = Path.GetFullPath(destination);
            if (roots.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Path.GetFullPath).Any(x =>
                archive == x || archive.StartsWith(x.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                return "archive_inside_library";
            // Refuse both leaf and parent symlinks, including the configured root.
            var cursor = new FileInfo(file) as FileSystemInfo;
            while (cursor != null)
            {
                if (cursor.LinkTarget != null) return "symlink";
                if (cursor.FullName == root) break;
                cursor = Directory.GetParent(cursor.FullName);
            }
            if (!File.Exists(file)) return "already_absent";
            if (new FileInfo(file).Length > 65536) return "unverified";
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length > 65536) return "unverified";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Trim()))).ToLowerInvariant();
            if (hash != version.UrlSha256) return "changed_evidence";
            Directory.CreateDirectory(destination);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var name = Guid.NewGuid().ToString("N") + ".strm";
            // Private archive is outside watched media. Copy+verify first because
            // DataPath and media may be different filesystems; retain recovery map.
            var target = Path.Combine(destination, name);
            File.WriteAllBytes(target, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (!File.ReadAllBytes(target).SequenceEqual(bytes)) return "archive_verification_failed";
            File.AppendAllText(Path.Combine(destination, "restore.jsonl"), JsonSerializer.Serialize(new { OriginalPath = file, ArchivedFile = name }) + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(destination, "restore.jsonl"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (!File.ReadAllBytes(file).SequenceEqual(bytes)) return "changed_evidence";
            File.Delete(file);
            return "archived";
        }
        catch { return "preserved_on_error"; }
    }
}
