# Named canary adoption lab

This local lab shows the complete boundary around a protected AppSurface named canary: your application triggers a workflow and records its evidence, AppSurface evaluates that existing evidence, and your caller chooses what to do with the [bounded `appsurface canary poll` result](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-canary-poll).

> A pass proves one consumer-defined workflow produced acceptable bound evidence at one point in time. It is not health, readiness, traffic rollout analysis, general application safety, automatic production approval, or customer readiness. You choose which features, dependencies, inputs, and stress conditions make the workflow meaningful for your release.

## First local proof (POSIX)

Prerequisites: a restored repository checkout, the .NET 10 SDK, Bash, and `curl`. The verifier starts the local lab, creates ephemeral values without printing them, triggers the application-owned workflow, and calls the source CLI.

```bash
bash examples/named-canary-lab/verify.sh pass
```

Expected safe terminal result:

```text
PASS canary=lab.proof attempts=1 elapsed=...
Named-canary lab 'pass' scenario verified safely.
```

The verifier is a POSIX convenience path, not a new product surface. It builds the lab and source CLI before starting the local host, cleans up the local child process, and does not print credentials, markers, marker fingerprints, endpoint bodies, application payloads, or local logs. It supplies trigger headers through curl's standard input, so they are not expanded into curl process arguments.
It allows up to two minutes for the loopback bind after that build, but stops earlier when the child process exits.

## What runs where

```text
your caller                         your application                    AppSurface
-----------                         ----------------                    ----------
set release policy                  protected trigger                   protected GET route
set marker + freshness       --->   bound proof store            --->   evaluator + envelope
poll CLI exit code                  candidate/environment check         no trigger, no deploy
```

The trigger and the named-canary route use the same local operator policy in this lab. It deliberately models one local development operator, not a multi-operator production permission design. The trigger is application-owned. `appsurface canary poll` is read-only: it never starts the workflow, changes traffic, deploys, rolls back, or selects a CI platform. The process-local proof cache is capped at 128 distinct markers; a full cache returns a bounded `429` response and should be cleared by restarting the lab rather than treated as rollout health. Trigger markers follow the named-canary marker profile, so an accepted proof is always pollable with the same marker.

## Manual walkthrough

Use this path when you want to inspect the integration shape. It keeps every secret-bearing value in environment variables. Run it in one terminal so the background lab and caller share the same local values.

### POSIX shell

```bash
(
set -eu
set +x
export NAMED_CANARY_LAB_OPERATOR_TOKEN="local-operator-$(date +%s)-$$"
export NAMED_CANARY_LAB_MARKER="local-marker-$(date +%s)-$$"
export NAMED_CANARY_LAB_FRESH_SINCE="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

lab_pid=""
cleanup() {
  if [ -n "$lab_pid" ] && kill -0 "$lab_pid" 2>/dev/null; then
    kill "$lab_pid" 2>/dev/null || true
    wait "$lab_pid" 2>/dev/null || true
  fi
}
trap cleanup 0 INT TERM

ASPNETCORE_ENVIRONMENT=Development \
NamedCanaryLab__OperatorToken="$NAMED_CANARY_LAB_OPERATOR_TOKEN" \
NamedCanaryLab__Candidate="local-candidate" \
NamedCanaryLab__Environment="development" \
NamedCanaryLab__Scenario="Pass" \
dotnet run --project examples/named-canary-lab/NamedCanaryLab.csproj -- --port 61260 &
lab_pid=$!

attempt=0
while [ "$attempt" -lt 1200 ]; do
  if curl --silent --show-error --fail http://127.0.0.1:61260/ >/dev/null 2>&1; then
    break
  fi

  attempt=$((attempt + 1))
  sleep 0.1
done
curl --silent --show-error --fail http://127.0.0.1:61260/ >/dev/null

curl --silent --show-error --fail \
  --request POST \
  --config - \
  --output /dev/null \
  http://127.0.0.1:61260/lab/canary/trigger <<EOF
header = "Authorization: Bearer $NAMED_CANARY_LAB_OPERATOR_TOKEN"
header = "X-AppSurface-Canary-Marker: $NAMED_CANARY_LAB_MARKER"
EOF

APPSURFACE_CANARY_TOKEN="$NAMED_CANARY_LAB_OPERATOR_TOKEN" \
APPSURFACE_CANARY_MARKER="$NAMED_CANARY_LAB_MARKER" \
dotnet run --project Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj -- \
  canary poll \
  --url http://127.0.0.1:61260 \
  --name lab.proof \
  --bearer-token-env APPSURFACE_CANARY_TOKEN \
  --marker-env APPSURFACE_CANARY_MARKER \
  --fresh-since "$NAMED_CANARY_LAB_FRESH_SINCE" \
  --timeout 5s \
  --interval 100ms \
  --no-github-summary
)
```

### PowerShell

```powershell
$env:NAMED_CANARY_LAB_OPERATOR_TOKEN = [guid]::NewGuid().ToString("N")
$env:NAMED_CANARY_LAB_MARKER = "local-marker-$([guid]::NewGuid().ToString('N'))"
$env:NAMED_CANARY_LAB_FRESH_SINCE = [DateTimeOffset]::UtcNow.ToString("O")
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:NamedCanaryLab__OperatorToken = $env:NAMED_CANARY_LAB_OPERATOR_TOKEN
$env:NamedCanaryLab__Candidate = "local-candidate"
$env:NamedCanaryLab__Environment = "development"
$env:NamedCanaryLab__Scenario = "Pass"
$lab = Start-Process dotnet -PassThru -ArgumentList "run --project examples/named-canary-lab/NamedCanaryLab.csproj -- --port 61260"

$ready = $false
for ($attempt = 0; $attempt -lt 1200 -and -not $ready; $attempt++) {
  try {
    Invoke-WebRequest -UseBasicParsing http://127.0.0.1:61260/ | Out-Null
    $ready = $true
  }
  catch {
    Start-Sleep -Milliseconds 100
  }
}

try {
  if (-not $ready) {
    throw "The named-canary lab did not become reachable before the local deadline."
  }

  Invoke-WebRequest -UseBasicParsing -Method Post `
    -Headers @{ Authorization = "Bearer $env:NAMED_CANARY_LAB_OPERATOR_TOKEN"; "X-AppSurface-Canary-Marker" = $env:NAMED_CANARY_LAB_MARKER } `
    -Uri http://127.0.0.1:61260/lab/canary/trigger | Out-Null

  $env:APPSURFACE_CANARY_TOKEN = $env:NAMED_CANARY_LAB_OPERATOR_TOKEN
  $env:APPSURFACE_CANARY_MARKER = $env:NAMED_CANARY_LAB_MARKER
  dotnet run --project Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj -- `
    canary poll `
    --url http://127.0.0.1:61260 `
    --name lab.proof `
    --bearer-token-env APPSURFACE_CANARY_TOKEN `
    --marker-env APPSURFACE_CANARY_MARKER `
    --fresh-since $env:NAMED_CANARY_LAB_FRESH_SINCE `
    --timeout 5s `
    --interval 100ms `
    --no-github-summary
}
finally {
  if (-not $lab.HasExited) {
    Stop-Process -Id $lab.Id
  }
}
```

The `curl` or `Invoke-WebRequest` trigger acknowledgement is deliberately discarded. Do not print the named-canary HTTP response either: it is a protected protocol envelope and contains a marker fingerprint. Use the CLI result as the safe caller surface.

## Deterministic local scenarios

Select the scenario at application startup, before triggering the workflow. It cannot be selected by a route, query string, request body, header, or cookie. Restart the lab or choose a new marker between scenarios.

| Scenario | Startup configuration | Safe terminal result | Caller next action |
| --- | --- | --- | --- |
| `pass` | `NamedCanaryLab__Scenario=Pass` | exit `0` | Continue only under your own release policy. |
| `pending` | `NamedCanaryLab__Scenario=Pending` | `ASCAN406`, exit `6` after caller deadline | Verify the trigger and evidence timing, then retry under caller policy. |
| `stale` | `NamedCanaryLab__Scenario=Stale` | `ASCAN403`, exit `3` | Produce fresh evidence after the caller freshness boundary. |

For a quick local check, run `bash examples/named-canary-lab/verify.sh pending` or `bash examples/named-canary-lab/verify.sh stale`. Those commands succeed only when the expected non-pass exit code is observed safely.

## Safe troubleshooting

| What happened | Safe CLI outcome | What to do next | Do not print |
| --- | --- | --- | --- |
| Fresh matching proof | exit `0` | Apply your own release policy. | marker, token, proof record, endpoint body |
| No current proof before deadline | `ASCAN406`, exit `6` | Verify your trigger completed and evidence is bound to this candidate/environment. | headers, marker, raw request, retry trace |
| Stale proof | `ASCAN403`, exit `3` | Create fresh proof after the caller boundary. | old proof content, marker fingerprint, correlation identifier |
| Application-owned failure | `ASCAN403`, exit `3` | Investigate through your protected operations surface. | payload, exception text, unbounded logs |
| Authorization or protocol problem | `ASCAN404`, exit `4` | Correct the host policy or request shape. | credential, authorization header, response body |

The existing CLI writes a bounded diagnostic, terminal result, next action, and documentation link. It does not render marker or credential values. Keep shell tracing disabled around secret-bearing commands and configure request logging so it does not capture the authorization or canary-marker headers.

## Copy the pattern, not the lab

The sample is intentionally Development-only. It fails outside `Development`, uses process-local records, and has no durability, eviction, multi-instance coordination, deployment orchestration, telemetry, or retry policy beyond the existing CLI.

| Lab component | Replace it with in your application |
| --- | --- |
| Local bearer-token handler | Your host-owned authentication scheme and deploy-operator policy. |
| Local process dictionary | A bounded or evicting durable proof source that binds candidate/version, environment, authorized producer, marker, and observed time. |
| Startup scenario | The real synthetic, integration, browser, or load workflow that exercises the release risk you chose. |
| `CanaryLabEvaluator` | Your application-owned evaluator, following the [complete Web fixture](../../Web/ForgeTrust.AppSurface.Web.Tests.CanaryConsumerFixture/CanaryConsumerFixture.cs). |
| Shell exit branch | Your deployment system’s policy for continuing, retrying, investigating, approving, or rolling back. |

This lab adds no server or CLI protocol and does not install a CI integration. The public protocol and terminal-result contract remain the [Web named-canary guide](../../Web/ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints) and [CLI poll reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-canary-poll).
