using Jellyfin.Plugin.TorrCatalog.Releases;
using Jellyfin.Plugin.TorrCatalog.Tmdb;
using Jellyfin.Plugin.TorrCatalog.TorrServer;
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

        // Per-search timeout comes from config (linked token); this is only the outer bound.
        serviceCollection.AddHttpClient(ProwlarrClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(150);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-TorrCatalog/0.1");
        });
        serviceCollection.AddSingleton<ProwlarrClient>();
        serviceCollection.AddSingleton<ReleaseService>();

        serviceCollection.AddHttpClient(TorrServerClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-TorrCatalog/0.1");
        });
        serviceCollection.AddSingleton<TorrServerClient>();
        serviceCollection.AddSingleton<StreamResolver>();
        serviceCollection.AddSingleton<WatchState>();
        serviceCollection.AddSingleton<Persistence>();
        serviceCollection.AddSingleton<PlaybackNotifier>();
        serviceCollection.AddHostedService<StrmProbeTuning>();

        // Plugin services share the ASP.NET container, so a global MVC filter sees every API action.
        serviceCollection.AddSingleton<VirtualItemFilter>();
        serviceCollection.PostConfigure<MvcOptions>(o => o.Filters.AddService<VirtualItemFilter>());
    }
}
