using System.Collections.Concurrent;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookReader.Services;

/// <summary>
/// Shrinks comic pages to the size of the reader's screen and re-encodes them as
/// WebP, using Jellyfin's own image encoder. A 3 MB scan typically becomes
/// 200–400 KB, which is what makes comics fast away from home. Results are cached
/// on disk, so each page is only processed once per screen size.
/// </summary>
public class PageImageService
{
    /// <summary>Widths pages are produced at. Requests snap up to one of these so the cache is shared.</summary>
    private static readonly int[] Widths = { 720, 1080, 1440, 1920, 2560 };

    // Don't bother shrinking pages that are already small.
    private const long SmallEnoughBytes = 300_000;

    private readonly IImageEncoder _encoder;
    private readonly ComicArchiveService _comics;
    private readonly ILogger<PageImageService> _logger;
    private readonly SemaphoreSlim _gate = new(Math.Max(1, Environment.ProcessorCount / 2));
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new();

    public PageImageService(IImageEncoder encoder, ComicArchiveService comics, ILogger<PageImageService> logger)
    {
        _encoder = encoder;
        _comics = comics;
        _logger = logger;
    }

    public static int? SnapWidth(int? requested)
    {
        if (requested is null or <= 0)
        {
            return null;
        }

        foreach (var w in Widths)
        {
            if (requested <= w)
            {
                return w;
            }
        }

        return Widths[^1];
    }

    /// <summary>Path to a resized WebP of the page, or null to serve the original.</summary>
    public async Task<string?> GetAsync(ComicBook book, int index, int width, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (!cfg.OptimizeComicImages || !_encoder.SupportsImageEncoding)
        {
            return null;
        }

        var dir = Path.Combine(book.CacheFolder, "w" + width);
        var output = Path.Combine(dir, index.ToString("D5") + ".webp");
        var keepOriginal = output + ".orig";
        if (File.Exists(output))
        {
            return output;
        }

        if (File.Exists(keepOriginal))
        {
            return null;
        }

        var job = _inFlight.GetOrAdd(output, o => new Lazy<Task<string?>>(() =>
        {
            var t = Task.Run(() => EncodeAsync(book, index, width, dir, o, keepOriginal));
            t.ContinueWith(done => _inFlight.TryRemove(o, out _), TaskScheduler.Default);
            return t;
        }));

        return await job.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<string?> EncodeAsync(ComicBook book, int index, int width, string dir, string output, string keepOriginal)
    {
        string? temp = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(dir);
            var ext = Path.GetExtension(book.Pages[index]).ToLowerInvariant();
            var inputFormat = ext.TrimStart('.');
            if (!_encoder.SupportedInputFormats.Contains(inputFormat, StringComparer.OrdinalIgnoreCase))
            {
                MarkKeepOriginal(keepOriginal);
                return null;
            }

            string input;
            if (book.DirectZip)
            {
                temp = Path.Combine(dir, $".src-{index}-{Guid.NewGuid():N}{ext}");
                await using (var src = await _comics.OpenPageAsync(book, index, CancellationToken.None).ConfigureAwait(false))
                await using (var fs = File.Create(temp))
                {
                    await src.CopyToAsync(fs).ConfigureAwait(false);
                }

                input = temp;
            }
            else
            {
                input = await _comics.WaitForUnpackedAsync(book, index, CancellationToken.None).ConfigureAwait(false);
            }

            var originalSize = new FileInfo(input).Length;
            if (originalSize <= SmallEnoughBytes)
            {
                MarkKeepOriginal(keepOriginal);
                return null;
            }

            var quality = Math.Clamp(Plugin.Instance!.Configuration.ComicImageQuality, 40, 95);
            var partial = Path.Combine(dir, $".out-{index}-{Guid.NewGuid():N}.webp");
            var options = new ImageProcessingOptions
            {
                MaxWidth = width,
                Quality = quality,
                SupportedOutputFormats = new[] { ImageFormat.Webp }
            };

            var result = _encoder.EncodeImage(input, File.GetLastWriteTimeUtc(input), partial, false, null, quality, options, ImageFormat.Webp);
            if (!string.Equals(result, partial, StringComparison.Ordinal) || !File.Exists(partial))
            {
                MarkKeepOriginal(keepOriginal);
                return null;
            }

            // If re-encoding didn't save much, the original is the better choice.
            if (new FileInfo(partial).Length > originalSize * 0.85)
            {
                File.Delete(partial);
                MarkKeepOriginal(keepOriginal);
                return null;
            }

            File.Move(partial, output, true);
            return output;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Book Reader: could not resize page {Index} of {Path}; sending the original", index, book.SourcePath);
            return null;
        }
        finally
        {
            _gate.Release();
            if (temp is not null)
            {
                try { File.Delete(temp); } catch (IOException) { /* ignore */ }
            }
        }
    }

    private static void MarkKeepOriginal(string path)
    {
        try { File.WriteAllText(path, string.Empty); } catch (IOException) { /* ignore */ }
    }
}
