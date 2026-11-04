using System.Text.Json.Nodes;
using Jellyfin.Plugin.BetterPlay.Probe;
using Xunit;

namespace Jellyfin.Plugin.BetterPlay.Tests;

public class SessionAnalyzerTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static JsonObject Segment(double atSec, int index, long bytes = 5_000_000, double ms = 100, string remote = "127.0.0.1") => new()
    {
        ["kind"] = "delivery",
        ["t"] = T0.AddSeconds(atSec).ToString("O"),
        ["playSessionId"] = "ps",
        ["segment"] = index,
        ["status"] = 200,
        ["bytes"] = bytes,
        ["totalMs"] = ms,
        ["remote"] = remote,
        ["forwardedFor"] = remote == "127.0.0.1" ? "203.0.113.5" : string.Empty,
    };

    private static JsonObject Playlist(double atSec, double segSec) => new()
    {
        ["kind"] = "delivery",
        ["t"] = T0.AddSeconds(atSec).ToString("O"),
        ["playSessionId"] = "ps",
        ["status"] = 200,
        ["bytes"] = 1000,
        ["segmentSeconds"] = segSec,
    };

    private static JsonObject Progress(double atSec, double positionSec, bool paused = false) => new()
    {
        ["kind"] = "progress",
        ["t"] = T0.AddSeconds(atSec).ToString("O"),
        ["playSessionId"] = "ps",
        ["client"] = "Jellyfin Web",
        ["device"] = "Chrome",
        ["playMethod"] = "Transcode",
        ["positionTicks"] = (long)(positionSec * 1e7),
        ["paused"] = paused,
    };

    private static JsonObject Only(List<JsonObject> events, double nowSec) =>
        (JsonObject)SessionAnalyzer.Summarize(events, T0.AddSeconds(nowSec)).Single()!;

    [Fact]
    public void SteadyPlaybackWithFullBufferIsHealthy()
    {
        // Burst of 5 segments, then one every 6 s (the player waits for room): buffer stays ~24 s ahead.
        var ev = new List<JsonObject> { Playlist(0, 6) };
        for (var i = 0; i < 5; i++)
        {
            ev.Add(Segment(0.5 + (i * 0.3), i));
        }

        for (var i = 5; i < 20; i++)
        {
            ev.Add(Segment(6 * (i - 4), i));
        }

        ev.Add(Progress(91, 90 - 24 + 6));
        var s = Only(ev, 92);
        Assert.Equal("Healthy", s["state"]!.GetValue<string>());
        Assert.Equal(0, s["lateRequests"]!.GetValue<int>());
        Assert.Equal("playlist", s["segmentSecondsFrom"]!.GetValue<string>());
    }

    [Fact]
    public void RequestsSlowerThanRealTimeAreAtRisk()
    {
        // 6 s segments arriving every 9 s: the link carries 2/3 of real time, the buffer drains.
        var ev = new List<JsonObject> { Playlist(0, 6) };
        for (var i = 0; i < 10; i++)
        {
            ev.Add(Segment(i * 9, i));
        }

        ev.Add(Progress(82, 50));
        var s = Only(ev, 83);
        Assert.Equal("AtRisk", s["state"]!.GetValue<string>());
        Assert.True(s["lateRequests"]!.GetValue<int>() >= 8);
        Assert.Equal(9, s["medianGapSec"]!.GetValue<double>());
    }

    [Fact]
    public void PositionNotMovingWhileUnpausedIsStalled()
    {
        var ev = new List<JsonObject> { Playlist(0, 6), Segment(1, 0), Segment(2, 1), Progress(10, 11), Progress(15, 11) };
        Assert.Equal("Stalled", Only(ev, 16)["state"]!.GetValue<string>());
    }

    [Fact]
    public void PausedGapsAreNotLate()
    {
        var ev = new List<JsonObject> { Playlist(0, 6), Segment(0, 0), Segment(6, 1), Progress(8, 4, paused: true), Segment(300, 2), Progress(301, 5) };
        var s = Only(ev, 302);
        Assert.Equal(0, s["lateRequests"]!.GetValue<int>());
    }

    [Fact]
    public void SeeksAndRepeatsAreCountedNotLate()
    {
        var ev = new List<JsonObject> { Playlist(0, 6), Segment(0, 0), Segment(6, 1), Segment(7, 200), Segment(13, 201), Segment(14, 201), Segment(20, 50) };
        var s = Only(ev, 21);
        Assert.Equal(2, s["seeks"]!.GetValue<int>());
        Assert.Equal(1, s["reRequests"]!.GetValue<int>());
        Assert.Equal(0, s["lateRequests"]!.GetValue<int>());
    }

    [Fact]
    public void FlagsTimingHiddenByLocalProxy()
    {
        // 5 MB in 100 ms = 400 Mbps from 127.0.0.1 with X-Forwarded-For: that's the hand-off to nginx.
        var ev = new List<JsonObject> { Playlist(0, 6), Segment(0, 0), Segment(6, 1), Progress(7, 1) };
        var s = Only(ev, 8);
        Assert.True(s["behindProxy"]!.GetValue<bool>());
        Assert.True(s["timingsHiddenByProxy"]!.GetValue<bool>());
    }

    [Fact]
    public void FallsBackToJellyfinSegmentLengthsWithoutAPlaylist()
    {
        var p = Progress(7, 1);
        p["transcode"] = new JsonObject { ["videoDirect"] = false };
        var s = Only(new List<JsonObject> { Segment(0, 0), Segment(3, 1), p }, 8);
        Assert.Equal(3, s["segmentSeconds"]!.GetValue<double>());
        Assert.Equal("default", s["segmentSecondsFrom"]!.GetValue<string>());
    }
}
