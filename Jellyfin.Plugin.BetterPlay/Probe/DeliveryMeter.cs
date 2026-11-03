using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Jellyfin.Plugin.BetterPlay.Probe;

/// <summary>
/// Times every video delivery the server makes (HLS playlists and segments, direct streams): bytes sent, time to the
/// first byte, total time. Segment timing against segment length is how BetterPlay will see a client's buffer drain
/// before it freezes. Read-only: bytes pass straight through.
/// </summary>
public class DeliveryMeterStartupFilter : IStartupFilter
{
    private readonly ProbeLog _log;

    public DeliveryMeterStartupFilter(ProbeLog log)
    {
        _log = log;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(Meter);
            next(app);
        };
    }

    private async Task Meter(HttpContext context, Func<Task> next)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var kind = IsVideoDelivery(path) ? "delivery" : IsImage(path) ? "image" : null;
        if (!ProbeLog.Enabled || kind is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var original = context.Features.Get<IHttpResponseBodyFeature>();
        if (original is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var sw = Stopwatch.StartNew();
        var counter = new CountingStream(original.Stream, sw);
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(counter, original));
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            context.Features.Set(original);
            sw.Stop();
            try
            {
                var q = context.Request.Query;
                var segment = SegmentRe.Match(path);
                _log.Write(kind, new JsonObject
                {
                    ["path"] = path,
                    // HLS segment number and container (ts / mp4), to see how far ahead each app buffers.
                    ["segment"] = segment.Success ? int.Parse(segment.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null,
                    ["segmentContainer"] = segment.Success ? segment.Groups[2].Value : null,
                    ["userAgent"] = context.Request.Headers.UserAgent.ToString(),
                    ["playSessionId"] = q["PlaySessionId"].ToString(),
                    ["mediaSourceId"] = q["MediaSourceId"].ToString(),
                    ["deviceId"] = q["DeviceId"].ToString(),
                    ["range"] = context.Request.Headers.Range.ToString(),
                    ["status"] = context.Response.StatusCode,
                    ["bytes"] = counter.Bytes,
                    ["firstByteMs"] = counter.FirstByteMs,
                    ["totalMs"] = sw.Elapsed.TotalMilliseconds,
                    ["aborted"] = context.RequestAborted.IsCancellationRequested,
                    ["remote"] = context.Connection.RemoteIpAddress?.ToString(),
                    ["forwardedFor"] = context.Request.Headers["X-Forwarded-For"].ToString(),
                });
            }
            catch
            {
                // never affect delivery
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex SegmentRe =
        new(@"/hls1?/[^/]+/(\d+)\.(ts|mp4|m4s)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // Posters, backdrops and other artwork: browsing traffic, a speed sample before play is pressed.
    private static bool IsImage(string path) =>
        path.Contains("/Images/", StringComparison.OrdinalIgnoreCase)
        && (path.StartsWith("/Items/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/Users/", StringComparison.OrdinalIgnoreCase));

    private static bool IsVideoDelivery(string path) =>
        (path.StartsWith("/videos/", StringComparison.OrdinalIgnoreCase)
         && (path.Contains("/hls", StringComparison.OrdinalIgnoreCase)
             || path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
             || path.Contains("/stream", StringComparison.OrdinalIgnoreCase)))
        // Apps that read the raw file (e.g. Infuse) may use the download endpoint instead.
        || (path.StartsWith("/Items/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/Download", StringComparison.OrdinalIgnoreCase));

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly Stopwatch _sw;

        public CountingStream(Stream inner, Stopwatch sw)
        {
            _inner = inner;
            _sw = sw;
        }

        public long Bytes { get; private set; }

        public double? FirstByteMs { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Count(count);
            _inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Count(buffer.Length);
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Count(count);
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        private void Count(int n)
        {
            FirstByteMs ??= _sw.Elapsed.TotalMilliseconds;
            Bytes += n;
        }
    }
}
