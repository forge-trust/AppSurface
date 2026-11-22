# File-based Console and Config sample

This sample is a package-only .NET file-based app. It uses the published AppSurface Console and Config APIs, CliFx switches, and a small command-local binding convention owned by this file. The packages do not provide this pilot binding API. The sample keeps switch parsing in CliFx and resolves omitted values through `IConfigManager`.

## Run outside the checkout

The .NET SDK must be able to write its file-based app cache. On macOS, an access error naming `Application Support/dotnet/runfile` refers to this SDK cache. Run the command with access to that directory under the current user account.

Install the pinned .NET SDK. Copy `Program.cs` and `appsettings.json` into a sample directory outside the repository. Copy the repository's `global.json` into a common parent of that sample directory and a separate caller directory, then run from the caller directory. The file-based app locates JSON beside the source file through the .NET SDK's entry-point source directory, while the copied `global.json` pins SDK selection from the caller directory.

```sh
dotnet --version
# 10.0.402
```

Set `sample_root`, `sample_dir`, and `caller_dir` to the absolute paths you created, then change to `caller_dir`:

```sh
sample_root=/absolute/path/to/file-based-console-consumer
sample_dir="$sample_root/sample"
caller_dir="$sample_root/caller"
cd "$caller_dir"
dotnet --version
dotnet run --file "$sample_dir/Program.cs" --artifacts-path "$sample_dir/.artifacts" -- --help
dotnet run --file "$sample_dir/Program.cs" --artifacts-path "$sample_dir/.artifacts" -- inspect
dotnet run --file "$sample_dir/Program.cs" --artifacts-path "$sample_dir/.artifacts" -- inspect --region ap-southeast-2
dotnet run --file "$sample_dir/Program.cs" --artifacts-path "$sample_dir/.artifacts" -- inspect --port 5055 --region ap-southeast-2
```

On Windows PowerShell, use the same layout and change to the caller directory before running:

```powershell
$sampleRoot = 'C:\path\to\file-based-console-consumer'
$sampleDir = Join-Path $sampleRoot 'sample'
$callerDir = Join-Path $sampleRoot 'caller'
Push-Location $callerDir
dotnet --version
dotnet run --file "$sampleDir\Program.cs" --artifacts-path "$sampleDir\.artifacts" -- --help
dotnet run --file "$sampleDir\Program.cs" --artifacts-path "$sampleDir\.artifacts" -- inspect
dotnet run --file "$sampleDir\Program.cs" --artifacts-path "$sampleDir\.artifacts" -- inspect --region ap-southeast-2
dotnet run --file "$sampleDir\Program.cs" --artifacts-path "$sampleDir\.artifacts" -- inspect --port 5055 --region ap-southeast-2
Pop-Location
```

The command help path renders CliFx metadata without reading configuration or resolving secrets. The baseline `inspect` resolves the JSON port `5454`, region `us-east-1`, and endpoint `https://api.example.test`. The last invocation explicitly supplies port `5055` and region `ap-southeast-2`. Confirm the printed `Effective port: 5055. Effective region: ap-southeast-2` output. The command validates the final settings object with .NET DataAnnotations, including the port range, required values, and endpoint URL, then compares the synthetic typed secret in memory without printing its payload. If `Demo:Port` in JSON is malformed, an explicit `--port` bypasses that lower-priority value. Without `--port`, Config converts and validates the configured port and reports the invalid value as an error.

To exercise the negative paths, run the app with `--not-a-switch` and with `inspect --port 70000`. CliFx rejects the unknown switch, and DataAnnotations rejects the out-of-range port. Both invocations exit nonzero. The package smoke test also runs the copied app from another caller directory, injects a throwing provider into a help-only copy, and scans help, success, and failure output for the synthetic secret.

## Precedence

| Input | Config key | Environment name | Result when supplied |
| --- | --- | --- | --- |
| `--port` | `Demo:Port` | `DEMO__PORT` | Explicit switch wins and skips typed conversion of the lower-priority port value. |
| omitted `--port` | `Demo:Port` | `DEMO__PORT` | The JSON-backed settings object's 5454. |
| `--region` | `Demo:Region` | `DEMO__REGION` | Explicit value `ap-southeast-2` wins over JSON `us-east-1`, and is printed for verification. |
| typed secret | `Demo:Credentials:ApiKey` | Provider-defined | The sample's synthetic provider returns a package-proof value which is checked in memory and redacted from output. |

The local `[OptionConfigKey]` marker names a Config key. CliFx's `[CommandOption]` owns switch parsing. `ReadSettings` reads `Demo:Port` only when `--port` is omitted, reads the other scalar settings individually, and loads the secret from the separate `Demo:Credentials` root. This keeps an invalid lower-priority port from failing the command when an explicit value is supplied. `Validator.ValidateObject(..., validateAllProperties: true)` validates the assembled settings instance. The JSON file declares the typed secret destination, and the local provider returns the same fixed synthetic marker on each run.

Exact package pins are `ForgeTrust.AppSurface.Console` `0.2.0-preview.14`, `ForgeTrust.AppSurface.Config` `0.2.0-preview.14`, and `Microsoft.Extensions.Hosting` `10.0.14`. This file-based app disables Native AOT and trimming because Config's typed secret composition uses runtime JSON metadata. For the canonical checkout switch table, see the [PWA verifier guide](../../scripts/web-pwa/README.md#configure-a-run). For repository inventory, adoption guidance, and file-based tooling conventions, see the [repository tooling guide](../../guides/repository-tooling.md). The AppSurface Console package's command authoring contract is documented in the [Console README](../../Console/ForgeTrust.AppSurface.Console/README.md#command-authoring-with-clifx-3).
