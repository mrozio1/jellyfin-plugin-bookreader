"""Speed and stability test for the Book Reader plugin against a real Jellyfin server.

Sets up a fresh Jellyfin (wizard, user, book library), then measures:
  * server response times for every reader endpoint, cold and warm
  * bytes sent for the reader's own files (compression, caching)
  * in a real browser: time until the first page is visible and page-turn times,
    on a fast local network and on a simulated slow phone connection

Usage: python run.py BOOKS_DIR
Writes results.json and a Markdown summary (results.md).
"""
import asyncio
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = os.environ.get("JF_URL", "http://127.0.0.1:8096")
BOOKS_DIR = sys.argv[1]
CLIENT = 'MediaBrowser Client="perf", Device="ci", DeviceId="perf-ci", Version="1.0"'
RESULTS = {"http": {}, "browser": {}, "features": {}, "errors": []}
SHOTS = os.path.abspath("shots")
os.makedirs(SHOTS, exist_ok=True)


def call(method, path, body=None, token=None, headers=None, timeout=600):
    h = {"Authorization": CLIENT + (f', Token="{token}"' if token else "")}
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        h["Content-Type"] = "application/json"
    h.update(headers or {})
    r = urllib.request.Request(BASE + path, data=data, method=method, headers=h)
    t = time.perf_counter()
    try:
        with urllib.request.urlopen(r, timeout=timeout) as resp:
            content, status, hdrs = resp.read(), resp.status, resp.headers
    except urllib.error.HTTPError as e:
        content, status, hdrs = e.read(), e.code, e.headers
    return status, content, hdrs, round((time.perf_counter() - t) * 1000)


def wait_for_server():
    # Jellyfin 10.11 answers with a "starting up" page until it is really ready.
    for _ in range(300):
        try:
            s, c, _, _ = call("GET", "/System/Info/Public", timeout=5)
            if s == 200:
                info = json.loads(c)
                if info.get("Version"):
                    s2, _, _, _ = call("GET", "/Startup/Configuration", timeout=5)
                    if s2 != 503:
                        return info
        except Exception:
            pass
        time.sleep(1)
    raise SystemExit("Jellyfin did not start")


def setup():
    info = wait_for_server()
    print("Jellyfin", info.get("Version"))
    RESULTS["jellyfin"] = info.get("Version")
    if not info.get("StartupWizardCompleted"):
        call("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
        call("GET", "/Startup/User")
        call("POST", "/Startup/User", {"Name": "perf", "Password": "perf"})
        call("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False})
        s, c, _, _ = call("POST", "/Startup/Complete")
        print("wizard complete:", s)
    s, c, _, _ = call("POST", "/Users/AuthenticateByName", {"Username": "perf", "Pw": "perf"})
    assert s == 200, (s, c[:300])
    auth = json.loads(c)
    token, uid = auth["AccessToken"], auth["User"]["Id"]

    s, c, _, _ = call("GET", "/Plugins", token=token)
    plugins = {p["Name"]: p["Status"] for p in json.loads(c)}
    print("plugins:", plugins)
    RESULTS["plugin_status"] = plugins.get("Book Reader")
    if plugins.get("Book Reader") != "Active":
        raise SystemExit(f"Book Reader plugin not active: {plugins}")

    q = urllib.parse.urlencode({"name": "Books", "collectionType": "books", "refreshLibrary": "true", "paths": "/media/books"})
    s, c, _, _ = call("POST", f"/Library/VirtualFolders?{q}", {"LibraryOptions": {}}, token=token)
    print("library created:", s)

    expected = len([f for f in os.listdir(BOOKS_DIR) if not f.startswith(".")])
    items = []
    for _ in range(240):
        s, c, _, _ = call("GET", f"/Items?userId={uid}&Recursive=true&IncludeItemTypes=Book&Fields=Path", token=token)
        items = json.loads(c).get("Items", []) if s == 200 else []
        if len(items) >= expected:
            break
        time.sleep(1)
    print("books found:", [(i["Name"], i.get("Path")) for i in items])
    if len(items) < expected:
        raise SystemExit(f"library scan found {len(items)} of {expected} books")
    return token, {os.path.basename(i["Path"]): i["Id"] for i in items}


def http_tests(token, books):
    out = RESULTS["http"]

    # Reader's own files: transfer size and caching.
    enc = {"Accept-Encoding": "gzip, br"}
    s, c, h, ms = call("GET", "/BookReader/Reader", headers=enc)
    out["reader_page"] = {"status": s, "ms": ms, "bytes": len(c)}
    for asset in ["reader.js", "reader.css", "lib/epub.min.js", "lib/pdf.min.mjs", "lib/pdf.worker.mjs", "fonts/literata-400-normal.woff2"]:
        s, c, h, ms = call("GET", f"/BookReader/Assets/{asset}", headers=enc)
        etag = h.get("ETag")
        s2, _, _, ms2 = call("GET", f"/BookReader/Assets/{asset}", headers={**enc, **({"If-None-Match": etag} if etag else {})})
        out[f"asset {asset}"] = {"status": s, "bytes_sent": len(c), "encoding": h.get("Content-Encoding"), "cache": h.get("Cache-Control"), "revalidate_status": s2}

    for name, item in books.items():
        r = out[name] = {}
        s, c, _, ms = call("GET", f"/BookReader/Books/{item}/Info", token=token)
        r["info_cold"] = {"status": s, "ms": ms}
        info = json.loads(c) if s == 200 else {}
        r["kind"] = info.get("kind")
        s, c, _, ms = call("GET", f"/BookReader/Books/{item}/Info", token=token)
        r["info_warm"] = {"status": s, "ms": ms}

        if info.get("kind") == "comic":
            n = info.get("pageCount") or 0
            r["pages"] = n
            for label, idx, extra in [("page_first_cold", 0, ""), ("page_first_warm", 0, ""), ("page_last_cold", n - 1, ""),
                                      ("page_mid_phone_size", n // 2, "?w=1080"), ("page_mid_phone_size_again", n // 2, "?w=1080")]:
                s, c, h, ms = call("GET", f"/BookReader/Books/{item}/Pages/{idx}{extra}", token=token)
                r[label] = {"status": s, "ms": ms, "bytes": len(c), "type": h.get("Content-Type")}
        if info.get("kind") in ("pdf", "epub"):
            # What a browser gets: the size must be known and the file not compressed,
            # or the PDF viewer can't fetch just the pages it needs.
            s, c, h, ms = call("GET", f"/BookReader/Books/{item}/File", token=token, headers={"Accept-Encoding": "gzip, deflate, br"})
            r["browser_style_download"] = {"status": s, "ms": ms, "bytes": len(c), "content_length": h.get("Content-Length"),
                                           "content_encoding": h.get("Content-Encoding"), "accept_ranges": h.get("Accept-Ranges")}
        if info.get("kind") == "pdf":
            s, c, h, ms = call("GET", f"/BookReader/Books/{item}/File", token=token, headers={"Range": "bytes=0-262143"})
            r["range_first_256k"] = {"status": s, "ms": ms, "bytes": len(c)}
            s, c, h, ms = call("GET", f"/BookReader/Books/{item}/File", token=token, headers={"Range": "bytes=20000000-20262143"})
            r["range_middle_256k"] = {"status": s, "ms": ms, "bytes": len(c)}
        elif info.get("kind") == "epub":
            s, c, h, ms = call("GET", f"/BookReader/Books/{item}/File", token=token)
            r["full_download"] = {"status": s, "ms": ms, "bytes": len(c)}
        print(name, json.dumps(r))


READY_JS = """() => {
  const app = document.getElementById('app');
  if (!app || app.classList.contains('is-loading')) return false;
  const v = document.getElementById('viewer');
  if (v.querySelector('.page-slot.placeholder')) return false;
  const img = v.querySelector('.page-slot img');
  if (img) return img.complete && img.naturalWidth > 0;
  if (v.querySelector('.page-slot canvas')) return true;
  const f = v.querySelector('iframe');
  return !!(f && f.contentDocument && f.contentDocument.body && f.contentDocument.body.innerText.length > 50);
}"""


def throttle(on):
    # Simulates a phone away from home: 80 ms round trip, 25 Mbit/s.
    subprocess.run(["sudo", "tc", "qdisc", "del", "dev", "lo", "root"], stderr=subprocess.DEVNULL)
    if on:
        subprocess.run(["sudo", "tc", "qdisc", "add", "dev", "lo", "root", "netem", "delay", "40ms", "rate", "25mbit"], check=True)


async def open_book(browser, token, item, turns=6, shot=None):
    ctx = await browser.new_context(viewport={"width": 412, "height": 915}, device_scale_factor=2)
    page = await ctx.new_page()
    errors = []
    page.on("pageerror", lambda e: errors.append(f"js: {e}"))
    page.on("response", lambda r: errors.append(f"http {r.status} {r.url.split('?')[0][-60:]}") if r.status >= 400 else None)
    result = {}
    sizes = []

    async def track(req):
        try:
            sizes.append((await req.sizes())["responseBodySize"])
        except Exception:
            pass
    ctx.on("requestfinished", lambda req: asyncio.ensure_future(track(req)))
    try:
        for attempt in ("first_open", "reopen"):
            await page.goto("about:blank")
            sizes.clear()
            t = time.perf_counter()
            await page.goto(f"{BASE}/BookReader/Reader?id={item}#t={token}")
            try:
                await page.wait_for_function(READY_JS, timeout=180000, polling=50)
                result[attempt + "_ms"] = round((time.perf_counter() - t) * 1000)
                await asyncio.sleep(0.3)
                result[attempt + "_mb"] = round(sum(sizes) / 1e6, 2)
            except Exception:
                result[attempt + "_ms"] = None
                result["stuck_on"] = await page.evaluate("document.getElementById('veil-text')?.textContent")
                break
            if attempt == "first_open":
                if shot:
                    await page.screenshot(path=os.path.join(SHOTS, shot))
                times = []
                for _ in range(turns):
                    before = await page.evaluate("document.getElementById('pos-label').textContent")
                    t = time.perf_counter()
                    await page.keyboard.press("ArrowRight")
                    try:
                        await page.wait_for_function(f"(prev) => document.getElementById('pos-label').textContent !== prev && ({READY_JS})()", arg=before, timeout=60000, polling=20)
                        times.append(round((time.perf_counter() - t) * 1000))
                    except Exception:
                        times.append(None)
                result["page_turn_ms"] = times
                await page.wait_for_timeout(2000)  # let progress save
    finally:
        result["errors"] = sorted(set(e for e in errors if "/Progress/" not in e or "40" in e))[:10]
        await ctx.close()
    return result


async def browser_tests(token, books):
    from playwright.async_api import async_playwright
    async with async_playwright() as p:
        browser = await p.chromium.launch()
        for net in ("home_network", "phone_away_from_home"):
            throttle(net != "home_network")
            try:
                for name, item in books.items():
                    shot = f"phone-{os.path.splitext(name)[0].replace(' ', '-').lower()}.png" if net == "home_network" else None
                    r = await open_book(browser, token, item, shot=shot)
                    RESULTS["browser"].setdefault(name, {})[net] = r
                    print(net, name, json.dumps(r))
            finally:
                throttle(False)
        await feature_tests(browser, token, books)
        await browser.close()


async def ready_page(browser, token, item, viewport, dpr=2):
    ctx = await browser.new_context(viewport=viewport, device_scale_factor=dpr)
    page = await ctx.new_page()
    errors = []
    page.on("pageerror", lambda e: errors.append(f"js: {e}"))
    await page.goto(f"{BASE}/BookReader/Reader?id={item}#t={token}")
    await page.wait_for_function(READY_JS, timeout=120000, polling=50)
    await page.wait_for_timeout(500)
    return ctx, page, errors


async def feature_tests(browser, token, books):
    """Bookmarks survive a reopen; comic panels are found; desktop layouts render."""
    f = RESULTS["features"]
    phone = {"width": 412, "height": 915}
    epub = next(i for n, i in books.items() if n.endswith(".epub"))
    panels = next(i for n, i in books.items() if n.startswith("Panel"))

    # Bookmark: add one, close, reopen, and it should still be marked.
    ctx, page, errors = await ready_page(browser, token, epub, phone)
    await page.keyboard.press("ArrowRight")
    await page.wait_for_timeout(800)
    await page.click("#btn-bookmark")
    await page.wait_for_function("document.getElementById('btn-bookmark').getAttribute('aria-pressed') === 'true'", timeout=10000)
    await page.wait_for_timeout(2500)  # let the reading position save too
    where_before = await page.inner_text("#pos-label")
    await page.screenshot(path=os.path.join(SHOTS, "phone-bookmarked.png"))
    await page.click("#btn-toc")
    await page.wait_for_timeout(600)
    await page.screenshot(path=os.path.join(SHOTS, "phone-contents.png"))
    await ctx.close()
    ctx, page, errors2 = await ready_page(browser, token, epub, phone)
    await page.wait_for_timeout(1500)
    f["resumed_at_same_place"] = (await page.inner_text("#pos-label")) == where_before
    f["position_before_after"] = [where_before, await page.inner_text("#pos-label")]
    f["bookmark_kept_after_reopen"] = await page.get_attribute("#btn-bookmark", "aria-pressed") == "true"
    f["bookmark_list_entries"] = await page.evaluate("document.querySelectorAll('#bookmark-list li').length")
    f["epub_errors"] = errors + errors2
    await ctx.close()

    # Panels: page 1 has 4 panels, page 2 has 3.
    ctx, page, errors = await ready_page(browser, token, panels, phone)
    await page.click("#btn-panels")
    await page.wait_for_function("document.getElementById('app').dataset.panels !== undefined", timeout=15000)
    await page.wait_for_timeout(600)
    f["panels_found_page1"] = int(await page.get_attribute("#app", "data-panels"))
    await page.screenshot(path=os.path.join(SHOTS, "phone-panel-view.png"))
    for _ in range(f["panels_found_page1"]):
        await page.keyboard.press("ArrowRight")
        await page.wait_for_timeout(500)
    f["panels_found_page2"] = int(await page.get_attribute("#app", "data-panels"))
    f["label_after_stepping"] = await page.inner_text("#pos-label")
    await page.click("#btn-panels")
    await page.click("#btn-toc")
    await page.wait_for_timeout(2500)
    await page.screenshot(path=os.path.join(SHOTS, "phone-pages-grid.png"))
    f["panel_errors"] = errors
    await ctx.close()

    # Desktop: two-page book spread and comic spread.
    for name, item in books.items():
        if name.endswith(".epub") or name.endswith(".cbz") and not name.startswith("Panel"):
            ctx, page, errors = await ready_page(browser, token, item, {"width": 1440, "height": 900}, dpr=1)
            await page.keyboard.press("ArrowRight")
            await page.wait_for_timeout(1500)
            await page.screenshot(path=os.path.join(SHOTS, f"desktop-{os.path.splitext(name)[1][1:]}.png"))
            f[f"desktop_{name}_errors"] = errors
            await ctx.close()
    print("features", json.dumps(f))


def summary():
    lines = [f"## Book Reader speed test (Jellyfin {RESULTS.get('jellyfin')})", ""]
    lines += ["### In the browser (412×915 phone screen)", "",
              "| Book | Network | First open | Downloaded | Reopen | Page turns (ms) | Problems |", "|---|---|---|---|---|---|---|"]
    for name, nets in RESULTS["browser"].items():
        for net, r in nets.items():
            fmt = lambda v: "**stuck**" if v is None else f"{v / 1000:.1f}s"
            turns = ", ".join("—" if x is None else str(x) for x in r.get("page_turn_ms", []))
            probs = "; ".join(r.get("errors", [])) + (f" stuck on: {r['stuck_on']}" if r.get("stuck_on") else "")
            lines.append(f"| {name} | {net.replace('_', ' ')} | {fmt(r.get('first_open_ms'))} | {r.get('first_open_mb', '—')} MB | {fmt(r.get('reopen_ms')) if 'reopen_ms' in r else '—'} | {turns} | {probs or 'none'} |")
    lines += ["", "### Features", "", "```json", json.dumps(RESULTS["features"], indent=1), "```"]
    lines += ["", "### Server", "", "```json", json.dumps(RESULTS["http"], indent=1), "```"]
    return "\n".join(lines)


def main():
    token, books = setup()
    http_tests(token, books)
    asyncio.run(browser_tests(token, books))
    with open("results.json", "w") as f:
        json.dump(RESULTS, f, indent=1)
    md = summary()
    with open("results.md", "w") as f:
        f.write(md)
    print("PERF_RESULTS " + json.dumps(RESULTS))
    print(md)


if __name__ == "__main__":
    main()
