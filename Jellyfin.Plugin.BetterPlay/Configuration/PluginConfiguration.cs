using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BetterPlay.Configuration;

/// <summary>What BetterPlay is allowed to do.</summary>
public enum BetterPlayMode
{
    /// <summary>Does nothing at all.</summary>
    Off,

    /// <summary>Observes and records what it sees (later: what it would have done). Never changes playback.</summary>
    WatchOnly,

    /// <summary>Acts on playback. Not available in the probe release.</summary>
    On
}

public class PluginConfiguration : BasePluginConfiguration
{
    public BetterPlayMode Mode { get; set; } = BetterPlayMode.WatchOnly;

    // What the probe records (each can be switched off on the dashboard).
    public bool RecordPlaybackInfo { get; set; } = true;

    public bool RecordPlaybackReports { get; set; } = true;

    public bool RecordVideoDelivery { get; set; } = true;

    /// <summary>Poster/artwork downloads: frequent while browsing, used to test speed estimates before play.</summary>
    public bool RecordImages { get; set; } = true;

    /// <summary>Full device profile each app sends with PlaybackInfo (large; log file only).</summary>
    public bool RecordDeviceProfiles { get; set; } = true;

    /// <summary>Days of probe log files to keep in the plugin's data folder.</summary>
    public int ProbeLogDays { get; set; } = 14;

    /// <summary>Recent events kept in memory for the dashboard.</summary>
    public int MemoryEvents { get; set; } = 3000;
}
