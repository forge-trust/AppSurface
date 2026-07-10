interface RazorWireBehaviorQueueItem {
    kind: 'register' | 'registerLifecycle';
    definition: unknown;
}

interface RazorWireBehaviorRegistrationStub {
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
