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

interface RazorWireBehaviorContext {
    signal: AbortSignal;
    query(selector: string): Element | null;
    queryAll(selector: string): Element[];
    behaviorName: string;
    rootId: string;
    diagnostic(message: string, fix: string, impact?: string, docs?: string): void;
}

interface RazorWireBehaviorDiagnostic {
    code: string;
    message: string;
    impact: string;
    fix: string;
    docs: string;
    behaviorName?: string;
    selector?: string;
    rootId?: string;
}

interface RazorWireBehaviorController {
    root: Element;
    rootId: string;
    abortController: AbortController;
    cleanup: (() => void) | null;
}

interface RazorWireBehaviorManager {
    __isRazorWireBehaviorManager: true;
    register(definition: RazorWireBehaviorDefinition): void;
    scan(root?: Document | Element): void;
    prune(): void;
    getDiagnostics(): RazorWireBehaviorDiagnostic[];
    clearDiagnostics(): void;
}

(function () {
    const docsPath = 'Web/ForgeTrust.RazorWire/Docs/behavior-kit.md#troubleshooting';

    if (window.RazorWireBehaviorKitInitialized) {
        const existing = window.RazorWire?.behaviors as RazorWireBehaviorManager | RazorWireBehaviorRegistrationStub | undefined;
        existing?.scan?.();
        return;
    }

    window.RazorWireBehaviorKitInitialized = true;

    class BehaviorKitManager implements RazorWireBehaviorManager {
        readonly __isRazorWireBehaviorManager = true;
        private readonly definitions = new Map<string, RazorWireBehaviorDefinition>();
        private readonly controllers = new Map<string, Map<Element, RazorWireBehaviorController>>();
        private readonly diagnostics: RazorWireBehaviorDiagnostic[] = [];
        private isStarted = false;
        private nextRootId = 0;

        constructor(queuedDefinitions: unknown[]) {
            for (const definition of queuedDefinitions) {
                this.register(definition as RazorWireBehaviorDefinition);
            }
        }

        start() {
            if (this.isStarted) return;
            this.isStarted = true;
            this.scan();
            document.addEventListener('turbo:render', () => this.scan());
            document.addEventListener('turbo:load', () => this.scan());
            document.addEventListener('turbo:frame-load', event => {
                const frame = event.target instanceof Element ? event.target : document;
                this.scan(frame);
            });
        }

        register(definition: unknown) {
            if (!this.isDefinitionShapeValid(definition)) {
                const invalidContext = this.getInvalidDefinitionContext(definition);
                this.recordDiagnostic({
                    code: 'BehaviorRegistrationInvalid',
                    message: 'RazorWire behavior definitions require a non-empty name, a non-empty selector, and a connect callback.',
                    impact: 'RazorWire skipped the invalid behavior definition so it cannot attach unmanaged page behavior.',
                    fix: 'Pass { name, selector, connect } to window.RazorWire.behaviors.register(...).',
                    docs: docsPath,
                    behaviorName: invalidContext.behaviorName,
                    selector: invalidContext.selector
                });
                return;
            }

            if (!this.isSelectorValid(definition.selector, definition.name)) {
                return;
            }

            const existing = this.definitions.get(definition.name);
            if (existing) {
                if (existing.selector === definition.selector) {
                    this.scan();
                    return;
                }

                this.recordDiagnostic({
                    code: 'BehaviorRegistrationConflict',
                    message: `RazorWire behavior "${definition.name}" was registered with a conflicting selector.`,
                    impact: 'RazorWire kept the first behavior definition so already-connected roots keep deterministic lifecycle ownership.',
                    fix: 'Use a unique behavior name for the new selector, or keep the selector identical across repeated bundle evaluation.',
                    docs: docsPath,
                    behaviorName: definition.name,
                    selector: definition.selector
                });
                return;
            }

            this.definitions.set(definition.name, definition);
            this.controllers.set(definition.name, new Map<Element, RazorWireBehaviorController>());
            this.scan();
        }

        scan(root: Document | Element = document) {
            if (!this.isAbortSupported()) {
                this.recordDiagnostic({
                    code: 'BehaviorAbortUnsupported',
                    message: 'AbortController and AbortSignal are unavailable in this browser.',
                    impact: 'RazorWire left behavior roots unconnected because it cannot guarantee lifecycle cleanup for app-authored listeners.',
                    fix: 'Run the behavior kit in browsers with AbortController support, or provide a browser-compatible polyfill before loading behavior-kit.js.',
                    docs: docsPath
                });
                return;
            }

            for (const definition of this.definitions.values()) {
                for (const element of this.findMatchingRoots(root, definition)) {
                    const controllers = this.controllers.get(definition.name);
                    if (!controllers || controllers.has(element) || !element.isConnected) {
                        continue;
                    }

                    this.connect(element, definition, controllers);
                }
            }

            this.prune();
        }

        prune() {
            for (const definition of this.definitions.values()) {
                const controllers = this.controllers.get(definition.name);
                if (!controllers) continue;

                for (const [root, controller] of Array.from(controllers.entries())) {
                    if (!root.isConnected || !this.matches(root, definition.selector, definition)) {
                        this.disconnect(definition, controller);
                        controllers.delete(root);
                    }
                }
            }
        }

        getDiagnostics() {
            return [...this.diagnostics];
        }

        clearDiagnostics() {
            this.diagnostics.length = 0;
        }

        private connect(
            root: Element,
            definition: RazorWireBehaviorDefinition,
            controllers: Map<Element, RazorWireBehaviorController>) {
            const abortController = new AbortController();
            const rootId = this.createRootId(definition);
            const context: RazorWireBehaviorContext = {
                signal: abortController.signal,
                query: selector => root.querySelector(selector),
                queryAll: selector => Array.from(root.querySelectorAll(selector)),
                behaviorName: definition.name,
                rootId,
                diagnostic: (message, fix, impact, docs) => {
                    this.recordDiagnostic({
                        code: 'BehaviorDiagnostic',
                        message,
                        impact: impact || 'A RazorWire app-authored behavior reported a development diagnostic.',
                        fix,
                        docs: docs || docsPath,
                        behaviorName: definition.name,
                        selector: definition.selector,
                        rootId
                    });
                }
            };

            try {
                const cleanup = definition.connect(root, context);
                controllers.set(root, {
                    root,
                    rootId,
                    abortController,
                    cleanup: typeof cleanup === 'function' ? cleanup : null
                });
            } catch (error) {
                abortController.abort();
                this.recordDiagnostic({
                    code: 'BehaviorConnectFailed',
                    message: `RazorWire behavior "${definition.name}" failed while connecting a root.`,
                    impact: 'RazorWire discarded the partial controller and aborted its signal so repeated scans can retry without duplicate listeners.',
                    fix: 'Fix the connect callback so it completes without throwing. Bind event listeners with context.signal so partial setup is abortable.',
                    docs: docsPath,
                    behaviorName: definition.name,
                    selector: definition.selector,
                    rootId
                });

                if (window.RazorWire?.config?.developmentDiagnostics === true && typeof console?.warn === 'function') {
                    console.warn('RazorWire behavior connect failed.', error);
                }
            }
        }

        private disconnect(
            definition: RazorWireBehaviorDefinition,
            controller: RazorWireBehaviorController) {
            controller.abortController.abort();

            if (!controller.cleanup) {
                return;
            }

            try {
                controller.cleanup();
            } catch (error) {
                this.recordDiagnostic({
                    code: 'BehaviorCleanupFailed',
                    message: `RazorWire behavior "${definition.name}" failed while cleaning up a root.`,
                    impact: 'RazorWire removed the controller state and continued pruning other behavior roots.',
                    fix: 'Make the cleanup callback no-throw and keep listener cleanup tied to context.signal where possible.',
                    docs: docsPath,
                    behaviorName: definition.name,
                    selector: definition.selector,
                    rootId: controller.rootId
                });

                if (window.RazorWire?.config?.developmentDiagnostics === true && typeof console?.warn === 'function') {
                    console.warn('RazorWire behavior cleanup failed.', error);
                }
            }
        }

        private findMatchingRoots(root: Document | Element, definition: RazorWireBehaviorDefinition) {
            const matches: Element[] = [];
            if (root instanceof Element && this.matches(root, definition.selector, definition)) {
                matches.push(root);
            }

            try {
                matches.push(...Array.from(root.querySelectorAll(definition.selector)));
            } catch {
                this.recordSelectorInvalid(definition.selector, definition.name);
            }

            return matches;
        }

        private isDefinitionShapeValid(definition: unknown): definition is RazorWireBehaviorDefinition {
            if (typeof definition !== 'object' || definition === null) {
                return false;
            }

            const candidate = definition as { name?: unknown; selector?: unknown; connect?: unknown };
            return typeof candidate.name === 'string'
                && candidate.name.trim().length > 0
                && typeof candidate.selector === 'string'
                && candidate.selector.trim().length > 0
                && typeof candidate.connect === 'function';
        }

        private getInvalidDefinitionContext(definition: unknown) {
            if (typeof definition !== 'object' || definition === null) {
                return {};
            }

            const candidate = definition as { name?: unknown; selector?: unknown };
            return {
                behaviorName: typeof candidate.name === 'string' ? candidate.name : undefined,
                selector: typeof candidate.selector === 'string' ? candidate.selector : undefined
            };
        }

        private isSelectorValid(selector: string, behaviorName: string) {
            try {
                document.querySelector(selector);
                return true;
            } catch {
                this.recordSelectorInvalid(selector, behaviorName);
                return false;
            }
        }

        private recordSelectorInvalid(selector: string, behaviorName?: string) {
            this.recordDiagnostic({
                code: 'BehaviorSelectorInvalid',
                message: `RazorWire behavior selector "${selector}" is invalid.`,
                impact: 'RazorWire skipped this behavior definition so it cannot break other behavior scans.',
                fix: 'Use a valid CSS selector for the behavior root.',
                docs: docsPath,
                behaviorName,
                selector
            });
        }

        private matches(root: Element, selector: string, definition: RazorWireBehaviorDefinition) {
            try {
                return root.matches(selector);
            } catch {
                this.recordSelectorInvalid(selector, definition.name);
                return false;
            }
        }

        private isAbortSupported() {
            return typeof AbortController === 'function' && typeof AbortSignal === 'function';
        }

        private createRootId(definition: RazorWireBehaviorDefinition) {
            this.nextRootId += 1;
            return `${definition.name}:${this.nextRootId}`;
        }

        private recordDiagnostic(diagnostic: RazorWireBehaviorDiagnostic) {
            const normalized = {
                docs: docsPath,
                ...diagnostic
            };
            if (this.diagnostics.some(existing =>
                existing.code === normalized.code
                && existing.message === normalized.message
                && existing.fix === normalized.fix
                && existing.behaviorName === normalized.behaviorName
                && existing.selector === normalized.selector
                && existing.rootId === normalized.rootId)) {
                return;
            }

            this.diagnostics.push(normalized);

            if (window.RazorWire?.config?.developmentDiagnostics === true && typeof console?.warn === 'function') {
                console.warn(`RazorWire behavior kit: ${normalized.message} Impact: ${normalized.impact} Fix: ${normalized.fix} Docs: ${normalized.docs}`);
            }
        }
    }

    const existing = window.RazorWire?.behaviors as RazorWireBehaviorRegistrationStub | undefined;
    const queuedDefinitions = Array.isArray(existing?.__queuedDefinitions)
        ? [...existing.__queuedDefinitions]
        : [];
    const manager = new BehaviorKitManager(queuedDefinitions);

    window.RazorWire = {
        ...(window.RazorWire || {}),
        behaviors: manager
    };

    manager.start();
})();
