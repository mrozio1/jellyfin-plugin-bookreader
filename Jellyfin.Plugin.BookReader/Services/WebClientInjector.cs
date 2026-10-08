using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookReader.Services;

/// <summary>
/// Adds one small script tag to Jellyfin's web client so book pages get a
/// "Read" button that opens this reader. Runs on every server start, and
/// removes itself cleanly when the admin turns the option off.
/// </summary>
public class WebClientInjector : IHostedService
{
    private const string StartMarker = "<!-- BookReader:start -->";
    private const string EndMarker = "<!-- BookReader:end -->";
    private const string Tag = StartMarker + "<script defer src=\"../BookReader/Assets/inject.js\"></script>" + EndMarker;

    private readonly IApplicationPaths _paths;
    private readonly ILogger<WebClientInjector> _logger;

    public WebClientInjector(IApplicationPaths paths, ILogger<WebClientInjector> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply(Plugin.Instance?.Configuration.InjectIntoWebClient ?? true);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Adds or removes the script tag. Returns a message for the admin page.</summary>
    public string Apply(bool enable)
    {
        var index = Path.Combine(_paths.WebPath, "index.html");
        try
        {
            if (!File.Exists(index))
            {
                return "Jellyfin web client not found at " + index;
            }

            var html = File.ReadAllText(index);
            var s = html.IndexOf(StartMarker, StringComparison.Ordinal);
            if (s >= 0)
            {
                var e = html.IndexOf(EndMarker, s, StringComparison.Ordinal);
                if (e > s)
                {
                    html = html.Remove(s, e + EndMarker.Length - s);
                }
            }

            if (enable)
            {
                var body = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                html = body >= 0 ? html.Insert(body, Tag) : html + Tag;
            }

            File.WriteAllText(index, html);
            _logger.LogInformation("Book Reader: web client integration {State}", enable ? "enabled" : "removed");
            return enable ? "Read button installed in the web client." : "Read button removed from the web client.";
        }
        catch (UnauthorizedAccessException)
        {
            var msg = "Can't edit " + index + " (read-only, common in Docker). Use the reader link instead, or install the File Transformation plugin.";
            _logger.LogWarning("Book Reader: {Msg}", msg);
            return msg;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Book Reader: could not update web client");
            return "Could not update the web client: " + ex.Message;
        }
    }
}
