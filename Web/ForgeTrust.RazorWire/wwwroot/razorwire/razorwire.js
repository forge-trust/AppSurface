/**
 * RazorWire Core Client Runtime
 * Provides native stream monitoring and event dispatching.
 */
(function () {
    if (window.RazorWireInitialized) return;
    window.RazorWireInitialized = true;

    class ConnectionManager {
        constructor() {
            this.sources = new Map(); // src -> { es: EventSource, elements: Set<Element>, closeTimer: int, state: string }
            this.channelStates = new Map(); // Channel -> State (for global access and dependent elements)
            this.observer = new MutationObserver(this.handleMutations.bind(this));
        }

        start() {
            this.observeBody();
            this.scan();

            document.addEventListener('turbo:render', () => {
                const isPreview = document.documentElement.hasAttribute('data-turbo-preview');
                this.observeBody();

                // restoreStates MUST run to apply 'connected' class to the new body
                this.restoreStates();

                // Scan FIRST to register new elements and keep the connection alive
                if (!isPreview) {
                    this.scan();
                }

                // Prune any elements that were removed (Turbo replaces body, so MO might miss them)
                this.prune();

                this.syncDependentElements();
            });
            document.addEventListener('turbo:load', () => {
                this.syncDependentElements();
                this.syncIslands();
            });
            document.addEventListener('turbo:frame-load', () => {
                this.scan();
                this.syncDependentElements();
            });
        }

        restoreStates() {
            for (const [channel, state] of this.channelStates) {
                this.updateBodyAttribute(channel, state);
            }
        }

        prune() {
            // console.log('[ConnectionManager] Pruning disconnected elements...');
            for (const [src, source] of this.sources) {
                for (const element of source.elements) {
                    if (!element.isConnected) {
                        // console.log('[ConnectionManager] Pruning element:', src);
                        this.unregister(element);
                    }
                }
            }
        }

        syncIslands(targetChannel = null) {
            const selector = targetChannel
                ? `turbo-frame[data-turbo-permanent][src][data-rw-swr][data-rw-requires-stream="${targetChannel}"]`
                : 'turbo-frame[data-turbo-permanent][src][data-rw-swr]';

            const islands = document.querySelectorAll(selector);



            setTimeout(() => {
                islands.forEach(frame => {
                    // Optimized SWR:
                    // If a frame is LAZY and hasn't loaded yet (no 'complete' attribute),
                    // we skip the reload. The native lazy load will happen when visible, providing fresh data.
                    const isLazy = frame.getAttribute('loading') === 'lazy';
                    const hasLoadedOnce = frame.hasAttribute('complete');

                    if (isLazy && !hasLoadedOnce) {
                        return;
                    }

                    if (typeof frame.reload === 'function') {
                        frame.reload();
                    }
                });
            }, 100);
        }

        observeBody() {
            this.observer.disconnect();
            this.observer.observe(document.body, {
                childList: true,
                subtree: true
            });
        }

        handleMutations(mutations) {
            for (const mutation of mutations) {
                for (const node of mutation.addedNodes) {
                    if (node instanceof Element) {
                        if (node.tagName === 'RW-STREAM-SOURCE') {
                            this.register(node);
                        } else {
                            node.querySelectorAll('rw-stream-source').forEach(el => this.register(el));
                        }
                    }
                }
                for (const node of mutation.removedNodes) {
                    if (node instanceof Element) {
                        if (node.tagName === 'RW-STREAM-SOURCE') {
                            this.unregister(node);
                        } else {
                            node.querySelectorAll('rw-stream-source').forEach(el => this.unregister(el));
                        }
                    }
                }
            }
            this.syncDependentElements();
        }

        scan() {
            document.querySelectorAll('rw-stream-source').forEach(el => this.register(el));
        }

        register(element) {
            const src = element.getAttribute('src');
            if (!src) return;

            let source = this.sources.get(src);
            if (!source) {
                console.log('[ConnectionManager] Creating NEW connection for:', src);
                // Create new persistent connection
                const es = new EventSource(src);
                Turbo.connectStreamSource(es);

                source = {
                    es,
                    elements: new Set(),
                    closeTimer: null,
                    state: 'connecting',
                    channel: this.getChannelName(src)
                };

                // Hook up state listeners to the EventSource directly
                es.onopen = () => this.updateSourceState(src, 'connected');
                es.onerror = () => {
                    if (es.readyState === 2) this.updateSourceState(src, 'disconnected');
                    else this.updateSourceState(src, 'connecting');
                };

                this.sources.set(src, source);

                // Initial state
                this.updateSourceState(src, 'connecting');
            }

            // Cancel any pending close
            if (source.closeTimer) {
                console.log('[ConnectionManager] Cancelling close timer for:', src);
                clearTimeout(source.closeTimer);
                source.closeTimer = null;
            }

            // Only increment count if we haven't already registered this specific element
            // (MutationObserver might fire multiple times or scan might duplicate)
            if (!source.elements.has(element)) {
                source.elements.add(element);
                element.setAttribute('data-rw-registered', 'true');
            }

            // Sync this element with current state
            this.dispatchToElement(element, source.channel, source.state);
        }

        unregister(element) {
            const src = element.getAttribute('src');
            if (!src) return;

            const source = this.sources.get(src);
            if (!source) return;

            if (source.elements.has(element)) {
                source.elements.delete(element);
                element.removeAttribute('data-rw-registered');
            }

            if (source.elements.size === 0) {
                console.log('[ConnectionManager] No more elements for:', src, 'Starting grace period (5000ms)...');

                // Capture the last known element info for reporting
                const lastId = element.id || '';

                // Grace period: Wait 5000ms before actually closing.
                // If a new page loads with the same stream, register() will cancel this timer.
                source.closeTimer = setTimeout(() => {
                    // Check size again in case re-registered
                    if (source.elements.size > 0) {
                        console.log('[ConnectionManager] Grace period saved connection:', src);
                        return;
                    }

                    console.log('[ConnectionManager] Closing connection:', src);
                    source.es.close();
                    Turbo.disconnectStreamSource(source.es);
                    this.sources.delete(src);

                    this.updateSourceState(src, 'disconnected');

                    // Fire disconnected event globally since source is gone
                    const event = new CustomEvent('razorwire:stream:disconnected', {
                        bubbles: true,
                        cancelable: false,
                        detail: {
                            channel: source.channel,
                            // Provide a mockup of the source so logging doesn't crash
                            source: { id: lastId },
                            state: 'disconnected'
                        }
                    });
                    document.dispatchEvent(event);

                    // Cleanup global state
                    this.channelStates.delete(source.channel);
                    this.updateBodyAttribute(source.channel, null);

                }, 5000);
            }
        }

        updateSourceState(src, state) {
            if (!src) return;
            const source = this.sources.get(src);
            if (!source && state !== 'disconnected') return;

            // Should be impossible if source is gone, but safety check
            const channel = source ? source.channel : this.getChannelName(src);
            if (!channel) return;

            if (source) source.state = state;

            // Update global trackers
            this.channelStates.set(channel, state);
            this.updateBodyAttribute(channel, state);
            this.syncDependentElements(channel);

            // Sync Islands on connect
            if (state === 'connected') {
                this.syncIslands(channel);
            }

            // Dispatch to all active elements for this src
            if (source) {
                source.elements.forEach(el => {
                    this.dispatchToElement(el, channel, state);
                });
            }
        }

        dispatchToElement(element, channel, state) {
            if (!element || !state) return;

            const eventName = `razorwire:stream:${state}`;
            const event = new CustomEvent(eventName, {
                bubbles: true,
                cancelable: false,
                detail: {
                    channel: channel,
                    source: element,
                    state: state
                }
            });
            element.dispatchEvent(event);
        }

        getChannelName(src) {
            if (!src) return null;
            try {
                const url = new URL(src, window.location.origin);
                const path = url.pathname.split('/').filter(Boolean).pop() || '';
                return path + url.search + url.hash;
            } catch {
                return null;
            }
        }

        syncDependentElements(targetChannel = null) {
            const selector = targetChannel
                ? `[data-rw-requires-stream="${targetChannel}"]`
                : '[data-rw-requires-stream]';

            const elements = document.querySelectorAll(selector);
            elements.forEach(el => {
                const channel = el.getAttribute('data-rw-requires-stream');
                const state = this.channelStates.get(channel);

                if (state === 'connected' || (state === 'connecting' && el.tagName === 'TURBO-FRAME')) {
                    el.removeAttribute('disabled');
                    el.removeAttribute('aria-disabled');
                } else {
                    el.setAttribute('disabled', 'disabled');
                    el.setAttribute('aria-disabled', 'true');
                }
            });
        }

        updateBodyAttribute(channel, state) {
            const attr = `data-rw-stream-${channel}`;
            if (state && state !== 'disconnected') {
                document.body.setAttribute(attr, state);
            } else {
                document.body.removeAttribute(attr);
            }
        }
    }

    /**
     * LocalTimeFormatter - Formats UTC timestamps for display
     * Handles <time data-rw-time> elements with support for:
     * - data-rw-time-display: time (default), date, datetime, relative
     * - data-rw-time-format: short, medium (default), long, full
     * - data-rw-time-tz: "utc" to display in UTC (default: user's local timezone)
     */
    class LocalTimeFormatter {
        constructor() {
            this.observer = new MutationObserver(mutations => this.handleMutations(mutations));
            this.formatter = typeof Intl !== 'undefined' && Intl.RelativeTimeFormat
                ? new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
                : null;
            this.updateInterval = null;
            this.isStarted = false;
            this.visibleElements = new Set();
            this.intersectionObserver = typeof IntersectionObserver !== 'undefined'
                ? new IntersectionObserver((entries) => {
                    entries.forEach(entry => {
                        if (entry.isIntersecting) {
                            this.visibleElements.add(entry.target);
                        } else {
                            this.visibleElements.delete(entry.target);
                        }
                    });
                })
                : null;
        }

        start() {
            if (this.isStarted) return;
            this.isStarted = true;

            this.formatAll();
            this.observer.observe(document.body, { childList: true, subtree: true });

            // Re-format on Turbo navigations
            document.addEventListener('turbo:load', () => this.formatAll());
            document.addEventListener('turbo:render', () => {
                this.observer.disconnect();
                this.observer.observe(document.body, { childList: true, subtree: true });
                this.formatAll();
            });

            this.startTimer();
        }

        startTimer() {
            if (this.updateInterval) return;
            // Update every 30 seconds to keep relative times (like "just now") fresh
            // Only updates elements currently visible in the viewport
            this.updateInterval = setInterval(() => this.formatRelativeOnly(), 30000);
        }

        stopTimer() {
            if (this.updateInterval) {
                clearInterval(this.updateInterval);
                this.updateInterval = null;
            }
        }

        handleMutations(mutations) {
            // Helper to check if element is relative
            const isRelative = (el) => el.tagName === 'TIME' && el.getAttribute('data-rw-time-display') === 'relative';

            for (const mutation of mutations) {
                // Handle added nodes
                for (const node of mutation.addedNodes) {
                    if (node instanceof Element) {
                        if (node.tagName === 'TIME' && node.hasAttribute('data-rw-time')) {
                            this.format(node);
                            if (isRelative(node) && this.intersectionObserver) this.intersectionObserver.observe(node);
                        }
                        node.querySelectorAll('time[data-rw-time]').forEach(el => {
                            this.format(el);
                            if (isRelative(el) && this.intersectionObserver) this.intersectionObserver.observe(el);
                        });
                    }
                }

                // Handle removed nodes to prevent memory leaks
                if (this.intersectionObserver) {
                    for (const node of mutation.removedNodes) {
                        if (node instanceof Element) {
                            if (node.tagName === 'TIME' && node.hasAttribute('data-rw-time')) {
                                this.intersectionObserver.unobserve(node);
                                this.visibleElements.delete(node);
                            }
                            node.querySelectorAll('time[data-rw-time]').forEach(el => {
                                this.intersectionObserver.unobserve(el);
                                this.visibleElements.delete(el);
                            });
                        }
                    }
                }
            }
        }

        formatAll() {
            this.visibleElements.clear();
            if (this.intersectionObserver) {
                this.intersectionObserver.disconnect();
            }

            document.querySelectorAll('time[data-rw-time]').forEach(el => {
                this.format(el);
                if (el.getAttribute('data-rw-time-display') === 'relative' && this.intersectionObserver) {
                    this.intersectionObserver.observe(el);
                }
            });
        }

        formatRelativeOnly() {
            if (this.intersectionObserver) {
                // Optimized update: only target elements that are relative AND visible in viewport
                this.visibleElements.forEach(el => {
                    this.format(el);
                });
            } else {
                // Fallback for environments without IntersectionObserver support: update all relative elements
                const selector = 'time[data-rw-time][data-rw-time-display="relative"]';
                document.querySelectorAll(selector).forEach(el => {
                    this.format(el);
                });
            }
        }

        format(element) {
            const dateStr = element.getAttribute('datetime');
            if (!dateStr) return;

            const date = new Date(dateStr);
            if (isNaN(date.getTime())) return;

            const display = element.getAttribute('data-rw-time-display') || 'time';
            const tz = element.getAttribute('data-rw-time-tz')?.toLowerCase();
            let formatStyle = element.getAttribute('data-rw-time-format');

            // Validate format style
            const validFormats = ['short', 'medium', 'long', 'full'];
            if (!validFormats.includes(formatStyle)) {
                formatStyle = 'medium';
            }

            const tzOption = tz === 'utc' ? { timeZone: 'UTC' } : {};

            let text = '';
            if (display === 'relative') {
                text = this.getRelativeTime(date);
            } else if (display === 'date') {
                text = date.toLocaleDateString(undefined, { dateStyle: formatStyle, ...tzOption });
            } else if (display === 'datetime') {
                text = date.toLocaleString(undefined, { dateStyle: formatStyle, timeStyle: formatStyle, ...tzOption });
            } else {
                // Default to time
                text = date.toLocaleTimeString(undefined, { timeStyle: formatStyle, ...tzOption });
            }

            if (text) {
                element.textContent = text;
            }
        }

        getRelativeTime(date) {
            const now = Date.now();
            const diff = date.getTime() - now;
            const absDiff = Math.abs(diff);
            const seconds = Math.round(diff / 1000);
            const minutes = Math.round(diff / 60000);
            const hours = Math.round(diff / 3600000);
            const days = Math.round(diff / 86400000);

            // Use Intl.RelativeTimeFormat if available
            if (this.formatter) {
                // Special handling for "just now" / "in a moment" to match user preference
                // Intl typically returns "in 0 seconds" or "0 seconds ago"
                if (absDiff < 60000) {
                    return diff >= 0 ? 'in a moment' : 'just now';
                }

                if (Math.abs(minutes) < 60) return this.formatter.format(minutes, 'minute');
                if (Math.abs(hours) < 24) return this.formatter.format(hours, 'hour');
                return this.formatter.format(days, 'day');
            }

            // Fallback for environments without Intl support
            const abs = Math.abs;
            if (abs(seconds) < 60) return diff >= 0 ? 'in a moment' : 'just now';
            if (abs(minutes) < 60) return minutes >= 0 ? `in ${minutes} min` : `${abs(minutes)} min ago`;
            if (abs(hours) < 24) return hours >= 0 ? `in ${hours} hr` : `${abs(hours)} hr ago`;
            return days >= 0 ? `in ${days} days` : `${abs(days)} days ago`;
        }


    }

    class FormFailureManager {
        constructor(config) {
            this.config = config;
            this.state = new WeakMap();
            this.nextId = 1;
            this.styleId = 'rw-form-failure-default-styles';
        }

        start() {
            if (this.config.failureUxEnabled !== false && (this.config.failureMode || 'auto').toLowerCase() !== 'off') {
                this.injectStyles();
            }

            document.addEventListener('turbo:before-fetch-request', event => this.handleBeforeFetchRequest(event));
            document.addEventListener('turbo:submit-start', event => this.handleSubmitStart(event));
            document.addEventListener('turbo:submit-end', event => this.handleSubmitEnd(event));
            document.addEventListener('turbo:fetch-request-error', event => this.handleFetchRequestError(event));
        }

        handleBeforeFetchRequest(event) {
            const form = this.getForm(event.target);
            if (!this.isRazorWireForm(form)) return;

            event.detail.fetchOptions = event.detail.fetchOptions || {};
            const headers = event.detail.fetchOptions.headers || {};
            if (typeof headers.set === 'function') {
                headers.set('X-RazorWire-Form', 'true');
            } else {
                headers['X-RazorWire-Form'] = 'true';
            }

            event.detail.fetchOptions.headers = headers;
        }

        handleSubmitStart(event) {
            const form = this.getForm(event.target);
            if (!this.isRazorWireForm(form)) return;

            const submitter = event.detail?.formSubmission?.submitter || null;
            this.clearGeneratedFailure(form);
            form.setAttribute('data-rw-submitting', 'true');
            form.setAttribute('data-rw-submit-status', 'submitting');
            form.removeAttribute('data-rw-last-status');
            form.setAttribute('aria-busy', 'true');

            const formState = { submitter, disabledByRazorWire: false, describedById: null };
            if (submitter && form.getAttribute('data-rw-disable-submit') !== 'false' && !submitter.disabled) {
                submitter.disabled = true;
                submitter.setAttribute('data-rw-submit-disabled-by-razorwire', 'true');
                formState.disabledByRazorWire = true;
            }

            this.state.set(form, formState);
            this.dispatch(form, 'razorwire:form:submit-start', { form, submitter });
        }

        handleSubmitEnd(event) {
            const form = this.getForm(event.target);
            if (!this.isRazorWireForm(form)) return;

            const statusCode = this.getStatusCode(event.detail?.fetchResponse);
            const handled = this.isHandled(event.detail?.fetchResponse);
            const responseKind = this.getResponseKind(event.detail?.fetchResponse);
            const success = event.detail?.success === true;
            const formState = this.state.get(form) || {};
            const submitter = formState.submitter || event.detail?.formSubmission?.submitter || null;

            this.finishSubmitting(form, formState);

            if (success) {
                this.clearGeneratedFailure(form);
                form.removeAttribute('data-rw-submit-status');
                form.removeAttribute('data-rw-last-status');
                this.dispatch(form, 'razorwire:form:submit-end', { form, submitter, success, statusCode, handled });
                return;
            }

            form.setAttribute('data-rw-submit-status', 'failed');
            if (statusCode !== null) {
                form.setAttribute('data-rw-last-status', String(statusCode));
            }

            const target = this.resolveTarget(form);
            const developmentDiagnostic = target.diagnostic
                || (!handled && this.config.developmentDiagnostics
                    ? this.diagnosticForFailure({ form, statusCode, responseKind })
                    : null);
            const failureDetail = {
                form,
                submitter,
                statusCode,
                handled,
                responseKind,
                target: target.element,
                message: this.messageForStatus(statusCode, responseKind),
                developmentDiagnostic
            };

            const failureEvent = this.dispatch(form, 'razorwire:form:failure', failureDetail, true);
            if (failureDetail.developmentDiagnostic) {
                this.dispatch(form, 'razorwire:form:diagnostic', failureDetail.developmentDiagnostic);
            }

            if (handled) {
                this.clearGeneratedFailure(form);
            } else if (!failureEvent.defaultPrevented && this.getMode(form) === 'auto') {
                this.renderFailure(form, target.element, failureDetail);
            }

            this.dispatch(form, 'razorwire:form:submit-end', { form, submitter, success, statusCode, handled });
        }

        handleFetchRequestError(event) {
            const form = this.getForm(event.target);
            if (!this.isRazorWireForm(form)) return;

            const formState = this.state.get(form) || {};
            const submitter = formState.submitter || null;
            this.finishSubmitting(form, formState);
            form.setAttribute('data-rw-submit-status', 'failed');

            const target = this.resolveTarget(form);
            const developmentDiagnostic = target.diagnostic
                || (this.config.developmentDiagnostics
                    ? this.diagnosticForFailure({ form, statusCode: null, responseKind: 'network' })
                    : null);
            const failureDetail = {
                form,
                submitter,
                statusCode: null,
                handled: false,
                responseKind: 'network',
                target: target.element,
                message: this.messageForStatus(null, 'network'),
                developmentDiagnostic
            };

            const failureEvent = this.dispatch(form, 'razorwire:form:failure', failureDetail, true);
            if (failureDetail.developmentDiagnostic) {
                this.dispatch(form, 'razorwire:form:diagnostic', failureDetail.developmentDiagnostic);
            }

            if (!failureEvent.defaultPrevented && this.getMode(form) === 'auto') {
                this.renderFailure(form, target.element, failureDetail);
            }

            this.dispatch(form, 'razorwire:form:submit-end', {
                form,
                submitter,
                success: false,
                statusCode: null,
                handled: false
            });
        }

        finishSubmitting(form, formState) {
            form.removeAttribute('data-rw-submitting');
            form.removeAttribute('aria-busy');
            if (formState.submitter && formState.disabledByRazorWire) {
                formState.submitter.disabled = false;
                formState.submitter.removeAttribute('data-rw-submit-disabled-by-razorwire');
            }
        }

        isRazorWireForm(form) {
            return form instanceof HTMLFormElement
                && this.config.failureUxEnabled !== false
                && form.getAttribute('data-rw-form') === 'true'
                && this.getMode(form) !== 'off';
        }

        getForm(target) {
            if (target instanceof HTMLFormElement) return target;
            if (target instanceof Element) return target.closest('form[data-rw-form="true"]');
            return null;
        }

        getMode(form) {
            return (form.getAttribute('data-rw-form-failure') || this.config.failureMode || 'auto').toLowerCase();
        }

        getStatusCode(fetchResponse) {
            const status = fetchResponse?.response?.status;
            return typeof status === 'number' ? status : null;
        }

        isHandled(fetchResponse) {
            const header = fetchResponse?.response?.headers?.get?.('X-RazorWire-Form-Handled');
            return header === 'true' || header === '1';
        }

        getResponseKind(fetchResponse) {
            const contentType = fetchResponse?.response?.headers?.get?.('content-type') || '';
            if (contentType.includes('text/vnd.turbo-stream.html')) return 'turbo-stream';
            if (contentType.includes('text/html')) return 'html';
            if (contentType.includes('application/json')) return 'json';
            return fetchResponse?.response ? 'unknown' : 'network';
        }

        resolveTarget(form) {
            const explicit = form.getAttribute('data-rw-form-failure-target');
            if (explicit) {
                const target = this.resolveSelector(form, explicit);
                if (target.element) return target;

                return {
                    element: form.querySelector('[data-rw-form-errors]') || form,
                    diagnostic: this.diagnostic(
                        form,
                        null,
                        'RazorWire form failure target was not found',
                        `Could not resolve data-rw-form-failure-target="${explicit}".`,
                        ['Check that the target id or selector exists before the form submits.'])
                };
            }

            return { element: form.querySelector('[data-rw-form-errors]') || form, diagnostic: null };
        }

        resolveSelector(form, value) {
            if (value.startsWith('#')) {
                const byId = document.getElementById(value.slice(1));
                if (byId) return { element: byId, diagnostic: null };
            } else {
                const byId = document.getElementById(value);
                if (byId) return { element: byId, diagnostic: null };
            }

            try {
                const scoped = form.querySelector(value);
                if (scoped) return { element: scoped, diagnostic: null };
            } catch (error) {
                return {
                    element: null,
                    diagnostic: this.diagnostic(
                        form,
                        null,
                        'RazorWire form failure target selector is invalid',
                        String(error.message || error),
                        ['Use a valid CSS selector or a simple element id.'])
                };
            }

            try {
                const global = document.querySelector(value);
                if (global) return { element: global, diagnostic: null };
            } catch (error) {
                return {
                    element: null,
                    diagnostic: this.diagnostic(
                        form,
                        null,
                        'RazorWire form failure target selector is invalid',
                        String(error.message || error),
                        ['Use a valid CSS selector or a simple element id.'])
                };
            }

            return { element: null, diagnostic: null };
        }

        renderFailure(form, target, detail) {
            this.injectStyles();
            this.clearGeneratedFailure(form);
            const owner = this.ensureFormOwner(form);
            const role = detail.responseKind === 'network' || (detail.statusCode && detail.statusCode >= 500) ? 'alert' : 'status';
            const live = role === 'alert' ? 'assertive' : 'polite';
            const title = this.titleForStatus(detail.statusCode, detail.responseKind);
            const diagnostic = detail.developmentDiagnostic
                || (this.config.developmentDiagnostics ? this.diagnosticForFailure(detail) : null);
            detail.developmentDiagnostic = diagnostic;
            const block = document.createElement('div');
            block.id = `rw-form-error-${owner}-${this.nextId++}`;
            block.setAttribute('data-rw-form-error-generated', 'true');
            block.setAttribute('data-rw-form-error-owner', owner);
            block.setAttribute('data-rw-form-error-kind', detail.responseKind || 'unknown');
            block.setAttribute('role', role);
            block.setAttribute('aria-live', live);
            block.setAttribute('tabindex', '-1');
            block.innerHTML = `
                <strong data-rw-form-error-title="true"></strong>
                <p data-rw-form-error-message="true"></p>
                ${diagnostic ? '<div data-rw-form-error-diagnostic="true"><p data-rw-form-error-detail="true"></p><ul data-rw-form-error-hints="true"></ul></div>' : ''}
            `;
            block.querySelector('[data-rw-form-error-title="true"]').textContent = title;
            block.querySelector('[data-rw-form-error-message="true"]').textContent = detail.message;
            if (diagnostic) {
                block.querySelector('[data-rw-form-error-detail="true"]').textContent = diagnostic.detail;
                const hintList = block.querySelector('[data-rw-form-error-hints="true"]');
                diagnostic.hints.forEach(hint => {
                    const item = document.createElement('li');
                    item.textContent = hint;
                    hintList.appendChild(item);
                });
            }

            if (target === form) {
                form.prepend(block);
            } else {
                target.querySelectorAll(`[data-rw-form-error-generated="true"][data-rw-form-error-owner="${owner}"]`).forEach(el => el.remove());
                target.appendChild(block);
            }

            this.linkDescribedBy(form, block.id);
            if (document.activeElement === form || document.activeElement?.type === 'submit') {
                block.focus({ preventScroll: true });
                block.scrollIntoView({ block: 'nearest' });
            }
        }

        clearGeneratedFailure(form) {
            const owner = form.getAttribute('data-rw-form-owner');
            if (owner) {
                document.querySelectorAll(`[data-rw-form-error-generated="true"][data-rw-form-error-owner="${owner}"]`).forEach(el => el.remove());
            } else {
                form.querySelectorAll('[data-rw-form-error-generated="true"]').forEach(el => el.remove());
            }

            this.unlinkDescribedBy(form);
        }

        ensureFormOwner(form) {
            let owner = form.getAttribute('data-rw-form-owner');
            if (!owner) {
                owner = `form-${this.nextId++}`;
                form.setAttribute('data-rw-form-owner', owner);
            }

            return owner;
        }

        linkDescribedBy(form, id) {
            this.unlinkDescribedBy(form);
            const existing = (form.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean);
            form.setAttribute('aria-describedby', [...existing, id].join(' '));
            this.state.set(form, { ...(this.state.get(form) || {}), describedById: id });
        }

        unlinkDescribedBy(form) {
            const describedById = this.state.get(form)?.describedById;
            if (!describedById) return;

            const nextValue = (form.getAttribute('aria-describedby') || '')
                .split(/\s+/)
                .filter(value => value && value !== describedById)
                .join(' ');
            if (nextValue) form.setAttribute('aria-describedby', nextValue);
            else form.removeAttribute('aria-describedby');
        }

        injectStyles() {
            if (document.getElementById(this.styleId)) return;

            const style = document.createElement('style');
            style.id = this.styleId;
            style.textContent = `
:where([data-rw-form-error-generated="true"]) {
  border: 1px solid var(--rw-form-error-border, #d97706);
  border-radius: var(--rw-form-error-radius, 6px);
  background: var(--rw-form-error-bg, #fffbeb);
  color: var(--rw-form-error-text, #3f3f46);
  font: var(--rw-form-error-font, inherit);
  margin-block: var(--rw-form-error-spacing, .75rem);
  padding: .75rem .875rem;
  overflow-wrap: anywhere;
}
:where([data-rw-form-error-title="true"]) {
  color: var(--rw-form-error-title, #92400e);
  display: block;
  font-weight: 700;
  margin-block-end: .25rem;
}
:where([data-rw-form-error-message="true"], [data-rw-form-error-detail="true"]) {
  margin: .25rem 0 0;
}
:where([data-rw-form-error-hints="true"], [data-rw-form-error-list="true"]) {
  margin: .5rem 0 0;
  padding-inline-start: 1.25rem;
}
`;
            document.head.appendChild(style);
        }

        titleForStatus(statusCode, responseKind) {
            if (responseKind === 'network') return 'Could not reach the server';
            if (statusCode === 401 || statusCode === 403) return 'Session may have expired';
            if (statusCode && statusCode >= 500) return 'Something went wrong';
            return 'We could not submit this form';
        }

        messageForStatus(statusCode, responseKind) {
            if (responseKind === 'network') return 'We could not reach the server. Check your connection and try again.';
            if (statusCode === 401 || statusCode === 403) return 'You may need to refresh or sign in again before submitting this form.';
            if (statusCode && statusCode >= 500) return 'Something went wrong while submitting this form. Try again in a moment.';
            return this.config.defaultFailureMessage;
        }

        diagnosticForFailure(detail) {
            const hints = ['Check the response status and whether the server set X-RazorWire-Form-Handled for custom UI.'];
            if (detail.statusCode === 400) {
                hints.push('Check server logs or the response body for the Bad Request reason.');
                hints.push('For expected validation failures, return a handled stream with FormError or FormValidationErrors instead of a bare 400.');
            }

            return this.diagnostic(
                detail.form,
                detail.statusCode,
                'RazorWire form submission failed',
                `Response kind: ${detail.responseKind}.`,
                hints);
        }

        diagnostic(form, statusCode, title, detail, hints) {
            return {
                form,
                statusCode,
                title,
                detail,
                docsHref: 'Web/ForgeTrust.RazorWire/Docs/antiforgery.md',
                hints
            };
        }

        dispatch(form, name, detail, cancelable = false) {
            const event = new CustomEvent(name, { bubbles: true, cancelable, detail });
            form.dispatchEvent(event);
            return event;
        }
    }

    function readRuntimeConfig() {
        const script = document.currentScript || document.querySelector('script[src*="/razorwire/razorwire.js"]');
        const dataset = script?.dataset || {};

        return {
            developmentDiagnostics: dataset.rwDevelopmentDiagnostics === 'true',
            failureUxEnabled: dataset.rwFormFailureEnabled === undefined
                ? (dataset.rwFormFailureMode || 'auto').toLowerCase() !== 'off'
                : dataset.rwFormFailureEnabled !== 'false',
            failureMode: dataset.rwFormFailureMode || 'auto',
            defaultFailureMessage: dataset.rwDefaultFailureMessage || 'We could not submit this form. Check your input and try again.'
        };
    }

    // Initialize
    const runtimeConfig = readRuntimeConfig();
    const connectionManager = new ConnectionManager();
    const localTimeFormatter = new LocalTimeFormatter();
    const formFailureManager = new FormFailureManager(runtimeConfig);

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => {
            connectionManager.start();
            localTimeFormatter.start();
            formFailureManager.start();
        });
    } else {
        connectionManager.start();
        localTimeFormatter.start();
        formFailureManager.start();
    }

    window.RazorWire = {
        ...(window.RazorWire || {}),
        config: { ...((window.RazorWire && window.RazorWire.config) || {}), ...runtimeConfig },
        connectionManager,
        localTimeFormatter,
        formFailureManager
    };
    // Global safeguard: Block clicks on disabled elements or their children even if pointer-events are enabled
    document.addEventListener('click', (e) => {
        const selector = '[disabled], [aria-disabled="true"], [data-rw-requires-stream][disabled]';

        let target = e.target;
        if (!(target instanceof Element) && target.parentElement) {
            target = target.parentElement;
        }

        if (target instanceof Element) {
            const disabledElement = target.closest(selector);
            if (disabledElement) {
                e.preventDefault();
                e.stopPropagation();
            }
        }
    }, true); // Capture phase to intervene early

    console.log('✅ RazorWire Runtime Initialized');
})();
