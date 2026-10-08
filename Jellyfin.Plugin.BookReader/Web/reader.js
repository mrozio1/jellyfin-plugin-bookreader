/* Book Reader for Jellyfin — reader page
 * Opens EPUB (epub.js), PDF (pdf.js, streamed by byte range) and comics
 * (one page at a time) inside Jellyfin, with progress synced per user.
 */

// ------------------------------------------------------------------
// Setup: where the server is, which book, who is reading
// ------------------------------------------------------------------
const API = location.pathname.replace(/\/BookReader\/Reader\/?$/i, '');
const ASSETS = `${location.origin}${API}/BookReader/Assets/`;
const params = new URLSearchParams(location.search);
const itemId = params.get('id');
const token = readToken();
const AUTH = token ? `MediaBrowser Client="Book Reader", Token="${token}"` : '';

const $ = (id) => document.getElementById(id);
const app = $('app');
const viewer = $('viewer');

const state = {
  info: null,
  config: null,
  settings: {},
  userOverrides: {},
  engine: null,
  position: { location: '', percent: 0 },
  saveTimer: 0,
  chromeTimer: 0,
};

function readToken() {
  const hash = new URLSearchParams(location.hash.slice(1));
  const fromHash = hash.get('t');
  if (fromHash) {
    try { sessionStorage.setItem('bookreader-token', fromHash); } catch { /* private mode */ }
    history.replaceState(null, '', location.pathname + location.search);
    return fromHash;
  }
  try {
    const saved = sessionStorage.getItem('bookreader-token');
    if (saved) return saved;
  } catch { /* ignore */ }
  // Fall back to the Jellyfin web client's own sign-in on this origin.
  try {
    const creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
    const servers = (creds.Servers || []).filter((s) => s.AccessToken);
    const here = servers.find((s) => [s.ManualAddress, s.LocalAddress, s.RemoteAddress].some((a) => a && a.startsWith(location.origin)));
    return (here || servers[0] || {}).AccessToken || '';
  } catch {
    return '';
  }
}

async function api(path, options = {}) {
  const res = await fetch(`${API}${path}`, {
    ...options,
    headers: { Authorization: AUTH, ...(options.body ? { 'Content-Type': 'application/json' } : {}), ...(options.headers || {}) },
  });
  if (res.status === 401) throw new ReaderError('Your session has expired. Sign in to Jellyfin again, then reopen the book.');
  return res;
}

class ReaderError extends Error {}

// ------------------------------------------------------------------
// Boot
// ------------------------------------------------------------------
boot().catch((err) => {
  console.error(err);
  showError(err instanceof ReaderError ? err.message : 'This book could not be opened. Check the server log for details.');
});

async function boot() {
  if (!itemId) throw new ReaderError('No book was chosen. Open a book from your Jellyfin library.');
  if (!token) throw new ReaderError('You are not signed in. Sign in to Jellyfin, then open the book again.');

  const [cfgRes, infoRes, progRes] = await Promise.all([
    api('/BookReader/Settings'),
    api(`/BookReader/Books/${itemId}/Info`),
    api(`/BookReader/Progress/${itemId}`),
  ]);
  if (infoRes.status === 404) throw new ReaderError('This book is not in your library, or the file has been moved.');
  state.config = await cfgRes.json();
  let info = await infoRes.json();
  const saved = progRes.status === 200 ? await progRes.json() : null;

  setAccent(state.config.accentColor);
  state.userOverrides = clean(state.config.user || {});
  state.settings = { ...state.config.defaults, ...(state.config.allowUserOverrides ? state.userOverrides : {}) };
  applyChromeTheme();
  wireUi();

  $('book-title').textContent = info.title || 'Untitled';
  document.title = `${info.title} — Book Reader`;

  // Formats that need converting: wait for the server to prepare them.
  if (info.kind === 'needsconversion') info = await waitForConversion();
  if (info.kind === 'unsupported') {
    throw new ReaderError(info.conversion?.error || `The .${info.format} format can't be opened in the reader.`);
  }

  state.info = info;
  app.classList.add(`mode-${info.kind}`);
  if (!state.config.allowUserOverrides) {
    $('locked-note').hidden = false;
    $('settings').classList.add('locked');
  }

  const engines = { epub: EpubEngine, pdf: PdfEngine, comic: ComicEngine };
  state.engine = new engines[info.kind]();
  await state.engine.open(saved);
  syncSettingsUi();

  app.classList.remove('is-loading');
  scheduleChromeHide(2500);
}

async function waitForConversion() {
  setLoading('Preparing this book for reading. The first open of this format can take a minute.');
  for (let i = 0; i < 400; i++) {
    await sleep(i < 5 ? 1000 : 2500);
    const res = await api(`/BookReader/Books/${itemId}/Info?convert=false`);
    const info = await res.json();
    const s = info.conversion?.state;
    if (info.kind !== 'needsconversion' && info.kind !== 'unsupported') return info;
    if (s === 'ToolMissing' || s === 'Failed' || s === 'Disabled' || info.kind === 'unsupported') {
      throw new ReaderError(info.conversion?.error || 'This book could not be converted.');
    }
  }
  throw new ReaderError('Preparing this book is taking too long. Try again in a few minutes.');
}

// ------------------------------------------------------------------
// EPUB engine (epub.js)
// ------------------------------------------------------------------
class EpubEngine {
  async open(saved) {
    setLoading('Opening your book');
    await loadScript(`${ASSETS}lib/jszip.min.js`);
    await loadScript(`${ASSETS}lib/epub.min.js`);
    const data = await download(`/BookReader/Books/${itemId}/File`);
    this.book = window.ePub(data);
    await this.book.ready;

    const nav = await this.book.loaded.navigation;
    this.toc = flattenToc(nav.toc || []);
    buildToc(this.toc.map((t) => ({ label: t.label, level: t.level, go: () => this.rendition.display(t.href) })));

    this.cfi = saved?.location || undefined;
    await this.render();
    this.loadLocations();
  }

  async render() {
    if (this.rendition) {
      this.rendition.destroy();
      viewer.innerHTML = '';
    }
    const scrolled = state.settings.layout === 'scrolled';
    app.classList.toggle('mode-scrolled', scrolled);
    this.applyMargins();

    this.rendition = this.book.renderTo(viewer, {
      width: '100%',
      height: '100%',
      flow: scrolled ? 'scrolled' : 'paginated',
      manager: scrolled ? 'continuous' : 'default',
      spread: 'auto',
      minSpreadWidth: 960,
      allowScriptedContent: false,
    });

    this.rendition.hooks.content.register((contents) => this.decorate(contents));
    this.rendition.on('relocated', (loc) => this.onRelocated(loc));
    this.rendition.on('keyup', onKey);
    this.applyTypography();

    try {
      await this.rendition.display(this.cfi);
    } catch {
      await this.rendition.display();
    }
  }

  decorate(contents) {
    const doc = contents.document;
    const style = doc.createElement('style');
    style.id = 'bookreader-style';
    doc.head.appendChild(style);
    this.writeStyle(style);

    // Swipe and tap inside the page.
    let sx = 0, sy = 0, st = 0;
    doc.addEventListener('touchstart', (e) => {
      const t = e.changedTouches[0]; sx = t.screenX; sy = t.screenY; st = Date.now();
    }, { passive: true });
    doc.addEventListener('touchend', (e) => {
      if (state.settings.layout === 'scrolled') return;
      const t = e.changedTouches[0];
      const dx = t.screenX - sx, dy = t.screenY - sy;
      if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(dy) * 1.5 && Date.now() - st < 700) {
        dx < 0 ? this.next() : this.prev();
      }
    }, { passive: true });
    doc.addEventListener('click', (e) => {
      if (e.target.closest && e.target.closest('a')) return;
      if (doc.getSelection && String(doc.getSelection()).length) return;
      toggleChrome();
    });
  }

  writeStyle(style) {
    const s = state.settings;
    const families = {
      serif: '"Literata", "Iowan Old Style", Charter, Georgia, serif',
      sans: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',
      dyslexic: '"Atkinson Hyperlegible", Verdana, sans-serif',
    };
    const font = families[s.font];
    const ink = cssVar('--ink');
    const accent = cssVar('--accent');
    style.textContent = `
      @font-face { font-family: "Literata"; src: url("${ASSETS}fonts/literata-400-normal.woff2") format("woff2"); font-weight: 400; font-style: normal; }
      @font-face { font-family: "Literata"; src: url("${ASSETS}fonts/literata-400-italic.woff2") format("woff2"); font-weight: 400; font-style: italic; }
      @font-face { font-family: "Literata"; src: url("${ASSETS}fonts/literata-700-normal.woff2") format("woff2"); font-weight: 700; font-style: normal; }
      @font-face { font-family: "Literata"; src: url("${ASSETS}fonts/literata-700-italic.woff2") format("woff2"); font-weight: 700; font-style: italic; }
      @font-face { font-family: "Atkinson Hyperlegible"; src: url("${ASSETS}fonts/atkinson-400-normal.woff2") format("woff2"); font-weight: 400; }
      @font-face { font-family: "Atkinson Hyperlegible"; src: url("${ASSETS}fonts/atkinson-700-normal.woff2") format("woff2"); font-weight: 700; }
      html { color-scheme: ${['dark', 'black'].includes(s.theme) ? 'dark' : 'light'}; }
      html, body { background: transparent !important; color: ${ink} !important; }
      body { -webkit-font-smoothing: antialiased; text-rendering: optimizeLegibility; }
      p, li, blockquote, dd, dt, div, span, td { line-height: ${s.lineHeight} !important; }
      ${font ? `body, p, li, blockquote, div, span, td, h1, h2, h3, h4, h5, h6, a { font-family: ${font} !important; }` : ''}
      p, li, h1, h2, h3, h4, h5, h6, blockquote, span, div { color: inherit !important; }
      a, a:visited { color: ${accent} !important; }
      img, svg, video { max-width: 100% !important; height: auto; }
      ::selection { background: ${accent}55; }
    `;
  }

  applyTypography() {
    if (!this.rendition) return;
    this.rendition.themes.fontSize(`${state.settings.fontSize}%`);
    for (const c of this.rendition.getContents()) {
      const style = c.document.getElementById('bookreader-style');
      if (style) this.writeStyle(style);
    }
  }

  applyMargins() {
    const pct = { narrow: 0.03, normal: 0.07, wide: 0.13 }[state.settings.margins] ?? 0.07;
    const w = window.innerWidth;
    let side = Math.round(w * pct);
    // Keep comfortable line lengths in scroll mode on wide screens.
    if (state.settings.layout === 'scrolled') side = Math.max(side, Math.round((w - 760) / 2));
    viewer.style.left = `${side}px`;
    viewer.style.right = `${side}px`;
  }

  onRelocated(loc) {
    if (!loc?.start) return;
    this.cfi = loc.start.cfi;
    const href = (loc.start.href || '').split('#')[0];
    const chapter = [...this.toc].reverse().find((t) => t.href.split('#')[0].endsWith(href) || href.endsWith(t.href.split('#')[0]));
    $('chapter-title').textContent = chapter?.label || '';
    markTocCurrent(chapter ? this.toc.indexOf(chapter) : -1);

    const d = loc.start.displayed;
    const label = d && state.settings.layout !== 'scrolled' ? `${d.page} of ${d.total} in this chapter` : '';
    let fraction = loc.start.percentage;
    if (this.locationsReady) fraction = this.book.locations.percentageFromCfi(this.cfi);
    updatePosition(fraction ?? 0, label, this.cfi, !this.locationsReady);
  }

  async loadLocations() {
    const key = `bookreader-locations-${itemId}`;
    let json = null;
    try { json = localStorage.getItem(key); } catch { /* ignore */ }
    if (json) {
      this.book.locations.load(json);
    } else {
      await sleep(400);
      await this.book.locations.generate(1200);
      try { localStorage.setItem(key, this.book.locations.save()); } catch { /* storage full */ }
    }
    this.locationsReady = true;
    this.locationList = JSON.parse(this.book.locations.save());

    // Chapter marks on the scrubber.
    const marks = [];
    for (const t of this.toc.filter((x) => x.level === 0)) {
      try {
        const section = this.book.spine.get(t.href.split('#')[0]);
        if (!section) continue;
        const prefix = `epubcfi(${section.cfiBase}!`;
        const idx = this.locationList.findIndex((c) => c.startsWith(prefix));
        if (idx > 0) marks.push(idx / this.locationList.length);
      } catch { /* skip */ }
    }
    setTicks(marks);
    if (this.cfi) updatePosition(this.book.locations.percentageFromCfi(this.cfi), $('pos-label').textContent, this.cfi);
  }

  next() { this.rendition?.next(); }
  prev() { this.rendition?.prev(); }
  first() { this.rendition?.display(this.book.spine.first()?.href); }
  last() { this.rendition?.display(this.book.spine.last()?.href); }

  seek(fraction) {
    if (!this.locationsReady) { toast('Still measuring the book. Try again in a moment.'); return; }
    this.rendition.display(this.book.locations.cfiFromPercentage(fraction));
  }

  previewLabel(fraction) {
    return `${Math.round(fraction * 100)}%`;
  }

  async settingsChanged(key) {
    if (key === 'layout') {
      await this.render();
    } else if (key === 'margins') {
      this.applyMargins();
      this.rendition.resize();
    } else {
      this.applyTypography();
    }
  }

  resized() {
    this.applyMargins();
  }
}

function flattenToc(items, level = 0, out = []) {
  for (const it of items) {
    out.push({ label: (it.label || '').trim(), href: it.href, level: Math.min(level, 2) });
    if (it.subitems?.length) flattenToc(it.subitems, level + 1, out);
  }
  return out;
}

// ------------------------------------------------------------------
// PDF engine (pdf.js, range-streamed: only the pages you view are fetched)
// ------------------------------------------------------------------
class PdfEngine {
  async open(saved) {
    setLoading('Opening your book');
    const pdfjs = await import(`${ASSETS}lib/pdf.min.mjs`);
    pdfjs.GlobalWorkerOptions.workerSrc = `${ASSETS}lib/pdf.worker.min.mjs`;
    this.doc = await pdfjs.getDocument({
      url: `${location.origin}${API}/BookReader/Books/${itemId}/File`,
      httpHeaders: { Authorization: AUTH },
      rangeChunkSize: 256 * 1024,
      disableAutoFetch: true,
      isEvalSupported: false,
    }).promise;

    this.count = this.doc.numPages;
    this.page = clamp(parseInt(saved?.location, 10) || 1, 1, this.count);
    const first = await this.doc.getPage(1);
    const vp = first.getViewport({ scale: 1 });
    this.aspect = vp.height / vp.width;
    this.cache = new Map();

    this.loadOutline();
    this.render();
  }

  async loadOutline() {
    const outline = await this.doc.getOutline().catch(() => null);
    if (!outline?.length) { buildToc([]); return; }
    const flat = [];
    const walk = (items, level) => {
      for (const it of items) {
        flat.push({ label: it.title, dest: it.dest, level: Math.min(level, 2) });
        if (it.items?.length) walk(it.items, level + 1);
      }
    };
    walk(outline, 0);
    const pages = await Promise.all(flat.map((f) => this.destToPage(f.dest)));
    this.outline = flat.map((f, i) => ({ ...f, page: pages[i] })).filter((f) => f.page);
    buildToc(this.outline.map((f) => ({ label: f.label, level: f.level, go: () => this.goTo(f.page) })));
    setTicks(this.outline.filter((f) => f.level === 0 && f.page > 1).map((f) => (f.page - 1) / Math.max(1, this.count - 1)));
    this.report();
  }

  async destToPage(dest) {
    try {
      const d = typeof dest === 'string' ? await this.doc.getDestination(dest) : dest;
      if (!d) return null;
      return (await this.doc.getPageIndex(d[0])) + 1;
    } catch {
      return null;
    }
  }

  render() {
    viewer.innerHTML = '';
    this.observer?.disconnect();
    const scrolled = state.settings.layout === 'scrolled';
    app.classList.toggle('mode-scrolled', scrolled);
    scrolled ? this.renderScrolled() : this.renderPaged();
  }

  // --- One page at a time ---
  renderPaged() {
    this.wrap = document.createElement('div');
    this.wrap.className = 'page-single';
    viewer.appendChild(this.wrap);
    this.showPage(this.page);
  }

  async showPage(n) {
    this.page = n;
    this.report();
    const box = viewer.getBoundingClientRect();
    const slot = document.createElement('div');
    slot.className = 'page-slot placeholder';
    const cssWidth = Math.min(box.width, (box.height - 16) / this.aspect);
    slot.style.width = `${Math.floor(cssWidth)}px`;
    slot.style.height = `${Math.floor(cssWidth * this.aspect)}px`;
    this.wrap.replaceChildren(slot);
    const canvas = await this.renderToCanvas(n, box.width, box.height - 16);
    if (this.page !== n || !canvas) return;
    slot.classList.remove('placeholder');
    slot.style.width = canvas.style.width;
    slot.style.height = canvas.style.height;
    slot.replaceChildren(canvas);
    // Warm up the next page so turning feels instant.
    if (n < this.count) this.renderToCanvas(n + 1, box.width, box.height - 16);
  }

  async renderToCanvas(n, maxW, maxH) {
    const key = `${n}:${Math.round(maxW)}x${Math.round(maxH)}`;
    if (this.cache.has(key)) {
      const c = this.cache.get(key);
      this.cache.delete(key); this.cache.set(key, c);
      return cloneCanvas(c);
    }
    const page = await this.doc.getPage(n);
    const vp1 = page.getViewport({ scale: 1 });
    const fit = maxH ? Math.min(maxW / vp1.width, maxH / vp1.height) : maxW / vp1.width;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    let scale = fit * dpr;
    // Stay under mobile canvas limits (~16 megapixels on iOS).
    const maxPixels = 12e6;
    if (vp1.width * vp1.height * scale * scale > maxPixels) scale = Math.sqrt(maxPixels / (vp1.width * vp1.height));
    const vp = page.getViewport({ scale });
    const canvas = document.createElement('canvas');
    canvas.width = Math.floor(vp.width);
    canvas.height = Math.floor(vp.height);
    canvas.style.width = `${Math.floor(vp1.width * fit)}px`;
    canvas.style.height = `${Math.floor(vp1.height * fit)}px`;
    const ctx = canvas.getContext('2d', { alpha: false });
    ctx.fillStyle = '#fff';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    try {
      await page.render({ canvasContext: ctx, viewport: vp }).promise;
    } catch {
      return null;
    }
    page.cleanup();
    this.cache.set(key, canvas);
    while (this.cache.size > 4) this.cache.delete(this.cache.keys().next().value);
    return cloneCanvas(canvas);
  }

  // --- Continuous scroll, pages render as they come into view ---
  renderScrolled() {
    const scroller = document.createElement('div');
    scroller.className = 'page-scroller';
    viewer.appendChild(scroller);
    const width = Math.min(viewer.clientWidth - 24, 980);
    this.slots = [];
    for (let i = 1; i <= this.count; i++) {
      const slot = document.createElement('div');
      slot.className = 'page-slot placeholder';
      slot.style.width = `${width}px`;
      slot.style.height = `${Math.round(width * this.aspect)}px`;
      slot.dataset.page = i;
      scroller.appendChild(slot);
      this.slots.push(slot);
    }

    this.observer = new IntersectionObserver((entries) => {
      for (const e of entries) {
        const slot = e.target;
        const n = +slot.dataset.page;
        if (e.isIntersecting && !slot.dataset.rendered) {
          slot.dataset.rendered = '1';
          this.renderToCanvas(n, width, 0).then((c) => {
            if (!c || !slot.dataset.rendered) return;
            slot.classList.remove('placeholder');
            slot.replaceChildren(c);
          });
        } else if (!e.isIntersecting && slot.dataset.rendered) {
          // Free memory for pages far off screen.
          delete slot.dataset.rendered;
          slot.classList.add('placeholder');
          slot.replaceChildren();
        }
      }
    }, { root: scroller, rootMargin: '150% 0px' });
    this.slots.forEach((s) => this.observer.observe(s));

    let ticking = false;
    scroller.addEventListener('scroll', () => {
      if (ticking) return;
      ticking = true;
      requestAnimationFrame(() => {
        ticking = false;
        const mid = scroller.scrollTop + scroller.clientHeight / 3;
        const idx = this.slots.findIndex((s) => s.offsetTop + s.offsetHeight > mid);
        if (idx >= 0 && idx + 1 !== this.page) { this.page = idx + 1; this.report(); }
      });
    }, { passive: true });
    this.scroller = scroller;
    requestAnimationFrame(() => { scroller.scrollTop = this.slots[this.page - 1].offsetTop - 12; });
  }

  goTo(n) {
    n = clamp(n, 1, this.count);
    if (state.settings.layout === 'scrolled') {
      this.page = n;
      this.scroller.scrollTop = this.slots[n - 1].offsetTop - 12;
      this.report();
    } else {
      this.showPage(n);
    }
  }

  report() {
    const chapter = this.outline ? [...this.outline].reverse().find((o) => o.page <= this.page) : null;
    $('chapter-title').textContent = chapter?.label || '';
    if (this.outline) markTocCurrent(chapter ? this.outline.indexOf(chapter) : -1);
    updatePosition((this.page - 1) / Math.max(1, this.count - 1), `Page ${this.page} of ${this.count}`, String(this.page));
  }

  next() { if (this.page < this.count) this.goTo(this.page + 1); }
  prev() { if (this.page > 1) this.goTo(this.page - 1); }
  first() { this.goTo(1); }
  last() { this.goTo(this.count); }
  seek(f) { this.goTo(Math.round(f * (this.count - 1)) + 1); }
  previewLabel(f) { return `Page ${Math.round(f * (this.count - 1)) + 1} of ${this.count}`; }
  settingsChanged(key) { if (key === 'layout') { this.cache.clear(); this.render(); } }
  resized() { this.cache.clear(); this.render(); }
}

function cloneCanvas(src) {
  const c = document.createElement('canvas');
  c.width = src.width; c.height = src.height;
  c.style.width = src.style.width; c.style.height = src.style.height;
  c.getContext('2d').drawImage(src, 0, 0);
  return c;
}

// ------------------------------------------------------------------
// Comic engine: pages are fetched one by one, with a few loaded ahead
// ------------------------------------------------------------------
class ComicEngine {
  async open(saved) {
    this.count = state.info.pageCount || 0;
    if (!this.count) throw new ReaderError('This comic has no pages the reader can show.');
    this.page = clamp(parseInt(saved?.location, 10) || 0, 0, this.count - 1);
    this.blobs = new Map(); // page index -> Promise<objectURL>
    buildToc([]);
    this.render();
  }

  get direction() { return state.settings.comicDirection || 'ltr'; }

  fetchPage(i) {
    if (this.blobs.has(i)) {
      const p = this.blobs.get(i);
      this.blobs.delete(i); this.blobs.set(i, p);
      return p;
    }
    const p = api(`/BookReader/Books/${itemId}/Pages/${i}`)
      .then((r) => { if (!r.ok) throw new Error(`page ${i}`); return r.blob(); })
      .then((b) => URL.createObjectURL(b));
    p.catch(() => this.blobs.delete(i));
    this.blobs.set(i, p);
    const keep = (state.config.prefetchPages || 3) * 2 + 8;
    while (this.blobs.size > keep) {
      const [oldest, op] = this.blobs.entries().next().value;
      this.blobs.delete(oldest);
      op.then((u) => setTimeout(() => URL.revokeObjectURL(u), 2000)).catch(() => {});
    }
    return p;
  }

  render() {
    viewer.innerHTML = '';
    this.observer?.disconnect();
    const vertical = this.direction === 'vertical';
    app.classList.toggle('mode-vertical', vertical);
    vertical ? this.renderVertical() : this.renderPaged();
  }

  spreadOn() {
    return state.settings.comicTwoPageSpread && window.innerWidth >= 800 && window.innerWidth > window.innerHeight * 1.1;
  }

  // Cover alone, then pairs: [0] [1,2] [3,4] ...
  groupFor(i) {
    if (!this.spreadOn() || i === 0) return [i];
    const start = i % 2 === 1 ? i : i - 1;
    return start + 1 < this.count ? [start, start + 1] : [start];
  }

  renderPaged() {
    this.wrap = document.createElement('div');
    this.wrap.className = `page-single${this.direction === 'rtl' ? ' rtl' : ''}`;
    viewer.appendChild(this.wrap);
    this.show(this.page);
  }

  async show(i) {
    const group = this.groupFor(clamp(i, 0, this.count - 1));
    this.page = group[0];
    this.report();
    const box = viewer.getBoundingClientRect();
    const w = Math.floor(box.width / group.length);
    const slots = group.map(() => {
      const s = document.createElement('div');
      s.className = 'page-slot placeholder';
      s.style.width = `${w}px`;
      s.style.height = `${Math.floor(box.height)}px`;
      return s;
    });
    this.wrap.replaceChildren(...slots);
    await Promise.all(group.map(async (p, k) => {
      try {
        const url = await this.fetchPage(p);
        if (!group.includes(this.page) && this.page !== group[0]) return;
        const img = new Image();
        img.decoding = 'async';
        img.alt = `Page ${p + 1}`;
        img.src = url;
        await img.decode().catch(() => {});
        slots[k].classList.remove('placeholder');
        if (group.length === 2) img.style.objectPosition = k === 0 ? (this.direction === 'rtl' ? 'left' : 'right') : (this.direction === 'rtl' ? 'right' : 'left');
        slots[k].replaceChildren(img);
      } catch {
        slots[k].classList.remove('placeholder');
        slots[k].textContent = 'Page could not be loaded';
      }
    }));
    this.prefetch(group[group.length - 1] + 1);
  }

  prefetch(from) {
    const n = state.config.prefetchPages || 3;
    for (let k = 0; k < n; k++) if (from + k < this.count) this.fetchPage(from + k);
    if (this.page > 0) this.fetchPage(this.page - 1);
  }

  renderVertical() {
    const scroller = document.createElement('div');
    scroller.className = 'page-scroller';
    viewer.appendChild(scroller);
    const width = Math.min(viewer.clientWidth, 900);
    this.slots = [];
    for (let i = 0; i < this.count; i++) {
      const slot = document.createElement('div');
      slot.className = 'page-slot placeholder';
      slot.style.width = `${width}px`;
      slot.style.height = `${Math.round(width * 1.45)}px`;
      slot.dataset.page = i;
      scroller.appendChild(slot);
      this.slots.push(slot);
    }
    this.observer = new IntersectionObserver((entries) => {
      for (const e of entries) {
        const slot = e.target;
        const i = +slot.dataset.page;
        if (e.isIntersecting && !slot.dataset.loaded) {
          slot.dataset.loaded = '1';
          this.fetchPage(i).then((url) => {
            const img = new Image();
            img.alt = `Page ${i + 1}`;
            img.onload = () => {
              slot.style.height = `${Math.round(width * (img.naturalHeight / img.naturalWidth))}px`;
              slot.classList.remove('placeholder');
            };
            img.src = url;
            slot.replaceChildren(img);
          }).catch(() => { delete slot.dataset.loaded; });
        }
      }
    }, { root: scroller, rootMargin: '200% 0px' });
    this.slots.forEach((s) => this.observer.observe(s));

    let ticking = false;
    scroller.addEventListener('scroll', () => {
      if (ticking) return;
      ticking = true;
      requestAnimationFrame(() => {
        ticking = false;
        const mid = scroller.scrollTop + scroller.clientHeight / 3;
        const idx = this.slots.findIndex((s) => s.offsetTop + s.offsetHeight > mid);
        if (idx >= 0 && idx !== this.page) { this.page = idx; this.report(); }
      });
    }, { passive: true });
    this.scroller = scroller;
    requestAnimationFrame(() => { scroller.scrollTop = this.slots[this.page].offsetTop; });
  }

  goTo(i) {
    i = clamp(i, 0, this.count - 1);
    if (this.direction === 'vertical') {
      this.page = i;
      this.scroller.scrollTop = this.slots[i].offsetTop;
      this.report();
    } else {
      this.show(i);
    }
  }

  report() {
    const shown = this.direction === 'vertical' ? [this.page] : this.groupFor(this.page);
    const label = shown.length === 2 ? `Pages ${shown[0] + 1}–${shown[1] + 1} of ${this.count}` : `Page ${this.page + 1} of ${this.count}`;
    updatePosition(this.page / Math.max(1, this.count - 1), label, String(this.page));
  }

  next() {
    const g = this.groupFor(this.page);
    if (g[g.length - 1] < this.count - 1) this.goTo(g[g.length - 1] + 1);
  }

  prev() {
    if (this.page > 0) this.goTo(this.groupFor(this.page - 1)[0]);
  }

  first() { this.goTo(0); }
  last() { this.goTo(this.count - 1); }
  seek(f) { this.goTo(Math.round(f * (this.count - 1))); }
  previewLabel(f) { return `Page ${Math.round(f * (this.count - 1)) + 1} of ${this.count}`; }
  settingsChanged(key) { if (key === 'comicDirection' || key === 'comicTwoPageSpread') this.render(); }
  resized() { this.render(); }
  get rtl() { return this.direction === 'rtl'; }
}

// ------------------------------------------------------------------
// Position, progress saving
// ------------------------------------------------------------------
function updatePosition(fraction, label, location, approximate = false) {
  fraction = clamp(fraction || 0, 0, 1);
  if (!dragging) {
    $('seek').value = Math.round(fraction * 1000);
    setRibbon(fraction);
  }
  $('pos-label').textContent = label;
  $('pct-label').textContent = approximate ? '' : `${Math.round(fraction * 100)}% read`;
  state.position = { location, percent: Math.round(fraction * 1000) / 10 };
  clearTimeout(state.saveTimer);
  state.saveTimer = setTimeout(saveProgress, 1500);
}

function setRibbon(fraction) {
  $('fill').style.width = `${fraction * 100}%`;
  $('ribbon').style.left = `${fraction * 100}%`;
}

function setTicks(fractions) {
  $('ticks').replaceChildren(...fractions.map((f) => {
    const i = document.createElement('i');
    i.style.left = `${f * 100}%`;
    return i;
  }));
}

function saveProgress(keepalive = false) {
  clearTimeout(state.saveTimer);
  if (!state.position.location) return;
  api(`/BookReader/Progress/${itemId}`, {
    method: 'POST',
    keepalive,
    body: JSON.stringify({ location: state.position.location, percent: state.position.percent }),
  }).catch(() => { /* offline: next save will retry */ });
}

// ------------------------------------------------------------------
// Interface wiring
// ------------------------------------------------------------------
let dragging = false;

function wireUi() {
  $('btn-close').addEventListener('click', closeBook);
  $('veil-action').addEventListener('click', closeBook);
  $('btn-toc').addEventListener('click', () => openPanel('toc'));
  $('btn-settings').addEventListener('click', () => openPanel('settings'));
  $('scrim').addEventListener('click', closePanels);
  document.querySelectorAll('[data-close]').forEach((b) => b.addEventListener('click', closePanels));

  const fsBtn = $('btn-fullscreen');
  if (!document.fullscreenEnabled) fsBtn.hidden = true;
  fsBtn.addEventListener('click', toggleFullscreen);

  $('zone-prev').addEventListener('click', () => turn(-1));
  $('zone-next').addEventListener('click', () => turn(1));

  // Tap the page centre (PDF / comics) to show or hide the bars.
  const stage = $('stage');
  stage.addEventListener('click', (e) => {
    if (e.target.closest('.turn-zone')) return;
    if (state.info?.kind !== 'epub') toggleChrome();
  });

  // Swipe on PDF / comic pages.
  let sx = 0, sy = 0, st = 0;
  stage.addEventListener('touchstart', (e) => { const t = e.changedTouches[0]; sx = t.clientX; sy = t.clientY; st = Date.now(); }, { passive: true });
  stage.addEventListener('touchend', (e) => {
    if (state.info?.kind === 'epub' || app.classList.contains('mode-scrolled') || app.classList.contains('mode-vertical')) return;
    if (e.touches.length) return;
    const t = e.changedTouches[0];
    const dx = t.clientX - sx, dy = t.clientY - sy;
    if (window.visualViewport && window.visualViewport.scale > 1.05) return; // user is zoomed in
    if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(dy) * 1.5 && Date.now() - st < 700) turn(dx < 0 ? 1 : -1);
  }, { passive: true });

  // Scrubber
  const seek = $('seek');
  seek.addEventListener('input', () => {
    dragging = true;
    $('scrubber').classList.add('dragging');
    const f = seek.value / 1000;
    setRibbon(f);
    if (state.engine) $('pos-label').textContent = state.engine.previewLabel(f);
  });
  seek.addEventListener('change', () => {
    dragging = false;
    $('scrubber').classList.remove('dragging');
    state.engine?.seek(seek.value / 1000);
  });

  document.addEventListener('keydown', onKey);
  document.addEventListener('mousemove', (e) => {
    if (e.clientY < 70 || e.clientY > window.innerHeight - 90) showChrome();
  });

  // Settings controls
  document.querySelectorAll('[data-setting]').forEach((group) => {
    group.addEventListener('click', (e) => {
      const btn = e.target.closest('[data-value]');
      if (!btn) return;
      let v = btn.dataset.value;
      if (group.dataset.setting === 'lineHeight') v = parseFloat(v);
      changeSetting(group.dataset.setting, v);
    });
  });
  $('size-down').addEventListener('click', () => changeSetting('fontSize', clamp(state.settings.fontSize - 10, 70, 220)));
  $('size-up').addEventListener('click', () => changeSetting('fontSize', clamp(state.settings.fontSize + 10, 70, 220)));
  $('spread-toggle').addEventListener('change', (e) => changeSetting('comicTwoPageSpread', e.target.checked));

  let resizeTimer = 0;
  window.addEventListener('resize', () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => state.engine?.resized(), 250);
  });

  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') saveProgress(true); });
  window.addEventListener('pagehide', () => saveProgress(true));
}

function onKey(e) {
  if (e.target?.tagName === 'INPUT' && e.target.type !== 'range') return;
  const rtl = state.engine?.rtl;
  const panelOpen = !$('toc').hidden || !$('settings').hidden;
  switch (e.key) {
    case 'ArrowRight': if (!panelOpen && e.target?.id !== 'seek') { turn(1); e.preventDefault?.(); } break;
    case 'ArrowLeft': if (!panelOpen && e.target?.id !== 'seek') { turn(-1); e.preventDefault?.(); } break;
    case 'PageDown': case ' ': if (!panelOpen && !app.classList.contains('mode-scrolled') && !app.classList.contains('mode-vertical')) { state.engine?.next(); e.preventDefault?.(); } break;
    case 'PageUp': if (!panelOpen) { state.engine?.prev(); e.preventDefault?.(); } break;
    case 'Home': state.engine?.first(); break;
    case 'End': state.engine?.last(); break;
    case 't': case 'T': openPanel('toc'); break;
    case 's': case 'S': openPanel('settings'); break;
    case 'f': case 'F': toggleFullscreen(); break;
    case 'Enter': if (!panelOpen && document.activeElement === document.body) toggleChrome(); break;
    case 'Escape': panelOpen ? closePanels() : closeBook(); break;
    default:
  }
}

/**
 * dir is the physical side: +1 = right (right tap zone, right arrow, swipe left),
 * -1 = left. In right-to-left comics the left side moves forward.
 */
function turn(dir) {
  if (!state.engine) return;
  const forward = state.engine.rtl ? dir < 0 : dir > 0;
  forward ? state.engine.next() : state.engine.prev();
}

function changeSetting(key, value) {
  if (!state.config.allowUserOverrides) return;
  state.settings[key] = value;
  if (value === state.config.defaults[key]) delete state.userOverrides[key];
  else state.userOverrides[key] = value;
  if (key === 'theme') applyChromeTheme();
  syncSettingsUi();
  if (key === 'theme' && state.engine instanceof EpubEngine) state.engine.applyTypography();
  state.engine?.settingsChanged(key);
  clearTimeout(changeSetting.timer);
  changeSetting.timer = setTimeout(() => {
    api('/BookReader/Settings', { method: 'POST', body: JSON.stringify(state.userOverrides) }).catch(() => {});
  }, 800);
}

function syncSettingsUi() {
  document.querySelectorAll('[data-setting]').forEach((group) => {
    const current = String(state.settings[group.dataset.setting]);
    group.querySelectorAll('[data-value]').forEach((b) => b.setAttribute('aria-checked', String(b.dataset.value === current)));
  });
  $('size-out').textContent = `${state.settings.fontSize}%`;
  $('spread-toggle').checked = !!state.settings.comicTwoPageSpread;
}

function applyChromeTheme() {
  const theme = ['light', 'sepia', 'dark', 'black'].includes(state.settings.theme) ? state.settings.theme : 'dark';
  document.documentElement.dataset.theme = theme;
  document.querySelector('meta[name="theme-color"]').content = cssVar('--paper');
}

function setAccent(color) {
  if (/^#[0-9a-f]{3,8}$/i.test(color || '')) document.documentElement.style.setProperty('--accent', color);
}

function openPanel(id) {
  closePanels();
  $(id).hidden = false;
  $('scrim').hidden = false;
  showChrome(true);
  const focusTarget = id === 'toc' ? $('toc').querySelector('a.current') || $('toc').querySelector('a') : $(id).querySelector('button');
  focusTarget?.focus({ preventScroll: false });
}

function closePanels() {
  $('toc').hidden = true;
  $('settings').hidden = true;
  $('scrim').hidden = true;
  $('stage').focus({ preventScroll: true });
  scheduleChromeHide(2500);
}

function buildToc(items) {
  $('btn-toc').hidden = !items.length;
  $('toc-list').replaceChildren(...items.map((it) => {
    const li = document.createElement('li');
    li.className = `lvl-${it.level}`;
    const a = document.createElement('a');
    a.href = '#';
    a.textContent = it.label || 'Untitled section';
    a.addEventListener('click', (e) => { e.preventDefault(); it.go(); closePanels(); });
    li.appendChild(a);
    return li;
  }));
}

function markTocCurrent(index) {
  $('toc-list').querySelectorAll('a').forEach((a, i) => a.classList.toggle('current', i === index));
}

function toggleChrome() {
  if (app.classList.contains('chrome-visible')) { app.classList.remove('chrome-visible'); clearTimeout(state.chromeTimer); }
  else showChrome();
}

function showChrome(stay = false) {
  app.classList.add('chrome-visible');
  if (!stay) scheduleChromeHide(4000);
}

function scheduleChromeHide(ms) {
  clearTimeout(state.chromeTimer);
  state.chromeTimer = setTimeout(() => {
    if ($('toc').hidden && $('settings').hidden && !dragging && !$('bottombar').matches(':hover') && !$('topbar').matches(':hover')) {
      app.classList.remove('chrome-visible');
    }
  }, ms);
}

function toggleFullscreen() {
  if (!document.fullscreenEnabled) return;
  if (document.fullscreenElement) document.exitFullscreen();
  else document.documentElement.requestFullscreen().catch(() => {});
}

function closeBook() {
  saveProgress(true);
  const fromJellyfin = document.referrer && new URL(document.referrer).origin === location.origin;
  if (fromJellyfin && history.length > 1) history.back();
  else location.href = `${API}/web/#/details?id=${encodeURIComponent(itemId || '')}`;
}

// ------------------------------------------------------------------
// Loading veil & messages
// ------------------------------------------------------------------
function setLoading(text, fraction) {
  app.classList.add('is-loading');
  $('veil-text').textContent = text;
  const meter = $('meter');
  if (fraction == null) { meter.hidden = true; return; }
  meter.hidden = false;
  $('meter-fill').style.width = `${Math.round(fraction * 100)}%`;
}

function showError(text) {
  app.classList.add('is-loading');
  $('veil').classList.add('error');
  $('veil-text').textContent = text;
  $('meter').hidden = true;
  $('veil-action').hidden = false;
}

function toast(text) {
  const t = $('toast');
  t.textContent = text;
  t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { t.hidden = true; }, 2600);
}

// ------------------------------------------------------------------
// Utilities
// ------------------------------------------------------------------
async function download(path) {
  const res = await api(path);
  if (res.status === 409) throw new ReaderError('This book is still being prepared. Try again in a moment.');
  if (!res.ok) throw new ReaderError('The book file could not be downloaded.');
  const total = Number(res.headers.get('Content-Length')) || 0;
  if (!res.body || !total) return res.arrayBuffer();
  const reader = res.body.getReader();
  const out = new Uint8Array(total);
  let got = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    if (got + value.length > out.length) return new Blob([out.slice(0, got), value]).arrayBuffer(); // size header was wrong
    out.set(value, got);
    got += value.length;
    if (total > 1.5e6) setLoading('Opening your book', got / total);
  }
  return out.buffer;
}

function loadScript(src) {
  return new Promise((resolve, reject) => {
    if (document.querySelector(`script[src="${src}"]`)) { resolve(); return; }
    const s = document.createElement('script');
    s.src = src;
    s.onload = resolve;
    s.onerror = () => reject(new ReaderError('Part of the reader failed to load. Reload the page to try again.'));
    document.head.appendChild(s);
  });
}

function clean(obj) {
  return Object.fromEntries(Object.entries(obj).filter(([, v]) => v !== null && v !== undefined));
}

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function clamp(n, lo, hi) { return Math.min(hi, Math.max(lo, n)); }
function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }
