// AppSurface PWA worker shared lifecycle fragment.
(() => {
  "use strict";

  const config = APPSURFACE_PWA_CONFIG;
  const warn = () => {
    try {
      console.warn("ASPWAJS030");
    } catch {
      // Diagnostics must never break the worker lifecycle.
    }
  };

  self.addEventListener("install", event => {
    event.waitUntil((async () => {
      try {
        await self.skipWaiting();
      } catch {
        warn();
      }
    })());
  });

  self.addEventListener("activate", event => {
    event.waitUntil((async () => {
      try {
        const cachePrefix = typeof config.cachePrefix === "string" ? config.cachePrefix : "";
        const activeCache = config.offlineEnabled === true && typeof config.cacheName === "string"
          ? config.cacheName
          : null;
        const legacyCaches = Array.isArray(config.legacyCacheNames) ? config.legacyCacheNames : [];
        const keys = await caches.keys();
        const obsolete = keys.filter(key =>
          (cachePrefix.length > 0 && key.startsWith(cachePrefix) && key !== activeCache)
          || legacyCaches.includes(key));
        await Promise.all(obsolete.map(key => caches.delete(key)));
      } catch {
        warn();
      }

      try {
        await self.clients.claim();
      } catch {
        warn();
      }
    })());
  });
})();
