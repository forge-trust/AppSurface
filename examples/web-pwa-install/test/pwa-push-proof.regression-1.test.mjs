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
  dispatch(type) { this.listeners.get(type)?.(); }
}

const settle = async () => {
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));
};

// Regression: ISSUE-001 — background preparation erased explicit subscribe feedback
// Found by /qa on 2026-07-15
// Report: .gstack/qa-reports/qa-report-127-0-0-1-6232-2026-07-15.md
test("background preparation preserves a denied subscribe outcome", async () => {
  const elements = {
    "push-status": new FakeElement(),
    "push-actions": new FakeElement(),
    "enable-push": new FakeElement(),
    "disable-push": new FakeElement(),
    "fake-push-send": new FakeElement(),
    "push-permission-capability": new FakeElement(),
    "push-subscription-capability": new FakeElement("Not configured"),
    "push-sender-capability": new FakeElement(),
    "push-delivery-capability": new FakeElement()
  };
  let prepareCalls = 0;
  const context = {
    Notification: { permission: "denied" },
    document: { getElementById: id => elements[id] ?? null },
    window: {
      AppSurface: {
        Pwa: {
          prepare: async () => ({ status: "prepared", handle: { id: ++prepareCalls } }),
          subscribe: async () => ({ status: "permission-denied" }),
          unsubscribe: async () => ({ status: "already-unsubscribed" })
        }
      }
    }
  };

  vm.runInNewContext(source, context);
  await settle();
  elements["enable-push"].dispatch("click");
  await settle();

  assert.equal(prepareCalls, 2);
  assert.equal(elements["push-status"].dataset.state, "failed");
  assert.match(elements["push-status"].textContent, /permission-denied/);
  assert.equal(elements["push-permission-capability"].textContent, "Denied");
  assert.equal(elements["enable-push"].disabled, false);
});
