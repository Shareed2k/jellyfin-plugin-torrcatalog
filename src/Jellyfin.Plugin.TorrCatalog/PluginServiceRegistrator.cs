using Jellyfin.Plugin.TorrCatalog.Tmdb;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TorrCatalog;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(TmdbClient.HttpClientName, c =>
        {
            c.BaseAddress = TmdbClient.BaseAddress;
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-TorrCatalog/0.1");
        });
        serviceCollection.AddSingleton<TmdbClient>();
        serviceCollection.AddSingleton<Catalog>();

        // Plugin services share the ASP.NET container, so a global MVC filter sees every API action.
        serviceCollection.AddSingleton<VirtualItemFilter>();
        serviceCollection.PostConfigure<MvcOptions>(o => o.Filters.AddService<VirtualItemFilter>());
    }
}
