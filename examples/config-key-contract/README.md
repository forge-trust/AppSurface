# Configuration logical-key proof

Run the no-credential proof from a restored source checkout:

```bash
dotnet run --project examples/config-key-contract
```

It uses isolated environment variables, temporary JSON and LocalSecrets files, and
the real Google provider with a fake client. It checks casing through direct reads
and wrappers, literal dotted segments, hyphen distinctions, same-layer collisions,
environment and provider precedence, audit provenance, segment-prefix boundaries,
explicit mapping for unsupported conventions, and deterministic legacy translation
with declaration collision detection. Any failed assertion exits nonzero without
printing values or raw exception text.
It also runs the shared [Config.Testing cases](../../Config/ForgeTrust.AppSurface.Config.Testing/README.md)
for a Google-specific dotted counterexample and a Google miss that falls through
to the real file provider. The counterexample has a populated lower file source,
which must not hide the terminal outcome.

Each stage prints a name, pass status, and elapsed milliseconds. The final contract
summary is:

```text
AppSurface Config logical-key contract
PASS 10/10
Logical identity: Payments:ApiKey
Providers: FileBasedConfigProvider, EnvironmentConfigProvider, AppSurfaceLocalSecretProvider, GoogleSecretManagerConfigProvider
Values: [not displayed]
```

The restored-machine target is under two minutes; shared CI uses a generous
10-minute timeout and records observed timing. Temporary files are deleted on exit.
No OS credential store or real Google secret is accessed.

Read the [logical-key reference](../../guides/config-logical-keys.md),
[migration guide](../../guides/config-key-migration.md), and
[provider-author guide](../../guides/config-provider-authors.md). The separate
[clean packed-consumer verifier](https://github.com/forge-trust/AppSurface/blob/codex/config-logical-key-contract/tests/config-package-consumer/README.md)
proves NuGet adoption without project references.
