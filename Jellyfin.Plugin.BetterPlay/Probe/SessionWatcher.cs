using System.Text.Json.Nodes;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BetterPlay.Probe;

/// <summary>
/// Records playback start / progress / stop as each client reports them: how often, what position, paused or not,
/// how it is being played and, when transcoding, what the transcode is doing.
/// </summary>
public class SessionWatcher : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly ProbeLog _log;
    private readonly ILogger<SessionWatcher> _logger;

    public SessionWatcher(ISessionManager sessions, ProbeLog log, ILogger<SessionWatcher> logger)
    {
        _sessions = sessions;
        _log = log;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnStart;
        _sessions.PlaybackProgress += OnProgress;
        _sessions.PlaybackStopped += OnStopped;
        _logger.LogInformation("BetterPlay probe: watching playback sessions");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnStart;
        _sessions.PlaybackProgress -= OnProgress;
        _sessions.PlaybackStopped -= OnStopped;
        return Task.CompletedTask;
    }

    private void OnStart(object? sender, PlaybackProgressEventArgs e) => Record("start", e);

    private void OnProgress(object? sender, PlaybackProgressEventArgs e) => Record("progress", e);

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        var o = Describe(e);
        o["playedToCompletion"] = e.PlayedToCompletion;
        _log.Write("stop", o);
    }

    private void Record(string kind, PlaybackProgressEventArgs e) => _log.Write(kind, Describe(e));

    private static JsonObject Describe(PlaybackProgressEventArgs e)
    {
        var s = e.Session;
        var o = new JsonObject
        {
            ["playSessionId"] = e.PlaySessionId,
            ["sessionId"] = s?.Id,
            ["client"] = e.ClientName,
            ["device"] = e.DeviceName,
            ["deviceId"] = e.DeviceId,
            ["remote"] = s?.RemoteEndPoint,
            ["user"] = s?.UserName,
            ["itemId"] = e.Item?.Id.ToString("N"),
            ["item"] = e.Item?.Name,
            ["mediaSourceId"] = e.MediaSourceId,
            ["positionTicks"] = e.PlaybackPositionTicks,
            ["paused"] = e.IsPaused,
            ["automated"] = e.IsAutomated,
            ["playMethod"] = s?.PlayState?.PlayMethod?.ToString(),
            ["canSeek"] = s?.PlayState?.CanSeek,
        };
        var t = s?.TranscodingInfo;
        if (t is not null)
        {
            o["transcode"] = new JsonObject
            {
                ["bitrate"] = t.Bitrate,
                ["width"] = t.Width,
                ["height"] = t.Height,
                ["videoCodec"] = t.VideoCodec,
                ["audioCodec"] = t.AudioCodec,
                ["videoDirect"] = t.IsVideoDirect,
                ["audioDirect"] = t.IsAudioDirect,
                ["fps"] = t.Framerate,
                ["completion"] = t.CompletionPercentage,
                ["hw"] = t.HardwareAccelerationType?.ToString(),
                ["reasons"] = t.TranscodeReasons.ToString(),
            };
        }

        return o;
    }
}
