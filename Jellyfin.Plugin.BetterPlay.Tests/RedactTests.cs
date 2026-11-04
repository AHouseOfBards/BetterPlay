using System.Text.Json.Nodes;
using Jellyfin.Plugin.BetterPlay.Probe;
using Xunit;

namespace Jellyfin.Plugin.BetterPlay.Tests;

public class RedactTests
{
    [Theory]
    [InlineData("?api_key=abc123&x=1", "?api_key=REDACTED&x=1")]
    [InlineData("?ApiKey=abc123", "?ApiKey=REDACTED")]
    [InlineData("\"AccessToken\":\"tok\"", "\"AccessToken\":\"REDACTED\"")]
    [InlineData("\"token\": \"secret\"", "\"token\": \"REDACTED\"")]
    [InlineData("X-Emby-Token: q1", "X-Emby-Token: REDACTED")]
    [InlineData("MediaBrowser Token=\\u0022zzz\\u0022", "MediaBrowser Token=\\u0022REDACTED\\u0022")]
    // The 0.1.0.0 leak: JSON writes & as &, so a digit sits right before the name.
    [InlineData("MinSegments=1\\u0026ApiKey=807792fa\\u0026Tag=b9", "MinSegments=1\\u0026ApiKey=REDACTED\\u0026Tag=b9")]
    public void RemovesSecrets(string input, string expected) => Assert.Equal(expected, ProbeLog.Redact(input));

    [Theory]
    [InlineData("PlaySessionId=0e83&TranscodeReasons=x")]
    [InlineData("\"TokenType\":\"Bearer\"")]
    [InlineData("\"PlaybackToken\":\"x\"")]
    [InlineData("tokens=5")]
    public void LeavesOtherTextAlone(string input) => Assert.Equal(input, ProbeLog.Redact(input));

    [Fact]
    public void RedactsARealSerialisedTranscodeUrl()
    {
        var json = new JsonObject { ["transcodingUrl"] = "/videos/x/master.m3u8?MediaSourceId=1&ApiKey=deadbeef&Tag=2" }.ToJsonString();
        var clean = ProbeLog.Redact(json);
        Assert.DoesNotContain("deadbeef", clean);
        Assert.Contains("REDACTED", clean);
    }
}
