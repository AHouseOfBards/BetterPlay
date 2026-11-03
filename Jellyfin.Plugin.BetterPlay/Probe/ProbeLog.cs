using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Jellyfin.Plugin.BetterPlay.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BetterPlay.Probe;

/// <summary>
/// Records probe events. Callers never wait on disk and never see an exception: events go to a bounded queue that a
/// background task writes to data/probe/yyyy-MM-dd.jsonl. A trimmed copy (no device profiles) is kept in memory for the
/// dashboard. API keys and tokens are removed before anything is stored.
/// </summary>
public sealed class ProbeLog : IDisposable
{
    // Not \b: JSON writes & as &, so "&ApiKey=" has a digit right before the name and \b never matches.
    private static readonly Regex Secrets = new(
        @"(?i)(?<![a-z])(api_?key|access_?token|token|x-emby-token|x-mediabrowser-token)(=(?:\\u0022|"")?|"":\s*""|\\u0022:\\u0022|:\s*)[^&""\s\\]+",
        RegexOptions.Compiled);

    private const int ImageMemoryLimit = 500;
    private readonly ConcurrentQueue<JsonObject> _recent = new();

    // Artwork events are far more frequent than playback events; they get their own small pool so browsing
    // never pushes playback data off the dashboard.
    private readonly ConcurrentQueue<JsonObject> _recentImages = new();
    private int _recentImageCount;
    private readonly Channel<(DateTime Day, string Line)> _disk =
        Channel.CreateBounded<(DateTime, string)>(new BoundedChannelOptions(20000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ConcurrentDictionary<string, HookStat> _stats = new();
    private readonly ILogger<ProbeLog> _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writer;
    private int _recentCount;
    private long _dropped;

    public ProbeLog(ILogger<ProbeLog> logger)
    {
        _logger = logger;
        _writer = Task.Run(WriteLoop);
    }

    public static PluginConfiguration? Config => Plugin.Instance?.Configuration;

    public static bool Enabled => Config?.Mode is BetterPlayMode.WatchOnly or BetterPlayMode.On;

    public static string? Folder => Plugin.Instance is null ? null : Path.Combine(Plugin.Instance.DataFolderPath, "probe");

    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Counts that a hook ran, even when nothing is recorded (shows the hook works on this server).</summary>
    public void Seen(string hook) => _stats.GetOrAdd(hook, _ => new HookStat()).Hit();

    public IReadOnlyDictionary<string, HookStat> Stats => _stats;

    public void Write(string kind, JsonObject data)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            data["t"] = now.ToString("O");
            data["kind"] = kind;
            var line = Redact(data.ToJsonString());
            _stats.GetOrAdd("recorded:" + kind, _ => new HookStat()).Hit();
            if (!_disk.Writer.TryWrite((now.Date, line)))
            {
                Interlocked.Increment(ref _dropped);
            }

            // In memory: the same event without device profiles (they can be tens of KB each).
            if (JsonNode.Parse(line) is JsonObject copy)
            {
                StripLarge(copy);
                if (kind == "image")
                {
                    _recentImages.Enqueue(copy);
                    if (Interlocked.Increment(ref _recentImageCount) > ImageMemoryLimit && _recentImages.TryDequeue(out _))
                    {
                        Interlocked.Decrement(ref _recentImageCount);
                    }
                }
                else
                {
                    _recent.Enqueue(copy);
                    var limit = Math.Clamp(Config?.MemoryEvents ?? 3000, 100, 20000);
                    if (Interlocked.Increment(ref _recentCount) > limit && _recent.TryDequeue(out _))
                    {
                        Interlocked.Decrement(ref _recentCount);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "BetterPlay probe: could not record an event");
        }
    }

    public IEnumerable<JsonObject> Recent(DateTime sinceUtc, string? kind)
    {
        var source = kind == "image" ? _recentImages.ToArray()
                   : kind is null ? _recent.ToArray().Concat(_recentImages.ToArray()).OrderBy(e => ParseTime(e)).ToArray()
                   : _recent.ToArray();
        foreach (var e in source)
        {
            if (kind is not null && (e["kind"] as JsonValue)?.GetValue<string>() != kind)
            {
                continue;
            }

            if (ParseTime(e) is { } t && t >= sinceUtc)
            {
                yield return e;
            }
        }
    }

    public void ClearMemory()
    {
        while (_recent.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _recentCount);
        }

        while (_recentImages.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _recentImageCount);
        }
    }

    public static DateTime? ParseTime(JsonObject e) =>
        (e["t"] as JsonValue)?.GetValue<string>() is { } s
        && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;

    /// <summary>Removes API keys and tokens from any text (URLs, query strings, JSON).</summary>
    public static string Redact(string? text) => text is null ? string.Empty : Secrets.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "REDACTED");

    /// <summary>Turns any object into JSON for the log, capped in size; never throws.</summary>
    public static JsonNode? ToNode(object? value, int maxChars = 65536)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            var json = JsonSerializer.Serialize(value, value.GetType());
            return json.Length > maxChars ? JsonValue.Create("(cut: " + json.Length + " chars)") : JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            return JsonValue.Create("(not serialisable: " + ex.GetType().Name + ")");
        }
    }

    public void Dispose()
    {
        _disk.Writer.TryComplete();
        _stop.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(6));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }

    private static void StripLarge(JsonObject e)
    {
        if (e["request"] is JsonObject req)
        {
            foreach (var (_, arg) in req)
            {
                if (arg is JsonObject o && o.ContainsKey("DeviceProfile"))
                {
                    o["DeviceProfile"] = "(in the log file)";
                }
            }
        }
    }

    private async Task WriteLoop()
    {
        var lastCleanup = DateTime.MinValue;
        var batch = new List<string>();
        try
        {
            ScrubExisting();

            while (await _disk.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                // Group what's waiting by day, append each group in one go.
                var day = DateTime.MinValue;
                while (_disk.Reader.TryRead(out var item))
                {
                    if (batch.Count > 0 && item.Day != day)
                    {
                        Append(day, batch);
                    }

                    day = item.Day;
                    batch.Add(item.Line);
                    if (batch.Count >= 500)
                    {
                        Append(day, batch);
                    }
                }

                if (batch.Count > 0)
                {
                    Append(day, batch);
                }

                if ((DateTime.UtcNow - lastCleanup).TotalHours >= 6)
                {
                    lastCleanup = DateTime.UtcNow;
                    Cleanup();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BetterPlay probe: log writer stopped");
        }
    }

    private void Append(DateTime day, List<string> lines)
    {
        try
        {
            if (Folder is { } folder)
            {
                Directory.CreateDirectory(folder);
                File.AppendAllLines(Path.Combine(folder, day.ToString("yyyy-MM-dd") + ".jsonl"), lines);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Add(ref _dropped, lines.Count);
            _logger.LogDebug(ex, "BetterPlay probe: could not write the log file");
        }

        lines.Clear();
    }

    /// <summary>Re-applies redaction to log files already on disk (written by an older version with weaker redaction).</summary>
    private void ScrubExisting()
    {
        try
        {
            if (Folder is not { } folder || !Directory.Exists(folder))
            {
                return;
            }

            foreach (var f in Directory.GetFiles(folder, "*.jsonl"))
            {
                var text = File.ReadAllText(f);
                var clean = Redact(text);
                if (clean != text)
                {
                    File.WriteAllText(f + ".tmp", clean);
                    File.Move(f + ".tmp", f, true);
                    _logger.LogInformation("BetterPlay probe: removed keys from {File}", Path.GetFileName(f));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BetterPlay probe: could not scrub old log files");
        }
    }

    private void Cleanup()
    {
        try
        {
            if (Folder is not { } folder || !Directory.Exists(folder))
            {
                return;
            }

            var keep = Math.Clamp(Config?.ProbeLogDays ?? 14, 1, 365);
            foreach (var f in Directory.GetFiles(folder, "*.jsonl"))
            {
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-keep))
                {
                    File.Delete(f);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "BetterPlay probe: log cleanup failed");
        }
    }

    public sealed class HookStat
    {
        private long _count;
        private long _lastTicks;

        public long Count => Interlocked.Read(ref _count);

        public DateTime? Last => Interlocked.Read(ref _lastTicks) is var t && t > 0 ? new DateTime(t, DateTimeKind.Utc) : null;

        public void Hit()
        {
            Interlocked.Increment(ref _count);
            Interlocked.Exchange(ref _lastTicks, DateTime.UtcNow.Ticks);
        }
    }
}
