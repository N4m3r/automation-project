/* Service worker — shell offline + fila de mutacoes (Background Sync).
   Nao intercepta CDN/cross-origin (quebrava hls.js e CSP connect-src). */
const CACHE = 'vms-shell-v6';
const ASSETS = [
  '/monitor.html',
  '/index.html',
  '/manifest.webmanifest',
  '/lib/hls.min.js'
];
const QUEUE_DB = 'vms-offline-queue';
const QUEUE_STORE = 'ops';

self.addEventListener('install', e => {
  e.waitUntil(
    caches.open(CACHE)
      .then(c => c.addAll(ASSETS).catch(() => {}))
      .then(() => self.skipWaiting())
  );
});

self.addEventListener('activate', e => {
  e.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

function openDb() {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(QUEUE_DB, 1);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(QUEUE_STORE))
        db.createObjectStore(QUEUE_STORE, { keyPath: 'id', autoIncrement: true });
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function allQueued() {
  const db = await openDb();
  return new Promise((resolve, reject) => {
    const tx = db.transaction(QUEUE_STORE, 'readonly');
    const r = tx.objectStore(QUEUE_STORE).getAll();
    r.onsuccess = () => resolve(r.result || []);
    r.onerror = () => reject(r.error);
  });
}

async function removeQueued(id) {
  const db = await openDb();
  return new Promise((resolve, reject) => {
    const tx = db.transaction(QUEUE_STORE, 'readwrite');
    tx.objectStore(QUEUE_STORE).delete(id);
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
  });
}

async function flushQueue() {
  const items = await allQueued();
  for (const item of items) {
    try {
      const headers = { ...(item.headers || {}) };
      if (item.token) headers['Authorization'] = 'Bearer ' + item.token;
      if (item.body && !headers['Content-Type']) headers['Content-Type'] = 'application/json';
      const res = await fetch(item.url, { method: item.method || 'POST', headers, body: item.body || null });
      if (res.ok || res.status === 204 || res.status === 409)
        await removeQueued(item.id);
    } catch {
      break;
    }
  }
  const left = (await allQueued()).length;
  const clients = await self.clients.matchAll({ type: 'window' });
  for (const c of clients) c.postMessage({ type: 'queue-flushed', remaining: left });
}

self.addEventListener('sync', e => {
  if (e.tag === 'vms-offline-flush') e.waitUntil(flushQueue());
});

self.addEventListener('message', e => {
  if (e.data && e.data.type === 'flush-queue') e.waitUntil(flushQueue());
  if (e.data && e.data.type === 'skip-waiting') self.skipWaiting();
});

self.addEventListener('fetch', e => {
  const req = e.request;
  if (req.method !== 'GET') return;

  let url;
  try { url = new URL(req.url); } catch { return; }

  // Nunca interceptar cross-origin (CDN, media externa, etc.)
  if (url.origin !== self.location.origin) return;

  // API / midia / WebSocket upgrade: deixa a rede pura
  if (url.pathname.startsWith('/api/') ||
      url.pathname.startsWith('/live/') ||
      url.pathname.startsWith('/media/') ||
      url.pathname.startsWith('/metrics') ||
      url.pathname.startsWith('/health'))
    return;

  e.respondWith(
    caches.match(req).then(hit => {
      if (hit) return hit;
      return fetch(req).then(res => {
        // Cache so shell e hls local
        if (res.ok && (
          url.pathname.endsWith('.html') ||
          url.pathname.endsWith('.webmanifest') ||
          url.pathname.endsWith('.js') ||
          url.pathname === '/sw.js' ||
          url.pathname.startsWith('/lib/')
        )) {
          const clone = res.clone();
          caches.open(CACHE).then(c => c.put(req, clone));
        }
        return res;
      }).catch(() => caches.match('/monitor.html'));
    })
  );
});
