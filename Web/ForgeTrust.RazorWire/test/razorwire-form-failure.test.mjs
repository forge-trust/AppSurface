import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const runtimePath = new URL('../wwwroot/razorwire/razorwire.js', import.meta.url);
const pageNavigationPath = new URL('../wwwroot/razorwire/page-navigation.js', import.meta.url);

test('runtime merges config and exposes formFailureManager', () => {
  const { context, document } = loadRuntime();

  assert.equal(context.window.RazorWire.config.existing, true);
  assert.equal(context.window.RazorWire.config.developmentDiagnostics, true);
  assert.equal(context.window.RazorWire.config.failureUxEnabled, true);
  assert.equal(context.window.RazorWire.config.failureMode, 'auto');
  assert.ok(context.window.RazorWire.formFailureManager);
  assert.equal(context.window.RazorWire.pageNavigationManager, undefined);
  assert.equal(document.head.querySelectorAll('#rw-form-failure-default-styles').length, 1);
});

test('before fetch marks RazorWire form requests with the durable header', () => {
  const { document } = loadRuntime();
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  document.body.appendChild(form);

  const event = {
    type: 'turbo:before-fetch-request',
    target: form,
    detail: { fetchOptions: { headers: {} } }
  };
  document.dispatchEvent(event);

  assert.equal(event.detail.fetchOptions.headers['X-RazorWire-Form'], 'true');
});

test('before fetch supports Headers-like request headers', () => {
  const { document } = loadRuntime();
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  document.body.appendChild(form);

  const headers = new FakeHeaders();
  document.dispatchEvent({
    type: 'turbo:before-fetch-request',
    target: form,
    detail: { fetchOptions: { headers } }
  });

  assert.equal(headers.get('X-RazorWire-Form'), 'true');
});

test('stream connections include credentials for configured live origin', () => {
  const { context, document } = loadRuntime({
    liveOrigin: 'https://api.example.test',
    hybridCredentials: 'include'
  });
  const stream = new FakeElement('rw-stream-source');
  stream.setAttribute('src', 'https://api.example.test/rw/stream');
  document.body.appendChild(stream);

  context.window.RazorWire.connectionManager.scan();

  const source = context.window.RazorWire.connectionManager.sources.get('https://api.example.test/rw/stream');
  assert.equal(source.es.options.withCredentials, true);
});

test('stream connections include credentials for auto mode with configured live origin', () => {
  const { context, document } = loadRuntime({
    liveOrigin: 'https://api.example.test',
    hybridCredentials: 'auto'
  });
  const stream = new FakeElement('rw-stream-source');
  stream.setAttribute('src', 'https://api.example.test/rw/stream');
  document.body.appendChild(stream);

  context.window.RazorWire.connectionManager.scan();

  const source = context.window.RazorWire.connectionManager.sources.get('https://api.example.test/rw/stream');
  assert.equal(source.es.options.withCredentials, true);
});

test('runtime normalizes live origin before matching hybrid credentials', () => {
  const { context, document } = loadRuntime({
    liveOrigin: ' https://api.example.test/ ',
    hybridCredentials: 'auto'
  });
  const stream = new FakeElement('rw-stream-source');
  stream.setAttribute('src', 'https://api.example.test/rw/stream');
  document.body.appendChild(stream);

  context.window.RazorWire.connectionManager.scan();

  assert.equal(context.window.RazorWire.config.liveOrigin, 'https://api.example.test');
  const source = context.window.RazorWire.connectionManager.sources.get('https://api.example.test/rw/stream');
  assert.equal(source.es.options.withCredentials, true);
});

test('runtime ignores invalid live origin for hybrid credentials', () => {
  const { context, document } = loadRuntime({
    liveOrigin: 'https://api.example.test/forms',
    hybridCredentials: 'include'
  });
  const stream = new FakeElement('rw-stream-source');
  stream.setAttribute('src', 'https://api.example.test/rw/stream');
  document.body.appendChild(stream);

  context.window.RazorWire.connectionManager.scan();

  assert.equal(context.window.RazorWire.config.liveOrigin, '');
  const source = context.window.RazorWire.connectionManager.sources.get('https://api.example.test/rw/stream');
  assert.equal(source.es.options.withCredentials, undefined);
});

test('before fetch lazily refreshes antiforgery token before submit resumes', async () => {
  const fetches = [];
  const { document } = loadRuntime({
    liveOrigin: 'https://api.example.test',
    hybridCredentials: 'include',
    antiforgeryEndpoint: '/tokens/antiforgery',
    fetch: async (url, options) => {
      fetches.push({ url, options });
      return {
        ok: true,
        json: async () => ({
          formFieldName: '__RequestVerificationToken',
          requestToken: 'runtime-token',
          headerName: 'RequestVerificationToken'
        })
      };
    }
  });
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  form.setAttribute('data-rw-antiforgery', 'lazy');
  form.setAttribute('action', 'https://api.example.test/profile/save');
  document.body.appendChild(form);

  let resumed = false;
  const event = {
    type: 'turbo:before-fetch-request',
    target: form,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    },
    detail: {
      fetchOptions: { headers: {} },
      resume: () => {
        resumed = true;
      }
    }
  };
  document.dispatchEvent(event);
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(event.defaultPrevented, true);
  assert.equal(event.detail.fetchOptions.credentials, 'include');
  assert.equal(fetches.length, 1);
  assert.equal(fetches[0].url, 'https://api.example.test/tokens/antiforgery');
  assert.equal(fetches[0].options.credentials, 'include');
  assert.equal(form.getAttribute('data-rw-antiforgery-state'), 'ready');
  assert.equal(form.children.find(child => child.name === '__RequestVerificationToken')?.value, 'runtime-token');
  assert.equal(resumed, true);
});

test('lazy antiforgery token fetch includes credentials for auto mode with live origin', async () => {
  const fetches = [];
  const { document } = loadRuntime({
    liveOrigin: 'https://api.example.test',
    hybridCredentials: 'auto',
    fetch: async (url, options) => {
      fetches.push({ url, options });
      return {
        ok: true,
        json: async () => ({
          formFieldName: '__RequestVerificationToken',
          requestToken: 'runtime-token'
        })
      };
    }
  });
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  form.setAttribute('data-rw-antiforgery', 'lazy');
  form.setAttribute('action', 'https://api.example.test/profile/save');
  document.body.appendChild(form);

  document.dispatchEvent({
    type: 'focusin',
    target: form
  });
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(fetches.length, 1);
  assert.equal(fetches[0].options.credentials, 'include');
  assert.equal(form.getAttribute('data-rw-antiforgery-state'), 'ready');
});

test('lazy antiforgery token refresh runs when form failure ux is disabled', async () => {
  const fetches = [];
  const { document } = loadRuntime({
    formFailureEnabled: 'false',
    failureMode: 'off',
    fetch: async (url, options) => {
      fetches.push({ url, options });
      return {
        ok: true,
        json: async () => ({
          formFieldName: '__RequestVerificationToken',
          requestToken: 'runtime-token'
        })
      };
    }
  });
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'off');
  form.setAttribute('data-rw-antiforgery', 'lazy');
  document.body.appendChild(form);

  let resumed = false;
  const event = {
    type: 'turbo:before-fetch-request',
    target: form,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    },
    detail: {
      fetchOptions: { headers: {} },
      resume: () => {
        resumed = true;
      }
    }
  };
  document.dispatchEvent(event);
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(event.defaultPrevented, true);
  assert.equal(event.detail.fetchOptions.headers['X-RazorWire-Form'], 'true');
  assert.equal(fetches.length, 1);
  assert.equal(fetches[0].options.credentials, 'same-origin');
  assert.equal(form.children.find(child => child.name === '__RequestVerificationToken')?.value, 'runtime-token');
  assert.equal(form.getAttribute('data-rw-antiforgery-state'), 'ready');
  assert.equal(resumed, true);
  assert.equal(document.head.querySelectorAll('#rw-form-failure-default-styles').length, 0);
});

test('intent lazy antiforgery failure dispatches when form failure ux is enabled', async () => {
  const { document } = loadRuntime({
    fetch: async () => {
      throw new Error('token endpoint offline');
    }
  });
  const events = [];
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  form.setAttribute('data-rw-antiforgery', 'lazy');
  form.dispatchEvent = event => {
    events.push(event);
    return !event.defaultPrevented;
  };
  document.body.appendChild(form);

  document.dispatchEvent({
    type: 'focusin',
    target: form
  });
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(form.getAttribute('data-rw-antiforgery-state'), 'failed');
  assert.equal(events.some(event => event.type === 'razorwire:form:failure'), true);
});

test('intent lazy antiforgery failure is quiet when form failure ux is disabled', async () => {
  const { document } = loadRuntime({
    fetch: async () => {
      throw new Error('token endpoint offline');
    }
  });
  const events = [];
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'off');
  form.setAttribute('data-rw-antiforgery', 'lazy');
  form.dispatchEvent = event => {
    events.push(event);
    return !event.defaultPrevented;
  };
  document.body.appendChild(form);

  document.dispatchEvent({
    type: 'focusin',
    target: form
  });
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(form.getAttribute('data-rw-antiforgery-state'), 'failed');
  assert.equal(events.some(event => event.type === 'razorwire:form:failure'), false);
  assert.equal(form.querySelectorAll('[data-rw-form-error-generated="true"]').length, 0);
});

test('before fetch patches paused request body and header with refreshed antiforgery token', async () => {
  const fetches = [];
  const { document } = loadRuntime({
    fetch: async (url, options) => {
      fetches.push({ url, options });
      return {
        ok: true,
        json: async () => ({
          formFieldName: 'csrf-token',
          requestToken: 'runtime-token',
          headerName: 'X-CSRF-TOKEN'
        })
      };
    }
  });
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  form.setAttribute('data-rw-antiforgery', 'lazy');
  form.setAttribute('action', '/profile/save');
  document.body.appendChild(form);

  const body = new URLSearchParams('name=Andrew');
  const headers = new FakeHeaders();
  let resumed = false;
  const event = {
    type: 'turbo:before-fetch-request',
    target: form,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    },
    detail: {
      fetchOptions: { headers, body },
      resume: () => {
        resumed = true;
      }
    }
  };
  document.dispatchEvent(event);
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(fetches.length, 1);
  assert.equal(event.defaultPrevented, true);
  assert.equal(headers.get('X-CSRF-TOKEN'), 'runtime-token');
  assert.equal(body.get('csrf-token'), 'runtime-token');
  assert.equal(form.children.find(child => child.name === 'csrf-token')?.value, 'runtime-token');
  assert.equal(resumed, true);
});

test('before fetch leaves non-form string bodies unchanged while setting antiforgery header', async () => {
  const { document } = loadRuntime({
    fetch: async () => ({
      ok: true,
      json: async () => ({
        formFieldName: 'csrf-token',
        requestToken: 'runtime-token',
        headerName: 'X-CSRF-TOKEN'
      })
    })
  });
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  form.setAttribute('data-rw-antiforgery', 'lazy');
  form.setAttribute('action', '/profile/save');
  document.body.appendChild(form);

  const body = JSON.stringify({ name: 'Andrew' });
  const headers = new FakeHeaders();
  headers.set('content-type', 'application/json');
  let resumed = false;
  const event = {
    type: 'turbo:before-fetch-request',
    target: form,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    },
    detail: {
      fetchOptions: { headers, body },
      resume: () => {
        resumed = true;
      }
    }
  };
  document.dispatchEvent(event);
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(event.detail.fetchOptions.body, body);
  assert.equal(headers.get('X-CSRF-TOKEN'), 'runtime-token');
  assert.equal(form.children.find(child => child.name === 'csrf-token')?.value, 'runtime-token');
  assert.equal(resumed, true);
});

test('before fetch patches form-url-encoded string bodies with refreshed antiforgery token', async () => {
  const { document } = loadRuntime({
    fetch: async () => ({
      ok: true,
      json: async () => ({
        formFieldName: 'csrf-token',
        requestToken: 'runtime-token',
        headerName: 'X-CSRF-TOKEN'
      })
    })
  });
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  form.setAttribute('data-rw-antiforgery', 'lazy');
  form.setAttribute('action', '/profile/save');
  document.body.appendChild(form);

  const headers = new FakeHeaders();
  headers.set('content-type', 'application/x-www-form-urlencoded; charset=UTF-8');
  const event = {
    type: 'turbo:before-fetch-request',
    target: form,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    },
    detail: {
      fetchOptions: { headers, body: 'name=Andrew' },
      resume: () => {
      }
    }
  };
  document.dispatchEvent(event);
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(event.detail.fetchOptions.body, 'name=Andrew&csrf-token=runtime-token');
  assert.equal(headers.get('X-CSRF-TOKEN'), 'runtime-token');
});

test('lazy antiforgery token refresh is shared between intent and submit', async () => {
  const fetches = [];
  let resolveFetch;
  const { document } = loadRuntime({
    fetch: async (url, options) => {
      fetches.push({ url, options });
      return new Promise(resolve => {
        resolveFetch = () => resolve({
          ok: true,
          json: async () => ({
            formFieldName: '__RequestVerificationToken',
            requestToken: 'shared-token'
          })
        });
      });
    }
  });
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  form.setAttribute('data-rw-antiforgery', 'lazy');
  form.setAttribute('action', '/profile/save');
  document.body.appendChild(form);

  document.dispatchEvent({
    type: 'focusin',
    target: form
  });

  let resumed = false;
  const event = {
    type: 'turbo:before-fetch-request',
    target: form,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    },
    detail: {
      fetchOptions: { headers: {} },
      resume: () => {
        resumed = true;
      }
    }
  };
  document.dispatchEvent(event);

  assert.equal(fetches.length, 1);
  assert.equal(event.defaultPrevented, true);

  resolveFetch();
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.equal(fetches.length, 1);
  assert.equal(form.children.find(child => child.name === '__RequestVerificationToken')?.value, 'shared-token');
  assert.equal(resumed, true);
});

test('lazy antiforgery refresh failure clears active submit state', async () => {
  const { document } = loadRuntime({
    fetch: async () => {
      throw new Error('token endpoint offline');
    }
  });
  const events = [];
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  form.setAttribute('data-rw-antiforgery', 'lazy');
  form.dispatchEvent = event => {
    events.push(event);
    return !event.defaultPrevented;
  };
  const button = new FakeElement('button');
  form.appendChild(button);
  document.body.appendChild(form);

  document.dispatchEvent({
    type: 'turbo:submit-start',
    target: form,
    detail: { formSubmission: { submitter: button } }
  });

  assert.equal(form.getAttribute('data-rw-submitting'), 'true');
  assert.equal(button.disabled, true);

  const event = {
    type: 'turbo:before-fetch-request',
    target: form,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    },
    detail: {
      fetchOptions: { headers: {} },
      resume: () => {}
    }
  };
  document.dispatchEvent(event);
  await new Promise(resolve => setTimeout(resolve, 0));

  const submitEnd = events.find(item => item.type === 'razorwire:form:submit-end');
  assert.equal(event.defaultPrevented, true);
  assert.equal(form.hasAttribute('data-rw-submitting'), false);
  assert.equal(form.hasAttribute('aria-busy'), false);
  assert.equal(form.getAttribute('data-rw-submit-status'), 'failed');
  assert.equal(button.disabled, false);
  assert.equal(submitEnd.detail.success, false);
  assert.equal(submitEnd.detail.submitter, button);
});

test('submit lifecycle disables only RazorWire-owned submitter state and restores it', () => {
  const { document } = loadRuntime();
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  const button = new FakeElement('button');
  form.appendChild(button);
  document.body.appendChild(form);

  document.dispatchEvent({
    type: 'turbo:submit-start',
    target: form,
    detail: { formSubmission: { submitter: button } }
  });

  assert.equal(form.getAttribute('data-rw-submitting'), 'true');
  assert.equal(form.getAttribute('aria-busy'), 'true');
  assert.equal(button.disabled, true);
  assert.equal(button.getAttribute('data-rw-submit-disabled-by-razorwire'), 'true');

  document.dispatchEvent({
    type: 'turbo:submit-end',
    target: form,
    detail: {
      success: true,
      formSubmission: { submitter: button },
      fetchResponse: response(200, {})
    }
  });

  assert.equal(form.hasAttribute('data-rw-submitting'), false);
  assert.equal(form.hasAttribute('aria-busy'), false);
  assert.equal(button.disabled, false);
});

test('failed submit renders one scoped fallback block and injects styles once', () => {
  const { document } = loadRuntime();
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  document.body.appendChild(form);

  document.dispatchEvent({
    type: 'turbo:submit-start',
    target: form,
    detail: { formSubmission: { submitter: null } }
  });
  document.dispatchEvent({
    type: 'turbo:submit-end',
    target: form,
    detail: {
      success: false,
      formSubmission: { submitter: null },
      fetchResponse: response(500, { 'content-type': 'text/html' })
    }
  });
  document.dispatchEvent({
    type: 'turbo:submit-end',
    target: form,
    detail: {
      success: false,
      formSubmission: { submitter: null },
      fetchResponse: response(500, { 'content-type': 'text/html' })
    }
  });

  assert.equal(form.querySelectorAll('[data-rw-form-error-generated="true"]').length, 1);
  assert.equal(document.head.querySelectorAll('#rw-form-failure-default-styles').length, 1);
  assert.equal(form.getAttribute('data-rw-submit-status'), 'failed');
  assert.equal(form.getAttribute('data-rw-last-status'), '500');
});

test('handled failures clear generated fallback instead of duplicating server UI', () => {
  const { document } = loadRuntime();
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  document.body.appendChild(form);

  assert.equal(windowHandled(response(422, { 'X-RazorWire-Form-Handled': 'true' })), true);
  assert.equal(windowHandled(response(422, { 'X-RazorWire-Form-Handled': '1' })), true);

  document.dispatchEvent({
    type: 'turbo:submit-end',
    target: form,
    detail: {
      success: false,
      formSubmission: { submitter: null },
      fetchResponse: response(500, { 'content-type': 'text/html' })
    }
  });

  assert.equal(form.querySelectorAll('[data-rw-form-error-generated="true"]').length, 1);

  document.dispatchEvent({
    type: 'turbo:submit-end',
    target: form,
    detail: {
      success: false,
      formSubmission: { submitter: null },
      fetchResponse: response(422, { 'X-RazorWire-Form-Handled': '1', 'content-type': 'text/vnd.turbo-stream.html' })
    }
  });

  assert.equal(form.querySelectorAll('[data-rw-form-error-generated="true"]').length, 0);
  assert.equal(form.getAttribute('data-rw-submit-status'), 'failed');
});

test('manual failures expose development diagnostic without rendering fallback', () => {
  const { document } = loadRuntime();
  const events = [];
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'manual');
  form.dispatchEvent = event => {
    events.push(event);
    return !event.defaultPrevented;
  };
  document.body.appendChild(form);

  document.dispatchEvent({
    type: 'turbo:submit-end',
    target: form,
    detail: {
      success: false,
      formSubmission: { submitter: null },
      fetchResponse: response(500, { 'content-type': 'text/html' })
    }
  });

  const failure = events.find(event => event.type === 'razorwire:form:failure');
  assert.equal(failure.detail.developmentDiagnostic.title, 'RazorWire form submission failed');
  assert.equal(failure.detail.developmentDiagnostic.statusCode, 500);
  assert.equal(form.querySelectorAll('[data-rw-form-error-generated="true"]').length, 0);
});

test('network failures dispatch submit end lifecycle event', () => {
  const { document } = loadRuntime();
  const events = [];
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  form.dispatchEvent = event => {
    events.push(event);
    return !event.defaultPrevented;
  };
  document.body.appendChild(form);

  document.dispatchEvent({
    type: 'turbo:fetch-request-error',
    target: form,
    detail: {}
  });

  const submitEnd = events.find(event => event.type === 'razorwire:form:submit-end');
  assert.equal(submitEnd.detail.success, false);
  assert.equal(submitEnd.detail.statusCode, null);
  assert.equal(submitEnd.detail.handled, false);
});

test('global disabled failure UX ignores stale form-level auto markup', () => {
  const { document } = loadRuntime({ formFailureEnabled: 'false', failureMode: 'off' });
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');
  const button = new FakeElement('button');
  form.appendChild(button);
  document.body.appendChild(form);

  const beforeFetch = {
    type: 'turbo:before-fetch-request',
    target: form,
    detail: { fetchOptions: { headers: {} } }
  };
  document.dispatchEvent(beforeFetch);
  document.dispatchEvent({
    type: 'turbo:submit-start',
    target: form,
    detail: { formSubmission: { submitter: button } }
  });

  assert.equal(beforeFetch.detail.fetchOptions.headers['X-RazorWire-Form'], 'true');
  assert.equal(form.hasAttribute('data-rw-submitting'), false);
  assert.equal(button.disabled, false);
  assert.equal(document.head.querySelectorAll('#rw-form-failure-default-styles').length, 0);
});

test('legacy runtime mode off also disables stale form-level auto markup', () => {
  const { context } = loadRuntime({ omitFormFailureEnabled: true, failureMode: 'off' });
  const form = new FakeForm();
  form.setAttribute('data-rw-form', 'true');
  form.setAttribute('data-rw-form-failure', 'auto');

  assert.equal(context.window.RazorWire.config.failureUxEnabled, false);
  assert.equal(context.window.RazorWire.formFailureManager.isRazorWireForm(form), false);
});

test('rw-visit stream action visits same-origin urls with configured action', () => {
  const { turbo } = loadRuntimeWithVisitAction({ windowHref: 'https://example.test/docs/current' });

  const stream = new FakeElement('turbo-stream');
  stream.setAttribute('url', '../next?tab=done#summary');
  stream.setAttribute('visit-action', 'replace');

  turbo.StreamActions['rw-visit'].call(stream);

  assert.equal(turbo.visits.length, 1);
  assert.equal(turbo.visits[0].url, 'https://example.test/next?tab=done#summary');
  assert.equal(turbo.visits[0].options.action, 'replace');
});

test('rw-visit stream action revisits the current page for completion sentinel urls', () => {
  const { turbo } = loadRuntimeWithVisitAction({ windowHref: 'https://example.test/docs/current?q=old#top' });

  const stream = new FakeElement('turbo-stream');
  stream.setAttribute('url', '#');
  stream.setAttribute('visit-action', 'replace');

  turbo.StreamActions['rw-visit'].call(stream);

  assert.equal(turbo.visits.length, 1);
  assert.equal(turbo.visits[0].url, 'https://example.test/docs/current?q=old#');
  assert.equal(turbo.visits[0].options.action, 'replace');
});

test('rw-visit stream action accepts the supported same-origin url forms', () => {
  const { turbo } = loadRuntimeWithVisitAction({ windowHref: 'https://example.test/docs/current?q=old#top' });
  const allowed = [
    ['/docs/next', 'https://example.test/docs/next'],
    ['?tab=done', 'https://example.test/docs/current?tab=done'],
    ['#summary', 'https://example.test/docs/current?q=old#summary'],
    ['./next', 'https://example.test/docs/next'],
    ['../next', 'https://example.test/next'],
    ['https://example.test/docs/absolute', 'https://example.test/docs/absolute']
  ];

  for (const [url, expected] of allowed) {
    const stream = new FakeElement('turbo-stream');
    stream.setAttribute('url', url);
    turbo.StreamActions['rw-visit'].call(stream);
    assert.equal(turbo.visits.at(-1).url, expected);
    assert.equal(turbo.visits.at(-1).options.action, 'advance');
  }

  assert.equal(turbo.visits.length, allowed.length);
});

test('rw-visit stream action defaults to advance', () => {
  const { turbo } = loadRuntimeWithVisitAction({ windowHref: 'https://example.test/docs/current' });

  const stream = new FakeElement('turbo-stream');
  stream.setAttribute('url', '#summary');

  turbo.StreamActions['rw-visit'].call(stream);

  assert.equal(turbo.visits.length, 1);
  assert.equal(turbo.visits[0].url, 'https://example.test/docs/current#summary');
  assert.equal(turbo.visits[0].options.action, 'advance');
});

test('rw-visit stream action rejects unsafe urls without throwing', () => {
  const { turbo } = loadRuntimeWithVisitAction({ windowHref: 'https://example.test/docs/current' });
  const rejectedUrls = [
    '',
    ' /docs/next',
    '~/docs/next',
    'javascript:alert(1)',
    'data:text/html,hello',
    '//evil.example/path',
    'https://evil.example/path',
    '\\\\evil.example\\path',
    '/docs/\u0001next',
    '/docs/\u007Fnext'
  ];

  for (const url of rejectedUrls) {
    const stream = new FakeElement('turbo-stream');
    stream.setAttribute('url', url);

    assert.doesNotThrow(() => turbo.StreamActions['rw-visit'].call(stream));
  }

  assert.deepEqual(turbo.visits, []);
});

test('rw-visit stream action rejects unsupported visit action without throwing', () => {
  const { turbo } = loadRuntimeWithVisitAction({ windowHref: 'https://example.test/docs/current' });

  const stream = new FakeElement('turbo-stream');
  stream.setAttribute('url', '/docs/next');
  stream.setAttribute('visit-action', 'restore');

  assert.doesNotThrow(() => turbo.StreamActions['rw-visit'].call(stream));
  assert.deepEqual(turbo.visits, []);
});

test('stream registration tolerates missing Turbo global', () => {
  const { context, document } = loadRuntime({ turbo: null });
  const stream = new FakeElement('rw-stream-source');
  stream.setAttribute('src', '/streams/orders');
  document.body.appendChild(stream);

  assert.doesNotThrow(() => context.window.RazorWire.connectionManager.scan());
  assert.equal(stream.getAttribute('data-rw-registered'), 'true');
});

test('stream state body attributes use decoded channel identity without url modifiers', () => {
  const { context, document } = loadRuntime();
  const stream = new FakeElement('rw-stream-source');
  stream.setAttribute('src', '/streams/tenant%3Aorders?replay=1#live');
  document.body.appendChild(stream);

  context.window.RazorWire.connectionManager.scan();
  const source = context.window.RazorWire.connectionManager.sources.get('/streams/tenant%3Aorders?replay=1#live');
  source.es.onopen();

  assert.equal(source.channel, 'tenant:orders');
  assert.equal(document.body.getAttribute('data-rw-stream-tenant-orders'), 'connected');
  assert.equal(document.body.hasAttribute('data-rw-stream-tenant-3Aorders-replay-1-live'), false);
});

test('stream state ignores decoded direct-request channels outside the public grammar', () => {
  const { context, document } = loadRuntime();
  const stream = new FakeElement('rw-stream-source');
  const selectors = [];
  const querySelectorAll = document.querySelectorAll.bind(document);
  stream.setAttribute('src', '/streams/%22');
  document.body.appendChild(stream);
  document.querySelectorAll = selector => {
    selectors.push(selector);
    return querySelectorAll(selector);
  };

  context.window.RazorWire.connectionManager.scan();
  const source = context.window.RazorWire.connectionManager.sources.get('/streams/%22');

  assert.equal(source.channel, null);
  assert.doesNotThrow(() => source.es.onerror());
  assert.equal(selectors.some(selector => selector.startsWith('[data-rw-requires-stream="')), false);
});

test('stream errors dispatch observable detail to registered stream source elements', () => {
  const { context, document } = loadRuntime();
  const stream = new FakeElement('rw-stream-source');
  const events = [];
  stream.setAttribute('src', '/streams/orders');
  stream.dispatchEvent = event => {
    events.push(event);
    return !event.defaultPrevented;
  };
  document.body.appendChild(stream);

  context.window.RazorWire.connectionManager.scan();
  const source = context.window.RazorWire.connectionManager.sources.get('/streams/orders');
  source.es.readyState = 0;
  source.es.onerror();

  const error = events.find(event => event.type === 'razorwire:stream:error');
  assert.equal(error.detail.channel, 'orders');
  assert.equal(error.detail.source, stream);
  assert.equal(error.detail.state, 'connecting');
  assert.equal(error.detail.readyState, 0);
  assert.equal(error.detail.src, '/streams/orders');
});

test('page navigation manager exposes diagnostics and starts inert without roots', () => {
  const { context } = loadRuntime({ pageNavigation: true });

  assert.ok(context.window.RazorWire.pageNavigationManager);
  assert.equal(context.window.RazorWire.pageNavigationManager.controllers.size, 0);
  assert.equal(context.window.RazorWire.pageNavigationManager.getDiagnostics().length, 0);
});

test('page navigation sets active state from the initial hash', () => {
  const { context, document } = loadRuntime({ windowHref: 'https://example.test/#pricing', pageNavigation: true });
  const { nav, overviewLink, pricingLink } = createPageNavigationFixture(document);
  document.body.appendChild(nav);

  context.window.RazorWire.pageNavigationManager.scan();

  assert.equal(overviewLink.hasAttribute('aria-current'), false);
  assert.equal(pricingLink.getAttribute('aria-current'), 'location');
  assert.equal(pricingLink.getAttribute('data-rw-page-nav-active'), 'true');
});

test('page navigation closes the configured panel after same-page navigation', () => {
  const { context, document } = loadRuntime({ pageNavigation: true });
  const { nav, overviewLink, toggle, panel } = createPageNavigationFixture(document);
  document.body.appendChild(nav);
  context.window.RazorWire.pageNavigationManager.scan();

  const event = {
    type: 'click',
    target: overviewLink,
    button: 0,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    }
  };

  nav.dispatchEvent(event);

  assert.equal(event.defaultPrevented, true);
  assert.equal(toggle.getAttribute('aria-expanded'), 'false');
  assert.equal(panel.getAttribute('data-rw-page-nav-panel-state'), 'closed');
  assert.equal(overviewLink.getAttribute('aria-current'), 'location');
});

test('page navigation leaves modified clicks and missing targets alone', () => {
  const { context, document } = loadRuntime({ pageNavigation: true });
  const { nav, overviewLink } = createPageNavigationFixture(document);
  const missing = new FakeElement('a');
  missing.setAttribute('href', '#missing');
  missing.setAttribute('data-rw-page-nav-link', 'true');
  nav.appendChild(missing);
  document.body.appendChild(nav);
  context.window.RazorWire.pageNavigationManager.scan();

  const modified = clickEvent(overviewLink, { metaKey: true });
  nav.dispatchEvent(modified);
  const absentTarget = clickEvent(missing);
  nav.dispatchEvent(absentTarget);

  assert.equal(modified.defaultPrevented, false);
  assert.equal(absentTarget.defaultPrevented, false);
});

test('page navigation prunes removed roots and records missing panel diagnostics', () => {
  const { context, document } = loadRuntime({ pageNavigation: true });
  const nav = new FakeElement('nav');
  nav.setAttribute('data-rw-page-nav', 'true');
  const toggle = new FakeElement('button');
  toggle.setAttribute('data-rw-page-nav-toggle', 'true');
  toggle.setAttribute('aria-controls', 'missing-panel');
  nav.appendChild(toggle);
  document.body.appendChild(nav);

  context.window.RazorWire.pageNavigationManager.scan();
  assert.equal(context.window.RazorWire.pageNavigationManager.controllers.size, 1);
  assert.equal(context.window.RazorWire.pageNavigationManager.getDiagnostics().length, 1);

  nav.remove();
  context.window.RazorWire.pageNavigationManager.prune();

  assert.equal(context.window.RazorWire.pageNavigationManager.controllers.size, 0);
});

test('page navigation rescans without duplicating root listeners', () => {
  const { context, document } = loadRuntime({ pageNavigation: true });
  const { nav } = createPageNavigationFixture(document);
  document.body.appendChild(nav);

  context.window.RazorWire.pageNavigationManager.scan();
  context.window.RazorWire.pageNavigationManager.scan();
  context.window.RazorWire.pageNavigationManager.scan();

  assert.equal(context.window.RazorWire.pageNavigationManager.controllers.size, 1);
  assert.equal(nav.listeners.get('click').length, 2);
});

test('page navigation promotes the first visible section from viewport state', () => {
  const { context, document } = loadRuntime({ pageNavigation: true });
  const { nav, overview, pricing, pricingLink } = createPageNavigationFixture(document);
  overview.rectTop = -500;
  pricing.rectTop = 24;
  document.body.appendChild(nav);

  context.window.RazorWire.pageNavigationManager.scan();

  assert.equal(pricingLink.getAttribute('aria-current'), 'location');
  assert.equal(pricingLink.getAttribute('data-rw-page-nav-active'), 'true');
});

test('page navigation falls back to viewport state when hash no longer resolves', () => {
  const { context, document, window } = loadRuntime({ windowHref: 'https://example.test/#pricing', pageNavigation: true });
  const { nav, overview, pricing, overviewLink, pricingLink } = createPageNavigationFixture(document);
  overview.rectTop = 24;
  pricing.rectTop = 800;
  document.body.appendChild(nav);
  context.window.RazorWire.pageNavigationManager.scan();
  assert.equal(pricingLink.getAttribute('aria-current'), 'location');

  window.location.href = 'https://example.test/#missing';
  window.location.hash = '#missing';
  context.window.RazorWire.pageNavigationManager.refreshActiveFromHash();

  assert.equal(overviewLink.getAttribute('aria-current'), 'location');
  assert.equal(overviewLink.getAttribute('data-rw-page-nav-active'), 'true');
  assert.equal(pricingLink.hasAttribute('aria-current'), false);
});

test('page navigation does not keep a hash active outside the boundary window', () => {
  const { context, document } = loadRuntime({ windowHref: 'https://example.test/#pricing', pageNavigation: true });
  const { nav, overview, pricing, overviewLink, pricingLink } = createPageNavigationFixture(document);
  overview.rectTop = 24;
  pricing.rectTop = 800;
  document.body.appendChild(nav);
  context.window.RazorWire.pageNavigationManager.scan();
  assert.equal(pricingLink.getAttribute('aria-current'), 'location');

  const controller = context.window.RazorWire.pageNavigationManager.controllers.get(nav);
  controller.refreshActiveFromViewport();

  assert.equal(overviewLink.getAttribute('aria-current'), 'location');
  assert.equal(pricingLink.hasAttribute('aria-current'), false);
});

test('page navigation keeps an initial hash active at the host scroll margin', () => {
  const { context, document } = loadRuntime({ windowHref: 'https://example.test/#pricing', pageNavigation: true });
  const { nav, overview, pricing, pricingLink } = createPageNavigationFixture(document);
  overview.rectTop = -600;
  overview.computedStyle = { overflowY: 'visible', scrollMarginTop: '160px' };
  pricing.rectTop = 176;
  document.body.appendChild(nav);
  context.window.RazorWire.pageNavigationManager.scan();

  const controller = context.window.RazorWire.pageNavigationManager.controllers.get(nav);
  controller.refreshActiveFromViewport();

  assert.equal(pricingLink.getAttribute('aria-current'), 'location');
});

test('page navigation aligns active boundaries with section scroll margin', () => {
  const { context, document } = loadRuntime({ pageNavigation: true });
  const { nav, overview, pricing, pricingLink } = createPageNavigationFixture(document);
  overview.rectTop = -500;
  overview.computedStyle = { overflowY: 'visible', scrollMarginTop: '96px' };
  pricing.rectTop = 88;
  document.body.appendChild(nav);

  context.window.RazorWire.pageNavigationManager.scan();

  assert.equal(pricingLink.getAttribute('aria-current'), 'location');
});

test('page navigation scrolls the nearest scrollable section root', () => {
  const { context, document } = loadRuntime({ pageNavigation: true });
  const { nav, overview, pricing, pricingLink } = createPageNavigationFixture(document);
  const scroller = new FakeElement('div');
  scroller.rectTop = 100;
  scroller.clientHeight = 400;
  scroller.scrollHeight = 1200;
  scroller.computedStyle = { overflowY: 'auto' };
  scroller.scrollTop = 0;
  overview.rectTop = 100;
  pricing.rectTop = 500;
  scroller.append(overview, pricing);
  document.body.append(scroller, nav);
  context.window.RazorWire.pageNavigationManager.scan();

  nav.dispatchEvent(clickEvent(pricingLink));

  assert.equal(pricing.lastScrollIntoViewOptions, null);
  assert.equal(scroller.lastScrollToOptions.top, 336);
  assert.equal(scroller.lastScrollToOptions.behavior, 'smooth');
});

test('page navigation honors reduced motion when scrolling to a target', () => {
  const { context, document } = loadRuntime({
    pageNavigation: true,
    matchMedia: () => ({ matches: true })
  });
  const { nav, overview, overviewLink } = createPageNavigationFixture(document);
  document.body.appendChild(nav);
  context.window.RazorWire.pageNavigationManager.scan();

  nav.dispatchEvent(clickEvent(overviewLink));

  assert.equal(overview.lastScrollIntoViewOptions.behavior, 'auto');
  assert.equal(overview.lastScrollIntoViewOptions.block, 'start');
});

test('page navigation leaves external download target and prehandled clicks alone', () => {
  const { context, document } = loadRuntime({ pageNavigation: true });
  const { nav, panel, overviewLink } = createPageNavigationFixture(document);
  const external = createPageNavigationLink('https://elsewhere.test/#overview');
  const download = createPageNavigationLink('#overview');
  download.setAttribute('download', 'overview.txt');
  const blank = createPageNavigationLink('#overview');
  blank.setAttribute('target', '_blank');
  panel.append(external, download, blank);
  document.body.appendChild(nav);
  context.window.RazorWire.pageNavigationManager.scan();

  const prehandled = clickEvent(overviewLink, { defaultPrevented: true });
  nav.dispatchEvent(prehandled);
  const externalClick = clickEvent(external);
  nav.dispatchEvent(externalClick);
  const downloadClick = clickEvent(download);
  nav.dispatchEvent(downloadClick);
  const blankClick = clickEvent(blank);
  nav.dispatchEvent(blankClick);

  assert.equal(prehandled.defaultPrevented, true);
  assert.equal(externalClick.defaultPrevented, false);
  assert.equal(downloadClick.defaultPrevented, false);
  assert.equal(blankClick.defaultPrevented, false);
});

test('page navigation tolerates malformed hashes and falls back to viewport state', () => {
  const { context, document } = loadRuntime({ windowHref: 'https://example.test/#%E0%A4%A', pageNavigation: true });
  const { nav, overviewLink, pricingLink } = createPageNavigationFixture(document);
  document.body.appendChild(nav);

  context.window.RazorWire.pageNavigationManager.scan();

  assert.equal(overviewLink.getAttribute('aria-current'), 'location');
  assert.equal(pricingLink.hasAttribute('aria-current'), false);
});

test('page navigation toggles a fallback panel when aria-controls is omitted', () => {
  const { context, document } = loadRuntime({ pageNavigation: true });
  const nav = new FakeElement('nav');
  nav.setAttribute('data-rw-page-nav', 'true');
  const toggle = new FakeElement('button');
  toggle.setAttribute('data-rw-page-nav-toggle', 'true');
  toggle.setAttribute('aria-expanded', 'false');
  const panel = new FakeElement('div');
  panel.setAttribute('data-rw-page-nav-panel', 'true');
  nav.append(toggle, panel);
  document.body.appendChild(nav);

  context.window.RazorWire.pageNavigationManager.scan();
  assert.equal(panel.getAttribute('data-rw-page-nav-panel-state'), 'closed');

  nav.dispatchEvent(clickEvent(toggle));

  assert.equal(toggle.getAttribute('aria-expanded'), 'true');
  assert.equal(panel.getAttribute('data-rw-page-nav-panel-state'), 'open');
});

function loadRuntime(runtimeOptions = {}) {
  const document = new FakeDocument(runtimeOptions);
  const locationUrl = new URL(runtimeOptions.windowHref ?? 'https://example.test/');
  const visits = [];
  const turbo = runtimeOptions.turbo === null ? null : {
    StreamActions: {},
    visits,
    connectStreamSource: () => {},
    disconnectStreamSource: () => {},
    visit: (url, options) => visits.push({ url, options })
  };
  const window = {
    RazorWireInitialized: false,
    RazorWire: { config: { existing: true } },
    location: {
      href: locationUrl.href,
      origin: locationUrl.origin,
      pathname: locationUrl.pathname,
      search: locationUrl.search,
      hash: locationUrl.hash
    },
    history: {
      pushState: (_state, _title, url) => {
        const next = new URL(url, window.location.href);
        window.location.href = next.href;
        window.location.pathname = next.pathname;
        window.location.search = next.search;
        window.location.hash = next.hash;
      }
    },
    matchMedia: runtimeOptions.matchMedia ?? (() => ({ matches: false })),
    getComputedStyle: element => element.computedStyle ?? { overflowY: 'visible' },
    addEventListener: () => {},
    removeEventListener: () => {}
  };
  if (turbo !== null) {
    window.Turbo = turbo;
  }
  const context = {
    console: { log: () => {} },
    document,
    window,
    Turbo: turbo ?? undefined,
    Element: FakeElement,
    HTMLFormElement: FakeForm,
    CustomEvent: FakeCustomEvent,
    EventSource: FakeEventSource,
    MutationObserver: class {
      observe() {}
      disconnect() {}
    },
    setTimeout,
    clearTimeout,
    setInterval: () => 1,
    clearInterval: () => {},
    Date,
    Intl,
    URL,
    URLSearchParams,
    fetch: runtimeOptions.fetch
  };
  context.globalThis = context;
  vm.createContext(context);
  vm.runInContext(readFileSync(runtimePath, 'utf8'), context);
  if (runtimeOptions.pageNavigation) {
    vm.runInContext(readFileSync(pageNavigationPath, 'utf8'), context);
  }

  return { context, document, window, turbo };
}

function createPageNavigationFixture(document) {
  const overview = new FakeElement('section');
  overview.setAttribute('id', 'overview');
  overview.rectTop = 0;
  const pricing = new FakeElement('section');
  pricing.setAttribute('id', 'pricing');
  pricing.rectTop = 800;
  document.body.append(overview, pricing);

  const nav = new FakeElement('nav');
  nav.setAttribute('data-rw-page-nav', 'true');
  const toggle = new FakeElement('button');
  toggle.setAttribute('data-rw-page-nav-toggle', 'true');
  toggle.setAttribute('aria-controls', 'page-sections-panel');
  toggle.setAttribute('aria-expanded', 'true');
  const panel = new FakeElement('div');
  panel.setAttribute('id', 'page-sections-panel');
  panel.setAttribute('data-rw-page-nav-panel', 'true');
  const overviewLink = new FakeElement('a');
  overviewLink.setAttribute('href', '#overview');
  overviewLink.setAttribute('data-rw-page-nav-link', 'true');
  const pricingLink = new FakeElement('a');
  pricingLink.setAttribute('href', '#pricing');
  pricingLink.setAttribute('data-rw-page-nav-link', 'true');
  panel.append(overviewLink, pricingLink);
  nav.append(toggle, panel);

  return { nav, overview, pricing, overviewLink, pricingLink, toggle, panel };
}

function createPageNavigationLink(href) {
  const link = new FakeElement('a');
  link.setAttribute('href', href);
  link.setAttribute('data-rw-page-nav-link', 'true');
  return link;
}

function clickEvent(target, overrides = {}) {
  return {
    type: 'click',
    target,
    button: 0,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    },
    ...overrides
  };
}

function loadRuntimeWithVisitAction(runtimeOptions = {}) {
  for (let attempt = 0; attempt < 3; attempt += 1) {
    const runtime = loadRuntime(runtimeOptions);
    if (typeof runtime.turbo?.StreamActions?.['rw-visit'] === 'function') {
      return runtime;
    }
  }

  assert.fail('RazorWire runtime did not register the rw-visit Turbo stream action.');
}

function response(status, headers) {
  const normalized = new Map(Object.entries(headers).map(([key, value]) => [key.toLowerCase(), value]));
  return {
    response: {
      status,
      headers: {
        get: name => normalized.get(String(name).toLowerCase()) || null
      }
    }
  };
}

function windowHandled(fetchResponse) {
  const { context } = loadRuntime();

  return context.window.RazorWire.formFailureManager.isHandled(fetchResponse);
}

class FakeCustomEvent {
  constructor(type, options = {}) {
    this.type = type;
    this.bubbles = options.bubbles || false;
    this.cancelable = options.cancelable || false;
    this.detail = options.detail;
    this.defaultPrevented = false;
  }

  preventDefault() {
    if (this.cancelable) this.defaultPrevented = true;
  }
}

class FakeHeaders {
  constructor() {
    this.values = new Map();
  }

  set(name, value) {
    this.values.set(String(name).toLowerCase(), String(value));
  }

  get(name) {
    return this.values.get(String(name).toLowerCase()) || null;
  }
}

class FakeEventSource {
  constructor(url, options = {}) {
    this.url = url;
    this.options = options;
    this.readyState = 0;
    this.closed = false;
    this.onopen = null;
    this.onerror = null;
  }

  close() {
    this.closed = true;
    this.readyState = 2;
  }
}

class FakeElement {
  constructor(tagName = 'div') {
    this.tagName = tagName.toUpperCase();
    this.attributes = new Map();
    this.children = [];
    this.parentElement = null;
    this.listeners = new Map();
    this.textContent = '';
    this.disabled = false;
    this.id = '';
    this.dataset = {};
    this.name = '';
    this.type = '';
    this.value = '';
    this.rectTop = 0;
    this.clientHeight = 100;
    this.scrollHeight = 100;
    this.scrollTop = 0;
    this.computedStyle = { overflowY: 'visible' };
    this.lastScrollIntoViewOptions = null;
    this.lastScrollToOptions = null;
  }

  get isConnected() {
    return this.tagName === 'BODY' || this.tagName === 'HEAD' || this.parentElement !== null;
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
    if (name === 'id') this.id = String(value);
    if (name.startsWith('data-')) {
      this.dataset[toDatasetName(name)] = String(value);
    }
  }

  getAttribute(name) {
    return this.attributes.get(name) ?? null;
  }

  hasAttribute(name) {
    return this.attributes.has(name);
  }

  removeAttribute(name) {
    this.attributes.delete(name);
  }

  appendChild(child) {
    child.parentElement = this;
    this.children.push(child);
    return child;
  }

  append(...children) {
    children.forEach(child => this.appendChild(child));
  }

  prepend(child) {
    child.parentElement = this;
    this.children.unshift(child);
    return child;
  }

  remove() {
    if (!this.parentElement) return;
    this.parentElement.children = this.parentElement.children.filter(child => child !== this);
    this.parentElement = null;
  }

  focus() {}

  scrollIntoView(options) {
    this.lastScrollIntoViewOptions = options;
  }

  scrollTo(options) {
    this.lastScrollToOptions = options;
    if (typeof options.top === 'number') this.scrollTop = options.top;
  }

  getBoundingClientRect() {
    return { top: this.rectTop, bottom: this.rectTop + 100, height: 100 };
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) || [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  removeEventListener(type, listener) {
    const listeners = this.listeners.get(type) || [];
    this.listeners.set(type, listeners.filter(candidate => candidate !== listener));
  }

  dispatchEvent(event) {
    event.target = event.target || this;
    for (const listener of this.listeners.get(event.type) || []) {
      listener(event);
    }
    return !event.defaultPrevented;
  }

  contains(candidate) {
    if (candidate === this) return true;
    let current = candidate?.parentElement || null;
    while (current) {
      if (current === this) return true;
      current = current.parentElement;
    }

    return false;
  }

  closest(selector) {
    let current = this;
    while (current) {
      if (matches(current, selector)) return current;
      current = current.parentElement;
    }

    return null;
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] || null;
  }

  querySelectorAll(selector) {
    const matchesFound = [];
    walk(this, element => {
      if (element !== this && matches(element, selector)) matchesFound.push(element);
    });
    return matchesFound;
  }

  set innerHTML(value) {
    this._innerHTML = value;
    this.children = [];
    if (value.includes('data-rw-form-error-title')) {
      const title = new FakeElement('strong');
      title.setAttribute('data-rw-form-error-title', 'true');
      this.appendChild(title);
    }
    if (value.includes('data-rw-form-error-message')) {
      const message = new FakeElement('p');
      message.setAttribute('data-rw-form-error-message', 'true');
      this.appendChild(message);
    }
    if (value.includes('data-rw-form-error-diagnostic')) {
      const diagnostic = new FakeElement('div');
      diagnostic.setAttribute('data-rw-form-error-diagnostic', 'true');
      const detail = new FakeElement('p');
      detail.setAttribute('data-rw-form-error-detail', 'true');
      const hints = new FakeElement('ul');
      hints.setAttribute('data-rw-form-error-hints', 'true');
      diagnostic.appendChild(detail);
      diagnostic.appendChild(hints);
      this.appendChild(diagnostic);
    }
  }
}

class FakeForm extends FakeElement {
  constructor() {
    super('form');
  }
}

class FakeDocument {
  constructor(runtimeOptions = {}) {
    this.readyState = 'complete';
    this.listeners = new Map();
    this.head = new FakeElement('head');
    this.body = new FakeElement('body');
    this.activeElement = null;
    this.currentScript = new FakeElement('script');
    this.currentScript.setAttribute('src', '/_content/ForgeTrust.RazorWire/razorwire/razorwire.js');
    this.currentScript.setAttribute('data-rw-development-diagnostics', runtimeOptions.developmentDiagnostics ?? 'true');
    if (!runtimeOptions.omitFormFailureEnabled) {
      this.currentScript.setAttribute('data-rw-form-failure-enabled', runtimeOptions.formFailureEnabled ?? 'true');
    }
    this.currentScript.setAttribute('data-rw-form-failure-mode', runtimeOptions.failureMode ?? 'auto');
    this.currentScript.setAttribute('data-rw-default-failure-message', runtimeOptions.defaultFailureMessage ?? 'Default failure');
    this.currentScript.setAttribute('data-rw-live-origin', runtimeOptions.liveOrigin ?? '');
    this.currentScript.setAttribute('data-rw-hybrid-credentials', runtimeOptions.hybridCredentials ?? 'auto');
    this.currentScript.setAttribute('data-rw-antiforgery-endpoint', runtimeOptions.antiforgeryEndpoint ?? '/_rw/antiforgery/token');
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) || [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  dispatchEvent(event) {
    for (const listener of this.listeners.get(event.type) || []) {
      listener(event);
    }
    return !event.defaultPrevented;
  }

  createElement(tagName) {
    return new FakeElement(tagName);
  }

  getElementById(id) {
    let found = null;
    [this.head, this.body].forEach(root => walk(root, element => {
      if (!found && element.id === id) found = element;
    }));
    return found;
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] || null;
  }

  querySelectorAll(selector) {
    const results = [];
    [this.currentScript, this.head, this.body].forEach(root => {
      if (matches(root, selector)) results.push(root);
      walk(root, element => {
        if (element !== root && matches(element, selector)) results.push(element);
      });
    });
    return results;
  }
}

function walk(root, callback) {
  for (const child of root.children) {
    callback(child);
    walk(child, callback);
  }
}

function matches(element, selector) {
  if (selector === 'rw-stream-source') return element.tagName === 'RW-STREAM-SOURCE';
  if (selector === 'time[data-rw-time]') return element.tagName === 'TIME' && element.hasAttribute('data-rw-time');
  if (selector === 'time[data-rw-time][data-rw-time-display="relative"]') {
    return element.tagName === 'TIME'
      && element.hasAttribute('data-rw-time')
      && element.getAttribute('data-rw-time-display') === 'relative';
  }
  if (selector === 'form[data-rw-form="true"]') {
    return element.tagName === 'FORM' && element.getAttribute('data-rw-form') === 'true';
  }
  if (selector.startsWith('input[name="')) {
    const name = selector.match(/input\[name="([^"]+)"\]/)?.[1];
    return element.tagName === 'INPUT' && (element.getAttribute('name') === name || element.name === name);
  }
  if (selector === 'input[data-rw-antiforgery-token="true"]') {
    return element.tagName === 'INPUT' && element.getAttribute('data-rw-antiforgery-token') === 'true';
  }
  if (selector === '[data-rw-form-errors]') return element.hasAttribute('data-rw-form-errors');
  if (selector === '[data-rw-form-error-generated="true"]') {
    return element.getAttribute('data-rw-form-error-generated') === 'true';
  }
  if (selector === '[data-rw-form-error-title="true"]') return element.getAttribute('data-rw-form-error-title') === 'true';
  if (selector === '[data-rw-form-error-message="true"]') return element.getAttribute('data-rw-form-error-message') === 'true';
  if (selector === '[data-rw-form-error-detail="true"]') return element.getAttribute('data-rw-form-error-detail') === 'true';
  if (selector === '[data-rw-form-error-hints="true"]') return element.getAttribute('data-rw-form-error-hints') === 'true';
  if (selector === '#rw-form-failure-default-styles') return element.id === 'rw-form-failure-default-styles';
  if (selector === '[data-rw-page-nav]') return element.hasAttribute('data-rw-page-nav');
  if (selector === '[data-rw-page-nav-link]') return element.hasAttribute('data-rw-page-nav-link');
  if (selector === 'a[data-rw-page-nav-link]') return element.tagName === 'A' && element.hasAttribute('data-rw-page-nav-link');
  if (selector === '[data-rw-page-nav-toggle]') return element.hasAttribute('data-rw-page-nav-toggle');
  if (selector === '[data-rw-page-nav-panel]') return element.hasAttribute('data-rw-page-nav-panel');
  if (selector.startsWith('script[src*=')) return element.tagName === 'SCRIPT' && element.getAttribute('src')?.includes('/razorwire/razorwire.js');
  if (selector.startsWith('[data-rw-form-error-generated="true"][data-rw-form-error-owner="')) {
    const owner = selector.match(/data-rw-form-error-owner="([^"]+)"/)?.[1];
    return element.getAttribute('data-rw-form-error-generated') === 'true'
      && element.getAttribute('data-rw-form-error-owner') === owner;
  }
  return false;
}

function toDatasetName(name) {
  return name
    .slice(5)
    .replace(/-([a-z])/g, (_, char) => char.toUpperCase());
}
