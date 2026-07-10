interface Window {
    RazorWireBehaviorKitInitialized?: boolean;
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
}

interface RazorWireBehaviorDefinition {
    name: string;
    selector: string;
    connect(root: Element, context: RazorWireBehaviorContext): void | (() => void);
}

interface RazorWireLifecycleDefinition {
    name: string;
    events?: string[];
    frames?: boolean;
    connect(context: RazorWireLifecycleContext): void | (() => void);
}

interface RazorWireBehaviorContext {
    signal: AbortSignal;
    behaviorName: string;
    rootId: string;
    query(selector: string): Element | null;
    queryAll(selector: string): Element[];
    diagnostic(message: string, fix: string): void;
}

interface RazorWireLifecycleContext {
    signal: AbortSignal;
    behaviorName: string;
    url: string;
    renderKind: string;
    root: Document | Element;
    diagnostic(message: string, fix: string): void;
}

interface RazorWireBehaviorDiagnostic {
    code: string;
    message: string;
    impact: string;
    fix: string;
    docs: string;
    behaviorName?: string;
    rootId?: string;
}

interface RazorWireBehaviorQueueItem {
    kind: 'register' | 'registerLifecycle';
    definition: unknown;
}

interface RazorWireBehaviorStub {
    __razorWireBehaviorStub?: true;
    __queue?: RazorWireBehaviorQueueItem[];
    __diagnostics?: unknown[];
}

interface RazorWireBehaviorsApi {
    register(definition: RazorWireBehaviorDefinition): void;
    registerLifecycle(definition: RazorWireLifecycleDefinition): void;
    scan(root?: Document | Element): void;
    prune(): void;
    getDiagnostics(): RazorWireBehaviorDiagnostic[];
    clearDiagnostics(): void;
}

interface RootBehaviorController {
    definition: RazorWireBehaviorDefinition;
    root: Element;
    rootId: string;
    abortController: AbortController;
    cleanup: (() => void) | null;
}

interface LifecycleController {
    definition: RazorWireLifecycleDefinition;
    abortController: AbortController;
    cleanup: (() => void) | null;
}

interface LifecyclePass {
    id: number;
    event: string;
    renderKind: string;
    root: Document | Element;
    url: string;
}

(function () {
    if (window.RazorWireBehaviorKitInitialized) return;
    window.RazorWireBehaviorKitInitialized = true;

    const lifecycleEventNames = new Set(['initial', 'turbo:load', 'turbo:render']);
    const defaultLifecycleEvents = ['initial', 'turbo:load'];
    const docsHref = 'Web/ForgeTrust.RazorWire/Docs/behavior-kit.md#troubleshooting';

    class BehaviorKitManager implements RazorWireBehaviorsApi {
        private readonly behaviorDefinitions = new Map<string, RazorWireBehaviorDefinition>();
        private readonly lifecycleDefinitions = new Map<string, RazorWireLifecycleDefinition>();
        private readonly rootControllers = new Map<string, RootBehaviorController>();
        private readonly lifecycleControllers = new Map<string, LifecycleController>();
        private readonly diagnostics: RazorWireBehaviorDiagnostic[];
        private readonly rootIds = new WeakMap<Element, string>();
        private connectedLifecyclePasses = new Set<string>();
        private nextRootId = 0;
        private nextPassId = 0;
        private currentPass: LifecyclePass | null = null;
        private isStarted = false;

        constructor(seedDiagnostics: unknown[] = []) {
            this.diagnostics = seedDiagnostics.filter(isBehaviorDiagnostic);
        }

        start() {
            if (this.isStarted) return;
            this.isStarted = true;

            const runInitial = () => {
                this.runLifecyclePass('initial', document, 'initial');
                this.scan(document);
            };

            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', runInitial, { once: true });
            } else {
                runInitial();
            }

            document.addEventListener('turbo:render', event => {
                this.runLifecyclePass('turbo:render', document, 'turbo:render');
                this.scan(document);
            });
            document.addEventListener('turbo:load', event => {
                this.runLifecyclePass('turbo:load', document, 'turbo:load');
                this.scan(document);
            });
            document.addEventListener('turbo:frame-load', event => {
                const target = event.target instanceof Element ? event.target : document;
                this.runLifecyclePass('turbo:frame-load', target, 'turbo:frame-load');
                this.scan(target);
            });
        }

        register(definition: RazorWireBehaviorDefinition) {
            if (!this.isRootDefinition(definition)) return;

            const existing = this.behaviorDefinitions.get(definition.name);
            if (existing) {
                if (existing.selector !== definition.selector) {
                    this.recordDiagnostic(
                        'BehaviorRegistrationConflict',
                        `Behavior "${definition.name}" was registered with a different selector.`,
                        'RazorWire keeps the first behavior definition and ignores the conflicting registration.',
                        'Use a unique behavior name, or keep the selector stable across repeated bundle evaluation.',
                        definition.name);
                    return;
                }

                this.scan(document);
                return;
            }

            if (!this.selectorIsValid(definition.selector, definition.name)) return;

            this.behaviorDefinitions.set(definition.name, definition);
            this.scan(document);
        }

        registerLifecycle(definition: RazorWireLifecycleDefinition) {
            if (!this.isLifecycleDefinition(definition)) return;

            const normalized = this.normalizeLifecycleDefinition(definition);
            if (!normalized) return;

            const existing = this.lifecycleDefinitions.get(normalized.name);
            if (existing) {
                if (this.lifecycleSignature(existing) !== this.lifecycleSignature(normalized)) {
                    this.recordDiagnostic(
                        'BehaviorRegistrationConflict',
                        `Lifecycle behavior "${normalized.name}" was registered with different lifecycle options.`,
                        'RazorWire keeps the first lifecycle definition and ignores the conflicting registration.',
                        'Use a unique lifecycle behavior name, or keep events and frame opt-in stable across repeated bundle evaluation.',
                        normalized.name);
                    return;
                }

                this.runLifecycleDefinitionForCurrentPass(existing);
                return;
            }

            this.lifecycleDefinitions.set(normalized.name, normalized);
            this.runLifecycleDefinitionForCurrentPass(normalized);
        }

        scan(root: Document | Element = document) {
            for (const definition of this.behaviorDefinitions.values()) {
                for (const candidate of this.findMatchingRoots(root, definition)) {
                    this.connectRoot(definition, candidate);
                }
            }

            this.prune();
        }

        prune() {
            for (const [key, controller] of Array.from(this.rootControllers)) {
                if (!controller.root.isConnected || !controller.root.matches(controller.definition.selector)) {
                    this.disconnectRoot(key, controller);
                }
            }
        }

        getDiagnostics() {
            return [...this.diagnostics];
        }

        clearDiagnostics() {
            this.diagnostics.length = 0;
        }

        private connectRoot(definition: RazorWireBehaviorDefinition, root: Element) {
            const rootId = this.rootId(root);
            const key = `${definition.name}::${rootId}`;
            if (this.rootControllers.has(key)) return;

            const abortController = this.createAbortController(definition.name, rootId);
            if (!abortController) return;

            try {
                const context = this.createRootContext(definition, root, rootId, abortController.signal);
                const cleanup = definition.connect(root, context);
                this.rootControllers.set(key, {
                    definition,
                    root,
                    rootId,
                    abortController,
                    cleanup: typeof cleanup === 'function' ? cleanup : null
                });
            } catch (error) {
                abortController.abort();
                this.recordDiagnostic(
                    'BehaviorConnectFailed',
                    `Behavior "${definition.name}" failed to connect.`,
                    'RazorWire discarded the failed controller so a later scan can retry safely.',
                    this.errorFix(error),
                    definition.name,
                    rootId);
            }
        }

        private disconnectRoot(key: string, controller: RootBehaviorController) {
            controller.abortController.abort();
            try {
                controller.cleanup?.();
            } catch (error) {
                this.recordDiagnostic(
                    'BehaviorCleanupFailed',
                    `Behavior "${controller.definition.name}" cleanup failed.`,
                    'RazorWire removed the controller and continued pruning other roots.',
                    this.errorFix(error),
                    controller.definition.name,
                    controller.rootId);
            }

            this.rootControllers.delete(key);
        }

        private runLifecyclePass(event: string, root: Document | Element, renderKind: string) {
            const pass: LifecyclePass = {
                id: ++this.nextPassId,
                event,
                renderKind,
                root,
                url: window.location?.href ?? ''
            };
            this.currentPass = pass;
            this.connectedLifecyclePasses = new Set();

            for (const definition of this.lifecycleDefinitions.values()) {
                this.runLifecycleDefinition(definition, pass);
            }
        }

        private runLifecycleDefinitionForCurrentPass(definition: RazorWireLifecycleDefinition) {
            if (!this.currentPass) return;
            this.runLifecycleDefinition(definition, this.currentPass);
        }

        private runLifecycleDefinition(definition: RazorWireLifecycleDefinition, pass: LifecyclePass) {
            if (!this.lifecycleApplies(definition, pass.event)) return;

            const passKey = `${pass.id}::${definition.name}`;
            if (this.connectedLifecyclePasses.has(passKey)) return;
            this.connectedLifecyclePasses.add(passKey);

            const previous = this.lifecycleControllers.get(definition.name);
            if (previous) {
                this.disconnectLifecycle(previous);
                this.lifecycleControllers.delete(definition.name);
            }

            const abortController = this.createAbortController(definition.name);
            if (!abortController) return;

            try {
                const cleanup = definition.connect({
                    signal: abortController.signal,
                    behaviorName: definition.name,
                    url: pass.url,
                    renderKind: pass.renderKind,
                    root: pass.root,
                    diagnostic: (message, fix) => this.recordDiagnostic(
                        'BehaviorDiagnostic',
                        message,
                        'The lifecycle behavior reported an app-owned diagnostic.',
                        fix,
                        definition.name)
                });
                this.lifecycleControllers.set(definition.name, {
                    definition,
                    abortController,
                    cleanup: typeof cleanup === 'function' ? cleanup : null
                });
            } catch (error) {
                abortController.abort();
                this.recordDiagnostic(
                    'BehaviorConnectFailed',
                    `Lifecycle behavior "${definition.name}" failed to connect.`,
                    'RazorWire discarded the failed lifecycle controller so a later lifecycle pass can retry safely.',
                    this.errorFix(error),
                    definition.name);
            }
        }

        private disconnectLifecycle(controller: LifecycleController) {
            controller.abortController.abort();
            try {
                controller.cleanup?.();
            } catch (error) {
                this.recordDiagnostic(
                    'BehaviorCleanupFailed',
                    `Lifecycle behavior "${controller.definition.name}" cleanup failed.`,
                    'RazorWire removed the lifecycle controller and continued the lifecycle pass.',
                    this.errorFix(error),
                    controller.definition.name);
            }
        }

        private lifecycleApplies(definition: RazorWireLifecycleDefinition, event: string) {
            if (event === 'turbo:frame-load') return definition.frames === true;
            const events = definition.events ?? defaultLifecycleEvents;
            return events.includes(event);
        }

        private normalizeLifecycleDefinition(definition: RazorWireLifecycleDefinition) {
            const events = definition.events ?? defaultLifecycleEvents;
            for (const event of events) {
                if (!lifecycleEventNames.has(event)) {
                    this.recordDiagnostic(
                        'BehaviorLifecycleEventInvalid',
                        `Lifecycle behavior "${definition.name}" uses unsupported event "${event}".`,
                        'RazorWire ignored the lifecycle registration because it cannot provide the requested lifecycle guarantee.',
                        'Use "initial", "turbo:load", or "turbo:render"; set frames: true for turbo:frame-load.',
                        definition.name);
                    return null;
                }
            }

            return {
                ...definition,
                events: [...new Set(events)],
                frames: definition.frames === true
            };
        }

        private lifecycleSignature(definition: RazorWireLifecycleDefinition) {
            const events = definition.events ?? defaultLifecycleEvents;
            return `${[...events].sort().join('|')}::frames=${definition.frames === true}`;
        }

        private findMatchingRoots(root: Document | Element, definition: RazorWireBehaviorDefinition) {
            const roots: Element[] = [];
            if (root instanceof Element && root.matches(definition.selector)) {
                roots.push(root);
            }

            roots.push(...Array.from(root.querySelectorAll(definition.selector)));
            return roots;
        }

        private selectorIsValid(selector: string, behaviorName: string) {
            try {
                document.querySelector(selector);
                return true;
            } catch {
                this.recordDiagnostic(
                    'BehaviorSelectorInvalid',
                    `Behavior "${behaviorName}" uses invalid selector "${selector}".`,
                    'RazorWire ignored the behavior registration because it cannot scan an invalid selector.',
                    'Use a valid CSS selector for root-scoped behaviors.',
                    behaviorName);
                return false;
            }
        }

        private createRootContext(
            definition: RazorWireBehaviorDefinition,
            root: Element,
            rootId: string,
            signal: AbortSignal): RazorWireBehaviorContext {
            return {
                signal,
                behaviorName: definition.name,
                rootId,
                query: selector => root.querySelector(selector),
                queryAll: selector => Array.from(root.querySelectorAll(selector)),
                diagnostic: (message, fix) => this.recordDiagnostic(
                    'BehaviorDiagnostic',
                    message,
                    'The behavior reported an app-owned diagnostic.',
                    fix,
                    definition.name,
                    rootId)
            };
        }

        private createAbortController(behaviorName: string, rootId?: string) {
            if (typeof AbortController === 'undefined') {
                this.recordDiagnostic(
                    'BehaviorAbortUnsupported',
                    `Behavior "${behaviorName}" cannot connect because AbortController is unavailable.`,
                    'RazorWire leaves behavior roots unconnected instead of attaching unmanaged listeners.',
                    'Run Behavior Kit in browsers that support AbortController and AbortSignal.',
                    behaviorName,
                    rootId);
                return null;
            }

            return new AbortController();
        }

        private rootId(root: Element) {
            const existing = this.rootIds.get(root);
            if (existing) return existing;

            this.nextRootId += 1;
            const id = `rw-behavior-root-${this.nextRootId}`;
            this.rootIds.set(root, id);
            return id;
        }

        private isRootDefinition(definition: RazorWireBehaviorDefinition) {
            return !!definition
                && typeof definition.name === 'string'
                && definition.name.trim().length > 0
                && typeof definition.selector === 'string'
                && definition.selector.trim().length > 0
                && typeof definition.connect === 'function';
        }

        private isLifecycleDefinition(definition: RazorWireLifecycleDefinition) {
            return !!definition
                && typeof definition.name === 'string'
                && definition.name.trim().length > 0
                && typeof definition.connect === 'function';
        }

        private recordDiagnostic(
            code: string,
            message: string,
            impact: string,
            fix: string,
            behaviorName?: string,
            rootId?: string) {
            const diagnostic = { code, message, impact, fix, docs: docsHref, behaviorName, rootId };
            if (this.diagnostics.some(existing =>
                existing.code === code
                && existing.message === message
                && existing.behaviorName === behaviorName
                && existing.rootId === rootId
                && existing.fix === fix)) {
                return;
            }

            this.diagnostics.push(diagnostic);
            if (window.RazorWire?.config?.developmentDiagnostics === true && typeof console?.warn === 'function') {
                console.warn(`RazorWire Behavior Kit ${code}: ${message} Impact: ${impact} Fix: ${fix} Docs: ${docsHref}`);
            }
        }

        private errorFix(error: unknown) {
            return error instanceof Error && error.message
                ? `Fix the app behavior callback. Error: ${error.message}`
                : 'Fix the app behavior callback and try again.';
        }
    }

    const existing = window.RazorWire?.behaviors as RazorWireBehaviorStub | undefined;
    const manager = new BehaviorKitManager(existing?.__diagnostics ?? []);
    window.RazorWire = { ...(window.RazorWire || {}), behaviors: manager };

    for (const item of existing?.__queue ?? []) {
        if (item.kind === 'register') {
            manager.register(item.definition as RazorWireBehaviorDefinition);
        } else {
            manager.registerLifecycle(item.definition as RazorWireLifecycleDefinition);
        }
    }

    manager.start();

    function isBehaviorDiagnostic(value: unknown): value is RazorWireBehaviorDiagnostic {
        return !!value
            && typeof value === 'object'
            && typeof (value as RazorWireBehaviorDiagnostic).code === 'string'
            && typeof (value as RazorWireBehaviorDiagnostic).message === 'string';
    }
})();
