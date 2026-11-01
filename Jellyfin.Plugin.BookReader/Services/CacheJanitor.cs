using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookReader.Services;

/// <summary>
/// Keeps the page cache under the size the admin chose by removing the books
/// that haven't been opened for the longest time. Runs at startup and every 6 hours.
/// </summary>
public sealed class CacheJanitor : IHostedService, IDisposable
{
    private readonly ILogger<CacheJanitor> _logger;
    private Timer? _timer;

    public CacheJanitor(ILogger<CacheJanitor> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Run(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void Run()
    {
        try
        {
            var data = Plugin.Instance?.PluginDataPath;
            if (data is null)
            {
                return;
            }

            // Version 1.0 kept unpacked comics here; the new cache replaces it.
            var legacy = Path.Combine(data, "comic-cache");
            if (Directory.Exists(legacy))
            {
                Directory.Delete(legacy, true);
            }

            var limitGb = Plugin.Instance!.Configuration.CacheSizeLimitGb;
            if (limitGb <= 0)
            {
                return;
            }

            var limit = (long)(limitGb * 1024L * 1024 * 1024);
            var books = new DirectoryInfo(BookKey.CacheRoot).GetDirectories()
                .Select(d =>
                {
                    var files = d.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                    var marker = files.FirstOrDefault(f => f.Name == ".used");
                    return (Dir: d, Bytes: files.Sum(f => f.Length), Used: marker?.LastWriteTimeUtc ?? d.LastWriteTimeUtc);
                })
                .OrderBy(b => b.Used)
                .ToList();

            var total = books.Sum(b => b.Bytes);
            var removed = 0;
            foreach (var b in books)
            {
                if (total <= limit)
                {
                    break;
                }

                if (DateTime.UtcNow - b.Used < TimeSpan.FromHours(1))
                {
                    continue; // someone is probably reading it right now
                }

                b.Dir.Delete(true);
                total -= b.Bytes;
                removed++;
            }

            if (removed > 0)
            {
                _logger.LogInformation("Book Reader: cleared {Count} books from the page cache", removed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Book Reader: page cache cleanup failed");
        }
    }
}
