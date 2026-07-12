// AppSurface PWA worker default push capability fragment.
(() => {
  "use strict";

  const config = APPSURFACE_PWA_CONFIG;
  const allowedKeys = ["version", "title", "body", "iconPath", "badgePath", "tag", "destinationPath"];
  const encoderLimit = 3993;

  const warn = code => {
    try {
      console.warn(code);
    } catch {
      // Diagnostics must not disclose data or break event handling.
    }
  };

  const isBoundedString = (value, minimum, maximum) =>
    typeof value === "string" && value.length >= minimum && value.length <= maximum;

  const hasMalformedEscape = value => {
    for (let index = 0; index < value.length; index += 1) {
      if (value[index] !== "%") continue;
      if (!/^[0-9a-f]{2}$/iu.test(value.slice(index + 1, index + 3))) return true;
      index += 2;
    }
    return false;
  };

  const decodeSafePathname = value => {
    if (hasMalformedEscape(value)) return null;
    let decoded = value;
    try {
      while (true) {
        const next = decodeURIComponent(decoded);
        if (next === decoded) break;
        decoded = next;
      }
    } catch {
      return null;
    }

    if (decoded.startsWith("//")
        || decoded.includes("\\")
        || /[\u0000-\u0020\u007f{}?#]/u.test(decoded)
        || decoded.split("/").some(segment => segment === "." || segment === "..")) {
      return null;
    }
    return decoded;
  };

  const parseSafePath = (value, allowQuery) => {
    if (!isBoundedString(value, 1, 1024)
        || value[0] !== "/"
        || value.startsWith("//")
        || value.includes("\\")
        || value.includes("#")
        || (!allowQuery && value.includes("?"))
        || hasMalformedEscape(value)
        || /[\u0000-\u0020\u007f{}]/u.test(value)) {
      return null;
    }

    const queryIndex = value.indexOf("?");
    if (allowQuery && queryIndex !== -1 && value.indexOf("?", queryIndex + 1) !== -1) return null;
    const pathname = queryIndex === -1 ? value : value.slice(0, queryIndex);
    const query = queryIndex === -1 ? "" : value.slice(queryIndex);

    const decoded = decodeSafePathname(pathname);
    if (decoded === null) return null;

    const pathBase = typeof config.pathBase === "string" && config.pathBase !== "/"
      ? config.pathBase.replace(/\/$/u, "")
      : "";
    let resolved;
    try {
      resolved = new URL(`${pathBase}${decoded}${query}`, self.location.origin);
    } catch {
      return null;
    }

    if (resolved.origin !== self.location.origin || resolved.username || resolved.password || resolved.hash) return null;
    return resolved;
  };

  const isAtOrBelowScope = url => {
    let scope;
    try {
      scope = new URL(config.scope, self.location.origin);
    } catch {
      return false;
    }
    if (scope.origin !== url.origin) return false;
    return url.pathname.startsWith(scope.pathname);
  };

  const parsePayload = async data => {
    if (!data || typeof data.arrayBuffer !== "function") return null;
    const bytes = await data.arrayBuffer();
    if (bytes === null || typeof bytes !== "object" || typeof bytes.byteLength !== "number" || bytes.byteLength > encoderLimit) return null;

    let payload;
    try {
      payload = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
    } catch {
      return null;
    }

    if (payload === null || Array.isArray(payload)) return null;
    const payloadPrototype = Object.getPrototypeOf(payload);
    if (payloadPrototype !== null && Object.getPrototypeOf(payloadPrototype) !== null) return null;
    const keys = Object.keys(payload);
    if (keys.length < 2 || keys.some(key => !allowedKeys.includes(key))) return null;
    if (!Object.prototype.hasOwnProperty.call(payload, "version") || payload.version !== 1) return null;
    if (!Object.prototype.hasOwnProperty.call(payload, "title") || !isBoundedString(payload.title, 1, 256)) return null;

    for (const key of ["body", "tag"]) {
      if (Object.prototype.hasOwnProperty.call(payload, key)
          && !isBoundedString(payload[key], 1, key === "body" ? 2048 : 128)) return null;
    }

    const options = {};
    if (payload.body !== undefined) options.body = payload.body;
    if (payload.tag !== undefined) options.tag = payload.tag;

    for (const key of ["iconPath", "badgePath"]) {
      if (!Object.prototype.hasOwnProperty.call(payload, key)) continue;
      const asset = parseSafePath(payload[key], false);
      if (!asset) return null;
      options[key === "iconPath" ? "icon" : "badge"] = asset.href;
    }

    if (Object.prototype.hasOwnProperty.call(payload, "destinationPath")) {
      const destination = parseSafePath(payload.destinationPath, true);
      if (!destination || !isAtOrBelowScope(destination)) return null;
      options.data = { appSurfaceDestination: destination.href };
    }

    return { title: payload.title, options };
  };

  self.addEventListener("push", event => {
    event.waitUntil((async () => {
      let notification;
      try {
        notification = await parsePayload(event.data);
      } catch {
        notification = null;
      }
      if (!notification) {
        warn("ASPWAJS010");
        return;
      }

      try {
        await self.registration.showNotification(notification.title, notification.options);
      } catch {
        warn("ASPWAJS011");
      }
    })());
  });

  const validateClickDestination = value => {
    if (typeof value !== "string") return null;
    let url;
    try {
      url = new URL(value);
    } catch {
      return null;
    }
    if (url.origin !== self.location.origin
        || url.username
        || url.password
        || url.hash
        || hasMalformedEscape(`${url.pathname}${url.search}`)
        || url.search.slice(1).includes("?")) return null;
    const decodedPathname = decodeSafePathname(url.pathname);
    if (decodedPathname === null) return null;
    url.pathname = decodedPathname;
    if (!isAtOrBelowScope(url)) return null;
    return url;
  };

  const openDestination = async destination => {
    if (typeof self.clients.openWindow !== "function") throw new Error("unavailable");
    const opened = await self.clients.openWindow(destination.href);
    if (!opened) throw new Error("unavailable");
  };

  self.addEventListener("notificationclick", event => {
    event.waitUntil((async () => {
      try {
        event.notification.close();
      } catch {
        // Closing is best effort; navigation can still proceed safely.
      }

      let data;
      try {
        data = event.notification && event.notification.data;
      } catch {
        warn("ASPWAJS020");
        return;
      }
      const destination = validateClickDestination(data && data.appSurfaceDestination);
      if (!destination) {
        warn("ASPWAJS020");
        return;
      }

      try {
        if (typeof self.clients.matchAll !== "function") throw new Error("unavailable");
        const clientList = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
        const matchingClient = clientList.find(client => {
          try {
            return new URL(client.url).href === destination.href;
          } catch {
            return false;
          }
        });

        if (matchingClient && typeof matchingClient.focus === "function") {
          try {
            const focused = await matchingClient.focus();
            if (focused) return;
          } catch {
            // One open fallback is required when focusing fails.
          }
        }
        await openDestination(destination);
      } catch {
        warn("ASPWAJS021");
      }
    })());
  });
})();
