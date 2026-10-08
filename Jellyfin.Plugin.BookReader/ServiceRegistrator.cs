using Jellyfin.Plugin.BookReader.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.BookReader;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ComicArchiveService>();
        serviceCollection.AddSingleton<ConversionService>();
        serviceCollection.AddSingleton<ProgressStore>();
        serviceCollection.AddSingleton<WebClientInjector>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<WebClientInjector>());
    }
}
