import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";

const source = await readFile(new URL("../wwwroot/js/pwa-push-proof.js", import.meta.url), "utf8");

class FakeElement {
  constructor(textContent = "") {
    this.className = "";
    this.dataset = {};
    this.disabled = false;
    this.listeners = new Map();
    this.textContent = textContent;
  }

  addEventListener(type, listener) { this.listeners.set(type, listener); }
  setAttribute() {}
  async dispatch(type) { await this.listeners.get(type)?.(); }
}

const settle = async () => {
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));
};

const createHostActionHarness = ({ configurationOk = true, actionOk = true } = {}) => {
  const elements = {
    "push-status": new FakeElement(),
    "push-actions": new FakeElement(),
    "enable-push": new FakeElement(),
    "disable-push": new FakeElement(),
    "fake-push-send": new FakeElement(),
    "push-permission-capability": new FakeElement(),
    "push-subscription-capability": new FakeElement("Enabled"),
    "push-sender-capability": new FakeElement("Not run"),
    "push-delivery-capability": new FakeElement("Not proven")
  };
  const calls = [];
  const context = {
    Notification: { permission: "granted" },
    document: { getElementById: id => elements[id] ?? null },
    fetch: async (url, init = {}) => {
      calls.push([String(url), init]);
      if (String(url).endsWith("/configuration")) {
        return {
          ok: configurationOk,
          json: async () => ({ antiforgery: { headerName: "X-CSRF", requestToken: "proof-token" } })
        };
      }

      return {
        ok: actionOk,
        json: async () => ({ senderClassification: "Accepted", message: "Safe classification." })
      };
    },
    window: {
      AppSurface: {
        Pwa: {
          prepare: async () => ({ status: "prepared", handle: {} }),
          subscribe: async () => ({ status: "subscribed" }),
          unsubscribe: async () => ({ status: "unsubscribed" })
        }
      }
    }
  };

  vm.runInNewContext(source, context);
  return { calls, elements };
};

test("example host action uses a protected POST with the package antiforgery contract", async () => {
  const { calls, elements } = createHostActionHarness();
  await settle();
  await elements["fake-push-send"].dispatch("click");

  assert.equal(calls.length, 2);
  assert.equal(calls[0][0], "/account/push-subscriptions/configuration");
  assert.equal(calls[1][0], "/account/push-subscriptions/host-action-proof");
  assert.equal(calls[1][1].method, "POST");
  assert.equal(calls[1][1].credentials, "same-origin");
  assert.equal(calls[1][1].headers["X-CSRF"], "proof-token");
});

test("example host action reports a rejected configuration without attempting the action", async () => {
  const { calls, elements } = createHostActionHarness({ configurationOk: false });
  await settle();

  await elements["fake-push-send"].dispatch("click");

  assert.equal(calls.length, 1);
  assert.equal(elements["push-status"].dataset.state, "failed");
  assert.equal(elements["push-sender-capability"].textContent, "Not run");
});

test("example host action reports a rejected protected action without claiming delivery", async () => {
  const { calls, elements } = createHostActionHarness({ actionOk: false });
  await settle();

  await elements["fake-push-send"].dispatch("click");

  assert.equal(calls.length, 2);
  assert.equal(elements["push-status"].dataset.state, "failed");
  assert.equal(elements["push-sender-capability"].textContent, "Not run");
  assert.equal(elements["push-delivery-capability"].textContent, "Not proven");
});
