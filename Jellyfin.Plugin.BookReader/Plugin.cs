using Jellyfin.Plugin.BookReader.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.BookReader;

/// <summary>
/// Book Reader plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Book Reader";

    public override Guid Id => Guid.Parse("007c7a3d-4922-4238-bc94-f20f5cb37996");

    public override string Description =>
        "A fast, good-looking reader for EPUB, PDF, comics (CBZ/CBR/CB7/CBT) and, with Calibre installed, MOBI/AZW3/FB2 and more.";

    /// <summary>Folder for converted books and per-user reading progress.</summary>
    public string PluginDataPath
    {
        get
        {
            var path = Path.Combine(DataFolderPath, "data");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "BookReader",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
