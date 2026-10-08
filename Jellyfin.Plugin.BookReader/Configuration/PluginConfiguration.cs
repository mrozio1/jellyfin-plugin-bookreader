using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BookReader.Configuration;

/// <summary>
/// Server-wide settings. Set by the admin on the plugin page; every user
/// starts from these defaults.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ---- Look & feel ----
    /// <summary>light | sepia | dark | black</summary>
    public string DefaultTheme { get; set; } = "dark";

    /// <summary>Hex colour used for buttons, progress bar and highlights.</summary>
    public string AccentColor { get; set; } = "#00a4dc";

    /// <summary>serif | sans | dyslexic | publisher</summary>
    public string DefaultFont { get; set; } = "serif";

    public int DefaultFontSize { get; set; } = 110; // percent

    public double DefaultLineHeight { get; set; } = 1.6;

    /// <summary>narrow | normal | wide</summary>
    public string DefaultMargins { get; set; } = "normal";

    /// <summary>paginated | scrolled</summary>
    public string DefaultLayout { get; set; } = "paginated";

    /// <summary>Let users change their own reading settings.</summary>
    public bool AllowUserOverrides { get; set; } = true;

    // ---- Comics ----
    /// <summary>ltr | rtl | vertical</summary>
    public string ComicDirection { get; set; } = "ltr";

    /// <summary>Show two pages side-by-side on wide screens.</summary>
    public bool ComicTwoPageSpread { get; set; } = true;

    /// <summary>How many pages to load ahead (higher = smoother, more data).</summary>
    public int PrefetchPages { get; set; } = 3;

    // ---- Integration ----
    /// <summary>Add a "Read" button to book pages in the Jellyfin web client.</summary>
    public bool InjectIntoWebClient { get; set; } = true;

    /// <summary>Open books in this reader instead of Jellyfin's built-in one.</summary>
    public bool ReplaceBuiltInReader { get; set; } = true;

    // ---- Conversion ----
    /// <summary>Convert MOBI/AZW3/FB2/etc. to EPUB with Calibre.</summary>
    public bool EnableConversion { get; set; } = true;

    /// <summary>Path to Calibre's ebook-convert. Blank = search PATH.</summary>
    public string CalibreConvertPath { get; set; } = string.Empty;

    /// <summary>Path to ddjvu (DjVuLibre) for DjVu files. Blank = search PATH.</summary>
    public string DdjvuPath { get; set; } = string.Empty;

    /// <summary>Maximum minutes to wait for a single conversion.</summary>
    public int ConversionTimeoutMinutes { get; set; } = 10;
}
