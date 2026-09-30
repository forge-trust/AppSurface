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
        formInteractionsManager?: unknown;
        behaviors?: unknown;
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

interface AttributeSnapshot {
    present: boolean;
    value: string | null;
}

interface LoadingVisual {
    kind: 'indicator' | 'fallback';
    element: HTMLElement;
}

interface LoadingStateNode {
    element: Element;
    attributeName: string;
}

interface FormLoadingAttempt {
    form: HTMLFormElement;
    fetchOptions: Record<string, unknown>;
    signal: AbortSignal | null;
    submission: object | null;
    lockEnabled: boolean;
    settled: boolean;
    stateNodes: LoadingStateNode[];
    visual: LoadingVisual | null;
    submitControls: Set<SubmitControl>;
    abortListener: (() => void) | null;
}

interface AttributeReference {
    count: number;
    snapshot: AttributeSnapshot;
}

interface HiddenReference {
    count: number;
    snapshot: AttributeSnapshot;
}

type SubmitControl = HTMLButtonElement | HTMLInputElement;

interface SubmitControlReference {
    count: number;
    markerSnapshot: AttributeSnapshot;
}

interface AntiforgeryContinuation {
    form: HTMLFormElement;
    fetchOptions: Record<string, unknown>;
    resume: () => void;
    resumed: boolean;
    canceled: boolean;
    signal: AbortSignal | null;
    abortListener: (() => void) | null;
}

/**
 * Public form-loading markup and runtime contract:
 * `data-rw-loading="true"` opts a Tag Helper-owned form into loading feedback;
 * `data-rw-loading="off"` opts that form out. The nearest
 * `data-rw-loading-boundary` owns its first non-nested
 * `data-rw-loading-indicator`. RazorWire temporarily removes only that
 * indicator's `hidden` attribute and sets `data-rw-loading-state="pending"`
 * on the form and selected boundary. If no indicator applies, the
 * `data-rw-form-loading-show-fallback-bar` script setting controls the
 * package-owned `data-rw-loading-fallback` status.
 *
 * The script settings `data-rw-form-loading-enabled`,
 * `data-rw-form-loading-show-fallback-bar`, and
 * `data-rw-form-loading-prevent-duplicate-submissions` default to `true`.
 * A form's `data-rw-loading-lock="true|false"` overrides only the last
 * setting; these two values are matched case-insensitively and all other values
 * use the script setting. While locked, RazorWire disables enabled submit controls associated
 * with the form and restores only controls it disabled. The capture-phase
 * submit guard remains authoritative; direct `form.submit()` bypasses it.
 *
 * The packaged stylesheet uses
 * `html[data-rw-loading-turbo-bar="suppress"] .turbo-progress-bar` for Turbo
 * bar arbitration. Local indicators remain app-authored and app-styled.
 * @public
 */
interface RuntimeConfig {
    developmentDiagnostics: boolean;
    failureUxEnabled: boolean;
    failureMode: string;
    defaultFailureMessage: string;
    liveOrigin: string;
    hybridCredentials: string;
    antiforgeryEndpoint: string;
    productIntelligenceEnabled: boolean;
    /** Reads `data-rw-form-loading-enabled`; absent or invalid values use the enabled default. */
    formLoadingEnabled: boolean;
    /** Reads `data-rw-form-loading-show-fallback-bar`; controls the package-owned status bar. */
    formLoadingShowFallbackBar: boolean;
    /** Reads `data-rw-form-loading-prevent-duplicate-submissions`; also controls submitter locking. */
    formLoadingPreventDuplicateSubmissions: boolean;
}

interface RazorWireBehaviorQueueItem {
    kind: 'register' | 'registerLifecycle';
    definition: unknown;
}

interface RazorWireBehaviorStub {
    __razorWireBehaviorStub?: true;
    __queue?: RazorWireBehaviorQueueItem[];
    __diagnostics?: unknown[];
    register(definition: unknown): void;
    registerLifecycle(definition: unknown): void;
    scan(root?: Document | Element): void;
    prune(): void;
    getDiagnostics(): unknown[];
    clearDiagnostics(): void;
}

interface FormSubmitState {
    submitter: (HTMLElement & { disabled: boolean }) | null;
    disabledByRazorWire: boolean;
    describedById: string | null;
    submission: object | null;
    fetchOptions: object | null;
    settled: boolean;
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

    /**
     * Enables pending feedback on a RazorWire form; `off` opts out regardless of case.
     * @public
     * @namespace RazorWire
     * @attribute data-rw-loading
     * @target form
     * @type {"true"|"off"}
     * @default none
     */

    /**
     * Selects the nearest owner for a form's local pending indicator.
     * @public
     * @namespace RazorWire
     * @attribute data-rw-loading-boundary
     * @target ancestor element
     * @type {boolean}
     * @default none; only marked ancestors are boundaries
     */

    /**
     * Marks an indicator owned by its nearest loading boundary.
     * @public
     * @namespace RazorWire
     * @attribute data-rw-loading-indicator
     * @target element inside [data-rw-loading-boundary]
     * @type {boolean}
     * @default none
     */

    /**
     * Globally enables RazorWire form loading feedback.
     * @public
     * @namespace RazorWire
     * @config formLoadingEnabled
     * @source script[data-rw-form-loading-enabled]
     * @type {boolean}
     * @default true
     */

    /**
     * Enables the generated accessible fallback status when no local indicator applies.
     * @public
     * @namespace RazorWire
     * @config formLoadingShowFallbackBar
     * @source script[data-rw-form-loading-show-fallback-bar]
     * @type {boolean}
     * @default true
     */

    /**
     * Prevents another submit while a loading-enabled form request is pending.
     * @public
     * @namespace RazorWire
     * @config formLoadingPreventDuplicateSubmissions
     * @source script[data-rw-form-loading-prevent-duplicate-submissions]
     * @type {boolean}
     * @default true
     */

    /**
     * Per-form override for submit locking; only `true` and `false` override the global setting.
     * @public
     * @namespace RazorWire
     * @attribute data-rw-loading-lock
     * @target form[data-rw-loading="true"]
     * @type {"true"|"false"}
     * @default script setting
     */

    /**
     * Marks the generated accessible loading fallback in the packaged stylesheet.
     * @public
     * @namespace RazorWire
     * @cssHook [data-rw-loading-fallback]
     * @hookKind data-attribute
     * @target generated loading status
     * @stability stable
     */

    /**
     * Suppresses Turbo's progress bar while RazorWire owns visible form feedback.
     * @public
     * @namespace RazorWire
     * @cssHook html[data-rw-loading-turbo-bar="suppress"]
     * @hookKind data-attribute
     * @target Turbo progress bar
     * @stability stable
     */

    /** Owns pending RazorWire form feedback independently from failed-form UX. */
    class FormLoadingManager {
        config: RuntimeConfig;
        attempts: Set<FormLoadingAttempt>;
        attemptsByForm: Map<HTMLFormElement, Set<FormLoadingAttempt>>;
        attemptsByFetchOptions: WeakMap<object, FormLoadingAttempt>;
        attemptsBySignal: WeakMap<object, FormLoadingAttempt>;
        attemptsBySubmission: WeakMap<object, FormLoadingAttempt>;
        attributeReferences: Map<Element, Map<string, AttributeReference>>;
        hiddenReferences: Map<HTMLElement, HiddenReference>;
        submitControlReferences: Map<SubmitControl, SubmitControlReference>;
        fallbackElement: HTMLElement | null;
        domObserver: MutationObserver | null;
        turboVisitPending: boolean;
        visitFallbackHandoff: LoadingVisual | null;
        turboBarSuppressed: boolean;
        turboBarSnapshot: AttributeSnapshot | null;
        formFailureManager: FormFailureManager | null;

        constructor(config: RuntimeConfig) {
            this.config = config;
            this.attempts = new Set();
            this.attemptsByForm = new Map();
            this.attemptsByFetchOptions = new WeakMap();
            this.attemptsBySignal = new WeakMap();
            this.attemptsBySubmission = new WeakMap();
            this.attributeReferences = new Map();
            this.hiddenReferences = new Map();
            this.submitControlReferences = new Map();
            this.fallbackElement = null;
            this.domObserver = null;
            this.turboVisitPending = false;
            this.visitFallbackHandoff = null;
            this.turboBarSuppressed = false;
            this.turboBarSnapshot = null;
            this.formFailureManager = null;
        }

        setFormFailureManager(manager: FormFailureManager) {
            this.formFailureManager = manager;
        }

        start() {
            if (!this.config.formLoadingEnabled) return;

            document.addEventListener('turbo:before-fetch-request', event => this.handleBeforeFetchRequest(event));
            document.addEventListener('turbo:submit-start', event => this.handleSubmitStart(event));
            document.addEventListener('turbo:submit-end', event => this.handleSubmitEnd(event));
            document.addEventListener('turbo:fetch-request-error', event => this.handleFetchRequestError(event));
            document.addEventListener('turbo:visit', () => this.handleVisitStart());
            document.addEventListener('turbo:load', () => this.handleVisitEnd());
            document.addEventListener('turbo:reload', () => this.handleVisitEnd());
            document.addEventListener('submit', event => this.handleSubmitCapture(event), true);
            window.addEventListener('pagehide', () => this.handlePageHide());
        }

        handleBeforeFetchRequest(event) {
            const form = event.target instanceof HTMLFormElement ? event.target : null;
            if (!form || !this.isLoadingEnabled(form)) return;

            const detail = event.detail || {};
            const fetchOptions = detail.fetchOptions;
            if (!fetchOptions || typeof fetchOptions !== 'object') return;

            const optionsKey = fetchOptions as object;
            const existingAttempt = this.attemptsByFetchOptions.get(optionsKey);
            if (existingAttempt && !existingAttempt.settled) return;

            const rawSignal = fetchOptions.signal;
            const signal = rawSignal && typeof rawSignal === 'object'
                ? rawSignal as AbortSignal
                : null;
            if (signal?.aborted) return;

            const attempt: FormLoadingAttempt = {
                form,
                fetchOptions,
                signal,
                submission: null,
                lockEnabled: this.isLockEnabled(form),
                settled: false,
                stateNodes: [],
                visual: null,
                submitControls: new Set(),
                abortListener: null
            };

            this.attempts.add(attempt);
            this.attemptsByFetchOptions.set(optionsKey, attempt);
            if (signal) {
                this.attemptsBySignal.set(signal, attempt);
                attempt.abortListener = () => this.settleAttempt(attempt, 'aborted');
                signal.addEventListener('abort', attempt.abortListener, { once: true });
            }

            const formAttempts = this.attemptsByForm.get(form) || new Set<FormLoadingAttempt>();
            formAttempts.add(attempt);
            this.attemptsByForm.set(form, formAttempts);

            this.observePendingDom();
            this.beginAttempt(attempt);
            this.updateTurboBarState();
        }

        handleSubmitStart(event) {
            const submission = event.detail?.formSubmission;
            if (!submission || typeof submission !== 'object') return;

            const fetchOptions = this.getSubmissionFetchOptions(submission);
            const attempt = fetchOptions
                ? this.attemptsByFetchOptions.get(fetchOptions)
                : null;
            if (!attempt || attempt.settled) return;

            attempt.submission = submission;
            this.attemptsBySubmission.set(submission, attempt);
        }

        handleSubmitEnd(event) {
            const submission = event.detail?.formSubmission;
            const attempt = this.getAttemptForSubmission(submission);
            if (attempt) this.settleAttempt(attempt, 'submit-end');
        }

        handleFetchRequestError(event) {
            const request = event.detail?.request;
            const fetchOptions = request?.fetchOptions;
            const attempt = fetchOptions && typeof fetchOptions === 'object'
                ? this.attemptsByFetchOptions.get(fetchOptions)
                    || this.getAttemptForSignal(fetchOptions.signal)
                : null;

            if (attempt) {
                this.settleAttempt(attempt, 'network-error');
            }
        }

        handleSubmitCapture(event) {
            const form = this.getForm(event.target);
            if (!form) return;

            const attempts = this.attemptsByForm.get(form);
            if (!attempts || !Array.from(attempts).some(attempt => !attempt.settled && attempt.lockEnabled)) {
                return;
            }

            event.preventDefault?.();
            event.stopPropagation?.();
        }

        handlePageHide() {
            this.turboVisitPending = false;
            this.releaseVisitFallbackHandoff();
            for (const attempt of Array.from(this.attempts)) {
                this.settleAttempt(attempt, 'pagehide');
            }
            if (this.attempts.size === 0) this.disconnectPendingDomObserver();
            this.updateTurboBarState();
        }

        handleVisitStart() {
            this.turboVisitPending = true;
            this.updateTurboBarState();
        }

        handleVisitEnd() {
            this.turboVisitPending = false;
            this.releaseVisitFallbackHandoff();
            if (this.attempts.size === 0) this.disconnectPendingDomObserver();
            this.updateTurboBarState();
        }

        retainFallbackForVisit() {
            if (this.visitFallbackHandoff || !this.config.formLoadingShowFallbackBar) return;

            const fallback = this.ensureFallback();
            if (!fallback) return;

            const visual: LoadingVisual = { kind: 'fallback', element: fallback };
            this.acquireVisual(visual);
            this.visitFallbackHandoff = visual;
        }

        releaseVisitFallbackHandoff() {
            if (!this.visitFallbackHandoff) return;

            const visual = this.visitFallbackHandoff;
            this.visitFallbackHandoff = null;
            this.releaseVisual(visual);
        }

        beginAttempt(attempt: FormLoadingAttempt) {
            const selected = this.resolveVisual(attempt.form);
            const nextStateNodes: LoadingStateNode[] = [
                { element: attempt.form, attributeName: 'data-rw-loading-state' }
            ];
            if (selected.boundary && selected.boundary !== attempt.form) {
                nextStateNodes.push({ element: selected.boundary, attributeName: 'data-rw-loading-state' });
            }
            if (document.documentElement) {
                nextStateNodes.push({ element: document.documentElement, attributeName: 'data-rw-loading-form-state' });
            }

            for (const stateNode of nextStateNodes) {
                this.acquireAttribute(stateNode.element, stateNode.attributeName, 'pending');
            }
            if (selected.visual) this.acquireVisual(selected.visual);
            if (attempt.lockEnabled) {
                for (const control of this.getSubmitControls(attempt.form)) {
                    if (this.acquireSubmitControl(control)) attempt.submitControls.add(control);
                }
            }

            attempt.stateNodes = nextStateNodes;
            attempt.visual = selected.visual;
        }

        observePendingDom() {
            if (this.domObserver || typeof MutationObserver === 'undefined' || !document.documentElement) return;

            this.domObserver = new MutationObserver(records => {
                const relevant = records.filter(record => this.isRelevantLoadingMutation(record));
                if (relevant.length > 0 || records.length === 0) {
                    this.reconcilePendingAttempts(relevant);
                }
            });
            this.domObserver.observe(document.documentElement, {
                childList: true,
                subtree: true,
                attributes: true,
                attributeFilter: ['data-rw-loading-boundary', 'data-rw-loading-indicator']
            });
        }

        disconnectPendingDomObserver() {
            if (this.attempts.size > 0 || this.visitFallbackHandoff) return;
            this.domObserver?.disconnect();
            this.domObserver = null;
        }

        isRelevantLoadingMutation(record: MutationRecord) {
            if (record.type === 'attributes') return true;

            const selector = '[data-rw-loading], [data-rw-loading-boundary], '
                + '[data-rw-loading-indicator], [data-rw-loading-fallback]';
            for (const node of [...Array.from(record.addedNodes), ...Array.from(record.removedNodes)]) {
                if (node instanceof Element && (node.matches(selector) || node.querySelector(selector))) {
                    return true;
                }
            }
            return false;
        }

        loadingMutationScopes(records: MutationRecord[]) {
            const scopes: Element[] = [];
            for (const record of records) {
                const target = record.target instanceof Element ? record.target : document.body;
                if (!target) continue;

                if (record.type === 'attributes') {
                    const scope = record.attributeName === 'data-rw-loading-boundary'
                        ? target
                        : this.nearestBoundary(target) || target;
                    scopes.push(scope);
                    continue;
                }

                for (const node of [...Array.from(record.addedNodes), ...Array.from(record.removedNodes)]) {
                    if (!(node instanceof Element)) continue;
                    const scope = node.isConnected
                        ? this.nearestBoundary(node) || node
                        : this.nearestBoundary(target) || target;
                    scopes.push(scope);
                }
            }
            return scopes;
        }

        reconcilePendingAttempts(records: MutationRecord[] = []) {
            const scopes = this.loadingMutationScopes(records);
            for (const attempt of Array.from(this.attempts)) {
                if (attempt.settled) continue;
                if (!attempt.form.isConnected) {
                    this.settleAttempt(attempt, 'form-disconnected');
                    continue;
                }

                if (records.length === 0 || scopes.some(scope => scope.contains(attempt.form)
                    || attempt.form.contains(scope))) {
                    this.reconcileAttemptDom(attempt);
                }
            }

            this.reconcileVisitFallbackHandoff();
            this.updateTurboBarState();
        }

        reconcileVisitFallbackHandoff() {
            const current = this.visitFallbackHandoff;
            if (!current || current.element.isConnected) return;

            const fallback = this.ensureFallback();
            if (!fallback || fallback === current.element) return;

            const next: LoadingVisual = { kind: 'fallback', element: fallback };
            this.acquireVisual(next);
            this.visitFallbackHandoff = next;
            this.releaseVisual(current);
        }

        reconcileAttemptDom(attempt: FormLoadingAttempt) {
            const selected = this.resolveVisual(attempt.form);
            const nextStateNodes: LoadingStateNode[] = [
                { element: attempt.form, attributeName: 'data-rw-loading-state' }
            ];
            if (selected.boundary && selected.boundary !== attempt.form) {
                nextStateNodes.push({ element: selected.boundary, attributeName: 'data-rw-loading-state' });
            }
            if (document.documentElement) {
                nextStateNodes.push({ element: document.documentElement, attributeName: 'data-rw-loading-form-state' });
            }

            for (const stateNode of nextStateNodes) {
                if (!attempt.stateNodes.some(current => current.element === stateNode.element
                    && current.attributeName === stateNode.attributeName)) {
                    this.acquireAttribute(stateNode.element, stateNode.attributeName, 'pending');
                }
            }
            for (const stateNode of attempt.stateNodes) {
                if (!nextStateNodes.some(next => next.element === stateNode.element
                    && next.attributeName === stateNode.attributeName)) {
                    this.releaseAttribute(stateNode.element, stateNode.attributeName);
                }
            }
            attempt.stateNodes = nextStateNodes;

            const visualUnchanged = attempt.visual?.kind === selected.visual?.kind
                && attempt.visual?.element === selected.visual?.element;
            if (!visualUnchanged) {
                if (selected.visual) this.acquireVisual(selected.visual);
                if (attempt.visual) this.releaseVisual(attempt.visual);
                attempt.visual = selected.visual;
            }
        }

        resolveVisual(form: HTMLFormElement) {
            let boundary: Element | null = form;
            while (boundary) {
                if (boundary.hasAttribute('data-rw-loading-boundary')) {
                    const indicator = this.firstOwnedIndicator(boundary);
                    if (indicator) {
                        return {
                            boundary,
                            visual: { kind: 'indicator', element: indicator } as LoadingVisual
                        };
                    }
                }
                boundary = boundary.parentElement;
            }

            const fallback = this.config.formLoadingShowFallbackBar ? this.ensureFallback() : null;
            return {
                boundary: null,
                visual: fallback ? { kind: 'fallback', element: fallback } as LoadingVisual : null
            };
        }

        firstOwnedIndicator(boundary: Element) {
            const candidates: Element[] = [];
            if (boundary.matches('[data-rw-loading-indicator]')) candidates.push(boundary);
            candidates.push(...Array.from(boundary.querySelectorAll('[data-rw-loading-indicator]')));
            return candidates.find(candidate => candidate.isConnected
                && this.nearestBoundary(candidate) === boundary) as HTMLElement | undefined || null;
        }

        nearestBoundary(element: Element) {
            let current: Element | null = element;
            while (current) {
                if (current.hasAttribute('data-rw-loading-boundary')) return current;
                current = current.parentElement;
            }
            return null;
        }

        ensureFallback() {
            if (!this.config.formLoadingEnabled || !this.config.formLoadingShowFallbackBar || !document.body) {
                return null;
            }

            if (this.fallbackElement?.isConnected && this.fallbackElement.parentElement === document.body) {
                return this.fallbackElement;
            }

            const fallback = document.createElement('div');
            fallback.setAttribute('data-rw-ui', 'loading-fallback');
            fallback.setAttribute('data-rw-loading-fallback', '');
            fallback.setAttribute('role', 'status');
            fallback.setAttribute('aria-live', 'polite');
            fallback.setAttribute('aria-atomic', 'true');
            fallback.textContent = 'Loading…';
            fallback.setAttribute('hidden', '');
            document.body.appendChild(fallback);
            this.fallbackElement = fallback;
            return fallback;
        }

        getSubmitControls(form: HTMLFormElement): Set<SubmitControl> {
            const formElements = form.elements ? Array.from(form.elements) : Array.from(form.querySelectorAll('button, input'));
            return new Set(formElements.filter((element): element is SubmitControl =>
                element instanceof HTMLElement && this.isSubmitControl(element)));
        }

        isSubmitControl(element: HTMLElement): element is SubmitControl {
            const tagName = element.tagName.toUpperCase();
            const type = String((element as HTMLButtonElement | HTMLInputElement).type || '').toLowerCase();
            if (tagName === 'BUTTON') return type === '' || type === 'submit';
            return tagName === 'INPUT' && (type === 'submit' || type === 'image');
        }

        acquireSubmitControl(control: SubmitControl) {
            const existing = this.submitControlReferences.get(control);
            if (existing) {
                existing.count += 1;
                return true;
            }

            const markerName = 'data-rw-loading-disabled-by-razorwire';
            if (control.disabled || control.hasAttribute(markerName)) return false;

            this.submitControlReferences.set(control, {
                count: 1,
                markerSnapshot: this.snapshotAttribute(control, markerName)
            });
            control.disabled = true;
            control.setAttribute(markerName, 'true');
            return true;
        }

        releaseSubmitControl(control: SubmitControl) {
            const reference = this.submitControlReferences.get(control);
            if (!reference) return;

            reference.count -= 1;
            if (reference.count > 0) return;

            const markerName = 'data-rw-loading-disabled-by-razorwire';
            if (control.disabled && control.getAttribute(markerName) === 'true') {
                control.disabled = false;
            }
            this.restoreAttribute(control, markerName, reference.markerSnapshot);
            this.submitControlReferences.delete(control);
        }

        acquireVisual(visual: LoadingVisual) {
            const existing = this.hiddenReferences.get(visual.element);
            if (existing) {
                existing.count += 1;
            } else {
                this.hiddenReferences.set(visual.element, {
                    count: 1,
                    snapshot: this.snapshotAttribute(visual.element, 'hidden')
                });
            }
            visual.element.removeAttribute('hidden');
        }

        releaseVisual(visual: LoadingVisual) {
            const reference = this.hiddenReferences.get(visual.element);
            if (!reference) return;

            reference.count -= 1;
            if (reference.count > 0) return;

            this.restoreAttribute(visual.element, 'hidden', reference.snapshot);
            this.hiddenReferences.delete(visual.element);
        }

        acquireAttribute(element: Element, name: string, pendingValue: string) {
            let references = this.attributeReferences.get(element);
            if (!references) {
                references = new Map();
                this.attributeReferences.set(element, references);
            }

            let reference = references.get(name);
            if (!reference) {
                reference = {
                    count: 0,
                    snapshot: this.snapshotAttribute(element, name)
                };
                references.set(name, reference);
            }

            reference.count += 1;
            element.setAttribute(name, pendingValue);
        }

        releaseAttribute(element: Element, name: string) {
            const references = this.attributeReferences.get(element);
            const reference = references?.get(name);
            if (!references || !reference) return;

            reference.count -= 1;
            if (reference.count > 0) return;

            this.restoreAttribute(element, name, reference.snapshot);
            references.delete(name);
            if (references.size === 0) this.attributeReferences.delete(element);
        }

        snapshotAttribute(element: Element, name: string): AttributeSnapshot {
            return {
                present: element.hasAttribute(name),
                value: element.getAttribute(name)
            };
        }

        restoreAttribute(element: Element, name: string, snapshot: AttributeSnapshot) {
            if (snapshot.present) element.setAttribute(name, snapshot.value ?? '');
            else element.removeAttribute(name);
        }

        settleAttempt(attempt: FormLoadingAttempt, reason: string) {
            if (attempt.settled) return;

            attempt.settled = true;
            if (this.turboVisitPending && attempt.visual) this.retainFallbackForVisit();
            if (reason === 'form-disconnected' || reason === 'aborted' || reason === 'pagehide') {
                this.formFailureManager?.cancelPendingAntiforgery(attempt.fetchOptions);
            }

            this.attempts.delete(attempt);
            this.attemptsByFetchOptions.delete(attempt.fetchOptions);
            if (attempt.signal) {
                this.attemptsBySignal.delete(attempt.signal);
                if (attempt.abortListener) {
                    attempt.signal.removeEventListener('abort', attempt.abortListener);
                }
            }
            if (attempt.submission) this.attemptsBySubmission.delete(attempt.submission);

            for (const stateNode of attempt.stateNodes) {
                this.releaseAttribute(stateNode.element, stateNode.attributeName);
            }
            if (attempt.visual) this.releaseVisual(attempt.visual);
            for (const control of attempt.submitControls) this.releaseSubmitControl(control);

            const formAttempts = this.attemptsByForm.get(attempt.form);
            formAttempts?.delete(attempt);
            if (formAttempts?.size === 0) this.attemptsByForm.delete(attempt.form);
            if (this.attempts.size === 0) this.disconnectPendingDomObserver();
            this.updateTurboBarState();
        }

        settleForFetchOptions(fetchOptions, reason: string) {
            if (!fetchOptions || typeof fetchOptions !== 'object') return;
            const attempt = this.attemptsByFetchOptions.get(fetchOptions);
            if (attempt) this.settleAttempt(attempt, reason);
        }

        cancelPendingAntiforgery(fetchOptions) {
            this.formFailureManager?.cancelPendingAntiforgery(fetchOptions);
        }

        updateTurboBarState() {
            const activeVisuals = Array.from(this.attempts)
                .filter(attempt => !attempt.settled)
                .map(attempt => attempt.visual)
                .filter((visual): visual is LoadingVisual => visual !== null);
            const hasFallback = this.visitFallbackHandoff !== null
                || activeVisuals.some(visual => visual.kind === 'fallback');
            const hasAppIndicator = activeVisuals.some(visual => visual.kind === 'indicator');
            const shouldSuppress = hasFallback || (hasAppIndicator && !this.turboVisitPending);
            if (shouldSuppress === this.turboBarSuppressed) return;

            const root = document.documentElement;
            if (!root) return;

            const attributeName = 'data-rw-loading-turbo-bar';
            if (shouldSuppress) {
                this.turboBarSnapshot = this.snapshotAttribute(root, attributeName);
                root.setAttribute(attributeName, 'suppress');
            } else if (this.turboBarSnapshot) {
                this.restoreAttribute(root, attributeName, this.turboBarSnapshot);
                this.turboBarSnapshot = null;
            }
            this.turboBarSuppressed = shouldSuppress;
        }

        isLoadingEnabled(form: HTMLFormElement) {
            const setting = form.getAttribute('data-rw-loading');
            return this.config.formLoadingEnabled
                && setting !== null
                && setting.toLowerCase() !== 'off'
                && setting === 'true';
        }

        isLockEnabled(form: HTMLFormElement) {
            const override = form.getAttribute('data-rw-loading-lock')?.toLowerCase();
            if (override === 'true') return true;
            if (override === 'false') return false;
            return this.config.formLoadingPreventDuplicateSubmissions;
        }

        getForm(target): HTMLFormElement | null {
            if (target instanceof HTMLFormElement) return target;
            if (target instanceof Element) {
                const form = target.closest('form');
                return form instanceof HTMLFormElement ? form : null;
            }
            return null;
        }

        getSubmissionFetchOptions(submission): Record<string, unknown> | null {
            const options = submission?.fetchRequest?.fetchOptions;
            return options && typeof options === 'object' ? options : null;
        }

        getAttemptForSubmission(submission): FormLoadingAttempt | null {
            if (!submission || typeof submission !== 'object') return null;
            const known = this.attemptsBySubmission.get(submission);
            if (known && !known.settled) return known;

            const fetchOptions = this.getSubmissionFetchOptions(submission);
            if (!fetchOptions) return null;
            return this.attemptsByFetchOptions.get(fetchOptions)
                || this.getAttemptForSignal(fetchOptions.signal);
        }

        getAttemptForSignal(signal): FormLoadingAttempt | null {
            if (!signal || typeof signal !== 'object') return null;
            const attempt = this.attemptsBySignal.get(signal);
            return attempt && !attempt.settled ? attempt : null;
        }

    }

    class FormFailureManager {
        config: RuntimeConfig;
        loadingManager: FormLoadingManager;
        state: WeakMap<HTMLFormElement, Partial<FormSubmitState>>;
        submissionStates: WeakMap<object, FormSubmitState>;
        fetchOptionStates: WeakMap<object, FormSubmitState>;
        activeStates: WeakMap<HTMLFormElement, Set<FormSubmitState>>;
        antiforgeryRefreshes: WeakMap<HTMLFormElement, Promise<AntiforgeryTokenPayload | null>>;
        antiforgeryContinuations: WeakMap<object, AntiforgeryContinuation>;
        activeAntiforgeryContinuations: Set<AntiforgeryContinuation>;
        antiforgeryDomObserver: MutationObserver | null;
        preparationFailures: WeakMap<object, true>;
        canceledPreparations: WeakMap<object, true>;
        reportedAntiforgeryFailures: WeakSet<Promise<AntiforgeryTokenPayload | null>>;
        nextId: number;
        styleId: string;

        constructor(config: RuntimeConfig, loadingManager: FormLoadingManager) {
            this.config = config;
            this.loadingManager = loadingManager;
            this.state = new WeakMap();
            this.submissionStates = new WeakMap();
            this.fetchOptionStates = new WeakMap();
            this.activeStates = new WeakMap();
            this.antiforgeryRefreshes = new WeakMap();
            this.antiforgeryContinuations = new WeakMap();
            this.activeAntiforgeryContinuations = new Set();
            this.antiforgeryDomObserver = null;
            this.preparationFailures = new WeakMap();
            this.canceledPreparations = new WeakMap();
            this.reportedAntiforgeryFailures = new WeakSet();
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
            window.addEventListener('pagehide', () => this.cancelAllPendingAntiforgery());
        }

        handleBeforeFetchRequest(event) {
            const form = event.target instanceof HTMLFormElement ? event.target : null;
            if (!this.isRazorWireTransportForm(form)) return;

            const detail = event.detail;
            if (!detail) return;
            detail.fetchOptions = detail.fetchOptions || {};
            const fetchOptions = detail.fetchOptions as Record<string, unknown>;
            if (this.shouldIncludeCredentials(form)) {
                fetchOptions.credentials = 'include';
            }

            const headers = (fetchOptions.headers || {}) as Record<string, unknown> & {
                set?: (name: string, value: string) => void;
            };
            if (typeof headers.set === 'function') {
                headers.set('X-RazorWire-Form', 'true');
            } else {
                headers['X-RazorWire-Form'] = 'true';
            }

            fetchOptions.headers = headers;

            if (this.isLazyAntiforgeryForm(form) && !this.hasAntiforgeryToken(form)) {
                event.preventDefault?.();
                const resume = typeof detail.resume === 'function'
                    ? () => detail.resume()
                    : () => {};
                const rawSignal = fetchOptions.signal;
                const signal = rawSignal && typeof rawSignal === 'object'
                    && typeof (rawSignal as AbortSignal).addEventListener === 'function'
                    ? rawSignal as AbortSignal
                    : null;
                const continuation: AntiforgeryContinuation = {
                    form,
                    fetchOptions,
                    resume,
                    resumed: false,
                    canceled: false,
                    signal,
                    abortListener: null
                };
                this.antiforgeryContinuations.set(fetchOptions, continuation);
                this.activeAntiforgeryContinuations.add(continuation);
                this.observePendingAntiforgeryDom();
                if (signal) {
                    continuation.abortListener = () => this.cancelPendingAntiforgery(fetchOptions);
                    signal.addEventListener('abort', continuation.abortListener, { once: true });
                    if (signal.aborted) {
                        this.cancelPendingAntiforgery(fetchOptions);
                        return;
                    }
                }

                const preparation = this.ensureAntiforgeryToken(form);
                preparation
                    .then(token => {
                        if (continuation.canceled) return;
                        if (!form.isConnected) {
                            this.cancelPendingAntiforgery(fetchOptions);
                            return;
                        }
                        this.applyAntiforgeryTokenToFetchOptions(fetchOptions, token);
                        this.resumeAntiforgeryContinuation(continuation);
                    })
                    .catch(error => {
                        if (continuation.canceled) return;
                        if (!form.isConnected) {
                            this.cancelPendingAntiforgery(fetchOptions);
                            return;
                        }

                        // Turbo resumes a prevented before-fetch event even when token preparation
                        // failed. Replacing this request's signal before resume makes Turbo finish
                        // its normal submit lifecycle through AbortError without sending the POST.
                        this.loadingManager.settleForFetchOptions(fetchOptions, 'token-failure');
                        this.abortFetchOptions(fetchOptions);
                        this.preparationFailures.set(fetchOptions, true);
                        if (this.reportedAntiforgeryFailures.has(preparation)) {
                            form.setAttribute('data-rw-antiforgery-state', 'failed');
                            form.setAttribute('data-rw-submit-status', 'failed');
                        } else {
                            this.reportedAntiforgeryFailures.add(preparation);
                            this.handleAntiforgeryRefreshFailure(form, error, fetchOptions);
                        }
                        this.resumeAntiforgeryContinuation(continuation);
                    });
            }
        }

        /** Cancels paused requests when their forms leave the document, even without loading feedback. */
        observePendingAntiforgeryDom() {
            if (this.antiforgeryDomObserver || typeof MutationObserver === 'undefined' || !document.documentElement) return;

            this.antiforgeryDomObserver = new MutationObserver(() => {
                for (const continuation of Array.from(this.activeAntiforgeryContinuations)) {
                    if (!continuation.form.isConnected) {
                        this.cancelPendingAntiforgery(continuation.fetchOptions);
                    }
                }
            });
            this.antiforgeryDomObserver.observe(document.documentElement, { childList: true, subtree: true });
        }

        /** Releases the disconnect observer once no anti-forgery request is paused. */
        disconnectPendingAntiforgeryDomObserver() {
            if (this.activeAntiforgeryContinuations.size > 0) return;
            this.antiforgeryDomObserver?.disconnect();
            this.antiforgeryDomObserver = null;
        }

        resumeAntiforgeryContinuation(continuation: AntiforgeryContinuation) {
            if (continuation.resumed || continuation.canceled) return;
            if (continuation.signal && continuation.abortListener) {
                continuation.signal.removeEventListener('abort', continuation.abortListener);
            }
            continuation.resumed = true;
            this.antiforgeryContinuations.delete(continuation.fetchOptions);
            this.activeAntiforgeryContinuations.delete(continuation);
            this.disconnectPendingAntiforgeryDomObserver();
            continuation.resume();
        }

        abortFetchOptions(fetchOptions: Record<string, unknown>) {
            const controller = new AbortController();
            controller.abort();
            fetchOptions.signal = controller.signal;
        }

        cancelPendingAntiforgery(fetchOptions) {
            if (!fetchOptions || typeof fetchOptions !== 'object') return;
            const continuation = this.antiforgeryContinuations.get(fetchOptions);
            if (!continuation || continuation.resumed || continuation.canceled) return;

            continuation.canceled = true;
            if (continuation.signal && continuation.abortListener) {
                continuation.signal.removeEventListener('abort', continuation.abortListener);
            }
            this.antiforgeryContinuations.delete(fetchOptions);
            this.activeAntiforgeryContinuations.delete(continuation);
            this.disconnectPendingAntiforgeryDomObserver();
            this.canceledPreparations.set(fetchOptions, true);
            this.abortFetchOptions(fetchOptions);
            continuation.resumed = true;
            continuation.resume();
        }

        cancelAllPendingAntiforgery() {
            for (const continuation of Array.from(this.activeAntiforgeryContinuations)) {
                this.cancelPendingAntiforgery(continuation.fetchOptions);
            }
        }

        handleAntiforgeryIntent(event) {
            const form = this.getForm(event.target);
            if (!this.isRazorWireTransportForm(form) || !this.isLazyAntiforgeryForm(form) || this.hasAntiforgeryToken(form)) return;

            const preparation = this.ensureAntiforgeryToken(form);
            preparation.catch(error => this.handleAntiforgeryIntentRefreshFailure(form, error, preparation));
        }

        handleAntiforgeryIntentRefreshFailure(form, error, preparation: Promise<AntiforgeryTokenPayload | null>) {
            if (this.reportedAntiforgeryFailures.has(preparation)) return;
            this.reportedAntiforgeryFailures.add(preparation);
            if (!this.isFormFailureEnabled(form)) {
                form.setAttribute('data-rw-antiforgery-state', 'failed');
                return;
            }

            this.handleAntiforgeryRefreshFailure(form, error);
        }

        handleSubmitStart(event) {
            const form = this.getForm(event.target);
            if (!this.isRazorWireForm(form)) return;

            const submission = event.detail?.formSubmission;
            const submitter = submission?.submitter || null;
            const fetchOptions = this.getSubmissionFetchOptions(submission);
            const preparationFailed = !!fetchOptions && this.preparationFailures.has(fetchOptions);
            if (!preparationFailed) this.clearGeneratedFailure(form);
            form.setAttribute('data-rw-submitting', 'true');
            form.setAttribute('data-rw-submit-status', 'submitting');
            if (!preparationFailed) form.removeAttribute('data-rw-last-status');
            form.setAttribute('aria-busy', 'true');

            const formState: FormSubmitState = {
                submitter,
                disabledByRazorWire: false,
                describedById: null,
                submission: submission && typeof submission === 'object' ? submission : null,
                fetchOptions,
                settled: false
            };
            const loadingAllowsOverlap = this.loadingManager.isLoadingEnabled(form)
                && !this.loadingManager.isLockEnabled(form);
            if (submitter && form.getAttribute('data-rw-disable-submit') !== 'false'
                && !loadingAllowsOverlap && !submitter.disabled) {
                submitter.disabled = true;
                submitter.setAttribute('data-rw-submit-disabled-by-razorwire', 'true');
                formState.disabledByRazorWire = true;
            }

            this.state.set(form, formState);
            if (formState.submission) this.submissionStates.set(formState.submission, formState);
            if (fetchOptions) this.fetchOptionStates.set(fetchOptions, formState);
            const active = this.activeStates.get(form) || new Set<FormSubmitState>();
            active.add(formState);
            this.activeStates.set(form, active);
            this.dispatch(form, 'razorwire:form:submit-start', { form, submitter });
        }

        handleSubmitEnd(event) {
            const form = this.getForm(event.target);
            if (!this.isRazorWireForm(form)) return;

            const fetchOptions = this.getSubmissionFetchOptions(event.detail?.formSubmission);
            if (fetchOptions && this.canceledPreparations.has(fetchOptions)) {
                const formState = this.getSubmissionState(form, event.detail?.formSubmission);
                const stillSubmitting = this.finishSubmitting(form, formState);
                if (stillSubmitting) form.setAttribute('data-rw-submit-status', 'submitting');
                else form.removeAttribute('data-rw-submit-status');
                this.canceledPreparations.delete(fetchOptions);
                this.dispatch(form, 'razorwire:form:submit-end', {
                    form,
                    submitter: formState.submitter || event.detail?.formSubmission?.submitter || null,
                    success: false,
                    statusCode: null,
                    handled: false
                });
                return;
            }
            if (fetchOptions && this.preparationFailures.has(fetchOptions)) {
                const formState = this.getSubmissionState(form, event.detail?.formSubmission);
                const submitter = formState.submitter || event.detail?.formSubmission?.submitter || null;
                const stillSubmitting = this.finishSubmitting(form, formState);
                form.setAttribute('data-rw-submit-status', stillSubmitting ? 'submitting' : 'failed');
                this.preparationFailures.delete(fetchOptions);
                this.dispatch(form, 'razorwire:form:submit-end', {
                    form,
                    submitter,
                    success: false,
                    statusCode: null,
                    handled: false
                });
                return;
            }

            const statusCode = this.getStatusCode(event.detail?.fetchResponse);
            const handled = this.isHandled(event.detail?.fetchResponse);
            const responseKind = this.getResponseKind(event.detail?.fetchResponse);
            const success = event.detail?.success === true;
            const formState = this.getSubmissionState(form, event.detail?.formSubmission);
            const submitter = formState.submitter || event.detail?.formSubmission?.submitter || null;
            const previousFailureCount = this.getFailureAttemptCount(form);

            const stillSubmitting = this.finishSubmitting(form, formState);

            if (success) {
                if (previousFailureCount > 0) {
                    this.dispatchProductIntelligenceEvent('razorwire.form.failure_recovered', {
                        recovery_action: 'next_success',
                        attempt_count: previousFailureCount
                    });
                }

                this.clearGeneratedFailure(form);
                if (stillSubmitting) form.setAttribute('data-rw-submit-status', 'submitting');
                else form.removeAttribute('data-rw-submit-status');
                form.removeAttribute('data-rw-last-status');
                form.removeAttribute('data-rw-form-failure-count');
                this.dispatch(form, 'razorwire:form:submit-end', { form, submitter, success, statusCode, handled });
                return;
            }

            form.setAttribute('data-rw-submit-status', stillSubmitting ? 'submitting' : 'failed');
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

            const fetchOptions = event.detail?.request?.fetchOptions;
            if (fetchOptions && this.canceledPreparations.has(fetchOptions)) {
                const formState = this.getFetchOptionState(form, fetchOptions);
                const stillSubmitting = this.finishSubmitting(form, formState);
                if (stillSubmitting) form.setAttribute('data-rw-submit-status', 'submitting');
                else form.removeAttribute('data-rw-submit-status');
                return;
            }
            if (fetchOptions && this.preparationFailures.has(fetchOptions)) {
                const formState = this.getFetchOptionState(form, fetchOptions);
                const stillSubmitting = this.finishSubmitting(form, formState);
                form.setAttribute('data-rw-submit-status', stillSubmitting ? 'submitting' : 'failed');
                return;
            }

            const formState = this.getFetchOptionState(form, fetchOptions);
            const submitter = formState.submitter || null;
            const stillSubmitting = this.finishSubmitting(form, formState);
            form.setAttribute('data-rw-submit-status', stillSubmitting ? 'submitting' : 'failed');

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

        getSubmissionState(form: HTMLFormElement, submission): Partial<FormSubmitState> {
            if (submission && typeof submission === 'object') {
                const state = this.submissionStates.get(submission);
                if (state) return state;
                const fetchOptions = this.getSubmissionFetchOptions(submission);
                if (fetchOptions) return this.getFetchOptionState(form, fetchOptions);
            }
            return this.state.get(form) || {};
        }

        getFetchOptionState(form: HTMLFormElement, fetchOptions): Partial<FormSubmitState> {
            if (fetchOptions && typeof fetchOptions === 'object') {
                return this.fetchOptionStates.get(fetchOptions) || this.state.get(form) || {};
            }
            return this.state.get(form) || {};
        }

        finishSubmitting(form: HTMLFormElement, formState: Partial<FormSubmitState>) {
            const active = this.activeStates.get(form);
            if (!formState.settled) {
                formState.settled = true;
                active?.delete(formState as FormSubmitState);
                if (formState.submitter && formState.disabledByRazorWire) {
                    formState.submitter.disabled = false;
                    formState.submitter.removeAttribute('data-rw-submit-disabled-by-razorwire');
                }
            }

            if (active?.size) {
                form.setAttribute('data-rw-submitting', 'true');
                form.setAttribute('aria-busy', 'true');
                return true;
            }

            this.activeStates.delete(form);
            form.removeAttribute('data-rw-submitting');
            form.removeAttribute('aria-busy');
            return false;
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

        getSubmissionFetchOptions(submission): Record<string, unknown> | null {
            const options = submission?.fetchRequest?.fetchOptions;
            return options && typeof options === 'object' ? options : null;
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

        ensureAntiforgeryToken(form): Promise<AntiforgeryTokenPayload | null> {
            if (this.hasAntiforgeryToken(form)) return Promise.resolve(null);

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

        handleAntiforgeryRefreshFailure(form, error, fetchOptions: Record<string, unknown> | null = null) {
            form.setAttribute('data-rw-antiforgery-state', 'failed');
            const formState = this.getFetchOptionState(form, fetchOptions);
            const submitter = formState.submitter || null;
            const wasSubmitting = form.hasAttribute('data-rw-submitting');
            if (wasSubmitting || fetchOptions) {
                const stillSubmitting = this.finishSubmitting(form, formState);
                form.setAttribute('data-rw-submit-status', stillSubmitting ? 'submitting' : 'failed');
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

            if (wasSubmitting && !fetchOptions) {
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
                failure_mode: detail.handled ? 'handled' : 'unhandled',
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
            productIntelligenceEnabled: dataset.rwProductIntelligenceEnabled === 'true',
            formLoadingEnabled: readBooleanSetting(dataset.rwFormLoadingEnabled, true),
            formLoadingShowFallbackBar: readBooleanSetting(dataset.rwFormLoadingShowFallbackBar, true),
            formLoadingPreventDuplicateSubmissions: readBooleanSetting(dataset.rwFormLoadingPreventDuplicateSubmissions, true)
        };
    }

    function readBooleanSetting(value: string | undefined, defaultValue: boolean) {
        if (value === 'true') return true;
        if (value === 'false') return false;
        return defaultValue;
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

    function ensureBehaviorStub() {
        const existing = window.RazorWire?.behaviors as RazorWireBehaviorStub | undefined;
        if (existing && !existing.__razorWireBehaviorStub) {
            return existing;
        }

        const queue = existing?.__queue ?? [];
        const diagnostics = existing?.__diagnostics ?? [];
        const stub: RazorWireBehaviorStub = {
            __razorWireBehaviorStub: true,
            __queue: queue,
            __diagnostics: diagnostics,
            register(definition) {
                queue.push({ kind: 'register', definition });
            },
            registerLifecycle(definition) {
                queue.push({ kind: 'registerLifecycle', definition });
            },
            scan() {
                if (diagnostics.some(diagnostic =>
                    !!diagnostic
                    && typeof diagnostic === 'object'
                    && (diagnostic as { code?: unknown }).code === 'BehaviorKitNotLoaded')) {
                    return;
                }

                diagnostics.push({
                    code: 'BehaviorKitNotLoaded',
                    message: 'RazorWire Behavior Kit is not loaded.',
                    impact: 'Queued behavior registrations will not connect until behavior-kit.js loads.',
                    fix: 'Render <rw:scripts behavior-kit="true" /> before app behavior bundles.',
                    docs: 'Web/ForgeTrust.RazorWire/Docs/behavior-kit.md#troubleshooting'
                });
            },
            prune() {
            },
            getDiagnostics() {
                return [...diagnostics];
            },
            clearDiagnostics() {
                diagnostics.length = 0;
            }
        };

        window.RazorWire = { ...(window.RazorWire || {}), behaviors: stub };
        return stub;
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
    const formLoadingManager = new FormLoadingManager(runtimeConfig);
    const formFailureManager = new FormFailureManager(runtimeConfig, formLoadingManager);
    formLoadingManager.setFormFailureManager(formFailureManager);

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => {
            connectionManager.start();
            localTimeFormatter.start();
            formLoadingManager.start();
            formFailureManager.start();
        });
    } else {
        connectionManager.start();
        localTimeFormatter.start();
        formLoadingManager.start();
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
        formFailureManager,
        behaviors: ensureBehaviorStub()
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
