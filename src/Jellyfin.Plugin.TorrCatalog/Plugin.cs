using Jellyfin.Plugin.TorrCatalog.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TorrCatalog;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "TorrCatalog";

    public override Guid Id => Guid.Parse("4f0c6a3e-9b8d-4c52-a1f7-3e2d5b6c7a81");

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo { Name = Name, EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" },
    ];
}
