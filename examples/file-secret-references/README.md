# File declared secret references

This is the runnable golden path for [the canonical file secret reference guide](../../Config/ForgeTrust.AppSurface.Config/docs/file-secret-references.md).
It uses the real Google Secret Manager module with a fake client, so no network, credentials, or Google project are needed.

Run the four proof cases from the repository root:

```bash
export DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false
dotnet build examples/file-secret-references/FileSecretReferencesExample.csproj -p:UseSharedCompilation=false -nodeReuse:false
dotnet run --no-build --project examples/file-secret-references
FILESECRETREFERENCES__APIKEY=environment-value dotnet run --no-build --project examples/file-secret-references
DOTNET_ENVIRONMENT=Development dotnet run --no-build --project examples/file-secret-references
DOTNET_ENVIRONMENT=Failure dotnet run --no-build --project examples/file-secret-references -- failure
```

The reload setting disables the default host's file watcher for these one-shot processes, including consumers copied to a
temporary directory. It does not change file selection or secret resolution. The processes assert outcomes and stop; they
do not demonstrate live configuration reload. PowerShell users can set the same variables with `$env:NAME = 'value'`.

The first case proves `enabled: false` performs no provider read. The second proves an exact environment value rescues
the same destination without a Google read. The third proves the enabled descriptor reaches the fake Google client and
reports the canonical provider id `google-secret-manager`. The final case proves an enabled missing reference remains a
value safe, fail closed `secret-not-found` failure. The fake client is in [ProgramSupport.cs](ProgramSupport.cs).
