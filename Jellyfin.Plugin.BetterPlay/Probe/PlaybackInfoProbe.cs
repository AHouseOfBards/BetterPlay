using System.Text.Json.Nodes;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BetterPlay.Probe;

/// <summary>
/// Records each PlaybackInfo request (what the app asks for and can play) and Jellyfin's answer (the versions, in the
/// order the app receives them, and how each would be played). Read-only: the response passes on untouched, and any
/// problem in here is swallowed. This is also the hook the Selector will use, so the probe proves it works.
/// </summary>
public sealed class PlaybackInfoProbe : IAsyncActionFilter
{
    private readonly ProbeLog _log;
    private readonly ILogger<PlaybackInfoProbe> _logger;

    public PlaybackInfoProbe(ProbeLog log, ILogger<PlaybackInfoProbe> logger)
    {
        _log = log;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        _log.Seen("api-filter");
        if (!path.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
        {
            await next().ConfigureAwait(false);
            return;
        }

        _log.Seen("playbackinfo-hook");
        var record = ProbeLog.Enabled && ProbeLog.Config?.RecordPlaybackInfo == true;
        var entry = record ? DescribeRequest(context, path) : null;

        var executed = await next().ConfigureAwait(false);

        if (entry is null)
        {
            return;
        }

        try
        {
            entry["response"] = executed.Result is ObjectResult { Value: PlaybackInfoResponse response }
                ? DescribeResponse(response)
                : executed.Result?.GetType().Name ?? executed.Exception?.GetType().Name;
            _log.Write("playbackinfo", entry);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "BetterPlay: could not record a PlaybackInfo response");
        }
    }

    private JsonObject? DescribeRequest(ActionExecutingContext context, string path)
    {
        try
        {
            var http = context.HttpContext;
            var entry = new JsonObject
            {
                ["method"] = http.Request.Method,
                ["path"] = path,
                ["query"] = http.Request.QueryString.Value,
                ["remote"] = http.Connection.RemoteIpAddress?.ToString(),
                ["forwardedFor"] = http.Request.Headers["X-Forwarded-For"].ToString(),
                ["userAgent"] = http.Request.Headers.UserAgent.ToString(),
                ["auth"] = ClientFromAuth(http.Request.Headers.Authorization.ToString()),
            };
            var args = new JsonObject();
            foreach (var (name, value) in context.ActionArguments)
            {
                var node = ProbeLog.ToNode(value);
                if (node is JsonObject obj && ProbeLog.Config?.RecordDeviceProfiles != true)
                {
                    obj.Remove("DeviceProfile");
                }

                args[name] = node;
            }

            entry["request"] = args;
            return entry;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "BetterPlay: could not record a PlaybackInfo request");
            return null;
        }
    }

    private static JsonObject DescribeResponse(PlaybackInfoResponse response)
    {
        var sources = new JsonArray();
        foreach (var m in response.MediaSources ?? Array.Empty<MediaSourceInfo>())
        {
            var video = m.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            sources.Add(new JsonObject
            {
                ["id"] = m.Id,
                ["name"] = m.Name,
                ["path"] = m.Path,
                ["bitrate"] = m.Bitrate,
                ["container"] = m.Container,
                ["width"] = video?.Width,
                ["height"] = video?.Height,
                ["codec"] = video?.Codec,
                ["range"] = video?.VideoRangeType.ToString(),
                ["runTimeTicks"] = m.RunTimeTicks,
                ["directPlay"] = m.SupportsDirectPlay,
                ["directStream"] = m.SupportsDirectStream,
                ["transcode"] = m.SupportsTranscoding,
                ["transcodingUrl"] = m.TranscodingUrl,
                ["transcodeReasons"] = m.TranscodeReasons.ToString(),
            });
        }

        return new JsonObject
        {
            ["playSessionId"] = response.PlaySessionId,
            ["errorCode"] = response.ErrorCode?.ToString(),
            ["mediaSources"] = sources,
        };
    }

    // Client, Device, DeviceId, Version from the MediaBrowser authorization header; never the token.
    private static JsonObject ClientFromAuth(string header)
    {
        var o = new JsonObject();
        foreach (var part in header.Split(','))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            var key = kv[0].Replace("MediaBrowser", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (key is "Client" or "Device" or "DeviceId" or "Version")
            {
                o[key] = Uri.UnescapeDataString(kv[1].Trim('"'));
            }
        }

        return o;
    }
}
