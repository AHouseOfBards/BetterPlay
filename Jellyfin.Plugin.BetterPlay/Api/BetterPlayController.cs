using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.BetterPlay.Probe;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.BetterPlay.Api;

/// <summary>Admin-only endpoints behind the BetterPlay dashboard page.</summary>
[ApiController]
[Route("BetterPlay")]
[Authorize(Policy = Policies.RequiresElevation)]
public class BetterPlayController : ControllerBase
{
    private static readonly Regex LogName = new(@"^\d{4}-\d{2}-\d{2}\.jsonl$", RegexOptions.Compiled);
    private readonly ProbeLog _log;

    public BetterPlayController(ProbeLog log)
    {
        _log = log;
    }

    /// <summary>Version, mode, which hooks have fired on this server (and when), and the probe log files.</summary>
    [HttpGet("Status")]
    public ActionResult<JsonObject> Status()
    {
        var hooks = new JsonObject();
        foreach (var (name, stat) in _log.Stats.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            hooks[name] = new JsonObject { ["count"] = stat.Count, ["last"] = stat.Last?.ToString("O") };
        }

        var files = new JsonArray();
        if (ProbeLog.Folder is { } folder && Directory.Exists(folder))
        {
            foreach (var f in new DirectoryInfo(folder).GetFiles("*.jsonl").OrderByDescending(f => f.Name))
            {
                files.Add(new JsonObject { ["name"] = f.Name, ["kb"] = f.Length / 1024 });
            }
        }

        var cfg = ProbeLog.Config;
        return new JsonObject
        {
            ["version"] = typeof(Plugin).Assembly.GetName().Version?.ToString(),
            ["mode"] = cfg?.Mode.ToString(),
            ["recording"] = new JsonObject
            {
                ["playbackInfo"] = cfg?.RecordPlaybackInfo,
                ["playbackReports"] = cfg?.RecordPlaybackReports,
                ["videoDelivery"] = cfg?.RecordVideoDelivery,
                ["images"] = cfg?.RecordImages,
                ["deviceProfiles"] = cfg?.RecordDeviceProfiles,
            },
            ["hooks"] = hooks,
            ["droppedEvents"] = _log.Dropped,
            ["serverTime"] = DateTime.UtcNow.ToString("O"),
            ["logFiles"] = files,
        };
    }

    /// <summary>Recent events, newest last. kind: playbackinfo | start | progress | stop | delivery | image.</summary>
    [HttpGet("Probe")]
    public ActionResult<JsonArray> Probe([FromQuery] int minutes = 30, [FromQuery] string? kind = null, [FromQuery] int limit = 500)
    {
        var since = DateTime.UtcNow.AddMinutes(-Math.Clamp(minutes, 1, 10080));
        var arr = new JsonArray();
        foreach (var e in _log.Recent(since, string.IsNullOrWhiteSpace(kind) ? null : kind).TakeLast(Math.Clamp(limit, 1, 5000)))
        {
            arr.Add(e.DeepClone());
        }

        return arr;
    }

    /// <summary>
    /// One line per play session: the app, how fast the first 10 s of video arrived (startup burst), and how long
    /// segments took on average.
    /// </summary>
    [HttpGet("Sessions")]
    public ActionResult<JsonArray> Sessions([FromQuery] int minutes = 180)
    {
        var since = DateTime.UtcNow.AddMinutes(-Math.Clamp(minutes, 1, 10080));
        var events = _log.Recent(since, null).ToList();
        var clientOf = new Dictionary<string, string>();
        foreach (var e in events)
        {
            if (Str(e, "kind") is "start" or "progress" && Str(e, "playSessionId") is { Length: > 0 } ps)
            {
                clientOf[ps] = $"{Str(e, "client")} on {Str(e, "device")} ({Str(e, "playMethod")})";
            }
        }

        var arr = new JsonArray();
        var deliveries = events.Where(e => Str(e, "kind") == "delivery" && Str(e, "playSessionId") is { Length: > 0 });
        foreach (var g in deliveries.GroupBy(e => Str(e, "playSessionId")!))
        {
            var list = g.Select(e => new
            {
                T = ProbeLog.ParseTime(e) ?? DateTime.MinValue,
                Bytes = Num(e, "bytes") ?? 0,
                Ms = Num(e, "totalMs") ?? 0,
                IsSegment = Num(e, "segment") is not null,
            }).OrderBy(x => x.T).ToList();
            var first = list[0].T;
            var burst = list.Where(x => (x.T - first).TotalSeconds <= 10).ToList();
            var burstMs = burst.Sum(x => x.Ms);
            var segs = list.Where(x => x.IsSegment).ToList();
            arr.Add(new JsonObject
            {
                ["playSessionId"] = g.Key,
                ["client"] = clientOf.GetValueOrDefault(g.Key),
                ["requests"] = list.Count,
                ["mb"] = Math.Round(list.Sum(x => x.Bytes) / 1e6, 1),
                ["startupBurstMbps"] = burstMs > 0 ? Math.Round(burst.Sum(x => x.Bytes) * 8 / 1e6 / (burstMs / 1000), 1) : null,
                ["segments"] = segs.Count,
                ["avgSegmentMs"] = segs.Count > 0 ? Math.Round(segs.Average(x => x.Ms)) : null,
                ["first"] = first.ToString("O"),
                ["last"] = list[^1].T.ToString("O"),
            });
        }

        return arr;
    }

    /// <summary>Forgets the in-memory events (log files are kept).</summary>
    [HttpPost("Clear")]
    public ActionResult Clear()
    {
        _log.ClearMemory();
        return NoContent();
    }

    /// <summary>Downloads one day's probe log file (yyyy-MM-dd.jsonl).</summary>
    [HttpGet("Logs/{name}")]
    public ActionResult Log([FromRoute] string name)
    {
        if (!LogName.IsMatch(name) || ProbeLog.Folder is not { } folder)
        {
            return BadRequest();
        }

        var path = Path.Combine(folder, name);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return File(stream, "application/x-ndjson", "betterplay-" + name);
    }

    private static string? Str(JsonObject e, string key) => e[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonObject e, string key)
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
