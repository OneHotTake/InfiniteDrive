using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Outbound HTTP client for AIOStreams' OWN config API (<c>/api/v1/user</c>).
    /// This is NOT a new InfiniteDrive endpoint — it is a client call to the user's
    /// existing AIOStreams instance, the same way we fetch manifests and streams.
    ///
    /// Scope is deliberately narrow: read the user's config, and (on explicit opt-in)
    /// overwrite ONLY the <c>formatter</c> and <c>sortCriteria</c> fields. Catalogs,
    /// presets, services, and everything else are preserved byte-for-byte.
    ///
    /// Verified contract (AIOStreams v2.30):
    ///   GET  /api/v1/user   Basic(uuid:password) → { data: { userData, encryptedPassword } }
    ///   PUT  /api/v1/user   Basic(uuid:password)  body { "config": userData, "password": pw }
    /// A PUT rotates encryptedPassword (fresh AES nonce) but old manifest URLs keep
    /// working — the token is a self-contained encrypted password, so writes are
    /// non-breaking and idempotent.
    /// </summary>
    public static class AioStreamsConfigClient
    {
        // ── Recommended formatter (reuses only tokens/modifiers proven on v2.30) ──

        /// <summary>Clean, deterministic version-picker label, e.g. "2160p BluRay REMUX HEVC DV ⚡".</summary>
        public const string RecommendedName =
            "{stream.resolution::exists[\"{stream.resolution} \"||\"\"]}" +
            "{stream.quality::exists[\"{stream.quality} \"||\"\"]}" +
            "{stream.encode::exists[\"{stream.encode} \"||\"\"]}" +
            "{stream.visualTags::exists[\"{stream.visualTags::join('/')} \"||\"\"]}" +
            "{service.cached::istrue[\"⚡\"||\"\"]}";

        /// <summary>Deterministic, delimited description block.</summary>
        public const string RecommendedDescription =
            "{stream.title::exists[\"{stream.title} \"||\"\"]}{stream.year::exists[\"({stream.year})\"||\"\"]}\n" +
            "{stream.size::>0[\"{stream.size::rbytes} • \"||\"\"]}" +
            "{stream.audioTags::exists[\"{stream.audioTags::join('/')} \"||\"\"]}" +
            "{stream.audioChannels::exists[\"{stream.audioChannels::join('/')} \"||\"\"]}" +
            "{stream.releaseGroup::exists[\"• {stream.releaseGroup}\"||\"\"]}";

        /// <summary>Recommended global sort order (best-quality-first, instant-play high).</summary>
        public static readonly (string Key, string Direction)[] RecommendedSort =
        {
            ("resolution", "desc"),
            ("quality",    "desc"),
            ("cached",     "desc"),
            ("seadex",     "desc"),
            ("visualTag",  "desc"),
            ("audioTag",   "desc"),
            ("bitrate",    "desc"),
        };

        // ── Result types ─────────────────────────────────────────────────────────

        public class ApplyResult
        {
            public bool Ok { get; set; }
            public bool Changed { get; set; }
            public bool DryRun { get; set; }
            public string Message { get; set; } = string.Empty;
            public string Diff { get; set; } = string.Empty;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the config, replaces ONLY formatter + sortCriteria with the recommended
        /// values, and (unless <paramref name="dryRun"/>) writes it back. Returns a
        /// human-readable before/after diff. Everything else in the config is preserved.
        /// </summary>
        public static async Task<ApplyResult> ApplyRecommendedAsync(
            string manifestUrl, string password, bool dryRun, HttpClient? httpClient = null,
            CancellationToken ct = default)
        {
            var result = new ApplyResult { DryRun = dryRun };

            var components = ManifestUrlParser.Parse(manifestUrl);
            if (components == null || string.IsNullOrWhiteSpace(components.BaseUrl)
                || string.IsNullOrWhiteSpace(components.UserId))
            {
                result.Message = "Could not parse the manifest URL (need a /stremio/{uuid}/{token}/manifest.json form).";
                return result;
            }
            if (string.IsNullOrWhiteSpace(password))
            {
                result.Message = "Password is required to edit your AIOStreams config.";
                return result;
            }

            var apiUrl = $"{components.BaseUrl}/api/v1/user";
            var http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{components.UserId}:{password}"));

            // 1. GET current config.
            JsonObject userData;
            try
            {
                using var getReq = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                getReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);
                using var getResp = await http.SendAsync(getReq, ct).ConfigureAwait(false);
                if (getResp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    || getResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    result.Message = "Authentication failed — check the password for this AIOStreams instance.";
                    return result;
                }
                if (!getResp.IsSuccessStatusCode)
                {
                    result.Message = $"Could not read config (HTTP {(int)getResp.StatusCode}).";
                    return result;
                }
                var getJson = await getResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var root = JsonNode.Parse(getJson)?.AsObject();
                var ud = root?["data"]?["userData"]?.AsObject();
                if (ud == null)
                {
                    result.Message = "Config response did not contain userData.";
                    return result;
                }
                userData = ud;
            }
            catch (Exception ex)
            {
                result.Message = $"Failed to read config: {ex.Message}";
                return result;
            }

            // 2-4. Mutate ONLY formatter + sortCriteria; capture diff. (Pure, tested.)
            var (changed, diff) = ApplyRecommendedToUserData(userData);
            result.Changed = changed;
            result.Diff = diff;

            if (!changed)
            {
                result.Ok = true;
                result.Message = "Already matches InfiniteDrive's recommended formatter & sort — nothing to change.";
                return result;
            }

            if (dryRun)
            {
                result.Ok = true;
                result.Message = "Preview only — nothing written. Confirm to apply.";
                return result;
            }

            // 5. PUT { config: userData, password }.
            try
            {
                var body = new JsonObject
                {
                    ["config"] = userData.DeepClone(),
                    ["password"] = password,
                };
                using var putReq = new HttpRequestMessage(HttpMethod.Put, apiUrl)
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                putReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);
                using var putResp = await http.SendAsync(putReq, ct).ConfigureAwait(false);
                var putJson = await putResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!putResp.IsSuccessStatusCode)
                {
                    result.Message = $"Write failed (HTTP {(int)putResp.StatusCode}): {Trunc(putJson, 160)}";
                    return result;
                }
                var ok = JsonNode.Parse(putJson)?["success"]?.GetValue<bool>() ?? false;
                result.Ok = ok;
                result.Message = ok
                    ? "Applied — your AIOStreams now uses InfiniteDrive's formatter & sort. Existing manifest URL still works."
                    : $"Server rejected the update: {Trunc(putJson, 160)}";
                return result;
            }
            catch (Exception ex)
            {
                result.Message = $"Write failed: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Pure mutation: overwrites ONLY <c>formatter</c> (id + custom name/description)
        /// and <c>sortCriteria.global</c> on the given userData object in place; every
        /// other field is left untouched. Returns whether anything changed and a
        /// human-readable diff. Extracted for unit testing the safety guarantee.
        /// </summary>
        public static (bool Changed, string Diff) ApplyRecommendedToUserData(JsonObject userData)
        {
            var beforeName = userData["formatter"]?["definitions"]?["custom"]?["name"]?.GetValue<string>() ?? "(none)";
            var beforeDesc = userData["formatter"]?["definitions"]?["custom"]?["description"]?.GetValue<string>() ?? "(none)";
            var beforeSort = DescribeSort(userData["sortCriteria"]?["global"]?.AsArray());

            var formatter = EnsureObject(userData, "formatter");
            formatter["id"] = "custom";
            var definitions = EnsureObject(formatter, "definitions");
            var custom = EnsureObject(definitions, "custom");
            custom["name"] = RecommendedName;
            custom["description"] = RecommendedDescription;

            var sortCriteria = EnsureObject(userData, "sortCriteria");
            var globalArr = new JsonArray();
            foreach (var (key, dir) in RecommendedSort)
                globalArr.Add(new JsonObject { ["key"] = key, ["direction"] = dir });
            sortCriteria["global"] = globalArr;

            var afterSort = DescribeSort(globalArr);

            bool changed = beforeName != RecommendedName || beforeDesc != RecommendedDescription || beforeSort != afterSort;
            var diff =
                $"Formatter name:\n  was: {Trunc(beforeName)}\n  now: {Trunc(RecommendedName)}\n"
              + $"Formatter description: updated to deterministic block\n"
              + $"Sort order:\n  was: {beforeSort}\n  now: {afterSort}\n"
              + "(catalogs, presets, services, and all other settings are unchanged)";
            return (changed, diff);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static JsonObject EnsureObject(JsonObject parent, string key)
        {
            if (parent[key] is JsonObject existing) return existing;
            var created = new JsonObject();
            parent[key] = created;
            return created;
        }

        private static string DescribeSort(JsonArray? arr)
        {
            if (arr == null || arr.Count == 0) return "(none)";
            var sb = new StringBuilder();
            foreach (var n in arr)
            {
                var key = n?["key"]?.GetValue<string>();
                if (string.IsNullOrEmpty(key)) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(key);
            }
            return sb.Length == 0 ? "(none)" : sb.ToString();
        }

        private static string Trunc(string s, int max = 80)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
