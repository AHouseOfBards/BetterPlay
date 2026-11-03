using System.Text.Json.Nodes;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.BetterPlay.Probe;

/// <summary>
/// Records each PlaybackInfo request (what the client asks for and can play) and Jellyfin's answer (the versions, in the
/// order the client receives them, and how each would be played). Read-only: the response is passed on untouched.
/// This is also the hook the Selector will use, so the probe proves it works on this server version.
/// </summary>
public class PlaybackInfoProbe : IAsyncActionFilter
{
    private readonly ProbeLog _log;

    public PlaybackInfoProbe(ProbeLog log)
    {
        _log = log;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        if (!ProbeLog.Enabled || !path.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
        {
            await next().ConfigureAwait(false);
            return;
        }

        JsonObject entry;
        try
        {
            entry = new JsonObject
            {
                ["method"] = context.HttpContext.Request.Method,
                ["path"] = path,
                ["query"] = context.HttpContext.Request.QueryString.Value,
                ["remote"] = context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                ["forwardedFor"] = context.HttpContext.Request.Headers["X-Forwarded-For"].ToString(),
                ["userAgent"] = context.HttpContext.Request.Headers.UserAgent.ToString(),
                ["auth"] = ClientFromAuth(context.HttpContext.Request.Headers.Authorization.ToString()),
            };
            var args = new JsonObject();
            foreach (var (name, value) in context.ActionArguments)
            {
                var node = ProbeLog.ToNode(value);
                if (node is JsonObject obj && Plugin.Instance?.Configuration.ProbeLogDeviceProfiles == false)
                {
                    obj.Remove("DeviceProfile");
                }

                args[name] = node;
            }

            entry["request"] = args;
        }
        catch (Exception ex)
        {
            entry = new JsonObject { ["path"] = path, ["error"] = ex.Message };
        }

        var executed = await next().ConfigureAwait(false);

        try
        {
            if (executed.Result is ObjectResult { Value: PlaybackInfoResponse response })
            {
                var sources = new JsonArray();
                foreach (var m in response.MediaSources ?? Array.Empty<MediaBrowser.Model.Dto.MediaSourceInfo>())
                {
                    var video = m.MediaStreams?.FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
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

                entry["response"] = new JsonObject
                {
                    ["playSessionId"] = response.PlaySessionId,
                    ["errorCode"] = response.ErrorCode?.ToString(),
                    ["mediaSources"] = sources,
                };
            }
            else
            {
                entry["response"] = executed.Result?.GetType().Name ?? executed.Exception?.Message;
            }
        }
        catch (Exception ex)
        {
            entry["responseError"] = ex.Message;
        }

        _log.Write("playbackinfo", entry);
    }

    // Client, Device, DeviceId, Version from the MediaBrowser authorization header, without the token.
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
