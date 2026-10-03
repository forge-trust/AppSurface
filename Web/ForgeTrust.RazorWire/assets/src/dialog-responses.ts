/**
 * Request-owned dialog presentation. Correlation controls UI only; it is never
 * an authorization or write-concurrency mechanism. See Docs/dialog-responses.md.
 */
interface DialogRequest {
    order: number;
    originFlow: string | null;
    trigger: HTMLElement | null;
    used: boolean;
    newFlow: string | null;
    failedValidation: boolean;
    dialogEligible: boolean;
}

interface DialogLink {
    trigger: HTMLElement;
    flow: string | null;
}

interface DialogTurbo {
    StreamActions?: Record<string, (this: Element) => void | Promise<void>>;
}

/** Owns one native shell and a bounded request registry for the current page. */
export class DialogResponseManager {
    private requests = new Map<string, DialogRequest>();
    private submitters = new WeakMap<HTMLFormElement, HTMLElement>();
    private formRequests = new WeakMap<HTMLFormElement, DialogRequest>();
    private linkForms = new WeakMap<HTMLFormElement, DialogLink>();
    private nextOrder = 0;
    private openerOrder = 0;
    private latestInsideOrder = 0;
    private flow: string | null = null;
    private shell: HTMLDialogElement | null = null;
    private body: HTMLElement | null = null;
    private title: HTMLElement | null = null;
    private returnFocus: HTMLElement | null = null;
    private renderQueue: Promise<void> = Promise.resolve();
    private generation = 0;

    constructor(private turbo: DialogTurbo | null) { }

    /** Installs Turbo hooks once, alongside the core form-failure manager. */
    start() {
        if (!this.turbo?.StreamActions) return;
        const manager = this;
        this.turbo.StreamActions['rw-dialog'] = function () { manager.command(this); };
        document.addEventListener('click', e => this.captureLink(e), true);
        document.addEventListener('submit', e => {
            if (e.target instanceof HTMLFormElement) {
                if (e.submitter instanceof HTMLElement) this.submitters.set(e.target, e.submitter);
                else this.submitters.delete(e.target);
            }
        }, true);
        document.addEventListener('turbo:before-fetch-request', e => this.beforeRequest(e as CustomEvent));
        document.addEventListener('turbo:before-fetch-response', e => this.beforeResponse(e as CustomEvent));
        document.addEventListener('turbo:before-stream-render', e => this.beforeRender(e as CustomEvent));
        document.addEventListener('turbo:before-cache', () => this.reset());
        document.addEventListener('turbo:before-render', () => this.reset());
    }

    private captureLink(event: MouseEvent) {
        if (event.button !== 0 || event.metaKey || event.ctrlKey || event.altKey || event.shiftKey) return;
        const link = event.target instanceof Element
            ? event.target.closest<HTMLAnchorElement>('a[data-turbo-stream],a[data-turbo-method]') : null;
        if (!link || link.closest('[data-turbo="false"]') || link.hasAttribute('download')) return;
        const provenance = { trigger: link, flow: this.shell?.contains(link) ? this.flow : null };
        // Turbo appends its temporary form synchronously during this click,
        // then submits on the next animation frame. Bind that exact form before
        // submission/confirmation; canceled or reordered confirmations cannot
        // leave a URL queue entry for a later request to consume.
        const bindRecords = (records: MutationRecord[]) => {
            for (const record of records) {
                for (const node of record.addedNodes) {
                    if (node instanceof HTMLFormElement && node.hidden
                        && node.getAttribute('data-turbo') === 'true'
                        && (node.hasAttribute('data-turbo-stream') || link.hasAttribute('data-turbo-method'))) {
                        this.linkForms.set(node, provenance);
                    }
                }
            }
        };
        const observer = new MutationObserver(bindRecords);
        observer.observe(document.body, { childList: true });
        const bindForm = () => {
            bindRecords(observer.takeRecords());
            observer.disconnect();
            document.removeEventListener('click', bindForm);
        };
        document.addEventListener('click', bindForm, { once: true });
        setTimeout(bindForm, 0);
    }

    private beforeRequest(event: CustomEvent) {
        const options = event.detail?.fetchOptions;
        if (!options || !event.detail?.url) return;
        const url = new URL(String(event.detail.url), location.href);
        if (url.origin !== location.origin) return;
        const headers = new Headers(options.headers);
        if (!headers.get('Accept')?.includes('text/vnd.turbo-stream.html')) return;
        const target = event.target;
        let trigger: HTMLElement | null = target instanceof HTMLFormElement
            ? this.submitters.get(target) ?? (document.activeElement instanceof HTMLElement && target.contains(document.activeElement)
                ? document.activeElement : target)
            : target instanceof HTMLElement ? target : null;
        let originFlow = target instanceof Node && this.shell?.contains(target) ? this.flow : null;
        const link = target instanceof HTMLFormElement ? this.linkForms.get(target) : null;
        if (link) {
            trigger = link.trigger;
            originFlow = link.flow;
        }
        const token = crypto.randomUUID();
        const order = ++this.nextOrder;
        const dialogEligible = !(target instanceof HTMLFormElement && target.hidden) || !!link;
        const request = { order, originFlow, trigger, used: false, newFlow: null, failedValidation: false, dialogEligible };
        this.requests.set(token, request);
        if (target instanceof HTMLFormElement) this.formRequests.set(target, request);
        if (originFlow && originFlow === this.flow) this.latestInsideOrder = order;
        // Old/unknown requests fail closed for dialog targets. Retain no unbounded
        // tombstones: matching the one live UUID is sufficient for old flows.
        if (this.requests.size > 512) this.requests.delete(this.requests.keys().next().value!);
        headers.set('X-RazorWire-Request', token);
        headers.set('X-RazorWire-Order', String(order));
        if (originFlow) headers.set('X-RazorWire-Flow', originFlow);
        else headers.delete('X-RazorWire-Flow');
        options.headers = Object.fromEntries(headers.entries());
    }

    private beforeResponse(event: CustomEvent) {
        const response = event.detail?.fetchResponse;
        if (response?.statusCode !== 422) return;
        // The server echoes the request token as a response header for handled
        // validation. Focus is deferred until the scoped stream actually renders.
        const token = response.response?.headers?.get('X-RazorWire-Request');
        const request = token ? this.requests.get(token) : null;
        if (request) request.failedValidation = true;
    }

    private beforeRender(event: CustomEvent) {
        const stream = event.detail?.newStream as Element | undefined;
        const render = event.detail?.render;
        if (!stream || typeof render !== 'function') return;
        // Reserve in connection order, before Turbo waits for next repaint.
        // A single queue also prevents interleaving dependent response actions.
        const before = this.renderQueue;
        let release!: () => void;
        this.renderQueue = new Promise<void>(resolve => { release = resolve; });
        const generation = this.generation;
        // Turbo awaits repaint after synchronous event dispatch. Select the
        // final renderer once all extensions have run so a later replacement
        // cannot bypass our queue and leave its reservation stranded.
        queueMicrotask(() => {
            if (event.defaultPrevented) { void before.then(release); return; }
            const selectedRender = event.detail.render;
            event.detail.render = async (element: Element) => {
                await before;
                try {
                    if (generation !== this.generation || !this.allowTarget(element)) return;
                    await selectedRender(element);
                    this.focusValidation(element);
                } finally { release(); }
            };
        });
    }

    private requestFor(stream: Element) {
        const token = stream.getAttribute('data-rw-request');
        const request = token ? this.requests.get(token) : null;
        if (!request || String(request.order) !== stream.getAttribute('data-rw-order')
            || request.originFlow !== stream.getAttribute('data-rw-flow')) return null;
        return request;
    }

    private eligible(request: DialogRequest) {
        return request.dialogEligible && (request.originFlow
            ? this.flow === request.originFlow && request.order === this.latestInsideOrder
            : request.order > this.openerOrder);
    }

    /** Internal seam used by form-failure UI to avoid writing into a newer flow. */
    isStaleForm(form: HTMLFormElement, detail?: any) {
        const headers = detail?.formSubmission?.fetchRequest?.headers ?? detail?.request?.headers;
        const token = headers?.get?.('X-RazorWire-Request') ?? headers?.['X-RazorWire-Request']
            ?? headers?.['x-razorwire-request'];
        const request = token ? this.requests.get(token) : this.formRequests.get(form);
        if (token && !request && this.formRequests.get(form)?.originFlow) return true;
        return !!request?.originFlow && !this.eligible(request);
    }

    private allowTarget(stream: Element) {
        if (stream.getAttribute('action') === 'rw-dialog') return true;
        // Opaque/manual actions have no package correlation contract. Leave
        // them to their author, as documented for the raw-stream escape hatch.
        if (!stream.hasAttribute('data-rw-request')) return true;
        const id = stream.getAttribute('target');
        const selector = !id ? stream.getAttribute('targets') : null;
        const target = id ? document.getElementById(id) : null;
        const targets = selector ? Array.from(document.querySelectorAll(selector)) : target ? [target] : [];
        if (!targets.some(element => this.shell?.contains(element))) return true;
        const request = this.requestFor(stream);
        const phase = stream.getAttribute('data-rw-dialog-phase');
        const allowed = request && (phase === 'new'
            ? !!request.newFlow && request.newFlow === this.flow && request.order === this.latestInsideOrder
            : phase === 'origin' && !!request.originFlow && this.eligible(request));
        if (allowed) return true;
        if (!selector || !targets.some(element => !this.shell?.contains(element))) return false;
        // A package selector action may match both page and dialog diagnostics.
        // Keep its ordinary page effects while excluding the stale shell targets.
        stream.setAttribute('targets', `:is(${selector}):not([data-rw-dialog],[data-rw-dialog] *)`);
        return true;
    }

    private command(stream: Element) {
        const request = this.requestFor(stream);
        if (!request || request.used) return;
        request.used = true;
        if (!this.eligible(request)) return;
        const command = stream.getAttribute('dialog-command');
        if (command === 'close') {
            if (this.flow && this.shell?.open) this.dismiss();
            return;
        }
        const title = stream.getAttribute('dialog-title');
        const template = stream.querySelector('template');
        if (command !== 'open' || !title?.trim() || !(template instanceof HTMLTemplateElement)) return;
        this.ensureShell();
        this.flow = crypto.randomUUID();
        request.newFlow = this.flow;
        this.openerOrder = request.order;
        this.latestInsideOrder = request.order;
        // An in-dialog trigger is about to be disconnected; keep its page opener.
        if (request.trigger && !this.shell!.contains(request.trigger)) this.returnFocus = request.trigger;
        this.title!.textContent = title;
        this.body!.replaceChildren(template.content.cloneNode(true));
        this.body!.setAttribute('data-rw-dialog-flow', this.flow);
        if (!this.shell!.open) this.shell!.showModal();
        this.focusBody(false);
    }

    private ensureShell() {
        if (this.shell?.isConnected) return;
        const shell = document.createElement('dialog');
        shell.setAttribute('data-rw-dialog', 'true');
        shell.setAttribute('data-rw-ui', 'dialog');
        const titleId = `rw-dialog-title-${crypto.randomUUID()}`;
        shell.setAttribute('aria-labelledby', titleId);
        const header = document.createElement('header');
        header.setAttribute('data-rw-dialog-header', 'true');
        const title = document.createElement('h2');
        title.id = titleId;
        title.tabIndex = -1;
        title.setAttribute('data-rw-dialog-title', 'true');
        const close = document.createElement('button');
        close.type = 'button';
        close.textContent = 'Close';
        close.setAttribute('data-rw-dialog-close', 'true');
        close.addEventListener('click', () => this.dismiss());
        header.append(title, close);
        const body = document.createElement('div');
        body.tabIndex = -1;
        body.setAttribute('data-rw-dialog-body', 'true');
        shell.append(header, body);
        shell.addEventListener('cancel', event => { event.preventDefault(); this.dismiss(); });
        shell.addEventListener('close', () => {
            // The previous close event can be queued when a newer open arrives.
            if (this.shell === shell && !shell.open && this.flow) this.dismiss();
        });
        document.body.append(shell);
        this.shell = shell;
        this.body = body;
        this.title = title;
    }

    private focusValidation(stream: Element) {
        const request = this.requestFor(stream);
        if (!request || !this.shell?.open || !this.allowTarget(stream)) return;
        const targetId = stream.getAttribute('target');
        const target = targetId ? document.getElementById(targetId) : null;
        if (stream.getAttribute('action') !== 'rw-dialog' && (!target || !this.body?.contains(target))) return;
        if (request.failedValidation || stream.querySelector('template')?.content.querySelector('[data-rw-form-error-kind="validation"]')) {
            const phase = stream.getAttribute('data-rw-dialog-phase');
            if ((phase === 'origin' && this.eligible(request)) || (phase === 'new' && request.newFlow === this.flow)) {
                this.focusBody(true);
            }
        }
    }

    private focusBody(validation: boolean) {
        if (!this.body) return;
        const fieldName = this.body.querySelector('[data-rw-form-error-field]')?.getAttribute('data-rw-form-error-field');
        const namedField = fieldName ? Array.from(this.body.querySelectorAll<HTMLElement>('input,select,textarea'))
            .find(field => field.getAttribute('name') === fieldName && this.visible(field)) : null;
        const invalid = this.body.querySelector<HTMLElement>('[aria-invalid="true"],.input-validation-error');
        const summary = this.body.querySelector<HTMLElement>('[data-rw-form-error-kind="validation"],.validation-summary-errors');
        const first = Array.from(this.body.querySelectorAll<HTMLElement>('input:not([type="hidden"]),select,textarea,button,a[href]'))
            .find(field => this.visible(field));
        const focus = validation ? namedField ?? (invalid && this.visible(invalid) ? invalid : summary) ?? this.title : first ?? this.title;
        if (focus) {
            if (!focus.hasAttribute('tabindex') && focus === summary) focus.tabIndex = -1;
            focus.focus({ preventScroll: true });
            if (validation) focus.scrollIntoView({ block: 'nearest' });
        }
    }

    private visible(element: HTMLElement) {
        return !element.hasAttribute('disabled') && !element.closest('[hidden],[inert]') && element.getClientRects().length > 0;
    }

    private dismiss(restoreFocus = true) {
        this.flow = null;
        if (this.shell?.open) this.shell.close();
        this.body?.replaceChildren();
        this.body?.removeAttribute('data-rw-dialog-flow');
        if (!restoreFocus) return;
        const target = this.returnFocus?.isConnected ? this.returnFocus : document.querySelector<HTMLElement>('main,[role="main"]') ?? document.body;
        if (!target.hasAttribute('tabindex') && target !== this.returnFocus) target.tabIndex = -1;
        target.focus({ preventScroll: true });
    }

    private reset() {
        this.generation++;
        this.dismiss(false);
        this.shell?.remove();
        this.shell = null;
        this.body = null;
        this.title = null;
        this.returnFocus = null;
        this.requests.clear();
        this.linkForms = new WeakMap();
        this.openerOrder = 0;
        this.latestInsideOrder = 0;
    }
}
