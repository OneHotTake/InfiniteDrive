using System.Collections.Generic;
using InfiniteDrive.Models;

namespace InfiniteDrive.Services.Scoring
{
    /// <summary>
    /// Service for scoring and selecting the best stream candidates using a bucket-based algorithm.
    /// </summary>
    public interface IStreamScoringService
    {
        /// <summary>
        /// Selects the best N candidates from a flat list using the configured
        /// bucket algorithm. Returns candidates in display order (best first).
        /// </summary>
        /// <param name="candidates">List of candidates to score and select from.</param>
        /// <returns>Ranked list of candidates, with Rank values updated to display order.</returns>
        List<StreamCandidate> SelectBest(List<StreamCandidate> candidates);
    }
}
