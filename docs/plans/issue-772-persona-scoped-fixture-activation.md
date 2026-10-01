# Persona-scoped fixture activation execution plan (#772)

Status: APPROVED by the user with final Autoplan answer A on 2026-10-01. This file specifies future implementation and acceptance, not completed code or runtime evidence. Source: [design and decision record](../designs/issue-772-persona-scoped-fixture-activation.md). Case: [#772](https://github.com/forge-trust/AppSurface/issues/772).

The outcome is one optional request-scoped host activation hook, awaited after DevAuth queues the protected persona cookie and before normal navigation. Extend the existing sample to prove that labeler and reviewer share one candidate while preserving separate completed work. Preserve the approved cookie order and host-owned failure policy.

## 1. Package hook and regression contract

- [ ] Add `IAppSurfaceDevAuthPersonaSelectionHandler` in the existing [DevAuth package](../../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md), with `ValueTask ActivateAsync(AppSurfaceDevAuthPersona persona, HttpContext httpContext, CancellationToken cancellationToken)` and full XML reference/rationale/pitfalls.
- [ ] Make the existing validated selection method asynchronous. After existing admission/ID lookup and protected-cookie append, resolve one optional unkeyed handler through the current request's `RequestServices.GetService`. Use normal `AddScoped`; do not resolve at startup/root, add a scope or capture a handler in options.
- [ ] Only when a handler exists, capture `RequestAborted`, check it before invocation, pass it, await once and check it after normal return before either success branch. Exceptions/cancellation propagate. Preserve explicit safe returnUrl > LandingUrl > control page.
- [ ] Extend [endpoint tests](../../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests/AppSurfaceDevAuthEndpointTests.cs) at real request boundaries: synchronous/async completion, pending redirect/control response, explicit persona versus incoming principal, cookie-before-call, absent service, no resolution on guards/invalid/non-selection, real scope identity/disposal, resolution/fault/cancellation branches. Retain existing cookie, guard, navigation, clear, status and marker contracts.

## 2. Host scenario, admission and recovery

- [ ] Extend the [existing sample](../../examples/auth-aspnetcore-dev-auth/README.md) with labeler/reviewer personas and explicit role policies; keep admin/viewer proof intact.
- [ ] Use one singleton `LocalCandidateFixtureStore`, fixed key `candidate-review-demo-v1`, one stable synthetic ID, per-role readiness and separate labeling/review completion. Keep its lock short and synchronous and return immutable coherent snapshots. No GET initialization, reset-on-select or mutable snapshot escape; restart intentionally resets the sample.
- [ ] Register scoped `LocalFixtureActivation`. Compose atomic ensure then role readiness, with admin/viewer successful no-op. Log start/success/failure/cancel through `ILogger<LocalFixtureActivation>` with trace ID, safe configured persona ID and fixed outcomes; exclude cookies/claims/raw exceptions.
- [ ] Put the four new candidate routes in a host-owned local admission group, with environment/loopback/same-origin guards and explicit role authorization. Wrong role, cross-site, anonymous or not-ready actions must not mutate or ensure fixtures.
- [ ] Put the sample typed failure middleware before auth/authorization/endpoints and after any configured outer general error handler. Catch only `LocalFixtureActivationException` before response start; fixed safe HTTP500, preserve queued Set-Cookie, no Location, no Response.Clear. Other errors/cancellation propagate.
- [ ] In [example HTTP tests](../../examples/auth-aspnetcore-dev-auth.tests/AuthAspNetCoreDevAuthExampleTests.cs), replace only the interface via `ConfigureTestServices` with a scoped test handler using the same singleton store and one shared test-owned fault counter. Prove failure → retained reviewer identity → read-only 409 → explicit POST retry with original candidate/labeler work. No runtime fault query/header.

## 3. Operator pages and accessibility

| Route | Required behavior |
| --- | --- |
| GET `/candidate/label` | Labeler policy; coherent read only; ready shows candidate and both stages, otherwise HTTP409 and explicit labeler POST reselection |
| GET `/candidate/review` | Reviewer policy; corresponding read-only ready/409 page |
| POST `/candidate/label/complete` | Local admission + labeler policy + existing ready candidate; only labeling changes; repeat is idempotent; HTTP303 `/candidate/label` |
| POST `/candidate/review/complete` | Corresponding reviewer-only transition; HTTP303 `/candidate/review` |

- [ ] Set LandingUrls to the corresponding page. Reselection forms POST to `/_appsurface/dev-auth/select/{persona}` with matching rooted, encoded returnUrl; GET never repairs data.
- [ ] Ready pages show the persistent identity marker, one role h1, `Fixtures ready`, stable ID, `Labeling: pending/completed`, `Review: pending/completed`, one primary role action and secondary change-persona navigation. Replace/disable completed actions. Encode dynamic values.
- [ ] Not-ready pages say `Fixtures not ready` and expose `Reselect labeler/reviewer`. The fixed 500 copy is `Persona selection did not finish. Fixtures are not ready. Open /_appsurface/dev-auth/ and select the persona again.` Native full-page form pending behavior waits for activation.
- [ ] Use the accepted light CSS tokens, 760px max width, 32px/16px padding, >=16px body/1.5 line height, 32px/28px h1, underlined visited links, offset focus and >=44px actions. Retain system UI fonts (Dsg4, approved at the final gate). Check landmarks, visible names, one h1, keyboard, contrast >=4.5:1, 375px/768px and 200% zoom with no overflow. Preserve package marker behavior.

## 4. Deterministic proof and existing verifier

- [ ] Implement all G01-G29 groups from the [saved test plan](issue-772-test-plan.md), retaining E01-E05 regressions. No feature tests have run yet.
- [ ] Use independent cookie clients and explicit gates for both overlapping activation orders; prove exactly one ID, coherent state and unchanged completed work. Add product-action/reselection overlap and repeated completion cases. Avoid sleeps or stress runs as correctness proof.
- [ ] Extend [verify.sh](../../examples/auth-aspnetcore-dev-auth/verify.sh) and [VerifierContractTests](../../examples/auth-aspnetcore-dev-auth.tests/VerifierContractTests.cs) together for stable-ID, independent progress and repeated persona switches. Keep child-owned listening evidence, private cookie jars, disabled redirects/proxies, finite timeouts, classified failures, bounded redacted artifacts and owned-child cleanup. Parse IDs privately; do not echo arbitrary HTML/cookies. Inject partial failure/cancellation only in deterministic HTTP tests.
- [ ] Execute the [browser QA checklist](issue-772-browser-qa.md) against the actual loopback host; record observed browser/accessibility/recovery results separately from authored plans.

## 5. Documentation, discoverability and release note

- [ ] Add a complete source-backed opt-in next to the [package quickstart](../../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md), including defined types/usings/setup/expected readiness. Compile and HTTP-prove it; snippet synchronization alone is insufficient.
- [ ] Put no-handler default, request scope, explicit persona versus incoming principal, cookie-before-activation/non-atomic failure, cooperative cancellation, response ownership and host composition/deadline guidance beside registration. Link reference, decision and pitfall content.
- [ ] Add resolution/partial-failure/cancellation runbooks and safe local log lookup to package/sample docs; update [auth adoption ladder](../../start-here/auth-adoption-ladder.md), relevant [repository overview](../../README.md), and [package-index source](../../packages/package-index.yml) followed by generated chooser. Use the [PackageIndex workflow](../../tools/ForgeTrust.AppSurface.PackageIndex/README.md#change-workflow).
- [ ] Add an included append-only entry under [unreleased entries](../../releases/unreleased.entries) describing additive opt-in, unchanged default, awaited timing and host recovery. Follow [contribution/release rules](../../CONTRIBUTING.md); no publishing is authorized here.
- [ ] Measure prepared-developer read/restore/build/run/select-label-work/switch-review/understand journey with .NET10 and a fresh usable checkout. Target <=5 minutes; current measurement unknown. Record SDK/checkout acquisition and cold/warm prerequisites separately. A miss is an observation, not an automated release failure.

## 6. Verification before pushing implementation

Run commands from the repository root, after the owning code/docs are implemented:

```sh
dotnet test Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests.csproj
dotnet test examples/auth-aspnetcore-dev-auth.tests/AuthAspNetCoreDevAuthExample.Tests.csproj
./examples/auth-aspnetcore-dev-auth/verify.sh
dotnet run --project tools/ForgeTrust.AppSurface.MarkdownSnippets/ForgeTrust.AppSurface.MarkdownSnippets.csproj -- generate --document Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md
dotnet run --project tools/ForgeTrust.AppSurface.MarkdownSnippets/ForgeTrust.AppSurface.MarkdownSnippets.csproj -- verify --document Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- generate
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- verify
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- gate
dotnet format
dotnet format --verify-no-changes
dotnet build
dotnet test --no-build
./scripts/coverage-solution.sh
```

Resolve introduced compiler/analyzer/documentation warnings and inspect generated diffs. Check near-complete changed branch coverage; broader solution coverage is required when practical under [repository verification policy](../../CONTRIBUTING.md#local-verification). Run the package artifact proof in the PackageIndex guide when changed package docs/payloads require it. Record actual commands/results and any environmental limits; do not label planned checks as passed.

## Dependencies and final approval

Sequential implementation, no parallelization opportunity: the hook, sample, tests and verifier share dependent modules. Docs can be drafted alongside work but must be reconciled against final source before verification.

| Step | Modules | Depends on |
| --- | --- | --- |
| Package hook + package regressions | Auth DevAuth / DevAuth.Tests | Final plan approval |
| Host state, guarded pages + HTTP proof | example / example tests | Hook contract |
| Verifier and shim | example / example tests | Final routes/state |
| Docs/discovery/release entry | package README / start-here / packages / releases | Final API and runnable sample |
| Full acceptance | solution / docs tools / manual QA | Owning implementation and tests |

Approved taste choices: C4 extend the existing verifier; U1 HTTP409 + native POST retry; Dsg4 retain the sample font stack. The [durable/multi-process TODO](../../TODOS.md#host-owned-durable-or-multi-process-reference-example) is saved as P3 and requires adopter evidence before new scope. No implementation, commit, push or release has been performed by this planning run.
