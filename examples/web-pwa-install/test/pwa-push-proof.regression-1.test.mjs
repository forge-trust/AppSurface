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
  dispatch(type) { return this.listeners.get(type)?.(); }
}

const settle = async () => {
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));
};

const createProof = ({ preparations, subscribe, unsubscribe }) => {
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
          prepare: async () => preparations[prepareCalls++] ?? preparations.at(-1),
          subscribe,
          unsubscribe
        }
      }
    }
  };

  vm.runInNewContext(source, context);
  return { elements, get prepareCalls() { return prepareCalls; } };
};

// Regression: ISSUE-001 — background preparation erased explicit subscribe feedback
// Found by /qa on 2026-07-15
// Report: .gstack/qa-reports/qa-report-127-0-0-1-6232-2026-07-15.md
test("background preparation preserves a denied subscribe outcome", async () => {
  const proof = createProof({
    preparations: [
      { status: "prepared", handle: { id: 1 } },
      { status: "network-failed" }
    ],
    subscribe: async () => ({ status: "permission-denied" }),
    unsubscribe: async () => ({ status: "already-unsubscribed" })
  });

  await settle();
  proof.elements["enable-push"].dispatch("click");
  await settle();

  assert.equal(proof.prepareCalls, 2);
  assert.equal(proof.elements["push-status"].dataset.state, "failed");
  assert.match(proof.elements["push-status"].textContent, /permission-denied/);
  assert.equal(proof.elements["push-permission-capability"].textContent, "Denied");
  assert.equal(proof.elements["enable-push"].disabled, true);
});

test("synchronous subscribe failures are contained and remain visible", async () => {
  const proof = createProof({
    preparations: [
      { status: "prepared", handle: { id: 1 } },
      { status: "prepared", handle: { id: 2 } }
    ],
    subscribe: () => { throw new TypeError("secret expired handle detail"); },
    unsubscribe: async () => ({ status: "already-unsubscribed" })
  });

  await settle();
  assert.doesNotThrow(() => proof.elements["enable-push"].dispatch("click"));
  await settle();

  assert.equal(proof.prepareCalls, 2);
  assert.match(proof.elements["push-status"].textContent, /safe browser invariant code/);
  assert.doesNotMatch(proof.elements["push-status"].textContent, /secret/);
  assert.equal(proof.elements["enable-push"].disabled, false);
});

test("background preparation preserves unsubscribe failures", async () => {
  const proof = createProof({
    preparations: [
      { status: "prepared", handle: { id: 1 } },
      { status: "forbidden" }
    ],
    subscribe: async () => ({ status: "subscribed" }),
    unsubscribe: async () => { throw new Error("secret unsubscribe detail"); }
  });

  await settle();
  await proof.elements["disable-push"].dispatch("click");

  assert.equal(proof.prepareCalls, 2);
  assert.match(proof.elements["push-status"].textContent, /unsubscribe action failed/);
  assert.doesNotMatch(proof.elements["push-status"].textContent, /secret/);
});

test("an absent browser subscription does not overclaim server custody removal", async () => {
  const proof = createProof({
    preparations: [
      { status: "prepared", handle: { id: 1 } },
      { status: "prepared", handle: { id: 2 } }
    ],
    subscribe: async () => ({ status: "subscribed" }),
    unsubscribe: async () => ({ status: "already-unsubscribed" })
  });

  await settle();
  await proof.elements["disable-push"].dispatch("click");

  assert.match(proof.elements["push-status"].textContent, /Server custody was not contacted/);
  assert.doesNotMatch(proof.elements["push-status"].textContent, /custody and browser subscription are removed/);
});

test("a completed unsubscribe reports server acceptance without claiming custody disposition", async () => {
  const proof = createProof({
    preparations: [
      { status: "prepared", handle: { id: 1 } },
      { status: "prepared", handle: { id: 2 } }
    ],
    subscribe: async () => ({ status: "subscribed" }),
    unsubscribe: async () => ({ status: "unsubscribed" })
  });

  await settle();
  await proof.elements["disable-push"].dispatch("click");

  assert.match(proof.elements["push-status"].textContent, /server accepted the custody-removal request/);
  assert.doesNotMatch(proof.elements["push-status"].textContent, /Server custody.*removed/);
});
