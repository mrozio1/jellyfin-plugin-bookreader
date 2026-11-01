using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.BookReader.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.BookReader.Api;

/// <summary>
/// HTTP API used by the reader. Everything except the static page shell
/// requires a signed-in Jellyfin user who can see the book.
/// </summary>
[ApiController]
[Route("BookReader")]
public partial class BookReaderController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";

    // Page turns and PDF range reads hit the API many times a second; checking the
    // library for each one is wasted work, so a user's access to a book is
    // remembered for a minute.
    private static readonly ConcurrentDictionary<(Guid User, Guid Item), (BaseItem Item, long Expires)> AccessCache = new();

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ComicArchiveService _comics;
    private readonly PageImageService _pageImages;
    private readonly ConversionService _conversion;
    private readonly ProgressStore _progress;
    private readonly LocationsStore _locations;
    private readonly AssetStore _assets;
    private readonly WebClientInjector _injector;

    public BookReaderController(
        ILibraryManager library,
        IUserManager users,
        ComicArchiveService comics,
        PageImageService pageImages,
        ConversionService conversion,
        ProgressStore progress,
        LocationsStore locations,
        AssetStore assets,
        WebClientInjector injector)
    {
        _library = library;
        _users = users;
        _comics = comics;
        _pageImages = pageImages;
        _conversion = conversion;
        _progress = progress;
        _locations = locations;
        _assets = assets;
        _injector = injector;
    }

    // ------------------------------------------------------------------
    // Static files (reader page, scripts, libraries)
    // ------------------------------------------------------------------

    /// <summary>The reader page itself. Opened as /BookReader/Reader?id=ITEM_ID</summary>
    [HttpGet("Reader")]
    [AllowAnonymous]
    public IActionResult Reader() => Asset("reader.html");

    [HttpGet("Assets/{**path}")]
    [AllowAnonymous]
    public IActionResult Asset(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains("..", StringComparison.Ordinal) || !SafeAssetPath().IsMatch(path))
        {
            return NotFound();
        }

        var asset = _assets.Get(path);
        if (asset is null)
        {
            return NotFound();
        }

        // Versioned links (?v=) never change, so browsers can keep them for a year.
        // Anything else is checked with the server each time (a tiny 304 when unchanged).
        Response.Headers[HeaderNames.CacheControl] = Request.Query.ContainsKey("v") && path != "reader.html"
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        Response.Headers[HeaderNames.ETag] = asset.ETag;
        Response.Headers[HeaderNames.Vary] = HeaderNames.AcceptEncoding;

        if (Request.Headers.IfNoneMatch.ToString() == asset.ETag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var accept = Request.Headers.AcceptEncoding.ToString();
        if (asset.Brotli is not null && accept.Contains("br", StringComparison.OrdinalIgnoreCase))
        {
            Response.Headers[HeaderNames.ContentEncoding] = "br";
            return File(asset.Brotli, asset.ContentType);
        }

        if (asset.Gzip is not null && accept.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            Response.Headers[HeaderNames.ContentEncoding] = "gzip";
            return File(asset.Gzip, asset.ContentType);
        }

        return File(asset.Raw, asset.ContentType);
    }

    // ------------------------------------------------------------------
    // Settings
    // ------------------------------------------------------------------

    /// <summary>Server defaults merged with this user's own choices.</summary>
    [HttpGet("Settings")]
    [Authorize]
    public async Task<IActionResult> GetSettings()
    {
        var cfg = Plugin.Instance!.Configuration;
        var mine = cfg.AllowUserOverrides && TryGetUserId(out var uid)
            ? await _progress.GetSettingsAsync(uid).ConfigureAwait(false)
            : new UserReaderSettings();

        return Ok(new
        {
            allowUserOverrides = cfg.AllowUserOverrides,
            replaceBuiltInReader = cfg.ReplaceBuiltInReader,
            accentColor = cfg.AccentColor,
            prefetchPages = Math.Clamp(cfg.PrefetchPages, 1, 10),
            defaults = new
            {
                theme = cfg.DefaultTheme,
                font = cfg.DefaultFont,
                fontSize = cfg.DefaultFontSize,
                lineHeight = cfg.DefaultLineHeight,
                margins = cfg.DefaultMargins,
                layout = cfg.DefaultLayout,
                comicDirection = cfg.ComicDirection,
                comicTwoPageSpread = cfg.ComicTwoPageSpread
            },
            user = mine
        });
    }

    [HttpPost("Settings")]
    [Authorize]
    public async Task<IActionResult> SaveSettings([FromBody] UserReaderSettings settings)
    {
        if (!Plugin.Instance!.Configuration.AllowUserOverrides)
        {
            return Forbid();
        }

        if (!TryGetUserId(out var uid))
        {
            return Unauthorized();
        }

        await _progress.SaveSettingsAsync(uid, settings).ConfigureAwait(false);
        return NoContent();
    }

    // ------------------------------------------------------------------
    // Books
    // ------------------------------------------------------------------

    /// <summary>Tells the reader how to open a book.</summary>
    [HttpGet("Books/{itemId}/Info")]
    [Authorize]
    public async Task<IActionResult> Info([FromRoute] Guid itemId, [FromQuery] bool convert = true)
    {
        var (item, error) = ResolveItem(itemId);
        if (item is null)
        {
            return error!;
        }

        var path = item.Path;
        var kind = BookFormats.Classify(path);
        object? conversion = null;
        int? pageCount = null;

        if (kind == ReaderKind.NeedsConversion)
        {
            if (!Plugin.Instance!.Configuration.EnableConversion)
            {
                return Ok(Describe(item, ReaderKind.Unsupported, null, new { state = "Disabled", error = "Format conversion is turned off on this server." }));
            }

            var status = convert ? _conversion.Start(path) : _conversion.Peek(path);
            conversion = new { state = status.State.ToString(), error = status.Error };
            kind = status.State == ConversionState.Done ? ConversionService.OutputKind(path) : ReaderKind.NeedsConversion;
        }
        else if (kind == ReaderKind.Comic)
        {
            try
            {
                var book = await _comics.OpenAsync(path).ConfigureAwait(false);
                pageCount = book.Pages.Count;
            }
            catch (Exception)
            {
                return Ok(Describe(item, ReaderKind.Unsupported, null, new { state = "Failed", error = "This comic archive could not be opened. It may be damaged." }));
            }
        }

        return Ok(Describe(item, kind, pageCount, conversion));
    }

    /// <summary>Streams the book file. Supports HTTP range requests so PDFs open instantly.</summary>
    [HttpGet("Books/{itemId}/File")]
    [Authorize]
    public IActionResult BookFile([FromRoute] Guid itemId)
    {
        var (item, error) = ResolveItem(itemId);
        if (item is null)
        {
            return error!;
        }

        var path = item.Path;
        if (BookFormats.Classify(path) == ReaderKind.NeedsConversion)
        {
            var status = _conversion.Peek(path);
            if (status.State != ConversionState.Done || status.OutputPath is null)
            {
                return StatusCode(StatusCodes.Status409Conflict, new { message = "Book is still being prepared." });
            }

            path = status.OutputPath;
        }

        var info = new FileInfo(path);
        var etag = new EntityTagHeaderValue("\"" + info.Length.ToString("x") + "-" + info.LastWriteTimeUtc.Ticks.ToString("x") + "\"");
        Response.Headers[HeaderNames.CacheControl] = "private, max-age=86400";
        return PhysicalFile(path, BookFormats.ContentTypeFor(path), info.LastWriteTimeUtc, etag, enableRangeProcessing: true);
    }

    /// <summary>
    /// One comic page. Pass w (the width in device pixels the page will be shown at)
    /// to get a smaller version sized for the screen.
    /// </summary>
    [HttpGet("Books/{itemId}/Pages/{index:int}")]
    [Authorize]
    public async Task<IActionResult> ComicPage([FromRoute] Guid itemId, [FromRoute] int index, [FromQuery] int? w)
    {
        var (item, error) = ResolveItem(itemId);
        if (item is null)
        {
            return error!;
        }

        if (BookFormats.Classify(item.Path) != ReaderKind.Comic)
        {
            return BadRequest();
        }

        var ct = HttpContext.RequestAborted;
        var book = await _comics.OpenAsync(item.Path).ConfigureAwait(false);
        if (index < 0 || index >= book.Pages.Count)
        {
            return NotFound();
        }

        Response.Headers[HeaderNames.CacheControl] = "private, max-age=604800, immutable";

        try
        {
            if (PageImageService.SnapWidth(w) is int width)
            {
                var resized = await _pageImages.GetAsync(book, index, width, ct).ConfigureAwait(false);

                // Then start on the next pages, so they're ready before the reader asks.
                _pageImages.PrepareAhead(book, index + 1, Math.Clamp(Plugin.Instance!.Configuration.PrefetchPages * 2, 2, 12), width);
                if (resized is not null)
                {
                    return PhysicalFile(resized, "image/jpeg");
                }
            }

            var stream = await _comics.OpenPageAsync(book, index, ct).ConfigureAwait(false);
            return File(stream, ComicArchiveService.ContentTypeFor(book, index));
        }
        catch (TimeoutException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "The page is still being unpacked." });
        }
        catch (InvalidDataException)
        {
            return NotFound(new { message = "This page could not be read from the archive." });
        }
    }

    /// <summary>Saved EPUB locations (the position-to-percentage map), shared by everyone.</summary>
    [HttpGet("Books/{itemId}/Locations")]
    [Authorize]
    public async Task<IActionResult> GetLocations([FromRoute] Guid itemId)
    {
        var (item, error) = ResolveItem(itemId);
        if (item is null)
        {
            return error!;
        }

        var json = await _locations.GetAsync(item.Path).ConfigureAwait(false);
        if (json is null)
        {
            return NoContent();
        }

        Response.Headers[HeaderNames.CacheControl] = "private, max-age=3600";
        return Content(json, "application/json");
    }

    [HttpPost("Books/{itemId}/Locations")]
    [Authorize]
    [RequestSizeLimit(4_000_000)]
    public async Task<IActionResult> SaveLocations([FromRoute] Guid itemId, [FromBody] JsonElement body)
    {
        var (item, error) = ResolveItem(itemId);
        if (item is null)
        {
            return error!;
        }

        // Only accept a plain list of EPUB CFIs.
        if (body.ValueKind != JsonValueKind.Array || body.GetArrayLength() > 200_000)
        {
            return BadRequest();
        }

        foreach (var el in body.EnumerateArray())
        {
            var s = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            if (s is null || s.Length > 1024 || !s.StartsWith("epubcfi(", StringComparison.Ordinal))
            {
                return BadRequest();
            }
        }

        await _locations.SaveAsync(item.Path, body.GetRawText()).ConfigureAwait(false);
        return NoContent();
    }

    // ------------------------------------------------------------------
    // Reading progress
    // ------------------------------------------------------------------

    [HttpGet("Progress/{itemId}")]
    [Authorize]
    public async Task<IActionResult> GetProgress([FromRoute] Guid itemId)
    {
        if (!TryGetUserId(out var uid))
        {
            return Unauthorized();
        }

        var p = await _progress.GetProgressAsync(uid, itemId).ConfigureAwait(false);
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        return p is null ? NoContent() : Ok(p);
    }

    [HttpPost("Progress/{itemId}")]
    [Authorize]
    public async Task<IActionResult> SaveProgress([FromRoute] Guid itemId, [FromBody] ReadingProgress progress)
    {
        if (!TryGetUserId(out var uid))
        {
            return Unauthorized();
        }

        var (item, error) = ResolveItem(itemId);
        if (item is null)
        {
            return error!;
        }

        progress.Percent = Math.Clamp(progress.Percent, 0, 100);
        if (progress.Location.Length > 2048)
        {
            return BadRequest();
        }

        await _progress.SaveProgressAsync(uid, itemId, progress).ConfigureAwait(false);
        return NoContent();
    }

    // ------------------------------------------------------------------
    // Admin
    // ------------------------------------------------------------------

    /// <summary>Shows which optional tools the server has, for the admin page.</summary>
    [HttpGet("Admin/Status")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult AdminStatus()
    {
        long cacheBytes = 0;
        try
        {
            cacheBytes = new DirectoryInfo(BookKey.CacheRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (IOException)
        {
            // ignore
        }

        return Ok(new
        {
            calibre = ConversionService.FindCalibre(),
            ddjvu = ConversionService.FindDdjvu(),
            cacheMb = Math.Round(cacheBytes / 1048576.0)
        });
    }

    /// <summary>Re-applies (or removes) the web client Read button without a restart.</summary>
    [HttpPost("Admin/ApplyWebIntegration")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult ApplyWebIntegration()
    {
        var message = _injector.Apply(Plugin.Instance!.Configuration.InjectIntoWebClient);
        return Ok(new { message });
    }

    /// <summary>Empties the page cache.</summary>
    [HttpPost("Admin/ClearCache")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult ClearCache()
    {
        var removed = 0;
        foreach (var dir in new DirectoryInfo(BookKey.CacheRoot).GetDirectories())
        {
            try
            {
                dir.Delete(true);
                removed++;
            }
            catch (IOException)
            {
                // In use; it will be cleared next time.
            }
        }

        return Ok(new { message = $"Cleared {removed} books from the cache. Restart Jellyfin if comics you were reading show errors." });
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static object Describe(BaseItem item, ReaderKind kind, int? pageCount, object? conversion)
    {
        return new
        {
            id = item.Id,
            title = item.Name,
            series = (item as Book)?.SeriesName,
            format = Path.GetExtension(item.Path).TrimStart('.').ToLowerInvariant(),
            kind = kind.ToString().ToLowerInvariant(),
            fileVersion = FileVersion(item.Path),
            pageCount,
            conversion
        };
    }

    /// <summary>Changes whenever the file on disk changes; lets devices keep their own copy.</summary>
    private static string FileVersion(string path)
    {
        var info = new FileInfo(path);
        return info.Length.ToString("x") + "-" + info.LastWriteTimeUtc.Ticks.ToString("x");
    }

    private bool TryGetUserId(out Guid userId)
    {
        var raw = User.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(raw, out userId) && userId != Guid.Empty;
    }

    /// <summary>Finds the item and checks the current user is allowed to see it.</summary>
    private (BaseItem? Item, IActionResult? Error) ResolveItem(Guid itemId)
    {
        if (!TryGetUserId(out var uid))
        {
            return (null, Unauthorized());
        }

        var now = Environment.TickCount64;
        if (AccessCache.TryGetValue((uid, itemId), out var hit) && hit.Expires > now)
        {
            return (hit.Item, null);
        }

        var user = _users.GetUserById(uid);
        var item = _library.GetItemById(itemId);
        if (user is null || item is null || !item.IsVisible(user))
        {
            return (null, NotFound());
        }

        if (string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path))
        {
            return (null, NotFound(new { message = "The book file is missing on the server." }));
        }

        if (AccessCache.Count > 10_000)
        {
            AccessCache.Clear();
        }

        AccessCache[(uid, itemId)] = (item, now + 60_000);
        return (item, null);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._/-]+$")]
    private static partial Regex SafeAssetPath();
}
