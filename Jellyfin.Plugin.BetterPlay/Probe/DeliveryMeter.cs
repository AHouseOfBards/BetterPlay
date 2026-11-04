using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Jellyfin.Plugin.BetterPlay.Probe;

/// <summary>
/// Times every video delivery (HLS playlists and segments, direct streams, downloads) and artwork download: bytes sent,
/// time to first byte, total time. Segment timing against segment length is how BetterPlay will see an app's buffer
/// drain before it freezes. Strictly pass-through: it only counts. Every write, file send and flush goes straight to
/// Jellyfin's own response, unchanged and unbuffered.
/// </summary>
public sealed class DeliveryMeterStartupFilter : IStartupFilter
{
    private static readonly Regex SegmentRe = new(@"/hls1?/[^/]+/(-?\d+)\.(ts|mp4|m4s)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExtInfRe = new(@"#EXTINF:([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex TargetDurationRe = new(@"#EXT-X-TARGETDURATION:(\d+)", RegexOptions.Compiled);

    // Enough of a playlist to read its segment length; the rest passes through uncopied.
    private const int PlaylistHeadBytes = 8192;

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
        _log.Seen("middleware");
        string? kind = null;
        var isPlaylist = false;
        try
        {
            var path = context.Request.Path.Value ?? string.Empty;
            // A local reverse proxy (nginx on the same machine) without KnownProxies: Jellyfin sees everyone as home.
            if (context.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip) && context.Request.Headers.ContainsKey("X-Forwarded-For"))
            {
                _log.Seen("forwarded-from-loopback");
            }

            isPlaylist = path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
            var cfg = ProbeLog.Config;
            if (ProbeLog.Enabled && cfg is not null && !context.WebSockets.IsWebSocketRequest)
            {
                kind = IsVideoDelivery(path) ? (cfg.RecordVideoDelivery ? "delivery" : null)
                     : IsImage(path) ? (cfg.RecordImages ? "image" : null)
                     : null;
            }
        }
        catch
        {
            kind = null;
        }

        var original = kind is null ? null : context.Features.Get<IHttpResponseBodyFeature>();
        if (original is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var sw = Stopwatch.StartNew();
        var counter = new CountingBodyFeature(original, sw, isPlaylist ? PlaylistHeadBytes : 0);
        context.Features.Set<IHttpResponseBodyFeature>(counter);
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            context.Features.Set(original);
            sw.Stop();
            Record(context, kind!, counter, sw.Elapsed.TotalMilliseconds);
        }
    }

    private void Record(HttpContext context, string kind, CountingBodyFeature counter, double totalMs)
    {
        try
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var q = context.Request.Query;
            var segment = SegmentRe.Match(path);
            _log.Seen(kind + "-meter");
            var o = new JsonObject
            {
                ["path"] = path,
                ["playSessionId"] = q["PlaySessionId"].ToString(),
                ["mediaSourceId"] = q["MediaSourceId"].ToString(),
                ["deviceId"] = q["DeviceId"].ToString(),
                // HLS segment number and container (ts / mp4), to see how far ahead each app buffers.
                ["segment"] = segment.Success && int.TryParse(segment.Groups[1].Value, out var n) ? n : null,
                ["segmentContainer"] = segment.Success ? segment.Groups[2].Value : null,
                ["range"] = context.Request.Headers.Range.ToString(),
                ["status"] = context.Response.StatusCode,
                ["contentLength"] = context.Response.ContentLength,
                ["bytes"] = counter.Bytes,
                ["firstByteMs"] = counter.FirstByteMs,
                ["totalMs"] = Math.Round(totalMs, 1),
                ["aborted"] = context.RequestAborted.IsCancellationRequested,
                ["userAgent"] = context.Request.Headers.UserAgent.ToString(),
                ["remote"] = context.Connection.RemoteIpAddress?.ToString(),
                ["forwardedFor"] = context.Request.Headers["X-Forwarded-For"].ToString(),
            };

            // Segment length, so request timing can be read against it (a player on a too-slow link asks for the next
            // segment later than one segment length after the last).
            if (counter.Head is { } head && head.StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                var inf = ExtInfRe.Match(head);
                var target = TargetDurationRe.Match(head);
                o["segmentSeconds"] = inf.Success && double.TryParse(inf.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : null;
                o["targetDuration"] = target.Success && int.TryParse(target.Groups[1].Value, out var td) ? td : null;
            }

            _log.Write(kind, o);
        }
        catch
        {
            // never affect delivery
        }
    }

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

    /// <summary>Delegates everything to Jellyfin's response body; only adds up what passes through.</summary>
    private sealed class CountingBodyFeature : IHttpResponseBodyFeature
    {
        private readonly IHttpResponseBodyFeature _inner;
        private readonly Stopwatch _sw;
        private readonly byte[]? _head;
        private int _headLength;
        private long _bytes;
        private long _firstByteTicks = -1;

        public CountingBodyFeature(IHttpResponseBodyFeature inner, Stopwatch sw, int headBytes)
        {
            _inner = inner;
            _sw = sw;
            _head = headBytes > 0 ? new byte[headBytes] : null;
            Stream = new CountingStream(inner.Stream, this);
            Writer = new CountingPipeWriter(inner.Writer, this);
        }

        public Stream Stream { get; }

        public PipeWriter Writer { get; }

        public long Bytes => Interlocked.Read(ref _bytes);

        public double? FirstByteMs => _firstByteTicks < 0 ? null : Math.Round(TimeSpan.FromTicks(_firstByteTicks).TotalMilliseconds, 1);

        /// <summary>The start of the response as text, when asked for (playlists only).</summary>
        public string? Head => _head is null || _headLength == 0 ? null : System.Text.Encoding.UTF8.GetString(_head, 0, _headLength);

        public void Add(ReadOnlySpan<byte> data)
        {
            if (_head is not null && _headLength < _head.Length)
            {
                var n = Math.Min(data.Length, _head.Length - _headLength);
                data[..n].CopyTo(_head.AsSpan(_headLength));
                _headLength += n;
            }

            Add(data.Length);
        }

        public void Add(long n)
        {
            if (n <= 0)
            {
                return;
            }

            Interlocked.CompareExchange(ref _firstByteTicks, _sw.Elapsed.Ticks, -1);
            Interlocked.Add(ref _bytes, n);
        }

        public void DisableBuffering() => _inner.DisableBuffering();

        public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

        public Task CompleteAsync() => _inner.CompleteAsync();

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
        {
            try
            {
                Add(count ?? Math.Max(0, new FileInfo(path).Length - offset));
            }
            catch
            {
                // counting only
            }

            return _inner.SendFileAsync(path, offset, count, cancellationToken);
        }
    }

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly CountingBodyFeature _owner;

        public CountingStream(Stream inner, CountingBodyFeature owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            _owner.Add(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            _owner.Add(buffer);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            _owner.Add(buffer.AsSpan(offset, count));
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _owner.Add(buffer.Span);
        }
    }

    private sealed class CountingPipeWriter : PipeWriter
    {
        private readonly PipeWriter _inner;
        private readonly CountingBodyFeature _owner;

        public CountingPipeWriter(PipeWriter inner, CountingBodyFeature owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override bool CanGetUnflushedBytes => _inner.CanGetUnflushedBytes;

        public override long UnflushedBytes => _inner.UnflushedBytes;

        public override void Advance(int bytes)
        {
            _inner.Advance(bytes);
            _owner.Add(bytes);
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);

        public override void CancelPendingFlush() => _inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => _inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null) => _inner.CompleteAsync(exception);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => _inner.FlushAsync(cancellationToken);

        public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            _owner.Add(source.Span);
            return _inner.WriteAsync(source, cancellationToken);
        }
    }
}
