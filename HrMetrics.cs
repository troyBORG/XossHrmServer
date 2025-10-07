// HrMetrics.cs
// Lightweight rolling metrics you can plug into your existing Program.cs
// Adds endpoints: GET /stats?window=60, GET /history?window=60

using System.Collections.Concurrent;

namespace XossHrmServer;

public static class HrMetrics
{
    // Rolling-window in seconds (default if not provided via query)
    private static int _defaultWindowSecs = 60;

    // Recent BPM samples (UTC ts + bpm)
    private static readonly ConcurrentQueue<(DateTimeOffset ts, int bpm)> _bpmQ = new();
    private static readonly object _pruneLock = new();

    // Recent RR samples (UTC ts + rr ms) – optional
    private static readonly ConcurrentQueue<(DateTimeOffset ts, int rr)> _rrQ = new();

    /// <summary>
    /// Call once after you build the Minimal API app to add endpoints.
    /// </summary>
    public static void MapEndpoints(WebApplication app, int defaultWindowSecs = 60)
    {
        _defaultWindowSecs = Math.Max(10, defaultWindowSecs);

        // /stats?window=60 -> rolling metrics JSON
        app.MapGet("/stats", (int? window) =>
        {
            int secs = Math.Max(10, window ?? _defaultWindowSecs);
            var snap = ComputeStats(secs);
            return snap is null ? Results.NoContent() : Results.Json(snap);
        });

        // /history?window=60 -> raw samples [{ts,bpm},...]
        app.MapGet("/history", (int? window) =>
        {
            int secs = Math.Max(10, window ?? _defaultWindowSecs);
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-secs);
            var list = new List<object>(256);
            foreach (var s in _bpmQ)
                if (s.ts >= cutoff) list.Add(new { ts = s.ts, bpm = s.bpm });
            return list.Count == 0 ? Results.NoContent() : Results.Json(list);
        });
    }

    /// <summary>
    /// Feed a new HR sample. Call this inside your HR notification handler.
    /// rrMs can be an empty collection if your device doesn’t send RR.
    /// </summary>
    public static void PushSample(DateTimeOffset tsUtc, int bpm, IReadOnlyList<int>? rrMs = null, int? battery = null, int? energy = null, int keepWindowSecs = 65)
    {
        _bpmQ.Enqueue((tsUtc, bpm));

        // prune old BPM samples (single-pass)
        var cutoff = tsUtc.AddSeconds(-Math.Max(keepWindowSecs, _defaultWindowSecs + 5));
        lock (_pruneLock)
        {
            while (_bpmQ.TryPeek(out var head) && head.ts < cutoff)
                _bpmQ.TryDequeue(out _);
        }

        // record RR (real if provided, else estimate from BPM for continuity)
        if (rrMs is { Count: > 0 })
        {
            foreach (var rr in rrMs)
                _rrQ.Enqueue((tsUtc, rr));
        }
        else
        {
            // Estimated RR from BPM – not valid HRV, but keeps charts continuous
            var est = (int)Math.Round(60000.0 / Math.Max(1, bpm));
            _rrQ.Enqueue((tsUtc, est));
        }

        // prune old RR
        while (_rrQ.TryPeek(out var rhead) && rhead.ts < cutoff)
            _rrQ.TryDequeue(out _);
    }

    // --------- metrics core ----------
    public sealed record StatsSnapshot(
        DateTimeOffset from,
        DateTimeOffset to,
        int count,
        double bpmAvg,
        int bpmMin,
        int bpmMax,
        double stdDev,
        double ratePerSec,
        double ratePer5Sec,
        double zScore,
        double? rmssd,   // null if RR were estimated (not real HRV)
        double? sdnn,    // null if RR were estimated
        IDictionary<string,int> zoneSeconds,
        bool rrEstimated
    );

    private static StatsSnapshot? ComputeStats(int windowSecs)
    {
        var now = DateTimeOffset.UtcNow;
        var from = now.AddSeconds(-windowSecs);

        // take BPM slice
        var arr = new List<(DateTimeOffset ts, int bpm)>(512);
        foreach (var s in _bpmQ)
            if (s.ts >= from) arr.Add(s);
        if (arr.Count < 2) return null;
        arr.Sort((a,b) => a.ts.CompareTo(b.ts));

        int min = int.MaxValue, max = int.MinValue;
        double sum = 0;
        foreach (var s in arr)
        {
            if (s.bpm < min) min = s.bpm;
            if (s.bpm > max) max = s.bpm;
            sum += s.bpm;
        }
        var avg = sum / arr.Count;

        double var = 0;
        foreach (var s in arr) var += Math.Pow(s.bpm - avg, 2);
        var /= arr.Count;
        var std = Math.Sqrt(var);

        var dtSec = Math.Max(1e-9, (arr[^1].ts - arr[0].ts).TotalSeconds);
        var ratePerSec = (arr[^1].bpm - arr[0].bpm) / dtSec;

        var fiveFrom = now.AddSeconds(-5);
        var idx5 = arr.FindIndex(s => s.ts >= fiveFrom);
        double rate5 = double.NaN;
        if (idx5 >= 0 && idx5 < arr.Count - 1)
        {
            var dt5 = Math.Max(1e-9, (arr[^1].ts - arr[idx5].ts).TotalSeconds);
            rate5 = (arr[^1].bpm - arr[idx5].bpm) / dt5;
        }

        var latest = arr[^1].bpm;
        var z = std > 1e-9 ? (latest - avg) / std : 0;

        // simple zones
        var zones = new Dictionary<string,int> {
            { "<90", 0 }, { "90-110", 0 }, { "110-130", 0 }, { "130-150", 0 }, { ">=150", 0 }
        };
        for (int i = 1; i < arr.Count; i++)
        {
            var secs = (int)Math.Round((arr[i].ts - arr[i-1].ts).TotalSeconds);
            secs = Math.Clamp(secs, 0, 10);
            var b = arr[i-1].bpm;
            string bucket =
                b < 90 ? "<90" :
                b < 110 ? "90-110" :
                b < 130 ? "110-130" :
                b < 150 ? "130-150" : ">=150";
            zones[bucket] += secs;
        }

        // RR slice (for HRV if *real* RR exists)
        var rrList = new List<int>(1024);
        foreach (var r in _rrQ)
            if (r.ts >= from) rrList.Add(r.rr);

        bool rrEstimated = false;
        double? rmssd = null, sdnn = null;

        if (rrList.Count >= 2)
        {
            rrEstimated = AllEqual(rrList);
            if (!rrEstimated)
            {
                // SDNN
                var rrAvg = rrList.Average(x => (double)x);
                var rrVar = rrList.Sum(x => Math.Pow(x - rrAvg, 2)) / rrList.Count;
                sdnn = Math.Sqrt(rrVar);

                // RMSSD
                double sumSq = 0; int pairs = 0;
                for (int i = 1; i < rrList.Count; i++)
                {
                    var diff = rrList[i] - rrList[i - 1];
                    sumSq += diff * diff;
                    pairs++;
                }
                if (pairs > 0) rmssd = Math.Sqrt(sumSq / pairs);
            }
        }

        return new StatsSnapshot(
            from: arr[0].ts,
            to: arr[^1].ts,
            count: arr.Count,
            bpmAvg: Math.Round(avg, 2),
            bpmMin: min,
            bpmMax: max,
            stdDev: Math.Round(std, 2),
            ratePerSec: Math.Round(double.IsFinite(ratePerSec) ? ratePerSec : 0, 3),
            ratePer5Sec: Math.Round(double.IsFinite(rate5) ? rate5 : 0, 3),
            zScore: Math.Round(z, 3),
            rmssd: rmssd is null ? null : Math.Round(rmssd.Value, 2),
            sdnn: sdnn is null ? null : Math.Round(sdnn.Value, 2),
            zoneSeconds: zones,
            rrEstimated: rrEstimated
        );
    }

    private static bool AllEqual(IEnumerable<int> xs)
    {
        using var e = xs.GetEnumerator();
        if (!e.MoveNext()) return true;
        var first = e.Current;
        while (e.MoveNext())
            if (e.Current != first) return false;
        return true;
    }
}
