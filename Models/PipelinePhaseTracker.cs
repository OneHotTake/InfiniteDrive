using System;
using System.Threading;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// In-memory snapshot of which task/phase is currently active.
    /// Sprint 361: Shared operational picture for diagnostics + admin UI.
    /// No DB writes, no events — pure read-only snapshot.
    /// </summary>
    public sealed record PipelinePhase(
        string TaskName,
        string PhaseName,
        DateTimeOffset StartedAt,
        int ItemsTotal,
        int ItemsProcessed);

    public class PipelinePhaseTracker
    {
        private PipelinePhase? _current;

        public PipelinePhase? Current => Volatile.Read(ref _current);

        public void SetPhase(string taskName, string phaseName)
        {
            Volatile.Write(ref _current, new PipelinePhase(
                taskName, phaseName, DateTimeOffset.UtcNow, 0, 0));
        }

        public void ReportProgress(int processed, int total)
        {
            var existing = Current;
            if (existing == null) return;
            Volatile.Write(ref _current, existing with
            {
                ItemsProcessed = processed,
                ItemsTotal = total
            });
        }

        public void Clear()
        {
            Volatile.Write(ref _current, null);
        }
    }
}
