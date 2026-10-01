using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace InfiniteDrive.Models;

public sealed record ImportStageTiming(int Count, double TotalSeconds, double MeanSeconds,
    double P95Seconds, double MaxSeconds, int SampleCount);
public sealed record ImportRunSnapshot(string Id, DateTimeOffset StartedAt, DateTimeOffset ReadAt,
    DateTimeOffset? FinishedAt, string Status, string Phase, string Title, string EpisodeKey,
    double ElapsedSeconds, double PhaseSeconds, int EpisodesChecked, int MissingAttempts,
    int RefreshAttempts, int ActiveLookups, int Matched, int EmptyResults, int TransportFailures,
    int LookupDeadlines, int Http429, int ProviderConfigurationFailures, int CancelledLookups, int Published, int Refreshed,
    int PublicationFailures, IReadOnlyDictionary<string, ImportStageTiming> Timings,
    int HttpRequests = 0, int HttpRetries = 0);

/// <summary>One bounded, thread-safe run observation. Contains no target URLs or exception messages.</summary>
public sealed class ImportRunTelemetry
{
    private static ImportRunTelemetry? _current;
    public static ImportRunSnapshot? Current => Volatile.Read(ref _current)?.Snapshot();
    private readonly object _gate = new();
    private readonly string _id;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Stopwatch _phaseTime = Stopwatch.StartNew();
    private readonly Dictionary<string, Stage> _timings = new(StringComparer.Ordinal);
    private DateTimeOffset? _finished;
    private string _status = "running", _phase = "inventory", _title = "", _episode = "";
    private int _checked, _missing, _refresh, _active, _matched, _empty, _transport, _deadlines,
        _http429, _providerConfiguration, _cancelled, _published, _refreshed, _publicationFailures;
    private int _httpRequests, _httpRetries;
    public ImportRunTelemetry(string id) { _id = id; }
    public static ImportRunTelemetry Start(string id)
    {
        var value = new ImportRunTelemetry(id); Interlocked.Exchange(ref _current, value); return value;
    }
    public void Work(string phase, string title = "", string episode = "")
    {
        lock (_gate) { _phase = phase; _title = title; _episode = episode; _phaseTime.Restart(); }
    }
    public void Checked() { lock (_gate) _checked++; }
    public static void RecordCurrentHttp(bool retry)
    {
        var current = Volatile.Read(ref _current);
        if (current == null) return;
        lock (current._gate) { current._httpRequests++; if (retry) current._httpRetries++; }
    }
    public static void RecordCurrentTiming(string stage, double seconds) => Volatile.Read(ref _current)?.RecordTiming(stage, seconds);
    public void Attempt(bool refresh) { lock (_gate) { if (refresh) _refresh++; else _missing++; } }
    public void LookupStarted() { lock (_gate) _active++; }
    public void LookupFinished(string failure)
    {
        lock (_gate)
        {
            _active--;
            switch (failure)
            {
                case "": _matched++; break;
                case "source_unavailable": _empty++; break;
                case "transport_failure": _transport++; break;
                case "lookup_deadline": _deadlines++; break;
                case "http_429": _http429++; break;
                case "provider_configuration": _providerConfiguration++; break;
                case "cancelled": _cancelled++; break;
            }
        }
    }
    public void Published(bool refresh) { lock (_gate) { _published++; if (refresh) _refreshed++; } }
    public void PublicationFailed() { lock (_gate) _publicationFailures++; }
    public void RecordTiming(string stage, double seconds)
    {
        if (stage is not ("metadata" or "observation" or "resolution" or "publication" or "checkpoint" or "dispatch_queue" or "source_http") ||
            !double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(stage));
        lock (_gate)
        {
            if (!_timings.TryGetValue(stage, out var value)) _timings[stage] = value = new();
            value.Count++; value.Total += seconds; value.Maximum = Math.Max(value.Maximum, seconds);
            // The latest 512 timings bound memory, even for a long or abnormal run.
            if (value.Samples.Count == 512) value.Samples.Dequeue();
            value.Samples.Enqueue(seconds);
        }
    }
    public void Finish(string status)
    {
        lock (_gate) { _status = status; _finished = DateTimeOffset.UtcNow; _elapsed.Stop(); _phaseTime.Stop(); _phase = "finished"; }
    }
    public ImportRunSnapshot Snapshot()
    {
        lock (_gate)
        {
            var times = _timings.ToDictionary(x => x.Key, x =>
            {
                var v = x.Value; var samples = v.Samples.OrderBy(n => n).ToArray();
                return new ImportStageTiming(v.Count, Math.Round(v.Total, 3), Math.Round(v.Total / v.Count, 3),
                    Math.Round(samples[(int)Math.Ceiling(samples.Length * .95) - 1], 3), Math.Round(v.Maximum, 3), samples.Length);
            }, StringComparer.Ordinal);
            return new(_id, _started, DateTimeOffset.UtcNow, _finished, _status, _phase, _title, _episode,
                Math.Round(_elapsed.Elapsed.TotalSeconds, 3), Math.Round(_phaseTime.Elapsed.TotalSeconds, 3),
                _checked, _missing, _refresh, _active, _matched, _empty, _transport, _deadlines, _http429,
                _providerConfiguration, _cancelled, _published, _refreshed, _publicationFailures, times, _httpRequests, _httpRetries);
        }
    }
    private sealed class Stage
    {
        public int Count; public double Total, Maximum; public Queue<double> Samples = new();
    }
}
