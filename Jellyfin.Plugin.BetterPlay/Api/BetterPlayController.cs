using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.BetterPlay.Probe;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
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
    private readonly IServerConfigurationManager _config;

    public BetterPlayController(ProbeLog log, IServerConfigurationManager config)
    {
        _log = log;
        _config = config;
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
            ["server"] = ServerSnapshot(),
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
    /// One line per play session: state (Healthy / AtRisk / Stalled / Paused / Ended / Idle) read from the timing of
    /// the player's requests, buffer ahead, late and repeated requests, seeks, and whether a proxy hides byte timing.
    /// </summary>
    [HttpGet("Sessions")]
    public ActionResult<JsonArray> Sessions([FromQuery] int minutes = 180)
    {
        var since = DateTime.UtcNow.AddMinutes(-Math.Clamp(minutes, 1, 10080));
        return SessionAnalyzer.Summarize(_log.Recent(since, null).ToList(), DateTime.UtcNow);
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

    /// <summary>Server settings that change how playback behaves or how BetterPlay reads it, plus warnings.</summary>
    private JsonObject ServerSnapshot()
    {
        var o = new JsonObject();
        var warnings = new JsonArray();
        try
        {
            var enc = _config.GetEncodingOptions();
            o["hardwareAcceleration"] = enc.HardwareAccelerationType.ToString();
            o["throttleTranscodes"] = enc.EnableThrottling;
            o["throttleDelaySeconds"] = enc.ThrottleDelaySeconds;
            o["deleteSegments"] = enc.EnableSegmentDeletion;
            o["segmentKeepSeconds"] = enc.SegmentKeepSeconds;
        }
        catch (Exception ex)
        {
            o["encodingError"] = ex.GetType().Name;
        }

        try
        {
            var net = _config.GetNetworkConfiguration();
            o["knownProxies"] = new JsonArray(net.KnownProxies.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
            o["localSubnets"] = new JsonArray(net.LocalNetworkSubnets.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
            if (_log.Stats.TryGetValue("forwarded-from-loopback", out var fwd) && fwd.Count > 0 && net.KnownProxies.Length == 0)
            {
                warnings.Add("Requests arrive from a proxy on this machine (127.0.0.1) with the viewer's real address, but Known proxies is empty, "
                    + "so Jellyfin treats remote viewers as home. Add 127.0.0.1 under Dashboard > Networking > Known proxies.");
            }
        }
        catch (Exception ex)
        {
            o["networkError"] = ex.GetType().Name;
        }

        o["warnings"] = warnings;
        return o;
    }
}
