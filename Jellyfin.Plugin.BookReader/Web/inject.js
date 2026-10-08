/* Book Reader for Jellyfin — web client integration.
 * Loaded into Jellyfin's web client. Adds a "Read" button to book pages and,
 * if the admin chose it, sends Play/Resume on books to this reader.
 */
(function () {
  'use strict';
  if (window.__bookReader) return;
  window.__bookReader = { ready: false };

  let settings = null;
  const itemTypeCache = new Map();

  function client() { return window.ApiClient; }

  function readerUrl(id) {
    const api = client();
    return `${api.serverAddress()}/BookReader/Reader?id=${encodeURIComponent(id)}#t=${encodeURIComponent(api.accessToken())}`;
  }

  function openReader(id) {
    window.location.href = readerUrl(id);
  }

  async function loadSettings() {
    const api = client();
    try {
      const res = await fetch(api.getUrl('BookReader/Settings'), {
        headers: { Authorization: `MediaBrowser Token="${api.accessToken()}"` },
      });
      settings = res.ok ? await res.json() : { replaceBuiltInReader: false };
    } catch {
      settings = { replaceBuiltInReader: false };
    }
  }

  async function itemType(id) {
    if (itemTypeCache.has(id)) return itemTypeCache.get(id);
    const api = client();
    const p = api.getItem(api.getCurrentUserId(), id).then((i) => i.Type).catch(() => null);
    itemTypeCache.set(id, p);
    return p;
  }

  function currentDetailsId() {
    const hash = window.location.hash || '';
    if (!/details/i.test(hash)) return null;
    const q = hash.indexOf('?');
    return q >= 0 ? new URLSearchParams(hash.slice(q + 1)).get('id') : null;
  }

  // ---- "Read" button on a book's page ----
  async function decorateDetailsPage() {
    const id = currentDetailsId();
    if (!id) return;
    const page = document.querySelector('.page:not(.hide) .mainDetailButtons') || document.querySelector('.mainDetailButtons');
    if (!page || page.querySelector('.btnBookReader')) return;
    if ((await itemType(id)) !== 'Book') return;
    if (page.querySelector('.btnBookReader')) return;

    const btn = document.createElement('button');
    btn.type = 'button';
    btn.setAttribute('is', 'emby-button');
    btn.className = 'button-flat btnBookReader detailButton emby-button';
    btn.title = 'Read';
    btn.innerHTML = '<div class="detailButton-content"><span class="material-icons detailButton-icon auto_stories" aria-hidden="true"></span></div>';
    btn.addEventListener('click', (e) => { e.preventDefault(); e.stopPropagation(); openReader(id); });
    page.insertBefore(btn, page.firstChild);
  }

  // ---- Send Play / Resume on books to the reader ----
  function interceptPlay(e) {
    if (!settings || !settings.replaceBuiltInReader) return;
    const target = e.target.closest && e.target.closest('[data-action="play"], [data-action="resume"], .btnPlay, .btnResume, .btnReadBook');
    if (!target) return;

    const card = target.closest('[data-type]');
    if (card && card.getAttribute('data-type') === 'Book' && card.getAttribute('data-id')) {
      e.preventDefault(); e.stopImmediatePropagation();
      openReader(card.getAttribute('data-id'));
      return;
    }

    const id = currentDetailsId();
    if (id && target.closest('.mainDetailButtons')) {
      const cached = itemTypeCache.get(id);
      // Only intercept once we know it is a book (the type is fetched when the page opens).
      if (cached && cached.__isBook) {
        e.preventDefault(); e.stopImmediatePropagation();
        openReader(id);
      }
    }
  }

  function markBookPromises() {
    const id = currentDetailsId();
    if (!id) return;
    const p = itemTypeCache.get(id);
    if (p && !('__isBook' in p)) p.then((t) => { p.__isBook = t === 'Book'; });
  }

  let scheduled = false;
  function onChange() {
    if (scheduled) return;
    scheduled = true;
    setTimeout(() => {
      scheduled = false;
      decorateDetailsPage().then(markBookPromises);
    }, 150);
  }

  function start() {
    if (!client() || !client().accessToken || !client().accessToken()) {
      setTimeout(start, 500);
      return;
    }
    window.__bookReader.ready = true;
    loadSettings();
    document.addEventListener('click', interceptPlay, true);
    window.addEventListener('hashchange', onChange);
    new MutationObserver(onChange).observe(document.body, { childList: true, subtree: true });
    onChange();
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
