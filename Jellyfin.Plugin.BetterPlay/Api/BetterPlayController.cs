using System.Text.Json.Nodes;
using Jellyfin.Plugin.BetterPlay.Probe;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.BetterPlay.Api;

/// <summary>Admin-only endpoints for reading what BetterPlay has observed.</summary>
[ApiController]
[Route("BetterPlay")]
[Authorize(Policy = "RequiresElevation")]
public class BetterPlayController : ControllerBase
{
    private readonly ProbeLog _log;

    public BetterPlayController(ProbeLog log)
    {
        _log = log;
    }

    /// <summary>Plugin version and mode; also proves the plugin's API is reachable.</summary>
    [HttpGet("Status")]
    public ActionResult<JsonObject> Status() => new JsonObject
    {
        ["version"] = typeof(Plugin).Assembly.GetName().Version?.ToString(),
        ["mode"] = Plugin.Instance?.Configuration.Mode.ToString(),
        ["probeFolder"] = Plugin.Instance is null ? null : Path.Combine(Plugin.Instance.DataFolderPath, "probe"),
    };

    /// <summary>Recent probe events, newest last. kind: playbackinfo | start | progress | stop | delivery.</summary>
    [HttpGet("Probe")]
    public ActionResult<JsonArray> Probe([FromQuery] int minutes = 30, [FromQuery] string? kind = null, [FromQuery] int limit = 2000)
    {
        var since = DateTime.UtcNow.AddMinutes(-Math.Clamp(minutes, 1, 10080));
        var arr = new JsonArray();
        foreach (var e in _log.Recent(since, kind).TakeLast(Math.Clamp(limit, 1, 5000)))
        {
            arr.Add(e.DeepClone());
        }

        return arr;
    }

    /// <summary>
    /// One line per play session: the app, how fast the first 10 s of video arrived (startup burst), and how long
    /// segments took to deliver on average.
    /// </summary>
    [HttpGet("Sessions")]
    public ActionResult<JsonArray> Sessions([FromQuery] int minutes = 180)
    {
        var since = DateTime.UtcNow.AddMinutes(-Math.Clamp(minutes, 1, 10080));
        var events = _log.Recent(since, null).ToList();
        var clientOf = new Dictionary<string, string?>();
        foreach (var e in events.Where(e => e["kind"]?.GetValue<string>() is "start" or "progress"))
        {
            var ps = e["playSessionId"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(ps))
            {
                clientOf[ps] = $"{e["client"]?.GetValue<string>()} / {e["device"]?.GetValue<string>()}";
            }
        }

        var arr = new JsonArray();
        foreach (var g in events.Where(e => e["kind"]?.GetValue<string>() == "delivery")
                     .GroupBy(e => e["playSessionId"]?.GetValue<string>() ?? string.Empty)
                     .Where(g => g.Key.Length > 0))
        {
            var list = g.Select(e => new
            {
                T = DateTime.Parse(e["t"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Bytes = e["bytes"]?.GetValue<long>() ?? 0,
                Ms = e["totalMs"]?.GetValue<double>() ?? 0,
                Segment = e["segment"]?.GetValue<int?>(),
            }).OrderBy(x => x.T).ToList();
            var first = list[0].T;
            var burst = list.Where(x => (x.T - first).TotalSeconds <= 10).ToList();
            var burstMs = burst.Sum(x => x.Ms);
            var segs = list.Where(x => x.Segment.HasValue).ToList();
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
}
