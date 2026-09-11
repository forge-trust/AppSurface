# Typed Work definitions v1 migration

This guide changes Durable Work authoring syntax. It does not add a database migration, alter the Work fingerprint
format, or rewrite accepted records. Read the [Work protocol v1](../work-protocol-v1.md) for acceptance, duplicate,
provider-safety, and caller-owned transaction rules.

## Syntax-only replacement

For a Work whose name/version, input and result codec behavior, provider safety, executor style, retry values, due time,
and request identities remain equivalent, replace the old registration with one definition binding:

The examples below build separate before/after service collections so each composition registers its Work identity
once. Each method accepts the same explicit scope, command, duplicate-submission key, payload, retry policy and due time;
it compares the resulting requests and resolves both registrations. They execute as part of the [packed proof](https://github.com/forge-trust/AppSurface/blob/main/Durable/verify-packed-consumers.sh).
The [complete compiled source](../packed-consumers/Adopter/TypedWorkDefinitionProof.cs) contains all definitions, codecs,
source-generated JSON metadata and executors. Legacy overloads remain supported and are not obsolete. Use the direct
request constructor when contract facts are already supplied by validated infrastructure; use a definition to share
application-owned facts between registration and request sites. No codemod can infer these policy choices safely.

<!-- appsurface:snippet id="durable-typed-work-migration-ordinary" file="Durable/packed-consumers/Adopter/TypedWorkDefinitionProof.cs" marker="durable-typed-work-migration-ordinary" lang="csharp" -->
```csharp
internal static void Ordinary(DurableScopeId scope, DurableCommandId command, string key,
    InvoiceWork work, DurableWorkRetryPolicy retryPolicy, DateTimeOffset? dueAtUtc)
{
    // Before and after are alternative host compositions; never register both in one provider.
    var legacy = new ServiceCollection();
    legacy.AddDurableWork<InvoiceWork, InvoiceResult, TypedWorkDefinitionProof.InvoiceExecutor>(
        "examples.invoice.send", "v1", DurableProviderSafety.Idempotent,
        TypedWorkCodecs.InvoiceWorkCodec, TypedWorkCodecs.InvoiceResultCodec);
    var before = new DurableWorkRequest(scope, command, key, "examples.invoice.send", "v1",
        TypedWorkCodecs.InvoiceWorkCodec.Encode(work), DurableProviderSafety.Idempotent, retryPolicy, dueAtUtc);

    var migrated = new ServiceCollection();
    migrated.AddDurableWork(TypedWorkDefinitionProof.OrdinaryDefinition.ExecutedBy<TypedWorkDefinitionProof.InvoiceExecutor>());
    var after = TypedWorkDefinitionProof.OrdinaryDefinition.CreateRequest(
        scope, command, key, work, retryPolicy: retryPolicy, dueAtUtc: dueAtUtc);
    TypedWorkDefinitionProof.AssertRequestParity(before, after);
    using var beforeProvider = legacy.BuildServiceProvider();
    using var afterProvider = migrated.BuildServiceProvider();
    _ = beforeProvider.GetRequiredService<IDurableWorkRegistry>().GetRequired("examples.invoice.send", "v1");
    _ = afterProvider.GetRequiredService<IDurableWorkRegistry>().GetRequired("examples.invoice.send", "v1");
}
```
<!-- /appsurface:snippet -->

Do not register both forms for one `(WorkName, WorkVersion)` in one provider. Registration order and object identity do
not select a winner; the Work registry rejects the duplicate. The definition's request factory preserves the existing
fingerprint and all accepted request fields. An explicit `retryPolicy` override and `dueAtUtc` remain caller decisions.

The same compiled source carries the reconciled and exit-aware replacements:

<!-- appsurface:snippet id="durable-typed-work-migration-reconciled" file="Durable/packed-consumers/Adopter/TypedWorkDefinitionProof.cs" marker="durable-typed-work-migration-reconciled" lang="csharp" -->
```csharp
internal static void Reconciled(DurableScopeId scope, DurableCommandId command, string key,
    LedgerWork work, DurableWorkRetryPolicy retryPolicy, DateTimeOffset? dueAtUtc)
{
    // Before and after are alternative host compositions; never register both in one provider.
    var legacy = new ServiceCollection();
    legacy.AddDurableWorkWithReconciler<LedgerWork, LedgerResult, TypedWorkDefinitionProof.LedgerExecutor, TypedWorkDefinitionProof.LedgerReconciler>(
        "examples.ledger.reconcile", "v1", TypedWorkCodecs.LedgerWorkCodec, TypedWorkCodecs.LedgerResultCodec);
    var before = new DurableWorkRequest(scope, command, key, "examples.ledger.reconcile", "v1",
        TypedWorkCodecs.LedgerWorkCodec.Encode(work), DurableProviderSafety.ReconcileBeforeRetry, retryPolicy, dueAtUtc);

    var migrated = new ServiceCollection();
    migrated.AddDurableWork(TypedWorkDefinitionProof.ReconciledDefinition.ExecutedBy<TypedWorkDefinitionProof.LedgerExecutor>()
        .ReconciledBy<TypedWorkDefinitionProof.LedgerReconciler>());
    var after = TypedWorkDefinitionProof.ReconciledDefinition.CreateRequest(
        scope, command, key, work, retryPolicy: retryPolicy, dueAtUtc: dueAtUtc);
    TypedWorkDefinitionProof.AssertRequestParity(before, after);
    using var beforeProvider = legacy.BuildServiceProvider();
    using var afterProvider = migrated.BuildServiceProvider();
    _ = beforeProvider.GetRequiredService<IDurableWorkRegistry>().GetRequired("examples.ledger.reconcile", "v1");
    _ = afterProvider.GetRequiredService<IDurableWorkRegistry>().GetRequired("examples.ledger.reconcile", "v1");
}
```
<!-- /appsurface:snippet -->

<!-- appsurface:snippet id="durable-typed-work-migration-exit" file="Durable/packed-consumers/Adopter/TypedWorkDefinitionProof.cs" marker="durable-typed-work-migration-exit" lang="csharp" -->
```csharp
internal static void ExitAware(DurableScopeId scope, DurableCommandId command, string key,
    NotificationWork work, DurableWorkRetryPolicy retryPolicy, DateTimeOffset? dueAtUtc)
{
    // Before and after are alternative host compositions; never register both in one provider.
    var legacy = new ServiceCollection();
    legacy.AddDurableWorkExit<NotificationWork, NotificationResult, TypedWorkDefinitionProof.NotificationExitExecutor>(
        "examples.notification.send", "v1", TypedWorkCodecs.NotificationWorkCodec, TypedWorkCodecs.NotificationResultCodec);
    var before = new DurableWorkRequest(scope, command, key, "examples.notification.send", "v1",
        TypedWorkCodecs.NotificationWorkCodec.Encode(work), DurableProviderSafety.ProviderKeyed, retryPolicy, dueAtUtc);

    var migrated = new ServiceCollection();
    migrated.AddDurableWork(TypedWorkDefinitionProof.ExitDefinition.ExecutedByExit<TypedWorkDefinitionProof.NotificationExitExecutor>());
    var after = TypedWorkDefinitionProof.ExitDefinition.CreateRequest(
        scope, command, key, work, retryPolicy: retryPolicy, dueAtUtc: dueAtUtc);
    TypedWorkDefinitionProof.AssertRequestParity(before, after);
    using var beforeProvider = legacy.BuildServiceProvider();
    using var afterProvider = migrated.BuildServiceProvider();
    _ = beforeProvider.GetRequiredService<IDurableWorkRegistry>().GetRequired("examples.notification.send", "v1");
    _ = afterProvider.GetRequiredService<IDurableWorkRegistry>().GetRequired("examples.notification.send", "v1");
}
```
<!-- /appsurface:snippet -->

## Semantic or exit-aware rollout

Changing codec semantics, provider safety, executor/binding style, or exit capability is a Work-version rollout. Create a
new immutable Work version when the existing compatibility discipline requires it. For exit-aware Work, deploy the
provider/runtime capability first, restart every worker that can discover the new contract, then register and accept the
new version. Keep workers capable of the accepted version until it is terminal or intentionally suspended.

If a rollout fails, stop new submissions for the affected version and inspect the existing diagnostics. Roll back the
composition or package only when the remaining workers still understand every accepted version. An old success-only
provider cannot safely process a non-success exit from an accepted exit-aware version.

There is no SQL rollback for this authoring change. Database migrations remain forward-only; syntax-only adoption needs
no schema step. Semantic changes require the existing Work-version and mixed-deployment checks. Never delete or rewrite
migration history, and never add a second registration as a test shortcut.

## Reuse, lookup, and concurrency

Input and result codec identities are independent from Work identity. The same generic CLR type may participate in several
contracts, but type-only codec lookup is ambiguous when more than one registered contract matches; use exact type,
contract name, and contract version lookup. A Flow activity must resolve the exact global `DurableWorkRegistration` by
Work name/version and pass the definition's codec views to the existing
`DurableFlowActivityBinding<TContext,TWork,TResult>` constructor. Do not create a nested service provider or a second
Work registration inside the Flow factory.

The [PostgreSQL Flow client](../ForgeTrust.AppSurface.Durable.PostgreSql/README.md) accepts the same definition-owned
and provider-owned codec views at startup. It retains captured payload guards even when a custom registry returns
the original raw source, and validates and decodes through that allowlisted source before
storage; an unrelated codec with matching metadata still fails. Sharing a definition codec with Flow therefore preserves
both registration and command behavior.

Static definitions may be shared by concurrent callers. Codec implementations, approval predicates, and captured
dependencies must provide their own thread safety and stable contract behavior. Durable does not lock around consumer
codec code or poll mutable metadata after capture.

## Validation and troubleshooting

Construction validates identifiers, safety, retry defaults, and codec metadata before any encode, executor, reconciler,
provider, or storage operation. Request validation checks scope, command, duplicate key, and non-null input before one
encode. A codec contract mismatch is a bounded local `InvalidOperationException`; provider/runtime diagnostics retain
their separate `ASDURxxx` meanings. See the [bounded diagnostics table](../../troubleshooting/durable-diagnostics.md#typed-work-definition-validation)
when a definition or guarded codec rejects a contract.
