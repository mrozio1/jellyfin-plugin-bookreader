namespace Jellyfin.Plugin.BookReader.Services;

/// <summary>How the browser will render a given book.</summary>
public enum ReaderKind
{
    Unsupported,
    Epub,
    Pdf,
    Comic,
    NeedsConversion
}

public static class BookFormats
{
    private static readonly HashSet<string> Comic = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cbz", ".cbr", ".cb7", ".cbt", ".zip", ".rar", ".7z"
    };

    // Formats Calibre's ebook-convert can turn into EPUB.
    private static readonly HashSet<string> CalibreInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mobi", ".azw", ".azw3", ".azw4", ".kfx", ".prc", ".pdb", ".fb2", ".fbz", ".lit", ".lrf",
        ".rtf", ".odt", ".docx", ".txt", ".txtz", ".htmlz", ".html", ".htm", ".snb", ".tcr", ".rb", ".pml", ".chm"
    };

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".bmp", ".jxl"
    };

    public static ReaderKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".epub", StringComparison.OrdinalIgnoreCase) || ext.Equals(".kepub", StringComparison.OrdinalIgnoreCase))
        {
            return ReaderKind.Epub;
        }

        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ReaderKind.Pdf;
        }

        if (Comic.Contains(ext))
        {
            return ReaderKind.Comic;
        }

        if (CalibreInputs.Contains(ext) || ext.Equals(".djvu", StringComparison.OrdinalIgnoreCase) || ext.Equals(".djv", StringComparison.OrdinalIgnoreCase))
        {
            return ReaderKind.NeedsConversion;
        }

        return ReaderKind.Unsupported;
    }

    public static bool IsDjvu(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".djvu", StringComparison.OrdinalIgnoreCase) || ext.Equals(".djv", StringComparison.OrdinalIgnoreCase);
    }

    public static string ContentTypeFor(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".epub" or ".kepub" => "application/epub+zip",
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".avif" => "image/avif",
            ".bmp" => "image/bmp",
            ".jxl" => "image/jxl",
            ".js" or ".mjs" => "text/javascript",
            ".css" => "text/css",
            ".html" => "text/html",
            ".svg" => "image/svg+xml",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
    }

    /// <summary>Sorts "page2.jpg" before "page10.jpg".</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return string.CompareOrdinal(a, b);
        }

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }
}
