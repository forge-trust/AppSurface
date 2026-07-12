// AppSurface PWA worker offline capability fragment.
(() => {
  "use strict";

  const config = APPSURFACE_PWA_CONFIG;
  const staticAssets = config.staticAssets;

  self.addEventListener("install", event => {
    event.waitUntil((async () => {
      const cache = await caches.open(config.cacheName);
      await cache.addAll(staticAssets);
    })());
  });

  self.addEventListener("fetch", event => {
    const request = event.request;
    if (request.method !== "GET") return;

    const url = new URL(request.url);
    if (url.origin !== self.location.origin) return;

    if (staticAssets.includes(url.pathname)) {
      event.respondWith(caches.match(request, { cacheName: config.cacheName, ignoreSearch: true }).then(cached => cached || fetch(request)));
      return;
    }

    if (request.mode === "navigate") {
      event.respondWith(fetch(request).catch(() => caches.match(config.offlineFallback, { cacheName: config.cacheName })));
    }
  });
})();
