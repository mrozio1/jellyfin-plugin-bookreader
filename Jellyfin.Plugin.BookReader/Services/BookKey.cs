using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.BookReader.Services;

/// <summary>
/// A short stable id for one version of a book file. Changes when the file is
/// replaced, so cached data for the old file is never served for the new one.
/// </summary>
public static class BookKey
{
    public static string For(string path)
    {
        var info = new FileInfo(path);
        var raw = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    /// <summary>Root folder for disposable caches (unpacked comics, resized pages).</summary>
    public static string CacheRoot
    {
        get
        {
            var dir = Path.Combine(Plugin.Instance!.PluginDataPath, "cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Marks a cache folder as recently used so the cleanup keeps it.</summary>
    public static void Touch(string folder)
    {
        try
        {
            var marker = Path.Combine(folder, ".used");
            if (!File.Exists(marker))
            {
                File.WriteAllText(marker, string.Empty);
            }
            else if (DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) > TimeSpan.FromMinutes(10))
            {
                File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
            }
        }
        catch (IOException)
        {
            // Not important enough to fail a request over.
        }
    }
}
