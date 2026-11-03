using Jellyfin.Plugin.BetterPlay.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.BetterPlay;

/// <summary>
/// BetterPlay: picks the right version and bitrate for each viewer and keeps playback smooth.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "BetterPlay";

    public override Guid Id => Guid.Parse("575fdaea-52dd-4363-a84b-69c5c52daded");

    public override string Description => "Picks the best version and bitrate for each viewer's connection and keeps playback from freezing.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }
}
