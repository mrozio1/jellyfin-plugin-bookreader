namespace Jellyfin.Plugin.BookReader.Services;

/// <summary>
/// Keeps each EPUB's "locations" (the map that turns a position into a
/// percentage and powers the progress bar). Working this out means reading the
/// whole book, which is slow on phones, so the first reader to open a book does it
/// once and every later reader, on any device, gets it instantly.
/// </summary>
public class LocationsStore
{
    private static string Folder
    {
        get
        {
            var dir = Path.Combine(Plugin.Instance!.PluginDataPath, "locations");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public async Task<string?> GetAsync(string bookPath)
    {
        var file = Path.Combine(Folder, BookKey.For(bookPath) + ".json");
        return File.Exists(file) ? await File.ReadAllTextAsync(file).ConfigureAwait(false) : null;
    }

    public async Task SaveAsync(string bookPath, string json)
    {
        var file = Path.Combine(Folder, BookKey.For(bookPath) + ".json");
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
        File.Move(temp, file, true);
    }
}
