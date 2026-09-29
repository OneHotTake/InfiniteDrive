using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using InfiniteDrive.Models;

namespace InfiniteDrive.Services;

/// <summary>Delivery service, distinct from release quality or the source addon.</summary>
public static class StreamServiceLabel
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["torbox"] = "TorBox", ["realdebrid"] = "Real-Debrid",
        ["alldebrid"] = "AllDebrid", ["premiumize"] = "Premiumize",
        ["debridlink"] = "Debrid-Link", ["offcloud"] = "Offcloud",
        ["easydebrid"] = "EasyDebrid", ["debrider"] = "Debrider",
        ["easynews"] = "Easynews", ["nzbdav"] = "NZBDav",
        ["altmount"] = "AltMount", ["stremthru"] = "StremThru",
        ["stremionntp"] = "Usenet", ["nntp"] = "Usenet", ["usenet"] = "Usenet"
    };

    public static string Parse(AioStreamsStream stream)
    {
        // Extended responses can identify any service, including ones unknown to this build.
        var structured = Clean(stream.Service?.Name) ?? Clean(stream.Service?.Id);
        if (structured != null) return Canonical(structured);

        // Standard Stremio responses omit extensions. Accept explicit service fields,
        // or a whole known service token in the formatter; never inspect signed URLs,
        // filenames, addon names, or assume an arbitrary trailing release group is a service.
        foreach (var text in new[] { stream.Description, stream.Title, stream.Name })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (var part in Regex.Split(text, @"[\r\n·•|]+"))
            {
                var token = part.Trim();
                var explicitService = Regex.Match(token, @"^(?:service|provider)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                if (explicitService.Success && Clean(explicitService.Groups[1].Value) is { } named)
                    return Canonical(named);
                if (Clean(token) is { } clean && Names.TryGetValue(Key(clean), out var known))
                    return known;
            }
        }
        return string.Equals(stream.StreamType, "usenet", StringComparison.OrdinalIgnoreCase) ? "Usenet" : "";
    }

    private static string Key(string text) => Regex.Replace(text, @"[\s_.-]", "");
    private static string Canonical(string text) => Names.TryGetValue(Key(text), out var known) ? known : text;
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        // A display label is short text, not a URL, path, credential, or filename fragment.
        if (!Regex.IsMatch(text, @"^[\p{L}\p{N}][\p{L}\p{N} .&()_-]{0,47}$") ||
            Regex.IsMatch(text, @"^[a-fA-F0-9]{24,}$")) return null;
        return text;
    }
}
