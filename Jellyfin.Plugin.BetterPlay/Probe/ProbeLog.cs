using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.BetterPlay.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BetterPlay.Probe;

/// <summary>
/// Records probe events: one JSON object per line in data/probe/yyyy-MM-dd.jsonl, plus the latest few thousand in memory
/// for the /BetterPlay/Probe endpoint. Never throws: a logging problem must not affect playback.
/// </summary>
public class ProbeLog
{
    private const int MemoryLimit = 5000;
    private readonly ConcurrentQueue<JsonObject> _recent = new();
    private readonly object _fileLock = new();
    private readonly ILogger<ProbeLog> _logger;
    private DateTime _lastCleanup = DateTime.MinValue;

    public ProbeLog(ILogger<ProbeLog> logger)
    {
        _logger = logger;
    }

    public static bool Enabled => Plugin.Instance?.Configuration.Mode is BetterPlayMode.WatchOnly or BetterPlayMode.On;

    public void Write(string kind, JsonObject data)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            data["t"] = DateTime.UtcNow.ToString("O");
            data["kind"] = kind;
            _recent.Enqueue(data);
            while (_recent.Count > MemoryLimit && _recent.TryDequeue(out _))
            {
            }

            var folder = Folder();
            if (folder is null)
            {
                return;
            }

            var line = data.ToJsonString() + Environment.NewLine;
            lock (_fileLock)
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl"), line);
                Cleanup(folder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "BetterPlay probe: could not record an event");
        }
    }

    public IEnumerable<JsonObject> Recent(DateTime sinceUtc, string? kind)
    {
        foreach (var e in _recent.ToArray())
        {
            if (kind is not null && e["kind"]?.GetValue<string>() != kind)
            {
                continue;
            }

            if (DateTime.TryParse(e["t"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) && t >= sinceUtc)
            {
                yield return e;
            }
        }
    }

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
            return json.Length > maxChars ? JsonValue.Create(json[..maxChars] + "...(cut)") : JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            return JsonValue.Create("(not serialisable: " + ex.GetType().Name + ")");
        }
    }

    private static string? Folder()
    {
        var p = Plugin.Instance;
        return p is null ? null : Path.Combine(p.DataFolderPath, "probe");
    }

    private void Cleanup(string folder)
    {
        if ((DateTime.UtcNow - _lastCleanup).TotalHours < 6)
        {
            return;
        }

        _lastCleanup = DateTime.UtcNow;
        var keep = Math.Max(1, Plugin.Instance?.Configuration.ProbeLogDays ?? 14);
        foreach (var f in Directory.GetFiles(folder, "*.jsonl"))
        {
            if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-keep))
            {
                File.Delete(f);
            }
        }
    }
}
