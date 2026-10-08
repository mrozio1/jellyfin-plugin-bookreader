# Book Reader for Jellyfin

A reader that lives inside Jellyfin 10.11. It opens EPUB, PDF and comic files quickly on phones, tablets, desktops and TVs. As the server admin, you set the reading style for everyone in one place.

## What it opens

| Format | How it works | Needs anything extra? |
|---|---|---|
| EPUB, KEPUB | Rendered in the browser with epub.js | No |
| PDF | pdf.js fetches only the pages being viewed (HTTP range requests), so large PDFs open right away | No |
| CBZ, CBR, CB7, CBT | Pages are sent one at a time, with a few loaded ahead. RAR/7z comics are unpacked once to a cache so page turns stay instant | No |
| MOBI, AZW, AZW3, FB2, LIT, PDB, DOCX, RTF, TXT, HTMLZ… | Converted to EPUB on first open, then cached | [Calibre](https://calibre-ebook.com) on the server |
| DjVu | Converted to PDF on first open, then cached | DjVuLibre (`ddjvu`) on the server |

DRM-protected books (Kindle store, Adobe DRM) can't be opened. That limit applies to every third-party reader.

## Features

- **Four page colours:** Paper, Sepia, Slate and Night (pure black for OLED screens)
- **Three bundled typefaces:** Literata (designed for screen reading), the device's sans-serif, and Atkinson Hyperlegible for low vision or dyslexia. You can also keep the book's own font
- **Reading controls:** text size, line spacing and margins; page-turn or continuous scroll
- **Comic modes:** left-to-right, right-to-left (manga) and vertical (webtoons), with two-page spreads on wide screens
- **Synced position:** each user's place in each book is saved on the server, so it follows them across devices
- **Contents:** a table of contents for EPUBs and for PDFs that have bookmarks
- **Navigation:** chapter marks on the progress bar; tap, swipe, keyboard and TV remote (arrow keys + Enter)
- **Offline-friendly:** all libraries and fonts are bundled, so nothing loads from the internet
- **Integration:** a **Read** button on book pages. Optionally, the Play button opens books here instead of Jellyfin's built-in viewer

## Install (easiest: from Jellyfin's catalog)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** (on some versions it's under the **Catalog** tab's settings), choose **+**, and add:
   - Name: `Book Reader`
   - URL: `https://raw.githubusercontent.com/mrozio1/jellyfin-plugin-bookreader/main/manifest.json`
2. Open the **Catalog**, find **Book Reader**, and install it.
3. Restart Jellyfin, then go to **Dashboard → Plugins → Book Reader** to set the defaults.

Updates show up in the catalog automatically whenever a new release is published.

## Publishing a release

GitHub builds everything for you, so you don't need .NET installed. To publish:

1. On GitHub, go to **Releases → Draft a new release**.
2. Create a tag such as `v1.0.0`.
3. Publish the release.

The workflow builds the plugin, attaches the zip, and updates `manifest.json`. Jellyfin then sees the new version.

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
- pdf.js (Apache-2.0)
- Literata and Atkinson Hyperlegible (SIL Open Font License)
- SharpCompress (MIT)
