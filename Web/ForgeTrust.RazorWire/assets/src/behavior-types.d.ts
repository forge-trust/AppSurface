interface RazorWireBehaviorRegistrationStub {
    __queuedDefinitions?: unknown[];
    register(definition: unknown): void;
    scan(root?: Document | Element): void;
    prune(): void;
    getDiagnostics(): unknown[];
    clearDiagnostics(): void;
}
