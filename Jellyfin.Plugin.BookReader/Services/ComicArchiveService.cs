using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Readers;

namespace Jellyfin.Plugin.BookReader.Services;

/// <summary>
/// Serves comic pages one at a time so the reader never downloads a whole
/// archive. ZIP-based comics (CBZ) are read directly; RAR/7z/TAR comics are
/// unpacked once into a cache folder because those formats are slow to seek.
/// </summary>
public class ComicArchiveService
{
    private readonly ILogger<ComicArchiveService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<ComicIndex>>> _indexes = new();

    public ComicArchiveService(ILogger<ComicArchiveService> logger)
    {
        _logger = logger;
    }

    private static string CacheRoot
    {
        get
        {
            var dir = Path.Combine(Plugin.Instance!.PluginDataPath, "comic-cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public Task<ComicIndex> GetIndexAsync(string archivePath)
    {
        var key = CacheKey(archivePath);
        var lazy = _indexes.GetOrAdd(key, _ => new Lazy<Task<ComicIndex>>(() => Task.Run(() => BuildIndex(archivePath, key))));
        return lazy.Value;
    }

    /// <summary>Returns a stream for the given page, or null if out of range.</summary>
    public async Task<(Stream Stream, string Name)?> OpenPageAsync(string archivePath, int index)
    {
        var comic = await GetIndexAsync(archivePath).ConfigureAwait(false);
        if (index < 0 || index >= comic.Pages.Count)
        {
            return null;
        }

        var name = comic.Pages[index];

        if (comic.ExtractedFolder is not null)
        {
            var file = Path.Combine(comic.ExtractedFolder, index.ToString("D5") + Path.GetExtension(name));
            return (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true), name);
        }

        // ZIP: random access, copy the single entry into memory.
        using var archive = ZipArchive.Open(archivePath);
        var entry = archive.Entries.First(e => e.Key == name);
        var ms = new MemoryStream();
        using (var es = entry.OpenEntryStream())
        {
            await es.CopyToAsync(ms).ConfigureAwait(false);
        }

        ms.Position = 0;
        return (ms, name);
    }

    private ComicIndex BuildIndex(string archivePath, string key)
    {
        try
        {
            var isZip = ZipArchive.IsZipFile(archivePath);
            using var archive = ArchiveFactory.Open(archivePath);
            var pages = archive.Entries
                .Where(e => !e.IsDirectory && e.Key is not null)
                .Where(e => BookFormats.ImageExtensions.Contains(Path.GetExtension(e.Key!)))
                .Where(e => !e.Key!.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase)
                            && !Path.GetFileName(e.Key!).StartsWith('.'))
                .Select(e => e.Key!)
                .ToList();
            pages.Sort(BookFormats.NaturalCompare);

            if (isZip)
            {
                return new ComicIndex(pages, null);
            }

            // RAR / 7z / TAR: unpack once, in reading order, so later page loads are instant.
            var folder = Path.Combine(CacheRoot, key);
            var marker = Path.Combine(folder, ".complete");
            if (!File.Exists(marker))
            {
                Directory.CreateDirectory(folder);
                var order = pages.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    var k = reader.Entry.Key;
                    if (k is null || reader.Entry.IsDirectory || !order.TryGetValue(k, out var idx))
                    {
                        continue;
                    }

                    var target = Path.Combine(folder, idx.ToString("D5") + Path.GetExtension(k));
                    using var fs = File.Create(target);
                    reader.WriteEntryTo(fs);
                }

                File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            }

            return new ComicIndex(pages, folder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Book Reader: could not read comic archive {Path}", archivePath);
            _indexes.TryRemove(key, out _);
            throw;
        }
    }

    private static string CacheKey(string path)
    {
        var info = new FileInfo(path);
        var raw = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public record ComicIndex(List<string> Pages, string? ExtractedFolder);
