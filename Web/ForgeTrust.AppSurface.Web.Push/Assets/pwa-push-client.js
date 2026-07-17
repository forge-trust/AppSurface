// AppSurface Web Push browser client v1. Calls that may prompt must be initiated by host-owned user gestures.
(() => {
  "use strict";

  const brand = Symbol.for("ForgeTrust.AppSurface.Pwa.Push.v1");
  const installedBrand = Symbol.for("ForgeTrust.AppSurface.Pwa.Push.installed");
  const warn = code => { try { console.warn(code); } catch { /* safe diagnostics only */ } };
  const objectLike = value => (typeof value === "object" && value !== null) || typeof value === "function";

  let root;
  let pwa;
  try {
    if (window.AppSurface !== undefined && !objectLike(window.AppSurface)) {
      warn("ASPUSHJS002");
      return;
    }
    root = window.AppSurface || {};
    if (window.AppSurface === undefined) window.AppSurface = root;
    if (root.Pwa !== undefined && !objectLike(root.Pwa)) {
      warn("ASPUSHJS002");
      return;
    }
    pwa = root.Pwa || {};
    if (root.Pwa === undefined) root.Pwa = pwa;
  } catch {
    warn("ASPUSHJS002");
    return;
  }

  if (pwa[installedBrand] === brand) return;
  if (["prepare", "subscribe", "unsubscribe"].some(name => name in pwa)) {
    warn("ASPUSHJS002");
    return;
  }

  const scriptPath = "/_appsurface/pwa/push-client.js";
  let pathBase = "";
  try {
    const scriptUrl = new URL(document.currentScript.src);
    const marker = scriptUrl.pathname.lastIndexOf(scriptPath);
    if (marker >= 0) pathBase = scriptUrl.pathname.slice(0, marker);
  } catch { /* root path remains safe */ }

  const handles = new WeakMap();
  let mutationActive = false;
  const now = () => Date.now();
  const result = (status, retryable = false) => Object.freeze({ status, retryable });
  const invalidState = () => new DOMException("ASPUSHJS009", "InvalidStateError");
  const isTransientStatus = status => status === 408 || status === 429 || status >= 500;
  const expectedStatus = new Map([
    [401, "unauthorized"], [403, "forbidden"],
    [409, "custody-conflict"], [413, "custody-failed"], [415, "custody-failed"]
  ]);
  const configurationStatus = new Map([[401, "unauthorized"], [403, "forbidden"]]);

  const abort = signal => {
    if (signal?.aborted) throw new DOMException("ASPUSHJS003", "AbortError");
  };

  const safeEndpoint = value => {
    if (typeof value !== "string" || value.includes("%")) throw new TypeError("ASPUSHJS004");
    let url;
    try { url = new URL(value, window.location.href); } catch { throw new TypeError("ASPUSHJS004"); }
    const base = pathBase || "";
    if (url.origin !== window.location.origin || url.username || url.password || url.hash || url.search
        || url.pathname.endsWith("/") || !url.pathname.startsWith(`${base}/`) || url.pathname.endsWith("/configuration")) {
      throw new TypeError("ASPUSHJS004");
    }
    return url;
  };

  const base64Url = bytes => {
    let binary = "";
    for (const value of new Uint8Array(bytes)) binary += String.fromCharCode(value);
    return btoa(binary).replace(/\+/gu, "-").replace(/\//gu, "_").replace(/=+$/gu, "");
  };

  const decodeKey = value => {
    if (typeof value !== "string" || !/^[A-Za-z0-9_-]+$/u.test(value)) throw new DOMException("ASPUSHJS005", "InvalidStateError");
    const padded = value.replace(/-/gu, "+").replace(/_/gu, "/") + "===".slice((value.length + 3) % 4);
    const binary = atob(padded);
    return Uint8Array.from(binary, character => character.charCodeAt(0));
  };

  const authorizationHeader = async (callback, signal) => {
    if (typeof callback !== "function") return null;
    abort(signal);
    let value;
    try { value = await callback({ signal }); } catch (error) {
      abort(signal);
      throw Object.assign(new Error("ASPUSHJS006"), { safeStatus: "authorization-failed" });
    }
    if (typeof value !== "string" || !/^Bearer\s+\S+$/u.test(value)) {
      throw Object.assign(new Error("ASPUSHJS006"), { safeStatus: "authorization-failed" });
    }
    return value;
  };

  const fetchAuthorized = async (url, configuration, authorization, init, signal) => {
    const headers = new Headers(init?.headers);
    if (configuration?.requestProtection === "antiforgery") {
      headers.set(configuration.antiforgery.headerName, configuration.antiforgery.requestToken);
    } else if (authorization) {
      let token = await authorizationHeader(authorization, signal);
      headers.set("Authorization", token);
      token = null;
    }
    return fetch(url, { ...init, headers, signal, credentials: "same-origin" });
  };

  const fetchConfiguration = async (endpoint, authorization, signal) => {
    let response;
    try {
      const headers = new Headers();
      if (authorization) {
        let token = await authorizationHeader(authorization, signal);
        headers.set("Authorization", token);
        token = null;
      }
      response = await fetch(`${endpoint.pathname}/configuration`, {
        headers, signal, credentials: "same-origin", cache: "no-store"
      });
    } catch (error) {
      abort(signal);
      if (error?.safeStatus) return { failure: result(error.safeStatus, true) };
      return { failure: result("network-failed", true) };
    }
    if (!response.ok) return { failure: result(configurationStatus.get(response.status) || "configuration-failed", isTransientStatus(response.status)) };
    let configuration;
    try { configuration = await response.json(); } catch { return { failure: result("invalid-response") }; }
    if (configuration?.schemaVersion !== 1
        || typeof configuration.vapidKeyId !== "string"
        || typeof configuration.applicationServerKey !== "string"
        || !["antiforgery", "bearer"].includes(configuration.requestProtection)
        || (configuration.requestProtection === "bearer" && typeof authorization !== "function")
        || (configuration.requestProtection === "antiforgery"
            && (typeof configuration.antiforgery?.headerName !== "string"
                || typeof configuration.antiforgery?.requestToken !== "string"))) {
      return { failure: result("invalid-response") };
    }
    return { configuration };
  };

  const mutationFailure = async (response, signal) => {
    if (response.status === 400) {
      try {
        const problem = await response.json();
        abort(signal);
        if (problem?.code === "ASPUSH104") return result("antiforgery-failed");
      } catch {
        abort(signal);
        /* malformed problem details remain a custody failure */
      }
      return result("custody-failed");
    }
    return result(expectedStatus.get(response.status) || "custody-failed", isTransientStatus(response.status));
  };

  const prepare = async ({ endpoint, authorization, signal } = {}) => {
    abort(signal);
    const endpointUrl = safeEndpoint(endpoint);
    if (!("serviceWorker" in navigator) || !("PushManager" in window) || !("Notification" in window)) return result("unsupported");
    if (typeof pwa.register !== "function") return result("worker-registration-failed");

    const configResult = await fetchConfiguration(endpointUrl, authorization, signal);
    if (configResult.failure) return configResult.failure;
    let registration;
    try {
      registration = await pwa.register();
      abort(signal);
    } catch {
      abort(signal);
      return result("worker-registration-failed", true);
    }
    if (!registration?.pushManager) return result("worker-registration-failed");

    let current;
    try {
      current = await registration.pushManager.getSubscription();
      abort(signal);
    } catch {
      abort(signal);
      return result("browser-subscription-failed", true);
    }
    const configuration = configResult.configuration;
    if (current) {
      try {
        if (!current.options?.applicationServerKey
            || base64Url(current.options.applicationServerKey) !== configuration.applicationServerKey) {
          return result("vapid-key-migration-required");
        }
      } catch {
        throw invalidState();
      }
    }

    const handle = Object.create(null);
    Object.defineProperty(handle, "toJSON", { value: () => { throw new TypeError("ASPUSHJS007"); } });
    Object.freeze(handle);
    handles.set(handle, {
      endpoint: endpointUrl, authorization, configuration, registration,
      observed: current || null, expires: now() + 300000, consumed: false
    });
    return Object.freeze({ status: "prepared", handle });
  };

  const subscribe = ({ prepared, signal } = {}) => {
    abort(signal);
    if (mutationActive) return Promise.resolve(result("operation-in-progress", true));
    const state = handles.get(prepared);
    if (!state || state.consumed || state.expires <= now()) throw new TypeError("ASPUSHJS008");
    mutationActive = true;
    state.consumed = true;

    // This browser call intentionally occurs before the first await so transient user activation is preserved.
    let browserPromise;
    try {
      browserPromise = state.observed
        ? Promise.resolve(state.observed)
        : state.registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey: decodeKey(state.configuration.applicationServerKey)
          });
    } catch {
      mutationActive = false;
      return Promise.resolve(result("browser-subscription-failed", true));
    }

    return (async () => {
      try {
        let subscription;
        try { subscription = await browserPromise; } catch {
          abort(signal);
          if (Notification.permission === "denied") return result("permission-denied");
          if (Notification.permission === "default") return result("permission-dismissed", true);
          return result("browser-subscription-failed", true);
        }
        abort(signal);
        let live;
        try {
          live = await state.registration.pushManager.getSubscription();
          abort(signal);
        }
        catch {
          abort(signal);
          return result("browser-subscription-failed", true);
        }
        let liveApplicationKey;
        let liveEndpoint;
        let subscriptionEndpoint;
        try {
          liveApplicationKey = live?.options?.applicationServerKey;
          liveEndpoint = live?.endpoint;
          subscriptionEndpoint = subscription.endpoint;
        }
        catch { return result("vapid-key-stale", true); }
        if (!live || liveEndpoint !== subscriptionEndpoint || !liveApplicationKey) {
          return result("vapid-key-stale", true);
        }
        let liveApplicationKeyText;
        try { liveApplicationKeyText = base64Url(liveApplicationKey); }
        catch { return result("vapid-key-stale", true); }
        if (liveApplicationKeyText !== state.configuration.applicationServerKey) {
          return result("vapid-key-stale", true);
        }
        let body;
        try {
          const wire = live.toJSON();
          body = JSON.stringify({
            schemaVersion: 1,
            endpoint: wire.endpoint,
            keys: wire.keys,
            vapidKeyId: state.configuration.vapidKeyId
          });
        } catch {
          throw invalidState();
        }
        let response;
        try {
          response = await fetchAuthorized(state.endpoint, state.configuration, state.authorization, {
            method: "PUT", headers: { "Content-Type": "application/json" },
            body
          }, signal);
        } catch (error) {
          abort(signal);
          return result(error?.safeStatus || "network-failed", true);
        }
        if (response.status === 204) return result(state.observed ? "already-subscribed" : "subscribed");
        if (response.status === 409) {
          try {
            const problem = await response.json();
            abort(signal);
            if (problem?.code === "ASPUSH109") return result("vapid-key-stale", true);
          } catch {
            abort(signal);
          }
        }
        return await mutationFailure(response, signal);
      } finally { mutationActive = false; }
    })();
  };

  const unsubscribe = async ({ endpoint, authorization, signal } = {}) => {
    abort(signal);
    if (mutationActive) return result("operation-in-progress", true);
    mutationActive = true;
    try {
      const endpointUrl = safeEndpoint(endpoint);
      if (!("serviceWorker" in navigator) || typeof pwa.register !== "function") return result("unsupported");
      const configResult = await fetchConfiguration(endpointUrl, authorization, signal);
      if (configResult.failure) return configResult.failure;
      let registration;
      try {
        registration = await pwa.register();
        abort(signal);
      } catch {
        abort(signal);
        return result("worker-registration-failed", true);
      }
      let current;
      try {
        current = await registration.pushManager.getSubscription();
        abort(signal);
      }
      catch {
        abort(signal);
        return result("browser-unsubscribe-failed", true);
      }
      if (!current) return result("already-unsubscribed");

      let response;
      try {
        response = await fetchAuthorized(endpointUrl, configResult.configuration, authorization, {
          method: "DELETE", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ schemaVersion: 1, endpoint: current.endpoint })
        }, signal);
      } catch (error) {
        abort(signal);
        return result(error?.safeStatus || "network-failed", true);
      }
      if (response.status !== 204) return await mutationFailure(response, signal);
      try {
        const removed = await current.unsubscribe();
        abort(signal);
        return removed ? result("unsubscribed") : result("browser-unsubscribe-failed", true);
      } catch {
        abort(signal);
        return result("browser-unsubscribe-failed", true);
      }
    } finally { mutationActive = false; }
  };

  Object.defineProperties(pwa, {
    prepare: { value: prepare, enumerable: true },
    subscribe: { value: subscribe, enumerable: true },
    unsubscribe: { value: unsubscribe, enumerable: true },
    [installedBrand]: { value: brand }
  });
})();
