# Book Reader for Jellyfin

A reader that lives inside Jellyfin 10.11. It opens EPUB, PDF and comic files quickly on phones, tablets, desktops and TVs. As the server admin, you set the reading style for everyone in one place.

## What it opens

| Format | How it works | Needs anything extra? |
|---|---|---|
| EPUB, KEPUB | Rendered in the browser with epub.js. Recently opened books are kept on the device, so reopening is instant | No |
| PDF | pdf.js fetches only the pages being viewed (HTTP range requests), so large PDFs open right away | No |
| CBZ, CBR, CB7, CBT | Pages are sent one at a time, resized to the screen, with the next few prepared ahead. RAR/7z comics open straight away and unpack in the background | No |
| MOBI, AZW, AZW3, FB2, LIT, PDB, DOCX, RTF, TXT, HTMLZ… | Converted to EPUB on first open, then cached | [Calibre](https://calibre-ebook.com) on the server |
| DjVu | Converted to PDF on first open, then cached | DjVuLibre (`ddjvu`) on the server |

DRM-protected books (Kindle store, Adobe DRM) can't be opened. That limit applies to every third-party reader.

## Features

- **Desk & Ribbons design:** the page sits on a desk, and the stack of page edges behind it thins out as you read. Controls are ribbon markers tucked under the page, with big Previous and Next buttons on either side.
- **Books:** running heads with the book and chapter title, page numbers, printed-style scene breaks, and a two-page spread on wide screens.
- **Comics:** pages sit on the desk as sheets. **Pages** opens a thumbnail grid of the whole issue, and **Panels** zooms from panel to panel, finding the gutters automatically. Reading direction can be left to right, right to left (manga) or vertical (webtoons), with two-page spreads on wide screens.
- **Zoom:** in comics and PDFs, pinch or double-tap to magnify a page and drag to move around; a sharper image loads as you zoom. In books, pinch or use the A−/A+ buttons to change text size on the fly. On a computer, use Ctrl + scroll or the `+`, `−` and `0` keys.
- **Bookmarks:** the Bookmark ribbon marks the page. Bookmarks are saved on the server and listed under Contents (books) or Pages (comics).
- **Page colours:** Paper, Sepia, Slate and Night, each with a matching desk.
- **Typefaces:** Literata, the device's sans-serif, Atkinson Hyperlegible, or the book's own font. You can also set text size, line spacing and margins, and choose page-turn or scroll.
- **Synced position:** each user's place in each book is saved on the server, so it follows them across devices.
- **Navigation:** tap or swipe the page edges, or use the keyboard and TV remotes: arrow keys, Enter, T for Contents, B for Bookmark, P for Panels, S for Settings.
- **Offline-friendly:** all libraries and fonts are bundled, so nothing loads from the internet.
- **Integration:** a **Read** button on book pages. Optionally, the Play button opens books here instead of Jellyfin's built-in viewer.

## Speed

The `Speed test` workflow runs the plugin inside a real Jellyfin 10.11 server with large sample books. It measures each one on a fast home network and on a simulated phone away from home (80 ms round trip, 25 Mbit/s). Results for 1.1 on the phone connection:

| Book | 1.0 first open | 1.1 first open | 1.1 reopen |
|---|---|---|---|
| 200-page scanned PDF (50 MB) | 17.8 s | 2.3 s (1.4 MB downloaded) | 0.4 s |
| Illustrated EPUB (7 MB) | 3.6 s | 3.3 s | 0.3 s |
| 120-page CBZ comic (107 MB) | 1.1 s | 0.7 s | 0.2 s |
| 120-page 7z comic (105 MB) | 7.6 s before the server responded | 0.7 s | 0.2 s |

Comic page turns on the phone connection went from 0.3–1 s to under 0.1 s, because pages are sent at screen size (about 110 KB instead of 900 KB).

### How it stays fast

- **Comics:** pages are resized for the screen using Jellyfin's own image encoder, then kept in a cache. The cache size is set on the plugin page; books opened least recently are removed first. RAR, 7z and TAR archives are indexed instantly and unpacked in the background in reading order.
- **PDF:** only the parts of the file needed for the pages on screen are fetched. Book files are sent with `Cache-Control: no-transform` so the server and proxies don't compress them, since compression would force a full download. The bundled pdf.js worker has a small patch (marked `[Book Reader patch]`) so pages in flat page lists, which are common in scanned PDFs, are found directly. Without it, pdf.js reads every page before showing the first.
- **EPUB:** the book downloads at the same time as the reader code. Each book's page map, used for the progress bar, is measured once and shared by every user and device.
- **Reader files:** compressed once and cached by browsers for a year. Links are versioned, so updates apply immediately.

### If books are still slow

- **Behind a reverse proxy** (Nginx Proxy Manager, Caddy, Traefik): make sure it doesn't buffer or compress `/BookReader/Books/` responses. With Nginx, add `proxy_buffering off;` for that path.
- **A comic is slow only the first time:** that's the server preparing pages. Later opens use the cache.
- **Jumping to the end of a large RAR or 7z comic the first time:** this waits until unpacking reaches that page. Solid archives must be unpacked in order.

## Install (easiest: from Jellyfin's catalog)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** (on some versions it's under the **Catalog** tab's settings), choose **+**, and add:
   - Name: `Book Reader`
   - URL: `https://raw.githubusercontent.com/mrozio1/jellyfin-plugin-bookreader/main/manifest.json`
2. Open the **Catalog**, find **Book Reader**, and install it.
3. Restart Jellyfin, then go to **Dashboard → Plugins → Book Reader** to set the defaults.

Updates show up in the catalog automatically whenever a new release is published.

## Publishing a release

GitHub builds everything for you, so you don't need .NET installed. To ship an update:

1. Open the `VERSION` file on GitHub and edit it.
2. Raise the number, for example from `1.0.0.0` to `1.0.1.0`.
3. Commit the change.

The workflow builds the plugin, publishes a release with the zip, and updates `manifest.json`. Jellyfin then offers the update in its catalog.

To build locally instead, install the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and run:

```bash
dotnet publish Jellyfin.Plugin.BookReader/Jellyfin.Plugin.BookReader.csproj -c Release -o out
```

## Install by hand (Docker)

1. Download `bookreader_x.x.x.x.zip` from the latest release.
2. Unzip it into a `BookReader` folder inside the plugins folder of your Jellyfin config volume. That's `/config/plugins/BookReader` in the container, so on the host it's wherever `/config` is mapped.
3. Restart the container.

### Optional: extra formats

- Debian/Ubuntu: `sudo apt install calibre djvulibre-bin`
- Docker: use an image that includes Calibre, or mount a Calibre install and set its path on the plugin page.

The plugin page shows whether each converter was found.

### If the Read button doesn't appear

The plugin adds one script tag to the web client's `index.html` on each server start. If that file is read-only (some Docker setups), the plugin page tells you so. In that case, you can:

- Make the web folder writable, or
- Open a book directly at `https://YOUR-SERVER/BookReader/Reader?id=ITEM_ID` while signed in to Jellyfin in the same browser.

The native Jellyfin apps for Android TV, Roku and Swiftfin use their own interfaces, so they don't run web-client scripts. On those devices, open books in a browser instead.

## API

All endpoints require a Jellyfin sign-in except the page shell and static assets.

| Method | Path | Purpose |
|---|---|---|
| GET | `/BookReader/Reader?id=` | Reader page |
| GET | `/BookReader/Settings` | Server defaults merged with the user's own choices |
| POST | `/BookReader/Settings` | Save the user's own choices (if allowed) |
| GET | `/BookReader/Books/{id}/Info` | Format, page count, conversion status |
| GET | `/BookReader/Books/{id}/File` | Book file, with range support |
| GET | `/BookReader/Books/{id}/Pages/{n}` | One comic page |
| GET/POST | `/BookReader/Progress/{id}` | Reading position |
| GET | `/BookReader/Admin/Status` | Converter detection (admins) |
| POST | `/BookReader/Admin/ApplyWebIntegration` | Re-apply the Read button (admins) |

## Credits

The reader bundles the following, each under its own license (see `Web/lib` and `Web/fonts`):

- epub.js (BSD-2-Clause)
- JSZip (MIT)
- pdf.js (Apache-2.0; the worker is modified as described under Speed)
- Literata and Atkinson Hyperlegible (SIL Open Font License)
- SharpCompress (MIT)
