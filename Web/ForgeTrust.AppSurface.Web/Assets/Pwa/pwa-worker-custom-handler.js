// AppSurface PWA worker custom push-handler import fragment.
(() => {
  "use strict";

  try {
    importScripts(APPSURFACE_PWA_CONFIG.handlerScriptPath);
  } catch {
    try {
      console.warn("ASPWAJS030");
    } catch {
      // Diagnostics must not expose exception details or break worker installation.
    }
  }
})();
