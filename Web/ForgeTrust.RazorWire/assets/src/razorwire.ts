/**
 * RazorWire Core Client Runtime
 * Provides native stream monitoring and event dispatching.
 */
interface Window {
    RazorWireInitialized?: boolean;
    RazorWire?: {
        config?: Record<string, unknown>;
        connectionManager?: unknown;
        localTimeFormatter?: unknown;
        formFailureManager?: unknown;
        pageNavigationManager?: unknown;
        sectionCopyManager?: unknown;
    };
    Turbo?: TurboRuntime;
}

interface TurboRuntime {
    connectStreamSource?(source: EventSource): void;
    disconnectStreamSource?(source: EventSource): void;
    visit?(url: string, options?: { action?: string }): void;
    StreamActions?: Record<string, (this: Element) => void>;
}

interface StreamSourceRegistration {
    es: EventSource;
    elements: Set<Element>;
    closeTimer: ReturnType<typeof setTimeout> | null;
    state: string;
    channel: string | null;
}

interface RuntimeConfig {
    developmentDiagnostics: boolean;
    failureUxEnabled: boolean;
    failureMode: string;
    defaultFailureMessage: string;
    liveOrigin: string;
    hybridCredentials: string;
    antiforgeryEndpoint: string;
    productIntelligenceEnabled: boolean;
}

interface FormSubmitState {
    submitter: (HTMLElement & { disabled: boolean }) | null;
    disabledByRazorWire: boolean;
    describedById: string | null;
}

interface AntiforgeryTokenPayload {
    fieldName: string;
    requestToken: string;
    headerName: string;
}

declare const Turbo: TurboRuntime | undefined;

(function () {
    if (window.RazorWireInitialized) return;
    window.RazorWireInitialized = true;

    class ConnectionManager {
        sources: Map<string, StreamSourceRegistration>;
        channelStates: Map<string, string>;
        observer: MutationObserver;
        config: RuntimeConfig;

        constructor(config: RuntimeConfig) {
            this.config = config;
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
            for (const [, source] of this.sources) {
                for (const element of source.elements) {
                    if (!element.isConnected) {
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

                    const turboFrame = frame as Element & { reload?: () => void };
                    if (typeof turboFrame.reload === 'function') {
                        turboFrame.reload();
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
                const es = this.shouldIncludeCredentials(src)
                    ? new EventSource(src, { withCredentials: true })
                    : new EventSource(src);
                this.connectStreamSource(es);

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
                    this.dispatchStreamError(source, src);
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
                    this.disconnectStreamSource(source.es);
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

        dispatchStreamError(source, src) {
            if (!source) return;

            source.elements.forEach(element => {
                const event = new CustomEvent('razorwire:stream:error', {
                    bubbles: true,
                    cancelable: false,
                    detail: {
                        channel: source.channel,
                        source: element,
                        state: source.state,
                        readyState: source.es.readyState,
                        src
                    }
                });
                element.dispatchEvent(event);
            });
        }

        getChannelName(src) {
            if (!src) return null;
            try {
                const url = new URL(src, window.location.origin);
                const path = url.pathname.split('/').filter(Boolean).pop() || '';
                const channel = decodeURIComponent(path);
                return this.isValidChannelName(channel) ? channel : null;
            } catch {
                return null;
            }
        }

        isValidChannelName(channel) {
            return /^[A-Za-z0-9._:-]+$/.test(channel);
        }

        toAttributeToken(channel) {
            return channel.replace(/[^A-Za-z0-9_-]/g, '-');
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
            if (!channel) return;

            const attr = `data-rw-stream-${this.toAttributeToken(channel)}`;
            if (state && state !== 'disconnected') {
                document.body.setAttribute(attr, state);
            } else {
                document.body.removeAttribute(attr);
            }
        }

        connectStreamSource(source) {
            const turbo = resolveTurbo();
            if (typeof turbo?.connectStreamSource === 'function') {
                turbo.connectStreamSource(source);
            }
        }

        disconnectStreamSource(source) {
            const turbo = resolveTurbo();
            if (typeof turbo?.disconnectStreamSource === 'function') {
                turbo.disconnectStreamSource(source);
            }
        }

        shouldIncludeCredentials(src) {
            if (!this.shouldUseHybridCredentials() || !this.config.liveOrigin) {
                return false;
            }

            try {
                return new URL(src, window.location.href).origin === this.config.liveOrigin;
            } catch {
                return false;
            }
        }

        shouldUseHybridCredentials() {
            const mode = (this.config.hybridCredentials || '').toLowerCase();
            return mode === 'include' || (mode === 'auto' && !!this.config.liveOrigin);
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
        observer: MutationObserver;
        formatter: Intl.RelativeTimeFormat | null;
        updateInterval: ReturnType<typeof setInterval> | null;
        isStarted: boolean;
        visibleElements: Set<Element>;
        intersectionObserver: IntersectionObserver | null;

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

    /**
     * A RazorWire-enhanced form started submitting through Turbo.
     * @public
     * @namespace RazorWire
     * @event razorwire:form:submit-start
     * @target form[data-rw-form="true"]
     * @firesWhen Turbo begins submitting a RazorWire-enhanced form and RazorWire marks it busy.
     * @bubbles true
     * @cancelable false
     * @property {HTMLFormElement} detail.form - Submitted form.
     * @property {HTMLElement|null} detail.submitter - Button or submit control that initiated the submission.
     * @example
     * form.addEventListener('razorwire:form:submit-start', event => {
     *   event.detail.form.classList.add('is-saving');
     * });
     */

    /**
     * A RazorWire-enhanced form submission failed and custom UI may handle the failure.
     * @public
     * @namespace RazorWire
     * @event razorwire:form:failure
     * @target form[data-rw-form="true"]
     * @firesWhen a RazorWire-enhanced form receives an unhandled failure response or a network error.
     * @bubbles true
     * @cancelable true
     * @property {HTMLFormElement} detail.form - Submitted form.
     * @property {HTMLElement|null} detail.submitter - Button or submit control that initiated the submission.
     * @property {number|null} detail.statusCode - HTTP status code when a response was received.
     * @property {boolean} detail.handled - Whether the server response already handled the failure.
     * @property {string} detail.responseKind - Failure category: turbo-stream, html, json, unknown, or network.
     * @property {Element|null} detail.target - Element RazorWire will use for generated failure UI.
     * @property {string} detail.message - Reader-facing fallback message.
     * @property {Object|null} detail.developmentDiagnostic - Development-only diagnostic payload when enabled.
     * @example
     * form.addEventListener('razorwire:form:failure', event => {
     *   event.preventDefault();
     *   renderCustomFailure(event.detail);
     * });
     */

    /**
     * Development diagnostics are available for a failed RazorWire-enhanced form submission.
     * @public
     * @namespace RazorWire
     * @event razorwire:form:diagnostic
     * @target form[data-rw-form="true"]
     * @firesWhen development diagnostics are enabled and RazorWire can explain a failed form submission.
     * @bubbles true
     * @cancelable false
     * @property {HTMLFormElement} detail.form - Submitted form.
     * @property {number|null} detail.statusCode - HTTP status code when a response was received.
     * @property {string} detail.title - Short diagnostic title.
     * @property {string} detail.detail - Diagnostic explanation.
     * @property {string} detail.docsHref - Repository-relative documentation target for the diagnostic.
     * @property {string[]} detail.hints - Suggested fixes for the developer.
     * @example
     * form.addEventListener('razorwire:form:diagnostic', event => {
     *   console.debug(event.detail.title, event.detail.hints);
     * });
     */

    /**
     * A RazorWire-enhanced form finished submitting.
     * @public
     * @namespace RazorWire
     * @event razorwire:form:submit-end
     * @target form[data-rw-form="true"]
     * @firesWhen Turbo finishes a RazorWire-enhanced form submission or RazorWire handles a fetch error.
     * @bubbles true
     * @cancelable false
     * @property {HTMLFormElement} detail.form - Submitted form.
     * @property {HTMLElement|null} detail.submitter - Button or submit control that initiated the submission.
     * @property {boolean} detail.success - Whether Turbo reported a successful submission.
     * @property {number|null} detail.statusCode - HTTP status code when a response was received.
     * @property {boolean} detail.handled - Whether the server response already handled the result.
     * @example
     * form.addEventListener('razorwire:form:submit-end', event => {
     *   event.detail.form.classList.remove('is-saving');
     * });
     */

    /**
     * Enables RazorWire form failure handling on a form.
     * @public
     * @namespace RazorWire
     * @attribute data-rw-form
     * @target form
     * @type {"true"}
     * @default none
     */

    /**
     * Selects how RazorWire renders unhandled form failures.
     * @public
     * @namespace RazorWire
     * @attribute data-rw-form-failure
     * @target form[data-rw-form="true"]
     * @type {"auto"|"manual"|"off"}
     * @default auto
     */

    /**
     * Reader-facing message used when a failed form submission has no more specific explanation.
     * @public
     * @namespace RazorWire
     * @config defaultFailureMessage
     * @source script[data-rw-default-failure-message]
     * @type {string}
     * @default We could not submit this form. Check your input and try again.
     */

    /**
     * Stable selector for generated form failure UI.
     * @public
     * @namespace RazorWire
     * @cssHook [data-rw-form-error-generated="true"]
     * @hookKind data-attribute
     * @target generated form failure UI
     * @stability stable
     */

    /**
     * Controls generated form failure text color.
     * @public
     * @namespace RazorWire
     * @cssCustomProperty --rw-form-error-text
     * @target [data-rw-form-error-generated="true"]
     * @syntax <color>
     * @default #3f3f46
     */
    class FormFailureManager {
        config: RuntimeConfig;
        state: WeakMap<HTMLFormElement, Partial<FormSubmitState>>;
        antiforgeryRefreshes: WeakMap<HTMLFormElement, Promise<AntiforgeryTokenPayload | null>>;
        nextId: number;
        styleId: string;

        constructor(config: RuntimeConfig) {
            this.config = config;
            this.state = new WeakMap();
            this.antiforgeryRefreshes = new WeakMap();
            this.nextId = 1;
            this.styleId = 'rw-form-failure-default-styles';
        }

        start() {
            if (this.config.failureUxEnabled !== false && (this.config.failureMode || 'auto').toLowerCase() !== 'off') {
                this.injectStyles();
            }

            document.addEventListener('focusin', event => this.handleAntiforgeryIntent(event));
            document.addEventListener('pointerdown', event => this.handleAntiforgeryIntent(event));
            document.addEventListener('keydown', event => this.handleAntiforgeryIntent(event));
            document.addEventListener('turbo:before-fetch-request', event => this.handleBeforeFetchRequest(event));
            document.addEventListener('turbo:submit-start', event => this.handleSubmitStart(event));
            document.addEventListener('turbo:submit-end', event => this.handleSubmitEnd(event));
            document.addEventListener('turbo:fetch-request-error', event => this.handleFetchRequestError(event));
        }

        handleBeforeFetchRequest(event) {
            const form = this.getForm(event.target);
            if (!this.isRazorWireTransportForm(form)) return;

            event.detail.fetchOptions = event.detail.fetchOptions || {};
            if (this.shouldIncludeCredentials(form)) {
                event.detail.fetchOptions.credentials = 'include';
            }

            const headers = event.detail.fetchOptions.headers || {};
            if (typeof headers.set === 'function') {
                headers.set('X-RazorWire-Form', 'true');
            } else {
                headers['X-RazorWire-Form'] = 'true';
            }

            event.detail.fetchOptions.headers = headers;

            if (this.isLazyAntiforgeryForm(form) && !this.hasAntiforgeryToken(form)) {
                event.preventDefault?.();
                this.ensureAntiforgeryToken(form)
                    .then(token => {
                        this.applyAntiforgeryTokenToFetchOptions(event.detail?.fetchOptions, token);
                        event.detail?.resume?.();
                    })
                    .catch(error => this.handleAntiforgeryRefreshFailure(form, error));
            }
        }

        handleAntiforgeryIntent(event) {
            const form = this.getForm(event.target);
            if (!this.isRazorWireTransportForm(form) || !this.isLazyAntiforgeryForm(form) || this.hasAntiforgeryToken(form)) return;

            this.ensureAntiforgeryToken(form).catch(error => this.handleAntiforgeryIntentRefreshFailure(form, error));
        }

        handleAntiforgeryIntentRefreshFailure(form, error) {
            if (!this.isFormFailureEnabled(form)) {
                form.setAttribute('data-rw-antiforgery-state', 'failed');
                return;
            }

            this.handleAntiforgeryRefreshFailure(form, error);
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
            const previousFailureCount = this.getFailureAttemptCount(form);

            this.finishSubmitting(form, formState);

            if (success) {
                if (previousFailureCount > 0) {
                    this.dispatchProductIntelligenceEvent('razorwire.form.failure_recovered', {
                        recovery_action: 'next_success',
                        attempt_count: previousFailureCount
                    });
                }

                this.clearGeneratedFailure(form);
                form.removeAttribute('data-rw-submit-status');
                form.removeAttribute('data-rw-last-status');
                form.removeAttribute('data-rw-form-failure-count');
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

            this.recordFormFailure(form, failureDetail, handled
                ? 'handled'
                : (!failureEvent.defaultPrevented && this.getMode(form) === 'auto' ? 'generated' : 'suppressed'));
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

            this.recordFormFailure(
                form,
                failureDetail,
                !failureEvent.defaultPrevented && this.getMode(form) === 'auto' ? 'generated' : 'suppressed');
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

        isRazorWireForm(form): form is HTMLFormElement {
            return this.isRazorWireTransportForm(form)
                && this.isFormFailureEnabled(form);
        }

        isRazorWireTransportForm(form): form is HTMLFormElement {
            return form instanceof HTMLFormElement
                && form.getAttribute('data-rw-form') === 'true';
        }

        isFormFailureEnabled(form) {
            return this.config.failureUxEnabled !== false
                && this.getMode(form) !== 'off';
        }

        getForm(target): HTMLFormElement | null {
            if (target instanceof HTMLFormElement) return target;
            if (target instanceof Element) return target.closest('form[data-rw-form="true"]');
            return null;
        }

        isLazyAntiforgeryForm(form) {
            return form.getAttribute('data-rw-antiforgery') === 'lazy';
        }

        hasAntiforgeryToken(form) {
            const input = (form.querySelector('input[name="__RequestVerificationToken"]') as HTMLInputElement | null)
                || (form.querySelector('input[data-rw-antiforgery-token="true"]') as HTMLInputElement | null);
            return !!input?.value;
        }

        shouldIncludeCredentials(form) {
            if (this.shouldUseHybridCredentials()) {
                return this.isLiveOriginUrl(form.getAttribute('action') || window.location.href);
            }

            return false;
        }

        shouldUseHybridCredentials() {
            const mode = (this.config.hybridCredentials || '').toLowerCase();
            return mode === 'include' || (mode === 'auto' && !!this.config.liveOrigin);
        }

        isLiveOriginUrl(rawUrl) {
            if (!this.config.liveOrigin) return false;
            try {
                return new URL(rawUrl || window.location.href, window.location.href).origin === this.config.liveOrigin;
            } catch {
                return false;
            }
        }

        async ensureAntiforgeryToken(form): Promise<AntiforgeryTokenPayload | null> {
            if (this.hasAntiforgeryToken(form)) return null;

            const pendingRefresh = this.antiforgeryRefreshes.get(form);
            if (pendingRefresh) {
                return pendingRefresh;
            }

            const refresh = this.refreshAntiforgeryToken(form)
                .finally(() => this.antiforgeryRefreshes.delete(form));
            this.antiforgeryRefreshes.set(form, refresh);
            return refresh;
        }

        async refreshAntiforgeryToken(form): Promise<AntiforgeryTokenPayload> {
            form.setAttribute('data-rw-antiforgery-state', 'refreshing');
            const endpoint = this.resolveAntiforgeryEndpoint();
            const response = await fetch(endpoint, {
                method: 'GET',
                credentials: this.shouldUseHybridCredentials() ? 'include' : 'same-origin',
                headers: { 'Accept': 'application/json' },
                cache: 'no-store'
            });

            if (!response.ok) {
                throw new Error(`Token endpoint returned ${response.status}`);
            }

            const payload = await response.json();
            const fieldName = payload.formFieldName || '__RequestVerificationToken';
            const requestToken = payload.requestToken || '';
            if (!requestToken) {
                throw new Error('Token endpoint did not return a request token.');
            }

            let input = form.querySelector(`input[name="${this.escapeAttributeValue(fieldName)}"]`) as HTMLInputElement | null;
            if (!input) {
                input = document.createElement('input') as HTMLInputElement;
                input.type = 'hidden';
                input.name = fieldName;
                form.appendChild(input);
            }

            input.setAttribute('data-rw-antiforgery-token', 'true');
            input.value = requestToken;
            form.setAttribute('data-rw-antiforgery-state', 'ready');
            return {
                fieldName,
                requestToken,
                headerName: payload.headerName || 'RequestVerificationToken'
            };
        }

        applyAntiforgeryTokenToFetchOptions(fetchOptions, token) {
            if (!fetchOptions || !token) return;

            const headerName = token.headerName || 'RequestVerificationToken';
            const headers = fetchOptions.headers || {};
            if (typeof headers.set === 'function') {
                headers.set(headerName, token.requestToken);
            } else {
                headers[headerName] = token.requestToken;
            }

            fetchOptions.headers = headers;

            const body = fetchOptions.body;
            if (typeof FormData !== 'undefined' && body instanceof FormData) {
                body.set(token.fieldName, token.requestToken);
                return;
            }

            if (typeof URLSearchParams !== 'undefined' && body instanceof URLSearchParams) {
                body.set(token.fieldName, token.requestToken);
                return;
            }

            if (typeof body === 'string' && this.isFormUrlEncodedFetchBody(fetchOptions)) {
                const parameters = new URLSearchParams(body);
                parameters.set(token.fieldName, token.requestToken);
                fetchOptions.body = parameters.toString();
            }
        }

        isFormUrlEncodedFetchBody(fetchOptions) {
            const contentType = this.readRequestHeader(fetchOptions.headers, 'content-type');
            return typeof contentType === 'string'
                && contentType.toLowerCase().split(';', 1)[0].trim() === 'application/x-www-form-urlencoded';
        }

        readRequestHeader(headers, name) {
            if (!headers) return null;

            if (typeof headers.get === 'function') {
                return headers.get(name);
            }

            const expectedName = name.toLowerCase();
            if (Array.isArray(headers)) {
                const entry = headers.find(([key]) => String(key).toLowerCase() === expectedName);
                return entry ? entry[1] : null;
            }

            const key = Object.keys(headers).find(item => item.toLowerCase() === expectedName);
            return key ? headers[key] : null;
        }

        resolveAntiforgeryEndpoint() {
            const endpoint = this.config.antiforgeryEndpoint || '/_rw/antiforgery/token';
            if (this.config.liveOrigin) {
                return this.config.liveOrigin.replace(/\/$/, '') + endpoint;
            }

            return endpoint;
        }

        handleAntiforgeryRefreshFailure(form, error) {
            form.setAttribute('data-rw-antiforgery-state', 'failed');
            const formState = this.state.get(form) || {};
            const submitter = formState.submitter || null;
            const wasSubmitting = form.hasAttribute('data-rw-submitting');
            if (wasSubmitting) {
                this.finishSubmitting(form, formState);
                form.setAttribute('data-rw-submit-status', 'failed');
            }

            const target = this.resolveTarget(form);
            const detail = {
                form,
                submitter,
                statusCode: null,
                handled: false,
                responseKind: 'network',
                target: target.element,
                message: 'We could not prepare this form. Check your connection and try again.',
                developmentDiagnostic: this.config.developmentDiagnostics
                    ? this.diagnostic(
                        form,
                        null,
                        'RazorWire anti-forgery token refresh failed',
                        String(error?.message || error || 'The token endpoint could not be reached.'),
                        [
                            'Check that the live origin is reachable.',
                            'Check CORS credentials and allowed origins.',
                            'Check that MapRazorWire() is mapped on the live app.'
                        ])
                    : null
            };

            const failureEvent = this.dispatch(form, 'razorwire:form:failure', detail, true);
            if (detail.developmentDiagnostic) {
                this.dispatch(form, 'razorwire:form:diagnostic', detail.developmentDiagnostic);
            }

            if (!failureEvent.defaultPrevented && this.isFormFailureEnabled(form) && this.getMode(form) === 'auto') {
                this.renderFailure(form, target.element, detail);
            }

            if (wasSubmitting) {
                this.dispatch(form, 'razorwire:form:submit-end', {
                    form,
                    submitter,
                    success: false,
                    statusCode: null,
                    handled: false
                });
            }
        }

        escapeAttributeValue(value) {
            return String(value).replace(/\\/g, '\\\\').replace(/"/g, '\\"');
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
            const activeElement = document.activeElement as (Element & { type?: string }) | null;
            if (activeElement === form || activeElement?.type === 'submit') {
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

        getFailureAttemptCount(form) {
            const value = Number.parseInt(form.getAttribute('data-rw-form-failure-count') || '0', 10);
            return Number.isFinite(value) && value > 0 ? value : 0;
        }

        recordFormFailure(form, detail, failureUi) {
            const nextCount = this.getFailureAttemptCount(form) + 1;
            form.setAttribute('data-rw-form-failure-count', String(nextCount));
            this.dispatchProductIntelligenceEvent('razorwire.form.failed', {
                failure_mode: detail.handled ? 'handled' : (detail.responseKind || 'unknown'),
                http_status: detail.statusCode === null || detail.statusCode === undefined
                    ? ''
                    : String(detail.statusCode),
                response_kind: detail.responseKind || 'unknown',
                failure_ui: failureUi
            });
        }

        dispatchProductIntelligenceEvent(name, properties) {
            if (!this.config.productIntelligenceEnabled) {
                return;
            }

            const safeProperties = {};
            Object.entries(properties || {}).forEach(([key, value]) => {
                safeProperties[key] = String(value ?? '');
            });

            document.dispatchEvent(new CustomEvent('appsurface:product-intelligence:event', {
                bubbles: false,
                cancelable: false,
                detail: {
                    name,
                    properties: safeProperties
                }
            }));
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
            defaultFailureMessage: dataset.rwDefaultFailureMessage || 'We could not submit this form. Check your input and try again.',
            liveOrigin: normalizeOrigin(dataset.rwLiveOrigin || ''),
            hybridCredentials: dataset.rwHybridCredentials || 'auto',
            antiforgeryEndpoint: dataset.rwAntiforgeryEndpoint || '/_rw/antiforgery/token',
            productIntelligenceEnabled: dataset.rwProductIntelligenceEnabled === 'true'
        };
    }

    function normalizeOrigin(rawOrigin) {
        if (!rawOrigin) return '';

        try {
            const url = new URL(rawOrigin);
            const hasPath = url.pathname.replace(/\/+$/, '').length > 0;
            if ((url.protocol !== 'http:' && url.protocol !== 'https:')
                || hasPath
                || url.search
                || url.hash
                || url.username
                || url.password) {
                return '';
            }

            return url.origin;
        } catch {
            return '';
        }
    }

    function installVisitStreamAction() {
        const turbo = resolveTurbo();
        if (!turbo?.StreamActions || typeof turbo.visit !== 'function') {
            return;
        }

        turbo.StreamActions['rw-visit'] = function () {
            const visit = resolveVisitStream(this);
            if (!visit) {
                return;
            }

            turbo.visit!(visit.url, { action: visit.action });
        };
    }

    function resolveTurbo() {
        if (window.Turbo) {
            return window.Turbo;
        }

        if (typeof Turbo !== 'undefined') {
            return Turbo;
        }

        return null;
    }

    function resolveVisitStream(streamElement: Element | null | undefined) {
        const rawUrl = streamElement?.getAttribute?.('url') || '';
        const action = (streamElement?.getAttribute?.('visit-action') || 'advance').toLowerCase();
        if (action !== 'advance' && action !== 'replace') {
            return null;
        }

        const url = normalizeVisitUrl(rawUrl);
        if (!url) {
            return null;
        }

        return { url, action };
    }

    function normalizeVisitUrl(rawUrl: string) {
        if (typeof rawUrl !== 'string' || rawUrl.length === 0 || rawUrl.trim() !== rawUrl) {
            return null;
        }

        if (rawUrl.startsWith('~/') || rawUrl.startsWith('//') || rawUrl.startsWith('\\')) {
            return null;
        }

        if (hasAsciiControlCharacter(rawUrl)) {
            return null;
        }

        try {
            const baseHref = window.location?.href || `${window.location?.origin || ''}/`;
            const url = new URL(rawUrl, baseHref);
            if (url.origin !== window.location.origin) {
                return null;
            }

            return url.href;
        } catch {
            return null;
        }
    }

    function hasAsciiControlCharacter(value: string) {
        for (let index = 0; index < value.length; index += 1) {
            const code = value.charCodeAt(index);
            if (code <= 0x1F || code === 0x7F) {
                return true;
            }
        }

        return false;
    }

    // Initialize
    const runtimeConfig = readRuntimeConfig();
    installVisitStreamAction();
    const connectionManager = new ConnectionManager(runtimeConfig);
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

    /**
     * Browser global that exposes RazorWire runtime managers and runtime configuration for diagnostics and advanced integrations.
     * @public
     * @namespace RazorWire
     * @global
     */
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

        let target: EventTarget | Element | null = e.target;
        const parentTarget = target as EventTarget & { parentElement?: Element };
        if (!(target instanceof Element) && parentTarget.parentElement) {
            target = parentTarget.parentElement;
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
