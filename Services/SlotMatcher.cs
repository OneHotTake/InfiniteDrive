using System;
using System.Collections.Generic;
using System.Linq;
using EmbyStreams.Models;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Filters and ranks normalized <see cref="Candidate"/> objects against each
    /// enabled <see cref="VersionSlot"/>'s matching policy.
    ///
    /// Matching pipeline per slot:
    /// <list type="number">
    ///   <item>Resolution band filter</item>
    ///   <item>Video codec allowlist filter</item>
    ///   <item>HDR class filter</item>
    ///   <item>Multi-key ranking: audio preference → bitrate → confidence → cache</item>
    ///   <item>Sequential rank assignment (0 = best)</item>
    /// </list>
    ///
    /// If no candidates match a slot, the slot is silently absent from the result.
    /// This is intentional — the versioned playback system tolerates partial coverage.
    /// </summary>
    public class SlotMatcher
    {
        // ── Resolution band definitions ───────────────────────────────────────
        // Maps normalized resolution strings to their numeric pixel-height band
        // for range-based comparisons (e.g. "720p" slot accepts ≤720p).

        private static readonly Dictionary<string, int> ResolutionBand = new(StringComparer.OrdinalIgnoreCase)
        {
            ["2160p"] = 2160,
            ["1440p"] = 1440,
            ["1080p"] = 1080,
            ["720p"]  = 720,
            ["480p"]  = 480,
            ["360p"]  = 360,
            ["240p"]  = 240,
        };

        /// <summary>
        /// Matches normalized candidates against all enabled slots, returning a
        /// dictionary of <c>slot_key → ranked candidate list</c>.
        ///
        /// Candidates with <see cref="Candidate.SlotKey"/> already set will be
        /// overwritten with the matching slot key.
        /// </summary>
        /// <param name="enabledSlots">All currently enabled slots.</param>
        /// <param name="normalizedCandidates">Flat list of slot-agnostic candidates from <c>CandidateNormalizer</c>.</param>
        /// <returns>Slot key → ordered list of candidates (rank 0 = best). Absent key = no match.</returns>
        public Dictionary<string, List<Candidate>> MatchToAllSlots(
            List<VersionSlot> enabledSlots,
            List<Candidate> normalizedCandidates)
        {
            var result = new Dictionary<string, List<Candidate>>(StringComparer.OrdinalIgnoreCase);

            if (enabledSlots == null || enabledSlots.Count == 0)
                return result;

            if (normalizedCandidates == null || normalizedCandidates.Count == 0)
                return result;

            foreach (var slot in enabledSlots)
            {
                var matched = MatchToSlot(slot, normalizedCandidates);
                if (matched.Count > 0)
                    result[slot.SlotKey] = matched;
            }

            return result;
        }

        /// <summary>
        /// Matches and ranks candidates for a single slot.
        /// Returns an empty list if no candidates qualify.
        /// </summary>
        public List<Candidate> MatchToSlot(
            VersionSlot slot,
            List<Candidate> normalizedCandidates)
        {
            if (slot == null || normalizedCandidates == null || normalizedCandidates.Count == 0)
                return new List<Candidate>();

            // Phase 1: Filter by slot policy
            var filtered = normalizedCandidates
                .Where(c => MatchesResolution(slot, c))
                .Where(c => MatchesVideoCodec(slot, c))
                .Where(c => MatchesHdrClass(slot, c))
                .ToList();

            if (filtered.Count == 0)
                return new List<Candidate>();

            // Phase 2: Rank candidates
            var ranked = RankCandidates(slot, filtered);

            // Phase 3: Assign sequential rank and slot key
            for (int i = 0; i < ranked.Count; i++)
            {
                ranked[i].Rank = i;
                ranked[i].SlotKey = slot.SlotKey;
            }

            return ranked;
        }

        // ════════════════════════════════════════════════════════════════════════
        //  FILTER 1 — Resolution band
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Checks whether a candidate's resolution falls within the slot's target band.
        ///
        /// Rules:
        /// <list type="bullet">
        ///   <item><c>highest</c> → accepts any resolution</item>
        ///   <item><c>1080p</c> → accepts 1080p only</item>
        ///   <item><c>2160p</c> → accepts 2160p only</item>
        ///   <item><c>720p</c> → accepts 720p and lower</item>
        /// </list>
        /// </summary>
        private static bool MatchesResolution(VersionSlot slot, Candidate candidate)
        {
            var slotRes = slot.Resolution?.ToLowerInvariant().Trim() ?? "";
            var candRes = candidate.Resolution?.ToLowerInvariant().Trim() ?? "";

            // "highest" → accept everything
            if (slotRes == "highest")
                return true;

            // Candidate has no resolution info — only match "highest" or unknown slots
            if (string.IsNullOrEmpty(candRes))
                return false;

            var slotBand = GetResolutionBand(slotRes);
            var candBand = GetResolutionBand(candRes);

            // Could not parse either — reject
            if (slotBand == 0 || candBand == 0)
                return false;

            // Exact resolution match for 1080p and 2160p
            if (slotRes is "1080p" or "2160p")
                return candBand == slotBand;

            // 720p accepts 720p and lower (band ≤ 720)
            if (slotRes == "720p")
                return candBand <= 720;

            // Generic: exact match
            return candBand == slotBand;
        }

        /// <summary>
        /// Converts a resolution string to its numeric pixel-height value.
        /// Returns 0 for unrecognized values.
        /// </summary>
        private static int GetResolutionBand(string res)
        {
            if (string.IsNullOrEmpty(res))
                return 0;

            // Direct lookup for normalized forms
            if (ResolutionBand.TryGetValue(res, out var band))
                return band;

            // Try parsing a raw number (e.g. "1080")
            if (int.TryParse(res, out var numeric))
                return numeric;

            return 0;
        }

        // ════════════════════════════════════════════════════════════════════════
        //  FILTER 2 — Video codec allowlist
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Checks whether a candidate's video codec is in the slot's allowlist.
        /// If the slot accepts any codec (<see cref="VersionSlot.AcceptsAnyCodec"/>),
        /// all candidates pass.
        /// </summary>
        private static bool MatchesVideoCodec(VersionSlot slot, Candidate candidate)
        {
            // Slot accepts any codec
            if (slot.AcceptsAnyCodec)
                return true;

            // Candidate has no codec info — reject (slot has a specific allowlist)
            var codec = candidate.VideoCodec?.ToLowerInvariant().Trim();
            if (string.IsNullOrEmpty(codec))
                return false;

            var allowlist = slot.VideoCodecList;
            return allowlist.Contains(codec);
        }

        // ════════════════════════════════════════════════════════════════════════
        //  FILTER 3 — HDR class
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Checks whether a candidate's HDR class matches the slot's HDR policy.
        ///
        /// Rules:
        /// <list type="bullet">
        ///   <item>Slot <see cref="VersionSlot.IsSdrOnly"/> (empty list) → candidate must have no HDR class</item>
        ///   <item>Slot <see cref="VersionSlot.AcceptsAnyHdr"/> ("any") → accepts all HDR classes</item>
        ///   <item>Specific list → candidate HDR class must be in the list</item>
        /// </list>
        /// </summary>
        private static bool MatchesHdrClass(VersionSlot slot, Candidate candidate)
        {
            var hdrClasses = slot.HdrClassList;
            var candHdr = candidate.HdrClass?.ToLowerInvariant().Trim() ?? "";

            // Slot is SDR-only → candidate must not have any HDR class
            if (hdrClasses.Count == 0)
                return string.IsNullOrEmpty(candHdr);

            // Slot accepts any HDR → all candidates pass
            if (hdrClasses.Contains("any"))
                return true;

            // Specific HDR allowlist → candidate must match one
            if (string.IsNullOrEmpty(candHdr))
                return false;

            return hdrClasses.Contains(candHdr);
        }

        // ════════════════════════════════════════════════════════════════════════
        //  RANKING — Multi-key sort
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Ranks candidates by the slot's priority order:
        /// <list type="number">
        ///   <item>Audio preference position (lower index in slot's list = better)</item>
        ///   <item>Bitrate descending (higher = better)</item>
        ///   <item>Confidence score descending (higher = better)</item>
        ///   <item>Cache status (cached = better)</item>
        /// </list>
        /// </summary>
        private static List<Candidate> RankCandidates(VersionSlot slot, List<Candidate> candidates)
        {
            var audioPrefs = slot.AudioPreferenceList;

            // Build audio codec → preference index map (lower = preferred)
            var audioRankMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < audioPrefs.Count; i++)
            {
                // Only store first occurrence — first = highest preference
                if (!audioRankMap.ContainsKey(audioPrefs[i]))
                    audioRankMap[audioPrefs[i]] = i;
            }

            return candidates
                .OrderBy(c => AudioPreferenceRank(c, audioRankMap))
                .ThenByDescending(c => c.BitrateKbps ?? 0)
                .ThenByDescending(c => c.ConfidenceScore)
                .ThenByDescending(c => c.IsCached ? 1 : 0)
                .ToList();
        }

        /// <summary>
        /// Returns the audio preference index for a candidate.
        /// Lower = better match. Candidates with unknown audio get <c>int.MaxValue</c>
        /// so they rank after all known audio codecs.
        /// </summary>
        private static int AudioPreferenceRank(
            Candidate candidate,
            Dictionary<string, int> audioRankMap)
        {
            var codec = candidate.AudioCodec?.ToLowerInvariant().Trim();

            if (string.IsNullOrEmpty(codec))
                return int.MaxValue;

            return audioRankMap.TryGetValue(codec, out var rank)
                ? rank
                : int.MaxValue;
        }
    }
}
