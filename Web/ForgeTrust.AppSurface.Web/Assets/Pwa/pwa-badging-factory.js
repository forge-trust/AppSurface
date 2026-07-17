(root, nativeTarget, conflictSink) => {
  "use strict";

  const reportConflict = () => {
    try {
      conflictSink();
    } catch {
      // A hostile diagnostic sink must not make namespace containment observable through throws.
    }
  };

  const fallbackError = (code, name) => {
    const error = { message: code, name };
    try {
      Object.defineProperties(error, {
        message: { value: code, configurable: true },
        name: { value: name, configurable: true }
      });
    } catch {
      // The bounded fallback remains sanitized even if its descriptors cannot be hardened.
    }
    return error;
  };

  const hasExpectedErrorIdentity = (error, code, name) => {
    try {
      return error !== null
        && (typeof error === "object" || typeof error === "function")
        && error.message === code
        && error.name === name;
    } catch {
      return false;
    }
  };

  const invalidState = code => {
    try {
      const error = new DOMException(code, "InvalidStateError");
      if (hasExpectedErrorIdentity(error, code, "InvalidStateError")) return error;
    } catch {
      // Fall through to the constructor-independent bounded throwable.
    }
    return fallbackError(code, "InvalidStateError");
  };

  const invalidCount = () => {
    try {
      const error = new TypeError("ASPWAJS040");
      if (hasExpectedErrorIdentity(error, "ASPWAJS040", "TypeError")) return error;
    } catch {
      // Fall through to the constructor-independent bounded throwable.
    }
    return fallbackError("ASPWAJS040", "TypeError");
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

  const isCompatibleApi = (value, brandKey) => {
    try {
      if (!isPlainObject(value) || !Object.isFrozen(value) || Object.keys(value).length !== 0) return false;
      const brandDescriptor = Object.getOwnPropertyDescriptor(value, brandKey);
      const setDescriptor = Object.getOwnPropertyDescriptor(value, "set");
      const clearDescriptor = Object.getOwnPropertyDescriptor(value, "clear");
      const brand = brandDescriptor && brandDescriptor.value;
      const brandKeys = brand && Reflect.ownKeys(brand);
      const versionDescriptor = brand && Object.getOwnPropertyDescriptor(brand, "version");
      return Boolean(
        brandDescriptor
        && brandDescriptor.enumerable === false
        && brandDescriptor.writable === false
        && brandDescriptor.configurable === false
        && isPlainObject(brand)
        && Object.isFrozen(brand)
        && brandKeys.length === 1
        && brandKeys[0] === "version"
        && versionDescriptor
        && versionDescriptor.value === 1
        && versionDescriptor.enumerable === true
        && versionDescriptor.writable === false
        && versionDescriptor.configurable === false
        && setDescriptor
        && setDescriptor.enumerable === false
        && setDescriptor.writable === false
        && setDescriptor.configurable === false
        && typeof setDescriptor.value === "function"
        && clearDescriptor
        && clearDescriptor.enumerable === false
        && clearDescriptor.writable === false
        && clearDescriptor.configurable === false
        && typeof clearDescriptor.value === "function");
    } catch {
      return false;
    }
  };

  try {
    const brandKey = Symbol.for("ForgeTrust.AppSurface.Pwa.badging");
    const brand = Object.freeze({ version: 1 });

    let appSurface;
    let appSurfacePresent;
    try {
      appSurfacePresent = "AppSurface" in root;
      if (appSurfacePresent) {
        const descriptor = Object.getOwnPropertyDescriptor(root, "AppSurface");
        if (!descriptor || !("value" in descriptor)) {
          reportConflict();
          return;
        }
        appSurface = descriptor.value;
      }
    } catch {
      reportConflict();
      return;
    }

    if (!appSurfacePresent) {
      appSurface = {};
      try {
        Object.defineProperty(root, "AppSurface", { value: appSurface, writable: true, configurable: true });
      } catch {
        reportConflict();
        return;
      }
    } else if (!isPlainObject(appSurface)) {
      reportConflict();
      return;
    }

    let pwa;
    let pwaPresent;
    try {
      pwaPresent = "Pwa" in appSurface;
      if (pwaPresent) {
        const descriptor = Object.getOwnPropertyDescriptor(appSurface, "Pwa");
        if (!descriptor || !("value" in descriptor)) {
          reportConflict();
          return;
        }
        pwa = descriptor.value;
      }
    } catch {
      reportConflict();
      return;
    }

    if (!pwaPresent) {
      pwa = {};
      try {
        Object.defineProperty(appSurface, "Pwa", { value: pwa, writable: true, configurable: true });
      } catch {
        reportConflict();
        return;
      }
    } else if (!isPlainObject(pwa)) {
      reportConflict();
      return;
    }

    let existing;
    let badgingPresent;
    try {
      badgingPresent = "badging" in pwa;
      if (badgingPresent) {
        const descriptor = Object.getOwnPropertyDescriptor(pwa, "badging");
        if (!descriptor || !("value" in descriptor)) {
          reportConflict();
          return;
        }
        existing = descriptor.value;
      }
    } catch {
      reportConflict();
      return;
    }

    if (badgingPresent) {
      if (isCompatibleApi(existing, brandKey)) return;
      reportConflict();
      return;
    }

    const clear = async () => {
      let method;
      let usesClearMethod = false;
      try {
        method = nativeTarget.clearAppBadge;
        usesClearMethod = typeof method === "function";
        if (!usesClearMethod) method = nativeTarget.setAppBadge;
      } catch {
        throw invalidState("ASPWAJS042");
      }
      if (typeof method !== "function") return "unsupported";
      try {
        if (usesClearMethod) await method.call(nativeTarget);
        else await method.call(nativeTarget, 0);
        return "accepted";
      } catch {
        throw invalidState("ASPWAJS042");
      }
    };

    const set = async count => {
      if (!Number.isSafeInteger(count) || count < 0) throw invalidCount();
      if (count === 0) return clear();

      let method;
      try {
        method = nativeTarget.setAppBadge;
      } catch {
        throw invalidState("ASPWAJS041");
      }
      if (typeof method !== "function") return "unsupported";
      try {
        await method.call(nativeTarget, count);
        return "accepted";
      } catch {
        throw invalidState("ASPWAJS041");
      }
    };

    const api = {};
    Object.defineProperties(api, {
      set: { value: set },
      clear: { value: clear },
      [brandKey]: { value: brand }
    });
    Object.freeze(api);

    try {
      Object.defineProperty(pwa, "badging", { value: api, writable: false, configurable: false });
    } catch {
      reportConflict();
    }
  } catch {
    reportConflict();
  }
}
