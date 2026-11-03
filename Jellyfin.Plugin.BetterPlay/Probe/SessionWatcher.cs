using System.Text.Json.Nodes;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BetterPlay.Probe;

/// <summary>
/// Records playback start / progress / stop as each app reports them: how often, what position, paused or not, how
/// it is being played and, when transcoding, what the transcode is doing. Handlers never throw: they run inside
/// Jellyfin's own session handling.
/// </summary>
public sealed class SessionWatcher : IHostedService
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
        _logger.LogInformation("BetterPlay: watching playback sessions");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnStart;
        _sessions.PlaybackProgress -= OnProgress;
        _sessions.PlaybackStopped -= OnStopped;
        return Task.CompletedTask;
    }

    private void OnStart(object? sender, PlaybackProgressEventArgs e) => Record("start", e, null);

    private void OnProgress(object? sender, PlaybackProgressEventArgs e) => Record("progress", e, null);

    private void OnStopped(object? sender, PlaybackStopEventArgs e) => Record("stop", e, e.PlayedToCompletion);

    private void Record(string kind, PlaybackProgressEventArgs e, bool? playedToCompletion)
    {
        try
        {
            _log.Seen("session-events");
            if (ProbeLog.Config?.RecordPlaybackReports != true)
            {
                return;
            }

            var o = Describe(e);
            if (playedToCompletion.HasValue)
            {
                o["playedToCompletion"] = playedToCompletion.Value;
            }

            _log.Write(kind, o);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "BetterPlay: could not record a playback event");
        }
    }

    private static JsonObject Describe(PlaybackProgressEventArgs e)
    {
        var s = e.Session;
        var o = new JsonObject
        {
            ["playSessionId"] = e.PlaySessionId,
            ["sessionId"] = s?.Id,
            ["client"] = e.ClientName,
            ["clientVersion"] = s?.ApplicationVersion,
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
            ["supportsRemoteControl"] = s?.SupportsRemoteControl,
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
