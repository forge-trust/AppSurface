// AppSurface PWA explicit registration helper.
(() => {
  "use strict";

  const conflict = () => {
    try {
      console.error("ASPWAJS002");
    } catch {
      // A hostile console must not make this helper observable through throws.
    }
  };

  const invalidState = code => {
    try {
      return new DOMException(code, "InvalidStateError");
    } catch {
      const error = new Error(code);
      error.name = "InvalidStateError";
      return error;
    }
  };

  const isPlainObject = value => {
    if (value === null || typeof value !== "object") return false;
    try {
      const prototype = Object.getPrototypeOf(value);
      return prototype === null || prototype === Object.prototype;
    } catch {
      return false;
    }
  };

  const isSafeMetadata = value => {
    if (typeof value !== "string" || value[0] !== "/" || value.startsWith("//")) return false;
    if (value.includes("\\") || value.includes("?") || value.includes("#") || /[\u0000-\u0020\u007f{}]/u.test(value)) return false;
    try {
      let decoded = value;
      while (true) {
        const next = decodeURIComponent(decoded);
        if (next === decoded) break;
        decoded = next;
      }
      return !decoded.startsWith("//")
        && !decoded.includes("\\")
        && !/[\u0000-\u0020\u007f{}?#]/u.test(decoded)
        && !decoded.split("/").some(segment => segment === "." || segment === "..");
    } catch {
      return false;
    }
  };

  try {
    const script = document.currentScript;
    const workerPath = script && script.dataset ? script.dataset.appsurfacePwaWorker : null;
    const scope = script && script.dataset ? script.dataset.appsurfacePwaScope : null;
    const metadataValid = isSafeMetadata(workerPath) && isSafeMetadata(scope);
    const brandKey = Symbol.for("ForgeTrust.AppSurface.Pwa.register");
    const brand = Object.freeze({ version: 1, workerPath, scope });

    let appSurface;
    let appSurfacePresent;
    try {
      appSurfacePresent = "AppSurface" in window;
      if (appSurfacePresent) {
        const descriptor = Object.getOwnPropertyDescriptor(window, "AppSurface");
        if (!descriptor || !("value" in descriptor)) {
          conflict();
          return;
        }
        appSurface = descriptor.value;
      }
    } catch {
      conflict();
      return;
    }
    if (!appSurfacePresent || appSurface === undefined) {
      appSurface = {};
      try {
        Object.defineProperty(window, "AppSurface", { value: appSurface, writable: true, configurable: true });
      } catch {
        conflict();
        return;
      }
    } else if (!isPlainObject(appSurface)) {
      conflict();
      return;
    }

    let pwa;
    let pwaPresent;
    try {
      pwaPresent = "Pwa" in appSurface;
      if (pwaPresent) {
        const descriptor = Object.getOwnPropertyDescriptor(appSurface, "Pwa");
        if (!descriptor || !("value" in descriptor)) {
          conflict();
          return;
        }
        pwa = descriptor.value;
      }
    } catch {
      conflict();
      return;
    }
    if (!pwaPresent) {
      pwa = {};
      try {
        Object.defineProperty(appSurface, "Pwa", { value: pwa, writable: true, configurable: true });
      } catch {
        conflict();
        return;
      }
    } else if (!isPlainObject(pwa)) {
      conflict();
      return;
    }

    let existing;
    let registerPresent;
    try {
      registerPresent = "register" in pwa;
      if (registerPresent) {
        const descriptor = Object.getOwnPropertyDescriptor(pwa, "register");
        if (!descriptor || !("value" in descriptor)) {
          conflict();
          return;
        }
        existing = descriptor.value;
      }
    } catch {
      conflict();
      return;
    }
    if (registerPresent) {
      let existingBrand;
      try {
        existingBrand = existing && existing[brandKey];
      } catch {
        conflict();
        return;
      }
      if (isPlainObject(existingBrand)
          && existingBrand.version === brand.version
          && existingBrand.workerPath === brand.workerPath
          && existingBrand.scope === brand.scope) return;
      conflict();
      return;
    }

    const register = async () => {
      try {
        if (!("serviceWorker" in navigator)) return null;
      } catch {
        throw invalidState("ASPWAJS003");
      }
      if (!metadataValid) throw invalidState("ASPWAJS001");
      try {
        return await navigator.serviceWorker.register(workerPath, { scope, updateViaCache: "none" });
      } catch {
        throw invalidState("ASPWAJS003");
      }
    };
    Object.defineProperty(register, brandKey, { value: brand });

    try {
      Object.defineProperty(pwa, "register", { value: register, writable: false, configurable: false });
    } catch {
      conflict();
    }
  } catch {
    conflict();
  }
})();
