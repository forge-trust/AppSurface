import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';
import { transformSync } from 'esbuild';

const source = readFileSync(new URL('../assets/src/swr-render-guard.ts', import.meta.url), 'utf8');
const { code } = transformSync(source, { loader: 'ts', format: 'cjs', target: 'es2022' });

test('a fresh snapshot renders through the host-selected renderer and native effects', async () => {
  const h = harness();
  const frame = h.frame();
  h.document.addEventListener('turbo:before-frame-render', event => {
    event.detail.render = (current, incoming) => { current.content = `host:${incoming.content}`; };
  });
  await h.deliver(frame, h.request(frame), h.response('fresh'));
  assert.equal(frame.content, 'host:fresh');
  assert.deepEqual(frame.effects, ['preserve-enter', 'scroll', 'preserve-leave', 'focus', 'scripts']);
  assert.equal(frame.scriptRuns, 1);
});

for (const phase of ['body', 'commit']) {
  test(`a relevant live update during the ${phase} await survives the older snapshot`, async () => {
    const h = harness();
    const frame = h.frame();
    const request = h.request(frame);
    const gate = deferred();
    const response = h.response('old', { [phase]: gate.promise });
    h.document.addEventListener('turbo:before-frame-render', event => {
      event.detail.render = () => { assert.fail('stale snapshot must not invoke the host renderer'); };
    });
    const loading = h.deliver(frame, request, response);
    await (phase === 'body' ? response.entered.promise : response.selected.promise);
    const permanent = frame.permanent;
    h.mutate([frame], () => { frame.content = 'live'; });
    gate.resolve();
    await loading;
    assert.equal(frame.content, 'live');
    assert.equal(frame.permanent, permanent, 'discard must not enter permanent-node preservation');
    assert.deepEqual(frame.effects, [], 'discard must not scroll, focus, or reactivate current scripts');
    assert.equal(frame.scriptRuns, 0);
  });
}

for (const reason of ['invalidated', 'canceled']) {
  test(`an already ${reason} response cannot change the source URL or read its body`, async () => {
    const h = harness();
    const frame = h.frame();
    const request = h.request(frame);
    if (reason === 'canceled') request.controller.abort();
    else h.mutate([frame], () => { frame.content = 'live'; });
    const response = h.response('stale');
    Object.defineProperty(response, 'body', {
      get() { assert.fail('discarded response body must not be read'); }
    });
    await h.deliver(frame, request, response);
    assert.equal(frame.sourceUrl, '/initial');
    assert.deepEqual(frame.effects, []);
  });
}

test('multiple targets invalidate their frames while unrelated SWR and ordinary frames render', async () => {
  const h = harness();
  const frames = [h.frame(), h.frame(), h.frame(), h.frame(false)];
  const requests = frames.map(frame => h.request(frame));
  const targets = frames.slice(0, 2).map(frame => new FakeElement('span', frame));
  h.mutate(targets, () => { frames[0].content = frames[1].content = 'live'; });
  await Promise.all(frames.map((frame, i) => h.deliver(frame, requests[i], h.response(`snapshot-${i}`))));
  assert.deepEqual(frames.map(frame => frame.content), ['live', 'live', 'snapshot-2', 'snapshot-3']);
  assert.deepEqual(frames[0].effects, []);
  assert.deepEqual(frames[1].effects, []);
  assert.equal(frames[2].scriptRuns, 1);
  assert.equal(frames[3].scriptRuns, 1);
});

for (const trackInner of [false, true]) {
  test(`a nested live update invalidates its outer snapshot with inner request tracking ${trackInner}`, async () => {
    const h = harness();
    const outer = h.frame();
    const inner = h.frame();
    inner.parentElement = outer;
    const request = h.request(outer);
    if (trackInner) h.request(inner);
    h.document.addEventListener('turbo:before-frame-render', event => {
      if (event.target === outer) {
        // Model replacement of the enclosing frame's complete subtree.
        event.detail.render = (current, incoming) => {
          current.content = incoming.content;
          inner.content = incoming.content;
        };
      }
    });
    const target = new FakeElement('span', inner);
    h.mutate([target], () => { inner.content = 'live descendant'; });
    await h.deliver(outer, request, h.response('stale descendant'));
    assert.equal(inner.content, 'live descendant');
    assert.deepEqual(outer.effects, []);
    await h.deliver(outer, h.request(outer), h.response('fresh descendant'));
    assert.equal(inner.content, 'fresh descendant', 'a later authoritative outer snapshot remains eligible');
  });
}

test('a sibling insertion targeting an inner SWR frame invalidates the enclosing snapshot', async () => {
  const h = harness();
  const outer = h.frame();
  const inner = h.frame();
  inner.parentElement = outer;
  const request = h.request(outer);
  h.request(inner);
  h.mutate([inner], () => { outer.content = 'inner frame followed by live sibling'; });
  await h.deliver(outer, request, h.response('inner frame without sibling'));
  assert.equal(outer.content, 'inner frame followed by live sibling');
  assert.deepEqual(outer.effects, []);
});

test('an ancestor morph preserves a retained descendant live update without invalidating sibling frames', async () => {
  const h = harness();
  const root = new FakeElement('main');
  const ancestor = new FakeElement('section', root);
  const wrapper = new FakeElement('div', ancestor);
  const retained = h.frame(); // Deliberately nonpermanent: morph may retain this element and change its contents.
  retained.parentElement = wrapper;
  const unrelated = h.frame();
  unrelated.parentElement = root;
  const request = h.request(retained);
  const siblingRequest = h.request(unrelated);
  const gate = deferred();
  const response = h.response('old descendant', { body: gate.promise });
  const loading = h.deliver(retained, request, response);
  await response.entered.promise;

  h.mutate([ancestor], () => { retained.content = 'morphed live descendant'; });
  gate.resolve();
  await loading;
  await h.deliver(unrelated, siblingRequest, h.response('fresh unrelated sibling'));

  assert.equal(retained.parentElement, wrapper, 'the live morph retained the same descendant frame');
  assert.equal(retained.content, 'morphed live descendant');
  assert.deepEqual(retained.effects, []);
  assert.equal(unrelated.content, 'fresh unrelated sibling');
  assert.equal(unrelated.scriptRuns, 1);
  await h.deliver(retained, h.request(retained), h.response('later authoritative descendant'));
  assert.equal(retained.content, 'later authoritative descendant');
});

test('a canceled request without a response does not consume a later fresh response', async () => {
  const h = harness();
  const frame = h.frame();
  const abandoned = h.request(frame);
  abandoned.controller.abort();
  h.mutate([frame], () => { frame.content = 'live'; });
  await h.deliver(frame, h.request(frame), h.response('authoritative'));
  assert.equal(frame.content, 'authoritative');
  assert.equal(frame.scriptRuns, 1);
});

test('canceling an already received response skips only that render and allows the next request', async () => {
  const h = harness();
  const frame = h.frame();
  const request = h.request(frame);
  const gate = deferred();
  const response = h.response('canceled', { body: gate.promise });
  const loading = h.deliver(frame, request, response);
  await response.entered.promise;
  request.controller.abort();
  gate.resolve();
  await loading;
  assert.equal(frame.content, 'initial');
  assert.deepEqual(frame.effects, []);
  await h.deliver(frame, h.request(frame), h.response('fresh'));
  assert.equal(frame.content, 'fresh');
});

test('an older response arriving after a newer commit cannot roll the frame back', async () => {
  const h = harness();
  const frame = h.frame();
  const older = h.request(frame);
  const newer = h.request(frame);
  await h.deliver(frame, newer, h.response('newer'));
  const effects = [...frame.effects];
  await h.deliver(frame, older, h.response('older'));
  assert.equal(frame.content, 'newer');
  assert.deepEqual(frame.effects, effects);
});

test('queued responses retain separate request provenance while an earlier body is pending', async () => {
  const h = harness();
  const frame = h.frame();
  const older = h.request(frame);
  const gate = deferred();
  const oldResponse = h.response('older', { body: gate.promise });
  const oldLoad = h.deliver(frame, older, oldResponse);
  await oldResponse.entered.promise;
  h.mutate([frame], () => { frame.content = 'live'; });
  const freshLoad = h.deliver(frame, h.request(frame), h.response('fresh'));
  gate.resolve();
  await Promise.all([oldLoad, freshLoad]);
  assert.equal(frame.content, 'fresh');
  assert.equal(frame.scriptRuns, 1, 'only the fresh snapshot commits');
});

test('an untracked queued load does not inherit the stale request context', async () => {
  const h = harness();
  const frame = h.frame();
  const request = h.request(frame);
  const gate = deferred();
  const tracked = h.response('stale', { body: gate.promise });
  const loading = h.deliver(frame, request, tracked);
  await tracked.entered.promise;
  h.mutate([frame], () => { frame.content = 'live'; });
  const untracked = frame.delegate.loadResponse(h.response('untracked'));
  gate.resolve();
  await Promise.all([loading, untracked]);
  assert.equal(frame.content, 'untracked');
  assert.equal(frame.scriptRuns, 1);
});

test('a rejected body releases the frame queue and its context for a later snapshot', async () => {
  const h = harness();
  const frame = h.frame();
  const failed = h.response('unused', { error: new Error('body failed') });
  await assert.rejects(h.deliver(frame, h.request(frame), failed), /body failed/);
  await h.deliver(frame, h.request(frame), h.response('recovered'));
  assert.equal(frame.content, 'recovered');
  assert.equal(frame.scriptRuns, 1);
});

test('HTTP failure responses retain the same stale-snapshot protection', async () => {
  const h = harness();
  const frame = h.frame();
  const request = h.request(frame);
  h.mutate([frame], () => { frame.content = 'live'; });
  await h.deliver(frame, request, h.response('old error page'), true);
  assert.equal(frame.content, 'live');
  assert.deepEqual(frame.effects, []);
});

test('a snapshot cannot commit during an asynchronous stream render, even if requested after it began', async () => {
  const h = harness();
  const frame = h.frame();
  h.request(frame); // Establish a real frame request before the stream starts.
  const finish = h.beginMutation({ target: 'resolved', targetElements: [frame] });
  try {
    await h.deliver(frame, h.request(frame), h.response('overlapping'));
    assert.equal(frame.content, 'initial');
    assert.deepEqual(frame.effects, []);
    frame.content = 'async-live';
  } finally { finish(); }
  await h.deliver(frame, h.request(frame), h.response('later-authoritative'));
  assert.equal(frame.content, 'later-authoritative');
});

test('a request begun during a stream render stays stale after that render finishes', async () => {
  const h = harness();
  const frame = h.frame();
  h.request(frame);
  const finish = h.beginMutation({ target: 'resolved', targetElements: [frame] });
  const request = h.request(frame);
  frame.content = 'live';
  finish();
  await h.deliver(frame, request, h.response('overlapping snapshot'));
  assert.equal(frame.content, 'live');
  assert.deepEqual(frame.effects, []);
});

test('a disconnected frame cannot apply a received snapshot or its effects', async () => {
  const h = harness();
  const frame = h.frame();
  const request = h.request(frame);
  frame.isConnected = false;
  await h.deliver(frame, request, h.response('detached snapshot'));
  assert.equal(frame.content, 'initial');
  assert.deepEqual(frame.effects, []);
});

function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}

class FakeElement {
  constructor(tagName, parentElement = null, attributes = []) {
    this.tagName = tagName;
    this.children = [];
    this.parentElement = parentElement;
    this.attributes = new Set(attributes);
    this.isConnected = true;
  }

  get parentElement() { return this.parent || null; }

  set parentElement(parent) {
    if (this.parent === parent) return;
    if (this.parent) {
      const index = this.parent.children.indexOf(this);
      if (index !== -1) this.parent.children.splice(index, 1);
    }
    this.parent = parent;
    if (parent) parent.children.push(this);
  }

  matches(selector) {
    return this.tagName === 'turbo-frame'
      && this.attributes.has('data-rw-swr')
      && (!selector.includes('[src]') || this.attributes.has('src'));
  }

  closest(selector) {
    for (let element = this; element; element = element.parentElement) {
      if (element.matches(selector)) return element;
    }
    return null;
  }

  querySelectorAll(selector) {
    const matches = [];
    for (const child of this.children) {
      if (child.matches(selector)) matches.push(child);
      matches.push(...child.querySelectorAll(selector));
    }
    return matches;
  }
}

function harness() {
  const listeners = new Map();
  const document = {
    addEventListener(type, callback) {
      const callbacks = listeners.get(type) || [];
      callbacks.push(callback);
      listeners.set(type, callbacks);
    },
    dispatchEvent(event) {
      for (const callback of listeners.get(event.type) || []) callback(event);
    }
  };
  const module = { exports: {} };
  const context = vm.createContext({ document, Element: FakeElement, module, exports: module.exports });
  vm.runInContext(code, context, { filename: 'swr-render-guard.cjs' });
  const beginMutation = module.exports.installSwrRenderGuard();

  function frame(swr = true) {
    const frame = new FakeElement('turbo-frame', null, swr ? ['data-rw-swr', 'src'] : ['src']);
    Object.assign(frame, { content: 'initial', sourceUrl: '/initial', effects: [], scriptRuns: 0, permanent: {} });
    frame.delegate = {
      view: {},
      async requestSucceededWithResponse(request, response) { return await this.loadResponse(response); },
      async requestFailedWithResponse(request, response) { return await this.loadResponse(response); },
      allowsImmediateRender({ element }, options) {
        const event = { type: 'turbo:before-frame-render', target: frame, detail: { newFrame: element, ...options } };
        document.dispatchEvent(event);
        this.view.renderer.renderElement = event.detail.render;
        return true;
      },
      async loadResponse(response) {
        frame.sourceUrl = response.url;
        response.entered.resolve();
        await response.body;
        if (response.error) throw response.error;
        const renderer = {
          renderElement(current, incoming) { current.content = incoming.content; },
          async preservingPermanentElements(callback) {
            frame.effects.push('preserve-enter');
            // A fake permanent-node move is observable; discarded renders must never enter here.
            frame.permanent = { moved: frame.permanent };
            await callback();
            frame.effects.push('preserve-leave');
          },
          scrollFrameIntoView() { frame.effects.push('scroll'); },
          focusFirstAutofocusableElement() { frame.effects.push('focus'); },
          activateScriptElements() { frame.effects.push('scripts'); frame.scriptRuns++; }
        };
        this.view.renderer = renderer;
        this.allowsImmediateRender({ element: response }, { render: renderer.renderElement });
        response.selected.resolve();
        await response.commit; // The repaint gap after allowsImmediateRender.
        // Pinned FrameRenderer does NOT await preservingPermanentElements.
        renderer.preservingPermanentElements(() => renderer.renderElement(frame, response));
        renderer.scrollFrameIntoView();
        await Promise.resolve();
        renderer.focusFirstAutofocusableElement();
        await Promise.resolve();
        renderer.activateScriptElements();
      }
    };
    return frame;
  }

  function request(frame) {
    const controller = new AbortController();
    const fetchOptions = { method: 'GET', signal: controller.signal };
    document.dispatchEvent({ type: 'turbo:before-fetch-request', target: frame, detail: { fetchOptions } });
    return { fetchOptions, controller };
  }

  return {
    document, frame, request, beginMutation,
    response(content, options = {}) {
      return { response: {}, content, url: `/${encodeURIComponent(content)}`, entered: deferred(), selected: deferred(), ...options };
    },
    deliver(frame, request, response, failed = false) {
      return frame.delegate[failed ? 'requestFailedWithResponse' : 'requestSucceededWithResponse'](request, response);
    },
    mutate(targets, action) {
      const finish = beginMutation({ target: 'resolved', targetElements: targets });
      try { action(); } finally { finish?.(); }
    }
  };
}

for (const action of ['rw-dialog', 'rw-visit', 'refresh']) {
  test(`targetless ${action} keeps its custom renderer and does not invalidate unrelated frames`, async () => {
    const h = harness();
    const frame = h.frame();
    const request = h.request(frame);
    const stream = {
      get target() { return null; },
      get targets() { return null; },
      getAttribute() { return null; },
      hasAttribute() { return false; },
      get targetElements() { throw new Error('target or targets attribute is missing'); }
    };
    let renders = 0;
    const finish = h.beginMutation(stream);
    try { renders++; } finally { finish?.(); }
    await h.deliver(frame, request, h.response('fresh'));
    assert.equal(renders, 1);
    assert.equal(frame.content, 'fresh');
    assert.equal(frame.scriptRuns, 1);
  });
}
