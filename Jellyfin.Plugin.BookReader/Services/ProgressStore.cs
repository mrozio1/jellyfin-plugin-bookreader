using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.BookReader.Services;

/// <summary>A user's place in one book.</summary>
public class ReadingProgress
{
    /// <summary>EPUB CFI, PDF page number, or comic page index, as a string.</summary>
    [JsonPropertyName("location")]

    public string Location { get; set; } = string.Empty;

    /// <summary>0 to 100.</summary>
    [JsonPropertyName("percent")]

    public double Percent { get; set; }

    [JsonPropertyName("updatedUtc")]

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>A saved place in a book.</summary>
public class Bookmark
{
    /// <summary>EPUB CFI, PDF page number, or comic page index, as a string.</summary>
    [JsonPropertyName("location")]

    public string Location { get; set; } = string.Empty;

    /// <summary>What to show in the list, e.g. the chapter name.</summary>
    [JsonPropertyName("label")]

    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("percent")]

    public double Percent { get; set; }

    [JsonPropertyName("createdUtc")]

    public DateTime CreatedUtc { get; set; }
}

/// <summary>Per-user reading settings (only used when the admin allows overrides).</summary>
public class UserReaderSettings
{
    [JsonPropertyName("theme")]

    public string? Theme { get; set; }
    [JsonPropertyName("font")]

    public string? Font { get; set; }
    [JsonPropertyName("fontSize")]

    public int? FontSize { get; set; }
    [JsonPropertyName("lineHeight")]

    public double? LineHeight { get; set; }
    [JsonPropertyName("margins")]

    public string? Margins { get; set; }
    [JsonPropertyName("layout")]

    public string? Layout { get; set; }
    [JsonPropertyName("comicDirection")]

    public string? ComicDirection { get; set; }
    [JsonPropertyName("comicTwoPageSpread")]

    public bool? ComicTwoPageSpread { get; set; }
}

internal class UserFile
{
    public Dictionary<string, ReadingProgress> Progress { get; set; } = new();
    public UserReaderSettings Settings { get; set; } = new();
    public Dictionary<string, List<Bookmark>> Bookmarks { get; set; } = new();
}

/// <summary>
/// Stores reading position and personal settings on the server so they
/// follow each user from phone to tablet to TV.
/// </summary>
public class ProgressStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    private static string FileFor(Guid userId)
    {
        var dir = Path.Combine(Plugin.Instance!.PluginDataPath, "users");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, userId.ToString("N") + ".json");
    }

    public async Task<ReadingProgress?> GetProgressAsync(Guid userId, Guid itemId)
    {
        var file = await LoadAsync(userId).ConfigureAwait(false);
        return file.Progress.TryGetValue(itemId.ToString("N"), out var p) ? p : null;
    }

    public Task SaveProgressAsync(Guid userId, Guid itemId, ReadingProgress progress)
    {
        progress.UpdatedUtc = DateTime.UtcNow;
        return MutateAsync(userId, f => f.Progress[itemId.ToString("N")] = progress);
    }

    public async Task<List<Bookmark>> GetBookmarksAsync(Guid userId, Guid itemId)
    {
        var file = await LoadAsync(userId).ConfigureAwait(false);
        return file.Bookmarks.TryGetValue(itemId.ToString("N"), out var list) ? list : new List<Bookmark>();
    }

    public async Task<List<Bookmark>> AddBookmarkAsync(Guid userId, Guid itemId, Bookmark bookmark)
    {
        bookmark.CreatedUtc = DateTime.UtcNow;
        List<Bookmark> result = new();
        await MutateAsync(userId, f =>
        {
            var key = itemId.ToString("N");
            if (!f.Bookmarks.TryGetValue(key, out var list))
            {
                list = new List<Bookmark>();
                f.Bookmarks[key] = list;
            }

            list.RemoveAll(b => b.Location == bookmark.Location);
            list.Add(bookmark);
            list.Sort((a, b) => a.Percent.CompareTo(b.Percent));
            if (list.Count > 500)
            {
                list.RemoveRange(500, list.Count - 500);
            }

            result = list.ToList();
        }).ConfigureAwait(false);
        return result;
    }

    public async Task<List<Bookmark>> RemoveBookmarkAsync(Guid userId, Guid itemId, string location)
    {
        List<Bookmark> result = new();
        await MutateAsync(userId, f =>
        {
            if (f.Bookmarks.TryGetValue(itemId.ToString("N"), out var list))
            {
                list.RemoveAll(b => b.Location == location);
                result = list.ToList();
            }
        }).ConfigureAwait(false);
        return result;
    }

    public async Task<UserReaderSettings> GetSettingsAsync(Guid userId)
    {
        var file = await LoadAsync(userId).ConfigureAwait(false);
        return file.Settings;
    }

    public Task SaveSettingsAsync(Guid userId, UserReaderSettings settings)
        => MutateAsync(userId, f => f.Settings = settings);

    private async Task<UserFile> LoadAsync(Guid userId)
    {
        var path = FileFor(userId);
        if (!File.Exists(path))
        {
            return new UserFile();
        }

        var gate = _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var fs = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<UserFile>(fs, JsonOpts).ConfigureAwait(false) ?? new UserFile();
        }
        catch (JsonException)
        {
            return new UserFile();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task MutateAsync(Guid userId, Action<UserFile> change)
    {
        var gate = _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = FileFor(userId);
            UserFile file = new();
            if (File.Exists(path))
            {
                try
                {
                    await using var read = File.OpenRead(path);
                    file = await JsonSerializer.DeserializeAsync<UserFile>(read, JsonOpts).ConfigureAwait(false) ?? new UserFile();
                }
                catch (JsonException)
                {
                    file = new UserFile();
                }
            }

            change(file);
            var tmp = path + ".tmp";
            await using (var write = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(write, file, JsonOpts).ConfigureAwait(false);
            }

            File.Move(tmp, path, true);
        }
        finally
        {
            gate.Release();
        }
    }
}
