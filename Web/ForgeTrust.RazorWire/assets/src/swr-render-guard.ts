/**
 * Internal Turbo 8.0.23 adapter: retain exact request ownership across frame body
 * parsing and queued rendering. This is UI ordering, never server data authority.
 */
type Frame = Element & { delegate?: Delegate };
type RequestRecord = { frame: Frame; epoch: object; order: number; signal?: AbortSignal };
type Renderer = {
    preservingPermanentElements: (callback: () => void) => Promise<void>;
    scrollFrameIntoView: () => void;
    focusFirstAutofocusableElement: () => void;
    activateScriptElements: () => void;
};
type Delegate = {
    loadResponse: (...args: any[]) => Promise<unknown>;
    allowsImmediateRender: (...args: any[]) => boolean;
    view: { renderer?: Renderer };
    requestSucceededWithResponse: (request: { fetchOptions: object }, response: { response: object }) => unknown;
    requestFailedWithResponse: (request: { fetchOptions: object }, response: { response: object }) => unknown;
};
type State = {
    epoch: object;
    busy: number;
    order: number;
    last: number; // Last committed request order.
    active?: RequestRecord;
    tail: Promise<unknown>;
};
/** Installs the pinned adapter and returns a scope around an actual queued stream action. */
export function installSwrRenderGuard(): (stream: Element) => (() => void) | undefined {
    const selector = 'turbo-frame[data-rw-swr]';
    const states = new WeakMap<Frame, State>();
    const requests = new WeakMap<object, RequestRecord>();
    const responses = new WeakMap<object, RequestRecord>();
    const eligible = (record: RequestRecord, state: State) => record.frame.isConnected
        && !record.signal?.aborted && state.busy === 0 && record.epoch === state.epoch && record.order >= state.last;
    const receivers = ['requestSucceededWithResponse', 'requestFailedWithResponse'] as const;
    document.addEventListener('turbo:before-fetch-request', event => {
        const frame = event.target as Frame;
        const options = (event as CustomEvent).detail?.fetchOptions;
        if (!(frame instanceof Element) || !frame.matches(selector + '[src]')
            || options?.method?.toUpperCase() !== 'GET') return;
        const delegate = frame.delegate;
        if (!delegate || ![...receivers, 'loadResponse', 'allowsImmediateRender']
            .every(name => typeof delegate[name as keyof Delegate] === 'function')) return;
        let state = states.get(frame);
        if (!state) {
            state = { epoch: {}, busy: 0, order: 0, last: 0, tail: Promise.resolve() };
            states.set(frame, state);
            const owner = state;
            for (const name of receivers) {
                const receive = delegate[name];
                delegate[name] = function (request, response) {
                    const record = requests.get(request.fetchOptions);
                    if (record) responses.set(response.response, record);
                    return receive.call(this, request, response);
                };
            }
            const load = delegate.loadResponse;
            delegate.loadResponse = function (...args) {
                const record = responses.get(args[0]?.response);
                const run = async () => {
                    if (record && !eligible(record, owner)) return;
                    owner.active = record?.frame === frame ? record : undefined;
                    try { return await load.apply(this, args); }
                    finally { owner.active = undefined; }
                };
                const result = owner.tail.then(run);
                owner.tail = result.catch(() => {});
                return result;
            };
            const allow = delegate.allowsImmediateRender;
            delegate.allowsImmediateRender = function (...args) {
                // Turbo selects event.detail.render synchronously inside this call.
                // Wrap that final selection, after host listeners, before its repaint.
                const allowed = allow.apply(this, args);
                const record = owner.active;
                const renderer = this.view.renderer;
                if (record && renderer) {
                    const preserve = renderer.preservingPermanentElements;
                    renderer.preservingPermanentElements = function (callback) {
                        if (!eligible(record, owner)) {
                            // A discarded snapshot must not move permanent nodes,
                            // refocus old controls, scroll, or execute old scripts.
                            this.scrollFrameIntoView = this.focusFirstAutofocusableElement = this.activateScriptElements = () => {};
                            return Promise.resolve();
                        }
                        return preserve.call(this, () => {
                            callback();
                            owner.last = record.order;
                        });
                    };
                }
                return allowed;
            };
        }
        requests.set(options, { frame, epoch: state.epoch, order: ++state.order, signal: options.signal });
    });

    return stream => {
        // Resolve targets when the existing stream queue actually executes, not
        // at message arrival. Turbo owns target/targets selector semantics.
        const element = stream as Element & { target?: string; targets?: string; targetElements?: Element[] };
        const targets = element.target || element.targets ? element.targetElements || [] : [];
        const affected = new Set<State>();
        for (const target of targets.flatMap(target => [target, ...target.querySelectorAll(selector)])) {
            for (let frame = target.closest(selector); frame;
                frame = frame.parentElement?.closest(selector) || null) {
                const state = states.get(frame as Frame);
                if (state) affected.add(state);
            }
        }
        for (const state of affected) { state.epoch = {}; state.busy++; }
        return () => {
            for (const state of affected) { state.epoch = {}; state.busy--; }
        };
    };
}
