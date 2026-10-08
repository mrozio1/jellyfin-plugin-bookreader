using System.Reflection;
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

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ComicArchiveService _comics;
    private readonly ConversionService _conversion;
    private readonly ProgressStore _progress;
    private readonly WebClientInjector _injector;

    public BookReaderController(
        ILibraryManager library,
        IUserManager users,
        ComicArchiveService comics,
        ConversionService conversion,
        ProgressStore progress,
        WebClientInjector injector)
    {
        _library = library;
        _users = users;
        _comics = comics;
        _conversion = conversion;
        _progress = progress;
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

        var asm = Assembly.GetExecutingAssembly();
        var resource = typeof(Plugin).Namespace + ".Web." + path.Replace('/', '.');
        var stream = asm.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return NotFound();
        }

        // Libraries never change between plugin versions; app files are revalidated.
        Response.Headers[HeaderNames.CacheControl] = path.StartsWith("lib/", StringComparison.Ordinal)
            ? "public, max-age=604800"
            : "no-cache";
        return File(stream, BookFormats.ContentTypeFor(path));
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
                var index = await _comics.GetIndexAsync(path).ConfigureAwait(false);
                pageCount = index.Pages.Count;
            }
            catch (Exception)
            {
                return Ok(Describe(item, ReaderKind.Unsupported, null, new { state = "Failed", error = "This comic archive could not be opened." }));
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

    /// <summary>One comic page as an image.</summary>
    [HttpGet("Books/{itemId}/Pages/{index:int}")]
    [Authorize]
    public async Task<IActionResult> ComicPage([FromRoute] Guid itemId, [FromRoute] int index)
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

        var page = await _comics.OpenPageAsync(item.Path, index).ConfigureAwait(false);
        if (page is null)
        {
            return NotFound();
        }

        Response.Headers[HeaderNames.CacheControl] = "private, max-age=604800, immutable";
        return File(page.Value.Stream, BookFormats.ContentTypeFor(page.Value.Name));
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
        return Ok(new
        {
            calibre = ConversionService.FindCalibre(),
            ddjvu = ConversionService.FindDdjvu()
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
            pageCount,
            conversion
        };
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

        return (item, null);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._/-]+$")]
    private static partial Regex SafeAssetPath();
}
