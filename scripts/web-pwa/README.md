# Verify the web PWA from a checkout

From the repository root, check that `dotnet --version` matches [global.json](https://github.com/forge-trust/AppSurface/blob/main/global.json), then run:

```sh
dotnet run --file scripts/web-pwa/verify.cs
dotnet run --file scripts/web-pwa/verify.cs -- --port 5056 --evidence-path "out dir/proof.json"
dotnet run --file scripts/web-pwa/verify.cs -- --help
```

Use an absolute path to `verify.cs` when running from a different directory. Check the SDK in that directory too. The verifier builds the repository’s [example](../../examples/web-pwa-install/README.md) and [public PWA CLI](../../Cli/ForgeTrust.AppSurface.Cli/README.md) using project sources, so run it from a complete checkout. A [standalone Console example](../../examples/file-based-console/README.md) uses published packages.

If the SDK cannot restore file-based app packages through a symlinked worktree, invoke `verify.cs` with the canonical checkout path.

## Configure a run

An explicit switch wins, including a value equal to its default. Otherwise the verifier resolves the logical key through [AppSurface.Config](../../guides/config-logical-keys.md), with environment values taking precedence over the matching JSON file. Only a missing key receives its default. The command validates the resulting settings before starting child processes.

| Switch | Logical key | Unscoped environment name | Default |
| --- | --- | --- | --- |
| `--port` | `PwaVerify:Port` | `APP_SURFACE_WEB_PWA_PORT` | 5055 on the example child, valid range 1–65535 |
| `--evidence-path` | `PwaVerify:EvidencePath` | `APP_SURFACE_WEB_PWA_EVIDENCE` | `examples/web-pwa-install/pwa-verify-v3.json` in the checkout |
| `--startup-timeout-seconds` | `PwaVerify:StartupTimeoutSeconds` | `PWAVERIFY__STARTUPTIMEOUTSECONDS` | 60 seconds from example launch, positive integers only |

The port and evidence environment names carry forward existing automation. The timeout uses Config’s hierarchical spelling. An explicit relative evidence path, including one supplied by a provider, resolves from the caller’s current directory. Parent directories are created when publishing. Use distinct destinations for concurrent runs.

With the verifier’s default Production environment, put the following in `appsettings.json` beside `verify.cs`. When selecting Development for the verifier, use `appsettings.Development.json` in that same directory. The verifier passes Development to the example process independently of its own configuration environment. [Environment selection](https://github.com/forge-trust/AppSurface/blob/main/ForgeTrust.AppSurface.Core/Defaults/DefaultEnvironmentProvider.cs)

```json
{
  "PwaVerify": {
    "Port": 5056,
    "StartupTimeoutSeconds": 30
  }
}
```

## Read the result

The verifier prints the PIDs of its example and public CLI to stderr. An opt-in Development endpoint at `/_appsurface/verify/instance` returns a fresh value passed only to the launched example, for requests arriving over loopback. A listening port or a plausible manifest alone cannot establish ownership. The verifier also checks the manifest and status endpoint, runs the public CLI with combined install/push expectations, then confirms the example identity again.

A publication message identifies this run’s complete schema-v3 JSON. Verification failure can produce a valid `passed: false` report and the public CLI’s nonzero status. Binding, startup, malformed output and file errors produce a nonzero exit. An existing destination can belong to an earlier run. Check stderr and the exit code, and use a fresh path in CI. If startup fails, check the child port and the example build. If publication fails, choose a writable file path. The runner tears down processes it started on success, failure and cooperative cancellation.

The command uses a local attribute beside each CliFx switch to choose the Config key for an omitted value. CliFx handles parsing. One final settings object owns validation. The pilot adds no package API. The [repository tooling guide](../../guides/repository-tooling.md) describes how to choose a file or project for the next tool.
