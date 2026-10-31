# Stage events

The Keycloak recipe package exposes bounded progress observations through
[`KeycloakStageEvents`](../README.md) and the BCL
[`ActivitySource`](https://learn.microsoft.com/dotnet/api/system.diagnostics.activitysource)
named `ForgeTrust.AppSurface.Auth.Keycloak`. The events are intended for a host
progress panel, a test observer, or an OpenTelemetry bridge. They are diagnostic
observations; they do not authorize publication, release, or deployment.

## Event contract

`KeycloakStageEvents.Begin` returns a disposable `KeycloakStageScope`. A scope
publishes one `Started` event immediately and one terminal event when the owner
calls `Complete` or `Reject`. Disposing an unfinished scope publishes a safe
`Rejected` event with `UNCOMPLETED`. The same `CorrelationId` joins the pair.

The package emits scopes for public capture, materialization, image resolution,
inspection, and evidence writing. A host may use the same API for Docker preflight,
build, and release-pending bookkeeping. A host build is still owned
by the caller; the package never treats a stage event as proof of publication.

Each event contains the phase, outcome, elapsed milliseconds, effect state,
optional material/check counts, canonical recipe, plan, policy, operation, and
check identities, a bounded profile key, and a stable failure code for
rejections. Digests are
accepted only as lowercase 64-character SHA256 identities. Profile and code
values are bounded and sanitized. Event observers receive no local paths,
Docker output, archive members, command lines, or exception messages. An
observer throwing an exception is isolated from the recipe workflow.

`Truncated` means a bounded identity/profile/code value was shortened or an unsafe
identity was omitted. It is a signal to inspect the typed result, never a
request to log the original value. `Effect` distinguishes no committed effect,
staged data, committed output, and an unknown effect requiring inspection.

## Usage

```csharp
var events = new List<KeycloakStageEvent>();
using var subscription = KeycloakStageEvents.Subscribe(events.Add);
using var stage = KeycloakStageEvents.Begin(
    KeycloakStagePhase.Build, plan.RecipeDigest, plan.PolicyDigest, "docker-29");

stage.SetCounts(materialCount: 12);
// Invoke the host builder with KeycloakBuildContext.CreateDockerBuildArguments.
stage.Complete(KeycloakEffectState.Unknown); // inspect the image before release.
```

Subscriptions are invocation-local through the current async flow, so unrelated
recipe invocations do not receive each other's events. Subscription snapshots
are copied under a lock before notification; disposing a subscription during
notification cannot change the workflow or mutate the active observer set.

For a rejected typed package operation, pass the `KeycloakSecurityException` to
`Reject`. The resulting event carries only its stable code and effect. For a
host-only failure, use the overload accepting a preselected safe code; never
pass process output or an exception message. Keep the observer bounded and
dispose the subscription before unloading a host component.

## Ordering and scope

Capture and materialization events identify the canonical recipe and policy
after those identities are computed. Inspection records check counts only after
the returned evidence is complete. The sample records Docker client/server and
buildx versions in a separate host receipt, then emits preflight, build,
inspection, evidence, and release-pending stages. The host receipt is an
environment observation and is not part of recipe identity.

For the complete input and diagnostic contracts, see
[`api-and-contracts.md`](api-and-contracts.md) and
[`diagnostics.md`](diagnostics.md). For host prerequisites and the release
boundary, see [`host-prerequisites.md`](host-prerequisites.md).


## Sample output files

The [compiled sample](../../../examples/auth-keycloak-security-recipes/README.md#progress-and-output-files) emits JSON stage events on stderr. For the requested evidence filename, it also attempts a `.stages.json` suffix at invocation exit. That sidecar has schema `appsurface-keycloak-stage-progress-v1`, a 300-second deadline declaration, and the observed stage array. It can be written after a rejected wrong-identity check or another failure; it is not passing image evidence. Failure to write this best-effort record does not replace the primary operation result.

Only `--run` performs the Docker host preflight and writes `<evidence>.host.json` before capture. It records observed Docker client/server/buildx versions, independently of canonical recipe identity. Neither this receipt nor a completed `ReleasePending` stage authorizes publication or certifies a successful image. Inspection must return actual passing evidence and publish the requested evidence file separately. Use absent filenames for evidence and both sidecars; existing files are never overwritten.
