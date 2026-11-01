using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Jellyfin.Plugin.BookReader.Services;

/// <summary>One comic archive, indexed and ready to serve pages from.</summary>
public sealed class ComicBook
{
    public required string SourcePath { get; init; }

    public required string Key { get; init; }

    /// <summary>This book's folder in the cache.</summary>
    public required string CacheFolder { get; init; }

    /// <summary>Archive entry names of the pages, in reading order.</summary>
    public required IReadOnlyList<string> Pages { get; init; }

    /// <summary>True for ZIP comics, which are read in place. False for RAR/7z/TAR, which are unpacked.</summary>
    public bool DirectZip { get; init; }

    /// <summary>For unpacked comics: completes with the file path once each page is on disk.</summary>
    internal TaskCompletionSource<string>[]? Unpacked { get; init; }
}

/// <summary>
/// Serves comic pages one at a time so the reader never downloads a whole archive.
/// ZIP comics (most CBZ) are read in place. RAR, 7z and TAR comics are unpacked in
/// the background in reading order; the book opens as soon as the archive is indexed
/// and each page can be shown the moment it has been unpacked.
/// </summary>
public class ComicArchiveService
{
    private static readonly TimeSpan PageWait = TimeSpan.FromMinutes(3);

    private readonly ILogger<ComicArchiveService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<ComicBook>>> _books = new();

    // Unpacking is disk and CPU heavy; two at once keeps small servers responsive.
    private readonly SemaphoreSlim _unpackGate = new(2, 2);

    public ComicArchiveService(ILogger<ComicArchiveService> logger)
    {
        _logger = logger;
    }

    public Task<ComicBook> OpenAsync(string archivePath)
    {
        var key = BookKey.For(archivePath);
        var lazy = _books.GetOrAdd(key, k => new Lazy<Task<ComicBook>>(() => Task.Run(() => Index(archivePath, k))));
        return lazy.Value;
    }

    /// <summary>The original image for a page. Caller disposes the stream.</summary>
    public async Task<Stream> OpenPageAsync(ComicBook book, int index, CancellationToken ct)
    {
        if (book.DirectZip)
        {
            using var zip = ZipFile.OpenRead(book.SourcePath);
            var entry = zip.GetEntry(book.Pages[index]) ?? throw new FileNotFoundException(book.Pages[index]);
            var ms = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            await using (var es = entry.Open())
            {
                await es.CopyToAsync(ms, ct).ConfigureAwait(false);
            }

            ms.Position = 0;
            return ms;
        }

        var file = await WaitForUnpackedAsync(book, index, ct).ConfigureAwait(false);
        return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    }

    /// <summary>A path to the page on disk (unpacked comics only).</summary>
    public Task<string> WaitForUnpackedAsync(ComicBook book, int index, CancellationToken ct)
    {
        if (book.Unpacked is null)
        {
            throw new InvalidOperationException("This comic is read in place.");
        }

        return book.Unpacked[index].Task.WaitAsync(PageWait, ct);
    }

    public static string ContentTypeFor(ComicBook book, int index) => BookFormats.ContentTypeFor(book.Pages[index]);

    private ComicBook Index(string path, string key)
    {
        try
        {
            var folder = Path.Combine(BookKey.CacheRoot, key);
            Directory.CreateDirectory(folder);
            BookKey.Touch(folder);

            var zipBook = TryIndexZip(path, key, folder);
            if (zipBook is not null)
            {
                return zipBook;
            }

            return IndexAndUnpack(path, key, folder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Book Reader: could not read comic archive {Path}", path);
            _books.TryRemove(key, out _);
            throw;
        }
    }

    private static ComicBook? TryIndexZip(string path, string key, string folder)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var pages = zip.Entries
                .Where(e => e.Length > 0 && IsPage(e.FullName))
                .Select(e => e.FullName)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            pages.Sort(BookFormats.NaturalCompare);

            // Make sure .NET can decompress this ZIP (a few use rare compression methods).
            if (pages.Count > 0)
            {
                using var probe = zip.GetEntry(pages[0])!.Open();
                probe.ReadByte();
            }

            return new ComicBook { SourcePath = path, Key = key, CacheFolder = folder, Pages = pages, DirectZip = true };
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            return null; // Not a ZIP (or one .NET can't read): fall back to unpacking.
        }
    }

    private ComicBook IndexAndUnpack(string path, string key, string folder)
    {
        List<string> pages;
        using (var archive = ArchiveFactory.Open(path))
        {
            pages = archive.Entries
                .Where(e => !e.IsDirectory && e.Key is not null && IsPage(e.Key))
                .Select(e => e.Key!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        pages.Sort(BookFormats.NaturalCompare);

        var dir = Path.Combine(folder, "x");
        Directory.CreateDirectory(dir);
        var ready = new TaskCompletionSource<string>[pages.Count];
        for (var i = 0; i < pages.Count; i++)
        {
            ready[i] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var existing = UnpackedPath(dir, i, pages[i]);
            if (File.Exists(existing))
            {
                ready[i].TrySetResult(existing);
            }
        }

        var book = new ComicBook { SourcePath = path, Key = key, CacheFolder = folder, Pages = pages, DirectZip = false, Unpacked = ready };
        if (ready.Any(r => !r.Task.IsCompleted))
        {
            _ = Task.Run(() => UnpackAsync(book, dir));
        }

        return book;
    }

    private async Task UnpackAsync(ComicBook book, string dir)
    {
        await _unpackGate.WaitAsync().ConfigureAwait(false);
        var failed = false;
        try
        {
            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < book.Pages.Count; i++)
            {
                order.TryAdd(book.Pages[i], i);
            }

            using var archive = ArchiveFactory.Open(book.SourcePath);
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                var k = reader.Entry.Key;
                if (k is null || reader.Entry.IsDirectory || !order.TryGetValue(k, out var idx))
                {
                    continue;
                }

                var tcs = book.Unpacked![idx];
                if (tcs.Task.IsCompleted)
                {
                    continue;
                }

                var target = UnpackedPath(dir, idx, k);
                var temp = target + ".part";
                using (var fs = File.Create(temp))
                {
                    reader.WriteEntryTo(fs);
                }

                File.Move(temp, target, true);
                tcs.TrySetResult(target);
            }
        }
        catch (Exception ex)
        {
            failed = true;
            _logger.LogError(ex, "Book Reader: could not unpack comic {Path}", book.SourcePath);
        }
        finally
        {
            _unpackGate.Release();
            foreach (var t in book.Unpacked!)
            {
                if (t.TrySetException(new InvalidDataException("This page could not be read from the archive.")))
                {
                    failed = true;
                }
            }

            if (failed)
            {
                // Forget this book so the next open tries again from scratch.
                _books.TryRemove(book.Key, out _);
            }
        }
    }

    private static string UnpackedPath(string dir, int index, string entry)
        => Path.Combine(dir, index.ToString("D5") + Path.GetExtension(entry).ToLowerInvariant());

    private static bool IsPage(string entry)
        => BookFormats.ImageExtensions.Contains(Path.GetExtension(entry))
           && !entry.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase)
           && !Path.GetFileName(entry).StartsWith('.');
}
