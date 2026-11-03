using Jellyfin.Plugin.BetterPlay.Probe;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.BetterPlay;

/// <summary>Registers BetterPlay's hooks with Jellyfin's web host.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ProbeLog>();
        serviceCollection.AddHostedService<SessionWatcher>();
        serviceCollection.AddScoped<PlaybackInfoProbe>();
        serviceCollection.Configure<MvcOptions>(o => o.Filters.AddService<PlaybackInfoProbe>());
        serviceCollection.AddTransient<IStartupFilter, DeliveryMeterStartupFilter>();
    }
}
