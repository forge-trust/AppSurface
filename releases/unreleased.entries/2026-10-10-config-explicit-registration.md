<!-- appsurface:unreleased-entry section="migration-watch" -->
### Select one typed Config wrapper from a Domain package

A `.NET 10` AppSurface host can select one compatible `IConfig` wrapper from a
Domain assembly outside normal module discovery with
[`AddAppSurfaceConfig<TConfig>()`](../../Config/ForgeTrust.AppSurface.Config/README.md#explicitly-register-one-typed-wrapper).
The call keeps lazy singleton initialization and adds the selected wrapper's
attributed declaration for audit; it does not scan assemblies or compose providers
for the host. See the [three-step Domain example](../../Config/ForgeTrust.AppSurface.Config/README.md#three-step-domain-example),
[logical-key and registration reference](../../guides/config-logical-keys.md#explicit-type-registration),
and the [packed Domain consumer proof](https://github.com/forge-trust/AppSurface/blob/main/tests/config-package-consumer/README.md).

Before adopting this pre-1.0 API, upgrade the coordinated AppSurface package set and
rebuild external providers and wrappers together. Keep the existing manual
registration path until the candidate consumer and staged-host proofs are accepted.
For rollback, restore that path together with the previously supported coordinated
package set. Registration does not migrate persisted keys or secret identities;
follow the [compatibility and rollback guide](../../guides/config-key-migration.md#explicit-registration-and-rollback).
