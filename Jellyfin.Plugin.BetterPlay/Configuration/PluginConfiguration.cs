using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BetterPlay.Configuration;

/// <summary>What BetterPlay is allowed to do.</summary>
public enum BetterPlayMode
{
    /// <summary>Does nothing at all.</summary>
    Off,

    /// <summary>Observes and logs what it sees (and, later, what it would have done). Never changes playback.</summary>
    WatchOnly,

    /// <summary>Acts on playback. Not used by the probe release.</summary>
    On
}

public class PluginConfiguration : BasePluginConfiguration
{
    public BetterPlayMode Mode { get; set; } = BetterPlayMode.WatchOnly;

    /// <summary>Days of probe log files to keep in the plugin's data folder.</summary>
    public int ProbeLogDays { get; set; } = 14;

    /// <summary>Also record the full DeviceProfile each client sends with PlaybackInfo (large, but shows what it can play).</summary>
    public bool ProbeLogDeviceProfiles { get; set; } = true;
}
