using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.BetterPlay.Probe;

/// <summary>
/// Reads each play session's health from the timing of the player's own requests, which a buffering reverse proxy
/// (nginx) cannot hide: a player asks for segment N+1 once N has fully arrived, so on a link slower than the video,
/// requests come further apart than one segment length and the buffer drains. Byte timing on the server is only kept
/// as a secondary figure, because behind a local proxy it measures the hand-off to the proxy, not the viewer.
/// </summary>
public static class SessionAnalyzer
{
    /// <summary>A gap this many segment lengths between consecutive segment requests counts as late.</summary>
    public const double LateFactor = 1.25;

    /// <summary>Server-side delivery faster than this from a loopback address is the proxy, not the viewer.</summary>
    public const double ProxyMbps = 200;

    public static JsonArray Summarize(IReadOnlyList<JsonObject> events, DateTime nowUtc)
    {
        var bySession = new Dictionary<string, List<JsonObject>>();
        foreach (var e in events)
        {
            if (Str(e, "playSessionId") is { Length: > 0 } ps && Str(e, "kind") is "delivery" or "start" or "progress" or "stop")
            {
                if (!bySession.TryGetValue(ps, out var list))
                {
                    bySession[ps] = list = new List<JsonObject>();
                }

                list.Add(e);
            }
        }

        var arr = new JsonArray();
        foreach (var (ps, list) in bySession)
        {
            arr.Add(Summarize(ps, list.OrderBy(e => ProbeLog.ParseTime(e) ?? DateTime.MinValue).ToList(), nowUtc));
        }

        return arr;
    }

    private static JsonObject Summarize(string playSessionId, List<JsonObject> events, DateTime now)
    {
        var deliveries = events.Where(e => Str(e, "kind") == "delivery").ToList();
        var reports = events.Where(e => Str(e, "kind") is "start" or "progress" or "stop").ToList();
        var lastReport = reports.LastOrDefault();

        // Segment length: from the playlist; else Jellyfin's defaults (6 s when the video is copied, 3 s transcoded).
        var segSec = deliveries.Select(e => Num(e, "segmentSeconds")).LastOrDefault(s => s is > 0);
        var segSource = "playlist";
        if (segSec is null)
        {
            var videoDirect = reports.Select(e => (e["transcode"] as JsonObject)?["videoDirect"]).LastOrDefault(v => v is not null);
            segSec = videoDirect is JsonValue v && v.TryGetValue<bool>(out var copied) ? (copied ? 6 : 3) : 6;
            segSource = "default";
        }

        double seg = segSec.Value;

        // Segment requests that sent video (304s are cache checks, -1 is the init segment).
        var segReqs = deliveries
            .Where(e => Num(e, "segment") is >= 0 && Num(e, "status") is 200 or 206)
            .Select(e => (T: ProbeLog.ParseTime(e) ?? DateTime.MinValue, Index: (int)Num(e, "segment")!.Value, Bytes: Num(e, "bytes") ?? 0, Ms: Num(e, "totalMs") ?? 0))
            .ToList();

        int reRequests = 0, seeks = 0, late = 0, recentLate = 0, maxIndex = -1;
        var seen = new HashSet<int>();
        var gaps = new List<double>();
        var lastReqTime = segReqs.Count > 0 ? segReqs[^1].T : (DateTime?)null;
        for (var i = 0; i < segReqs.Count; i++)
        {
            var r = segReqs[i];
            if (!seen.Add(r.Index))
            {
                reRequests++;
                continue;
            }

            var isSeek = false;
            if (i > 0 && maxIndex >= 0 && (r.Index < maxIndex || r.Index > maxIndex + 3))
            {
                seeks++;
                isSeek = true;
            }
            else if (i > 0 && r.Index == maxIndex + 1 && !PausedBetween(reports, segReqs[i - 1].T, r.T))
            {
                var gap = (r.T - segReqs[i - 1].T).TotalSeconds;
                gaps.Add(gap);
                if (gap > seg * LateFactor)
                {
                    late++;
                    if (lastReqTime is { } lr && (lr - r.T).TotalSeconds <= 60)
                    {
                        recentLate++;
                    }
                }
            }

            if (isSeek || r.Index > maxIndex)
            {
                maxIndex = r.Index;
            }
        }

        // Buffer ahead at the last report: video requested (minus the segment that may still be in flight) minus position.
        double? positionSec = lastReport is null ? null : Num(lastReport, "positionTicks") / 1e7;
        double? bufferAhead = null;
        if (positionSec is { } pos && lastReport is not null && ProbeLog.ParseTime(lastReport) is { } reportTime)
        {
            // The latest request, not the highest: after a seek back the player is fetching from the new position.
            var requested = segReqs.Where(r => r.T <= reportTime).Select(r => r.Index).DefaultIfEmpty(-1).Last();
            if (requested >= 0)
            {
                bufferAhead = Math.Round((requested * seg) - pos, 1);
            }
        }

        var paused = lastReport is not null && Bool(lastReport, "paused") == true;
        var ended = lastReport is not null && Str(lastReport, "kind") == "stop";
        var lastEvent = ProbeLog.ParseTime(events[^1]) ?? now;
        var stalled = !paused && !ended && PositionStuck(reports);

        // Behind a local proxy, server-side speed is the hand-off to the proxy.
        var behindProxy = deliveries.Any(e => Str(e, "remote") is "127.0.0.1" or "::1" && !string.IsNullOrEmpty(Str(e, "forwardedFor")));
        var segMbps = segReqs.Where(r => r.Ms > 0 && r.Bytes > 100_000).Select(r => r.Bytes * 8 / 1e3 / r.Ms).OrderBy(x => x).ToList();
        double? medianMbps = segMbps.Count > 0 ? Math.Round(segMbps[segMbps.Count / 2], 1) : null;

        string state;
        if (ended)
        {
            state = "Ended";
        }
        else if ((now - lastEvent).TotalMinutes > 2)
        {
            state = "Idle";
        }
        else if (paused)
        {
            state = "Paused";
        }
        else if (stalled)
        {
            state = "Stalled";
        }
        else if (recentLate >= 2 || bufferAhead is < 0 || (bufferAhead is { } b && segReqs.Count > 3 && b < seg * 2))
        {
            state = "AtRisk";
        }
        else
        {
            state = "Healthy";
        }

        return new JsonObject
        {
            ["playSessionId"] = playSessionId,
            ["client"] = lastReport is null ? null : $"{Str(lastReport, "client")} on {Str(lastReport, "device")} ({Str(lastReport, "playMethod")})",
            ["item"] = lastReport is null ? null : Str(lastReport, "item"),
            ["state"] = state,
            ["segmentSeconds"] = seg,
            ["segmentSecondsFrom"] = segSource,
            ["segments"] = seen.Count,
            ["bufferAheadSec"] = bufferAhead,
            ["lateRequests"] = late,
            ["lateRequestsLastMinute"] = recentLate,
            ["medianGapSec"] = gaps.Count > 0 ? Math.Round(gaps.OrderBy(g => g).ElementAt(gaps.Count / 2), 2) : null,
            ["reRequests"] = reRequests,
            ["seeks"] = seeks,
            ["positionSec"] = positionSec is null ? null : Math.Round(positionSec.Value),
            ["behindProxy"] = behindProxy,
            ["serverSideMbps"] = medianMbps,
            ["timingsHiddenByProxy"] = behindProxy && medianMbps is > ProxyMbps,
            ["mb"] = Math.Round(deliveries.Sum(e => Num(e, "bytes") ?? 0) / 1e6, 1),
            ["requests"] = deliveries.Count,
            ["first"] = ProbeLog.ParseTime(events[0])?.ToString("O"),
            ["last"] = lastEvent.ToString("O"),
        };
    }

    // The player reported the same position twice, at least 3 s apart, while not paused.
    private static bool PositionStuck(List<JsonObject> reports)
    {
        var live = reports.Where(r => Str(r, "kind") == "progress").TakeLast(2).ToList();
        if (live.Count < 2 || live.Any(r => Bool(r, "paused") == true))
        {
            return false;
        }

        var (a, b) = (live[0], live[1]);
        var dt = (ProbeLog.ParseTime(b) - ProbeLog.ParseTime(a))?.TotalSeconds ?? 0;
        return dt >= 3 && Num(a, "positionTicks") is { } pa && Num(b, "positionTicks") is { } pb && Math.Abs(pb - pa) < 5e6;
    }

    private static bool PausedBetween(List<JsonObject> reports, DateTime from, DateTime to) =>
        reports.Any(r => Bool(r, "paused") == true && ProbeLog.ParseTime(r) is { } t && t >= from.AddSeconds(-1) && t <= to);

    private static string? Str(JsonObject e, string key) => e[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Bool(JsonObject e, string key) => e[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    internal static double? Num(JsonObject e, string key)
    {
        if (e[key] is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (v.TryGetValue<long>(out var l))
        {
            return l;
        }

        return v.TryGetValue<int>(out var i) ? i : null;
    }
}
