import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";

const source = await readFile(new URL("../Assets/pwa-push-client.js", import.meta.url), "utf8");
const applicationKey = Uint8Array.from({ length: 65 }, (_, index) => index === 0 ? 4 : index);
const applicationKeyText = Buffer.from(applicationKey).toString("base64url");

const deferred = () => {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
};

const createHarness = ({ current = null, subscribePromise, onFetch, getSubscription, responseFor, register, clock = Date.now } = {}) => {
  const calls = [];
  let subscribeCalled = false;
  let unsubscribeCalled = false;
  const subscription = current || {
    endpoint: "https://push.example.test/send",
    options: { applicationServerKey: applicationKey },
    toJSON: () => ({
      endpoint: "https://push.example.test/send",
      keys: { p256dh: "public", auth: "secret" }
    }),
    unsubscribe: async () => { unsubscribeCalled = true; return true; }
  };
  const registration = {
    pushManager: {
      getSubscription: getSubscription || (async () => current),
      subscribe: options => {
        subscribeCalled = true;
        calls.push(["browser-subscribe", options]);
        return (subscribePromise || Promise.resolve(subscription)).then(value => {
          current = value;
          return value;
        });
      }
    }
  };
  const context = {
    URL, Headers, DOMException,
    Date: class extends Date { static now() { return clock(); } },
    Uint8Array,
    atob: value => Buffer.from(value, "base64").toString("binary"),
    btoa: value => Buffer.from(value, "binary").toString("base64"),
    console: { warn: code => calls.push(["warn", code]) },
    document: { currentScript: { src: "https://app.example.test/_appsurface/pwa/push-client.js?v=1" } },
    navigator: { serviceWorker: {} },
    Notification: { permission: "granted" },
    PushManager: function PushManager() {},
    fetch: async (url, init = {}) => {
      calls.push(["fetch", String(url), init]);
      onFetch?.(String(url), init);
      const supplied = responseFor?.(String(url), init);
      if (supplied) return supplied;
      if (String(url).endsWith("/configuration")) {
        return {
          ok: true,
          status: 200,
          json: async () => ({
            schemaVersion: 1,
            vapidKeyId: "primary",
            applicationServerKey: applicationKeyText,
            requestProtection: "antiforgery",
            antiforgery: { headerName: "X-CSRF", requestToken: "token" }
          })
        };
      }
      return { ok: true, status: 204, json: async () => ({}) };
    },
    window: {
      location: { origin: "https://app.example.test", href: "https://app.example.test/account" },
      AppSurface: { Pwa: { register: register || (async () => registration) } }
    }
  };
  context.window.window = context.window;
  context.window.Notification = context.Notification;
  context.window.PushManager = context.PushManager;
  vm.runInNewContext(source, context);
  return {
    api: context.window.AppSurface.Pwa,
    calls,
    registration,
    subscription,
    get subscribeCalled() { return subscribeCalled; },
    get unsubscribeCalled() { return unsubscribeCalled; }
  };
};

test("prepare is non-prompting and returns a five-minute opaque handle", async () => {
  const harness = createHarness();

  const prepared = await harness.api.prepare({ endpoint: "/account/push" });

  assert.equal(prepared.status, "prepared");
  assert.equal(Object.isFrozen(prepared.handle), true);
  assert.throws(() => JSON.stringify(prepared.handle), error => error?.name === "TypeError");
  assert.equal(harness.subscribeCalled, false);
});

test("prepared handles expire at the exact five-minute boundary", async () => {
  let beforeBoundary = 0;
  const accepted = createHarness({ clock: () => beforeBoundary });
  const acceptedPreparation = await accepted.api.prepare({ endpoint: "/account/push" });
  beforeBoundary = 299999;

  assert.equal((await accepted.api.subscribe({ prepared: acceptedPreparation.handle })).status, "subscribed");

  let atBoundary = 0;
  const expired = createHarness({ clock: () => atBoundary });
  const expiredPreparation = await expired.api.prepare({ endpoint: "/account/push" });
  atBoundary = 300000;

  assert.throws(
    () => expired.api.subscribe({ prepared: expiredPreparation.handle }),
    error => error?.name === "TypeError");
});

test("subscribe invokes PushManager synchronously before its first await", async () => {
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  const harness = createHarness({ subscribePromise: pending });
  const prepared = await harness.api.prepare({ endpoint: "/account/push" });

  const operation = harness.api.subscribe({ prepared: prepared.handle });

  assert.equal(harness.subscribeCalled, true);
  release(harness.subscription);
  assert.equal((await operation).status, "subscribed");
  const put = harness.calls.find(call => call[0] === "fetch" && call[2]?.method === "PUT");
  assert.ok(put);
  assert.equal(put[2].headers.get("X-CSRF"), "token");
});

test("single flight does not consume the second handle", async () => {
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  const harness = createHarness({ subscribePromise: pending });
  const first = await harness.api.prepare({ endpoint: "/account/push" });
  const second = await harness.api.prepare({ endpoint: "/account/push" });

  const active = harness.api.subscribe({ prepared: first.handle });
  assert.equal((await harness.api.subscribe({ prepared: second.handle })).status, "operation-in-progress");
  release(harness.subscription);
  await active;
  assert.equal((await harness.api.subscribe({ prepared: second.handle })).status, "subscribed");
});

test("mismatched application key requires explicit two-action migration", async () => {
  const differentKey = Uint8Array.from(applicationKey);
  differentKey[64] ^= 1;
  const harness = createHarness({
    current: {
      endpoint: "https://push.example.test/send",
      options: { applicationServerKey: differentKey }
    }
  });

  const prepared = await harness.api.prepare({ endpoint: "/account/push" });

  assert.equal(prepared.status, "vapid-key-migration-required");
  assert.equal("handle" in prepared, false);
  assert.equal(harness.subscribeCalled, false);
});

test("unsubscribe removes server custody before browser subscription", async () => {
  const current = {
    endpoint: "https://push.example.test/send",
    options: { applicationServerKey: applicationKey },
    unsubscribe: async () => { order.push("browser"); return true; }
  };
  const order = [];
  const harness = createHarness({
    current,
    onFetch: (_url, init) => { if (init?.method === "DELETE") order.push("server"); }
  });

  const result = await harness.api.unsubscribe({ endpoint: "/account/push" });

  assert.equal(result.status, "unsubscribed");
  assert.deepEqual(order, ["server", "browser"]);
});

test("duplicate load is a no-op and incompatible namespace owners are preserved", () => {
  const harness = createHarness();
  const prepare = harness.api.prepare;
  const context = {
    URL, Headers, DOMException, Uint8Array,
    atob: value => Buffer.from(value, "base64").toString("binary"),
    btoa: value => Buffer.from(value, "binary").toString("base64"),
    console: { warn() {} }, document: { currentScript: { src: "https://app.example.test/_appsurface/pwa/push-client.js" } },
    navigator: { serviceWorker: {} }, Notification: {}, PushManager: function () {}, fetch: async () => {},
    window: { location: { origin: "https://app.example.test", href: "https://app.example.test/" }, AppSurface: { Pwa: harness.api } }
  };
  vm.runInNewContext(source, context);
  assert.equal(context.window.AppSurface.Pwa.prepare, prepare);

  for (const incompatible of ["host", { Pwa: "host" }]) {
    const warnings = [];
    const incompatibleContext = {
      console: { warn: code => warnings.push(code) },
      window: { AppSurface: incompatible }
    };
    vm.runInNewContext(source, incompatibleContext);
    assert.equal(incompatibleContext.window.AppSurface, incompatible);
    assert.deepEqual(warnings, ["ASPUSHJS002"]);
  }
});

test("a malformed live application key returns a stable stale-key result", async () => {
  const harness = createHarness({
    subscribePromise: Promise.resolve({
      endpoint: "https://push.example.test/send",
      options: {},
      toJSON: () => ({ endpoint: "https://push.example.test/send", keys: {} })
    })
  });
  const prepared = await harness.api.prepare({ endpoint: "/account/push" });

  assert.equal((await harness.api.subscribe({ prepared: prepared.handle })).status, "vapid-key-stale");
});

test("hostile endpoint projections return a stable stale-key result", async () => {
  const returned = {
    endpoint: "https://push.example.test/send",
    options: { applicationServerKey: applicationKey },
    toJSON: () => ({ endpoint: "https://push.example.test/send", keys: {} })
  };
  const hostileReturned = {
    ...returned,
    get endpoint() { throw new Error("secret returned endpoint getter failure"); }
  };
  const hostileLive = {
    ...returned,
    get endpoint() { throw new Error("secret live endpoint getter failure"); }
  };
  let reads = 0;
  for (const options of [
    { subscribePromise: Promise.resolve(hostileReturned) },
    {
      subscribePromise: Promise.resolve(returned),
      getSubscription: async () => ++reads === 1 ? null : hostileLive
    }
  ]) {
    const harness = createHarness(options);
    const prepared = await harness.api.prepare({ endpoint: "/account/push" });

    const outcome = await harness.api.subscribe({ prepared: prepared.handle });

    assert.equal(outcome.status, "vapid-key-stale");
    assert.equal(outcome.retryable, true);
  }
});

test("a rejected post-subscribe inspection returns a stable browser failure", async () => {
  let reads = 0;
  const harness = createHarness({
    getSubscription: async () => {
      reads++;
      if (reads > 1) throw new Error("hostile browser failure");
      return null;
    }
  });
  const prepared = await harness.api.prepare({ endpoint: "/account/push" });

  assert.equal((await harness.api.subscribe({ prepared: prepared.handle })).status, "browser-subscription-failed");
});

test("hostile subscription projection rejects with only a safe invariant", async () => {
  for (const toJSON of [
    () => { throw new Error("secret browser projection failure"); },
    () => ({
      get endpoint() { throw new Error("secret endpoint getter failure"); },
      keys: { p256dh: "public", auth: "secret" }
    })
  ]) {
    const harness = createHarness({
      subscribePromise: Promise.resolve({
        endpoint: "https://push.example.test/send",
        options: { applicationServerKey: applicationKey },
        toJSON
      })
    });
    const prepared = await harness.api.prepare({ endpoint: "/account/push" });

    await assert.rejects(
      harness.api.subscribe({ prepared: prepared.handle }),
      error => error?.name === "InvalidStateError"
        && error?.message === "ASPUSHJS009"
        && !String(error).includes("secret"));
  }
});

test("hostile current subscription key projection rejects with only a safe invariant", async () => {
  for (const current of [
    {
      endpoint: "https://push.example.test/send",
      get options() { throw new Error("secret options getter failure"); }
    },
    {
      endpoint: "https://push.example.test/send",
      options: {
        get applicationServerKey() { throw new Error("secret key getter failure"); }
      }
    }
  ]) {
    const harness = createHarness({ current });

    await assert.rejects(
      harness.api.prepare({ endpoint: "/account/push" }),
      error => error?.name === "InvalidStateError"
        && error?.message === "ASPUSHJS009"
        && !String(error).includes("secret"));
  }
});

test("408, 429, and 5xx responses are retryable across configuration and custody", async () => {
  for (const status of [408, 429, 500, 503]) {
    const failedConfiguration = createHarness({
      responseFor: url => url.endsWith("/configuration")
        ? { ok: false, status }
        : null
    });
    const configurationResult = await failedConfiguration.api.prepare({ endpoint: "/account/push" });
    assert.equal(configurationResult.status, "configuration-failed");
    assert.equal(configurationResult.retryable, true);

    const failedPut = createHarness({
      responseFor: (_url, init) => init?.method === "PUT"
        ? { ok: false, status, json: async () => ({}) }
        : null
    });
    const prepared = await failedPut.api.prepare({ endpoint: "/account/push" });
    const putResult = await failedPut.api.subscribe({ prepared: prepared.handle });
    assert.equal(putResult.status, "custody-failed");
    assert.equal(putResult.retryable, true);

    const current = {
      endpoint: "https://push.example.test/send",
      options: { applicationServerKey: applicationKey },
      unsubscribe: async () => true
    };
    const failedDelete = createHarness({
      current,
      responseFor: (_url, init) => init?.method === "DELETE"
        ? { ok: false, status }
        : null
    });
    const deleteResult = await failedDelete.api.unsubscribe({ endpoint: "/account/push" });
    assert.equal(deleteResult.status, "custody-failed");
    assert.equal(deleteResult.retryable, true);
  }
});

test("mutation-specific HTTP failures do not leak into configuration results", async () => {
  for (const status of [400, 409, 413, 415]) {
    const harness = createHarness({
      responseFor: url => url.endsWith("/configuration")
        ? { ok: false, status }
        : null
    });

    const outcome = await harness.api.prepare({ endpoint: "/account/push" });

    assert.equal(outcome.status, "configuration-failed");
    assert.equal(outcome.retryable, false);
  }
});

test("mutation 400 responses distinguish antiforgery from custody failures", async () => {
  for (const [code, expected] of [
    ["ASPUSH104", "antiforgery-failed"],
    ["ASPUSH100", "custody-failed"],
    ["ASPUSH101", "custody-failed"],
    [null, "custody-failed"]
  ]) {
    const response = () => ({
      ok: false,
      status: 400,
      json: async () => code === null ? Promise.reject(new Error("malformed")) : { code }
    });
    const failedPut = createHarness({
      responseFor: (_url, init) => init?.method === "PUT" ? response() : null
    });
    const prepared = await failedPut.api.prepare({ endpoint: "/account/push" });
    assert.equal((await failedPut.api.subscribe({ prepared: prepared.handle })).status, expected);

    const current = {
      endpoint: "https://push.example.test/send",
      options: { applicationServerKey: applicationKey },
      unsubscribe: async () => true
    };
    const failedDelete = createHarness({
      current,
      responseFor: (_url, init) => init?.method === "DELETE" ? response() : null
    });
    assert.equal((await failedDelete.api.unsubscribe({ endpoint: "/account/push" })).status, expected);
  }
});

test("cancellation after non-abortable browser awaits rejects with AbortError", async () => {
  const assertAbort = operation => assert.rejects(operation, error => error?.name === "AbortError");

  {
    const pending = deferred();
    const harness = createHarness({ register: () => pending.promise });
    const cancellation = new AbortController();
    const operation = harness.api.prepare({ endpoint: "/account/push", signal: cancellation.signal });
    cancellation.abort();
    pending.resolve(harness.registration);
    await assertAbort(operation);
  }

  {
    const pending = deferred();
    const harness = createHarness({ getSubscription: () => pending.promise });
    const cancellation = new AbortController();
    const operation = harness.api.prepare({ endpoint: "/account/push", signal: cancellation.signal });
    cancellation.abort();
    pending.resolve(null);
    await assertAbort(operation);
  }

  {
    const pending = deferred();
    const harness = createHarness({ subscribePromise: pending.promise });
    const prepared = await harness.api.prepare({ endpoint: "/account/push" });
    const cancellation = new AbortController();
    const operation = harness.api.subscribe({ prepared: prepared.handle, signal: cancellation.signal });
    cancellation.abort();
    pending.reject(new Error("browser rejected after abort"));
    await assertAbort(operation);
  }

  {
    const pending = deferred();
    let reads = 0;
    const harness = createHarness({
      getSubscription: () => ++reads === 1 ? Promise.resolve(null) : pending.promise
    });
    const prepared = await harness.api.prepare({ endpoint: "/account/push" });
    const cancellation = new AbortController();
    const operation = harness.api.subscribe({ prepared: prepared.handle, signal: cancellation.signal });
    cancellation.abort();
    pending.resolve(harness.subscription);
    await assertAbort(operation);
  }

  {
    const parsing = deferred();
    const started = deferred();
    const harness = createHarness({
      responseFor: (_url, init) => init?.method === "PUT" ? {
        ok: false,
        status: 400,
        json: () => {
          started.resolve();
          return parsing.promise;
        }
      } : null
    });
    const prepared = await harness.api.prepare({ endpoint: "/account/push" });
    const cancellation = new AbortController();
    const operation = harness.api.subscribe({ prepared: prepared.handle, signal: cancellation.signal });
    await started.promise;
    cancellation.abort();
    parsing.resolve({ code: "ASPUSH104" });
    await assertAbort(operation);
  }

  {
    const pending = deferred();
    const harness = createHarness({ register: () => pending.promise });
    const cancellation = new AbortController();
    const operation = harness.api.unsubscribe({ endpoint: "/account/push", signal: cancellation.signal });
    cancellation.abort();
    pending.resolve(harness.registration);
    await assertAbort(operation);
  }

  {
    const pending = deferred();
    const harness = createHarness({ getSubscription: () => pending.promise });
    const cancellation = new AbortController();
    const operation = harness.api.unsubscribe({ endpoint: "/account/push", signal: cancellation.signal });
    cancellation.abort();
    pending.resolve(harness.subscription);
    await assertAbort(operation);
  }

  {
    const pending = deferred();
    const current = {
      endpoint: "https://push.example.test/send",
      options: { applicationServerKey: applicationKey },
      unsubscribe: () => pending.promise
    };
    const harness = createHarness({ current });
    const cancellation = new AbortController();
    const operation = harness.api.unsubscribe({ endpoint: "/account/push", signal: cancellation.signal });
    await new Promise(resolve => setImmediate(resolve));
    cancellation.abort();
    pending.resolve(true);
    await assertAbort(operation);
  }

  {
    const parsing = deferred();
    const started = deferred();
    const current = {
      endpoint: "https://push.example.test/send",
      options: { applicationServerKey: applicationKey },
      unsubscribe: async () => true
    };
    const harness = createHarness({
      current,
      responseFor: (_url, init) => init?.method === "DELETE" ? {
        ok: false,
        status: 400,
        json: () => {
          started.resolve();
          return parsing.promise;
        }
      } : null
    });
    const cancellation = new AbortController();
    const operation = harness.api.unsubscribe({ endpoint: "/account/push", signal: cancellation.signal });
    await started.promise;
    cancellation.abort();
    parsing.reject(new Error("malformed after abort"));
    await assertAbort(operation);
  }
});

test("invalid and consumed handles throw TypeError", async () => {
  const harness = createHarness();
  assert.throws(() => harness.api.subscribe({ prepared: {} }), error => error?.name === "TypeError");
  const prepared = await harness.api.prepare({ endpoint: "/account/push" });
  await harness.api.subscribe({ prepared: prepared.handle });
  assert.throws(() => harness.api.subscribe({ prepared: prepared.handle }), error => error?.name === "TypeError");
});
