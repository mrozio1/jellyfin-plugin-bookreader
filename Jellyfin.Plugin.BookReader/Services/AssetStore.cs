using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.BookReader.Services;

/// <summary>A reader file held in memory, with pre-compressed copies.</summary>
public sealed record WebAsset(byte[] Raw, byte[]? Gzip, byte[]? Brotli, string ContentType, string ETag);

/// <summary>
/// Serves the reader's own files (page, scripts, fonts, libraries) from memory,
/// compressed once and cached by browsers for a year. URLs carry the plugin
/// version, so an update is picked up immediately.
/// </summary>
public class AssetStore
{
    private const string VersionToken = "__V__";

    private static readonly HashSet<string> Compressible = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".css", ".html", ".svg", ".json"
    };

    private readonly ConcurrentDictionary<string, Lazy<WebAsset?>> _cache = new(StringComparer.Ordinal);

    public static string Version { get; } = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0";

    public WebAsset? Get(string path) => _cache.GetOrAdd(path, p => new Lazy<WebAsset?>(() => Load(p))).Value;

    private static WebAsset? Load(string path)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resource = typeof(Plugin).Namespace + ".Web." + path.Replace('/', '.');
        using var stream = asm.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var raw = ms.ToArray();
        var ext = Path.GetExtension(path);

        // Our own text files get the version stamped into their asset links.
        if (Compressible.Contains(ext) && !path.StartsWith("lib/", StringComparison.Ordinal))
        {
            var text = Encoding.UTF8.GetString(raw);
            if (text.Contains(VersionToken, StringComparison.Ordinal))
            {
                raw = Encoding.UTF8.GetBytes(text.Replace(VersionToken, Version, StringComparison.Ordinal));
            }
        }

        byte[]? gzip = null, brotli = null;
        if (Compressible.Contains(ext) && raw.Length > 1024)
        {
            gzip = Compress(raw, s => new GZipStream(s, CompressionLevel.SmallestSize, true));
            brotli = Compress(raw, s => new BrotliStream(s, CompressionLevel.SmallestSize, true));
        }

        var etag = "\"" + Version + "-" + Convert.ToHexString(SHA1.HashData(raw))[..12].ToLowerInvariant() + "\"";
        return new WebAsset(raw, gzip, brotli, BookFormats.ContentTypeFor(path), etag);
    }

    private static byte[] Compress(byte[] data, Func<Stream, Stream> make)
    {
        using var output = new MemoryStream();
        using (var z = make(output))
        {
            z.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }
}
